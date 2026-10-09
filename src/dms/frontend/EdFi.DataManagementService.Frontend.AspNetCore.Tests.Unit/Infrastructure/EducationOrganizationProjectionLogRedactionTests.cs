// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;
using EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.Modules;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;
using static EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.EducationOrganizationProjectionTestHost;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.Infrastructure;

/// <summary>
/// Captures what a production sink would receive from a booted host: a second Serilog provider whose
/// logger runs the same <c>LoggingConfigurator.ApplyLogContextAndIdentityRedaction</c> stage
/// <c>LoggingConfigurator.ConfigureLogging</c> applies, so the framework's own hosting-diagnostics
/// events pass through the production redaction exactly as the host emits them.
/// </summary>
internal sealed class ProjectionLogCapture : ILogEventSink
{
    public const string HostingDiagnosticsSourceContext = "Microsoft.AspNetCore.Hosting.Diagnostics";

    private readonly ConcurrentQueue<LogEvent> _events = new();

    public IReadOnlyList<LogEvent> Events => [.. _events];

    public void Emit(LogEvent logEvent) => _events.Enqueue(logEvent);

    public void Clear() => _events.Clear();

    public Action<IServiceCollection> Register =>
        services =>
            services.AddSingleton<ILoggerProvider>(
                new SerilogLoggerProvider(
                    LoggingConfigurator
                        .ApplyLogContextAndIdentityRedaction(new LoggerConfiguration().MinimumLevel.Verbose())
                        .WriteTo.Sink(this)
                        .CreateLogger(),
                    dispose: true
                )
            );

    public static string? Scalar(LogEvent logEvent, string propertyName) =>
        logEvent.Properties.TryGetValue(propertyName, out LogEventPropertyValue? value)
        && value is ScalarValue scalar
            ? scalar.Value?.ToString()
            : null;

    /// <summary>The framework's request-starting and request-finished events.</summary>
    public IReadOnlyList<LogEvent> HostingRequestEvents() =>
        [
            .. Events.Where(logEvent =>
                Scalar(logEvent, "SourceContext") == HostingDiagnosticsSourceContext
                && logEvent.Properties.ContainsKey("QueryString")
            ),
        ];

    /// <summary>
    /// Every captured event carrying <paramref name="secret"/> in its rendered message or in any
    /// property value, nested values included. Empty when nothing leaks.
    /// </summary>
    public IReadOnlyList<string> Leaks(string secret) =>
        [
            .. Events.SelectMany(logEvent =>
                logEvent
                    .Properties.Where(property => Contains(property.Value, secret))
                    .Select(property => $"[{Scalar(logEvent, "SourceContext")}] property {property.Key}")
                    .Concat(
                        logEvent.RenderMessage().Contains(secret, StringComparison.Ordinal)
                            ? [$"[{Scalar(logEvent, "SourceContext")}] rendered: {logEvent.RenderMessage()}"]
                            : []
                    )
            ),
        ];

    private static bool Contains(LogEventPropertyValue value, string secret) =>
        value switch
        {
            ScalarValue scalar => scalar.Value?.ToString()?.Contains(secret, StringComparison.Ordinal)
                ?? false,
            StructureValue structure => structure.Properties.Any(p => Contains(p.Value, secret)),
            SequenceValue sequence => sequence.Elements.Any(element => Contains(element, secret)),
            DictionaryValue dictionary => dictionary.Elements.Any(pair =>
                Contains(pair.Key, secret) || Contains(pair.Value, secret)
            ),
            _ => value.ToString().Contains(secret, StringComparison.Ordinal),
        };
}

