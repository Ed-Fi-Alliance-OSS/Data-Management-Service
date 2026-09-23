// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.External.Security;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Security.Model;
using EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;
using EdFi.DataManagementService.Identity;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.Infrastructure;

/// <summary>
/// D11/C6/C7: no identity operation identifier ever reaches a log, at either DMS layer or in the
/// framework's own hosting-diagnostics events, while a resource route (the negative control) and the
/// other four identity routes are unaffected.
/// </summary>
/// <remarks>
/// Two independent proofs, because they need two different harnesses:
/// <para>
/// <b>Redaction and C7</b> (<see cref="Given_The_Toggle_Is_On_With_Single_Tenancy"/>,
/// <see cref="Given_Multi_Tenancy_Is_Enabled"/>) drive the real <c>IApiService</c> and Core identity
/// pipelines through a booted host, with only the plugin boundary (<c>IIdentityService</c>) and the
/// CMS providers faked - the same shape <c>IdentityLocationRoundTripTests</c> uses for B6 - and
/// capture through a second Serilog provider registered via <c>ConfigureServices</c>
/// (<c>PluginHostProbe.cs:152-183</c>'s pattern). This proves <c>LoggingMiddleware</c> and
/// <c>RequestResponseLoggingMiddleware</c> log the redacted template, never the raw identifier, and
/// that <c>IdentityProviderBoundary</c>'s sanitized failure-level log carries no provider detail.
/// </para>
/// <para>
/// <b>The framework hosting-diagnostics filter</b>
/// (<see cref="Given_The_Production_Logging_Pipeline"/>) cannot be proven through that same booted
/// host: <c>Program.cs</c> calls <c>LoggingConfigurator.ConfigureLogging</c> (where
/// <c>IdentityHostingDiagnosticsFilter</c> is wired in) while composing services, before
/// <c>WebApplicationFactory</c>'s test configuration overrides are merged onto the builder, so a
/// per-test Serilog sink or File-sink path configured through the test host's
/// <c>ConfigureAppConfiguration</c> is never visible to that call. Instead this calls the exact same
/// production <c>LoggingConfigurator.ConfigureLogging</c> directly, over a configuration this test
/// fully controls (no hosting timing involved), and drives synthetic events shaped exactly like Task
/// 4's probe (Contract round, Probe (a)): <c>Microsoft.AspNetCore.Hosting.Diagnostics</c>
/// request-starting/request-finished carrying both <c>Path</c> and <c>RequestPath</c>, and an
/// unrelated <c>HostingStartupAssemblyLoaded</c>-shaped event carrying neither, so a presence-check
/// bug in the filter would surface as a thrown exception here.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
public class IdentityLogRedactionTests
{
    private const string ClaimSetName = "IdentityLogRedaction-ClaimSet";
    private const string ClientId = "identity-log-redaction-client";
    private const string BearerToken = "identity-log-redaction-token";
    private const string UniqueId = "605943412";
    private const string ResultsToken = "SECRET-TOKEN-XYZ";
    private const string HostingDiagnosticsSourceContext = "Microsoft.AspNetCore.Hosting.Diagnostics";

    private static string? ScalarProperty(LogEvent logEvent, string propertyName)
    {
        if (
            !logEvent.Properties.TryGetValue(propertyName, out LogEventPropertyValue? value)
            || value is not ScalarValue scalar
        )
        {
            return null;
        }

        return scalar.Value?.ToString();
    }

    private static bool IsFrontendCompletionEvent(LogEvent e) =>
        e.MessageTemplate.Text.Contains("DMS request completed", StringComparison.Ordinal)
        || e.MessageTemplate.Text.Contains("DMS request failed", StringComparison.Ordinal);

    private static bool IsCoreCompletionEvent(LogEvent e) =>
        e.MessageTemplate.Text.Contains("DMS core request completed", StringComparison.Ordinal)
        || e.MessageTemplate.Text.Contains("DMS core request failed", StringComparison.Ordinal);

