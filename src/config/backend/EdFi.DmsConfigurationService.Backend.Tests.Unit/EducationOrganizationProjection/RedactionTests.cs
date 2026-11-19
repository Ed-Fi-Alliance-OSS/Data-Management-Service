// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection;
using EdFi.DmsConfigurationService.DataModel;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static EdFi.DmsConfigurationService.Backend.Tests.Unit.EducationOrganizationProjection.ProjectionPages;
using Code = EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection.EducationOrganizationProjectionFailureCode;
using Stage = EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection.EducationOrganizationProjectionStage;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.EducationOrganizationProjection;

/// <summary>
/// DMS-1440 spec §5.6 at every stage: reads through the production registration, with the default
/// <c>IHttpClientFactory</c> registration the Configuration Service host also makes, every logger category captured at
/// <see cref="LogLevel.Trace"/> together with every scope, and a fake DMS that puts hostile values in problem bodies,
/// response headers, Discovery documents, token and page bodies and exception messages.
/// </summary>
public partial class RedactionTests
{
    private const string Tenant = "Tenant\r\n<b>FORGED";
    private const string SanitizedTenant = "TenantbFORGED";
    private const string ClientId = "client";
    private const string ClientSecret = "SECRET-CLIENT-1440";
    private const string AccessToken = "SECRET-TOKEN-1440";
    private const string Cursor = "SECRET-CURSOR-1440";
    private const string BodyText = "HOSTILE-BODY-1440";
    private const string HeaderText = "HOSTILE-HEADER-1440";
    private const string ExceptionText = "HOSTILE-EXCEPTION-1440";
    private const string DiscoveryText = "HOSTILE-DISCOVERY-1440";
    private const string DiscoveryPlaceholder = "hostileDiscoveryPlaceholder1440";
    private const string NameText = "HOSTILE-NAME-1440";

    /// <summary>Line breaks and markup, appended to hostile values that a log forger would use.</summary>
    private const string Forgery = "\r\n<b>FORGED";

    private const string HostileProblemType = "urn:ed-fi:api:hostile" + Forgery;
    private const string SanitizedProblemType = "urn:ed-fi:api:hostilebFORGED";
    private const string HostileCorrelationId = "corr-1" + Forgery;
    private const string SanitizedCorrelationId = "corr-1bFORGED";

    private static readonly string _basicCredential = Convert.ToBase64String(
        Encoding.UTF8.GetBytes($"{ClientId}:{ClientSecret}")
    );

    /// <summary>Text no log record or scope may contain, at any level.</summary>
    private static readonly string[] _neverLogged =
    [
        ClientSecret,
        _basicCredential,
        AccessToken,
        Cursor,
        BodyText,
        HeaderText,
        ExceptionText,
        DiscoveryText,
        DiscoveryPlaceholder,
        NameText,
        "Bearer",
        "Basic",
        "Authorization",
    ];

    /// <summary>One read: what the fake DMS answers, and the stage and code the read must fail with.</summary>
    /// <param name="Discovery">Answers Discovery requests; the harness document when <c>null</c>.</param>
    /// <param name="Token">Answers token requests; a valid token when <c>null</c>.</param>
    /// <param name="Page">Answers page requests by their number from 1; a one-item last page when <c>null</c>.</param>
    /// <param name="ProblemType">The sanitized problem type the failure carries, when it carries one.</param>
    /// <param name="CorrelationId">The sanitized correlation id the failure carries, when it carries one.</param>
    public sealed record FailureScenario(
        string Name,
        Stage Stage,
        Code Code,
        Func<HttpRequestMessage, HttpResponseMessage>? Discovery = null,
        Func<HttpRequestMessage, HttpResponseMessage>? Token = null,
        Func<int, HttpResponseMessage>? Page = null,
        int PagesRead = 0,
        string? ProblemType = null,
        string? CorrelationId = null,
        IReadOnlyDictionary<string, string>? Settings = null
    )
    {
        public override string ToString() => Name;
    }

