// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Models;
using EdFi.DmsConfigurationService.Backend.OpenIddict.SigningKeys;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using static EdFi.DmsConfigurationService.Backend.Tests.Unit.SigningKeys.SigningKeyTestSupport;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.SigningKeys;

public class SigningKeyBearerEventsTests
{
    private const string Issuer = "https://cms.example.test";
    private const string Audience = "ed-fi-cms-tests";
    private const string TraceId = "trace-2-3";

    private static readonly AuthenticationScheme _scheme = new(
        JwtBearerDefaults.AuthenticationScheme,
        displayName: null,
        typeof(JwtBearerHandler)
    );

    private static SigningKeyBearerEvents Events(
        ISigningKeySnapshotProvider provider,
        ITokenManager tokenManager,
        ILogger<SigningKeyBearerEvents> logger
    ) => new(provider, tokenManager, Options.Create(new IdentityOptions { Authority = Issuer }), logger);

    private static ILogger<SigningKeyBearerEvents> EnabledLogger()
    {
        var logger = A.Fake<ILogger<SigningKeyBearerEvents>>();
        A.CallTo(() => logger.IsEnabled(A<LogLevel>._)).Returns(true);
        return logger;
    }

    private static DefaultHttpContext HttpContextWith(string? authorization)
    {
        DefaultHttpContext context = new() { TraceIdentifier = TraceId };
        context.Response.Body = new MemoryStream();
        if (authorization is not null)
        {
            context.Request.Headers.Authorization = authorization;
        }

        return context;
    }

    /// <summary>A signed token for the test issuer and audience whose <c>kid</c> header is <paramref name="keyId"/>.</summary>
    private static string SignedToken(RSA rsa, string keyId) =>
        new JsonWebTokenHandler().CreateToken(
            new SecurityTokenDescriptor
            {
                Issuer = Issuer,
                Audience = Audience,
                Expires = DateTime.UtcNow.AddMinutes(10),
                SigningCredentials = new SigningCredentials(
                    new RsaSecurityKey(rsa) { KeyId = keyId },
                    SecurityAlgorithms.RsaSha256
                ),
            }
        );

    private static SigningKeySnapshot SnapshotOf(string keyId, RSA rsa) =>
        new(
            [SigningKeyEntry.FromRsaPublicParameters(keyId, rsa.ExportParameters(false))],
            DateTimeOffset.UtcNow,
            version: 1,
            SigningKeySource.Database
        );

    private static SigningKeysUnavailableException Unavailable() =>
        new(
            SigningKeysUnavailableReason.SnapshotExpired,
            new SigningKeyRefreshOutcome.Failed(
                SigningKeyFailureKind.Retrieval,
                new TimeoutException("store down")
            )
        );

