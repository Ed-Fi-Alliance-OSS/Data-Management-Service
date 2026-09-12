// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Security;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit;

/// <summary>
/// FR-LOG-6: the normalized correlation ID must be identical in every log event and every
/// error response body, whatever the status code and whichever layer produced the response.
/// This fixture boots the real DMS HTTP pipeline in-process with WebApplicationFactory and
/// asserts parity across four status codes produced by three different layers:
/// <list type="bullet">
/// <item>404 from <c>Program.cs</c>'s <c>MapFallback</c> catch-all</item>
/// <item>401 from <c>HealthCheckEndpointModule</c> via <c>FailureResponse.ForAuthenticationFailure</c></item>
/// <item>403 from <c>HealthCheckEndpointModule</c> via <c>FailureResponse.ForForbidden</c></item>
/// <item>429 from <c>WebApplicationBuilderExtensions</c> via <c>FailureResponse.ForTooManyRequests</c></item>
/// </list>
/// A fifth request - the permitted <c>/health</c> call that opens the rate-limited arm - carries
/// the same hostile correlation ID and is expected to answer 200, which is the "still succeeds"
/// half of FR-LOG-5 that the four failure paths cannot evidence.
/// Every one of those requests is also sent a second time with a clean correlation ID,
/// which is FR-LOG-5's evidence: the status codes are compared against each other rather than
/// against hardcoded expectations. No database is required: every one of these responses is
/// produced before the request reaches a backend.
/// </summary>
[TestFixture]
[NonParallelizable]
public class Given_A_Hostile_Correlation_Id_On_Requests_That_Fail_In_Different_Layers
{
    private const string CorrelationHeader = "x-correlation-id";
    private const int ConfiguredMaxLength = 24;
    private const string ValidRequiredRole = "dms-document-cache-operator";
    private const string RoleClaimType = "operator_role";
    private const string ValidBearerToken = "valid-token";

    /// <summary>
    /// Over-length (49 characters against a configured cap of 24) and carrying control
    /// characters, so it exercises the allowlist and the length cap at the same time. The
    /// braces are the FR-LOG-3 check: the stricter Method/Path allowlist would strip them.
    /// </summary>
    private const string HostileCorrelationId = "hos\r\ntile{id}XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX";

    /// <summary>
    /// Truncate-to-24-then-filter yields this. Filter-then-truncate would instead yield
    /// "hostile{id}XXXXXXXXXXXXX" (a full 24 characters), so this literal also pins the order
    /// end to end. The result being shorter than the cap is the intended consequence.
    /// </summary>
    private const string ExpectedCorrelationId = "hostile{id}XXXXXXXXXXX";

    /// <summary>
    /// The control arm for FR-LOG-5: short enough for the cap and holding nothing the
    /// allowlist would remove, so normalization leaves it alone.
    /// </summary>
    private const string CleanCorrelationId = "clean-correlation-id";

    /// <summary>
    /// The number of requests each arm sends on the rate-limited factory: one that consumes the
    /// window's single permit, one that is rejected. The recorder keeps only the two
    /// request-logging event ids and the middleware emits exactly one of those per request, so
    /// recording at least this many proves the *rejected* request logged too - which
    /// non-emptiness alone does not, since the permitted request satisfies that on its own.
    /// </summary>
    private const int RateLimitedArmRequestCount = 2;

    private CorrelationIdRecordingLoggerProvider _loggerProvider = default!;
    private CorrelationIdRecordingLoggerProvider _rateLimitedLoggerProvider = default!;

    /// <summary>
    /// The control arm's own recorder. Nothing asserts on it - the control arm exists to compare
    /// status codes - but the factory needs a provider, and giving it its own keeps the hostile
    /// arm's snapshot free of clean values by construction rather than by ordering.
    /// </summary>
    private CorrelationIdRecordingLoggerProvider _cleanRateLimitedLoggerProvider = default!;
    private WebApplicationFactory<Program> _factory = default!;
    private WebApplicationFactory<Program> _rateLimitedFactory = default!;
    private WebApplicationFactory<Program> _cleanRateLimitedFactory = default!;

    private HttpResponseMessage _notFoundResponse = default!;
    private HttpResponseMessage _unauthorizedResponse = default!;
    private HttpResponseMessage _forbiddenResponse = default!;
    private HttpResponseMessage _permittedHealthResponse = default!;
    private HttpResponseMessage _tooManyRequestsResponse = default!;

