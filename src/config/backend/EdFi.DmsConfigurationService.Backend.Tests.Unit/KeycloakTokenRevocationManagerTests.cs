// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json;
using EdFi.DmsConfigurationService.Backend.Keycloak;
using FakeItEasy;
using FluentAssertions;
using Flurl.Http;
using Keycloak.Net.Models.Clients;
using Microsoft.Extensions.Logging;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit;

/// <summary>
/// DMS-1327 D-09, D-10, D-11 and D-15 for <see cref="KeycloakTokenRevocationManager"/>. The admin read
/// is a faked facade; the revoke request goes through a real <see cref="HttpClient"/> over a
/// recording handler, so the request shape, the client's timeout and the response handling are the
/// production ones. Keycloak's answers are the strings recorded in the design's §9.1.
/// </summary>
public class KeycloakTokenRevocationManagerTests
{
    protected const string CallerClientId = "caller-client";
    protected const string CallerSecret = "SECRET-CALLER-SENTINEL";
    protected const string ServiceClientId = "cms-service";
    protected const string ServiceSecret = "SECRET-SERVICE-SENTINEL";
    protected const string Token = "SECRET-TOKEN-SENTINEL.payload.signature";
    protected const string Sentinel = "SECRET-PROVIDER-SENTINEL";
    protected const string RevokeUrl = "http://keycloak.test/realms/edfi/protocol/openid-connect/revoke";

    protected static readonly KeycloakContext _context = new(
        "http://keycloak.test",
        "edfi",
        ServiceClientId,
        ServiceSecret,
        "role"
    );

    protected RecordingHandler _handler = null!;
    protected IKeycloakClientFacade _facade = null!;
    protected IHttpClientFactory _httpClientFactory = null!;
    protected CapturingLogger<KeycloakTokenRevocationManager> _logger = null!;
    protected List<string> _events = null!;
    protected TimeSpan _clientTimeout = TimeSpan.FromSeconds(30);

    [SetUp]
    public void CreateCollaborators()
    {
        _events = [];
        _clientTimeout = TimeSpan.FromSeconds(30);
        _logger = new CapturingLogger<KeycloakTokenRevocationManager>();
        _handler = new RecordingHandler(_events, (_, _) => Task.FromResult(Response(200, "")));
        _facade = A.Fake<IKeycloakClientFacade>();
        _httpClientFactory = A.Fake<IHttpClientFactory>();
        A.CallTo(() => _httpClientFactory.CreateClient(A<string>._))
            .ReturnsLazily(() =>
                new HttpClient(_handler, disposeHandler: false) { Timeout = _clientTimeout }
            );
        GateAnswers(ConfidentialClient());
    }

    [TearDown]
    public void DisposeCollaborators() => _handler.Dispose();

    protected KeycloakTokenRevocationManager CreateManager() =>
        new(_context, _httpClientFactory, _facade, _logger);

    protected Task<TokenRevocationResult> Revoke(
        TokenTypeHint hint = TokenTypeHint.None,
        CancellationToken cancellationToken = default,
        string clientSecret = CallerSecret
    ) =>
        CreateManager()
            .RevokeTokenAsync(
                new TokenRevocationRequest(CallerClientId, clientSecret, Token, hint),
                cancellationToken
            );

    protected static Client ConfidentialClient(string clientId = CallerClientId) =>
        new() { ClientId = clientId, PublicClient = false };