    public static IEnumerable<FailureScenario> FailureScenarios() =>
        [
            new(
                "Discovery transport failure",
                Stage.Discovery,
                Code.NetworkError,
                Discovery: _ => throw new HttpRequestException(ExceptionText + Forgery)
            ),
            new(
                "Discovery redirect",
                Stage.Discovery,
                Code.DiscoveryInvalid,
                Discovery: _ =>
                    WithHostileHeaders(DmsResponses.Text(HttpStatusCode.Found, BodyText + Forgery))
            ),
            new(
                "Discovery problem",
                Stage.Discovery,
                Code.ServiceUnavailable,
                Discovery: _ => HostileProblem(HttpStatusCode.ServiceUnavailable),
                ProblemType: SanitizedProblemType,
                CorrelationId: SanitizedCorrelationId
            ),
            new(
                "Discovery URL outside the base URL",
                Stage.Discovery,
                Code.DiscoveryInvalid,
                Discovery: _ =>
                    DiscoveryDocument(
                        $"https://evil.example.org/{DiscoveryText}/oauth/token?secret={ClientSecret}",
                        "https://dms.example.org/api/{districtId}/management/education-organizations"
                    )
            ),
            new(
                "Discovery placeholder no context fills",
                Stage.Discovery,
                Code.TargetNotRoutable,
                Discovery: _ =>
                    DiscoveryDocument(
                        "https://dms.example.org/api/{districtId}/oauth/token",
                        "https://dms.example.org/api/{"
                            + DiscoveryPlaceholder
                            + "}/management/education-organizations"
                    )
            ),
            new(
                "Discovery document of the wrong shape",
                Stage.Discovery,
                Code.DiscoveryInvalid,
                Discovery: _ =>
                    DmsResponses.Text(
                        HttpStatusCode.OK,
                        new JsonObject { ["urls"] = DiscoveryText + Forgery }.ToJsonString()
                    )
            ),
            new(
                "Token refused with an echoing problem and challenge",
                Stage.Token,
                Code.TokenRejected,
                Token: _ => HostileProblem(HttpStatusCode.Unauthorized),
                ProblemType: SanitizedProblemType,
                CorrelationId: SanitizedCorrelationId
            ),
            new(
                "Token response with a malformed access token",
                Stage.Token,
                Code.MalformedResponse,
                Token: _ =>
                    DmsResponses.Text(
                        HttpStatusCode.OK,
                        new JsonObject
                        {
                            ["access_token"] = AccessToken + Forgery,
                            ["token_type"] = "bearer",
                            ["expires_in"] = 3600,
                        }.ToJsonString()
                    )
            ),
            new(
                "Token transport failure echoing the credentials",
                Stage.Token,
                Code.NetworkError,
                Token: request =>
                    throw new HttpRequestException(
                        $"{ExceptionText} {request.Headers.Authorization} {ClientSecret}{Forgery}"
                    )
            ),
            new(
                "Token non-problem 500",
                Stage.Token,
                Code.ServiceUnavailable,
                Token: _ =>
                    WithHostileHeaders(
                        DmsResponses.Text(
                            HttpStatusCode.InternalServerError,
                            new JsonObject
                            {
                                ["message"] = $"{BodyText} {ClientSecret}{Forgery}",
                                ["traceId"] = BodyText,
                            }.ToJsonString()
                        )
                    )
            ),
            new(
                "Page refused twice with an echoing problem and challenge",
                Stage.Page,
                Code.Unauthorized,
                Page: _ => HostileProblem(HttpStatusCode.Unauthorized),
                ProblemType: SanitizedProblemType,
                CorrelationId: SanitizedCorrelationId
            ),
            new(
                "Page transport failure after a cursor",
                Stage.Page,
                Code.NetworkError,
                Page: call =>
                    call == 1
                        ? FirstPage()
                        : throw new HttpRequestException($"{ExceptionText} {Cursor} {AccessToken}{Forgery}"),
                PagesRead: 1
            ),
            new(
                "Page problem after a cursor",
                Stage.Page,
                Code.UnexpectedResponse,
                Page: call => call == 1 ? FirstPage() : HostileProblem(HttpStatusCode.Conflict),
                PagesRead: 1,
                ProblemType: SanitizedProblemType,
                CorrelationId: SanitizedCorrelationId
            ),
            new(
                "Page body read failure after a cursor",
                Stage.Page,
                Code.NetworkError,
                Page: call =>
                    call == 1
                        ? FirstPage()
                        : DmsResponses.Stream(
                            HttpStatusCode.OK,
                            new StalledStream(new IOException($"{ExceptionText} {Cursor}{Forgery}"))
                        ),
                PagesRead: 1
            ),
            new(
                "Page with an unknown member",
                Stage.Page,
                Code.MalformedResponse,
                Page: _ =>
                {
                    JsonObject page = JsonNode.Parse(PageJson(null, [Item(1)]))!.AsObject();
                    page[BodyText] = Cursor + Forgery;
                    return DmsResponses.Text(HttpStatusCode.OK, page.ToJsonString());
                }
            ),
            new(
                "Page with an invalid item",
                Stage.Page,
                Code.DataInvalid,
                Page: _ =>
                    DmsResponses.Text(
                        HttpStatusCode.OK,
                        PageJson(null, [Item(1, discriminator: BodyText + Forgery, name: NameText + Forgery)])
                    ),
                PagesRead: 1
            ),
            new(
                "Page body over the cap",
                Stage.Page,
                Code.LimitExceeded,
                Page: _ => DmsResponses.Text(HttpStatusCode.OK, new string(' ', 4096) + BodyText),
                Settings: new Dictionary<string, string> { ["MaxResponseBodyBytes"] = "3072" }
            ),
        ];

