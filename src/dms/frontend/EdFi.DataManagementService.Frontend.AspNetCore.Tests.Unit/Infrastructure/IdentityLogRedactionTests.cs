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
/// No identity operation identifier reaches any property or rendered message of a log event, at
/// either DMS layer or in the framework's own hosting-diagnostics events, while a resource route (the
/// negative control) and the other three identity routes are unaffected. Exception detail attached to
/// the identity provider boundary's Debug event is outside that guarantee by design and is not
/// scanned here.
/// </summary>
/// <remarks>
/// Two independent proofs, because they need two different harnesses:
/// <para>
/// <b>Redaction and sanitized failure logging</b> (<see cref="Given_The_Toggle_Is_On_With_Single_Tenancy"/>,
/// <see cref="Given_Multi_Tenancy_Is_Enabled"/>) drive the real <c>IApiService</c> and Core identity
/// pipelines through a booted host, with only the plugin boundary (<c>IIdentityService</c>) and the
/// CMS providers faked - the same shape <c>IdentityLocationRoundTripTests</c> uses - and
/// capture through a second Serilog provider registered via <c>ConfigureServices</c>
/// (<c>PluginHostProbe.cs:152-183</c>'s pattern), whose logger runs the same
/// <c>LoggingConfigurator.ApplyLogContextAndIdentityRedaction</c> stage production does. This proves
/// that no captured event - framework, DMS frontend, DMS Core, or handler - carries the raw
/// identifier in any property (including the request scope's <c>RequestPath</c>) or in its rendered
/// message, and that <c>IdentityProviderBoundary</c>'s sanitized failure-level log carries no
/// provider detail.
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
/// fully controls (no hosting timing involved), and drives synthetic events shaped exactly like the
/// framework's own hosting-diagnostics events: <c>Microsoft.AspNetCore.Hosting.Diagnostics</c>
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

    /// <summary>
    /// Describes every captured event that carries <paramref name="secret"/> anywhere a sink could
    /// write it: the rendered message, or any scalar property value, including scalars nested inside
    /// structure, sequence, or dictionary property values. Empty when nothing leaks.
    /// </summary>
    private static IReadOnlyList<string> EventsLeaking(IEnumerable<LogEvent> events, string secret) =>
        events
            .SelectMany(e =>
                e.Properties.Where(property => ValueContains(property.Value, secret))
                    .Select(property =>
                        $"[{ScalarProperty(e, "SourceContext")}] {e.MessageTemplate.Text} :: property {property.Key}"
                    )
                    .Concat(
                        e.RenderMessage().Contains(secret, StringComparison.Ordinal)
                            ?
                            [
                                $"[{ScalarProperty(e, "SourceContext")}] {e.MessageTemplate.Text} :: rendered message",
                            ]
                            : []
                    )
            )
            .ToList();

    private static bool ValueContains(LogEventPropertyValue value, string secret) =>
        value switch
        {
            ScalarValue scalar => scalar.Value?.ToString()?.Contains(secret, StringComparison.Ordinal)
                ?? false,
            StructureValue structure => structure.Properties.Any(p => ValueContains(p.Value, secret)),
            SequenceValue sequence => sequence.Elements.Any(element => ValueContains(element, secret)),
            DictionaryValue dictionary => dictionary.Elements.Any(pair =>
                ValueContains(pair.Key, secret) || ValueContains(pair.Value, secret)
            ),
            _ => value.ToString().Contains(secret, StringComparison.Ordinal),
        };

    private static WebApplicationFactory<Program> CreateFactory(
        IIdentityService identityService,
        CapturingSerilogSink sink,
        bool multiTenancy = false,
        string routeQualifierSegments = ""
    )
    {
        // The capture runs the same log-context and identity redaction stage that
        // LoggingConfigurator.ConfigureLogging applies in production, so every captured event is
        // what a production sink would receive.
        Logger captureLogger = LoggingConfigurator
            .ApplyLogContextAndIdentityRedaction(new LoggerConfiguration().MinimumLevel.Verbose())
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
    public class Given_A_Get_By_Id_Success_With_Single_Tenancy
    {
        private HttpResponseMessage _response = null!;
        private IReadOnlyList<LogEvent> _events = null!;
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;

        [OneTimeSetUp]
        public async Task Setup()
        {
            var sink = new CapturingSerilogSink();
            var identityService = new FakeIdentityService();
            _factory = CreateFactory(identityService, sink);
            _client = _factory.CreateClient();

            _response = await _client.SendAsync(AuthorizedGet($"/identity/v2/identities/{UniqueId}"));
            _events = sink.Events;
        }

        [OneTimeTearDown]
        public async Task TearDown()
        {
            _response.Dispose();
            _client.Dispose();
            await _factory.DisposeAsync();
        }

        [Test]
        public void It_returns_200()
        {
            _response.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Test]
        public void It_never_logs_the_unique_id_in_any_property_or_rendered_message_of_any_event()
        {
            // Every captured event, not just the DMS completion events: the framework's request
            // scope stamps RequestPath onto every event logged while the request runs (routing,
            // DMS frontend and Core, handler logs), so any event is a potential carrier. The
            // capture goes through the same redaction stage LoggingConfigurator applies in
            // production (see CreateFactory); the hosting-diagnostics filter half of that stage is
            // also proven separately by Given_The_Production_Logging_Pipeline.
            IReadOnlyList<string> leaks = EventsLeaking(_events, UniqueId);
            leaks.Should().BeEmpty("no event may carry it, yet these did: {0}", string.Join(" | ", leaks));
        }

        [Test]
        public void It_redacts_the_request_path_scope_property()
        {
            _events
                .Where(e => e.Properties.ContainsKey("RequestPath"))
                .Select(e => ScalarProperty(e, "RequestPath"))
                .Should()
                .NotBeEmpty()
                .And.AllBe("/identity/v2/identities/{id}");
        }

        [Test]
        public void It_redacts_the_unique_id_in_the_frontend_completion_path()
        {
            // No literal braces: LoggingSanitizer.SanitizeInternalValueForLogging's Method/Path
            // allowlist (letters, digits, space, and `_-.:/\`) strips `{` and `}` from every value
            // it sanitizes, redacted or not - the redaction guarantee here is only that the
            // identifier segment itself never reaches this Path value.
            LogEvent frontendCompleted = _events.Single(IsFrontendCompletionEvent);
            ScalarProperty(frontendCompleted, "Path").Should().Be("/identity/v2/identities/id");
        }

        [Test]
        public void It_redacts_the_unique_id_in_the_core_completion_path()
        {
            LogEvent coreCompleted = _events.Single(IsCoreCompletionEvent);
            ScalarProperty(coreCompleted, "Path").Should().Be("/identity/v2/identities/id");
        }
    }

    [TestFixture]
    public class Given_A_Results_Poll_Failure_With_Single_Tenancy
    {
        // Used only by the assertion below, which must inspect every property value on every
        // other captured event (not just Path) to prove the provider's message appears nowhere
        // above Debug.
        private static string PropertyText(LogEventPropertyValue value) =>
            value is ScalarValue { Value: not null } scalar
                ? scalar.Value.ToString() ?? string.Empty
                : value.ToString();

        private HttpResponseMessage _response = null!;
        private IReadOnlyList<LogEvent> _events = null!;
        private LogEvent _errorEvent = null!;
        private LogEvent _debugEvent = null!;
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;

        [OneTimeSetUp]
        public async Task Setup()
        {
            var sink = new CapturingSerilogSink();
            var identityService = new FakeIdentityService();
            _factory = CreateFactory(identityService, sink);
            _client = _factory.CreateClient();

            _response = await _client.SendAsync(
                AuthorizedGet($"/identity/v2/identities/results/{ResultsToken}")
            );
            _events = sink.Events;

            _errorEvent = _events.Single(e =>
                e.Level == LogEventLevel.Error
                && e.MessageTemplate.Text.Contains("Identity provider threw", StringComparison.Ordinal)
            );
            _debugEvent = _events.Single(e =>
                e.Level == LogEventLevel.Debug
                && e.MessageTemplate.Text.Contains(
                    "Identity provider failure detail",
                    StringComparison.Ordinal
                )
            );
        }

        [OneTimeTearDown]
        public async Task TearDown()
        {
            _response.Dispose();
            _client.Dispose();
            await _factory.DisposeAsync();
        }

        [Test]
        public void It_returns_502_bad_gateway()
        {
            _response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        }

        [Test]
        public void It_never_logs_the_results_token_in_any_property_or_rendered_message_of_any_event()
        {
            IReadOnlyList<string> leaks = EventsLeaking(_events, ResultsToken);
            leaks.Should().BeEmpty("no event may carry it, yet these did: {0}", string.Join(" | ", leaks));
        }

        [Test]
        public void It_redacts_the_request_path_scope_property()
        {
            _events
                .Where(e => e.Properties.ContainsKey("RequestPath"))
                .Select(e => ScalarProperty(e, "RequestPath"))
                .Should()
                .NotBeEmpty()
                .And.AllBe("/identity/v2/identities/results/{token}");
        }

        [Test]
        public void It_redacts_the_token_in_the_frontend_completion_path()
        {
            LogEvent frontendFailed = _events.Single(IsFrontendCompletionEvent);
            ScalarProperty(frontendFailed, "Path").Should().Be("/identity/v2/identities/results/token");
        }

        [Test]
        public void It_redacts_the_token_in_the_core_completion_path()
        {
            LogEvent coreCompletedOrFailed = _events.Single(IsCoreCompletionEvent);
            ScalarProperty(coreCompletedOrFailed, "Path")
                .Should()
                .Be("/identity/v2/identities/results/token");
        }

        // The provider boundary's sanitized failure-level log carries the exception type, the
        // stage and operation, and the trace id, plus stack frames, but never the provider's own
        // message. The full exception (with the message) is logged only at Debug.
        [Test]
        public void It_logs_the_exception_type_on_the_error_event()
        {
            ScalarProperty(_errorEvent, "ExceptionType").Should().Be(nameof(InvalidOperationException));
        }

        [Test]
        public void It_logs_the_operation_and_trace_id_properties_on_the_error_event()
        {
            _errorEvent.Properties.Should().ContainKey("Operation");
            _errorEvent.Properties.Should().ContainKey("TraceId");
        }

        [Test]
        public void It_does_not_attach_the_raw_exception_to_the_error_event()
        {
            _errorEvent.Exception.Should().BeNull();
        }

        [Test]
        public void It_includes_stack_frames_but_not_the_provider_message_in_the_error_event()
        {
            _errorEvent.RenderMessage().Should().Contain("at ");
            _errorEvent.RenderMessage().Should().NotContain(FakeIdentityService.ResultsFailureMessage);
        }

        [Test]
        public void It_logs_the_raw_exception_message_only_at_debug()
        {
            _debugEvent.Exception.Should().NotBeNull();
            _debugEvent.Exception!.Message.Should().Be(FakeIdentityService.ResultsFailureMessage);
        }

        [Test]
        public void It_never_leaks_the_provider_message_outside_the_debug_event()
        {
            _events
                .Where(e => e != _debugEvent)
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
    }

    /// <summary>
    /// Negative control (Disciplines: "verify the verifier"): a resource route still logs its
    /// resolved path at both DMS layers. The complementary half of the negative control - a resource
    /// route still produces a Microsoft.AspNetCore.Hosting.Diagnostics event while an identity
    /// id/token route does not - is proven by <see cref="Given_The_Production_Logging_Pipeline"/>,
    /// which is the only harness that actually exercises
    /// <c>IdentityHostingDiagnosticsFilter</c> as wired into the production pipeline.
    /// </summary>
    [TestFixture]
    public class Given_The_Toggle_Is_On_With_Single_Tenancy
    {
        private HttpResponseMessage _response = null!;
        private IReadOnlyList<LogEvent> _events = null!;
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;

        [OneTimeSetUp]
        public async Task Setup()
        {
            var sink = new CapturingSerilogSink();
            var identityService = new FakeIdentityService();
            _factory = CreateFactory(identityService, sink);
            _client = _factory.CreateClient();

            // No Authorization header: JwtAuthenticationMiddleware (Core) rejects the request before
            // any backend or datastore call, but only after RequestResponseLoggingMiddleware - the
            // first Core pipeline step - has already wrapped it, so a completion event is still
            // emitted with the real, unredacted path.
            _response = await _client.GetAsync("/data/ed-fi/students/abc");
            _events = sink.Events;
        }

        [OneTimeTearDown]
        public async Task TearDown()
        {
            _response.Dispose();
            _client.Dispose();
            await _factory.DisposeAsync();
        }

        [Test]
        public void It_returns_401_unauthorized()
        {
            _response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        [Test]
        public void It_still_logs_its_resolved_path_at_both_layers()
        {
            LogEvent frontendEvent = _events.Single(IsFrontendCompletionEvent);
            ScalarProperty(frontendEvent, "Path").Should().Be("/data/ed-fi/students/abc");

            LogEvent coreEvent = _events.Single(IsCoreCompletionEvent);
            ScalarProperty(coreEvent, "Path").Should().Be("/ed-fi/students/abc");
        }

        [Test]
        public void It_still_logs_its_raw_request_path_scope_property()
        {
            _events
                .Where(e => e.Properties.ContainsKey("RequestPath"))
                .Select(e => ScalarProperty(e, "RequestPath"))
                .Should()
                .NotBeEmpty()
                .And.AllBe("/data/ed-fi/students/abc");
        }
    }

    [TestFixture]
    public class Given_Multi_Tenancy_Is_Enabled
    {
        private HttpResponseMessage _response = null!;
        private IReadOnlyList<LogEvent> _events = null!;
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;

        [OneTimeSetUp]
        public async Task Setup()
        {
            var sink = new CapturingSerilogSink();
            var identityService = new FakeIdentityService();
            _factory = CreateFactory(
                identityService,
                sink,
                multiTenancy: true,
                routeQualifierSegments: "districtId,schoolYear"
            );
            _client = _factory.CreateClient();

            _response = await _client.SendAsync(
                AuthorizedGet($"/tenant-a/255901/2026/identity/v2/identities/{UniqueId}")
            );
            _events = sink.Events;
        }

        [OneTimeTearDown]
        public async Task TearDown()
        {
            _response.Dispose();
            _client.Dispose();
            await _factory.DisposeAsync();
        }

        [Test]
        public void It_returns_200()
        {
            _response.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Test]
        public void It_never_logs_the_unique_id_in_any_property_or_rendered_message_of_any_event()
        {
            IReadOnlyList<string> leaks = EventsLeaking(_events, UniqueId);
            leaks.Should().BeEmpty("no event may carry it, yet these did: {0}", string.Join(" | ", leaks));
        }

        [Test]
        public void It_redacts_the_request_path_scope_property_and_keeps_the_tenant_and_qualifier_literals()
        {
            _events
                .Where(e => e.Properties.ContainsKey("RequestPath"))
                .Select(e => ScalarProperty(e, "RequestPath"))
                .Should()
                .NotBeEmpty()
                .And.AllBe("/tenant-a/255901/2026/identity/v2/identities/{id}");
        }

        [Test]
        public void It_keeps_the_tenant_and_qualifier_literals_in_the_frontend_completion_path()
        {
            // No literal braces: LoggingSanitizer.SanitizeInternalValueForLogging's Method/Path
            // allowlist (letters, digits, space, and `_-.:/\`) strips `{` and `}` from any value,
            // including this one, same as it always has for a non-identity path - only the
            // identifier segment itself is what the redaction guarantee promises never reaches this
            // Path value.
            const string expectedPath = "/tenant-a/255901/2026/identity/v2/identities/id";

            LogEvent frontendCompleted = _events.Single(IsFrontendCompletionEvent);
            ScalarProperty(frontendCompleted, "Path").Should().Be(expectedPath);
        }

        [Test]
        public void It_keeps_the_tenant_and_qualifier_literals_in_the_core_completion_path()
        {
            const string expectedPath = "/tenant-a/255901/2026/identity/v2/identities/id";

            LogEvent coreCompleted = _events.Single(IsCoreCompletionEvent);
            ScalarProperty(coreCompleted, "Path").Should().Be(expectedPath);
        }
    }

    /// <summary>
    /// An identity-shaped path whose leading segment count does not match the host's configuration
    /// (here: no tenant segment on a multi-tenant host) matches no endpoint and falls through to a
    /// 404, yet the frontend completion event still logs its path. Redaction must not depend on the
    /// configured prefix shape, or the identifier reaches that event's <c>Path</c> and rendered message.
    /// </summary>
    [TestFixture]
    public class Given_A_Get_By_Id_Path_Without_The_Tenant_Segment_On_A_Multi_Tenant_Host
    {
        private HttpResponseMessage _response = null!;
        private IReadOnlyList<LogEvent> _events = null!;
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;

        [OneTimeSetUp]
        public async Task Setup()
        {
            var sink = new CapturingSerilogSink();
            var identityService = new FakeIdentityService();
            _factory = CreateFactory(identityService, sink, multiTenancy: true);
            _client = _factory.CreateClient();

            _response = await _client.SendAsync(AuthorizedGet($"/identity/v2/identities/{UniqueId}"));
            _events = sink.Events;
        }

        [OneTimeTearDown]
        public async Task TearDown()
        {
            _response.Dispose();
            _client.Dispose();
            await _factory.DisposeAsync();
        }

        [Test]
        public void It_returns_404()
        {
            _response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        [Test]
        public void It_never_logs_the_unique_id_in_any_property_or_rendered_message_of_any_event()
        {
            IReadOnlyList<string> leaks = EventsLeaking(_events, UniqueId);
            leaks.Should().BeEmpty("no event may carry it, yet these did: {0}", string.Join(" | ", leaks));
        }

        [Test]
        public void It_redacts_the_unique_id_in_the_frontend_completion_path()
        {
            LogEvent frontendCompleted = _events.Single(IsFrontendCompletionEvent);
            ScalarProperty(frontendCompleted, "Path").Should().Be("/identity/v2/identities/id");
        }
    }

    /// <summary>
    /// Unit-level coverage of <see cref="IdentityRoutePathRedactor.RedactWithAnyPrefix"/> itself:
    /// ASP.NET Core routing matches these routes case-insensitively and tolerates a
    /// trailing slash, so the redactor's regexes must too, or an identifier reaches
    /// <c>LoggingMiddleware</c>'s scope <c>Path</c> unredacted whenever a client or an upstream
    /// proxy sends the route in a different case or with a trailing slash.
    /// </summary>
    [TestFixture]
    public class Given_A_Mixed_Case_Or_Trailing_Slash_Path_For_The_Route_Path_Redactor
    {
        [TestCase("/Identity/V2/Identities/605943412", "/Identity/V2/Identities/{id}")]
        [TestCase("/identity/v2/identities/605943412/", "/identity/v2/identities/{id}")]
        [TestCase("/IDENTITY/V2/IDENTITIES/605943412/", "/IDENTITY/V2/IDENTITIES/{id}")]
        public void It_redacts_a_get_by_id_path_regardless_of_case_or_a_trailing_slash(
            string path,
            string expected
        )
        {
            IdentityRoutePathRedactor.RedactWithAnyPrefix(path).Should().Be(expected);
        }

        [TestCase(
            "/Identity/V2/Identities/Results/SECRET-TOKEN-XYZ",
            "/Identity/V2/Identities/Results/{token}"
        )]
        [TestCase(
            "/identity/v2/identities/results/SECRET-TOKEN-XYZ/",
            "/identity/v2/identities/results/{token}"
        )]
        public void It_redacts_a_results_poll_path_regardless_of_case_or_a_trailing_slash(
            string path,
            string expected
        )
        {
            IdentityRoutePathRedactor.RedactWithAnyPrefix(path).Should().Be(expected);
        }

        [TestCase("/identity/v2/identities/FIND/")]
        [TestCase("/identity/v2/identities/Search/")]
        public void It_leaves_find_and_search_unchanged_even_with_a_trailing_slash(string path)
        {
            IdentityRoutePathRedactor.RedactWithAnyPrefix(path).Should().Be(path);
        }

        [Test]
        public void It_keeps_the_tenant_and_qualifier_literal_case_while_redacting_a_mixed_case_trailing_slash_identifier()
        {
            IdentityRoutePathRedactor
                .RedactWithAnyPrefix("/tenant-a/255901/2026/IDENTITY/V2/IDENTITIES/605943412/")
                .Should()
                .Be("/tenant-a/255901/2026/IDENTITY/V2/IDENTITIES/{id}");
        }
    }

    /// <summary>
    /// Unit-level coverage of <see cref="IdentityHostingDiagnosticsFilter.Matches"/> itself,
    /// mirroring the case-insensitive and trailing-slash-tolerant cases above but
    /// against the predicate the framework hosting-diagnostics filter actually evaluates.
    /// </summary>
    [TestFixture]
    public class Given_A_Mixed_Case_Or_Trailing_Slash_Path_For_The_Hosting_Diagnostics_Filter
    {
        private static LogEvent CaptureHostingDiagnosticsEvent(string path)
        {
            var sink = new CapturingSerilogSink();
            using Logger captureLogger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();

            captureLogger
                .ForContext("SourceContext", HostingDiagnosticsSourceContext)
                .ForContext("Path", path)
                .ForContext("RequestPath", path)
                .Information("Request starting {Path}", path);

            return sink.Events.Single();
        }

        [TestCase("/Identity/V2/Identities/605943412")]
        [TestCase("/identity/v2/identities/605943412/")]
        [TestCase("/IDENTITY/V2/IDENTITIES/605943412/")]
        public void It_matches_a_get_by_id_path_regardless_of_case_or_a_trailing_slash(string path)
        {
            IdentityHostingDiagnosticsFilter.Matches(CaptureHostingDiagnosticsEvent(path)).Should().BeTrue();
        }

        [TestCase("/Identity/V2/Identities/Results/SECRET-TOKEN-XYZ")]
        [TestCase("/identity/v2/identities/results/SECRET-TOKEN-XYZ/")]
        public void It_matches_a_results_poll_path_regardless_of_case_or_a_trailing_slash(string path)
        {
            IdentityHostingDiagnosticsFilter.Matches(CaptureHostingDiagnosticsEvent(path)).Should().BeTrue();
        }

        [TestCase("/identity/v2/identities/FIND/")]
        [TestCase("/identity/v2/identities/Search/")]
        public void It_does_not_match_find_or_search_even_with_a_trailing_slash(string path)
        {
            IdentityHostingDiagnosticsFilter.Matches(CaptureHostingDiagnosticsEvent(path)).Should().BeFalse();
        }
    }

    /// <summary>
    /// The routing matcher's Debug-level candidate events carry the raw request path in their
    /// <c>Path</c> property and rendered message, so the filter drops them for an identity
    /// get-by-id or results-poll route, and keeps them for every other route.
    /// </summary>
    [TestFixture]
    public class Given_A_Routing_Matcher_Debug_Event_For_The_Hosting_Diagnostics_Filter
    {
        private static LogEvent CaptureRoutingMatcherEvent(string path)
        {
            var sink = new CapturingSerilogSink();
            using Logger captureLogger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.Sink(sink)
                .CreateLogger();

            captureLogger
                .ForContext("SourceContext", "Microsoft.AspNetCore.Routing.Matching.DfaMatcher")
                .Debug("{CandidateCount} candidate(s) found for the request path '{Path}'", 1, path);

            return sink.Events.Single();
        }

        [TestCase("/identity/v2/identities/605943412")]
        [TestCase("/tenant-a/255901/2026/identity/v2/identities/results/SECRET-TOKEN-XYZ")]
        public void It_matches_an_identity_get_by_id_or_results_poll_path(string path)
        {
            IdentityHostingDiagnosticsFilter.Matches(CaptureRoutingMatcherEvent(path)).Should().BeTrue();
        }

        [TestCase("/identity/v2/identities/find")]
        [TestCase("/data/ed-fi/students/abc")]
        public void It_does_not_match_find_or_a_resource_path(string path)
        {
            IdentityHostingDiagnosticsFilter.Matches(CaptureRoutingMatcherEvent(path)).Should().BeFalse();
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
        /// Shaped exactly like a real hosting-diagnostics event pair: both request-starting (Information) and
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
        /// Shaped exactly like a third, unrelated hosting-diagnostics event: same SourceContext, Debug, and
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

        [Test]
        public void It_redacts_the_request_path_scope_property_on_events_the_hosting_filter_keeps()
        {
            const string GetByIdPath = "/identity/v2/identities/605943412";
            const string ResultsPath = "/identity/v2/identities/results/SECRET-TOKEN-XYZ";
            const string MultiTenantGetByIdPath = "/tenant-a/255901/2026/identity/v2/identities/605943412";
            const string FindPath = "/identity/v2/identities/find";
            const string ResourcePath = "/data/ed-fi/students/abc";
            string[] paths = [GetByIdPath, ResultsPath, MultiTenantGetByIdPath, FindPath, ResourcePath];

            Serilog.ILogger logger = BuildProductionLogger();
            try
            {
                // The same route production takes: a Microsoft.Extensions.Logging logger over the
                // Serilog logger, inside a request scope shaped like the framework's own hosting
                // scope, which is what stamps RequestPath onto every event logged during a request.
                using var loggerFactory = new SerilogLoggerFactory(logger);
                Microsoft.Extensions.Logging.ILogger routingLogger = loggerFactory.CreateLogger(
                    "Microsoft.AspNetCore.Routing.EndpointMiddleware"
                );

                for (int probeCase = 0; probeCase < paths.Length; probeCase++)
                {
                    string path = paths[probeCase];
                    using (
                        routingLogger.BeginScope(
                            new Dictionary<string, object>
                            {
                                ["RequestId"] = "0HN0000000000:00000001",
                                ["RequestPath"] = path,
                            }
                        )
                    )
                    {
                        routingLogger.LogInformation(
                            "Executing endpoint '{EndpointName}' for {ProbeCase}",
                            "HTTP: GET /identity/v2/identities/{id}",
                            probeCase
                        );
                    }
                }
            }
            finally
            {
                (logger as IDisposable)?.Dispose();
            }

            IReadOnlyList<CapturedEvent> events = ReadCapturedEvents(_logFilePath);

            string? RequestPathFor(string path) =>
                events
                    .Single(e => e.Property("ProbeCase") == Array.IndexOf(paths, path).ToString())
                    .Property("RequestPath");

            RequestPathFor(GetByIdPath).Should().Be("/identity/v2/identities/{id}");
            RequestPathFor(ResultsPath).Should().Be("/identity/v2/identities/results/{token}");
            RequestPathFor(MultiTenantGetByIdPath)
                .Should()
                .Be("/tenant-a/255901/2026/identity/v2/identities/{id}");

            // Not redacted: no identifier segment (find), or not an identity route at all.
            RequestPathFor(FindPath).Should().Be(FindPath);
            RequestPathFor(ResourcePath).Should().Be(ResourcePath);
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
    /// throws, so the request drives both the redaction path (the token must never reach a logged property or rendered message) and
    /// the provider-exception logging contract in the same call.
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