/// <summary>
/// Real projection requests through the production pipeline, carrying a cursor DMS issued (answered
/// 200) and cursors the pipeline refuses (invalid, unauthenticated, repeated), beside an unrelated
/// route carrying the same kind of value. No captured event carries a projection cursor; the
/// framework's request events are still written, with the query string replaced; the unrelated route
/// keeps its query string.
/// </summary>
[TestFixture]
[NonParallelizable]
public class Given_Projection_Requests_With_Cursors_Through_The_Production_Pipeline
{
    private const string Route = "/management/education-organizations";
    private const string RejectedCursor = "PROJECTION-CURSOR-REJECTED-7f3a";
    private const string UnauthenticatedCursor = "PROJECTION-CURSOR-UNAUTHENTICATED-91c2";
    private const string RepeatedCursorA = "PROJECTION-CURSOR-REPEATED-A-44b0";
    private const string RepeatedCursorB = "PROJECTION-CURSOR-REPEATED-B-c87e";
    private const string ControlCursor = "UNRELATED-ROUTE-CURSOR-5d1e";

    private readonly ProjectionLogCapture _capture = new();
    private WebApplicationFactory<Program> _factory = null!;
    private string _issuedCursor = "";
    private readonly List<HttpStatusCode> _projectionStatuses = [];

    [OneTimeSetUp]
    public async Task Setup()
    {
        _factory = Create(
            PostgresqlStore(3788),
            _ => new FixedSetReader([
                new(1, "Ed-Fi:StateEducationAgency", "Sea", null, null, null, null, null),
                new(10, "Ed-Fi:EducationServiceCenter", "Esc", null, null, null, null, 1),
                new(100, "Ed-Fi:LocalEducationAgency", "Lea", null, null, null, 10, 1),
            ]),
            configureServices: _capture.Register
        );
        using HttpClient client = _factory.CreateClient();

        using (HttpResponseMessage first = await Send(client, "?dataStoreId=3788&limit=1"))
        {
            _issuedCursor = JsonNode.Parse(await first.Content.ReadAsStringAsync())![
                "nextCursor"
            ]!.GetValue<string>();
        }

        await Send(client, $"?dataStoreId=3788&limit=1&cursor={_issuedCursor}");
        await Send(client, $"?dataStoreId=3788&cursor={RejectedCursor}");
        await Send(client, $"?dataStoreId=3788&cursor={UnauthenticatedCursor}", authenticated: false);
        await Send(client, $"?dataStoreId=3788&cursor={RepeatedCursorA}&Cursor={RepeatedCursorB}");

        using HttpResponseMessage control = await client.GetAsync($"/metadata?cursor={ControlCursor}");
    }

    private async Task<HttpResponseMessage> Send(HttpClient client, string query, bool authenticated = true)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{Route}{query}");

        if (authenticated)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "projection-token");
        }

        HttpResponseMessage response = await client.SendAsync(request);
        _projectionStatuses.Add(response.StatusCode);
        return response;
    }

    [OneTimeTearDown]
    public async Task TearDown() => await _factory.DisposeAsync();

    private IEnumerable<LogEvent> ProjectionHostingEvents =>
        _capture
            .HostingRequestEvents()
            .Where(logEvent => ProjectionLogCapture.Scalar(logEvent, "Path") == Route);

    [Test]
    public void It_answers_the_issued_cursor_and_refuses_the_others() =>
        _projectionStatuses
            .Should()
            .Equal(
                HttpStatusCode.OK,
                HttpStatusCode.OK,
                HttpStatusCode.BadRequest,
                HttpStatusCode.Unauthorized,
                HttpStatusCode.BadRequest
            );

    [TestCase(RejectedCursor)]
    [TestCase(UnauthenticatedCursor)]
    [TestCase(RepeatedCursorA)]
    [TestCase(RepeatedCursorB)]
    public void It_logs_no_refused_cursor_anywhere(string cursor) =>
        _capture.Leaks(cursor).Should().BeEmpty();

    [Test]
    public void It_logs_no_issued_cursor_anywhere()
    {
        _issuedCursor.Should().NotBeNullOrEmpty();
        _capture.Leaks(_issuedCursor).Should().BeEmpty();
    }

    [Test]
    public void It_keeps_both_framework_request_events_of_every_projection_request_with_the_query_replaced()
    {
        LogEvent[] events = [.. ProjectionHostingEvents];

        events.Should().HaveCount(2 * _projectionStatuses.Count);
        events
            .Select(logEvent => ProjectionLogCapture.Scalar(logEvent, "QueryString"))
            .Should()
            .AllBe(EducationOrganizationProjectionQueryStringRedactingEnricher.RedactedQueryString);
        events
            .Should()
            .OnlyContain(logEvent =>
                logEvent
                    .RenderMessage()
                    .Contains(
                        EducationOrganizationProjectionQueryStringRedactingEnricher.RedactedQueryString,
                        StringComparison.Ordinal
                    )
            );
    }

    [Test]
    public void It_keeps_the_dms_request_completion_events_with_their_path()
    {
        _capture
            .Events.Where(logEvent =>
                logEvent.MessageTemplate.Text.Contains("DMS request", StringComparison.Ordinal)
                && ProjectionLogCapture.Scalar(logEvent, "Path") == Route
            )
            .Should()
            .HaveCount(_projectionStatuses.Count);
    }

    [Test]
    public void It_leaves_an_unrelated_route_query_string_in_its_framework_events()
    {
        LogEvent[] events =
        [
            .. _capture
                .HostingRequestEvents()
                .Where(logEvent => ProjectionLogCapture.Scalar(logEvent, "Path") == "/metadata"),
        ];

        events.Should().HaveCount(2);
        events
            .Select(logEvent => ProjectionLogCapture.Scalar(logEvent, "QueryString"))
            .Should()
            .AllBe($"?cursor={ControlCursor}");
        events
            .Should()
            .OnlyContain(logEvent =>
                logEvent.RenderMessage().Contains(ControlCursor, StringComparison.Ordinal)
            );
    }
}