    /// <summary>A first page holding one item with a hostile name, continued by the secret cursor.</summary>
    private static HttpResponseMessage FirstPage() =>
        DmsResponses.Text(HttpStatusCode.OK, PageJson(Cursor, [Item(1, name: NameText + Forgery)]));

    private static HttpResponseMessage DiscoveryDocument(string oauth, string projection) =>
        DmsResponses.Text(
            HttpStatusCode.OK,
            new JsonObject
            {
                ["urls"] = new JsonObject
                {
                    ["oauth"] = oauth,
                    ["educationOrganizationProjection"] = projection,
                },
                ["educationOrganizationProjection"] = new JsonObject
                {
                    ["contractVersions"] = new JsonArray(ContractVersionV1),
                },
                ["hostile"] = DiscoveryText + Forgery,
            }.ToJsonString()
        );

    /// <summary>A problem body whose every member is hostile, with hostile headers.</summary>
    private static HttpResponseMessage HostileProblem(HttpStatusCode status) =>
        WithHostileHeaders(
            DmsResponses.Text(
                status,
                new JsonObject
                {
                    ["type"] = HostileProblemType,
                    ["title"] = BodyText + Forgery,
                    ["status"] = (int)status,
                    ["detail"] = $"{BodyText} {ClientSecret} {AccessToken} {Cursor}{Forgery}",
                    ["correlationId"] = HostileCorrelationId,
                    ["errors"] = new JsonArray(BodyText + Forgery),
                }.ToJsonString(),
                "application/problem+json"
            )
        );

    /// <summary>Adds a challenge, a cookie and a location that echo secrets.</summary>
    private static HttpResponseMessage WithHostileHeaders(HttpResponseMessage response)
    {
        response.Headers.TryAddWithoutValidation(
            "WWW-Authenticate",
            $"Bearer error=\"invalid_token\", error_description=\"{HeaderText} {AccessToken}\""
        );
        response.Headers.TryAddWithoutValidation("Set-Cookie", $"session={HeaderText}");
        response.Headers.TryAddWithoutValidation(
            "Location",
            $"https://evil.example.org/{HeaderText}?cursor={Cursor}"
        );
        return response;
    }