    /// <summary>Counts the handler's requests for its configuration.</summary>
    private sealed class CountingConfigurationManager(IConfigurationManager<OpenIdConnectConfiguration> inner)
        : IConfigurationManager<OpenIdConnectConfiguration>
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel)
        {
            Interlocked.Increment(ref _calls);
            return inner.GetConfigurationAsync(cancel);
        }

        public void RequestRefresh() => inner.RequestRefresh();
    }

    /// <summary>
    /// Authenticates one request through the real <see cref="JwtBearerHandler"/>, configured with the counting manager
    /// and the shared message-received and token-validated handlers. The scheme wiring itself is step 3.1.
    /// </summary>
    private static async Task<AuthenticateResult> AuthenticateAsync(
        HttpContext httpContext,
        SigningKeyBearerEvents events,
        IConfigurationManager<OpenIdConnectConfiguration> configurationManager
    )
    {
        JwtBearerOptions options = new()
        {
            ConfigurationManager = configurationManager,
            RefreshOnIssuerKeyNotFound = false,
            TokenValidationParameters = new TokenValidationParameters
            {
                ValidIssuer = Issuer,
                ValidAudience = Audience,
            },
            Events = new JwtBearerEvents
            {
                OnMessageReceived = events.MessageReceivedAsync,
                OnTokenValidated = events.TokenValidatedAsync,
            },
        };
        var optionsMonitor = A.Fake<IOptionsMonitor<JwtBearerOptions>>();
        A.CallTo(() => optionsMonitor.Get(A<string>._)).Returns(options);

        JwtBearerHandler handler = new(optionsMonitor, NullLoggerFactory.Instance, UrlEncoder.Default);
        await handler.InitializeAsync(_scheme, httpContext);
        return await handler.AuthenticateAsync();
    }

    // 2.3-c, through the real handler: a cold request whose keys cannot be loaded fails with the typed exception before
    // the handler asks the configuration manager for anything.
    [TestFixture]
    public class Given_a_cold_request_when_no_keys_can_be_loaded
    {
        private SigningKeySnapshotProvider _provider = null!;
        private CountingConfigurationManager _manager = null!;
        private ITokenManager _tokenManager = null!;
        private ILogger<SigningKeyBearerEvents> _logger = null!;
        private AuthenticateResult _result = null!;

        [SetUp]
        public async Task Act()
        {
            var time = NewTime();
            KeyRepositoryHarness harness = new(time);
            harness.Fails(new TimeoutException("store down"));
            _provider = Provider(harness, time);
            _manager = new CountingConfigurationManager(new SigningKeyConfigurationManager(_provider));
            _tokenManager = A.Fake<ITokenManager>();
            _logger = EnabledLogger();
            using RSA rsa = RSA.Create(2048);

            _result = await AuthenticateAsync(
                HttpContextWith($"Bearer {SignedToken(rsa, "key-1")}"),
                Events(_provider, _tokenManager, _logger),
                _manager
            );
        }

        [TearDown]
        public void TearDown() => _provider.Dispose();

        [Test]
        public void It_fails_authentication() => _result.Succeeded.Should().BeFalse();

        [Test]
        public void It_fails_with_the_typed_exception() =>
            _result.Failure.Should().BeOfType<SigningKeysUnavailableException>();

        [Test]
        public void It_never_asks_the_configuration_manager() => _manager.Calls.Should().Be(0);

        [Test]
        public void It_never_checks_token_status() =>
            A.CallTo(() => _tokenManager.ValidateTokenAsync(A<string>._)).MustNotHaveHappened();

        [Test]
        public void It_logs_the_category_and_trace_id_at_error() =>
            MessagesAt(_logger, LogLevel.Error)
                .Should()
                .Equal(
                    $"Authentication could not reach a decision: the SigningKeyStore is unavailable (trace {TraceId})"
                );
    }

    // The same handler with keys available: the manager is asked once and the request authenticates, so the zero count
    // above is the boundary's doing.
    [TestFixture]
    public class Given_a_request_when_keys_are_available
    {
        private SigningKeySnapshotProvider _provider = null!;
        private CountingConfigurationManager _manager = null!;
        private ITokenManager _tokenManager = null!;
        private string _token = null!;
        private AuthenticateResult _result = null!;

        [SetUp]
        public async Task Act()
        {
            using RSA rsa = RSA.Create(2048);
            var time = NewTime();
            KeyRepositoryHarness harness = new(time);
            harness.Returns(
                new PublicKeyInfo { KeyId = "key-1", PublicKey = rsa.ExportSubjectPublicKeyInfo() }
            );
            _provider = Provider(harness, time);
            _manager = new CountingConfigurationManager(new SigningKeyConfigurationManager(_provider));
            _tokenManager = A.Fake<ITokenManager>();
            A.CallTo(() => _tokenManager.ValidateTokenAsync(A<string>._)).Returns(true);
            _token = SignedToken(rsa, "key-1");

            _result = await AuthenticateAsync(
                HttpContextWith($"Bearer {_token}"),
                Events(_provider, _tokenManager, NullLogger<SigningKeyBearerEvents>.Instance),
                _manager
            );
        }

        [TearDown]
        public void TearDown() => _provider.Dispose();

        [Test]
        public void It_authenticates() => _result.Succeeded.Should().BeTrue();

        [Test]
        public void It_asks_the_configuration_manager_once() => _manager.Calls.Should().Be(1);

        [Test]
        public void It_checks_the_status_of_the_validated_token() =>
            A.CallTo(() => _tokenManager.ValidateTokenAsync(_token)).MustHaveHappenedOnceExactly();
    }

    /// <summary>Runs the message-received handler over a faked provider whose snapshot holds <c>known-key</c>.</summary>
    private static async Task<(
        MessageReceivedContext Context,
        ISigningKeySnapshotProvider Provider
    )> ReceiveAsync(
        string? authorization,
        Action<ISigningKeySnapshotProvider, SigningKeySnapshot>? arrange = null,
        ILogger<SigningKeyBearerEvents>? logger = null
    )
    {
        using RSA rsa = RSA.Create(2048);
        SigningKeySnapshot snapshot = SnapshotOf("known-key", rsa);
        var provider = A.Fake<ISigningKeySnapshotProvider>();
        A.CallTo(() => provider.GetUsableAsync(A<CancellationToken>._)).Returns(snapshot);
        A.CallTo(() => provider.TryRefreshForUnknownKeyAsync(A<string>._, A<CancellationToken>._))
            .Returns(SigningKeyUnknownKeyOutcome.RefreshedAbsent);
        arrange?.Invoke(provider, snapshot);

        MessageReceivedContext context = new(HttpContextWith(authorization), _scheme, new JwtBearerOptions());
        await Events(provider, A.Fake<ITokenManager>(), logger ?? NullLogger<SigningKeyBearerEvents>.Instance)
            .MessageReceivedAsync(context);
        return (context, provider);
    }

    private static string TokenWithKeyId(string keyId)
    {
        using RSA rsa = RSA.Create(2048);
        return SignedToken(rsa, keyId);
    }

    [TestFixture]
    public class Given_a_request_without_a_bearer_token
    {
        [TestCase(null)]
        [TestCase("Basic Y2xpZW50OnNlY3JldA==")]
        [TestCase("Bearer   ")]
        public async Task It_does_nothing(string? authorization)
        {
            var (context, provider) = await ReceiveAsync(authorization);

            context.Result.Should().BeNull();
            A.CallTo(() => provider.GetUsableAsync(A<CancellationToken>._)).MustNotHaveHappened();
        }
    }

    // The request's abort token reaches the provider, so a client that disconnects stops waiting for a cold load.
    [TestFixture]
    public class Given_a_bearer_token_whose_key_id_the_snapshot_holds
    {
        private MessageReceivedContext _context = null!;
        private ISigningKeySnapshotProvider _provider = null!;

        [SetUp]
        public async Task Act() =>
            (_context, _provider) = await ReceiveAsync($"Bearer {TokenWithKeyId("known-key")}");

        [Test]
        public void It_leaves_the_result_to_the_handler() => _context.Result.Should().BeNull();

        [Test]
        public void It_waits_with_the_request_abort_token() =>
            A.CallTo(() => _provider.GetUsableAsync(_context.HttpContext.RequestAborted))
                .MustHaveHappenedOnceExactly();

        [Test]
        public void It_requests_no_unknown_key_refresh() =>
            A.CallTo(() => _provider.TryRefreshForUnknownKeyAsync(A<string>._, A<CancellationToken>._))
                .MustNotHaveHappened();
    }

    [TestFixture]
    public class Given_a_bearer_token_whose_header_cannot_be_parsed
    {
        private MessageReceivedContext _context = null!;
        private ISigningKeySnapshotProvider _provider = null!;

        [SetUp]
        public async Task Act() => (_context, _provider) = await ReceiveAsync("Bearer not-a-jwt");

        [Test]
        public void It_leaves_the_rejection_to_the_handler() => _context.Result.Should().BeNull();

        [Test]
        public void It_requests_no_unknown_key_refresh() =>
            A.CallTo(() => _provider.TryRefreshForUnknownKeyAsync(A<string>._, A<CancellationToken>._))
                .MustNotHaveHappened();
    }

    // 2.3-d: an unknown key id gets one refresh request, logged with the sanitized key id and the outcome, and
    // usability is checked again afterwards.
    [TestFixture]
    public class Given_a_bearer_token_with_an_unknown_key_id
    {
        private MessageReceivedContext _context = null!;
        private ISigningKeySnapshotProvider _provider = null!;
        private ILogger<SigningKeyBearerEvents> _logger = null!;

        [SetUp]
        public async Task Act()
        {
            _logger = EnabledLogger();
            (_context, _provider) = await ReceiveAsync(
                $"Bearer {TokenWithKeyId("unknown-key")}",
                logger: _logger
            );
        }

        [Test]
        public void It_requests_one_refresh_for_the_key_id() =>
            A.CallTo(() =>
                    _provider.TryRefreshForUnknownKeyAsync("unknown-key", _context.HttpContext.RequestAborted)
                )
                .MustHaveHappenedOnceExactly();

        [Test]
        public void It_checks_usability_again_after_the_refresh() =>
            A.CallTo(() => _provider.GetUsableAsync(A<CancellationToken>._)).MustHaveHappenedTwiceExactly();

        [Test]
        public void It_leaves_the_rejection_to_the_handler() => _context.Result.Should().BeNull();

        [Test]
        public void It_warns_with_the_key_id_and_the_outcome() =>
            MessagesAt(_logger, LogLevel.Warning)
                .Should()
                .Equal(
                    "Bearer token key id unknown-key was not in the signing-key snapshot; unknown-key refresh outcome: RefreshedAbsent"
                );
    }

    // 2.3-d: a snapshot that stopped being usable while the refresh ran (it crossed the maximum staleness and the
    // refresh failed) fails with the typed exception rather than proceeding to a 401.
    [TestFixture]
    public class Given_an_unknown_key_id_when_the_keys_expire_during_the_refresh
    {
        private readonly SigningKeysUnavailableException _unavailable = Unavailable();
        private MessageReceivedContext _context = null!;

        [SetUp]
        public async Task Act() =>
            (_context, _) = await ReceiveAsync(
                $"Bearer {TokenWithKeyId("unknown-key")}",
                (provider, snapshot) =>
                {
                    A.CallTo(() => provider.GetUsableAsync(A<CancellationToken>._))
                        .ReturnsNextFromSequence(
                            Task.FromResult(snapshot),
                            Task.FromException<SigningKeySnapshot>(_unavailable)
                        );
                    A.CallTo(() => provider.TryRefreshForUnknownKeyAsync(A<string>._, A<CancellationToken>._))
                        .Returns(SigningKeyUnknownKeyOutcome.RefreshFailed);
                }
            );

        [Test]
        public void It_fails_with_the_typed_exception() =>
            _context.Result!.Failure.Should().BeSameAs(_unavailable);
    }

    /// <summary>Runs the token-validated handler for a validated token whose status check answers as arranged.</summary>
    private static async Task<(
        TokenValidatedContext Context,
        ITokenManager TokenManager,
        string Token
    )> ValidatedAsync(Action<ITokenManager> arrange, ILogger<SigningKeyBearerEvents>? logger = null)
    {
        string token = TokenWithKeyId("known-key");
        var tokenManager = A.Fake<ITokenManager>();
        arrange(tokenManager);
        TokenValidatedContext context = new(
            HttpContextWith($"Bearer {token}"),
            _scheme,
            new JwtBearerOptions()
        )
        {
            SecurityToken = new JsonWebToken(token),
        };

        await Events(
                A.Fake<ISigningKeySnapshotProvider>(),
                tokenManager,
                logger ?? NullLogger<SigningKeyBearerEvents>.Instance
            )
            .TokenValidatedAsync(context);
        return (context, tokenManager, token);
    }

    // I-2: the uncached per-request status check runs on the validated token.
    [TestFixture]
    public class Given_a_validated_token_whose_status_is_valid
    {
        private TokenValidatedContext _context = null!;
        private ITokenManager _tokenManager = null!;
        private string _token = null!;

        [SetUp]
        public async Task Act() =>
            (_context, _tokenManager, _token) = await ValidatedAsync(tokenManager =>
                A.CallTo(() => tokenManager.ValidateTokenAsync(A<string>._)).Returns(true)
            );

        [Test]
        public void It_checks_the_status_of_the_validated_token() =>
            A.CallTo(() => _tokenManager.ValidateTokenAsync(_token)).MustHaveHappenedOnceExactly();

        [Test]
        public void It_leaves_the_success_in_place() => _context.Result.Should().BeNull();
    }

    [TestFixture]
    public class Given_a_validated_token_that_is_revoked
    {
        private TokenValidatedContext _context = null!;

        [SetUp]
        public async Task Act() =>
            (_context, _, _) = await ValidatedAsync(tokenManager =>
                A.CallTo(() => tokenManager.ValidateTokenAsync(A<string>._)).Returns(false)
            );

        [Test]
        public void It_fails_as_an_invalid_token() =>
            _context.Result!.Failure!.Message.Should().Be("Token has been revoked or is invalid.");

        [Test]
        public void It_is_not_a_dependency_failure() =>
            _context
                .Result!.Failure.Should()
                .NotBeAssignableTo<AuthenticationDependencyUnavailableException>();
    }

    [TestFixture]
    public class Given_a_validated_token_whose_status_store_is_unavailable
    {
        private readonly AuthenticationDependencyUnavailableException _unavailable = new(
            AuthenticationDependencyCategory.TokenStatusStore,
            "The token status store could not be read."
        );
        private TokenValidatedContext _context = null!;
        private ILogger<SigningKeyBearerEvents> _logger = null!;

        [SetUp]
        public async Task Act()
        {
            _logger = EnabledLogger();
            (_context, _, _) = await ValidatedAsync(
                tokenManager =>
                    A.CallTo(() => tokenManager.ValidateTokenAsync(A<string>._)).ThrowsAsync(_unavailable),
                _logger
            );
        }

        [Test]
        public void It_fails_with_the_typed_exception() =>
            _context.Result!.Failure.Should().BeSameAs(_unavailable);

        [Test]
        public void It_logs_the_category_and_trace_id_at_error() =>
            MessagesAt(_logger, LogLevel.Error)
                .Should()
                .Equal(
                    $"Authentication could not reach a decision: the TokenStatusStore is unavailable (trace {TraceId})"
                );
    }

    private static (bool Handled, AuthenticationFailedContext Context) FailedWith(Exception exception)
    {
        AuthenticationFailedContext context = new(HttpContextWith(null), _scheme, new JwtBearerOptions())
        {
            Exception = exception,
        };
        bool handled = Events(
                A.Fake<ISigningKeySnapshotProvider>(),
                A.Fake<ITokenManager>(),
                NullLogger<SigningKeyBearerEvents>.Instance
            )
            .TryFailOnDependency(context);
        return (handled, context);
    }

    // For example the configuration manager throwing inside the handler.
    [TestFixture]
    public class Given_authentication_failed_on_a_dependency
    {
        [Test]
        public void It_fails_with_the_typed_exception()
        {
            SigningKeysUnavailableException unavailable = Unavailable();

            var (handled, context) = FailedWith(unavailable);

            handled.Should().BeTrue();
            context.Result!.Failure.Should().BeSameAs(unavailable);
        }

        [Test]
        public void It_finds_the_typed_exception_inside_an_aggregate()
        {
            SigningKeysUnavailableException unavailable = Unavailable();

            var (handled, context) = FailedWith(
                new AggregateException(new SecurityTokenExpiredException("expired"), unavailable)
            );

            handled.Should().BeTrue();
            context.Result!.Failure.Should().BeSameAs(unavailable);
        }
    }

    // Anything else is left to the scheme's existing logging and the handler's existing behavior.
    [TestFixture]
    public class Given_authentication_failed_for_another_reason
    {
        [Test]
        public void It_leaves_the_failure_untouched()
        {
            var (handled, context) = FailedWith(new SecurityTokenSignatureKeyNotFoundException("IDX10500"));

            handled.Should().BeFalse();
            context.Result.Should().BeNull();
        }
    }

    private static async Task<(bool Handled, JwtBearerChallengeContext Context)> ChallengeAsync(
        Exception? failure
    )
    {
        JwtBearerChallengeContext context = new(
            HttpContextWith(null),
            _scheme,
            new JwtBearerOptions(),
            new AuthenticationProperties()
        )
        {
            AuthenticateFailure = failure,
        };
        bool handled = await Events(
                A.Fake<ISigningKeySnapshotProvider>(),
                A.Fake<ITokenManager>(),
                NullLogger<SigningKeyBearerEvents>.Instance
            )
            .TryChallengeDependencyAsync(context);
        return (handled, context);
    }

    // 2.3-e, D-9: a dependency failure is answered with 503, Retry-After, and a generic problem body.
    [TestFixture]
    public class Given_a_challenge_after_a_dependency_failure
    {
        private bool _handled;
        private JwtBearerChallengeContext _context = null!;
        private JsonObject _body = null!;

        private static JsonObject BodyOf(HttpResponse response)
        {
            response.Body.Position = 0;
            return JsonNode.Parse(new StreamReader(response.Body).ReadToEnd())!.AsObject();
        }

        [SetUp]
        public async Task Act()
        {
            (_handled, _context) = await ChallengeAsync(Unavailable());
            _body = BodyOf(_context.Response);
        }

        [Test]
        public void It_reports_the_challenge_handled() => _handled.Should().BeTrue();

        [Test]
        public void It_suppresses_the_default_challenge() => _context.Handled.Should().BeTrue();

        [Test]
        public void It_answers_503() =>
            _context.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);

        // min(RefreshInterval, 30 s) with the default 300 s interval.
        [Test]
        public void It_sets_retry_after() =>
            _context.Response.Headers.RetryAfter.ToString().Should().Be("30");

        [Test]
        public void It_answers_with_a_problem_document() =>
            _context.Response.ContentType.Should().Be("application/problem+json");

        [Test]
        public void It_uses_the_generic_service_unavailable_problem() =>
            _body
                .ToJsonString()
                .Should()
                .Be(
                    new JsonObject
                    {
                        ["detail"] = "",
                        ["type"] = "about:blank",
                        ["title"] = "Service Unavailable",
                        ["status"] = 503,
                        ["correlationId"] = TraceId,
                        ["validationErrors"] = new JsonObject(),
                        ["errors"] = new JsonArray(),
                    }.ToJsonString()
                );

        [Test]
        public void It_does_not_challenge_for_credentials() =>
            _context.Response.Headers.WWWAuthenticate.Should().BeEmpty();
    }

    // 2.3-e: any other challenge is untouched, so the scheme's 401 follows.
    [TestFixture]
    public class Given_a_challenge_after_an_ordinary_failure
    {
        [TestCase(true)]
        [TestCase(false)]
        public async Task It_leaves_the_challenge_to_the_scheme(bool hasFailure)
        {
            var (handled, context) = await ChallengeAsync(
                hasFailure ? new SecurityTokenExpiredException("expired") : null
            );

            handled.Should().BeFalse();
            context.Handled.Should().BeFalse();
            context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
            context.Response.Headers.RetryAfter.Should().BeEmpty();
            context.Response.Body.Length.Should().Be(0);
        }
    }
}