    private HttpResponseMessage _cleanNotFoundResponse = default!;
    private HttpResponseMessage _cleanUnauthorizedResponse = default!;
    private HttpResponseMessage _cleanForbiddenResponse = default!;
    private HttpResponseMessage _cleanPermittedHealthResponse = default!;
    private HttpResponseMessage _cleanTooManyRequestsResponse = default!;

    private JsonNode _notFoundBody = default!;
    private JsonNode _unauthorizedBody = default!;
    private JsonNode _forbiddenBody = default!;
    private JsonNode _tooManyRequestsBody = default!;

    private string[] _loggedTraceIds = [];
    private string[] _mainFactoryLoggedTraceIds = [];
    private string[] _rateLimitedLoggedTraceIds = [];

    [OneTimeSetUp]
    public async Task Setup()
    {
        _loggerProvider = new CorrelationIdRecordingLoggerProvider();
        _rateLimitedLoggerProvider = new CorrelationIdRecordingLoggerProvider();
        _factory = CreateFactory(_loggerProvider, rateLimited: false);
        _rateLimitedFactory = CreateFactory(_rateLimitedLoggerProvider, rateLimited: true);

        using HttpClient client = _factory.CreateClient();

        _notFoundResponse = await SendWithCorrelationId(
            client,
            "/no-such-route",
            HostileCorrelationId,
            token: null
        );
        _notFoundBody = await ReadBody(_notFoundResponse);

        _unauthorizedResponse = await SendWithCorrelationId(
            client,
            "/health/document-cache",
            HostileCorrelationId,
            token: null
        );
        _unauthorizedBody = await ReadBody(_unauthorizedResponse);

        _forbiddenResponse = await SendWithCorrelationId(
            client,
            "/health/document-cache",
            HostileCorrelationId,
            ValidBearerToken
        );
        _forbiddenBody = await ReadBody(_forbiddenResponse);

        using HttpClient rateLimitedClient = _rateLimitedFactory.CreateClient();

        // The first request consumes the single permit in the window; the second is rejected.
        // It carries the hostile ID as well, so every event captured before the snapshot
        // below belongs to a request that supplied that ID.
        _permittedHealthResponse = await SendWithCorrelationId(
            rateLimitedClient,
            "/health",
            HostileCorrelationId,
            token: null
        );

        // /health is the one request in this fixture whose body nothing else reads, and reading
        // it is what makes the snapshot below deterministic: TestServer returns as soon as the
        // response *starts*, while LoggingMiddleware writes its completion event only after
        // `await _next(context)` returns. Without this await the permitted request's event may
        // not be enqueued yet when the snapshot is taken.
        await _permittedHealthResponse.Content.ReadAsStringAsync();

        _tooManyRequestsResponse = await SendWithCorrelationId(
            rateLimitedClient,
            "/health",
            HostileCorrelationId,
            token: null
        );
        _tooManyRequestsBody = await ReadBody(_tooManyRequestsResponse);

        // Snapshotted after the rate-limited requests and before the clean control arm, so
        // the 429 path's log event is included and no clean value is.
        _mainFactoryLoggedTraceIds = _loggerProvider.LoggedTraceIds;
        _rateLimitedLoggedTraceIds = _rateLimitedLoggerProvider.LoggedTraceIds;
        _loggedTraceIds = [.. _mainFactoryLoggedTraceIds, .. _rateLimitedLoggedTraceIds];

        // The control arm: the identical requests with a clean correlation ID.
        _cleanNotFoundResponse = await SendWithCorrelationId(
            client,
            "/no-such-route",
            CleanCorrelationId,
            token: null
        );
        _cleanUnauthorizedResponse = await SendWithCorrelationId(
            client,
            "/health/document-cache",
            CleanCorrelationId,
            token: null
        );
        _cleanForbiddenResponse = await SendWithCorrelationId(
            client,
            "/health/document-cache",
            CleanCorrelationId,
            ValidBearerToken
        );

        // The control arm gets its own rate-limited host. Sharing the hostile arm's host would
        // make the control result depend on a single sixty-second window still being open after
        // two factory boots and five earlier requests: if that window rolled over, the clean
        // /health request would answer 200 and the differential assertion would fail for a
        // reason that has nothing to do with correlation IDs. With its own host the control arm
        // reproduces the 429 the same way the hostile arm did - permit, then rejection - inside
        // a window that opened moments earlier.
        _cleanRateLimitedLoggerProvider = new CorrelationIdRecordingLoggerProvider();
        _cleanRateLimitedFactory = CreateFactory(_cleanRateLimitedLoggerProvider, rateLimited: true);
        using HttpClient cleanRateLimitedClient = _cleanRateLimitedFactory.CreateClient();

        _cleanPermittedHealthResponse = await SendWithCorrelationId(
            cleanRateLimitedClient,
            "/health",
            CleanCorrelationId,
            token: null
        );
        await _cleanPermittedHealthResponse.Content.ReadAsStringAsync();

        _cleanTooManyRequestsResponse = await SendWithCorrelationId(
            cleanRateLimitedClient,
            "/health",
            CleanCorrelationId,
            token: null
        );
    }