    private static HttpResponseMessage ValidToken() =>
        DmsResponses.Text(
            HttpStatusCode.OK,
            $$"""{"access_token":"{{AccessToken}}","token_type":"bearer","expires_in":3600}"""
        );

    /// <summary>Whether a text contains a value no record, scope or failure text may contain.</summary>
    private static bool HasHostileValue(string text) =>
        Array.Exists(_neverLogged, value => text.Contains(value, StringComparison.Ordinal));

    /// <summary>Every text a record or scope carries, one entry per message, value, exception and scope.</summary>
    private static IEnumerable<string> LoggedTexts(RecordingLoggerProvider recorder) =>
        recorder
            .Records.SelectMany(record =>
                new[] { record.Category, record.Message, record.Exception?.ToString() ?? "" }.Concat(
                    record.State.Select(pair => $"{pair.Key}={pair.Value}")
                )
            )
            .Concat(recorder.Scopes.Select(scope => scope.Text));

    [GeneratedRegex(@"^[A-Za-z0-9 _\-.:/\\]*$")]
    private static partial Regex LogSafe();

    /// <summary>
    /// The production reader over a fake DMS: <c>AddHttpClient()</c> as the host calls it, then
    /// <c>AddDmsEducationOrganizationProjectionReader</c>, with only the named client's primary handler replaced.
    /// </summary>
    public sealed class ProductionReader : IDisposable
    {
        private readonly ServiceProvider _provider;

        public ProductionReader(
            Func<HttpRequestMessage, HttpResponseMessage>? discovery,
            Func<HttpRequestMessage, HttpResponseMessage>? token,
            Func<int, CancellationToken, Task<HttpResponseMessage>> page,
            IReadOnlyDictionary<string, string>? settings = null
        )
        {
            int pageCalls = 0;
            Handler = new FakeDmsHandler(
                (request, cancellationToken) =>
                {
                    if (request.Method == HttpMethod.Post)
                    {
                        return Task.FromResult(token?.Invoke(request) ?? ValidToken());
                    }
                    return IsPageRequest(request.RequestUri!)
                        ? page(Interlocked.Increment(ref pageCalls), cancellationToken)
                        : Task.FromResult(
                            discovery?.Invoke(request)
                                ?? DmsResponses.Text(
                                    HttpStatusCode.OK,
                                    ProjectionReaderHarness.DiscoveryDocument
                                )
                        );
                }
            );

            string section = DmsEducationOrganizationProjectionSettings.SectionName;
            Dictionary<string, string?> values = new()
            {
                [$"{section}:DmsBaseUrl"] = ProjectionReaderHarness.BaseUrl,
                [$"{section}:Credentials:ClientId"] = ClientId,
                [$"{section}:Credentials:ClientSecret"] = ClientSecret,
                [$"{section}:PageSize"] = "1",
            };
            foreach ((string key, string value) in settings ?? new Dictionary<string, string>())
            {
                values[$"{section}:{key}"] = value;
            }

            ServiceCollection services = new();
            services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(Recorder));
            services.AddHttpClient();
            services.AddDmsEducationOrganizationProjectionReader(
                new ConfigurationBuilder().AddInMemoryCollection(values).Build()
            );
            services
                .AddHttpClient(DmsEducationOrganizationProjectionHttpClient.Name)
                .ConfigurePrimaryHttpMessageHandler(() => Handler);
            _provider = services.BuildServiceProvider();
        }

        public RecordingLoggerProvider Recorder { get; } = new();

        public FakeDmsHandler Handler { get; }

        public IReadOnlyList<RecordedLog> ReaderRecords =>
            [
                .. Recorder.Records.Where(record =>
                    record.Category == typeof(EducationOrganizationProjectionReader).FullName
                ),
            ];

