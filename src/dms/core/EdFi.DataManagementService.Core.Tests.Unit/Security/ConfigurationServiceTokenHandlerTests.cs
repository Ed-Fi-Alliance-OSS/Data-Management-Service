// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.Profile;
using EdFi.DataManagementService.Core.Security;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Security;

public class ConfigurationServiceTokenHandlerTests
{
    /// <summary>No deprecated cap: reuse follows the issuer's lifetime.</summary>
    protected static IOptions<ConfigurationServiceTokenCacheOptions> CreateTokenCacheOptions() =>
        Options.Create(new ConfigurationServiceTokenCacheOptions());

    [TestFixture]
    [Parallelizable]
    public class Given_Valid_Credentials : ConfigurationServiceTokenHandlerTests
    {
        private ConfigurationServiceTokenHandler? _configServiceTokenHandler;
        private readonly string _expectedToken = "valid-token";
        private string? _token;
        private int _httpCallCount;

        [SetUp]
        public async Task Setup()
        {
            _httpCallCount = 0;
            var handler = new TestHttpMessageHandler(HttpStatusCode.OK, "");
            handler.SetResponse(
                "https://api.example.com/connect/token",
                new { Access_Token = "valid-token", Expires_in = 1800 }
            );
            handler.OnRequest = () => _httpCallCount++;

            var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.example.com") };
            var grantClient = new ConfigurationServiceGrantClient(httpClient);

            _configServiceTokenHandler = new ConfigurationServiceTokenHandler(
                grantClient,
                CreateTokenCacheOptions(),
                TimeProvider.System,
                NullLogger<ConfigurationServiceTokenHandler>.Instance
            );
            _token = (
                await _configServiceTokenHandler.GetTokenAsync("ClientId", "Secret", "Scope")
            ).AccessToken;
        }

        [Test]
        public void It_Should_Return_Valid_Token()
        {
            _token.Should().NotBeNullOrEmpty();
            _token.Should().Be(_expectedToken);
        }

        [Test]
        public void It_Should_Make_Http_Call_On_Cache_Miss()
        {
            _httpCallCount.Should().Be(1);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Subsequent_Request_After_Cache_Populated : ConfigurationServiceTokenHandlerTests
    {
        private ConfigurationServiceTokenHandler? _configServiceTokenHandler;
        private readonly string _expectedToken = "valid-token";
        private string? _firstToken;
        private string? _secondToken;
        private int _httpCallCount;

        [SetUp]
        public async Task Setup()
        {
            _httpCallCount = 0;
            var handler = new TestHttpMessageHandler(HttpStatusCode.OK, "");
            handler.SetResponse(
                "https://api.example.com/connect/token",
                new { Access_Token = "valid-token", Expires_in = 1800 }
            );
            handler.OnRequest = () => _httpCallCount++;

            var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.example.com") };
            var grantClient = new ConfigurationServiceGrantClient(httpClient);

            _configServiceTokenHandler = new ConfigurationServiceTokenHandler(
                grantClient,
                CreateTokenCacheOptions(),
                TimeProvider.System,
                NullLogger<ConfigurationServiceTokenHandler>.Instance
            );

            // First call - should fetch from HTTP
            _firstToken = (
                await _configServiceTokenHandler.GetTokenAsync("ClientId", "Secret", "Scope")
            ).AccessToken;

            // Second call - should return from cache
            _secondToken = (
                await _configServiceTokenHandler.GetTokenAsync("ClientId", "Secret", "Scope")
            ).AccessToken;
        }

        [Test]
        public void It_Should_Return_Same_Token_For_Both_Calls()
        {
            _firstToken.Should().Be(_expectedToken);
            _secondToken.Should().Be(_expectedToken);
        }

        [Test]
        public void It_Should_Make_Only_One_Http_Call()
        {
            // Second request should come from cache
            _httpCallCount.Should().Be(1);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Registered CMS pipelines. The fixtures below resolve the clients from Core DI and substitute
    // only primary transports, so redirect policy, logging policy and the response handler are
    // the production ones.
    // ---------------------------------------------------------------------------------------------

    internal const string CmsBaseAddress = "https://cms.example.com/";
    internal const string GrantClientName = "ConfigurationServiceGrantClient";
    internal const string ApiClientName = "ConfigurationServiceApiClient";
    internal const string GrantClientId = "dms-system-client";
    internal const string GrantScope = "edfi_admin_api/full_access";
    internal const string SecretSentinel = "sentinel-client-secret-6f1c0d";
    internal const string TokenSentinel = "sentinel-bearer-value-9a2e7b";
    internal const string TokenHandlerCategory =
        "EdFi.DataManagementService.Core.Security.ConfigurationServiceTokenHandler";

    internal static readonly string TokenShapedBody =
        $$"""{"access_token":"{{TokenSentinel}}","token_type":"bearer","expires_in":600,"client_secret":"{{SecretSentinel}}"}""";

    /// <summary>One request as a primary transport saw it.</summary>
    internal sealed record RecordedRequest(
        HttpMethod Method,
        Uri RequestUri,
        IReadOnlyDictionary<string, string[]> Headers,
        string? Body
    );

    /// <summary>A primary transport that records each request and answers from a script.</summary>
    internal sealed class ScriptedTransport(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond
    ) : HttpMessageHandler
    {
        public ConcurrentQueue<RecordedRequest> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            string? body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            Requests.Enqueue(
                new RecordedRequest(
                    request.Method,
                    request.RequestUri!,
                    request.Headers.ToDictionary(header => header.Key, header => header.Value.ToArray()),
                    body
                )
            );

            return await respond(request, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            // The factory may expire and dispose a handler chain; the script and its recordings
            // outlive that.
        }
    }

    /// <summary>One captured log event, rendered with its full exception chain.</summary>
    internal sealed record CapturedLog(string Category, LogLevel Level, string Message, string? Exception)
    {
        public string Rendered => $"{Category} [{Level}] {Message} {Exception}";
    }

    /// <summary>Captures every log event, Trace included, from every category.</summary>
    internal sealed class RecordingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<CapturedLog> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(categoryName, Entries);

        public string Rendered => string.Join(Environment.NewLine, Entries.Select(entry => entry.Rendered));

        public IReadOnlyList<CapturedLog> For(string category) =>
            Entries.Where(entry => entry.Category == category).ToList();

        public void Dispose() { }

        private sealed class RecordingLogger(string category, ConcurrentQueue<CapturedLog> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter
            ) =>
                entries.Enqueue(
                    new CapturedLog(category, logLevel, formatter(state, exception), exception?.ToString())
                );
        }
    }

    internal const string TokenCacheCapKey = "CacheSettings:TokenCacheExpirationSeconds";

    internal static IConfiguration BuildCmsConfiguration(string baseAddress, string? tokenCacheCap = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["ConfigurationServiceSettings:BaseUrl"] = baseAddress,
            ["ConfigurationServiceSettings:ClientId"] = GrantClientId,
            ["ConfigurationServiceSettings:ClientSecret"] = SecretSentinel,
            ["ConfigurationServiceSettings:Scope"] = GrantScope,
        };

        if (tokenCacheCap is not null)
        {
            settings[TokenCacheCapKey] = tokenCacheCap;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    /// <summary>
    /// Builds the shared Core CMS registration with Trace logging. When transports are supplied they
    /// replace only the primary handler of the named client.
    /// </summary>
    internal static ServiceProvider BuildRegisteredProvider(
        RecordingLoggerProvider logs,
        TimeProvider timeProvider,
        HttpMessageHandler? grantTransport = null,
        HttpMessageHandler? apiTransport = null,
        string baseAddress = CmsBaseAddress,
        string? tokenCacheCap = null,
        Action<IServiceCollection>? configureServices = null
    )
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        services.AddSingleton(timeProvider);
        services.AddDmsConfigurationServiceDataStoreProvider(
            BuildCmsConfiguration(baseAddress, tokenCacheCap)
        );
        configureServices?.Invoke(services);

        if (grantTransport is not null)
        {
            services.AddHttpClient(GrantClientName).ConfigurePrimaryHttpMessageHandler(() => grantTransport);
        }

        if (apiTransport is not null)
        {
            services.AddHttpClient(ApiClientName).ConfigurePrimaryHttpMessageHandler(() => apiTransport);
        }

        return services.BuildServiceProvider();
    }

    internal static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string body)
    {
        var response = new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        response.Headers.TryAddWithoutValidation("X-Echo", $"{SecretSentinel} {TokenSentinel}");
        return response;
    }

    internal static string ValidGrantBody(
        string accessToken = "registered-grant-token",
        int expiresIn = 600
    ) => $$"""{"access_token":"{{accessToken}}","token_type":"bearer","expires_in":{{expiresIn}}}""";

    /// <summary>A transport that answers every request with the same status and body.</summary>
    internal static ScriptedTransport Answering(HttpStatusCode statusCode, string body) =>
        new((_, _) => Task.FromResult(JsonResponse(statusCode, body)));

    /// <summary>Real-time failsafe so a regression hangs a single assertion, not the run.</summary>
    internal static readonly TimeSpan Failsafe = TimeSpan.FromSeconds(10);