    [OneTimeTearDown]
    public async Task Teardown()
    {
        _notFoundResponse.Dispose();
        _unauthorizedResponse.Dispose();
        _forbiddenResponse.Dispose();
        _permittedHealthResponse.Dispose();
        _tooManyRequestsResponse.Dispose();
        _cleanNotFoundResponse.Dispose();
        _cleanUnauthorizedResponse.Dispose();
        _cleanForbiddenResponse.Dispose();
        _cleanPermittedHealthResponse.Dispose();
        _cleanTooManyRequestsResponse.Dispose();
        await _factory.DisposeAsync();
        await _rateLimitedFactory.DisposeAsync();
        await _cleanRateLimitedFactory.DisposeAsync();
        _loggerProvider.Dispose();
        _rateLimitedLoggerProvider.Dispose();
        _cleanRateLimitedLoggerProvider.Dispose();
    }

    [Test]
    public void It_produces_the_status_codes_the_parity_assertions_depend_on()
    {
        // Pins which layer answered each request, so the parity assertions below are known to
        // span the four status codes this fixture claims to cover. The FR-LOG-5 claim that a
        // malformed ID does not change the outcome is asserted separately and differentially.
        _notFoundResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
        _unauthorizedResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _forbiddenResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _tooManyRequestsResponse.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Test]
    public void It_still_succeeds_when_the_request_would_have_succeeded()
    {
        // The other half of FR-LOG-5, and the only request in this fixture that is supposed to
        // succeed: a hostile correlation ID must not turn a 2xx into an error. Every other
        // assertion here is about a request that was going to fail anyway, so without this one
        // an implementation that rejected a malformed correlation ID outright would still be
        // consistent with the rest of the fixture.
        _permittedHealthResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        _permittedHealthResponse.StatusCode.Should().Be(_cleanPermittedHealthResponse.StatusCode);
    }

    [Test]
    public void It_answers_with_the_same_status_a_clean_correlation_id_receives()
    {
        // FR-LOG-5: the request still succeeds or fails on its own merits rather than on the
        // shape of an operational identifier. Each status is compared against the identical
        // request sent with a clean correlation ID, so a future change that rejected a
        // malformed ID would fail here whatever status it chose.
        _notFoundResponse.StatusCode.Should().Be(_cleanNotFoundResponse.StatusCode);
        _unauthorizedResponse.StatusCode.Should().Be(_cleanUnauthorizedResponse.StatusCode);
        _forbiddenResponse.StatusCode.Should().Be(_cleanForbiddenResponse.StatusCode);
        _tooManyRequestsResponse.StatusCode.Should().Be(_cleanTooManyRequestsResponse.StatusCode);
    }

    [Test]
    public void It_normalizes_the_correlation_id_in_the_map_fallback_404_body()
    {
        _notFoundBody["correlationId"]!.ToString().Should().Be(ExpectedCorrelationId);
    }

    [Test]
    public void It_normalizes_the_correlation_id_in_the_401_body()
    {
        _unauthorizedBody["correlationId"]!.ToString().Should().Be(ExpectedCorrelationId);
    }

    [Test]
    public void It_normalizes_the_correlation_id_in_the_403_body()
    {
        _forbiddenBody["correlationId"]!.ToString().Should().Be(ExpectedCorrelationId);
    }

    [Test]
    public void It_normalizes_the_correlation_id_in_the_429_body()
    {
        _tooManyRequestsBody["correlationId"]!.ToString().Should().Be(ExpectedCorrelationId);
    }