        public IReadOnlyList<RecordedLog> HttpRecords =>
            [
                .. Recorder.Records.Where(record =>
                    record.Category == typeof(ProjectionHttpClientLogger).FullName
                ),
            ];

        public Task<EducationOrganizationProjectionReadResult> ReadAsync(
            CancellationToken cancellationToken
        ) =>
            _provider
                .GetRequiredService<IEducationOrganizationProjectionReader>()
                .ReadAllAsync(
                    new(Tenant, DataStoreId, new Dictionary<string, string> { ["districtId"] = "255901" }),
                    cancellationToken
                );

        public void Dispose()
        {
            _provider.Dispose();
            Recorder.Dispose();
        }
    }

    /// <summary>The checks every read makes, however it ends.</summary>
    public abstract class HostileReadFixture
    {
        protected ProductionReader Run { get; private set; } = null!;

        protected CancellationTokenSource Caller { get; private set; } = null!;

        protected EducationOrganizationProjectionReadResult? Result { get; private set; }

        protected OperationCanceledException? Cancellation { get; private set; }

        protected abstract ProductionReader Arrange();

        [SetUp]
        public async Task Setup()
        {
            Caller = new CancellationTokenSource();
            Run = Arrange();
            try
            {
                Result = await Run.ReadAsync(Caller.Token);
            }
            catch (OperationCanceledException exception)
            {
                Cancellation = exception;
            }
        }

        [TearDown]
        public void TearDown()
        {
            Run.Dispose();
            Caller.Dispose();
        }

        [Test]
        public void It_logs_no_secret_token_cursor_body_header_or_exception_text_at_any_level() =>
            LoggedTexts(Run.Recorder).Where(HasHostileValue).Should().BeEmpty();

        [Test]
        public void It_logs_no_line_break_or_markup_at_any_level() =>
            LoggedTexts(Run.Recorder)
                .Where(text => text.IndexOfAny(['\r', '\n', '<', '>']) >= 0)
                .Should()
                .BeEmpty();

        [Test]
        public void It_attaches_no_exception_to_any_record() =>
            Run.Recorder.Records.Where(record => record.Exception is not null).Should().BeEmpty();

        [Test]
        public void It_opens_no_logger_scope() => Run.Recorder.Scopes.Should().BeEmpty();

        [Test]
        public void It_logs_only_through_the_reader_and_the_projection_http_logger() =>
            Run
                .Recorder.Records.Select(record => record.Category)
                .Distinct()
                .Should()
                .BeSubsetOf([
                    typeof(EducationOrganizationProjectionReader).FullName!,
                    typeof(ProjectionHttpClientLogger).FullName!,
                ]);

        [Test]
        public void It_logs_each_request_once_by_its_sanitized_path_without_the_query() =>
            Run
                .HttpRecords.Select(record => $"{record.State.Single(pair => pair.Key == "Path").Value}")
                .Should()
                .Equal(
                    Run.Handler.Requests.Select(request =>
                        LoggingUtility.SanitizeForLog(request.Uri.AbsolutePath)
                    )
                );
    }

    [TestFixtureSource(typeof(RedactionTests), nameof(FailureScenarios))]
    public class Given_a_failed_read_with_hostile_values(FailureScenario scenario) : HostileReadFixture
    {
        private EducationOrganizationProjectionFailure Failure =>
            ((EducationOrganizationProjectionReadResult.Failure)Result!).Detail;

        protected override ProductionReader Arrange() =>
            new(
                scenario.Discovery,
                scenario.Token,
                (call, _) => Task.FromResult(scenario.Page?.Invoke(call) ?? Page(null, Schools(1))),
                scenario.Settings
            );

        [Test]
        public void It_fails_at_the_scenario_stage_with_its_code() =>
            (Failure.Stage, Failure.Code, Failure.PagesRead)
                .Should()
                .Be((scenario.Stage, scenario.Code, scenario.PagesRead));

        [Test]
        public void It_carries_the_problem_fields_sanitized() =>
            (Failure.ProblemType, Failure.CorrelationId)
                .Should()
                .Be((scenario.ProblemType, scenario.CorrelationId));

        [Test]
        public void It_logs_one_warning_from_the_reader() =>
            Run.ReaderRecords.Select(record => record.Level).Should().Equal(LogLevel.Warning);

        [Test]
        public void It_logs_the_sanitized_values_the_failure_carries() =>
            Run
                .ReaderRecords.Single()
                .State.Where(pair =>
                    pair.Key is "Tenant" or "Stage" or "Code" or "ProblemType" or "CorrelationId"
                )
                .Select(pair => $"{pair.Key}={pair.Value}")
                .Should()
                .BeEquivalentTo(
                    $"Tenant={SanitizedTenant}",
                    $"Stage={scenario.Stage}",
                    $"Code={scenario.Code}",
                    $"ProblemType={Failure.ProblemType}",
                    $"CorrelationId={Failure.CorrelationId}"
                );

        [Test]
        public void It_logs_only_log_safe_values_from_the_reader() =>
            Run
                .ReaderRecords.Single()
                .State.Where(pair => pair.Key != "{OriginalFormat}")
                .Select(pair => $"{pair.Value}")
                .Should()
                .AllSatisfy(value => LogSafe().IsMatch(value).Should().BeTrue(value));

        [Test]
        public void It_keeps_every_hostile_value_out_of_the_failure_text() =>
            new[] { Result!.ToString() }
                .Where(text => HasHostileValue(text) || text.IndexOfAny(['\r', '\n', '<', '>']) >= 0)
                .Should()
                .BeEmpty();
    }

    [TestFixture]
    public class Given_a_successful_read_with_hostile_names : HostileReadFixture
    {
        protected override ProductionReader Arrange() =>
            new(
                null,
                null,
                (call, _) =>
                    Task.FromResult(
                        call == 1
                            ? FirstPage()
                            : DmsResponses.Text(
                                HttpStatusCode.OK,
                                PageJson(null, [Item(2, name: NameText + Forgery, shortName: NameText)])
                            )
                    )
            );

        [Test]
        public void It_returns_the_names_unchanged() =>
            ((EducationOrganizationProjectionReadResult.Success)Result!)
                .Items.Select(item => (item.NameOfInstitution, item.ShortNameOfInstitution))
                .Should()
                .Equal((NameText + Forgery, null), (NameText + Forgery, NameText));

        [Test]
        public void It_logs_one_information_record_from_the_reader_with_the_sanitized_tenant() =>
            Run
                .ReaderRecords.Select(record =>
                    (record.Level, $"{record.State.Single(pair => pair.Key == "Tenant").Value}")
                )
                .Should()
                .Equal((LogLevel.Information, SanitizedTenant));
    }

    [TestFixture]
    public class Given_a_read_cancelled_while_a_page_after_a_cursor_is_outstanding : HostileReadFixture
    {
        protected override ProductionReader Arrange() =>
            new(
                null,
                null,
                async (call, cancellationToken) =>
                {
                    if (call == 1)
                    {
                        return FirstPage();
                    }
                    await Caller.CancelAsync();
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                    throw new InvalidOperationException(ExceptionText);
                }
            );

        [Test]
        public void It_throws_with_the_callers_token() =>
            Cancellation!.CancellationToken.Should().Be(Caller.Token);

        [Test]
        public void It_logs_no_reader_record() => Run.ReaderRecords.Should().BeEmpty();

        [Test]
        public void It_logs_the_cancelled_page_request_by_its_exception_type_only() =>
            Run
                .HttpRecords.Select(record => record.Level)
                .Should()
                .Equal(LogLevel.Information, LogLevel.Information, LogLevel.Information, LogLevel.Warning);
    }
}