/// <summary>
/// One projection request path form: its route mode, path base, the path as the client spells it,
/// whether the endpoint is enabled, and the status the faked facade answers.
/// </summary>
public sealed record ProjectionLogPathCase(
    string Name,
    ProjectionRouteMode Mode,
    string? PathBase,
    string RequestPath,
    bool Enabled,
    int FacadeStatus,
    HttpStatusCode ExpectedStatus
)
{
    public static ProjectionLogPathCase[] All { get; } =
    [
        new(
            "single-tenant",
            ProjectionRouteMode.SingleTenant,
            null,
            "/management/education-organizations",
            true,
            200,
            HttpStatusCode.OK
        ),
        new(
            "single-tenant, trailing slash",
            ProjectionRouteMode.SingleTenant,
            null,
            "/management/education-organizations/",
            true,
            200,
            HttpStatusCode.OK
        ),
        new(
            "single-tenant, mixed case",
            ProjectionRouteMode.SingleTenant,
            null,
            "/Management/Education-Organizations",
            true,
            200,
            HttpStatusCode.OK
        ),
        new(
            "path base",
            ProjectionRouteMode.SingleTenant,
            "dms-api",
            "/dms-api/management/education-organizations",
            true,
            200,
            HttpStatusCode.OK
        ),
        new(
            "tenant prefix, refused by Core",
            ProjectionRouteMode.MultiTenant,
            null,
            "/Tenant_255901/management/education-organizations",
            true,
            400,
            HttpStatusCode.BadRequest
        ),
        new(
            "tenant and qualifier prefix",
            ProjectionRouteMode.MultiTenantWithQualifiers,
            null,
            "/Tenant_255901/255901/2025/management/education-organizations",
            true,
            200,
            HttpStatusCode.OK
        ),
        new(
            "tenant and qualifier prefix, mixed case and trailing slash, refused by Core",
            ProjectionRouteMode.MultiTenantWithQualifiers,
            "dms-api",
            "/dms-api/Tenant_255901/255901/2025/MANAGEMENT/education-organizations/",
            true,
            409,
            HttpStatusCode.Conflict
        ),
        new(
            "endpoint disabled, never reached",
            ProjectionRouteMode.MultiTenantWithQualifiers,
            null,
            "/Tenant_255901/255901/2025/management/education-organizations",
            false,
            200,
            HttpStatusCode.NotFound
        ),
        new(
            "prefix-less form in multi-tenant mode, never reached",
            ProjectionRouteMode.MultiTenant,
            null,
            "/management/education-organizations",
            true,
            200,
            HttpStatusCode.NotFound
        ),
    ];

    public override string ToString() => Name;
}