    [Test]
    public void It_logs_the_same_value_the_response_bodies_carry()
    {
        // The point of the correlation ID: what the client read is what an operator searches
        // for. Includes the rate-limited pipeline, whose log events are captured by their own
        // recording provider so a regression in the 429 writer cannot hide behind a
        // body-only assertion.
        //
        // Presence is asserted per arm and the value with OnlyContain, rather than pinning one
        // global total. A single expected total across both hosts encoded how many TraceId-
        // bearing log events the whole application happens to emit on these paths, so an
        // unrelated new log line failed this test with an arithmetic mismatch that named
        // nothing. The recorder is scoped to the request-logging event ids instead, and what
        // this fixture actually claims - every request-log event in the hostile arm carries the
        // normalized value, and both arms produced events - is asserted directly.
        _mainFactoryLoggedTraceIds.Should().NotBeEmpty();
        _rateLimitedLoggedTraceIds.Should().NotBeEmpty();
        _loggedTraceIds.Should().OnlyContain(traceId => traceId == ExpectedCorrelationId);
    }

    [Test]
    public void It_captures_the_log_event_the_rate_limited_pipeline_produced()
    {
        // The log arm of 429 parity is only real if the rate-limited pipeline's own events
        // were captured. This pins that wiring so the 429 case cannot quietly regress to a
        // body-only assertion by someone dropping the recording provider from that factory.
        // One event per request is what makes it prove the *rejected* request logged too:
        // non-emptiness alone was satisfied by the permitted request on its own.
        _rateLimitedLoggedTraceIds.Should().HaveCountGreaterThanOrEqualTo(RateLimitedArmRequestCount);
        _rateLimitedLoggedTraceIds.Should().OnlyContain(traceId => traceId == ExpectedCorrelationId);
    }

    [Test]
    public void It_never_lets_a_control_character_reach_a_response_body_or_a_log_event()
    {
        string[] emitted =
        [
            _notFoundBody["correlationId"]!.ToString(),
            _unauthorizedBody["correlationId"]!.ToString(),
            _forbiddenBody["correlationId"]!.ToString(),
            _tooManyRequestsBody["correlationId"]!.ToString(),
            .. _loggedTraceIds,
        ];

        emitted.Should().OnlyContain(value => !value.Any(char.IsControl));
    }

    [Test]
    public void It_keeps_printable_punctuation_that_the_stricter_allowlist_would_strip()
    {
        // Guards against the correlation ID being routed through SanitizeForLogging, which is
        // correct for Method and Path but violates FR-LOG-3 for a correlation ID.
        _notFoundBody["correlationId"]!.ToString().Should().Contain("{id}");
    }

    private static async Task<HttpResponseMessage> SendWithCorrelationId(
        HttpClient client,
        string path,
        string correlationId,
        string? token
    )
    {
        using HttpRequestMessage request = new(HttpMethod.Get, path);

        // TryAddWithoutValidation is required: HttpClient refuses to add a header value
        // containing control characters, which is exactly the value under test. TestServer
        // hands the headers to the pipeline without wire-level parsing, so the hostile value
        // reaches the correlation ID ingestion point intact.
        request.Headers.TryAddWithoutValidation(CorrelationHeader, correlationId);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await client.SendAsync(request);
    }