    protected void GateAnswers(params Client?[] clients) =>
        A.CallTo(() =>
                _facade.GetClientsByClientIdAsync(
                    A<string>._,
                    A<string>._,
                    A<TimeSpan>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(() =>
            {
                _events.Add("client-type-read");
                return Task.FromResult<IEnumerable<Client>>(clients!);
            });

    protected void GateThrows(Exception exception) =>
        A.CallTo(() =>
                _facade.GetClientsByClientIdAsync(
                    A<string>._,
                    A<string>._,
                    A<TimeSpan>._,
                    A<CancellationToken>._
                )
            )
            .ThrowsAsync(exception);

    protected void KeycloakAnswers(Func<HttpResponseMessage> response) =>
        _handler.Respond = (_, _) => Task.FromResult(response());

    protected static HttpResponseMessage Response(int status, string body) =>
        new((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    protected static HttpResponseMessage Response(int status, HttpContent content) =>
        new((HttpStatusCode)status) { Content = content };

    protected static string OAuthError(string error, string description) =>
        JsonSerializer.Serialize(
            new Dictionary<string, string> { ["error"] = error, ["error_description"] = description }
        );

    /// <summary>A dependency exception whose message and inner message carry a value that must never be logged.</summary>
    protected static InvalidOperationException DependencyFailure() =>
        new($"outer {Sentinel} {CallerSecret}", new InvalidOperationException($"inner {Sentinel}"));

    /// <summary>Shaped the way Keycloak.Net raises a refused admin call: Flurl's exception, whose message holds the URL.</summary>
    protected static FlurlHttpException FlurlStatusException(int status) =>
        FlurlException(new HttpResponseMessage((HttpStatusCode)status));

    protected static FlurlHttpException FlurlException(HttpResponseMessage? response, Exception? inner = null)
    {
        string url = $"http://keycloak.test/admin/realms/edfi/clients?clientId={Sentinel}";
        var call = new FlurlCall
        {
            Request = new FlurlRequest(url),
            HttpRequestMessage = new HttpRequestMessage(HttpMethod.Get, url),
            HttpResponseMessage = response,
        };
        if (response is not null)
        {
            call.Response = new FlurlResponse(call);
        }

        return inner is null ? new FlurlHttpException(call) : new FlurlHttpException(call, inner);
    }

    protected static TokenRevocationResult.TemporarilyUnavailable Unavailable(string reason) => new(reason);

    protected void AssertNoDisclosure()
    {
        string logged = _logger.AllText();
        logged.Should().NotContain(Sentinel).And.NotContain(CallerSecret).And.NotContain(ServiceSecret);
        logged.Should().NotContain(Token);
        _logger.Entries.Where(entry => entry.Exception is not null).Should().BeEmpty();
    }

    // ---------------------------------------------------------------------------------------------
    // Request shape (D-09, D-06)
    // ---------------------------------------------------------------------------------------------

    [TestFixture]
    public class Given_a_confidential_caller : KeycloakTokenRevocationManagerTests
    {
        private TokenRevocationResult _result = null!;

        [SetUp]
        public async Task Setup() => _result = await Revoke();

        [Test]
        public void It_completes() => _result.Should().Be(new TokenRevocationResult.Completed());

        [Test]
        public void It_reads_the_client_type_before_revoking() =>
            _events.Should().Equal("client-type-read", "revoke");

        [Test]
        public void It_reads_the_callers_client_type_in_the_configured_realm_within_the_client_timeout() =>
            A.CallTo(() =>
                    _facade.GetClientsByClientIdAsync(
                        "edfi",
                        CallerClientId,
                        _clientTimeout,
                        A<CancellationToken>._
                    )
                )
                .MustHaveHappenedOnceExactly();

        [Test]
        public void It_uses_the_named_keycloak_client() =>
            A.CallTo(() => _httpClientFactory.CreateClient("KeycloakClient")).MustHaveHappenedOnceExactly();

        [Test]
        public void It_sends_exactly_one_post_to_the_realm_revocation_endpoint()
        {
            _handler.Requests.Should().ContainSingle();
            _handler.Requests[0].Method.Should().Be(HttpMethod.Post);
            _handler.Requests[0].Uri.Should().Be(new Uri(RevokeUrl));
        }

        [Test]
        public void It_sends_a_form_body() =>
            _handler.Requests[0].ContentType.Should().Be("application/x-www-form-urlencoded");

        [Test]
        public void It_sends_the_callers_credentials_and_the_token_as_form_fields_only() =>
            _handler
                .Requests[0]
                .Form.Should()
                .Equal(
                    new KeyValuePair<string, string>("client_id", CallerClientId),
                    new KeyValuePair<string, string>("client_secret", CallerSecret),
                    new KeyValuePair<string, string>("token", Token)
                );

        [Test]
        public void It_sends_no_authorization_header() =>
            _handler.Requests[0].Authorization.Should().BeNull();

        [Test]
        public void It_never_sends_the_service_credentials()
        {
            _handler.Requests[0].RawText.Should().NotContain(ServiceSecret);
            _handler.Requests[0].Form.Should().NotContain(pair => pair.Value == ServiceClientId);
        }
    }

    [TestFixture]
    public class Given_a_client_record_that_states_bearerOnly_false : KeycloakTokenRevocationManagerTests
    {
        private TokenRevocationResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            GateAnswers(
                new Client
                {
                    ClientId = CallerClientId,
                    PublicClient = false,
                    BearerOnly = false,
                }
            );
            _result = await Revoke();
        }

        [Test]
        public void It_completes() => _result.Should().Be(new TokenRevocationResult.Completed());

        [Test]
        public void It_delegates() => _handler.Requests.Should().ContainSingle();
    }

    [TestFixture]
    public class Given_a_case_variant_beside_the_callers_client : KeycloakTokenRevocationManagerTests
    {
        [SetUp]
        public async Task Setup()
        {
            GateAnswers(ConfidentialClient(CallerClientId.ToUpperInvariant()), ConfidentialClient());
            await Revoke();
        }

        [Test]
        public void It_delegates_because_exactly_one_client_matches_ordinally() =>
            _handler.Requests.Should().ContainSingle();
    }

    public static IEnumerable<TestFixtureData> ForwardedHints()
    {
        yield return new TestFixtureData(TokenTypeHint.AccessToken, "access_token").SetArgDisplayNames(
            "access_token"
        );
        yield return new TestFixtureData(TokenTypeHint.RefreshToken, "refresh_token").SetArgDisplayNames(
            "refresh_token"
        );
    }

    [TestFixtureSource(typeof(KeycloakTokenRevocationManagerTests), nameof(ForwardedHints))]
    public class Given_a_token_type_hint(TokenTypeHint hint, string expected)
        : KeycloakTokenRevocationManagerTests
    {
        [SetUp]
        public async Task Setup() => await Revoke(hint);

        [Test]
        public void It_forwards_the_hint_after_the_required_fields() =>
            _handler
                .Requests[0]
                .Form.Should()
                .Equal(
                    new KeyValuePair<string, string>("client_id", CallerClientId),
                    new KeyValuePair<string, string>("client_secret", CallerSecret),
                    new KeyValuePair<string, string>("token", Token),
                    new KeyValuePair<string, string>("token_type_hint", expected)
                );
    }

    [TestFixture]
    public class Given_no_token_type_hint : KeycloakTokenRevocationManagerTests
    {
        [SetUp]
        public async Task Setup() => await Revoke(TokenTypeHint.None);

        [Test]
        public void It_does_not_forward_a_hint() =>
            _handler.Requests[0].Form.Should().NotContain(pair => pair.Key == "token_type_hint");
    }

    [TestFixture]
    public class Given_an_empty_secret : KeycloakTokenRevocationManagerTests
    {
        private TokenRevocationResult _result = null!;

        [SetUp]
        public async Task Setup() => _result = await Revoke(clientSecret: "");

        [Test]
        public void It_is_an_invalid_client() =>
            _result.Should().Be(new TokenRevocationResult.InvalidClient());

        [Test]
        public void It_reads_no_client_type() =>
            A.CallTo(() =>
                    _facade.GetClientsByClientIdAsync(
                        A<string>._,
                        A<string>._,
                        A<TimeSpan>._,
                        A<CancellationToken>._
                    )
                )
                .MustNotHaveHappened();

        [Test]
        public void It_sends_no_revoke_request() => _handler.Requests.Should().BeEmpty();
    }

    // ---------------------------------------------------------------------------------------------
    // Client-type gate (D-11.3): every outcome except a single confidential match sends nothing
    // ---------------------------------------------------------------------------------------------

    public static IEnumerable<TestFixtureData> GateOutcomes()
    {
        yield return new TestFixtureData(
            new Client?[] { },
            new TokenRevocationResult.InvalidClient()
        ).SetArgDisplayNames("no client");
        yield return new TestFixtureData(
            new Client?[] { ConfidentialClient(CallerClientId.ToUpperInvariant()) },
            new TokenRevocationResult.InvalidClient()
        ).SetArgDisplayNames("case variant only");
        yield return new TestFixtureData(
            new Client?[] { ConfidentialClient("other-client") },
            new TokenRevocationResult.InvalidClient()
        ).SetArgDisplayNames("different client only");
        yield return new TestFixtureData(
            new Client?[]
            {
                new Client { ClientId = CallerClientId, PublicClient = true },
            },
            new TokenRevocationResult.InvalidClient()
        ).SetArgDisplayNames("public client");
        yield return new TestFixtureData(
            new Client?[]
            {
                new Client
                {
                    ClientId = CallerClientId,
                    PublicClient = false,
                    BearerOnly = true,
                },
            },
            new TokenRevocationResult.InvalidClient()
        ).SetArgDisplayNames("bearer-only client");
        yield return new TestFixtureData(
            new Client?[]
            {
                new Client { ClientId = CallerClientId, BearerOnly = true },
            },
            new TokenRevocationResult.InvalidClient()
        ).SetArgDisplayNames("bearer-only client without publicClient");
        yield return new TestFixtureData(
            new Client?[] { new Client { ClientId = CallerClientId } },
            Unavailable("client-type-unknown")
        ).SetArgDisplayNames("publicClient absent");
        yield return new TestFixtureData(
            new Client?[]
            {
                new Client { ClientId = CallerClientId, BearerOnly = false },
            },
            Unavailable("client-type-unknown")
        ).SetArgDisplayNames("publicClient absent, bearerOnly false");
        yield return new TestFixtureData(
            new Client?[]
            {
                ConfidentialClient(),
                new Client { PublicClient = false },
            },
            Unavailable("client-type-unknown")
        ).SetArgDisplayNames("a record without a client id");
        yield return new TestFixtureData(
            new Client?[] { ConfidentialClient(), null },
            Unavailable("client-type-unknown")
        ).SetArgDisplayNames("a null record");
        yield return new TestFixtureData(
            new Client?[] { ConfidentialClient(), ConfidentialClient() },
            Unavailable("client-type-ambiguous")
        ).SetArgDisplayNames("two ordinal matches");
        yield return new TestFixtureData(null, Unavailable("client-type-unknown")).SetArgDisplayNames(
            "no list"
        );
    }

    [TestFixtureSource(typeof(KeycloakTokenRevocationManagerTests), nameof(GateOutcomes))]
    public class Given_a_client_type_read_that_does_not_prove_a_confidential_client(
        Client?[]? clients,
        TokenRevocationResult expected
    ) : KeycloakTokenRevocationManagerTests
    {
        private TokenRevocationResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            GateAnswers(clients!);
            _result = await Revoke();
        }

        [Test]
        public void It_answers_the_gate_outcome() => _result.Should().Be(expected);

        [Test]
        public void It_sends_no_revoke_request() => _handler.Requests.Should().BeEmpty();

        [Test]
        public void It_logs_an_operational_failure_at_error_only_when_unavailable() =>
            _logger
                .Entries.Exists(entry => entry.Level == LogLevel.Error)
                .Should()
                .Be(expected is TokenRevocationResult.TemporarilyUnavailable);
    }

    public static IEnumerable<TestFixtureData> GateReadFailures()
    {
        yield return new TestFixtureData(
            FlurlStatusException(401),
            "client-type-read-refused"
        ).SetArgDisplayNames("admin 401");
        yield return new TestFixtureData(
            FlurlStatusException(403),
            "client-type-read-refused"
        ).SetArgDisplayNames("admin 403");
        yield return new TestFixtureData(FlurlStatusException(404), "client-type-read").SetArgDisplayNames(
            "admin 404"
        );
        yield return new TestFixtureData(FlurlStatusException(500), "client-type-read").SetArgDisplayNames(
            "admin 500"
        );
        yield return new TestFixtureData(
            FlurlException(null, new HttpRequestException(Sentinel)),
            "client-type-read"
        ).SetArgDisplayNames("admin unreachable");
        yield return new TestFixtureData(
            new FlurlParsingException(
                new FlurlCall
                {
                    Request = new FlurlRequest("http://keycloak.test/admin/realms/edfi/clients"),
                    HttpRequestMessage = new HttpRequestMessage(
                        HttpMethod.Get,
                        "http://keycloak.test/admin/realms/edfi/clients"
                    ),
                },
                "JSON",
                new JsonException(Sentinel)
            ),
            "client-type-read"
        ).SetArgDisplayNames("admin response not deserializable");
        yield return new TestFixtureData(
            new TimeoutException(Sentinel),
            "client-type-timeout"
        ).SetArgDisplayNames("admin read timeout");
        yield return new TestFixtureData(DependencyFailure(), "client-type-read").SetArgDisplayNames(
            "unexpected failure"
        );
    }

    [TestFixtureSource(typeof(KeycloakTokenRevocationManagerTests), nameof(GateReadFailures))]
    public class Given_a_client_type_read_that_fails(Exception failure, string reason)
        : KeycloakTokenRevocationManagerTests
    {
        private TokenRevocationResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            GateThrows(failure);
            _result = await Revoke();
        }

        [Test]
        public void It_is_temporarily_unavailable_never_an_invalid_client() =>
            _result.Should().Be(Unavailable(reason));

        [Test]
        public void It_sends_no_revoke_request() => _handler.Requests.Should().BeEmpty();

        [Test]
        public void It_logs_the_failure_at_error() =>
            _logger.Entries.Should().Contain(entry => entry.Level == LogLevel.Error);

        [Test]
        public void It_logs_only_type_names_never_the_exception_or_its_content() => AssertNoDisclosure();
    }

    [TestFixture]
    public class Given_the_admin_api_refuses_the_client_type_read : KeycloakTokenRevocationManagerTests
    {
        [SetUp]
        public async Task Setup()
        {
            GateThrows(FlurlStatusException(403));
            await Revoke();
        }

        [Test]
        public void It_tells_the_operator_what_to_correct() =>
            _logger
                .Entries.Should()
                .Contain(entry =>
                    entry.Level == LogLevel.Error
                    && entry.Message.Contains("HTTP 403")
                    && entry.Message.Contains("lacks permission to read clients in the realm")
                );
    }

    [TestFixture]
    public class Given_the_caller_cancels_during_the_client_type_read : KeycloakTokenRevocationManagerTests
    {
        private CancellationTokenSource _caller = null!;
        private Exception? _exception;

        [SetUp]
        public async Task Setup()
        {
            _caller = new CancellationTokenSource();
            A.CallTo(() =>
                    _facade.GetClientsByClientIdAsync(
                        A<string>._,
                        A<string>._,
                        A<TimeSpan>._,
                        A<CancellationToken>._
                    )
                )
                .ReturnsLazily(
                    async (string _, string _, TimeSpan _, CancellationToken token) =>
                    {
                        await _caller.CancelAsync();
                        // What Flurl raises for a cancelled call: its own wrapper, URL in the message.
                        return token.IsCancellationRequested
                            ? throw FlurlException(null, new TaskCanceledException(Sentinel, null, token))
                            : Enumerable.Empty<Client>();
                    }
                );
            _exception = await Catch(Revoke(cancellationToken: _caller.Token));
        }

        [TearDown]
        public void TearDown() => _caller.Dispose();

        [Test]
        public void It_rethrows_a_cancellation_for_the_callers_token() =>
            _exception
                .Should()
                .BeAssignableTo<OperationCanceledException>()
                .Which.CancellationToken.Should()
                .Be(_caller.Token);

        [Test]
        public void It_carries_no_provider_or_request_content() =>
            ExceptionText(_exception!).Should().NotContain(Sentinel);

        [Test]
        public void It_sends_no_revoke_request() => _handler.Requests.Should().BeEmpty();

        [Test]
        public void It_logs_nothing() => _logger.Entries.Should().BeEmpty();
    }

    // ---------------------------------------------------------------------------------------------
    // Response mapping (D-10, keyed on the §9.1 strings; everything else fails closed)
    // ---------------------------------------------------------------------------------------------

    public static IEnumerable<TestFixtureData> ResponseMappings()
    {
        TokenRevocationResult completed = new TokenRevocationResult.Completed();
        TokenRevocationResult invalidClient = new TokenRevocationResult.InvalidClient();
        TokenRevocationResult invalidRequest = new TokenRevocationResult.InvalidRequest();

        TestFixtureData Case(string name, int status, string body, TokenRevocationResult expected) =>
            new TestFixtureData(status, body, expected).SetArgDisplayNames(name);

        // Characterized answers (§9.1).
        yield return Case("K-01 200 empty", 200, "", completed);
        yield return Case(
            "K-17 200 invalid_token body",
            200,
            OAuthError("invalid_token", "Invalid token"),
            completed
        );
        yield return Case(
            "K-02 400 ownership mismatch",
            400,
            OAuthError("invalid_request", OwnershipMismatchDescriptionFromEvidence),
            completed
        );
        yield return Case(
            "400 ownership mismatch, members reordered",
            400,
            """{"error_description":"Unmatching clients","error":"invalid_request"}""",
            completed
        );
        yield return Case(
            "K-15 400 unsupported_token_type",
            400,
            OAuthError("unsupported_token_type", "Unsupported token type"),
            new TokenRevocationResult.UnsupportedTokenType()
        );
        yield return Case(
            "K-03 401 invalid_client",
            401,
            OAuthError("invalid_client", "Invalid client or Invalid client credentials"),
            invalidClient
        );
        yield return Case(
            "K-04 401 unauthorized_client",
            401,
            OAuthError("unauthorized_client", "Invalid client or Invalid client credentials"),
            invalidClient
        );
        yield return Case(
            "400 invalid_client bearer-only",
            400,
            OAuthError("invalid_client", "Bearer-only not allowed"),
            invalidClient
        );
        yield return Case(
            "400 unauthorized_client",
            400,
            OAuthError("unauthorized_client", "Invalid client or Invalid client credentials"),
            invalidClient
        );
        yield return Case(
            "K-09 400 duplicated parameter",
            400,
            OAuthError("invalid_request", "duplicated parameter"),
            invalidRequest
        );
        yield return Case(
            "K-22 400 token not provided",
            400,
            OAuthError("invalid_request", "Token not provided"),
            invalidRequest
        );

        // The ownership normalization is exact on both members and on the status.
        yield return Case(
            "400 mismatch description in another case",
            400,
            OAuthError("invalid_request", "unmatching clients"),
            invalidRequest
        );
        yield return Case(
            "400 mismatch description with a trailing space",
            400,
            OAuthError("invalid_request", "Unmatching clients "),
            invalidRequest
        );
        yield return Case(
            "400 mismatch description as a prefix",
            400,
            OAuthError("invalid_request", "Unmatching clients."),
            invalidRequest
        );
        yield return Case(
            "400 invalid_request without description",
            400,
            """{"error":"invalid_request"}""",
            invalidRequest
        );
        yield return Case(
            "400 invalid_request with a non-string description",
            400,
            """{"error":"invalid_request","error_description":7}""",
            invalidRequest
        );
        yield return Case(
            "401 mismatch body",
            401,
            OAuthError("invalid_request", "Unmatching clients"),
            Unavailable("unrecognized-response")
        );
        yield return Case(
            "400 mismatch description under another error",
            400,
            OAuthError("invalid_token", "Unmatching clients"),
            Unavailable("unrecognized-response")
        );
        yield return Case(
            "400 error in another case",
            400,
            OAuthError("INVALID_REQUEST", "Unmatching clients"),
            Unavailable("unrecognized-response")
        );

        // Uncharacterized answers at a mapped status.
        yield return Case(
            "401 unsupported_token_type",
            401,
            OAuthError("unsupported_token_type", "Unsupported token type"),
            Unavailable("unrecognized-response")
        );
        yield return Case(
            "400 invalid_token",
            400,
            OAuthError("invalid_token", "Invalid token"),
            Unavailable("unrecognized-response")
        );
        yield return Case(
            "400 non-string error",
            400,
            """{"error":42}""",
            Unavailable("unrecognized-response")
        );
        yield return Case("400 empty object", 400, "{}", Unavailable("unrecognized-response"));

        // Unparseable bodies at a mapped status.
        yield return Case("400 empty body", 400, "", Unavailable("unparseable"));
        yield return Case("400 not JSON", 400, "Bad Request", Unavailable("unparseable"));
        yield return Case(
            "400 JSON array",
            400,
            """[{"error":"invalid_request"}]""",
            Unavailable("unparseable")
        );
        yield return Case("400 JSON string", 400, "\"invalid_request\"", Unavailable("unparseable"));
        yield return Case(
            "400 truncated object",
            400,
            """{"error":"invalid_request","error_description":"Unmatching clients" """,
            Unavailable("unparseable")
        );
        yield return Case(
            "400 duplicated error member",
            400,
            """{"error":"invalid_client","error":"invalid_request","error_description":"Unmatching clients"}""",
            Unavailable("unparseable")
        );
        yield return Case(
            "400 duplicated description member",
            400,
            """{"error":"invalid_request","error_description":"x","error_description":"Unmatching clients"}""",
            Unavailable("unparseable")
        );

        // Every other status.
        yield return Case(
            "403 HTTPS required",
            403,
            OAuthError("invalid_request", "HTTPS required"),
            Unavailable("provider-status")
        );
        yield return Case(
            "403 with an authentication error",
            403,
            OAuthError("invalid_client", "Invalid client or Invalid client credentials"),
            Unavailable("provider-status")
        );
        yield return Case(
            "404 wrong realm",
            404,
            """{"error":"Realm does not exist"}""",
            Unavailable("provider-status")
        );
        yield return Case("500", 500, "", Unavailable("provider-status"));
        yield return Case("502 not JSON", 502, "<html>Bad Gateway</html>", Unavailable("provider-status"));
        yield return Case(
            "503 temporarily_unavailable",
            503,
            OAuthError("temporarily_unavailable", "down"),
            Unavailable("provider-status")
        );
        yield return Case("204 no content", 204, "", Unavailable("provider-status"));
    }

    /// <summary>Copied from §9.1 (K-02, K-08, K-20), not from the production constant, so a drift in either fails.</summary>
    private const string OwnershipMismatchDescriptionFromEvidence = "Unmatching clients";

    [TestFixtureSource(typeof(KeycloakTokenRevocationManagerTests), nameof(ResponseMappings))]
    public class Given_a_keycloak_answer(int status, string body, TokenRevocationResult expected)
        : KeycloakTokenRevocationManagerTests
    {
        private TokenRevocationResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            KeycloakAnswers(() => Response(status, body));
            _result = await Revoke();
        }

        [Test]
        public void It_maps_to_the_expected_result() => _result.Should().Be(expected);

        [Test]
        public void It_sent_exactly_one_revoke_request() => _handler.Requests.Should().ContainSingle();

        [Test]
        public void It_logs_an_operational_failure_at_error_only_when_unavailable() =>
            _logger
                .Entries.Exists(entry => entry.Level == LogLevel.Error)
                .Should()
                .Be(expected is TokenRevocationResult.TemporarilyUnavailable);
    }