    internal static async Task<Exception?> CaptureFailure(Func<Task> act)
    {
        try
        {
            await act().WaitAsync(Failsafe);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    internal static void AssertNoSentinels(string diagnostics)
    {
        diagnostics.Should().NotContain(SecretSentinel).And.NotContain(TokenSentinel);
    }

    internal static async Task<string> RequestToken(
        ServiceProvider provider,
        CancellationToken cancellationToken = default
    ) =>
        (
            await provider
                .GetRequiredService<IConfigurationServiceTokenHandler>()
                .GetTokenAsync(GrantClientId, SecretSentinel, GrantScope, cancellationToken)
        ).AccessToken;

    /// <summary>
    /// Unwraps a registered client's handler chain down to its primary transport.
    /// </summary>
    internal static HttpMessageHandler PrimaryHandlerOf(ServiceProvider provider, string clientName)
    {
        HttpMessageHandler handler = provider
            .GetRequiredService<IHttpMessageHandlerFactory>()
            .CreateHandler(clientName);

        while (handler is DelegatingHandler delegating)
        {
            handler = delegating.InnerHandler!;
        }

        return handler;
    }

    /// <summary>
    /// The registered grant transport must not follow a redirect, and the protected read transport
    /// keeps its existing redirect setting.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_The_Registered_Cms_Transports : ConfigurationServiceTokenHandlerTests
    {
        private ServiceProvider _provider = null!;

        [SetUp]
        public void Setup()
        {
            _provider = BuildRegisteredProvider(new RecordingLoggerProvider(), TimeProvider.System);
        }

        [TearDown]
        public void TearDown() => _provider.Dispose();

        [Test]
        public void It_does_not_follow_redirects_on_the_grant_transport()
        {
            HttpMessageHandler primary = PrimaryHandlerOf(_provider, GrantClientName);

            bool allowsRedirect = primary switch
            {
                SocketsHttpHandler sockets => sockets.AllowAutoRedirect,
                HttpClientHandler client => client.AllowAutoRedirect,
                _ => true,
            };

            allowsRedirect.Should().BeFalse();
        }

        [Test]
        public void It_leaves_the_grant_timeout_to_the_token_handler_budget() =>
            _provider
                .GetRequiredService<IHttpClientFactory>()
                .CreateClient(GrantClientName)
                .Timeout.Should()
                .Be(Timeout.InfiniteTimeSpan);

        [Test]
        public void It_keeps_the_protected_read_redirect_setting()
        {
            PrimaryHandlerOf(_provider, ApiClientName)
                .Should()
                .BeOfType<HttpClientHandler>()
                .Which.AllowAutoRedirect.Should()
                .BeTrue();
        }
    }

    /// <summary>
    /// A grant through the registered clients posts the existing credential form to the token route
    /// on the grant transport, without a Tenant header, and keeps the credentials out of diagnostics.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_A_Successful_Grant_Through_The_Registered_Clients
        : ConfigurationServiceTokenHandlerTests
    {
        private ServiceProvider _provider = null!;
        private ScriptedTransport _grantTransport = null!;
        private ScriptedTransport _apiTransport = null!;
        private RecordingLoggerProvider _logs = null!;
        private string _token = null!;

        [SetUp]
        public async Task Setup()
        {
            _logs = new RecordingLoggerProvider();
            _grantTransport = Answering(HttpStatusCode.OK, ValidGrantBody());
            _apiTransport = Answering(HttpStatusCode.OK, ValidGrantBody());
            _provider = BuildRegisteredProvider(_logs, TimeProvider.System, _grantTransport, _apiTransport);

            _token = await RequestToken(_provider).WaitAsync(Failsafe);
        }

        [TearDown]
        public void TearDown() => _provider.Dispose();

        [Test]
        public void It_returns_the_bearer() => _token.Should().Be("registered-grant-token");

        [Test]
        public void It_posts_once_to_the_token_route_on_the_grant_transport()
        {
            _grantTransport.Requests.Should().ContainSingle();
            RecordedRequest grant = _grantTransport.Requests.Single();
            grant.Method.Should().Be(HttpMethod.Post);
            grant.RequestUri.Should().Be(new Uri("https://cms.example.com/connect/token"));
        }

        [Test]
        public void It_sends_the_existing_client_credentials_form()
        {
            string expectedForm = new FormUrlEncodedContent([
                new KeyValuePair<string, string>("client_id", GrantClientId),
                new KeyValuePair<string, string>("client_secret", SecretSentinel),
                new KeyValuePair<string, string>("grant_type", "client_credentials"),
                new KeyValuePair<string, string>("scope", GrantScope),
            ])
                .ReadAsStringAsync()
                .GetAwaiter()
                .GetResult();

            _grantTransport.Requests.Single().Body.Should().Be(expectedForm);
        }

        [Test]
        public void It_omits_the_tenant_header()
        {
            _grantTransport.Requests.Single().Headers.Keys.Should().NotContain("Tenant");
        }

        [Test]
        public void It_keeps_the_existing_accept_headers()
        {
            _grantTransport
                .Requests.Single()
                .Headers["Accept"]
                .Should()
                .Equal("application/json", "application/x-www-form-urlencoded");
        }

        [Test]
        public void It_sends_nothing_through_the_protected_read_transport()
        {
            _apiTransport.Requests.Should().BeEmpty();
        }

        [Test]
        public void It_keeps_the_credential_out_of_diagnostics()
        {
            _logs.Rendered.Should().NotContain(SecretSentinel);
        }
    }

    /// <summary>
    /// Every non-2xx grant, including a 3xx and a token-shaped body, fails with its numeric status,
    /// logs that status once at the grant boundary, and discloses nothing it was sent or shown.
    /// </summary>
    [TestFixture(HttpStatusCode.Found)]
    [TestFixture(HttpStatusCode.TemporaryRedirect)]
    [TestFixture(HttpStatusCode.BadRequest)]
    [TestFixture(HttpStatusCode.Unauthorized)]
    [TestFixture(HttpStatusCode.Forbidden)]
    [TestFixture(HttpStatusCode.NotFound)]
    [TestFixture(HttpStatusCode.Conflict)]
    [TestFixture(HttpStatusCode.TooManyRequests)]
    [TestFixture(HttpStatusCode.InternalServerError)]
    [TestFixture(HttpStatusCode.ServiceUnavailable)]
    [Parallelizable]
    public class Given_A_Failed_Grant_Status(HttpStatusCode statusCode)
        : ConfigurationServiceTokenHandlerTests
    {
        private ServiceProvider _provider = null!;
        private RecordingLoggerProvider _logs = null!;
        private Exception? _failure;

        [SetUp]
        public async Task Setup()
        {
            _logs = new RecordingLoggerProvider();
            ScriptedTransport transport = Answering(statusCode, TokenShapedBody);
            _provider = BuildRegisteredProvider(_logs, new FakeTimeProvider(), transport, transport);

            _failure = await CaptureFailure(() => RequestToken(_provider));
        }

        [TearDown]
        public void TearDown() => _provider.Dispose();

        [Test]
        public void It_rejects_with_the_numeric_status()
        {
            _failure.Should().BeOfType<HttpRequestException>().Which.StatusCode.Should().Be(statusCode);
        }

        [Test]
        public void It_names_the_numeric_status_in_the_message()
        {
            _failure!.Message.Should().Contain(((int)statusCode).ToString());
        }

        [Test]
        public void It_logs_the_status_once_at_the_grant_boundary()
        {
            _logs
                .For(TokenHandlerCategory)
                .Where(entry => entry.Message.Contains($"status code {(int)statusCode}"))
                .Should()
                .ContainSingle();
        }

        [Test]
        public void It_keeps_secrets_out_of_logs_and_the_exception_chain()
        {
            AssertNoSentinels(_logs.Rendered);
            AssertNoSentinels(_failure!.ToString());
        }
    }

    /// <summary>
    /// The safe problem-type allowlist. Allowlisted types are emitted exactly, any other string
    /// type as a fixed marker, and an absent, non-string or unreadable type contributes nothing.
    /// </summary>
    [TestFixture(
        """{"type":"about:blank","detail":"SECRET"}""",
        "about:blank",
        HttpStatusCode.TooManyRequests
    )]
    [TestFixture(
        """{"type":"urn:ed-fi:api:security:authentication:too-many-tokens","detail":"SECRET"}""",
        "urn:ed-fi:api:security:authentication:too-many-tokens",
        HttpStatusCode.TooManyRequests
    )]
    [TestFixture(
        """{"type":"urn:ed-fi:management-api:security:authentication:too-many-tokens","detail":"SECRET"}""",
        "urn:ed-fi:management-api:security:authentication:too-many-tokens",
        HttpStatusCode.TooManyRequests
    )]
    [TestFixture(
        """{"type":"urn:ed-fi:api:internal-server-error","detail":"SECRET TOKEN"}""",
        "urn:ed-fi:api:internal-server-error",
        HttpStatusCode.InternalServerError
    )]
    [TestFixture(
        """{"type":"urn:ed-fi:management-api:internal-server-error","detail":"SECRET TOKEN"}""",
        "urn:ed-fi:management-api:internal-server-error",
        HttpStatusCode.InternalServerError
    )]
    [TestFixture(
        """{"type":"urn:example:identity-provider-unavailable"}""",
        "unrecognized",
        HttpStatusCode.TooManyRequests
    )]
    [TestFixture(
        """{"type":"urn:example:TOKEN:SECRET","detail":"SECRET"}""",
        "unrecognized",
        HttpStatusCode.TooManyRequests
    )]
    [TestFixture(
        """{"type":"URN:ED-FI:API:SECURITY:AUTHENTICATION:TOO-MANY-TOKENS"}""",
        "unrecognized",
        HttpStatusCode.TooManyRequests
    )]
    [TestFixture("""{"title":"TOKEN","detail":"SECRET"}""", null, HttpStatusCode.TooManyRequests)]
    [TestFixture("""{"type":42,"detail":"SECRET"}""", null, HttpStatusCode.TooManyRequests)]
    [TestFixture("""{"type":{"value":"SECRET"},"detail":"TOKEN"}""", null, HttpStatusCode.TooManyRequests)]
    [TestFixture(
        """{"error":"invalid_client","error_description":"SECRET TOKEN"}""",
        null,
        HttpStatusCode.TooManyRequests
    )]
    [TestFixture("""not json SECRET TOKEN""", null, HttpStatusCode.TooManyRequests)]
    [Parallelizable]
    public class Given_A_Problem_Type_In_A_Failed_Grant(
        string bodyTemplate,
        string? expectedType,
        HttpStatusCode statusCode
    ) : ConfigurationServiceTokenHandlerTests
    {
        private ServiceProvider _provider = null!;
        private RecordingLoggerProvider _logs = null!;
        private Exception? _failure;
        private Exception? _cooldownFailure;

        [SetUp]
        public async Task Setup()
        {
            string body = bodyTemplate.Replace("SECRET", SecretSentinel).Replace("TOKEN", TokenSentinel);
            _logs = new RecordingLoggerProvider();
            ScriptedTransport transport = Answering(statusCode, body);
            _provider = BuildRegisteredProvider(_logs, new FakeTimeProvider(), transport, transport);

            _failure = await CaptureFailure(() => RequestToken(_provider));

            // The fake clock has not moved, so this caller is answered from the failure window.
            _cooldownFailure = await CaptureFailure(() => RequestToken(_provider));
        }

        [TearDown]
        public void TearDown() => _provider.Dispose();

        [Test]
        public void It_keeps_the_numeric_status()
        {
            _failure.Should().BeOfType<HttpRequestException>().Which.StatusCode.Should().Be(statusCode);
        }

        [Test]
        public void It_emits_only_the_safe_type_representation()
        {
            string grantBoundaryLog = string.Join(
                Environment.NewLine,
                _logs.For(TokenHandlerCategory).Select(entry => entry.Message)
            );

            if (expectedType is null)
            {
                grantBoundaryLog
                    .Should()
                    .Contain($"status code {(int)statusCode}")
                    .And.NotContain("problem type");
            }
            else
            {
                grantBoundaryLog
                    .Should()
                    .Contain($"status code {(int)statusCode} and problem type {expectedType}");
            }
        }

        [Test]
        public void It_carries_the_same_safe_type_in_the_initial_and_cooldown_exceptions()
        {
            foreach (Exception? failure in new[] { _failure, _cooldownFailure })
            {
                HttpRequestException rejected = failure.Should().BeOfType<HttpRequestException>().Which;
                rejected.StatusCode.Should().Be(statusCode);

                if (expectedType is null)
                {
                    rejected
                        .Message.Should()
                        .Contain($"status code {(int)statusCode}")
                        .And.NotContain("problem type");
                }
                else
                {
                    rejected
                        .Message.Should()
                        .Contain($"status code {(int)statusCode} and problem type {expectedType}");
                }
            }
        }

        [Test]
        public void It_keeps_secrets_out_of_logs_and_the_exception_chain()
        {
            AssertNoSentinels(_logs.Rendered);
            AssertNoSentinels(_failure!.ToString());
            AssertNoSentinels(_cooldownFailure!.ToString());
        }
    }

    /// <summary>A response body that signals its first read and then never produces data.</summary>
    internal sealed class StalledStream(TaskCompletionSource readStarted) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            readStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken
        ) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A response body whose read fails with text that names the sentinels.</summary>
    internal sealed class ThrowingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        ) => throw new IOException($"connection reset while echoing {SecretSentinel} {TokenSentinel}");

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken
        ) => throw new IOException($"connection reset while echoing {SecretSentinel} {TokenSentinel}");

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new IOException($"connection reset while echoing {SecretSentinel} {TokenSentinel}");

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    internal static ScriptedTransport AnsweringWithBody(HttpStatusCode statusCode, Stream body) =>
        new(
            (_, _) =>
            {
                var response = new HttpResponseMessage(statusCode) { Content = new StreamContent(body) };
                response.Content.Headers.ContentType = new("application/problem+json");
                return Task.FromResult(response);
            }
        );

