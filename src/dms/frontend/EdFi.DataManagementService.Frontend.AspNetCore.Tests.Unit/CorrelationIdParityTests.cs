// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
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
/// No database is required: every one of these responses is produced before the request
/// reaches a backend.
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

    private RecordingLoggerProvider _loggerProvider = null!;
    private WebApplicationFactory<Program> _factory = null!;
    private WebApplicationFactory<Program> _rateLimitedFactory = null!;

    private HttpResponseMessage _notFoundResponse = null!;
    private HttpResponseMessage _unauthorizedResponse = null!;
    private HttpResponseMessage _forbiddenResponse = null!;
    private HttpResponseMessage _tooManyRequestsResponse = null!;

    private JsonNode _notFoundBody = null!;
    private JsonNode _unauthorizedBody = null!;
    private JsonNode _forbiddenBody = null!;
    private JsonNode _tooManyRequestsBody = null!;

    private string[] _loggedTraceIds = [];

    [OneTimeSetUp]
    public async Task Setup()
    {
        _loggerProvider = new RecordingLoggerProvider();
        _factory = CreateFactory(_loggerProvider, rateLimited: false);
        _rateLimitedFactory = CreateFactory(loggerProvider: null, rateLimited: true);

        using HttpClient client = _factory.CreateClient();

        _notFoundResponse = await SendWithHostileCorrelationId(client, "/no-such-route", token: null);
        _notFoundBody = await ReadBody(_notFoundResponse);

        _unauthorizedResponse = await SendWithHostileCorrelationId(
            client,
            "/health/document-cache",
            token: null
        );
        _unauthorizedBody = await ReadBody(_unauthorizedResponse);

        _forbiddenResponse = await SendWithHostileCorrelationId(
            client,
            "/health/document-cache",
            ValidBearerToken
        );
        _forbiddenBody = await ReadBody(_forbiddenResponse);

        _loggedTraceIds = _loggerProvider.LoggedTraceIds;

        using HttpClient rateLimitedClient = _rateLimitedFactory.CreateClient();

        // The first request consumes the single permit in the window; the second is rejected.
        using HttpResponseMessage permitted = await rateLimitedClient.GetAsync("/health");

        _tooManyRequestsResponse = await SendWithHostileCorrelationId(
            rateLimitedClient,
            "/health",
            token: null
        );
        _tooManyRequestsBody = await ReadBody(_tooManyRequestsResponse);
    }

    [OneTimeTearDown]
    public async Task Teardown()
    {
        _notFoundResponse.Dispose();
        _unauthorizedResponse.Dispose();
        _forbiddenResponse.Dispose();
        _tooManyRequestsResponse.Dispose();
        await _factory.DisposeAsync();
        await _rateLimitedFactory.DisposeAsync();
        _loggerProvider.Dispose();
    }

    [Test]
    public void It_produces_the_status_codes_the_parity_assertions_depend_on()
    {
        // FR-LOG-5: a malformed correlation ID never changes the outcome of the request; each
        // of these is the status the same request would receive with a clean correlation ID.
        _notFoundResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
        _unauthorizedResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _forbiddenResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _tooManyRequestsResponse.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
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
        // The point of the correlation ID: what the client read is what an operator searches for.
        _loggedTraceIds.Should().NotBeEmpty();
        _loggedTraceIds.Should().OnlyContain(traceId => traceId == ExpectedCorrelationId);
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
        ExpectedCorrelationId.Should().Contain("{").And.Contain("}");
        _notFoundBody["correlationId"]!.ToString().Should().Contain("{id}");
    }

    private static async Task<HttpResponseMessage> SendWithHostileCorrelationId(
        HttpClient client,
        string path,
        string? token
    )
    {
        using HttpRequestMessage request = new(HttpMethod.Get, path);

        // TryAddWithoutValidation is required: HttpClient refuses to add a header value
        // containing control characters, which is exactly the value under test. TestServer
        // hands the headers to the pipeline without wire-level parsing, so the hostile value
        // reaches the correlation ID ingestion point intact.
        request.Headers.TryAddWithoutValidation(CorrelationHeader, HostileCorrelationId);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await client.SendAsync(request);
    }

    private static async Task<JsonNode> ReadBody(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

    private static WebApplicationFactory<Program> CreateFactory(
        RecordingLoggerProvider? loggerProvider,
        bool rateLimited
    ) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            // The rate limiter is registered from the builder's configuration before
            // WebApplicationFactory's in-memory overrides are layered on, so the limit has to
            // come from a settings file. TestRateLimit permits one request per sixty-second
            // window, which keeps every request in this fixture inside a single window.
            builder.UseEnvironment(rateLimited ? "TestRateLimit" : "Test");
            if (loggerProvider is not null)
            {
                builder.ConfigureLogging(logging => logging.AddProvider(loggerProvider));
            }

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

    /// <summary>
    /// Captures the TraceId that LoggingMiddleware writes into its request log events, which
    /// is the value an operator would search the logs for.
    /// </summary>
    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _traceIds = new();

        public string[] LoggedTraceIds => [.. _traceIds];

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(_traceIds);

        public void Dispose() { }

        private sealed class RecordingLogger(ConcurrentQueue<string> traceIds) : ILogger
        {
            public IDisposable BeginScope<TState>(TState state)
                where TState : notnull => NullScope.Instance;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter
            )
            {
                if (state is not IReadOnlyList<KeyValuePair<string, object?>> values)
                {
                    return;
                }

                foreach (KeyValuePair<string, object?> value in values)
                {
                    if (value.Key == "TraceId" && value.Value is string traceId)
                    {
                        traceIds.Enqueue(traceId);
                    }
                }
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose() { }
        }
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

    private WebApplicationFactory<Program> _factory = null!;
    private HttpResponseMessage _response = null!;
    private JsonNode _body = null!;

    [OneTimeSetUp]
    public async Task Setup()
    {
        // The Test environment inherits AppSettings:CorrelationIdHeader = "" from the base
        // appsettings.json, which is how a host disables client-supplied correlation IDs.
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(TestMockHelper.AddEssentialMocks);
        });

        using HttpClient client = _factory.CreateClient();
        using HttpRequestMessage request = new(HttpMethod.Get, "/no-such-route");
        request.Headers.TryAddWithoutValidation("correlationid", ClientSuppliedValue);

        _response = await client.SendAsync(request);
        _body = JsonNode.Parse(await _response.Content.ReadAsStringAsync())!;
    }

    [OneTimeTearDown]
    public async Task Teardown()
    {
        _response.Dispose();
        await _factory.DisposeAsync();
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
        _body["correlationId"]!.ToString().Should().NotBeNullOrEmpty();
    }
}