/// <summary>
/// Each path form a projection request can take, answered or refused, reached or not: the framework's
/// request events are written with the query string replaced, and the cursor appears nowhere.
/// </summary>
[TestFixtureSource(typeof(ProjectionLogPathCase), nameof(ProjectionLogPathCase.All))]
[NonParallelizable]
public class Given_A_Projection_Request_Path_Form(ProjectionLogPathCase pathCase)
{
    private const string Cursor = "PROJECTION-CURSOR-PATH-FORM-e19b";

    private readonly ProjectionLogCapture _capture = new();
    private ProjectionFrontendHost _host = null!;
    private HttpResponseMessage _response = null!;

    [OneTimeSetUp]
    public async Task Setup()
    {
        _host = new ProjectionFrontendHost(
            pathCase.Enabled,
            pathCase.Mode,
            pathCase.PathBase,
            configureServices: _capture.Register
        )
        {
            Response = ProjectionFrontendHost.FakeResponse(
                pathCase.FacadeStatus,
                pathCase.FacadeStatus == 200 ? "application/json" : "application/problem+json"
            ),
        };

        _response = await _host.Get($"{pathCase.RequestPath}?dataStoreId=3788&cursor={Cursor}&limit=2");
    }

    [OneTimeTearDown]
    public async Task TearDown()
    {
        _response.Dispose();
        await _host.DisposeAsync();
    }

    /// <summary>
    /// The hosting layer logs both events outside <c>UsePathBase</c>, so their <c>Path</c> is the whole
    /// request path, any path base included.
    /// </summary>
    private string ExpectedPath => pathCase.RequestPath;

    [Test]
    public void It_answers_as_the_route_form_dictates()
    {
        _response.StatusCode.Should().Be(pathCase.ExpectedStatus);
        _host.Calls.Should().HaveCount(pathCase.ExpectedStatus == HttpStatusCode.NotFound ? 0 : 1);
    }

    [Test]
    public void It_logs_the_cursor_nowhere() => _capture.Leaks(Cursor).Should().BeEmpty();

    [Test]
    public void It_keeps_both_framework_request_events_with_the_query_replaced()
    {
        LogEvent[] events = [.. _capture.HostingRequestEvents()];

        events.Should().HaveCount(2);
        events.Select(logEvent => ProjectionLogCapture.Scalar(logEvent, "Path")).Should().AllBe(ExpectedPath);
        events
            .Select(logEvent => ProjectionLogCapture.Scalar(logEvent, "QueryString"))
            .Should()
            .AllBe(EducationOrganizationProjectionQueryStringRedactingEnricher.RedactedQueryString);
    }
}

/// <summary>
/// <c>LoggingConfigurator.ConfigureLogging</c> itself, over the shipped <c>appsettings.json</c>
/// (Information level) with its File sink pointed at a temporary JSON file that also renders messages.
/// The booted host cannot show this: it configures logging before the test host's configuration is
/// merged. Events are shaped like the framework's request-starting and request-finished events.
/// </summary>
[TestFixture]
[NonParallelizable]
public class Given_The_Production_Logging_Configuration_And_Hosting_Request_Events
{
    private const string Cursor = "PROJECTION-CURSOR-CONFIGURED-0a6d";
    private const string ControlCursor = "UNRELATED-ROUTE-CURSOR-CONFIGURED-b2f4";

    private string _logFilePath = "";
    private JsonElement[] _lines = [];