    private static WebApplicationFactory<Program> CreateFactory(
        IIdentityService identityService,
        CapturingSerilogSink sink,
        bool multiTenancy = false,
        string routeQualifierSegments = ""
    )
    {
        Logger captureLogger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(sink)
            .CreateLogger();

        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration(
                (_, configuration) =>
                {
                    configuration.AddInMemoryCollection(
                        new Dictionary<string, string?>
                        {
                            ["AppSettings:EnableIdentityManagement"] = "true",
                            ["AppSettings:MultiTenancy"] = multiTenancy ? "true" : "false",
                            ["AppSettings:RouteQualifierSegments"] = routeQualifierSegments,
                        }
                    );
                }
            );
            builder.ConfigureServices(services =>
            {
                TestMockHelper.AddEssentialMocks(services);

                // Added through ConfigureServices, after AddServices configures Serilog from
                // configuration and clears providers, so this provider survives (PluginHostProbe's
                // precedent). It runs alongside the production Serilog provider rather than
                // replacing it, so both DMS logging layers - which log directly through
                // Microsoft.Extensions.Logging, not through Serilog's configured sinks - are
                // captured here exactly as production emits them.
                services.AddSingleton<ILoggerProvider>(
                    new SerilogLoggerProvider(captureLogger, dispose: true)
                );

                var jwtValidationService = A.Fake<IJwtValidationService>();
                var principal = new ClaimsPrincipal(
                    new ClaimsIdentity([new Claim("client_id", ClientId)], "test")
                );
                var clientAuthorizations = new ClientAuthorizations(
                    TokenId: "identity-log-redaction",
                    ClientId: ClientId,
                    ClaimSetName: ClaimSetName,
                    EducationOrganizationIds: [],
                    NamespacePrefixes: [],
                    DataStoreIds: []
                );
                A.CallTo(() =>
                        jwtValidationService.ValidateAndExtractClientAuthorizationsAsync(
                            A<string>._,
                            A<CancellationToken>._
                        )
                    )
                    .Returns(
                        Task.FromResult(
                            ((ClaimsPrincipal?)principal, (ClientAuthorizations?)clientAuthorizations)
                        )
                    );
                A.CallTo(() =>
                        jwtValidationService.ValidateAndExtractClientAuthorizationsAsync(
                            A<string>._,
                            A<int>._,
                            A<CancellationToken>._
                        )
                    )
                    .Returns(
                        Task.FromResult(
                            ((ClaimsPrincipal?)principal, (ClientAuthorizations?)clientAuthorizations)
                        )
                    );

                var applicationContextProvider = A.Fake<IApplicationContextProvider>();
                var applicationContextResult = new ApplicationContextResult.Success(
                    new ApplicationContext(
                        Id: 1,
                        ApplicationId: 1,
                        ClientId: ClientId,
                        ClientUuid: Guid.Parse("33333333-3333-3333-3333-333333333333"),
                        DataStoreIds: [],
                        CreatorOwnershipTokenId: null,
                        OwnershipTokenIds: []
                    )
                );
                A.CallTo(() =>
                        applicationContextProvider.GetApplicationByClientIdAsync(
                            A<string>._,
                            A<string?>._,
                            A<CancellationToken>._
                        )
                    )
                    .Returns(applicationContextResult);

                if (multiTenancy)
                {
                    var dataStoreProvider = A.Fake<IDataStoreProvider>();
                    A.CallTo(() => dataStoreProvider.LoadTenants(A<CancellationToken>._))
                        .Returns(new List<string> { "tenant-a" });
                    services.AddTransient(_ => dataStoreProvider);
                }

                services.RemoveAll<IJwtValidationService>();
                services.RemoveAll<IApplicationContextProvider>();
                services.RemoveAll<IClaimSetProvider>();

                services.AddSingleton(jwtValidationService);
                services.AddSingleton(applicationContextProvider);
                services.AddSingleton<IClaimSetProvider>(new IdentityGrantingClaimSetProvider());

                // IIdentityService is registered scoped, exactly as production does: the real
                // IApiService and Core identity pipelines resolve it once per request from the
                // request's own scope.
                services.Replace(ServiceDescriptor.Scoped<IIdentityService>(_ => identityService));
            });
        });
    }

    private static HttpRequestMessage AuthorizedGet(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", BearerToken);
        return request;
    }

    [TestFixture]
    public class Given_The_Toggle_Is_On_With_Single_Tenancy
    {
        // Used only by the C7 assertion below, which must inspect every property value on every
        // other captured event (not just Path) to prove the provider's message appears nowhere
        // above Debug.
        private static string PropertyText(LogEventPropertyValue value) =>
            value is ScalarValue { Value: not null } scalar
                ? scalar.Value.ToString() ?? string.Empty
                : value.ToString();

        [Test]
        public async Task It_redacts_the_unique_id_on_a_get_by_id_success()
        {
            var sink = new CapturingSerilogSink();
            var identityService = new FakeIdentityService();
            await using var factory = CreateFactory(identityService, sink);
            using var client = factory.CreateClient();

            using var response = await client.SendAsync(AuthorizedGet($"/identity/v2/identities/{UniqueId}"));
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            IReadOnlyList<LogEvent> events = sink.Events;

            // Scoped to the Path property and the rendered message on the frontend and Core
            // completion events (C6's literal wording), not every property a Verbose capture
            // happens to see: unrelated framework internals (routing candidate matching, endpoint
            // selection) legitimately carry the raw path and are out of D11's scope, and this
            // capture bypasses the production Filter.ByExcluding entirely (see the class remarks),
            // so it cannot speak to Microsoft.AspNetCore.Hosting.Diagnostics either way - that is
            // proven separately by Given_The_Production_Logging_Pipeline.
            foreach (
                LogEvent completed in events.Where(e =>
                    IsFrontendCompletionEvent(e) || IsCoreCompletionEvent(e)
                )
            )
            {
                ScalarProperty(completed, "Path").Should().NotContain(UniqueId);
                completed.RenderMessage().Should().NotContain(UniqueId);
            }

            // No literal braces: LoggingSanitizer.SanitizeInternalValueForLogging's Method/Path
            // allowlist (letters, digits, space, and `_-.:/\`) strips `{` and `}` from every value
            // it sanitizes, redacted or not - unchanged by D11, whose only promise is that the
            // identifier segment itself never reaches the log.
            LogEvent frontendCompleted = events.Single(IsFrontendCompletionEvent);
            ScalarProperty(frontendCompleted, "Path").Should().Be("/identity/v2/identities/id");

            LogEvent coreCompleted = events.Single(IsCoreCompletionEvent);
            ScalarProperty(coreCompleted, "Path").Should().Be("/identity/v2/identities/id");
        }

        [Test]
        public async Task It_redacts_the_token_on_a_results_poll_failure_and_logs_the_provider_exception_contract()
        {
            var sink = new CapturingSerilogSink();
            var identityService = new FakeIdentityService();
            await using var factory = CreateFactory(identityService, sink);
            using var client = factory.CreateClient();

            using var response = await client.SendAsync(
                AuthorizedGet($"/identity/v2/identities/results/{ResultsToken}")
            );
            response.StatusCode.Should().Be(HttpStatusCode.BadGateway);

            IReadOnlyList<LogEvent> events = sink.Events;
            foreach (
                LogEvent completed in events.Where(e =>
                    IsFrontendCompletionEvent(e) || IsCoreCompletionEvent(e)
                )
            )
            {
                ScalarProperty(completed, "Path").Should().NotContain(ResultsToken);
                completed.RenderMessage().Should().NotContain(ResultsToken);
            }

            LogEvent frontendFailed = events.Single(IsFrontendCompletionEvent);
            ScalarProperty(frontendFailed, "Path").Should().Be("/identity/v2/identities/results/token");

            LogEvent coreCompletedOrFailed = events.Single(IsCoreCompletionEvent);
            ScalarProperty(coreCompletedOrFailed, "Path")
                .Should()
                .Be("/identity/v2/identities/results/token");

            // C7: the provider boundary's sanitized failure-level log carries the exception type,
            // the stage/operation, and the trace id, plus stack frames - never the provider's own
            // message; the full exception (with the message) is logged only at Debug.
            LogEvent errorEvent = events.Single(e =>
                e.Level == LogEventLevel.Error
                && e.MessageTemplate.Text.Contains("Identity provider threw", StringComparison.Ordinal)
            );
            ScalarProperty(errorEvent, "ExceptionType").Should().Be(nameof(InvalidOperationException));
            errorEvent.Properties.Should().ContainKey("Operation");
            errorEvent.Properties.Should().ContainKey("TraceId");
            errorEvent.Exception.Should().BeNull();
            errorEvent.RenderMessage().Should().Contain("at ");
            errorEvent.RenderMessage().Should().NotContain(FakeIdentityService.ResultsFailureMessage);

            LogEvent debugEvent = events.Single(e =>
                e.Level == LogEventLevel.Debug
                && e.MessageTemplate.Text.Contains(
                    "Identity provider failure detail",
                    StringComparison.Ordinal
                )
            );
            debugEvent.Exception.Should().NotBeNull();
            debugEvent.Exception!.Message.Should().Be(FakeIdentityService.ResultsFailureMessage);

            events
                .Where(e => e != debugEvent)
                .SelectMany(e =>
                    new[] { e.RenderMessage() }
                        .Concat(e.Properties.Values.Select(PropertyText))
                        .Append(e.Exception?.Message ?? string.Empty)
                )
                .Should()
                .NotContain(text =>
                    text.Contains(FakeIdentityService.ResultsFailureMessage, StringComparison.Ordinal)
                );
        }

        /// <summary>
        /// Negative control (Disciplines: "verify the verifier" / C6): a resource route still logs
        /// its resolved path at both DMS layers. The complementary half of the negative control - a
        /// resource route still produces a Microsoft.AspNetCore.Hosting.Diagnostics event while an
        /// identity id/token route does not - is proven by
        /// <see cref="Given_The_Production_Logging_Pipeline"/>, which is the only harness that
        /// actually exercises <c>IdentityHostingDiagnosticsFilter</c> as wired into the production
        /// pipeline.
        /// </summary>
        [Test]
        public async Task A_resource_route_still_logs_its_resolved_path_at_both_layers()
        {
            var sink = new CapturingSerilogSink();
            var identityService = new FakeIdentityService();
            await using var factory = CreateFactory(identityService, sink);
            using var client = factory.CreateClient();

            // No Authorization header: JwtAuthenticationMiddleware (Core) rejects the request before
            // any backend or datastore call, but only after RequestResponseLoggingMiddleware - the
            // first Core pipeline step - has already wrapped it, so a completion event is still
            // emitted with the real, unredacted path.
            using var response = await client.GetAsync("/data/ed-fi/students/abc");
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

            IReadOnlyList<LogEvent> events = sink.Events;

            LogEvent frontendEvent = events.Single(IsFrontendCompletionEvent);
            ScalarProperty(frontendEvent, "Path").Should().Be("/data/ed-fi/students/abc");

            LogEvent coreEvent = events.Single(IsCoreCompletionEvent);
            ScalarProperty(coreEvent, "Path").Should().Be("/ed-fi/students/abc");
        }
    }

    [TestFixture]
    public class Given_Multi_Tenancy_Is_Enabled
    {
        [Test]
        public async Task It_keeps_the_tenant_and_qualifier_literals_while_redacting_the_identifier()
        {
            var sink = new CapturingSerilogSink();
            var identityService = new FakeIdentityService();
            await using var factory = CreateFactory(
                identityService,
                sink,
                multiTenancy: true,
                routeQualifierSegments: "districtId,schoolYear"
            );
            using var client = factory.CreateClient();

            using var response = await client.SendAsync(
                AuthorizedGet($"/tenant-a/255901/2026/identity/v2/identities/{UniqueId}")
            );
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            IReadOnlyList<LogEvent> events = sink.Events;
            foreach (
                LogEvent completed in events.Where(e =>
                    IsFrontendCompletionEvent(e) || IsCoreCompletionEvent(e)
                )
            )
            {
                ScalarProperty(completed, "Path").Should().NotContain(UniqueId);
                completed.RenderMessage().Should().NotContain(UniqueId);
            }

            // No literal braces: LoggingSanitizer.SanitizeInternalValueForLogging's Method/Path
            // allowlist (letters, digits, space, and `_-.:/\`) strips `{` and `}` from any value,
            // including this one, same as it always has for a non-identity path - only the
            // identifier segment itself is what D11 promises never reaches the log.
            const string expectedPath = "/tenant-a/255901/2026/identity/v2/identities/id";

            LogEvent frontendCompleted = events.Single(IsFrontendCompletionEvent);
            ScalarProperty(frontendCompleted, "Path").Should().Be(expectedPath);

            LogEvent coreCompleted = events.Single(IsCoreCompletionEvent);
            ScalarProperty(coreCompleted, "Path").Should().Be(expectedPath);
        }
    }

    /// <summary>
    /// Exercises <c>IdentityHostingDiagnosticsFilter</c> exactly as
    /// <c>LoggingConfigurator.ConfigureLogging</c> wires it, without going through
    /// <c>WebApplicationFactory</c> (see the class remarks for why that host cannot prove this).
    /// </summary>
    [TestFixture]
    public class Given_The_Production_Logging_Pipeline
    {
        private string _logFilePath = string.Empty;

        [SetUp]
        public void SetUp() => _logFilePath = CreateTempLogFilePath();

        [TearDown]
        public void TearDown()
        {
            if (File.Exists(_logFilePath))
            {
                File.Delete(_logFilePath);
            }
        }

        private static string CreateTempLogFilePath() =>
            Path.Combine(Path.GetTempPath(), $"identity-hosting-diagnostics-{Guid.NewGuid():N}.log");

        private static string FindRepositoryFile(params string[] pathParts)
        {
            var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (directory is not null)
            {
                var candidate = Path.Combine([directory.FullName, .. pathParts]);
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }

            throw new FileNotFoundException("Could not find repository file.", Path.Combine(pathParts));
        }

        private Serilog.ILogger BuildProductionLogger()
        {
            string appsettingsPath = FindRepositoryFile(
                "src",
                "dms",
                "frontend",
                "EdFi.DataManagementService.Frontend.AspNetCore",
                "appsettings.json"
            );

            IConfigurationRoot configuration = new ConfigurationBuilder()
                .AddJsonFile(appsettingsPath, optional: false)
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["Serilog:MinimumLevel:Default"] = "Debug",
                        ["Serilog:WriteTo:0:Name"] = "File",
                        ["Serilog:WriteTo:0:Args:path"] = _logFilePath,
                        ["Serilog:WriteTo:0:Args:rollingInterval"] = "Infinite",
                        ["Serilog:WriteTo:0:Args:formatter:type"] =
                            "Serilog.Formatting.Json.JsonFormatter, Serilog",
                        ["Serilog:WriteTo:0:Args:formatter:renderMessage"] = "true",
                    }
                )
                .Build();

            // The real production method: IdentityHostingDiagnosticsFilter is wired in here, in code,
            // unconditionally - not driven by this configuration.
            return LoggingConfigurator.ConfigureLogging(configuration);
        }

        /// <summary>
        /// Shaped exactly like Task 4's Probe (a): both request-starting (Information) and
        /// request-finished (Information) carry both Path and RequestPath, holding the identical
        /// value.
        /// </summary>
        private static void EmitHostingDiagnosticsRequestEvents(Serilog.ILogger logger, string path)
        {
            Serilog.ILogger scoped = logger
                .ForContext("SourceContext", HostingDiagnosticsSourceContext)
                .ForContext("Path", path)
                .ForContext("RequestPath", path);

            scoped.Information(
                "Request starting {Protocol} {Method} {Scheme}://{Host}{PathBase}{Path}{QueryString} - {ContentType} {ContentLength}",
                "HTTP/1.1",
                "GET",
                "http",
                "localhost",
                "",
                path,
                ""
            );
            scoped.Information(
                "Request finished {Protocol} {Method} {Scheme}://{Host}{PathBase}{Path}{QueryString} - {StatusCode} {ContentLength} {ContentType} {ElapsedMilliseconds}ms",
                "HTTP/1.1",
                "GET",
                "http",
                "localhost",
                "",
                path,
                "",
                200,
                (int?)null,
                "application/json",
                1.23
            );
        }

        /// <summary>
        /// Shaped exactly like Task 4's Probe (a) third event: same SourceContext, Debug, and
        /// carries neither Path nor RequestPath. A filter that indexes those properties without a
        /// presence check throws or misfires on an event like this one.
        /// </summary>
        private static void EmitUnrelatedHostingStartupEvent(Serilog.ILogger logger) =>
            logger
                .ForContext("SourceContext", HostingDiagnosticsSourceContext)
                .ForContext("assemblyName", "Acme.Fixture")
                .Debug("Loaded hosting startup assembly {assemblyName}", "Acme.Fixture");

        [Test]
        public void It_excludes_only_the_identity_get_by_id_and_results_poll_shapes()
        {
            const string GetByIdPath = "/identity/v2/identities/605943412";
            const string ResultsPath = "/identity/v2/identities/results/SECRET-TOKEN-XYZ";
            const string MultiTenantGetByIdPath = "/tenant-a/255901/2026/identity/v2/identities/605943412";
            const string FindPath = "/identity/v2/identities/find";
            const string SearchPath = "/identity/v2/identities/search";
            const string ResourcePath = "/data/ed-fi/students/abc";

            Serilog.ILogger logger = BuildProductionLogger();
            try
            {
                EmitHostingDiagnosticsRequestEvents(logger, GetByIdPath);
                EmitHostingDiagnosticsRequestEvents(logger, ResultsPath);
                EmitHostingDiagnosticsRequestEvents(logger, MultiTenantGetByIdPath);
                EmitHostingDiagnosticsRequestEvents(logger, FindPath);
                EmitHostingDiagnosticsRequestEvents(logger, SearchPath);
                EmitHostingDiagnosticsRequestEvents(logger, ResourcePath);
                EmitUnrelatedHostingStartupEvent(logger);
            }
            finally
            {
                (logger as IDisposable)?.Dispose();
            }

            IReadOnlyList<CapturedEvent> events = ReadCapturedEvents(_logFilePath);
            IReadOnlyList<CapturedEvent> hostingDiagnosticsEvents = events
                .Where(e => e.Property("SourceContext") == HostingDiagnosticsSourceContext)
                .ToList();

            hostingDiagnosticsEvents.Where(e => e.Property("Path") == GetByIdPath).Should().BeEmpty();
            hostingDiagnosticsEvents.Where(e => e.Property("Path") == ResultsPath).Should().BeEmpty();
            hostingDiagnosticsEvents
                .Where(e => e.Property("Path") == MultiTenantGetByIdPath)
                .Should()
                .BeEmpty();

            // Not excluded: no identifier segment (find/search), or not an identity route at all.
            hostingDiagnosticsEvents.Where(e => e.Property("Path") == FindPath).Should().HaveCount(2);
            hostingDiagnosticsEvents.Where(e => e.Property("Path") == SearchPath).Should().HaveCount(2);
            hostingDiagnosticsEvents.Where(e => e.Property("Path") == ResourcePath).Should().HaveCount(2);

            // The unrelated Debug event on the same SourceContext, carrying neither Path nor
            // RequestPath, was not mistaken for a match (no throw above, and it still comes through).
            hostingDiagnosticsEvents
                .Where(e => e.Level == "Debug" && e.Property("assemblyName") == "Acme.Fixture")
                .Should()
                .ContainSingle();
        }

        private sealed record CapturedEvent(string Level, IReadOnlyDictionary<string, string> Properties)
        {
            public string? Property(string name) => Properties.GetValueOrDefault(name);
        }

        private static IReadOnlyList<CapturedEvent> ReadCapturedEvents(string path)
        {
            if (!File.Exists(path))
            {
                return [];
            }

            List<CapturedEvent> events = [];
            foreach (string line in File.ReadAllLines(path))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                JsonObject root = JsonNode.Parse(line)!.AsObject();
                Dictionary<string, string> properties = [];
                if (root["Properties"] is JsonObject propertiesObject)
                {
                    foreach (KeyValuePair<string, JsonNode?> property in propertiesObject)
                    {
                        properties[property.Key] = property.Value is JsonValue value
                            ? value.ToString()
                            : property.Value?.ToJsonString() ?? string.Empty;
                    }
                }

                events.Add(
                    new CapturedEvent(
                        Level: root["Level"]?.GetValue<string>() ?? "Information",
                        Properties: properties
                    )
                );
            }

            return events;
        }
    }

    private sealed class IdentityGrantingClaimSetProvider : IClaimSetProvider
    {
        public Task<IList<ClaimSet>> GetAllClaimSets(
            string? tenant = null,
            CancellationToken cancellationToken = default
        )
        {
            return Task.FromResult<IList<ClaimSet>>([
                new ClaimSet(
                    Name: ClaimSetName,
                    ResourceClaims:
                    [
                        new ResourceClaim(
                            Name: $"{Conventions.EdFiOdsServiceClaimBaseUri}/identity",
                            Action: "Read",
                            AuthorizationStrategies:
                            [
                                new AuthorizationStrategy(
                                    AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired
                                ),
                            ]
                        ),
                    ]
                ),
            ]);
        }
    }

    /// <summary>
    /// The plugin boundary double: GetByIdAsync answers a fixed success payload; ResultsAsync always
    /// throws, so the request drives both the redaction path (the token must never reach a log) and
    /// the C7 provider-exception logging contract in the same call.
    /// </summary>
    private sealed class FakeIdentityService : IIdentityService
    {
        /// <summary>
        /// Deliberately distinctive so the test can assert it appears only inside the Debug-level
        /// event and nowhere else - never in the response body, never at Error or above.
        /// </summary>
        public const string ResultsFailureMessage = "SENTINEL-provider-detail-never-logged-above-debug";

        public IdentityCapabilities Capabilities =>
            IdentityCapabilities.GetById | IdentityCapabilities.Results;

        public Task<IdentityResult> CreateAsync(
            JsonObject request,
            IdentityRequestContext context,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException("Not exercised by this fixture.");

        public Task<IdentityResult> GetByIdAsync(
            string uniqueId,
            IdentityRequestContext context,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                new IdentityResult
                {
                    Status = IdentityResultStatus.Success,
                    Payload = new JsonObject { ["UniqueId"] = uniqueId },
                }
            );

        public Task<IdentityAsyncResult> FindAsync(
            IReadOnlyList<string> uniqueIds,
            IdentityRequestContext context,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException("Not exercised by this fixture.");

        public Task<IdentityAsyncResult> SearchAsync(
            IReadOnlyList<JsonObject> requests,
            IdentityRequestContext context,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException("Not exercised by this fixture.");

        public Task<IdentityResult> ResultsAsync(
            string requestToken,
            IdentityRequestContext context,
            CancellationToken cancellationToken
        ) => throw new InvalidOperationException(ResultsFailureMessage);
    }

    private sealed class CapturingSerilogSink : ILogEventSink
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<LogEvent> _events = new();

        public IReadOnlyList<LogEvent> Events => _events.ToArray();

        public void Emit(LogEvent logEvent) => _events.Enqueue(logEvent);
    }
}