    /// <summary>
    /// A stalled 429 body is abandoned at the one-second diagnostic deadline, measured by the
    /// injected TimeProvider, and the already-observed status survives.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_A_Stalled_Failed_Grant_Body : ConfigurationServiceTokenHandlerTests
    {
        private ServiceProvider _provider = null!;
        private RecordingLoggerProvider _logs = null!;
        private bool _completedBeforeDeadline;
        private Exception? _failure;

        [SetUp]
        public async Task Setup()
        {
            var fakeTime = new FakeTimeProvider();
            var readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _logs = new RecordingLoggerProvider();
            ScriptedTransport transport = AnsweringWithBody(
                HttpStatusCode.TooManyRequests,
                new StalledStream(readStarted)
            );
            _provider = BuildRegisteredProvider(_logs, fakeTime, transport, transport);

            Task<string> call = RequestToken(_provider);
            await readStarted.Task.WaitAsync(Failsafe);

            fakeTime.Advance(TimeSpan.FromMilliseconds(999));
            _completedBeforeDeadline = call.IsCompleted;

            fakeTime.Advance(TimeSpan.FromMilliseconds(1));
            _failure = await CaptureFailure(() => call);
        }

        [TearDown]
        public void TearDown() => _provider.Dispose();

        [Test]
        public void It_waits_until_the_diagnostic_deadline() => _completedBeforeDeadline.Should().BeFalse();

        [Test]
        public void It_keeps_the_numeric_status()
        {
            _failure
                .Should()
                .BeOfType<HttpRequestException>()
                .Which.StatusCode.Should()
                .Be(HttpStatusCode.TooManyRequests);
        }

        [Test]
        public void It_logs_the_status_without_a_type()
        {
            _logs
                .For(TokenHandlerCategory)
                .Should()
                .ContainSingle(entry => entry.Message.Contains("status code 429"))
                .Which.Message.Should()
                .NotContain("problem type");
        }
    }

    /// <summary>A failed grant whose body read throws keeps its status and hides the read error.</summary>
    [TestFixture]
    [Parallelizable]
    public class Given_An_Unreadable_Failed_Grant_Body : ConfigurationServiceTokenHandlerTests
    {
        private ServiceProvider _provider = null!;
        private RecordingLoggerProvider _logs = null!;
        private Exception? _failure;

        [SetUp]
        public async Task Setup()
        {
            _logs = new RecordingLoggerProvider();
            ScriptedTransport transport = AnsweringWithBody(
                HttpStatusCode.TooManyRequests,
                new ThrowingStream()
            );
            _provider = BuildRegisteredProvider(_logs, new FakeTimeProvider(), transport, transport);

            _failure = await CaptureFailure(() => RequestToken(_provider));
        }

        [TearDown]
        public void TearDown() => _provider.Dispose();

        [Test]
        public void It_keeps_the_numeric_status()
        {
            _failure
                .Should()
                .BeOfType<HttpRequestException>()
                .Which.StatusCode.Should()
                .Be(HttpStatusCode.TooManyRequests);
        }

        [Test]
        public void It_keeps_the_read_error_out_of_diagnostics()
        {
            AssertNoSentinels(_logs.Rendered);
            AssertNoSentinels(_failure!.ToString());
        }
    }

    /// <summary>
    /// Diagnostics read at most 16 KiB of a failed grant body. A type beyond that limit is not
    /// reported, and the status survives.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_An_Oversized_Failed_Grant_Body : ConfigurationServiceTokenHandlerTests
    {
        private ServiceProvider _provider = null!;
        private RecordingLoggerProvider _logs = null!;
        private Exception? _failure;

        [SetUp]
        public async Task Setup()
        {
            string body =
                $$"""{"padding":"{{new string('a', 20 * 1024)}}","type":"about:blank","detail":"{{SecretSentinel}}"}""";
            _logs = new RecordingLoggerProvider();
            ScriptedTransport transport = Answering(HttpStatusCode.TooManyRequests, body);
            _provider = BuildRegisteredProvider(_logs, new FakeTimeProvider(), transport, transport);

            _failure = await CaptureFailure(() => RequestToken(_provider));
        }

        [TearDown]
        public void TearDown() => _provider.Dispose();

        [Test]
        public void It_keeps_the_numeric_status()
        {
            _failure
                .Should()
                .BeOfType<HttpRequestException>()
                .Which.StatusCode.Should()
                .Be(HttpStatusCode.TooManyRequests);
        }

        [Test]
        public void It_reports_no_type()
        {
            _logs
                .For(TokenHandlerCategory)
                .Should()
                .ContainSingle(entry => entry.Message.Contains("status code 429"))
                .Which.Message.Should()
                .NotContain("problem type");
        }
    }

    /// <summary>
    /// A grant whose success body never arrives is bounded by the existing 100-second HTTP budget,
    /// which covers the body read as well as the headers, and fails without a secret.
    /// </summary>
    [TestFixture(false)]
    [TestFixture(true)]
    [Parallelizable]
    public class Given_A_Stalled_Grant_Success_Body(bool duringDrain) : ConfigurationServiceTokenHandlerTests
    {
        private ServiceProvider _provider = null!;
        private RecordingLoggerProvider _logs = null!;
        private bool _completedBeforeBudget;
        private Exception? _failure;

        [SetUp]
        public async Task Setup()
        {
            var fakeTime = new FakeTimeProvider();
            IHostApplicationLifetime lifetime = A.Fake<IHostApplicationLifetime>();
            A.CallTo(() => lifetime.ApplicationStopping).Returns(new CancellationToken(duringDrain));
            A.CallTo(() => lifetime.ApplicationStopped).Returns(CancellationToken.None);
            var readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _logs = new RecordingLoggerProvider();
            ScriptedTransport transport = AnsweringWithBody(
                HttpStatusCode.OK,
                new StalledStream(readStarted)
            );
            _provider = BuildRegisteredProvider(
                _logs,
                fakeTime,
                transport,
                transport,
                configureServices: services => services.AddSingleton(lifetime)
            );

            Task<string> call = RequestToken(_provider);
            await readStarted.Task.WaitAsync(Failsafe);

            fakeTime.Advance(TimeSpan.FromSeconds(99));
            _completedBeforeBudget = call.IsCompleted;

            fakeTime.Advance(TimeSpan.FromSeconds(1));
            _failure = await CaptureFailure(() => call);
        }

        [TearDown]
        public void TearDown() => _provider.Dispose();

        [Test]
        public void It_waits_for_the_grant_budget() => _completedBeforeBudget.Should().BeFalse();

        [Test]
        public void It_fails_with_a_request_failure() => _failure.Should().BeOfType<HttpRequestException>();

        [Test]
        public void It_keeps_secrets_out_of_diagnostics()
        {
            AssertNoSentinels(_logs.Rendered);
            AssertNoSentinels(_failure!.ToString());
        }
    }

    /// <summary>
    /// A transport failure, whose text may carry anything, surfaces as a fixed request failure.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_A_Grant_Transport_Failure : ConfigurationServiceTokenHandlerTests
    {
        private ServiceProvider _provider = null!;
        private RecordingLoggerProvider _logs = null!;
        private Exception? _failure;

        [SetUp]
        public async Task Setup()
        {
            _logs = new RecordingLoggerProvider();
            var transport = new ScriptedTransport(
                (_, _) =>
                    throw new HttpRequestException(
                        HttpRequestError.ConnectionError,
                        $"connect failed echoing {SecretSentinel} {TokenSentinel}"
                    )
            );
            _provider = BuildRegisteredProvider(_logs, TimeProvider.System, transport, transport);

            _failure = await CaptureFailure(() => RequestToken(_provider));
        }

        [TearDown]
        public void TearDown() => _provider.Dispose();

        [Test]
        public void It_fails_with_a_request_failure_keeping_the_error_category()
        {
            _failure
                .Should()
                .BeOfType<HttpRequestException>()
                .Which.HttpRequestError.Should()
                .Be(HttpRequestError.ConnectionError);
        }

        [Test]
        public void It_keeps_the_transport_text_out_of_diagnostics()
        {
            AssertNoSentinels(_logs.Rendered);
            AssertNoSentinels(_failure!.ToString());
        }
    }

    /// <summary>
    /// A real socket parser can include an invalid response header name in its exception. Exercise
    /// both registered transports with an echoed secret and bearer, capturing every log category.
    /// </summary>
    [TestFixture(true)]
    [TestFixture(false)]
    [NonParallelizable]
    public class Given_A_Malformed_Cms_Response_Header(bool duringGrant)
        : ConfigurationServiceTokenHandlerTests
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private ServiceProvider _provider = null!;
        private RecordingLoggerProvider _logs = null!;
        private Exception? _failure;
        private string _receivedRequest = string.Empty;

        [SetUp]
        public async Task Setup()
        {
            _listener.Start();
            int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Task<string> server = ReplyWithMalformedHeaderAsync();
            _logs = new RecordingLoggerProvider();
            _provider = BuildRegisteredProvider(
                _logs,
                TimeProvider.System,
                grantTransport: duringGrant
                    ? null
                    : Answering(HttpStatusCode.OK, ValidGrantBody(TokenSentinel)),
                baseAddress: $"http://127.0.0.1:{port}/"
            );

            _failure = await CaptureFailure(async () =>
            {
                if (duringGrant)
                {
                    await RequestToken(_provider);
                }
                else
                {
                    var services = new ConfigurationServiceAuthenticatedReadTests.ReadServices(_provider);
                    await services.Profiles.GetProfilesAsync(null);
                }
            });
            _receivedRequest = await server.WaitAsync(Failsafe);
        }

        private async Task<string> ReplyWithMalformedHeaderAsync()
        {
            // A failsafe only; the request and complete response determine ordering.
            using var deadline = new CancellationTokenSource(Failsafe);
            using TcpClient client = await _listener.AcceptTcpClientAsync(deadline.Token);
            await using NetworkStream stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            var request = new StringBuilder();
            int contentLength = 0;

            while (true)
            {
                string line =
                    await reader.ReadLineAsync(deadline.Token)
                    ?? throw new IOException("The CMS request ended before its headers.");
                request.AppendLine(line);
                if (line.Length == 0)
                {
                    break;
                }

                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    contentLength = int.Parse(line["Content-Length:".Length..], CultureInfo.InvariantCulture);
                }
            }