    [TestFixture]
    public class Given_the_production_ownership_constant : KeycloakTokenRevocationManagerTests
    {
        [Test]
        public void It_is_the_string_keycloak_was_observed_to_answer() =>
            KeycloakTokenRevocationManager
                .OwnershipMismatchDescription.Should()
                .Be(OwnershipMismatchDescriptionFromEvidence);
    }

    [TestFixture]
    public class Given_a_provider_status_with_an_allowlisted_error : KeycloakTokenRevocationManagerTests
    {
        [SetUp]
        public async Task Setup()
        {
            KeycloakAnswers(() => Response(503, OAuthError("temporarily_unavailable", "down")));
            await Revoke();
        }

        [Test]
        public void It_logs_the_status_code_and_the_error_category() =>
            _logger
                .Entries.Should()
                .Contain(entry =>
                    entry.Level == LogLevel.Error
                    && entry.Message.Contains("HTTP 503")
                    && entry.Message.Contains("(temporarily_unavailable)")
                );
    }

    [TestFixture]
    public class Given_a_body_that_is_not_utf8 : KeycloakTokenRevocationManagerTests
    {
        private TokenRevocationResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            byte[] body =
            [
                .. Encoding.UTF8.GetBytes(
                    """{"error":"invalid_request","error_description":"Unmatching clients"""
                ),
                0xFF,
                .. "\"}"u8,
            ];
            KeycloakAnswers(() => Response(400, new ByteArrayContent(body)));
            _result = await Revoke();
        }