    private static async Task<JsonNode> ReadBody(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

    private static WebApplicationFactory<Program> CreateFactory(
        CorrelationIdRecordingLoggerProvider loggerProvider,
        bool rateLimited
    ) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            // The rate limiter is registered from the builder's configuration before
            // WebApplicationFactory's in-memory overrides are layered on, so the limit has to
            // come from a settings file. TestRateLimit permits one request per sixty-second
            // window, which keeps every request in this fixture inside a single window.
            builder.UseEnvironment(rateLimited ? "TestRateLimit" : "Test");
            builder.ConfigureLogging(logging => logging.AddProvider(loggerProvider));

            builder.ConfigureAppConfiguration(
                (_, configuration) =>
                {
                    Dictionary<string, string?> settings = new()
                    {
                        ["AppSettings:CorrelationIdHeader"] = CorrelationHeader,
                        ["AppSettings:CorrelationIdMaxLength"] = ConfiguredMaxLength.ToString(),
                        ["DataManagement:DocumentCache:Status:RequiredRole"] = ValidRequiredRole,
                        ["JwtAuthentication:RoleClaimType"] = RoleClaimType,
                        ["JwtAuthentication:ClientRole"] = "legacy-service",
                    };

                    configuration.AddInMemoryCollection(settings);
                }
            );

            builder.ConfigureServices(services =>
            {
                TestMockHelper.AddEssentialMocks(services);
                services.Replace(
                    ServiceDescriptor.Singleton<IJwtValidationService>(
                        // A token that validates but carries no matching role, so the
                        // document-cache endpoint answers 403 rather than 401.
                        new StubJwtValidationService(
                            ValidBearerToken,
                            new ClaimsPrincipal(
                                new ClaimsIdentity([new Claim(RoleClaimType, "some-other-role")], "test")
                            )
                        )
                    )
                );
                services.Replace(
                    ServiceDescriptor.Singleton<IDocumentCacheStatusService>(
                        new StubDocumentCacheStatusService()
                    )
                );
            });
        });

    private sealed class StubJwtValidationService(string expectedToken, ClaimsPrincipal principal)
        : IJwtValidationService
    {
        public Task<(
            ClaimsPrincipal? Principal,
            ClientAuthorizations? ClientAuthorizations
        )> ValidateAndExtractClientAuthorizationsAsync(string token, CancellationToken cancellationToken) =>
            Task.FromResult<(ClaimsPrincipal?, ClientAuthorizations?)>(
                (token == expectedToken ? principal : null, null)
            );
    }

    private sealed class StubDocumentCacheStatusService : IDocumentCacheStatusService
    {
        public Task<DocumentCacheStatusResponse> GetStatusAsync(
            CancellationToken cancellationToken = default,
            DocumentCacheStatusEvaluationMode evaluationMode =
                DocumentCacheStatusEvaluationMode.RuntimeEndpoint,
            TimeSpan? endpointTimeoutOverride = null
        ) => Task.FromResult(new DocumentCacheStatusResponse(DateTimeOffset.UnixEpoch, []));
    }
}

/// <summary>
/// The catch-all 404 used to read the request header named by a hardcoded
/// <c>"correlationid"</c> fallback whenever <c>AppSettings:CorrelationIdHeader</c> was empty,
/// so a host that had deliberately disabled client-supplied correlation IDs still had them
/// honored on unmatched routes. It now uses the same extraction as every other path.
/// </summary>
[TestFixture]
[NonParallelizable]
public class Given_An_Unmatched_Route_And_No_Configured_Correlation_Header
{
    private const string ClientSuppliedValue = "client-supplied-value";

    private CorrelationIdRecordingLoggerProvider _loggerProvider = default!;
    private WebApplicationFactory<Program> _factory = default!;
    private HttpResponseMessage _response = default!;
    private JsonNode _body = default!;
    private string[] _loggedTraceIds = [];

    [OneTimeSetUp]
    public async Task Setup()
    {
        _loggerProvider = new CorrelationIdRecordingLoggerProvider();

        // The Test environment inherits AppSettings:CorrelationIdHeader = "" from the base
        // appsettings.json, which is how a host disables client-supplied correlation IDs.
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureLogging(logging => logging.AddProvider(_loggerProvider));
            builder.ConfigureServices(TestMockHelper.AddEssentialMocks);
        });

        using HttpClient client = _factory.CreateClient();
        using HttpRequestMessage request = new(HttpMethod.Get, "/no-such-route");
        request.Headers.TryAddWithoutValidation("correlationid", ClientSuppliedValue);

        _response = await client.SendAsync(request);
        _body = JsonNode.Parse(await _response.Content.ReadAsStringAsync())!;
        _loggedTraceIds = _loggerProvider.LoggedTraceIds;
    }

    [OneTimeTearDown]
    public async Task Teardown()
    {
        _response.Dispose();
        await _factory.DisposeAsync();
        _loggerProvider.Dispose();
    }

    [Test]
    public void It_still_answers_with_the_not_found_failure_response_shape()
    {
        _response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        _body["type"]!.ToString().Should().Be("urn:ed-fi:api:not-found");
        _body["status"]!.GetValue<int>().Should().Be(404);
    }

    [Test]
    public void It_ignores_the_hardcoded_correlationid_header_name()
    {
        _body["correlationId"]!.ToString().Should().NotBe(ClientSuppliedValue);
    }

    [Test]
    public void It_uses_the_server_generated_trace_identifier_instead()
    {
        // Asserting against the TraceId the request-logging middleware actually emitted, not
        // merely against non-emptiness: a literal placeholder such as "unknown", or an
        // unrelated GUID, would satisfy non-emptiness while leaving the 404 uncorrelatable to
        // its log line. The framework's identifier format is deliberately not asserted.
        _loggedTraceIds.Should().NotBeEmpty();
        _loggedTraceIds.Should().OnlyContain(traceId => traceId == _body["correlationId"]!.ToString());
    }
}