            // StreamReader can refill its buffer even for an empty read. A bodyless GET's
            // client is already waiting for our response, so do not read beyond its headers.
            if (contentLength > 0)
            {
                char[] body = new char[contentLength];
                int read = await reader.ReadBlockAsync(body.AsMemory(), deadline.Token);
                request.Append(body, 0, read);
            }
            byte[] response = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\n{SecretSentinel} {TokenSentinel}: echoed\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
            );
            await stream.WriteAsync(response, deadline.Token);
            return request.ToString();
        }

        [TearDown]
        public void TearDown()
        {
            _listener.Stop();
            _provider?.Dispose();
        }

        [Test]
        public void It_exercises_the_real_transport_with_the_credential_or_bearer() =>
            _receivedRequest.Should().Contain(duringGrant ? SecretSentinel : $"Bearer {TokenSentinel}");

        [Test]
        public void It_reports_a_dependency_failure()
        {
            if (duringGrant)
            {
                _failure.Should().BeOfType<HttpRequestException>();
            }
            else
            {
                _failure.Should().BeOfType<ProfileDataUnavailableException>();
            }
        }

        [Test]
        public void It_keeps_echoed_secrets_out_of_every_log_and_the_exception_chain()
        {
            _logs.Entries.Should().Contain(entry => entry.Level == LogLevel.Error);
            AssertNoSentinels(_logs.Rendered);
            AssertNoSentinels(_failure!.ToString());
        }
    }

    /// <summary>
    /// Header failures and failures while HttpClient buffers a successful read must be sanitized
    /// before a provider logs or wraps them. Neither path is a protected 401 or starts recovery.
    /// </summary>
    [TestFixture(false, HttpRequestError.InvalidResponse)]
    [TestFixture(false, HttpRequestError.NameResolutionError)]
    [TestFixture(false, HttpRequestError.ConnectionError)]
    [TestFixture(false, HttpRequestError.SecureConnectionError)]
    [TestFixture(true, HttpRequestError.Unknown)]
    [Parallelizable]
    public class Given_A_Protected_Read_Transport_Failure(bool duringBody, HttpRequestError error)
        : ConfigurationServiceTokenHandlerTests
    {
        private ServiceProvider _provider = null!;
        private RecordingLoggerProvider _logs = null!;
        private ScriptedTransport _grants = null!;
        private ScriptedTransport _reads = null!;
        private Exception? _failure;

        [SetUp]
        public async Task Setup()
        {
            _logs = new RecordingLoggerProvider();
            _grants = Answering(HttpStatusCode.OK, ValidGrantBody(TokenSentinel));
            _reads = duringBody
                ? AnsweringWithBody(HttpStatusCode.OK, new ThrowingStream())
                : new ScriptedTransport(
                    (_, _) =>
                        throw new HttpRequestException(
                            error,
                            $"invalid response header {SecretSentinel} {TokenSentinel}"
                        )
                );
            _provider = BuildRegisteredProvider(_logs, TimeProvider.System, _grants, _reads);
            var services = new ConfigurationServiceAuthenticatedReadTests.ReadServices(_provider);
            _failure = await CaptureFailure(() => services.Profiles.GetProfilesAsync(null));
        }

        [TearDown]
        public void TearDown() => _provider.Dispose();

        [Test]
        public void It_preserves_the_provider_failure_without_inventing_a_401()
        {
            ProfileDataUnavailableException failure = _failure
                .Should()
                .BeOfType<ProfileDataUnavailableException>()
                .Which;
            failure
                .InnerException.Should()
                .BeOfType<HttpRequestException>()
                .Which.StatusCode.Should()
                .BeNull();
            _grants.Requests.Should().ContainSingle();
            _reads.Requests.Should().ContainSingle();
        }

        [Test]
        public void It_keeps_transport_text_out_of_logs_and_the_exception_chain()
        {
            AssertNoSentinels(_logs.Rendered);
            AssertNoSentinels(_failure!.ToString());
        }

        [Test]
        public void It_preserves_the_safe_transport_category_in_provider_diagnostics()
        {
            HttpRequestException failure = _failure!
                .InnerException.Should()
                .BeOfType<HttpRequestException>()
                .Which;
            failure.HttpRequestError.Should().Be(error);
            failure.Message.Should().Contain($"({error})");
            failure.InnerException.Should().BeNull();
            _logs.Rendered.Should().Contain($"({error})");
        }
    }

    /// <summary>Cancellation stays cancellation, without exposing an upstream exception chain.</summary>
    [TestFixture]
    [Parallelizable]
    public class Given_A_Canceled_Protected_Read : ConfigurationServiceTokenHandlerTests
    {
        private ServiceProvider _provider = null!;
        private CancellationTokenSource _cancellation = null!;
        private RecordingLoggerProvider _logs = null!;
        private ScriptedTransport _grants = null!;
        private ScriptedTransport _reads = null!;
        private Exception? _failure;

        [SetUp]
        public async Task Setup()
        {
            _cancellation = new CancellationTokenSource();
            _logs = new RecordingLoggerProvider();
            _grants = Answering(HttpStatusCode.OK, ValidGrantBody(TokenSentinel));
            _reads = new ScriptedTransport(
                (_, cancellationToken) =>
                {
                    _cancellation.Cancel();
                    throw new TaskCanceledException(
                        $"canceled while echoing {SecretSentinel}",
                        new HttpRequestException(TokenSentinel),
                        cancellationToken
                    );
                }
            );
            _provider = BuildRegisteredProvider(_logs, TimeProvider.System, _grants, _reads);
            _failure = await CaptureFailure(async () =>
            {
                using HttpResponseMessage response = await _provider
                    .GetRequiredService<ConfigurationServiceApiClient>()
                    .SendAuthenticatedAsync(
                        _provider.GetRequiredService<IConfigurationServiceTokenHandler>(),
                        _provider.GetRequiredService<ConfigurationServiceContext>(),
                        () => new HttpRequestMessage(HttpMethod.Get, "v3/profiles"),
                        _cancellation.Token
                    );
            });
        }

        [TearDown]
        public void TearDown()
        {
            _provider.Dispose();
            _cancellation.Dispose();
        }

        [Test]
        public void It_preserves_cancellation_and_does_not_recover()
        {
            _failure
                .Should()
                .BeOfType<TaskCanceledException>()
                .Which.CancellationToken.Should()
                .Be(_cancellation.Token);
            _grants.Requests.Should().ContainSingle();
            _reads.Requests.Should().ContainSingle();
        }

        [Test]
        public void It_keeps_transport_text_out_of_diagnostics()
        {
            AssertNoSentinels(_logs.Rendered);
            AssertNoSentinels(_failure!.ToString());
        }
    }

    /// <summary>
    /// A 2xx grant that cannot yield a usable bearer and lifetime fails safely and is not cached:
    /// the next admitted grant posts again.
    /// </summary>
    [TestFixture("not json SECRET TOKEN")]
    [TestFixture("[]")]
    [TestFixture("{}")]
    [TestFixture("""{"access_token":""}""")]
    [TestFixture("""{"access_token":null}""")]
    [TestFixture("""{"access_token":12345}""")]
    [TestFixture("""{"access_token":"TOKEN\r\nX-Injected: SECRET"}""")]
    [TestFixture("""{"access_token":"TOKEN with spaces"}""")]
    [TestFixture("""{"access_token":"TOKEN","expires_in":"soon SECRET"}""")]
    [TestFixture("""{"access_token":"TOKEN","expires_in":true}""")]
    [TestFixture("""{"access_token":"TOKEN","expires_in":{}}""")]
    [TestFixture("""{"access_token":"TOKEN","expires_in":1e400}""")]
    [Parallelizable]
    public class Given_A_Malformed_Grant_Success(string bodyTemplate) : ConfigurationServiceTokenHandlerTests
    {
        private ServiceProvider _provider = null!;
        private RecordingLoggerProvider _logs = null!;
        private ScriptedTransport _transport = null!;
        private Exception? _failure;
        private string? _nextToken;

        [SetUp]
        public async Task Setup()
        {
            string body = bodyTemplate.Replace("SECRET", SecretSentinel).Replace("TOKEN", TokenSentinel);
            int attempt = 0;
            var fakeTime = new FakeTimeProvider();
            _logs = new RecordingLoggerProvider();
            _transport = new ScriptedTransport(
                (_, _) =>
                    Task.FromResult(
                        Interlocked.Increment(ref attempt) == 1
                            ? JsonResponse(HttpStatusCode.OK, body)
                            : JsonResponse(HttpStatusCode.OK, ValidGrantBody("recovered-token"))
                    )
            );
            _provider = BuildRegisteredProvider(_logs, fakeTime, _transport, _transport);

            _failure = await CaptureFailure(() => RequestToken(_provider));

            // Past any failed-grant cooldown, so the next call is admitted to a fresh grant.
            fakeTime.Advance(TimeSpan.FromSeconds(5));
            _nextToken = await RequestToken(_provider).WaitAsync(Failsafe);
        }

        [TearDown]
        public void TearDown() => _provider.Dispose();

        [Test]
        public void It_fails_with_a_request_failure() => _failure.Should().BeOfType<HttpRequestException>();

        [Test]
        public void It_does_not_cache_the_unusable_grant()
        {
            _nextToken.Should().Be("recovered-token");
            _transport.Requests.Should().HaveCount(2);
        }

        [Test]
        public void It_keeps_secrets_out_of_logs_and_the_exception_chain()
        {
            AssertNoSentinels(_logs.Rendered);
            AssertNoSentinels(_failure!.ToString());
        }
    }

    /// <summary>
    /// A grant redirect is surfaced as its status. The redirect target, a real loopback endpoint
    /// behind the unmodified registration, never receives the credential form.
    /// </summary>
    [TestFixture(HttpStatusCode.TemporaryRedirect)]
    [TestFixture(HttpStatusCode.PermanentRedirect)]
    [NonParallelizable]
    public class Given_A_Redirecting_Grant_Endpoint(HttpStatusCode redirectStatus)
        : ConfigurationServiceTokenHandlerTests
    {
        private WebApplication _cms = null!;
        private ServiceProvider _provider = null!;
        private RecordingLoggerProvider _logs = null!;
        private int _redirectTargetRequestCount;
        private Exception? _failure;

        [SetUp]
        public async Task Setup()
        {
            _redirectTargetRequestCount = 0;
            WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            _cms = builder.Build();
            _cms.MapPost(
                "/connect/token",
                context =>
                {
                    context.Response.StatusCode = (int)redirectStatus;
                    context.Response.Headers.Location = "/redirect-target";
                    return Task.CompletedTask;
                }
            );
            _cms.Map(
                "/redirect-target",
                async context =>
                {
                    Interlocked.Increment(ref _redirectTargetRequestCount);
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(ValidGrantBody("redirected-token"));
                }
            );
            await _cms.StartAsync();

            string baseAddress = _cms.Urls.Single().TrimEnd('/') + "/";
            _logs = new RecordingLoggerProvider();
            _provider = BuildRegisteredProvider(_logs, TimeProvider.System, baseAddress: baseAddress);

            _failure = await CaptureFailure(() => RequestToken(_provider));
        }

        [TearDown]
        public async Task TearDown()
        {
            await _provider.DisposeAsync();
            await _cms.StopAsync();
            await _cms.DisposeAsync();
        }

        [Test]
        public void It_rejects_with_the_redirect_status()
        {
            _failure.Should().BeOfType<HttpRequestException>().Which.StatusCode.Should().Be(redirectStatus);
        }

        [Test]
        public void It_sends_nothing_to_the_redirect_target() => _redirectTargetRequestCount.Should().Be(0);

        [Test]
        public void It_keeps_the_credential_out_of_diagnostics() =>
            _logs.Rendered.Should().NotContain(SecretSentinel);
    }

    /// <summary>
    /// A protected GET failure surfaces as a status-bearing request failure. Neither the outgoing
    /// bearer nor anything the CMS echoes in headers or body reaches logs, even at Trace, or the
    /// exception chain.
    /// </summary>
    [TestFixture(HttpStatusCode.Unauthorized, false)]
    [TestFixture(HttpStatusCode.Forbidden, false)]
    [TestFixture(HttpStatusCode.ServiceUnavailable, false)]
    [TestFixture(HttpStatusCode.NotFound, false)]
    [TestFixture(HttpStatusCode.Forbidden, true)]
    [Parallelizable]
    public class Given_A_Failed_Protected_Read(HttpStatusCode statusCode, bool allowNotFound)
        : ConfigurationServiceTokenHandlerTests
    {
        private ServiceProvider _provider = null!;
        private RecordingLoggerProvider _logs = null!;
        private Exception? _failure;

        [SetUp]
        public async Task Setup()
        {
            _logs = new RecordingLoggerProvider();
            ScriptedTransport transport = Answering(statusCode, TokenShapedBody);
            _provider = BuildRegisteredProvider(_logs, TimeProvider.System, transport, transport);
            HttpClient client = _provider.GetRequiredService<ConfigurationServiceApiClient>().Client;

            _failure = await CaptureFailure(async () =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"v3/apiClients/{GrantClientId}");
                request.Headers.Authorization = new("Bearer", TokenSentinel);
                request.Options.Set(ConfigurationServiceResponseHandler.AllowNotFoundResponse, allowNotFound);
                using HttpResponseMessage response = await client.SendAsync(request);
            });
        }

        [TearDown]
        public void TearDown() => _provider.Dispose();

        [Test]
        public void It_rejects_with_the_numeric_status()
        {
            _failure.Should().BeOfType<HttpRequestException>().Which.StatusCode.Should().Be(statusCode);
            _failure!.Message.Should().Contain(((int)statusCode).ToString());
        }

        [Test]
        public void It_keeps_the_bearer_and_echoed_content_out_of_diagnostics()
        {
            AssertNoSentinels(_logs.Rendered);
            AssertNoSentinels(_failure!.ToString());
        }

        [Test]
        public void It_logs_a_recoverable_401_as_a_warning_and_other_failures_as_errors()
        {
            LogLevel expected = statusCode is HttpStatusCode.Unauthorized ? LogLevel.Warning : LogLevel.Error;

            _logs
                .Entries.Where(entry => entry.Message.Contains($"status code {(int)statusCode}"))
                .Should()
                .ContainSingle()
                .Which.Level.Should()
                .Be(expected);
        }
    }

    /// <summary>An opted-in detail read still hands its 404 to the provider without a failure.</summary>
    [TestFixture]
    [Parallelizable]
    public class Given_An_Opted_In_Protected_Read_Not_Found : ConfigurationServiceTokenHandlerTests
    {
        private ServiceProvider _provider = null!;
        private RecordingLoggerProvider _logs = null!;
        private HttpStatusCode _statusCode;

        [SetUp]
        public async Task Setup()
        {
            _logs = new RecordingLoggerProvider();
            ScriptedTransport transport = Answering(HttpStatusCode.NotFound, TokenShapedBody);
            _provider = BuildRegisteredProvider(_logs, TimeProvider.System, transport, transport);
            HttpClient client = _provider.GetRequiredService<ConfigurationServiceApiClient>().Client;

            using var request = new HttpRequestMessage(HttpMethod.Get, $"v3/apiClients/{GrantClientId}");
            request.Headers.Authorization = new("Bearer", TokenSentinel);
            request.Options.Set(ConfigurationServiceResponseHandler.AllowNotFoundResponse, true);
            using HttpResponseMessage response = await client.SendAsync(request).WaitAsync(Failsafe);
            _statusCode = response.StatusCode;
        }

        [TearDown]
        public void TearDown() => _provider.Dispose();

        [Test]
        public void It_returns_the_not_found_response() => _statusCode.Should().Be(HttpStatusCode.NotFound);

        [Test]
        public void It_logs_no_error() =>
            _logs.Entries.Should().NotContain(entry => entry.Level >= LogLevel.Error);

        [Test]
        public void It_keeps_the_bearer_out_of_diagnostics() => AssertNoSentinels(_logs.Rendered);
    }

    /// <summary>
    /// The grant's raw lifetime. Missing and null stay distinct from malformed (which fails the
    /// grant), and the numeric forms the previous reader accepted, quoted numbers included, still
    /// parse.
    /// </summary>
    [TestFixture(null, null)]
    [TestFixture("null", null)]
    [TestFixture("600", 600d)]
    [TestFixture("\"600\"", 600d)]
    [TestFixture("600.5", 600.5d)]
    [TestFixture("0", 0d)]
    [TestFixture("-5", -5d)]
    [Parallelizable]
    public class Given_A_Grant_Lifetime_Representation(string? expiresInJson, double? expectedExpiresIn)
        : ConfigurationServiceTokenHandlerTests
    {
        private (string AccessToken, double? ExpiresIn) _grant;

        [SetUp]
        public async Task Setup()
        {
            string body = expiresInJson is null
                ? """{"access_token":"lifetime-token"}"""
                : $$"""{"access_token":"lifetime-token","expires_in":{{expiresInJson}}}""";

            var handler = new ConfigurationServiceTokenHandler(
                new ConfigurationServiceGrantClient(
                    new HttpClient(Answering(HttpStatusCode.OK, body))
                    {
                        BaseAddress = new Uri(CmsBaseAddress),
                    }
                ),
                CreateTokenCacheOptions(),
                TimeProvider.System,
                NullLogger<ConfigurationServiceTokenHandler>.Instance
            );

            _grant = await handler
                .FetchTokenAsync(GrantClientId, SecretSentinel, GrantScope, CancellationToken.None)
                .WaitAsync(Failsafe);
        }

        [Test]
        public void It_returns_the_bearer() => _grant.AccessToken.Should().Be("lifetime-token");

        [Test]
        public void It_returns_the_raw_lifetime() => _grant.ExpiresIn.Should().Be(expectedExpiresIn);
    }

    // ---------------------------------------------------------------------------------------------
    // Token lifetime, failed-grant cooldown and single flight. Every timing assertion advances the
    // injected FakeTimeProvider and waits on deterministic grant signals, never on real delays.
    // ---------------------------------------------------------------------------------------------

    internal static readonly TimeSpan OneTick = TimeSpan.FromTicks(1);

    /// <summary>Marks a fixture case whose grant response omits <c>expires_in</c>.</summary>
    internal const string MissingLifetime = "missing";

    /// <summary>A grant body with the given raw lifetime JSON, or none for <see cref="MissingLifetime" />.</summary>
    internal static string GrantBody(string accessToken, string expiresInJson) =>
        expiresInJson == MissingLifetime
            ? $$"""{"access_token":"{{accessToken}}","token_type":"bearer"}"""
            : $$"""{"access_token":"{{accessToken}}","token_type":"bearer","expires_in":{{expiresInJson}}}""";

    /// <summary>A transport that answers each request by its 1-based attempt number.</summary>
    internal static ScriptedTransport AnsweringByAttempt(Func<int, HttpResponseMessage> respond)
    {
        int attempt = 0;
        return new ScriptedTransport((_, _) => Task.FromResult(respond(Interlocked.Increment(ref attempt))));
    }

    internal static int TokenHandlerWarnings(RecordingLoggerProvider logs) =>
        logs.For(TokenHandlerCategory).Count(entry => entry.Level == LogLevel.Warning);

    /// <summary>One request held by a <see cref="GatedTransport" /> until the test answers it.</summary>
    internal sealed class PendingRequest(CancellationToken cancellationToken)
    {
        public TaskCompletionSource<HttpResponseMessage> Response { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The token the grant pipeline sent with this request.</summary>
        public CancellationToken CancellationToken { get; } = cancellationToken;

        public void Answer(HttpStatusCode statusCode, string body) =>
            Response.TrySetResult(JsonResponse(statusCode, body));
    }

    /// <summary>
    /// A primary transport that holds every request until the test answers it and signals each
    /// arrival, so a test can hold a grant in flight while callers join it.
    /// </summary>
    internal sealed class GatedTransport : HttpMessageHandler
    {
        private readonly Channel<PendingRequest> _arrivals = Channel.CreateUnbounded<PendingRequest>();
        private int _count;

        public int Count => Volatile.Read(ref _count);

        /// <summary>Waits for the next request, or returns null when none arrives by the failsafe.</summary>
        public async Task<PendingRequest?> NextAsync()
        {
            using var failsafe = new CancellationTokenSource(Failsafe);

            try
            {
                return await _arrivals.Reader.ReadAsync(failsafe.Token);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Interlocked.Increment(ref _count);
            var pending = new PendingRequest(cancellationToken);
            _arrivals.Writer.TryWrite(pending);
            return await pending.Response.Task.WaitAsync(cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            // Outlives the factory's handler chain, as ScriptedTransport does.
        }
    }

    /// <summary>
    /// A positive issuer lifetime L is reused for L - min(60 s, 0.1 L). No grant is sent before the
    /// cutoff and exactly one at it, measured with the injected clock.
    /// </summary>
    [TestFixture(600, 540)]
    [TestFixture(300, 270)]
    [TestFixture(3600, 3540)]
    [Parallelizable]
    public class Given_An_Issuer_Lifetime(int lifetimeSeconds, int cutoffSeconds)
        : ConfigurationServiceTokenHandlerTests
    {
        private ServiceProvider _provider = null!;
        private string _firstToken = null!;
        private string _tokenJustBeforeCutoff = null!;
        private string _tokenAtCutoff = null!;
        private int _attemptsJustBeforeCutoff;
        private int _attemptsAtCutoff;

        [SetUp]
        public async Task Setup()
        {
            var fakeTime = new FakeTimeProvider();
            ScriptedTransport transport = AnsweringByAttempt(attempt =>
                JsonResponse(HttpStatusCode.OK, ValidGrantBody($"issuer-token-{attempt}", lifetimeSeconds))
            );
            _provider = BuildRegisteredProvider(
                new RecordingLoggerProvider(),
                fakeTime,
                transport,
                transport
            );

            _firstToken = await RequestToken(_provider).WaitAsync(Failsafe);

            fakeTime.Advance(TimeSpan.FromSeconds(cutoffSeconds) - OneTick);
            _tokenJustBeforeCutoff = await RequestToken(_provider).WaitAsync(Failsafe);
            _attemptsJustBeforeCutoff = transport.Requests.Count;

            fakeTime.Advance(OneTick);
            _tokenAtCutoff = await RequestToken(_provider).WaitAsync(Failsafe);
            _attemptsAtCutoff = transport.Requests.Count;
        }

        [TearDown]
        public void TearDown() => _provider.Dispose();

        [Test]
        public void It_reuses_the_bearer_until_just_before_the_cutoff()
        {
            _attemptsJustBeforeCutoff.Should().Be(1);
            _tokenJustBeforeCutoff.Should().Be(_firstToken);
        }

        [Test]
        public void It_obtains_one_replacement_at_the_cutoff() => _attemptsAtCutoff.Should().Be(2);

        [Test]
        public void It_returns_the_replacement_at_the_cutoff() =>
            _tokenAtCutoff.Should().Be("issuer-token-2");
    }

    /// <summary>
    /// A missing, null, zero or negative lifetime is reused for 300 seconds from acquisition, with one
    /// safe warning per fallback grant.
    /// </summary>
    [TestFixture(MissingLifetime)]
    [TestFixture("null")]
    [TestFixture("0")]
    [TestFixture("-5")]
    [Parallelizable]
    public class Given_A_Fallback_Lifetime(string expiresInJson) : ConfigurationServiceTokenHandlerTests
    {
        private ServiceProvider _provider = null!;
        private RecordingLoggerProvider _logs = null!;
        private string _firstToken = null!;
        private string _tokenJustBeforeCutoff = null!;
        private string _tokenAtCutoff = null!;
        private int _attemptsJustBeforeCutoff;
        private int _attemptsAtCutoff;
        private int _warningsAfterFirstGrant;
        private int _warningsJustBeforeCutoff;
        private int _warningsAtCutoff;

        [SetUp]
        public async Task Setup()
        {
            var fakeTime = new FakeTimeProvider();
            _logs = new RecordingLoggerProvider();
            ScriptedTransport transport = AnsweringByAttempt(attempt =>
                JsonResponse(HttpStatusCode.OK, GrantBody($"{TokenSentinel}-{attempt}", expiresInJson))
            );
            _provider = BuildRegisteredProvider(_logs, fakeTime, transport, transport);

            _firstToken = await RequestToken(_provider).WaitAsync(Failsafe);
            _warningsAfterFirstGrant = TokenHandlerWarnings(_logs);

            fakeTime.Advance(TimeSpan.FromSeconds(300) - OneTick);
            _tokenJustBeforeCutoff = await RequestToken(_provider).WaitAsync(Failsafe);
            _attemptsJustBeforeCutoff = transport.Requests.Count;
            _warningsJustBeforeCutoff = TokenHandlerWarnings(_logs);

            fakeTime.Advance(OneTick);
            _tokenAtCutoff = await RequestToken(_provider).WaitAsync(Failsafe);
            _attemptsAtCutoff = transport.Requests.Count;
            _warningsAtCutoff = TokenHandlerWarnings(_logs);
        }

        [TearDown]
        public void TearDown() => _provider.Dispose();

        [Test]
        public void It_reuses_the_bearer_until_just_before_300_seconds()
        {
            _attemptsJustBeforeCutoff.Should().Be(1);
            _tokenJustBeforeCutoff.Should().Be(_firstToken);
        }

        [Test]
        public void It_obtains_one_replacement_at_300_seconds()
        {
            _attemptsAtCutoff.Should().Be(2);
            _tokenAtCutoff.Should().Be($"{TokenSentinel}-2");
        }

        [Test]
        public void It_warns_once_per_fallback_grant()
        {
            _warningsAfterFirstGrant.Should().Be(1);
            _warningsJustBeforeCutoff.Should().Be(1);
            _warningsAtCutoff.Should().Be(2);
        }

        [Test]
        public void It_keeps_the_bearer_out_of_the_warning() => AssertNoSentinels(_logs.Rendered);
    }

    /// <summary>
    /// An explicit 8.x cap limits reuse when it is lower than the issuer-derived or fallback window
    /// and leaves a shorter window unchanged.
    /// </summary>
    [TestFixture("90", "600", 90)]
    [TestFixture("90", MissingLifetime, 90)]
    [TestFixture("1000", "600", 540)]
    [TestFixture("1500", "600", 540)]
    [TestFixture("1500", MissingLifetime, 300)]
    [Parallelizable]
    public class Given_A_Token_Cache_Cap(string cap, string expiresInJson, int cutoffSeconds)
        : ConfigurationServiceTokenHandlerTests
    {
        private ServiceProvider _provider = null!;
        private string _firstToken = null!;
        private string _tokenJustBeforeCutoff = null!;
        private string _tokenAtCutoff = null!;
        private int _attemptsJustBeforeCutoff;
        private int _attemptsAtCutoff;

        [SetUp]
        public async Task Setup()
        {
            var fakeTime = new FakeTimeProvider();
            ScriptedTransport transport = AnsweringByAttempt(attempt =>
                JsonResponse(HttpStatusCode.OK, GrantBody($"capped-token-{attempt}", expiresInJson))
            );
            _provider = BuildRegisteredProvider(
                new RecordingLoggerProvider(),
                fakeTime,
                transport,
                transport,
                tokenCacheCap: cap
            );

            _firstToken = await RequestToken(_provider).WaitAsync(Failsafe);

            fakeTime.Advance(TimeSpan.FromSeconds(cutoffSeconds) - OneTick);
            _tokenJustBeforeCutoff = await RequestToken(_provider).WaitAsync(Failsafe);
            _attemptsJustBeforeCutoff = transport.Requests.Count;

            fakeTime.Advance(OneTick);
            _tokenAtCutoff = await RequestToken(_provider).WaitAsync(Failsafe);
            _attemptsAtCutoff = transport.Requests.Count;
        }

        [TearDown]
        public void TearDown() => _provider.Dispose();

        [Test]
        public void It_reuses_the_bearer_until_just_before_the_effective_cutoff()
        {
            _attemptsJustBeforeCutoff.Should().Be(1);
            _tokenJustBeforeCutoff.Should().Be(_firstToken);
        }

        [Test]
        public void It_obtains_one_replacement_at_the_effective_cutoff()
        {
            _attemptsAtCutoff.Should().Be(2);
            _tokenAtCutoff.Should().Be("capped-token-2");
        }
    }

    /// <summary>
    /// The reuse window is measured from the start of the grant, so a slow grant consumes it: a
    /// 600-second token that took 10 seconds to arrive is reused for only 530 more seconds.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_A_Slow_Grant : ConfigurationServiceTokenHandlerTests
    {
        private ServiceProvider _provider = null!;
        private GatedTransport _transport = null!;
        private string _firstToken = null!;
        private string _tokenJustBeforeCutoff = null!;
        private string? _tokenAtCutoff;
        private int _attemptsJustBeforeCutoff;
        private int _attemptsAtCutoff;

        [SetUp]
        public async Task Setup()
        {
            var fakeTime = new FakeTimeProvider();
            _transport = new GatedTransport();
            _provider = BuildRegisteredProvider(
                new RecordingLoggerProvider(),
                fakeTime,
                _transport,
                _transport
            );

            Task<string> first = RequestToken(_provider);
            PendingRequest firstGrant = await _transport.NextAsync() ?? throw new TimeoutException();
            fakeTime.Advance(TimeSpan.FromSeconds(10));
            firstGrant.Answer(HttpStatusCode.OK, ValidGrantBody("slow-token-1", 600));
            _firstToken = await first.WaitAsync(Failsafe);

            fakeTime.Advance(TimeSpan.FromSeconds(530) - OneTick);
            _tokenJustBeforeCutoff = await RequestToken(_provider).WaitAsync(Failsafe);
            _attemptsJustBeforeCutoff = _transport.Count;

            fakeTime.Advance(OneTick);
            Task<string> atCutoff = RequestToken(_provider);
            PendingRequest? replacement = await _transport.NextAsync();
            _attemptsAtCutoff = _transport.Count;
            replacement?.Answer(HttpStatusCode.OK, ValidGrantBody("slow-token-2", 600));
            _tokenAtCutoff = (await CaptureResult(atCutoff)).Token;
        }

        [TearDown]
        public void TearDown() => _provider.Dispose();

        [Test]
        public void It_reuses_the_bearer_until_just_before_530_more_seconds()
        {
            _attemptsJustBeforeCutoff.Should().Be(1);
            _tokenJustBeforeCutoff.Should().Be(_firstToken);
        }

        [Test]
        public void It_obtains_one_replacement_at_540_seconds_from_grant_start()
        {
            _attemptsAtCutoff.Should().Be(2);
            _tokenAtCutoff.Should().Be("slow-token-2");
        }
    }

    /// <summary>
    /// The deprecated cap runs from acquisition while the issuer window runs from the grant's start.
    /// A grant slower than the cap still yields a usable bearer, reused for the cap after it arrives,
    /// and an issuer window that ends first still wins.
    /// </summary>
    [TestFixture(600, "5", 10, 5)]
    [TestFixture(10, "100", 3, 6)]
    [Parallelizable]
    public class Given_A_Token_Cache_Cap_And_A_Slow_Grant(
        int lifetimeSeconds,
        string cap,
        int grantSeconds,
        int reuseAfterAcquisitionSeconds
    ) : ConfigurationServiceTokenHandlerTests
    {
        private ServiceProvider _provider = null!;
        private GatedTransport _transport = null!;
        private (string? Token, Exception? Failure) _first;
        private string _tokenJustBeforeCutoff = null!;
        private int _attemptsJustBeforeCutoff;
        private int _attemptsAtCutoff;

        [SetUp]
        public async Task Setup()
        {
            var fakeTime = new FakeTimeProvider();
            _transport = new GatedTransport();
            _provider = BuildRegisteredProvider(
                new RecordingLoggerProvider(),
                fakeTime,
                _transport,
                _transport,
                tokenCacheCap: cap
            );

            Task<string> first = RequestToken(_provider);
            PendingRequest firstGrant = await _transport.NextAsync() ?? throw new TimeoutException();
            fakeTime.Advance(TimeSpan.FromSeconds(grantSeconds));
            firstGrant.Answer(HttpStatusCode.OK, ValidGrantBody("capped-slow-token-1", lifetimeSeconds));
            _first = await CaptureResult(first);

            fakeTime.Advance(TimeSpan.FromSeconds(reuseAfterAcquisitionSeconds) - OneTick);
            _tokenJustBeforeCutoff = await RequestToken(_provider).WaitAsync(Failsafe);
            _attemptsJustBeforeCutoff = _transport.Count;

            fakeTime.Advance(OneTick);
            Task<string> atCutoff = RequestToken(_provider);
            PendingRequest? replacement = await _transport.NextAsync();
            _attemptsAtCutoff = _transport.Count;
            replacement?.Answer(HttpStatusCode.OK, ValidGrantBody("capped-slow-token-2", lifetimeSeconds));
            await CaptureResult(atCutoff);
        }

        [TearDown]
        public void TearDown() => _provider.Dispose();

        [Test]
        public void It_publishes_the_slow_bearer()
        {
            _first.Failure.Should().BeNull();
            _first.Token.Should().Be("capped-slow-token-1");
        }

        [Test]
        public void It_reuses_the_bearer_until_just_before_the_effective_cutoff()
        {
            _attemptsJustBeforeCutoff.Should().Be(1);
            _tokenJustBeforeCutoff.Should().Be("capped-slow-token-1");
        }

        [Test]
        public void It_obtains_one_replacement_at_the_effective_cutoff() => _attemptsAtCutoff.Should().Be(2);
    }

    internal static async Task<(string? Token, Exception? Failure)> CaptureResult(Task<string> call)
    {
        try
        {
            return (await call.WaitAsync(Failsafe), null);
        }
        catch (Exception exception)
        {
            return (null, exception);
        }
    }

    /// <summary>
    /// A short positive lifetime is not rounded up: one second gives a 900 ms reuse window.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_A_Short_Issuer_Lifetime : ConfigurationServiceTokenHandlerTests
    {
        private ServiceProvider _provider = null!;
        private int _attemptsJustBeforeCutoff;
        private int _attemptsAtCutoff;

        [SetUp]
        public async Task Setup()
        {
            var fakeTime = new FakeTimeProvider();
            ScriptedTransport transport = AnsweringByAttempt(attempt =>
                JsonResponse(HttpStatusCode.OK, GrantBody($"short-token-{attempt}", "1"))
            );
            _provider = BuildRegisteredProvider(
                new RecordingLoggerProvider(),
                fakeTime,
                transport,
                transport
            );

            await RequestToken(_provider).WaitAsync(Failsafe);

            fakeTime.Advance(TimeSpan.FromMilliseconds(899));
            await RequestToken(_provider).WaitAsync(Failsafe);
            _attemptsJustBeforeCutoff = transport.Requests.Count;

            fakeTime.Advance(TimeSpan.FromMilliseconds(1));
            await RequestToken(_provider).WaitAsync(Failsafe);
            _attemptsAtCutoff = transport.Requests.Count;
        }

        [TearDown]
        public void TearDown() => _provider.Dispose();

        [Test]
        public void It_reuses_the_bearer_before_900_milliseconds() =>
            _attemptsJustBeforeCutoff.Should().Be(1);

        [Test]
        public void It_obtains_a_replacement_at_900_milliseconds() => _attemptsAtCutoff.Should().Be(2);
    }

    /// <summary>Holds a joined caller after publication without delaying the shared grant.</summary>
    private sealed class DeferredTokenCaller : SynchronizationContext
    {
        private readonly Channel<(SendOrPostCallback Callback, object? State)> _callbacks =
            Channel.CreateUnbounded<(SendOrPostCallback, object?)>();

        public override void Post(SendOrPostCallback d, object? state) =>
            _callbacks.Writer.TryWrite((d, state));

        public Task<ConfigurationServiceTokenLease> Join(IConfigurationServiceTokenHandler handler)
        {
            SynchronizationContext? previous = Current;
            SetSynchronizationContext(this);

            try
            {
                return handler.GetTokenAsync(GrantClientId, SecretSentinel, GrantScope);
            }
            finally
            {
                SetSynchronizationContext(previous);
            }
        }

        public async Task ResumeAsync()
        {
            var callback = await _callbacks.Reader.ReadAsync().AsTask().WaitAsync(Failsafe);
            callback.Callback(callback.State);
        }
    }

    /// <summary>
    /// A grant can publish successfully but expire before its joiners resume. Those joiners must
    /// fail through one shared cooldown instead of repeatedly acquiring short-lived tokens.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_A_Published_Token_Expiring_Before_Admission : ConfigurationServiceTokenHandlerTests
    {
        private ServiceProvider _provider = null!;
        private RecordingLoggerProvider _logs = null!;
        private ScriptedTransport _transport = null!;
        private Exception?[] _failures = [];
        private Exception? _insideCooldown;
        private int _attemptsInsideCooldown;
        private int _errorsInsideCooldown;
        private string _replacement = null!;

        [SetUp]
        public async Task Setup()
        {
            var time = new FakeTimeProvider();
            var response = new TaskCompletionSource<HttpResponseMessage>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            int attempts = 0;
            _transport = new ScriptedTransport(
                (_, _) =>
                    Interlocked.Increment(ref attempts) == 1
                        ? response.Task
                        : Task.FromResult(JsonResponse(HttpStatusCode.OK, ValidGrantBody("replacement")))
            );
            _logs = new RecordingLoggerProvider();
            _provider = BuildRegisteredProvider(_logs, time, _transport);
            var handler = _provider.GetRequiredService<IConfigurationServiceTokenHandler>();
            Task<string> first = RequestToken(_provider);
            DeferredTokenCaller[] callers = [new(), new(), new()];
            Task<ConfigurationServiceTokenLease>[] joined =
            [
                .. callers.Select(caller => caller.Join(handler)),
            ];

            time.Advance(TimeSpan.FromMilliseconds(850));
            response.SetResult(JsonResponse(HttpStatusCode.OK, ValidGrantBody(TokenSentinel, 1)));
            (await first.WaitAsync(Failsafe)).Should().Be(TokenSentinel);
            time.Advance(TimeSpan.FromMilliseconds(50));

            foreach (DeferredTokenCaller caller in callers)
            {
                await caller.ResumeAsync();
            }

            _failures = await Task.WhenAll(joined.Select(call => CaptureFailure(() => call)));
            time.Advance(TimeSpan.FromSeconds(5) - OneTick);
            _insideCooldown = await CaptureFailure(() => RequestToken(_provider));
            _attemptsInsideCooldown = _transport.Requests.Count;
            _errorsInsideCooldown = _logs
                .For(TokenHandlerCategory)
                .Count(entry => entry.Level == LogLevel.Error);

            time.Advance(OneTick);
            _replacement = await RequestToken(_provider).WaitAsync(Failsafe);
        }

        [TearDown]
        public void TearDown() => _provider.Dispose();

        [Test]
        public void It_fails_every_delayed_joiner_without_returning_an_expired_token() =>
            _failures
                .Should()
                .AllSatisfy(failure =>
                    failure
                        .Should()
                        .BeOfType<HttpRequestException>()
                        .Which.HttpRequestError.Should()
                        .Be(HttpRequestError.InvalidResponse)
                );

        [Test]
        public void It_shares_the_failure_for_five_seconds()
        {
            _insideCooldown
                .Should()
                .BeOfType<HttpRequestException>()
                .Which.Message.Should()
                .Be(_failures[0]!.Message);
            _attemptsInsideCooldown.Should().Be(1);
        }

        [Test]
        public void It_logs_the_exhaustion_once_without_bearer_or_credential_values()
        {
            _errorsInsideCooldown.Should().Be(1);
            AssertNoSentinels(_logs.Rendered);
            foreach (Exception? failure in _failures)
            {
                AssertNoSentinels(failure!.ToString());
            }
        }

        [Test]
        public void It_admits_a_replacement_at_the_cooldown_boundary()
        {
            _transport.Requests.Should().HaveCount(2);
            _replacement.Should().Be("replacement");
        }
    }

    /// <summary>
    /// A delayed joiner must not overwrite a newer generation or its failure window when the older
    /// token expires. Generation fencing still takes precedence over the exhausted-token policy.
    /// </summary>
    [TestFixture("success")]
    [TestFixture("failure")]
    [TestFixture("pending")]
    [Parallelizable]
    public class Given_An_Expired_Joined_Token_After_A_Newer_Grant(string newerOutcome)
        : ConfigurationServiceTokenHandlerTests
    {
        private ServiceProvider _provider = null!;
        private ScriptedTransport _transport = null!;
        private Exception? _failure;
        private ConfigurationServiceTokenLease? _result;
        private ConfigurationServiceTokenLease? _newer;

        [SetUp]
        public async Task Setup()
        {
            var time = new FakeTimeProvider();
            var response = new TaskCompletionSource<HttpResponseMessage>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            var replacement = new TaskCompletionSource<HttpResponseMessage>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            int attempts = 0;
            _transport = new ScriptedTransport(
                (_, _) => Interlocked.Increment(ref attempts) == 1 ? response.Task : replacement.Task
            );
            _provider = BuildRegisteredProvider(new RecordingLoggerProvider(), time, _transport);
            var handler = _provider.GetRequiredService<IConfigurationServiceTokenHandler>();
            Task<string> first = RequestToken(_provider);
            var caller = new DeferredTokenCaller();
            Task<ConfigurationServiceTokenLease> joined = caller.Join(handler);
            response.SetResult(JsonResponse(HttpStatusCode.OK, ValidGrantBody(TokenSentinel, 1)));
            await first.WaitAsync(Failsafe);
            time.Advance(TimeSpan.FromMilliseconds(900));

            Task<ConfigurationServiceTokenLease> newer = handler.GetTokenAsync(
                GrantClientId,
                SecretSentinel,
                GrantScope
            );

            if (newerOutcome is "pending")
            {
                await caller.ResumeAsync();
            }

            replacement.SetResult(
                newerOutcome is "failure"
                    ? JsonResponse(HttpStatusCode.TooManyRequests, TokenShapedBody)
                    : JsonResponse(HttpStatusCode.OK, ValidGrantBody("newer"))
            );
            await CaptureFailure(async () =>
            {
                _newer = await newer;
            });

            if (newerOutcome is not "pending")
            {
                await caller.ResumeAsync();
            }

            _failure = await CaptureFailure(async () =>
            {
                _result = await joined;
            });

            // A late joiner must not shorten a newer 429's 30-second window to five seconds.
            time.Advance(TimeSpan.FromSeconds(5));
            await CaptureFailure(() => RequestToken(_provider));
        }

        [TearDown]
        public void TearDown() => _provider.Dispose();

        [Test]
        public void It_preserves_the_newer_outcome()
        {
            if (newerOutcome is "failure")
            {
                _failure
                    .Should()
                    .BeOfType<HttpRequestException>()
                    .Which.StatusCode.Should()
                    .Be(HttpStatusCode.TooManyRequests);
            }
            else
            {
                _failure.Should().BeNull();
                _result.Should().BeSameAs(_newer);
            }
        }

        [Test]
        public void It_sends_no_additional_grant() => _transport.Requests.Should().HaveCount(2);
    }

    /// <summary>
    /// A grant that completes at its own computed cutoff has no reuse left. It fails safely and
    /// starts the short failure window.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_A_Grant_Completing_At_Its_Cutoff : ConfigurationServiceTokenHandlerTests
    {
        private ServiceProvider _provider = null!;
        private RecordingLoggerProvider _logs = null!;
        private GatedTransport _transport = null!;
        private Exception? _failure;
        private Exception? _failureInsideWindow;
        private int _attemptsInsideWindow;
        private string? _tokenAtWindowBoundary;
        private int _attemptsAtWindowBoundary;

        [SetUp]
        public async Task Setup()
        {
            var fakeTime = new FakeTimeProvider();
            _logs = new RecordingLoggerProvider();
            _transport = new GatedTransport();
            _provider = BuildRegisteredProvider(_logs, fakeTime, _transport, _transport);

            // A 10-second lifetime gives a 9-second window from grant start.
            Task<string> first = RequestToken(_provider);
            PendingRequest exhausted = await _transport.NextAsync() ?? throw new TimeoutException();
            fakeTime.Advance(TimeSpan.FromSeconds(9));
            exhausted.Answer(HttpStatusCode.OK, GrantBody(TokenSentinel, "10"));
            _failure = await CaptureFailure(() => first);

            fakeTime.Advance(TimeSpan.FromSeconds(5) - OneTick);
            _failureInsideWindow = await CaptureFailure(() => RequestToken(_provider));
            _attemptsInsideWindow = _transport.Count;

            fakeTime.Advance(OneTick);
            Task<string> atBoundary = RequestToken(_provider);
            PendingRequest? replacement = await _transport.NextAsync();
            _attemptsAtWindowBoundary = _transport.Count;
            replacement?.Answer(HttpStatusCode.OK, ValidGrantBody("after-exhaustion-token", 600));
            _tokenAtWindowBoundary = (await CaptureResult(atBoundary)).Token;
        }

        [TearDown]
        public void TearDown() => _provider.Dispose();

        [Test]
        public void It_fails_the_exhausted_grant() => _failure.Should().BeOfType<HttpRequestException>();

        [Test]
        public void It_suppresses_attempts_for_five_seconds()
        {
            _failureInsideWindow.Should().BeOfType<HttpRequestException>();
            _attemptsInsideWindow.Should().Be(1);
        }

        [Test]
        public void It_admits_one_attempt_at_the_window_boundary()
        {
            _attemptsAtWindowBoundary.Should().Be(2);
            _tokenAtWindowBoundary.Should().Be("after-exhaustion-token");
        }

        [Test]
        public void It_keeps_the_exhausted_bearer_out_of_diagnostics()
        {
            AssertNoSentinels(_logs.Rendered);
            AssertNoSentinels(_failure!.ToString());
        }
    }

    /// <summary>
    /// An already-canceled caller must not consume a cached answer, start a grant, or receive an
    /// availability failure from the cooldown instead of its own cancellation.
    /// </summary>
    [TestFixture("empty", 0)]
    [TestFixture("reusable", 1)]
    [TestFixture("cooldown", 1)]
    [Parallelizable]
    public class Given_An_Already_Canceled_Token_Caller(string cacheState, int expectedGrantCount)
        : ConfigurationServiceTokenHandlerTests
    {
        private ServiceProvider _provider = null!;
        private ScriptedTransport _transport = null!;
        private Exception _failure = null!;
        private CancellationToken _callerToken;
        private int _grantCount;

        [SetUp]
        public async Task Setup()
        {
            HttpStatusCode status =
                cacheState == "cooldown" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK;
            _transport = Answering(status, ValidGrantBody());
            _provider = BuildRegisteredProvider(
                new RecordingLoggerProvider(),
                new FakeTimeProvider(),
                _transport
            );

            if (cacheState != "empty")
            {
                await CaptureFailure(() => RequestToken(_provider));
            }

            _callerToken = new CancellationToken(canceled: true);
            _failure = (await CaptureFailure(() => RequestToken(_provider, _callerToken)))!;
            _grantCount = _transport.Requests.Count;
        }

        [TearDown]
        public void TearDown() => _provider.Dispose();

        [Test]
        public void It_propagates_the_callers_cancellation() =>
            _failure.Should().BeAssignableTo<OperationCanceledException>();

        [Test]
        public void It_preserves_the_callers_token() =>
            _failure
                .Should()
                .BeAssignableTo<OperationCanceledException>()
                .Which.CancellationToken.Should()
                .Be(_callerToken);

        [Test]
        public void It_does_not_start_a_grant() => _grantCount.Should().Be(expectedGrantCount);
    }

    /// <summary>
    /// A failed grant suppresses new attempts from its completion: 30 seconds after a 429 and 5 seconds
    /// after any other status. Overlapping callers share the one attempt, later arrivals inside the
    /// window receive the same safe failure, and one attempt is admitted at the boundary.
    /// </summary>
    [TestFixture(HttpStatusCode.TooManyRequests, 30)]
    [TestFixture(HttpStatusCode.InternalServerError, 5)]
    [TestFixture(HttpStatusCode.Conflict, 5)]
    [Parallelizable]
    public class Given_A_Failed_Grant(HttpStatusCode statusCode, int windowSeconds)
        : ConfigurationServiceTokenHandlerTests
    {
        private const int Callers = 3;
        private const string ReplacementBearer = "replacement-bearer";

        private ServiceProvider _provider = null!;
        private RecordingLoggerProvider _logs = null!;
        private GatedTransport _transport = null!;
        private Exception?[] _overlappingFailures = [];
        private int _attemptsForOverlappingCallers;
        private Exception? _failureInsideCooldown;
        private int _attemptsInsideCooldown;
        private int _grantBoundaryLogsInsideCooldown;
        private int _attemptsAtCooldownBoundary;
        private (string? Token, Exception? Failure)[] _boundaryResults = [];

        [SetUp]
        public async Task Setup()
        {
            var fakeTime = new FakeTimeProvider();
            _logs = new RecordingLoggerProvider();
            _transport = new GatedTransport();
            _provider = BuildRegisteredProvider(_logs, fakeTime, _transport, _transport);

            Task<string>[] overlapping =
            [
                .. Enumerable.Range(0, Callers).Select(_ => RequestToken(_provider)),
            ];
            PendingRequest failing = await _transport.NextAsync() ?? throw new TimeoutException();
            failing.Answer(statusCode, TokenShapedBody);
            _overlappingFailures = await Task.WhenAll(overlapping.Select(call => CaptureFailure(() => call)));
            _attemptsForOverlappingCallers = _transport.Count;

            fakeTime.Advance(TimeSpan.FromSeconds(windowSeconds) - OneTick);
            _failureInsideCooldown = await CaptureFailure(() => RequestToken(_provider));
            _attemptsInsideCooldown = _transport.Count;
            _grantBoundaryLogsInsideCooldown = _logs
                .For(TokenHandlerCategory)
                .Count(entry => entry.Message.Contains($"status code {(int)statusCode}"));

            fakeTime.Advance(OneTick);
            Task<string>[] atBoundary =
            [
                .. Enumerable.Range(0, Callers).Select(_ => RequestToken(_provider)),
            ];
            PendingRequest? replacement = await _transport.NextAsync();
            _attemptsAtCooldownBoundary = _transport.Count;
            replacement?.Answer(HttpStatusCode.OK, ValidGrantBody(ReplacementBearer, 600));
            _boundaryResults = await Task.WhenAll(atBoundary.Select(CaptureResult));
        }

        [TearDown]
        public void TearDown() => _provider.Dispose();

        [Test]
        public void It_shares_one_attempt_among_overlapping_callers() =>
            _attemptsForOverlappingCallers.Should().Be(1);

        [Test]
        public void It_fails_every_overlapping_caller_with_the_status()
        {
            _overlappingFailures
                .Should()
                .AllSatisfy(failure =>
                    failure.Should().BeOfType<HttpRequestException>().Which.StatusCode.Should().Be(statusCode)
                );
        }

        [Test]
        public void It_suppresses_attempts_inside_the_window() => _attemptsInsideCooldown.Should().Be(1);

        [Test]
        public void It_returns_the_same_safe_failure_inside_the_window()
        {
            _failureInsideCooldown
                .Should()
                .BeOfType<HttpRequestException>()
                .Which.StatusCode.Should()
                .Be(statusCode);
            AssertNoSentinels(_failureInsideCooldown!.ToString());
        }

        [Test]
        public void It_logs_the_failed_grant_once() => _grantBoundaryLogsInsideCooldown.Should().Be(1);

        [Test]
        public void It_admits_one_attempt_at_the_window_boundary() =>
            _attemptsAtCooldownBoundary.Should().Be(2);

        [Test]
        public void It_shares_the_replacement_with_every_boundary_caller()
        {
            string?[] successfulWaiters = [.. _boundaryResults.Select(result => result.Token)];
            successfulWaiters
                .Should()
                .HaveCount(Callers)
                .And.OnlyContain(token => token == ReplacementBearer);
        }
    }

    /// <summary>
    /// A grant that fails without a status - unusable metadata, an unrepresentable lifetime, an
    /// unusable bearer, a transport failure or the shared timeout - suppresses attempts for five
    /// seconds.
    /// </summary>
    [TestFixture("malformed")]
    [TestFixture("unrepresentable")]
    [TestFixture("unusable")]
    [TestFixture("transport")]
    [TestFixture("timeout")]
    [Parallelizable]
    public class Given_A_Failed_Grant_Without_A_Status(string failureKind)
        : ConfigurationServiceTokenHandlerTests
    {
        private ServiceProvider _provider = null!;
        private ScriptedTransport _transport = null!;
        private Exception? _failure;
        private Exception? _failureInsideWindow;
        private int _attemptsInsideWindow;
        private string? _tokenAtWindowBoundary;
        private int _attemptsAtWindowBoundary;

        [SetUp]
        public async Task Setup()
        {
            var fakeTime = new FakeTimeProvider();
            var readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int attempt = 0;
            _transport = new ScriptedTransport(
                (_, _) =>
                {
                    if (Interlocked.Increment(ref attempt) > 1)
                    {
                        return Task.FromResult(
                            JsonResponse(HttpStatusCode.OK, ValidGrantBody("recovered-token"))
                        );
                    }

                    return failureKind switch
                    {
                        "malformed" => Task.FromResult(JsonResponse(HttpStatusCode.OK, "not json")),
                        "unrepresentable" => Task.FromResult(
                            JsonResponse(HttpStatusCode.OK, GrantBody("unrepresentable-token", "1e300"))
                        ),
                        "unusable" => Task.FromResult(
                            JsonResponse(HttpStatusCode.OK, """{"access_token":""}""")
                        ),
                        "transport" => throw new HttpRequestException(
                            HttpRequestError.ConnectionError,
                            "connect failed"
                        ),
                        _ => Task.FromResult(
                            new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new StreamContent(new StalledStream(readStarted)),
                            }
                        ),
                    };
                }
            );
            _provider = BuildRegisteredProvider(
                new RecordingLoggerProvider(),
                fakeTime,
                _transport,
                _transport
            );

            Task<string> first = RequestToken(_provider);

            if (failureKind == "timeout")
            {
                await readStarted.Task.WaitAsync(Failsafe);
                fakeTime.Advance(TimeSpan.FromSeconds(100));
            }

            _failure = await CaptureFailure(() => first);

            fakeTime.Advance(TimeSpan.FromSeconds(5) - OneTick);
            _failureInsideWindow = await CaptureFailure(() => RequestToken(_provider));
            _attemptsInsideWindow = _transport.Requests.Count;

            fakeTime.Advance(OneTick);
            _tokenAtWindowBoundary = (await CaptureResult(RequestToken(_provider))).Token;
            _attemptsAtWindowBoundary = _transport.Requests.Count;
        }

        [TearDown]
        public void TearDown() => _provider.Dispose();

        [Test]
        public void It_fails_the_grant() => _failure.Should().BeOfType<HttpRequestException>();

        [Test]
        public void It_suppresses_attempts_for_five_seconds()
        {
            _failureInsideWindow.Should().BeOfType<HttpRequestException>();
            _attemptsInsideWindow.Should().Be(1);
        }

        [Test]
        public void It_admits_one_attempt_at_the_window_boundary()
        {
            _attemptsAtWindowBoundary.Should().Be(2);
            _tokenAtWindowBoundary.Should().Be("recovered-token");
        }
    }

    /// <summary>
    /// Once the host has finished draining, it cancels a pending grant. Nothing is published, no
    /// failed-grant cooldown starts, and the grant boundary logs no failure.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_Host_Stopped_During_A_Grant : ConfigurationServiceTokenHandlerTests
    {
        private ServiceProvider _provider = null!;
        private RecordingLoggerProvider _logs = null!;
        private CancellationTokenSource _stopped = null!;
        private Exception? _pendingFailure;
        private Exception? _failureAfterShutdown;

        [SetUp]
        public async Task Setup()
        {
            _stopped = new CancellationTokenSource();
            IHostApplicationLifetime lifetime = A.Fake<IHostApplicationLifetime>();
            A.CallTo(() => lifetime.ApplicationStopped).Returns(_stopped.Token);
            _logs = new RecordingLoggerProvider();
            var transport = new GatedTransport();
            _provider = BuildRegisteredProvider(
                _logs,
                new FakeTimeProvider(),
                transport,
                transport,
                configureServices: services => services.AddSingleton(lifetime)
            );

            Task<string> pending = RequestToken(_provider);
            _ = await transport.NextAsync() ?? throw new TimeoutException();
            await _stopped.CancelAsync();
            _pendingFailure = await CaptureFailure(() => pending);

            _failureAfterShutdown = await CaptureFailure(() => RequestToken(_provider));
        }

        [TearDown]
        public void TearDown()
        {
            _provider.Dispose();
            _stopped.Dispose();
        }

        [Test]
        public void It_cancels_the_pending_grant() =>
            _pendingFailure.Should().BeAssignableTo<OperationCanceledException>();

        [Test]
        public void It_starts_no_failed_grant_cooldown() =>
            _failureAfterShutdown.Should().BeAssignableTo<OperationCanceledException>();

        [Test]
        public void It_logs_no_failed_grant() =>
            _logs.For(TokenHandlerCategory).Should().NotContain(entry => entry.Level >= LogLevel.Error);
    }

    /// <summary>
    /// Outside the web host nothing validates the deprecated cap at startup. An invalid one must still
    /// surface as its value-free options failure before any grant is sent, not as a transport failure
    /// after a real one.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_An_Invalid_Token_Cache_Cap_Without_Startup_Validation
        : ConfigurationServiceTokenHandlerTests
    {
        private const string MalformedCap = "cap-sentinel-4b7e2d";

        private ServiceProvider _provider = null!;
        private RecordingLoggerProvider _logs = null!;
        private ScriptedTransport _transport = null!;
        private Exception? _failure;
        private Exception? _secondFailure;

        [SetUp]
        public async Task Setup()
        {
            _logs = new RecordingLoggerProvider();
            _transport = Answering(HttpStatusCode.OK, ValidGrantBody());
            _provider = BuildRegisteredProvider(
                _logs,
                new FakeTimeProvider(),
                _transport,
                _transport,
                tokenCacheCap: MalformedCap
            );

            _failure = await CaptureFailure(() => RequestToken(_provider));
            _secondFailure = await CaptureFailure(() => RequestToken(_provider));
        }

        [TearDown]
        public void TearDown() => _provider.Dispose();

        [Test]
        public void It_fails_with_the_options_failure() =>
            _failure.Should().BeOfType<OptionsValidationException>();

        [Test]
        public void It_names_the_setting() => _failure!.Message.Should().Contain(TokenCacheCapKey);

        [Test]
        public void It_sends_no_grant() => _transport.Requests.Should().BeEmpty();

        [Test]
        public void It_reports_the_same_failure_again_rather_than_a_failure_window() =>
            _secondFailure.Should().BeOfType<OptionsValidationException>();

        [Test]
        public void It_keeps_the_supplied_value_out_of_diagnostics()
        {
            _failure!.ToString().Should().NotContain(MalformedCap);
            _logs.Rendered.Should().NotContain(MalformedCap);
        }
    }
}