    [OneTimeSetUp]
    public void Setup()
    {
        _logFilePath = Path.Combine(
            Path.GetTempPath(),
            $"projection-hosting-diagnostics-{Guid.NewGuid():N}.log"
        );

        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddJsonFile(FindAppSettings(), optional: false)
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Serilog:WriteTo:0:Name"] = "File",
                    ["Serilog:WriteTo:0:Args:path"] = _logFilePath,
                    ["Serilog:WriteTo:0:Args:rollingInterval"] = "Infinite",
                    ["Serilog:WriteTo:0:Args:formatter:type"] =
                        "Serilog.Formatting.Json.JsonFormatter, Serilog",
                    ["Serilog:WriteTo:0:Args:formatter:renderMessage"] = "true",
                    ["Serilog:WriteTo:1:Name"] = "File",
                    ["Serilog:WriteTo:1:Args:path"] = _logFilePath + ".discard",
                }
            )
            .Build();

        var logger = (Logger)LoggingConfigurator.ConfigureLogging(configuration);

        foreach (
            string path in new[]
            {
                "/management/education-organizations",
                "/Tenant_255901/255901/2025/Management/Education-Organizations/",
                "/metadata",
            }
        )
        {
            string cursor = path == "/metadata" ? ControlCursor : Cursor;
            Serilog.ILogger scoped = logger
                .ForContext("SourceContext", ProjectionLogCapture.HostingDiagnosticsSourceContext)
                .ForContext("RequestPath", path);

            scoped.Information(
                "Request starting {Protocol} {Method} {Scheme}://{Host}{PathBase}{Path}{QueryString} - {ContentType} {ContentLength}",
                "HTTP/1.1",
                "GET",
                "http",
                "localhost",
                "/dms-api",
                path,
                $"?dataStoreId=1&cursor={cursor}",
                null,
                null
            );
            scoped.Information(
                "Request finished {Protocol} {Method} {Scheme}://{Host}{PathBase}{Path}{QueryString} - {StatusCode} {ContentLength} {ContentType} {ElapsedMilliseconds}ms",
                "HTTP/1.1",
                "GET",
                "http",
                "localhost",
                "/dms-api",
                path,
                $"?dataStoreId=1&cursor={cursor}",
                400,
                null,
                "application/problem+json",
                1.5
            );
        }

        logger.Dispose();

        _lines =
        [
            .. File.ReadAllLines(_logFilePath)
                .Where(line => line.Length > 0)
                .Select(line => JsonDocument.Parse(line).RootElement.Clone()),
        ];
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        foreach (string file in new[] { _logFilePath, _logFilePath + ".discard" })
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }

    private static string FindAppSettings()
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);

        while (directory is not null)
        {
            string candidate = Path.Combine(
                directory.FullName,
                "src",
                "dms",
                "frontend",
                "EdFi.DataManagementService.Frontend.AspNetCore",
                "appsettings.json"
            );

            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("appsettings.json was not found above the test directory.");
    }

    private IEnumerable<JsonElement> LinesFor(bool projection) =>
        _lines.Where(line =>
            (line.GetProperty("Properties").GetProperty("Path").GetString() == "/metadata") != projection
        );

    [Test]
    public void It_writes_every_event_at_the_shipped_level() => _lines.Should().HaveCount(6);

    [Test]
    public void It_writes_the_projection_events_without_the_cursor()
    {
        JsonElement[] lines = [.. LinesFor(projection: true)];

        lines.Should().HaveCount(4);
        lines.Should().OnlyContain(line => !line.GetRawText().Contains(Cursor, StringComparison.Ordinal));
        lines
            .Select(line => line.GetProperty("Properties").GetProperty("QueryString").GetString())
            .Should()
            .AllBe(EducationOrganizationProjectionQueryStringRedactingEnricher.RedactedQueryString);
        lines
            .Should()
            .OnlyContain(line =>
                line.GetProperty("RenderedMessage")
                    .GetString()!
                    .Contains("?[redacted]", StringComparison.Ordinal)
            );
    }

    [Test]
    public void It_writes_the_unrelated_route_events_unchanged()
    {
        JsonElement[] lines = [.. LinesFor(projection: false)];

        lines.Should().HaveCount(2);
        lines
            .Should()
            .OnlyContain(line =>
                line.GetProperty("RenderedMessage")
                    .GetString()!
                    .Contains(ControlCursor, StringComparison.Ordinal)
                && line.GetProperty("Properties").GetProperty("QueryString").GetString()
                    == $"?dataStoreId=1&cursor={ControlCursor}"
            );
    }
}