        [Test]
        public void It_is_unparseable() => _result.Should().Be(Unavailable("unparseable"));
    }

    /// <summary>
    /// Escaped, unpaired UTF-16 surrogates. Each body is pure ASCII and syntactically valid JSON, so it
    /// passes the UTF-8 check and <see cref="JsonDocument.Parse(ReadOnlyMemory{byte}, JsonDocumentOptions)"/>;
    /// only reading the string fails. The payloads are raw literals, never produced by a serializer that
    /// could replace the invalid escape.
    /// </summary>
    public static IEnumerable<TestFixtureData> MalformedUnicodeEscapes()
    {
        TestFixtureData Case(string name, int status, string body, TokenRevocationResult expected) =>
            new TestFixtureData(status, body, expected).SetArgDisplayNames(name);

        TokenRevocationResult unparseable = Unavailable("unparseable");

        yield return Case(
            "400 lone high surrogate in error",
            400,
            """{"error":"\uD800","error_description":"Unmatching clients"}""",
            unparseable
        );
        yield return Case(
            "400 lone low surrogate in error",
            400,
            """{"error":"\uDC00SECRET-PROVIDER-SENTINEL","error_description":"Unmatching clients"}""",
            unparseable
        );
        yield return Case(
            "400 lone high surrogate in error_description",
            400,
            """{"error":"invalid_request","error_description":"SECRET-PROVIDER-SENTINEL\uD800"}""",
            unparseable
        );
        yield return Case(
            "400 lone low surrogate in error_description",
            400,
            """{"error":"invalid_request","error_description":"\uDC00SECRET-PROVIDER-SENTINEL"}""",
            unparseable
        );
        yield return Case(
            "400 high surrogate followed by an ordinary character",
            400,
            """{"error":"invalid_request","error_description":"Unmatching clients\uD800x"}""",
            unparseable
        );
        yield return Case(
            "400 surrogates in reversed order",
            400,
            """{"error":"invalid_request","error_description":"\uDC00\uD800"}""",
            unparseable
        );
        yield return Case(
            "401 lone high surrogate in error",
            401,
            """{"error":"\uD800SECRET-PROVIDER-SENTINEL"}""",
            unparseable
        );
        // At an unmapped status the body is parsed only for the log category.
        yield return Case(
            "500 lone high surrogate in error",
            500,
            """{"error":"\uD800SECRET-PROVIDER-SENTINEL"}""",
            Unavailable("provider-status")
        );

        // Undecodable member names: any one makes the body unparseable, because it could be either member.
        yield return Case(
            "400 lone high surrogate as a name before error",
            400,
            """{"\uD800":0,"error":"invalid_request"}""",
            unparseable
        );
        yield return Case(
            "400 lone low surrogate as an unrelated name beside an ownership mismatch",
            400,
            """{"\uDC00":"SECRET-PROVIDER-SENTINEL","error":"invalid_request","error_description":"Unmatching clients"}""",
            unparseable
        );
        yield return Case(
            "400 malformed unrelated name after the recognized members",
            400,
            """{"error":"invalid_request","error_description":"Unmatching clients","x\uD800":"SECRET-PROVIDER-SENTINEL"}""",
            unparseable
        );
        yield return Case(
            "400 lone low surrogate inside a name resembling error",
            400,
            """{"error\uDC00":"invalid_client","error_description":"Unmatching clients"}""",
            unparseable
        );
        yield return Case(
            "401 lone high surrogate as a name before invalid_client",
            401,
            """{"\uD800":0,"error":"invalid_client"}""",
            unparseable
        );
        yield return Case(
            "500 lone high surrogate as a name",
            500,
            """{"\uD800SECRET-PROVIDER-SENTINEL":0,"error":"server_error"}""",
            Unavailable("provider-status")
        );

        // Controls: well-formed escapes still decode and map as before.
        yield return Case(
            "400 valid surrogate pair in error_description",
            400,
            """{"error":"invalid_request","error_description":"\uD83D\uDE00"}""",
            new TokenRevocationResult.InvalidRequest()
        );
        yield return Case(
            "400 valid surrogate pair in error",
            400,
            """{"error":"\uD83D\uDE00","error_description":"Unmatching clients"}""",
            Unavailable("unrecognized-response")
        );
        yield return Case(
            "400 ownership mismatch written with escapes",
            400,
            """{"error":"invalid\u005Frequest","error_description":"Unmatching\u0020clients"}""",
            new TokenRevocationResult.Completed()
        );
        yield return Case(
            "400 ownership mismatch with escaped member names",
            400,
            """{"\u0065rror":"invalid_request","\u0065rror_description":"Unmatching clients"}""",
            new TokenRevocationResult.Completed()
        );
        yield return Case(
            "401 invalid_client under an escaped error name",
            401,
            """{"\u0065rror":"invalid_client","error_description":"x"}""",
            new TokenRevocationResult.InvalidClient()
        );
        yield return Case(
            "400 valid surrogate pair as an unrelated name",
            400,
            """{"\uD83D\uDE00":0,"error":"invalid_request","error_description":"Unmatching clients"}""",
            new TokenRevocationResult.Completed()
        );
        yield return Case(
            "400 error repeated through an escaped name",
            400,
            """{"error":"invalid_client","\u0065rror":"invalid_request","error_description":"Unmatching clients"}""",
            unparseable
        );
    }

    [TestFixtureSource(typeof(KeycloakTokenRevocationManagerTests), nameof(MalformedUnicodeEscapes))]
    public class Given_a_keycloak_answer_with_unicode_escapes(
        int status,
        string body,
        TokenRevocationResult expected
    ) : KeycloakTokenRevocationManagerTests
    {
        private TokenRevocationResult? _result;
        private Exception? _exception;

        [SetUp]
        public async Task Setup()
        {
            KeycloakAnswers(() => Response(status, body));
            try
            {
                _result = await Revoke();
            }
            catch (Exception ex)
            {
                _exception = ex;
            }
        }

        [Test]
        public void It_sends_the_escape_text_to_the_parser_unchanged() =>
            body.Should().Contain(@"\u").And.Match(text => text.All(char.IsAscii));

        [Test]
        public void It_lets_no_exception_escape() => _exception.Should().BeNull();

        [Test]
        public void It_maps_to_the_expected_result() => _result.Should().Be(expected);

        [Test]
        public void It_logs_no_provider_content_and_attaches_no_exception() => AssertNoDisclosure();
    }

    // ---------------------------------------------------------------------------------------------
    // Bounded reading (D-09)
    // ---------------------------------------------------------------------------------------------

    protected static string PaddedOwnershipMismatch(int totalBytes)
    {
        string json = OAuthError("invalid_request", "Unmatching clients");
        return json[..^1] + new string(' ', totalBytes - Encoding.UTF8.GetByteCount(json)) + "}";
    }

    [TestFixture]
    public class Given_an_error_body_of_exactly_the_cap : KeycloakTokenRevocationManagerTests
    {
        private TokenRevocationResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            string body = PaddedOwnershipMismatch(KeycloakTokenRevocationManager.MaxResponseBodyBytes);
            KeycloakAnswers(() =>
                Response(400, new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(body))))
            );
            _result = await Revoke();
        }

        [Test]
        public void It_reads_and_maps_it() => _result.Should().Be(new TokenRevocationResult.Completed());
    }

    [TestFixture]
    public class Given_an_error_body_one_byte_over_the_cap_without_a_length
        : KeycloakTokenRevocationManagerTests
    {
        private TokenRevocationResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            string body = PaddedOwnershipMismatch(KeycloakTokenRevocationManager.MaxResponseBodyBytes + 1);
            // StreamContent over a non-seekable stream carries no Content-Length, so the cap is
            // enforced while reading.
            KeycloakAnswers(() =>
                Response(400, new StreamContent(new ProbeStream(Encoding.UTF8.GetBytes(body))))
            );
            _result = await Revoke();
        }

        [Test]
        public void It_is_unparseable() => _result.Should().Be(Unavailable("unparseable"));
    }

    [TestFixture]
    public class Given_an_error_body_that_declares_a_length_over_the_cap : KeycloakTokenRevocationManagerTests
    {
        private TokenRevocationResult _result = null!;
        private ProbeStream _stream = null!;

        [SetUp]
        public async Task Setup()
        {
            _stream = new ProbeStream(
                Encoding.UTF8.GetBytes(OAuthError("invalid_request", "Unmatching clients"))
            );
            KeycloakAnswers(() =>
            {
                var content = new StreamContent(_stream);
                content.Headers.ContentLength = KeycloakTokenRevocationManager.MaxResponseBodyBytes + 1;
                return Response(400, content);
            });
            _result = await Revoke();
        }

        [Test]
        public void It_is_unparseable() => _result.Should().Be(Unavailable("unparseable"));

        [TearDown]
        public void DisposeStream() => _stream.Dispose();

        [Test]
        public void It_does_not_read_the_body() => _stream.WasRead.Should().BeFalse();
    }

    [TestFixture]
    public class Given_a_200_whose_body_cannot_be_read : KeycloakTokenRevocationManagerTests
    {
        private TokenRevocationResult _result = null!;
        private ProbeStream _stream = null!;

        [SetUp]
        public async Task Setup()
        {
            _stream = ProbeStream.Failing(new IOException(Sentinel));
            KeycloakAnswers(() => Response(200, new StreamContent(_stream)));
            _result = await Revoke();
        }

        [Test]
        public void It_completes_on_the_status_line() =>
            _result.Should().Be(new TokenRevocationResult.Completed());

        [TearDown]
        public void DisposeStream() => _stream.Dispose();

        [Test]
        public void It_does_not_read_the_body() => _stream.WasRead.Should().BeFalse();
    }

    // ---------------------------------------------------------------------------------------------
    // Transport failures, timeout and cancellation (D-09, D-13.3)
    // ---------------------------------------------------------------------------------------------

    public static IEnumerable<TestFixtureData> TransportFailures()
    {
        yield return new TestFixtureData(
            new HttpRequestException(HttpRequestError.ConnectionError, Sentinel),
            "unreachable"
        ).SetArgDisplayNames("connection refused");
        yield return new TestFixtureData(
            new HttpRequestException(HttpRequestError.NameResolutionError, Sentinel),
            "unreachable"
        ).SetArgDisplayNames("name resolution");
        yield return new TestFixtureData(
            new HttpRequestException(HttpRequestError.SecureConnectionError, Sentinel),
            "unreachable"
        ).SetArgDisplayNames("TLS");
        // The request was written and the connection dropped before the answer: Keycloak may
        // already have revoked.
        yield return new TestFixtureData(
            new HttpRequestException(HttpRequestError.ResponseEnded, Sentinel, new IOException(Sentinel)),
            "response-lost"
        ).SetArgDisplayNames("response ended after the request was sent");
        yield return new TestFixtureData(
            new HttpRequestException(HttpRequestError.InvalidResponse, Sentinel),
            "response-lost"
        ).SetArgDisplayNames("invalid response");
        yield return new TestFixtureData(DependencyFailure(), "unexpected").SetArgDisplayNames(
            "unexpected failure"
        );
    }

    /// <summary>
    /// The handler records the request before failing, so each case is a request that left CMS.
    /// No case asserts anything about the token's final state, which CMS cannot know (D-13.3).
    /// </summary>
    [TestFixtureSource(typeof(KeycloakTokenRevocationManagerTests), nameof(TransportFailures))]
    public class Given_the_revoke_request_fails_in_transport(Exception failure, string reason)
        : KeycloakTokenRevocationManagerTests
    {
        private TokenRevocationResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _handler.Respond = (_, _) => Task.FromException<HttpResponseMessage>(failure);
            _result = await Revoke();
        }

        [Test]
        public void It_is_temporarily_unavailable() => _result.Should().Be(Unavailable(reason));

        [Test]
        public void It_had_sent_the_request() => _handler.Requests.Should().ContainSingle();

        [Test]
        public void It_logs_only_type_names_never_the_exception_or_its_content() => AssertNoDisclosure();
    }

    [TestFixture]
    public class Given_an_error_body_that_fails_while_being_read : KeycloakTokenRevocationManagerTests
    {
        private TokenRevocationResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            KeycloakAnswers(() =>
                Response(
                    400,
                    new StreamContent(
                        ProbeStream.Failing(
                            new IOException(Sentinel),
                            Encoding.UTF8.GetBytes("""{"error":"inv""")
                        )
                    )
                )
            );
            _result = await Revoke();
        }

        [Test]
        public void It_is_a_lost_response() => _result.Should().Be(Unavailable("response-lost"));

        [Test]
        public void It_logs_only_type_names_never_the_exception_or_its_content() => AssertNoDisclosure();
    }

    /// <summary>
    /// The handler waits for its token and nothing else, so only the client's timeout can end the
    /// call: the outcome does not depend on how long the timer takes to fire.
    /// </summary>
    [TestFixture]
    public class Given_keycloak_does_not_answer_within_the_client_timeout
        : KeycloakTokenRevocationManagerTests
    {
        private TokenRevocationResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _clientTimeout = TimeSpan.FromMilliseconds(50);
            _handler.Respond = async (_, token) =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return Response(200, "");
            };
            _result = await Revoke();
        }

        [Test]
        public void It_is_temporarily_unavailable() => _result.Should().Be(Unavailable("timeout"));

        [Test]
        public void It_had_sent_the_request() => _handler.Requests.Should().ContainSingle();

        /// <summary>A-03: on .NET 10 an <see cref="HttpClient"/> timeout is a cancellation with a <see cref="TimeoutException"/> inside.</summary>
        [Test]
        public void It_logs_the_http_client_timeout_shape() =>
            _logger
                .Entries.Should()
                .Contain(entry =>
                    entry.Message.Contains(
                        "System.Threading.Tasks.TaskCanceledException -> System.TimeoutException"
                    )
                );
    }

    [TestFixture]
    public class Given_an_error_body_that_stalls : KeycloakTokenRevocationManagerTests
    {
        private TokenRevocationResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _clientTimeout = TimeSpan.FromMilliseconds(50);
            KeycloakAnswers(() => Response(400, new StreamContent(ProbeStream.Stalling())));
            _result = await Revoke();
        }

        [Test]
        public void It_bounds_the_read_and_reports_a_lost_response() =>
            _result.Should().Be(Unavailable("response-lost"));
    }

    [TestFixture]
    public class Given_the_caller_cancels_while_waiting_for_keycloak : KeycloakTokenRevocationManagerTests
    {
        private CancellationTokenSource _caller = null!;
        private Exception? _exception;

        [SetUp]
        public async Task Setup()
        {
            _clientTimeout = Timeout.InfiniteTimeSpan;
            _caller = new CancellationTokenSource();
            _handler.Respond = async (_, token) =>
            {
                await _caller.CancelAsync();
                await Task.Delay(Timeout.Infinite, token);
                return Response(200, "");
            };
            _exception = await Catch(Revoke(cancellationToken: _caller.Token));
        }

        [TearDown]
        public void TearDown() => _caller.Dispose();

        [Test]
        public void It_rethrows_a_cancellation_for_the_callers_token() =>
            _exception
                .Should()
                .BeAssignableTo<OperationCanceledException>()
                .Which.CancellationToken.Should()
                .Be(_caller.Token);

        [Test]
        public void It_wraps_nothing() => _exception!.InnerException.Should().BeNull();

        [Test]
        public void It_logs_no_failure() =>
            _logger.Entries.Should().NotContain(entry => entry.Level >= LogLevel.Warning);
    }

    [TestFixture]
    public class Given_the_caller_cancels_while_the_error_body_is_read : KeycloakTokenRevocationManagerTests
    {
        private CancellationTokenSource _caller = null!;
        private Exception? _exception;

        [SetUp]
        public async Task Setup()
        {
            _clientTimeout = Timeout.InfiniteTimeSpan;
            _caller = new CancellationTokenSource();
            KeycloakAnswers(() =>
                Response(400, new StreamContent(ProbeStream.Stalling(onRead: () => _caller.Cancel())))
            );
            _exception = await Catch(Revoke(cancellationToken: _caller.Token));
        }

        [TearDown]
        public void TearDown() => _caller.Dispose();

        [Test]
        public void It_rethrows_a_cancellation_for_the_callers_token() =>
            _exception
                .Should()
                .BeAssignableTo<OperationCanceledException>()
                .Which.CancellationToken.Should()
                .Be(_caller.Token);

        [Test]
        public void It_logs_no_failure() =>
            _logger.Entries.Should().NotContain(entry => entry.Level >= LogLevel.Warning);
    }

    // ---------------------------------------------------------------------------------------------
    // Disclosure (D-15): provider content never reaches a log, a result or an exception
    // ---------------------------------------------------------------------------------------------

    public static IEnumerable<TestFixtureData> ProviderBodiesCarryingASentinel()
    {
        yield return new TestFixtureData(
            400,
            JsonSerializer.Serialize(
                new
                {
                    error = Sentinel,
                    error_description = Sentinel,
                    extra = Sentinel,
                }
            )
        ).SetArgDisplayNames("400 sentinel in every member");
        yield return new TestFixtureData(400, OAuthError("invalid_request", Sentinel)).SetArgDisplayNames(
            "400 invalid_request with a sentinel description"
        );
        yield return new TestFixtureData(401, OAuthError("invalid_client", Sentinel)).SetArgDisplayNames(
            "401 invalid_client with a sentinel description"
        );
        yield return new TestFixtureData(500, OAuthError(Sentinel, Sentinel)).SetArgDisplayNames(
            "500 sentinel error"
        );
        yield return new TestFixtureData(400, Sentinel).SetArgDisplayNames("400 non-JSON sentinel body");
        yield return new TestFixtureData(400, $$"""{"error":"{{Sentinel}}" """).SetArgDisplayNames(
            "400 malformed JSON carrying a sentinel"
        );
        yield return new TestFixtureData(200, OAuthError(Sentinel, Sentinel)).SetArgDisplayNames(
            "200 with a sentinel body"
        );
    }

    [TestFixtureSource(typeof(KeycloakTokenRevocationManagerTests), nameof(ProviderBodiesCarryingASentinel))]
    public class Given_a_provider_body_carrying_a_sentinel(int status, string body)
        : KeycloakTokenRevocationManagerTests
    {
        private TokenRevocationResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            KeycloakAnswers(() => Response(status, body));
            _result = await Revoke();
        }

        [Test]
        public void It_logs_neither_the_body_nor_any_credential_or_token() => AssertNoDisclosure();

        [Test]
        public void It_returns_no_provider_content() => _result.ToString().Should().NotContain(Sentinel);
    }

    [TestFixture]
    public class Given_an_unrecognized_provider_error : KeycloakTokenRevocationManagerTests
    {
        [SetUp]
        public async Task Setup()
        {
            KeycloakAnswers(() => Response(500, OAuthError(Sentinel, "x")));
            await Revoke();
        }

        [Test]
        public void It_logs_the_fixed_category_instead_of_the_value() =>
            _logger
                .Entries.Should()
                .Contain(entry => entry.Level == LogLevel.Error && entry.Message.Contains("(unrecognized)"));
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    protected static async Task<Exception?> Catch(Task task)
    {
        try
        {
            await task;
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    protected static string ExceptionText(Exception exception)
    {
        var text = new StringBuilder();
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            text.AppendLine(current.Message).AppendLine(current.ToString());
        }

        return text.ToString();
    }

    protected sealed record RecordedRequest(
        HttpMethod Method,
        Uri? Uri,
        string? Authorization,
        string? ContentType,
        IReadOnlyList<KeyValuePair<string, string>> Form,
        string RawText
    );

    /// <summary>Records each request, including its form body, before handing it to <see cref="Respond"/>.</summary>
    protected sealed class RecordingHandler(
        List<string> events,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond
    ) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } =
            respond;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            string body = request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken);
            List<KeyValuePair<string, string>> form =
            [
                .. body.Split('&', StringSplitOptions.RemoveEmptyEntries)
                    .Select(pair => pair.Split('=', 2))
                    .Select(parts => new KeyValuePair<string, string>(
                        WebUtility.UrlDecode(parts[0]),
                        WebUtility.UrlDecode(parts.Length > 1 ? parts[1] : "")
                    )),
            ];
            Requests.Add(
                new RecordedRequest(
                    request.Method,
                    request.RequestUri,
                    request.Headers.Authorization?.ToString(),
                    request.Content?.Headers.ContentType?.MediaType,
                    form,
                    $"{request.RequestUri} {request.Headers} {request.Content?.Headers} {body}"
                )
            );
            events.Add("revoke");
            return await Respond(request, cancellationToken);
        }
    }

    /// <summary>A non-seekable body that records whether it was read and can fail or stall on demand.</summary>
    protected sealed class ProbeStream(
        byte[] data,
        Exception? failure = null,
        bool stall = false,
        Action? onRead = null
    ) : Stream
    {
        private int _position;

        public bool WasRead { get; private set; }

        public static ProbeStream Failing(Exception failure, byte[]? before = null) =>
            new(before ?? [], failure);

        public static ProbeStream Stalling(Action? onRead = null) => new([], stall: true, onRead: onRead);

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            WasRead = true;
            onRead?.Invoke();
            if (_position < data.Length)
            {
                int count = Math.Min(buffer.Length, data.Length - _position);
                data.AsMemory(_position, count).CopyTo(buffer);
                _position += count;
                return count;
            }

            if (stall)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            if (failure is not null)
            {
                throw failure;
            }

            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    protected sealed record CapturedLogEntry(
        LogLevel Level,
        string Message,
        IReadOnlyList<KeyValuePair<string, object?>> State,
        IReadOnlyList<object?> Scopes,
        Exception? Exception
    );

    /// <summary>
    /// Records every field a sink could write: the rendered message, every state value, every active
    /// scope and the attached exception with its inner chain (D-15).
    /// </summary>
    protected sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<object?> _scopes = [];

        public List<CapturedLogEntry> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull
        {
            _scopes.Add(state);
            return new ScopeExit(() => _scopes.Remove(state));
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) =>
            Entries.Add(
                new CapturedLogEntry(
                    logLevel,
                    formatter(state, exception),
                    state is IEnumerable<KeyValuePair<string, object?>> pairs ? [.. pairs] : [],
                    [.. _scopes],
                    exception
                )
            );

        public string AllText()
        {
            var text = new StringBuilder();
            foreach (CapturedLogEntry entry in Entries)
            {
                text.AppendLine(entry.Message);
                foreach (KeyValuePair<string, object?> pair in entry.State)
                {
                    text.AppendLine(pair.Value?.ToString());
                }

                foreach (object? scope in entry.Scopes)
                {
                    text.AppendLine(scope?.ToString());
                }

                for (
                    Exception? current = entry.Exception;
                    current is not null;
                    current = current.InnerException
                )
                {
                    text.AppendLine(current.ToString());
                }
            }

            return text.ToString();
        }

        private sealed class ScopeExit(Action exit) : IDisposable
        {
            public void Dispose() => exit();
        }
    }
}
