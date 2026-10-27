// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend.Keycloak;
using EdFi.DmsConfigurationService.DataModel;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit;

/// <summary>
/// RFC 7662 introspection delegated to Keycloak by <see cref="KeycloakTokenIntrospectionManager"/>. The
/// request goes through a real <see cref="HttpClient"/> over a recording handler, so the request shape,
/// the client's timeout and the response handling are the production ones. Keycloak's answers are the
/// ones recorded on 26.1.4 and 26.7.5 by <c>KeycloakIntrospectionCharacterizationTests</c> (rows I-00 to
/// I-27), and the logging rules are the revocation manager's (DMS-1327 D-15).
/// </summary>
public class KeycloakTokenIntrospectionManagerTests
{
    protected const string CallerClientId = "caller-client";
    protected const string OtherClientId = "other-client";
    protected const string CallerSecret = "SECRET-CALLER-SENTINEL";
    protected const string ServiceClientId = "cms-service";
    protected const string ServiceSecret = "SECRET-SERVICE-SENTINEL";
    protected const string Token = "SECRET-TOKEN-SENTINEL.payload.signature";
    protected const string Sentinel = "SECRET-PROVIDER-SENTINEL";
    protected const string IntrospectUrl =
        "http://keycloak.test/realms/edfi/protocol/openid-connect/token/introspect";
    protected const string Issuer = "http://keycloak.test/realms/edfi";
    protected const string Subject = "9f0c5a1e-subject";
    protected const long Exp = 1_792_607_774;
    protected const long Iat = 1_792_605_974;
    protected const string InactiveAnswer = """{"active":false}""";

    protected static readonly KeycloakContext _context = new(
        "http://keycloak.test",
        "edfi",
        ServiceClientId,
        ServiceSecret,
        "role"
    );

    protected RecordingHandler _handler = null!;
    protected IHttpClientFactory _httpClientFactory = null!;
    protected KeycloakCapturingLogger<KeycloakTokenIntrospectionManager> _logger = null!;
    protected TimeSpan _clientTimeout = TimeSpan.FromSeconds(30);

    [SetUp]
    public void CreateCollaborators()
    {
        _clientTimeout = TimeSpan.FromSeconds(30);
        _logger = new KeycloakCapturingLogger<KeycloakTokenIntrospectionManager>();
        _handler = new RecordingHandler((_, _) => Task.FromResult(Response(200, InactiveAnswer)));
        _httpClientFactory = A.Fake<IHttpClientFactory>();
        A.CallTo(() => _httpClientFactory.CreateClient(A<string>._))
            .ReturnsLazily(() =>
                new HttpClient(_handler, disposeHandler: false) { Timeout = _clientTimeout }
            );
    }

    [TearDown]
    public void DisposeCollaborators() => _handler.Dispose();

    protected KeycloakTokenIntrospectionManager CreateManager() => new(_context, _httpClientFactory, _logger);

    protected Task<TokenIntrospectionResult> Introspect(
        TokenTypeHint hint = TokenTypeHint.None,
        CancellationToken cancellationToken = default,
        string clientId = CallerClientId,
        string clientSecret = CallerSecret
    ) =>
        CreateManager()
            .IntrospectTokenAsync(
                new TokenIntrospectionRequest(clientId, clientSecret, Token, hint),
                cancellationToken
            );

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

    /// <summary>
    /// The answer Keycloak gave an access token's owner on both characterized versions (I-01, I-02,
    /// I-05): every member recorded there, in Keycloak's order, with a sentinel in members CMS must
    /// never disclose. <paramref name="change"/> edits it for one case.
    /// </summary>
    protected static string Answer(Action<JsonObject>? change = null)
    {
        JsonObject body = new()
        {
            ["exp"] = Exp,
            ["iat"] = Iat,
            ["jti"] = $"jti-{Sentinel}",
            ["iss"] = Issuer,
            ["aud"] = "account",
            ["sub"] = Subject,
            ["typ"] = "Bearer",
            ["azp"] = CallerClientId,
            ["acr"] = "1",
            ["realm_access"] = new JsonObject { ["roles"] = new JsonArray(Sentinel) },
            ["resource_access"] = new JsonObject
            {
                ["account"] = new JsonObject { ["roles"] = new JsonArray(Sentinel) },
            },
            ["scope"] = "profile email",
            ["namespacePrefixes"] = Sentinel,
            ["email_verified"] = false,
            ["preferred_username"] = $"service-account-{CallerClientId}",
            ["client_id"] = CallerClientId,
            ["username"] = $"service-account-{CallerClientId}",
            ["token_type"] = "Bearer",
            ["active"] = true,
        };
        change?.Invoke(body);
        return body.ToJsonString();
    }

    protected static IntrospectedToken OwnedToken(
        IReadOnlyList<string>? aud = null,
        string? sub = Subject,
        string? iss = Issuer,
        string scope = "profile email"
    ) => new(CallerClientId, scope, Exp, Iat, sub, aud ?? ["account"], iss);

    protected static TokenIntrospectionResult.Active Active(IntrospectedToken token) => new(token);

    protected static TokenIntrospectionResult.TemporarilyUnavailable Unavailable(string reason) =>
        new(reason);

    /// <summary>
    /// Records compare a list member by reference, so an active result is compared member by member,
    /// the audience in order.
    /// </summary>
    protected static void AssertResult(TokenIntrospectionResult actual, TokenIntrospectionResult expected)
    {
        if (expected is TokenIntrospectionResult.Active active)
        {
            actual
                .Should()
                .BeOfType<TokenIntrospectionResult.Active>()
                .Which.Token.Should()
                .BeEquivalentTo(
                    active.Token,
                    options => options.ComparingByMembers<IntrospectedToken>().WithStrictOrdering()
                );
        }
        else
        {
            actual.Should().Be(expected);
        }
    }

    /// <summary>A dependency exception whose message and inner message carry a value that must never be logged.</summary>
    protected static InvalidOperationException DependencyFailure() =>
        new($"outer {Sentinel} {CallerSecret}", new InvalidOperationException($"inner {Sentinel}"));

    protected void AssertNoDisclosure()
    {
        string logged = _logger.AllText();
        logged.Should().NotContain(Sentinel).And.NotContain(CallerSecret).And.NotContain(ServiceSecret);
        logged.Should().NotContain(Token);
        _logger.Entries.Where(entry => entry.Exception is not null).Should().BeEmpty();
    }

    // ---------------------------------------------------------------------------------------------
    // Request shape: one form POST carrying only the caller's credentials and the token
    // ---------------------------------------------------------------------------------------------

    [TestFixture]
    public class Given_a_confidential_caller : KeycloakTokenIntrospectionManagerTests
    {
        private TokenIntrospectionResult _result = null!;

        [SetUp]
        public async Task Setup() => _result = await Introspect();

        [Test]
        public void It_maps_keycloaks_inactive_answer() =>
            _result.Should().Be(new TokenIntrospectionResult.Inactive());

        [Test]
        public void It_uses_the_named_introspection_client() =>
            A.CallTo(() => _httpClientFactory.CreateClient("KeycloakIntrospectionClient"))
                .MustHaveHappenedOnceExactly();

        [Test]
        public void It_sends_exactly_one_post_to_the_realm_introspection_endpoint()
        {
            _handler.Requests.Should().ContainSingle();
            _handler.Requests[0].Method.Should().Be(HttpMethod.Post);
            _handler.Requests[0].Uri.Should().Be(new Uri(IntrospectUrl));
        }

        [Test]
        public void It_sends_a_form_body() =>
            _handler.Requests[0].ContentType.Should().Be("application/x-www-form-urlencoded");

        [Test]
        public void It_sends_only_the_callers_credentials_and_the_token_as_form_fields() =>
            _handler
                .Requests[0]
                .Form.Should()
                .BeEquivalentTo([
                    new KeyValuePair<string, string>("client_id", CallerClientId),
                    new KeyValuePair<string, string>("client_secret", CallerSecret),
                    new KeyValuePair<string, string>("token", Token),
                ]);

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

    public static IEnumerable<TestFixtureData> TokenTypeHints()
    {
        yield return new TestFixtureData(TokenTypeHint.AccessToken).SetArgDisplayNames("access_token");
        yield return new TestFixtureData(TokenTypeHint.RefreshToken).SetArgDisplayNames("refresh_token");
    }

    /// <summary>
    /// 26.7.5 answers an access token sent with <c>token_type_hint=refresh_token</c> as inactive
    /// (I-17) where 26.1.4 ignores the hint, so a forwarded hint could hide the caller's own live token.
    /// </summary>
    [TestFixtureSource(typeof(KeycloakTokenIntrospectionManagerTests), nameof(TokenTypeHints))]
    public class Given_a_token_type_hint(TokenTypeHint hint) : KeycloakTokenIntrospectionManagerTests
    {
        [SetUp]
        public async Task Setup() => await Introspect(hint);

        [Test]
        public void It_does_not_forward_the_hint() =>
            _handler.Requests[0].Form.Should().NotContain(pair => pair.Key == "token_type_hint");

        [Test]
        public void It_sends_only_the_callers_credentials_and_the_token() =>
            _handler
                .Requests[0]
                .Form.Select(pair => pair.Key)
                .Should()
                .BeEquivalentTo("client_id", "client_secret", "token");
    }

    public static IEnumerable<TestFixtureData> BlankCredentials()
    {
        yield return new TestFixtureData(CallerClientId, "").SetArgDisplayNames("empty secret");
        yield return new TestFixtureData(CallerClientId, "  ").SetArgDisplayNames("blank secret");
        yield return new TestFixtureData("", CallerSecret).SetArgDisplayNames("empty client id");
        yield return new TestFixtureData(" ", CallerSecret).SetArgDisplayNames("blank client id");
    }

    [TestFixtureSource(typeof(KeycloakTokenIntrospectionManagerTests), nameof(BlankCredentials))]
    public class Given_a_blank_credential(string clientId, string clientSecret)
        : KeycloakTokenIntrospectionManagerTests
    {
        private TokenIntrospectionResult _result = null!;

        [SetUp]
        public async Task Setup() =>
            _result = await Introspect(clientId: clientId, clientSecret: clientSecret);

        [Test]
        public void It_is_an_invalid_client() =>
            _result.Should().Be(new TokenIntrospectionResult.InvalidClient());

        [Test]
        public void It_sends_no_introspection_request() => _handler.Requests.Should().BeEmpty();
    }

    protected const string LongClientIdSentinel = "LONG-CLIENT-ID-SENTINEL";

    /// <summary>A client id of <paramref name="length"/> characters that starts with a sentinel.</summary>
    protected static string ClientIdOfLength(int length) =>
        LongClientIdSentinel + new string('c', length - LongClientIdSentinel.Length);

    /// <summary>
    /// A client id of <paramref name="codePoints"/> code points that starts with a sentinel and goes on
    /// with a character outside the Basic Multilingual Plane, two UTF-16 code units each.
    /// </summary>
    protected static string SupplementaryClientIdOf(int codePoints) =>
        LongClientIdSentinel
        + string.Concat(Enumerable.Repeat("\U0001F600", codePoints - LongClientIdSentinel.Length));

    /// <summary>
    /// Keycloak keeps a client id in a 255-character column, which PostgreSQL counts in code points
    /// (observed on 26.7.5: 255 code points outside the Basic Multilingual Plane, 510 UTF-16 code units,
    /// are stored).
    /// </summary>
    public static IEnumerable<TestFixtureData> ClientIdsOfKeycloaksMaximumLength()
    {
        yield return new TestFixtureData(ClientIdOfLength(255)).SetArgDisplayNames("255 characters");
        yield return new TestFixtureData(SupplementaryClientIdOf(255)).SetArgDisplayNames(
            "255 code points outside the Basic Multilingual Plane"
        );
    }

    /// <summary>A client id Keycloak can hold is still Keycloak's to judge.</summary>
    [TestFixtureSource(
        typeof(KeycloakTokenIntrospectionManagerTests),
        nameof(ClientIdsOfKeycloaksMaximumLength)
    )]
    public class Given_a_client_id_of_keycloaks_maximum_length(string clientId)
        : KeycloakTokenIntrospectionManagerTests
    {
        private TokenIntrospectionResult _result = null!;

        [SetUp]
        public async Task Setup() => _result = await Introspect(clientId: clientId);

        [Test]
        public void It_asks_keycloak_with_that_client_id() =>
            _handler
                .Requests.Should()
                .ContainSingle()
                .Which.Form.Should()
                .Contain(new KeyValuePair<string, string>("client_id", clientId));

        [Test]
        public void It_maps_keycloaks_answer() =>
            _result.Should().Be(new TokenIntrospectionResult.Inactive());
    }

    /// <summary>
    /// Each client id with what Keycloak answers it when asked: an unknown client just past the
    /// maximum, whether counted in characters or in code points, and a 413 once the id is over
    /// Keycloak's form attribute limit.
    /// </summary>
    public static IEnumerable<TestFixtureData> ClientIdsLongerThanKeycloaksMaximum()
    {
        string unknownClient = OAuthError("invalid_client", "Invalid client or Invalid client credentials");
        yield return new TestFixtureData(ClientIdOfLength(256), 401, unknownClient).SetArgDisplayNames(
            "256 characters"
        );
        yield return new TestFixtureData(SupplementaryClientIdOf(256), 401, unknownClient).SetArgDisplayNames(
            "256 code points outside the Basic Multilingual Plane"
        );
        yield return new TestFixtureData(ClientIdOfLength(131073), 413, FormLimitAnswer).SetArgDisplayNames(
            "over Keycloak's form attribute limit"
        );
    }

    /// <summary>
    /// A longer client id names no Keycloak client, so it is refused without asking Keycloak, and the
    /// caller-sized value never reaches a log.
    /// </summary>
    [TestFixtureSource(
        typeof(KeycloakTokenIntrospectionManagerTests),
        nameof(ClientIdsLongerThanKeycloaksMaximum)
    )]
    public class Given_a_client_id_longer_than_keycloaks_maximum(string clientId, int status, string body)
        : KeycloakTokenIntrospectionManagerTests
    {
        private TokenIntrospectionResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            KeycloakAnswers(() => Response(status, body));
            _result = await Introspect(clientId: clientId);
        }

        [Test]
        public void It_is_an_invalid_client() =>
            _result.Should().Be(new TokenIntrospectionResult.InvalidClient());

        [Test]
        public void It_sends_no_introspection_request() => _handler.Requests.Should().BeEmpty();

        [Test]
        public void It_logs_the_refusal_once_at_information() =>
            _logger
                .Entries.Should()
                .ContainSingle()
                .Which.Should()
                .Match<KeycloakCapturedLogEntry>(entry =>
                    entry.Level == LogLevel.Information
                    && entry.Message
                        == "Introspection refused: the client id is longer than any Keycloak client id"
                );

        [Test]
        public void It_logs_neither_the_client_id_the_token_nor_the_secret()
        {
            _logger.AllText().Should().NotContain(LongClientIdSentinel);
            AssertNoDisclosure();
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Response mapping: only the characterized answers; everything else fails closed
    // ---------------------------------------------------------------------------------------------

    public static IEnumerable<TestFixtureData> ResponseMappings()
    {
        TokenIntrospectionResult inactive = new TokenIntrospectionResult.Inactive();
        TokenIntrospectionResult invalidClient = new TokenIntrospectionResult.InvalidClient();
        TokenIntrospectionResult unrecognized = Unavailable("unrecognized-response");
        TokenIntrospectionResult unparseable = Unavailable("unparseable");
        TokenIntrospectionResult providerStatus = Unavailable("provider-status");

        TestFixtureData Case(string name, int status, string body, TokenIntrospectionResult expected) =>
            new TestFixtureData(status, body, expected).SetArgDisplayNames(name);

        // Characterized token outcomes (200).
        yield return Case("I-13 200 inactive", 200, InactiveAnswer, inactive);
        yield return Case("I-01 200 owner, single audience", 200, Answer(), Active(OwnedToken()));
        yield return Case(
            "I-02 200 owner, audience array",
            200,
            Answer(body => body["aud"] = new JsonArray(CallerClientId, "account")),
            Active(OwnedToken(aud: [CallerClientId, "account"]))
        );
        yield return Case(
            "I-03 200 another client's token",
            200,
            Answer(body =>
            {
                body["azp"] = OtherClientId;
                body["client_id"] = OtherClientId;
            }),
            inactive
        );
        yield return Case(
            "I-16 200 the caller's own refresh token",
            200,
            Answer(body =>
            {
                body["typ"] = "Refresh";
                body["token_type"] = "Refresh";
                body["aud"] = new JsonArray(Issuer, CallerClientId, "account");
            }),
            inactive
        );

        // Characterized client-authentication failures.
        yield return Case(
            "I-06 26.1.4 401 invalid_request Authentication failed.",
            401,
            OAuthError("invalid_request", "Authentication failed."),
            invalidClient
        );
        yield return Case(
            "I-06 26.7.5 401 invalid_client Client authentication failed.",
            401,
            OAuthError("invalid_client", "Client authentication failed."),
            invalidClient
        );
        yield return Case(
            "I-07 26.7.5 401 invalid_client Invalid client or Invalid client credentials",
            401,
            OAuthError("invalid_client", "Invalid client or Invalid client credentials"),
            invalidClient
        );
        yield return Case(
            "401 unauthorized_client",
            401,
            OAuthError("unauthorized_client", "Invalid client or Invalid client credentials"),
            invalidClient
        );
        yield return Case(
            "I-10 403 public client",
            403,
            OAuthError("invalid_request", PublicClientRefusalDescriptionFromEvidence),
            invalidClient
        );
        yield return Case(
            "403 public client, members reordered",
            403,
            """{"error_description":"Client not allowed.","error":"invalid_request"}""",
            invalidClient
        );

        // Characterized request errors CMS never causes: still not usable.
        yield return Case(
            "I-18 26.1.4 400 unsupported hint",
            400,
            OAuthError("invalid_request", "Unsupported token type [bogus]."),
            providerStatus
        );
        yield return Case(
            "I-18 26.7.5 400 unsupported hint",
            400,
            OAuthError("invalid_request", "Unsupported token type."),
            providerStatus
        );
        yield return Case(
            "I-20 400 duplicated parameter",
            400,
            OAuthError("invalid_request", "duplicated parameter"),
            providerStatus
        );
        yield return Case(
            "I-21 400 token not provided",
            400,
            OAuthError("invalid_request", "Token not provided."),
            providerStatus
        );

        // Ownership: azp and, when present, client_id must both equal the caller ordinally.
        yield return Case(
            "200 owner without client_id",
            200,
            Answer(body => body.Remove("client_id")),
            Active(OwnedToken())
        );
        yield return Case(
            "200 azp names another client, client_id the caller",
            200,
            Answer(body => body["azp"] = OtherClientId),
            inactive
        );
        yield return Case(
            "200 azp names another client, client_id absent",
            200,
            Answer(body =>
            {
                body["azp"] = OtherClientId;
                body.Remove("client_id");
            }),
            inactive
        );
        yield return Case(
            "200 client_id names another client, azp the caller",
            200,
            Answer(body => body["client_id"] = OtherClientId),
            inactive
        );
        yield return Case(
            "200 azp is a case variant of the caller",
            200,
            Answer(body =>
            {
                body["azp"] = CallerClientId.ToUpperInvariant();
                body["client_id"] = CallerClientId.ToUpperInvariant();
            }),
            inactive
        );
        yield return Case(
            "200 client_id is a case variant of the caller",
            200,
            Answer(body => body["client_id"] = CallerClientId.ToUpperInvariant()),
            inactive
        );
        yield return Case("200 azp missing", 200, Answer(body => body.Remove("azp")), unrecognized);
        yield return Case("200 azp not a string", 200, Answer(body => body["azp"] = 7), unrecognized);
        yield return Case("200 azp null", 200, Answer(body => body["azp"] = null), unrecognized);
        yield return Case(
            "200 client_id absent, azp the caller",
            200,
            Answer(body => body.Remove("client_id")),
            Active(OwnedToken())
        );
        yield return Case(
            "200 client_id not a string",
            200,
            Answer(body => body["client_id"] = new JsonArray(CallerClientId)),
            unrecognized
        );
        yield return Case("200 client_id null", 200, Answer(body => body["client_id"] = null), unrecognized);

        // Only access tokens are disclosed; ownership is decided before the token type.
        yield return Case("200 typ ID", 200, Answer(body => body["typ"] = "ID"), inactive);
        yield return Case("200 typ in another case", 200, Answer(body => body["typ"] = "bearer"), inactive);
        yield return Case(
            "200 typ Bearer but token_type Refresh",
            200,
            Answer(body => body["token_type"] = "Refresh"),
            Active(OwnedToken())
        );
        yield return Case("200 typ missing", 200, Answer(body => body.Remove("typ")), unrecognized);
        yield return Case("200 typ not a string", 200, Answer(body => body["typ"] = true), unrecognized);
        yield return Case(
            "200 another client's token with typ missing",
            200,
            Answer(body =>
            {
                body["azp"] = OtherClientId;
                body["client_id"] = OtherClientId;
                body.Remove("typ");
            }),
            inactive
        );

        // Required and optional members of an owned access token.
        yield return Case("200 exp missing", 200, Answer(body => body.Remove("exp")), unrecognized);
        yield return Case(
            "200 exp as a string",
            200,
            Answer(body => body["exp"] = Exp.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            unrecognized
        );
        yield return Case(
            "200 exp fractional",
            200,
            Answer(body => body["exp"] = 1792607774.5),
            unrecognized
        );
        yield return Case(
            "200 exp with an exponent",
            200,
            Answer(body => body["exp"] = JsonNode.Parse("1.792607774e9")),
            unrecognized
        );
        yield return Case(
            "200 exp beyond Int64",
            200,
            Answer(body => body["exp"] = JsonNode.Parse("9223372036854775808")),
            unrecognized
        );
        yield return Case("200 iat missing", 200, Answer(body => body.Remove("iat")), unrecognized);
        yield return Case("200 iat null", 200, Answer(body => body["iat"] = null), unrecognized);
        // Keycloak leaves scope out when the token has none (I-27); null is not absent.
        yield return Case(
            "I-27 200 owner, scope absent",
            200,
            Answer(body => body.Remove("scope")),
            Active(OwnedToken(scope: ""))
        );
        yield return Case("200 scope null", 200, Answer(body => body["scope"] = null), unrecognized);
        yield return Case(
            "200 scope not a string",
            200,
            Answer(body => body["scope"] = new JsonArray("profile", "email")),
            unrecognized
        );
        yield return Case(
            "200 sub absent",
            200,
            Answer(body => body.Remove("sub")),
            Active(OwnedToken(sub: null))
        );
        yield return Case("200 sub not a string", 200, Answer(body => body["sub"] = 5), unrecognized);
        yield return Case("200 sub null", 200, Answer(body => body["sub"] = null), unrecognized);
        yield return Case(
            "200 iss absent",
            200,
            Answer(body => body.Remove("iss")),
            Active(OwnedToken(iss: null))
        );
        yield return Case(
            "200 iss not a string",
            200,
            Answer(body => body["iss"] = new JsonObject()),
            unrecognized
        );
        yield return Case("200 iss null", 200, Answer(body => body["iss"] = null), unrecognized);
        yield return Case(
            "200 aud absent",
            200,
            Answer(body => body.Remove("aud")),
            Active(OwnedToken(aud: []))
        );
        yield return Case(
            "200 aud an empty array",
            200,
            Answer(body => body["aud"] = new JsonArray()),
            Active(OwnedToken(aud: []))
        );
        yield return Case(
            "200 aud array holding a non-string",
            200,
            Answer(body => body["aud"] = new JsonArray("account", 3)),
            unrecognized
        );
        yield return Case("200 aud a number", 200, Answer(body => body["aud"] = 3), unrecognized);
        yield return Case("200 aud null", 200, Answer(body => body["aud"] = null), unrecognized);

        // The active member itself.
        yield return Case("200 active missing", 200, Answer(body => body.Remove("active")), unrecognized);
        yield return Case(
            "200 active as a string",
            200,
            Answer(body => body["active"] = "true"),
            unrecognized
        );
        yield return Case("200 active null", 200, """{"active":null}""", unrecognized);
        yield return Case("200 empty object", 200, "{}", unrecognized);
        yield return Case(
            "200 inactive with other members",
            200,
            Answer(body => body["active"] = false),
            inactive
        );

        // Unparseable 200 bodies.
        yield return Case("200 empty body", 200, "", unparseable);
        yield return Case("200 not JSON", 200, "OK", unparseable);
        yield return Case("200 JSON array", 200, """[{"active":false}]""", unparseable);
        yield return Case("200 JSON true", 200, "true", unparseable);
        yield return Case(
            "200 truncated object",
            200,
            """{"active":true,"azp":"caller-client" """,
            unparseable
        );
        yield return Case("200 duplicated active", 200, """{"active":false,"active":true}""", unparseable);
        yield return Case(
            "200 duplicated azp",
            200,
            Answer()
                .Replace("\"azp\":\"caller-client\"", "\"azp\":\"other-client\",\"azp\":\"caller-client\""),
            unparseable
        );
        yield return Case(
            "200 duplicated client_id",
            200,
            Answer()
                .Replace(
                    "\"client_id\":\"caller-client\"",
                    "\"client_id\":\"other-client\",\"client_id\":\"caller-client\""
                ),
            unparseable
        );
        yield return Case(
            "200 duplicated scope",
            200,
            Answer().Replace("\"scope\":\"profile email\"", "\"scope\":\"x\",\"scope\":\"profile email\""),
            unparseable
        );
        yield return Case(
            "200 duplicated member that is never read",
            200,
            Answer().Replace("\"acr\":\"1\"", "\"acr\":\"0\",\"acr\":\"1\""),
            Active(OwnedToken())
        );

        // Client-authentication answers are exact on the status and the error.
        yield return Case(
            "401 invalid_token",
            401,
            OAuthError("invalid_token", "Invalid token"),
            unrecognized
        );
        yield return Case(
            "401 error in another case",
            401,
            OAuthError("INVALID_CLIENT", "Client authentication failed."),
            unrecognized
        );
        yield return Case("401 empty object", 401, "{}", unrecognized);
        yield return Case("401 non-string error", 401, """{"error":42}""", unrecognized);
        yield return Case("401 empty body", 401, "", unparseable);
        yield return Case("401 not JSON", 401, "Unauthorized", unparseable);
        yield return Case(
            "401 duplicated error member",
            401,
            """{"error":"invalid_client","error":"invalid_client"}""",
            unparseable
        );
        yield return Case(
            "403 HTTPS required",
            403,
            OAuthError("invalid_request", "HTTPS required"),
            unrecognized
        );
        yield return Case(
            "403 public client description without the period",
            403,
            OAuthError("invalid_request", "Client not allowed"),
            unrecognized
        );
        yield return Case(
            "403 public client description in another case",
            403,
            OAuthError("invalid_request", "client not allowed."),
            unrecognized
        );
        yield return Case(
            "403 public client description with a trailing space",
            403,
            OAuthError("invalid_request", "Client not allowed. "),
            unrecognized
        );
        yield return Case(
            "403 public client description under invalid_client",
            403,
            OAuthError("invalid_client", "Client not allowed."),
            unrecognized
        );
        yield return Case(
            "403 invalid_request without description",
            403,
            """{"error":"invalid_request"}""",
            unrecognized
        );
        yield return Case("403 empty body", 403, "", unparseable);
        yield return Case(
            "403 duplicated description member",
            403,
            """{"error":"invalid_request","error_description":"x","error_description":"Client not allowed."}""",
            unparseable
        );
        yield return Case(
            "401 public client description",
            401,
            OAuthError("invalid_request", "Client not allowed."),
            invalidClient
        );
        yield return Case(
            "400 public client description",
            400,
            OAuthError("invalid_request", "Client not allowed."),
            providerStatus
        );

        // Every other status.
        yield return Case("302 redirect", 302, "", providerStatus);
        yield return Case("307 redirect", 307, "", providerStatus);
        yield return Case("204 no content", 204, "", providerStatus);
        yield return Case("201 with an inactive body", 201, InactiveAnswer, providerStatus);
        yield return Case("400 invalid_client", 400, OAuthError("invalid_client", "x"), providerStatus);
        yield return Case("404 wrong realm", 404, """{"error":"Realm does not exist"}""", providerStatus);
        yield return Case("500", 500, "", providerStatus);
        yield return Case("502 not JSON", 502, "<html>Bad Gateway</html>", providerStatus);
        yield return Case(
            "503 temporarily_unavailable",
            503,
            OAuthError("temporarily_unavailable", "down"),
            providerStatus
        );
    }

    /// <summary>Copied from the characterization (I-10, identical on 26.1.4 and 26.7.5), not from the production constant, so a drift in either fails.</summary>
    private const string PublicClientRefusalDescriptionFromEvidence = "Client not allowed.";

    [TestFixtureSource(typeof(KeycloakTokenIntrospectionManagerTests), nameof(ResponseMappings))]
    public class Given_a_keycloak_answer(int status, string body, TokenIntrospectionResult expected)
        : KeycloakTokenIntrospectionManagerTests
    {
        private TokenIntrospectionResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            KeycloakAnswers(() => Response(status, body));
            _result = await Introspect();
        }

        [Test]
        public void It_maps_to_the_expected_result() => AssertResult(_result, expected);

        [Test]
        public void It_sent_exactly_one_introspection_request() => _handler.Requests.Should().ContainSingle();

        [Test]
        public void It_logs_an_operational_failure_at_error_only_when_unavailable() =>
            _logger
                .Entries.Exists(entry => entry.Level == LogLevel.Error)
                .Should()
                .Be(expected is TokenIntrospectionResult.TemporarilyUnavailable);

        [Test]
        public void It_logs_no_provider_content_and_attaches_no_exception() => AssertNoDisclosure();
    }

    [TestFixture]
    public class Given_the_production_public_client_constant : KeycloakTokenIntrospectionManagerTests
    {
        [Test]
        public void It_is_the_string_keycloak_was_observed_to_answer() =>
            KeycloakTokenIntrospectionManager
                .PublicClientRefusalDescription.Should()
                .Be(PublicClientRefusalDescriptionFromEvidence);
    }

    // ---------------------------------------------------------------------------------------------
    // The owner's answer: only the RFC 7662 members, client_id taken from azp
    // ---------------------------------------------------------------------------------------------

    [TestFixture]
    public class Given_keycloak_reports_the_callers_own_access_token : KeycloakTokenIntrospectionManagerTests
    {
        private IntrospectedToken _token = null!;

        [SetUp]
        public async Task Setup()
        {
            KeycloakAnswers(() => Response(200, Answer()));
            TokenIntrospectionResult result = await Introspect();
            _token = ((TokenIntrospectionResult.Active)result).Token;
        }

        [Test]
        public void It_reports_azp_as_the_client_id() => _token.ClientId.Should().Be(CallerClientId);

        [Test]
        public void It_reports_the_scope() => _token.Scope.Should().Be("profile email");

        [Test]
        public void It_reports_exp() => _token.Exp.Should().Be(Exp);

        [Test]
        public void It_reports_iat() => _token.Iat.Should().Be(Iat);

        [Test]
        public void It_reports_the_subject() => _token.Sub.Should().Be(Subject);

        [Test]
        public void It_reports_the_single_audience() => _token.Aud.Should().Equal("account");

        [Test]
        public void It_reports_the_issuer() => _token.Iss.Should().Be(Issuer);

        [Test]
        public void It_carries_no_other_keycloak_member() =>
            new[] { _token.ClientId, _token.Scope, _token.Sub, _token.Iss }
                .Concat(_token.Aud)
                .Should()
                .NotContain(value => (value ?? "").Contains(Sentinel));

        [Test]
        public void It_logs_nothing_above_debug() =>
            _logger.Entries.Should().NotContain(entry => entry.Level > LogLevel.Debug);
    }

    [TestFixture]
    public class Given_keycloak_reports_an_owned_token_with_several_audiences
        : KeycloakTokenIntrospectionManagerTests
    {
        private IntrospectedToken _token = null!;

        [SetUp]
        public async Task Setup()
        {
            KeycloakAnswers(() =>
                Response(200, Answer(body => body["aud"] = new JsonArray(CallerClientId, "account")))
            );
            _token = ((TokenIntrospectionResult.Active)await Introspect()).Token;
        }

        [Test]
        public void It_reports_every_audience_in_keycloaks_order() =>
            _token.Aud.Should().Equal(CallerClientId, "account");
    }

    [TestFixture]
    public class Given_keycloak_reports_another_clients_active_token : KeycloakTokenIntrospectionManagerTests
    {
        private TokenIntrospectionResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            KeycloakAnswers(() =>
                Response(
                    200,
                    Answer(body =>
                    {
                        body["azp"] = OtherClientId;
                        body["client_id"] = OtherClientId;
                    })
                )
            );
            _result = await Introspect();
        }

        [Test]
        public void It_answers_inactive() => _result.Should().Be(new TokenIntrospectionResult.Inactive());

        [Test]
        public void It_logs_at_debug_that_the_token_was_not_issued_to_the_caller() =>
            _logger
                .Entries.Should()
                .ContainSingle(entry =>
                    entry.Level == LogLevel.Debug
                    && entry.Message.Contains("not issued to the calling client")
                    && entry.Message.Contains(CallerClientId)
                );

        [Test]
        public void It_does_not_log_the_tokens_client() =>
            _logger.AllText().Should().NotContain(OtherClientId);

        [Test]
        public void It_logs_nothing_above_debug() =>
            _logger.Entries.Should().NotContain(entry => entry.Level > LogLevel.Debug);
    }

    [TestFixture]
    public class Given_a_caller_id_carrying_line_breaks : KeycloakTokenIntrospectionManagerTests
    {
        private const string ForgingClientId = "caller-client\r\nforged-entry";

        [SetUp]
        public async Task Setup()
        {
            KeycloakAnswers(() => Response(500, ""));
            await Introspect(clientId: ForgingClientId);
        }

        [Test]
        public void It_logs_only_the_sanitized_client_id() =>
            _logger
                .Entries.SelectMany(entry => entry.State)
                .Where(pair => pair.Key == "ClientId")
                .Select(pair => pair.Value)
                .Should()
                .NotBeEmpty()
                .And.AllBeEquivalentTo(LoggingUtility.SanitizeForLog(ForgingClientId));
    }

    [TestFixture]
    public class Given_a_provider_status_with_an_allowlisted_error : KeycloakTokenIntrospectionManagerTests
    {
        [SetUp]
        public async Task Setup()
        {
            KeycloakAnswers(() => Response(503, OAuthError("temporarily_unavailable", "down")));
            await Introspect();
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
    public class Given_an_unrecognized_provider_error : KeycloakTokenIntrospectionManagerTests
    {
        [SetUp]
        public async Task Setup()
        {
            KeycloakAnswers(() => Response(500, OAuthError(Sentinel, "x")));
            await Introspect();
        }

        [Test]
        public void It_logs_the_fixed_category_instead_of_the_value() =>
            _logger
                .Entries.Should()
                .Contain(entry => entry.Level == LogLevel.Error && entry.Message.Contains("(unrecognized)"));
    }

    [TestFixture]
    public class Given_keycloak_refuses_the_callers_credentials : KeycloakTokenIntrospectionManagerTests
    {
        [SetUp]
        public async Task Setup()
        {
            KeycloakAnswers(() =>
                Response(401, OAuthError("invalid_client", "Client authentication failed."))
            );
            await Introspect();
        }

        [Test]
        public void It_logs_the_refusal_at_information_with_the_status_and_category() =>
            _logger
                .Entries.Should()
                .ContainSingle(entry =>
                    entry.Level == LogLevel.Information
                    && entry.Message.Contains("HTTP 401")
                    && entry.Message.Contains("invalid_client")
                    && entry.Message.Contains(CallerClientId)
                );
    }

    /// <summary>
    /// Keycloak's answer to a form value over its form attribute limit (128 KiB by default), recorded on
    /// 26.1.4 and 26.7.5 with valid and wrong credentials alike: it rejects the body before it
    /// authenticates the caller.
    /// </summary>
    protected const string FormLimitAnswer = """{"error":"HTTP 413 Request Entity Too Large"}""";

    public static IEnumerable<TestFixtureData> TooLargeAnswers()
    {
        yield return new TestFixtureData(FormLimitAnswer).SetArgDisplayNames("recorded body");
        yield return new TestFixtureData("").SetArgDisplayNames("empty body");
        yield return new TestFixtureData($"<html><body>{Sentinel}</body></html>").SetArgDisplayNames(
            "HTML body"
        );
        yield return new TestFixtureData(
            OAuthError("invalid_client", "Client authentication failed.")
        ).SetArgDisplayNames("an authentication error body");
        yield return new TestFixtureData(
            new string(' ', KeycloakTokenIntrospectionManager.MaxResponseBodyBytes + 1)
        ).SetArgDisplayNames("a body over the read cap");
    }

    /// <summary>
    /// The caller chose the oversized value, and Keycloak rejected it before judging the caller, so the
    /// status alone decides the answer: neither an outage nor rejected credentials, whatever the body.
    /// </summary>
    [TestFixtureSource(typeof(KeycloakTokenIntrospectionManagerTests), nameof(TooLargeAnswers))]
    public class Given_keycloak_rejects_the_request_as_too_large(string body)
        : KeycloakTokenIntrospectionManagerTests
    {
        private TokenIntrospectionResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            KeycloakAnswers(() => Response(413, body));
            _result = await Introspect();
        }

        [Test]
        public void It_is_an_invalid_request() =>
            _result.Should().Be(new TokenIntrospectionResult.InvalidRequest());

        [Test]
        public void It_logs_the_rejection_once_at_information() =>
            _logger
                .Entries.Should()
                .ContainSingle()
                .Which.Should()
                .Match<KeycloakCapturedLogEntry>(entry =>
                    entry.Level == LogLevel.Information
                    && entry.Message
                        == "Keycloak rejected an introspection request as too large (HTTP 413) before authenticating the caller"
                );

        [Test]
        public void It_logs_no_client_id_field() =>
            _logger
                .Entries.SelectMany(entry => entry.State)
                .Should()
                .NotContain(pair => pair.Key == "ClientId");

        [Test]
        public void It_logs_no_failure() =>
            _logger.Entries.Should().NotContain(entry => entry.Level >= LogLevel.Warning);

        [Test]
        public void It_logs_neither_the_token_the_secret_nor_the_body() => AssertNoDisclosure();
    }

    /// <summary>
    /// Keycloak authenticated no one, so the rejection is logged without the caller's client id, even
    /// one of the longest length Keycloak can hold.
    /// </summary>
    [TestFixture]
    public class Given_a_long_client_id_whose_request_keycloak_rejects_as_too_large
        : KeycloakTokenIntrospectionManagerTests
    {
        private TokenIntrospectionResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            KeycloakAnswers(() => Response(413, FormLimitAnswer));
            _result = await Introspect(clientId: ClientIdOfLength(255));
        }

        [Test]
        public void It_is_an_invalid_request() =>
            _result.Should().Be(new TokenIntrospectionResult.InvalidRequest());

        [Test]
        public void It_logs_the_rejection_once_at_information() =>
            _logger.Entries.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Information);

        [Test]
        public void It_logs_neither_the_client_id_the_token_nor_the_secret() =>
            _logger
                .AllText()
                .Should()
                .NotContain(LongClientIdSentinel)
                .And.NotContain(Token)
                .And.NotContain(CallerSecret);
    }

    [TestFixture]
    public class Given_an_unrecognized_active_answer : KeycloakTokenIntrospectionManagerTests
    {
        [SetUp]
        public async Task Setup()
        {
            KeycloakAnswers(() => Response(200, Answer(body => body.Remove("exp"))));
            await Introspect();
        }

        [Test]
        public void It_logs_which_member_was_not_usable_without_its_value() =>
            _logger
                .Entries.Should()
                .ContainSingle(entry =>
                    entry.Level == LogLevel.Error
                    && entry.Message.Contains("HTTP 200")
                    && entry.Message.Contains("exp")
                    && entry.Message.Contains("unrecognized-response")
                );
    }

    [TestFixture]
    public class Given_a_body_that_is_not_utf8 : KeycloakTokenIntrospectionManagerTests
    {
        private TokenIntrospectionResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            byte[] body =
            [
                .. Encoding.UTF8.GetBytes("""{"active":true,"azp":"caller-client"""),
                0xFF,
                .. "\"}"u8,
            ];
            KeycloakAnswers(() => Response(200, new ByteArrayContent(body)));
            _result = await Introspect();
        }

        [Test]
        public void It_is_unparseable() => _result.Should().Be(Unavailable("unparseable"));
    }

    /// <summary>
    /// Escaped, unpaired UTF-16 surrogates. Each body is pure ASCII and syntactically valid JSON, so it
    /// passes the UTF-8 check and the parser; only decoding the name or the string fails. The payloads
    /// are raw literals, never produced by a serializer that could replace the invalid escape.
    /// </summary>
    public static IEnumerable<TestFixtureData> MalformedUnicodeEscapes()
    {
        TestFixtureData Case(string name, int status, string body, TokenIntrospectionResult expected) =>
            new TestFixtureData(status, body, expected).SetArgDisplayNames(name);

        TokenIntrospectionResult unparseable = Unavailable("unparseable");
        const string owned =
            "\"exp\":1792607774,\"iat\":1792605974,\"typ\":\"Bearer\",\"scope\":\"profile email\",\"active\":true";

        yield return Case(
            "200 lone high surrogate in azp",
            200,
            $$"""{"azp":"caller-client\uD800",{{owned}}}""",
            unparseable
        );
        yield return Case(
            "200 lone low surrogate in scope",
            200,
            """{"azp":"caller-client","exp":1792607774,"iat":1792605974,"typ":"Bearer","scope":"\uDC00SECRET-PROVIDER-SENTINEL","active":true}""",
            unparseable
        );
        yield return Case(
            "200 lone surrogate inside the audience array",
            200,
            $$"""{"azp":"caller-client","aud":["account","\uD800"],{{owned}}}""",
            unparseable
        );
        yield return Case(
            "200 lone surrogate as an unrelated name",
            200,
            $$"""{"\uD800SECRET-PROVIDER-SENTINEL":0,"azp":"caller-client",{{owned}}}""",
            unparseable
        );
        yield return Case(
            "200 lone surrogate inside a name resembling azp",
            200,
            $$"""{"azp\uDC00":"other-client","azp":"caller-client",{{owned}}}""",
            unparseable
        );
        yield return Case(
            "401 lone surrogate in error",
            401,
            """{"error":"\uD800SECRET-PROVIDER-SENTINEL"}""",
            unparseable
        );
        yield return Case(
            "403 lone surrogate in error_description",
            403,
            """{"error":"invalid_request","error_description":"Client not allowed.\uD800"}""",
            unparseable
        );
        // At an unmapped status the body is parsed only for the log category.
        yield return Case(
            "500 lone surrogate in error",
            500,
            """{"error":"\uD800SECRET-PROVIDER-SENTINEL"}""",
            Unavailable("provider-status")
        );

        // Controls: well-formed escapes still decode and map as before.
        yield return Case(
            "200 owner with escaped member names and values",
            200,
            """{"\u0061zp":"caller\u002Dclient","exp":1792607774,"iat":1792605974,"typ":"B\u0065arer","scope":"profile email","active":true}""",
            Active(new IntrospectedToken(CallerClientId, "profile email", Exp, Iat, null, [], null))
        );
        yield return Case(
            "200 azp repeated through an escaped name",
            200,
            $$"""{"azp":"other-client","\u0061zp":"caller-client",{{owned}}}""",
            unparseable
        );
        yield return Case(
            "200 valid surrogate pair as an unrelated name",
            200,
            $$"""{"\uD83D\uDE00":0,"azp":"caller-client",{{owned}}}""",
            Active(new IntrospectedToken(CallerClientId, "profile email", Exp, Iat, null, [], null))
        );
        yield return Case(
            "403 public client written with escapes",
            403,
            """{"error":"invalid\u005Frequest","error_description":"Client not allowed\u002E"}""",
            new TokenIntrospectionResult.InvalidClient()
        );
    }

    [TestFixtureSource(typeof(KeycloakTokenIntrospectionManagerTests), nameof(MalformedUnicodeEscapes))]
    public class Given_a_keycloak_answer_with_unicode_escapes(
        int status,
        string body,
        TokenIntrospectionResult expected
    ) : KeycloakTokenIntrospectionManagerTests
    {
        private TokenIntrospectionResult? _result;
        private Exception? _exception;

        [SetUp]
        public async Task Setup()
        {
            KeycloakAnswers(() => Response(status, body));
            try
            {
                _result = await Introspect();
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
        public void It_maps_to_the_expected_result() => AssertResult(_result!, expected);

        [Test]
        public void It_logs_no_provider_content_and_attaches_no_exception() => AssertNoDisclosure();
    }

    // ---------------------------------------------------------------------------------------------
    // Bounded reading
    // ---------------------------------------------------------------------------------------------

    protected static string PaddedInactiveAnswer(int totalBytes) =>
        InactiveAnswer[..^1] + new string(' ', totalBytes - Encoding.UTF8.GetByteCount(InactiveAnswer)) + "}";

    [TestFixture]
    public class Given_an_answer_of_exactly_the_cap : KeycloakTokenIntrospectionManagerTests
    {
        private TokenIntrospectionResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            string body = PaddedInactiveAnswer(KeycloakTokenIntrospectionManager.MaxResponseBodyBytes);
            KeycloakAnswers(() =>
                Response(200, new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(body))))
            );
            _result = await Introspect();
        }

        [Test]
        public void It_reads_and_maps_it() => _result.Should().Be(new TokenIntrospectionResult.Inactive());
    }

    /// <summary>
    /// A client with many education organizations gets a long token, and Keycloak's answer repeats its
    /// claims. 9,600 nine-digit ids give the longest token Keycloak's form attribute limit lets
    /// through (129,287 characters on 26.7.5, whose answer was 96,671 bytes), so the owner is told
    /// about the largest token Keycloak can describe.
    /// </summary>
    [TestFixture]
    public class Given_the_owners_answer_for_the_longest_token_keycloak_accepts
        : KeycloakTokenIntrospectionManagerTests
    {
        private string _body = null!;
        private TokenIntrospectionResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _body = Answer(answer =>
                answer["educationOrganizationIds"] = string.Join(",", Enumerable.Range(255900001, 9600))
            );
            KeycloakAnswers(() => Response(200, _body));
            _result = await Introspect();
        }

        [Test]
        public void It_is_an_answer_over_64_KiB() =>
            Encoding.UTF8.GetByteCount(_body).Should().BeGreaterThan(64 * 1024);

        [Test]
        public void It_describes_the_token() =>
            AssertResult(_result, new TokenIntrospectionResult.Active(OwnedToken()));

        [Test]
        public void It_logs_no_provider_content() => AssertNoDisclosure();
    }

    [TestFixture]
    public class Given_an_answer_one_byte_over_the_cap_without_a_length
        : KeycloakTokenIntrospectionManagerTests
    {
        private TokenIntrospectionResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            string body = PaddedInactiveAnswer(KeycloakTokenIntrospectionManager.MaxResponseBodyBytes + 1);
            // StreamContent over a non-seekable stream carries no Content-Length, so the cap is
            // enforced while reading.
            KeycloakAnswers(() =>
                Response(200, new StreamContent(new ProbeStream(Encoding.UTF8.GetBytes(body))))
            );
            _result = await Introspect();
        }

        [Test]
        public void It_is_unparseable() => _result.Should().Be(Unavailable("unparseable"));
    }

    [TestFixture]
    public class Given_an_answer_that_declares_a_length_over_the_cap : KeycloakTokenIntrospectionManagerTests
    {
        private TokenIntrospectionResult _result = null!;
        private ProbeStream _stream = null!;

        [SetUp]
        public async Task Setup()
        {
            _stream = new ProbeStream(Encoding.UTF8.GetBytes(InactiveAnswer));
            KeycloakAnswers(() =>
            {
                var content = new StreamContent(_stream);
                content.Headers.ContentLength = KeycloakTokenIntrospectionManager.MaxResponseBodyBytes + 1;
                return Response(200, content);
            });
            _result = await Introspect();
        }

        [TearDown]
        public void DisposeStream() => _stream.Dispose();

        [Test]
        public void It_is_unparseable() => _result.Should().Be(Unavailable("unparseable"));

        [Test]
        public void It_does_not_read_the_body() => _stream.WasRead.Should().BeFalse();
    }

    // ---------------------------------------------------------------------------------------------
    // Transport failures, timeout and cancellation
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

    [TestFixtureSource(typeof(KeycloakTokenIntrospectionManagerTests), nameof(TransportFailures))]
    public class Given_the_introspection_request_fails_in_transport(Exception failure, string reason)
        : KeycloakTokenIntrospectionManagerTests
    {
        private TokenIntrospectionResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _handler.Respond = (_, _) => Task.FromException<HttpResponseMessage>(failure);
            _result = await Introspect();
        }

        [Test]
        public void It_is_temporarily_unavailable() => _result.Should().Be(Unavailable(reason));

        [Test]
        public void It_had_sent_the_request() => _handler.Requests.Should().ContainSingle();

        [Test]
        public void It_logs_the_failure_at_error() =>
            _logger.Entries.Should().Contain(entry => entry.Level == LogLevel.Error);

        [Test]
        public void It_logs_only_type_names_never_the_exception_or_its_content() => AssertNoDisclosure();
    }

    public static IEnumerable<TestFixtureData> AnswersThatFailWhileRead()
    {
        yield return new TestFixtureData(200).SetArgDisplayNames("200");
        yield return new TestFixtureData(401).SetArgDisplayNames("401");
    }

    [TestFixtureSource(typeof(KeycloakTokenIntrospectionManagerTests), nameof(AnswersThatFailWhileRead))]
    public class Given_an_answer_that_fails_while_being_read(int status)
        : KeycloakTokenIntrospectionManagerTests
    {
        private TokenIntrospectionResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            KeycloakAnswers(() =>
                Response(
                    status,
                    new StreamContent(
                        ProbeStream.Failing(new IOException(Sentinel), Encoding.UTF8.GetBytes("""{"act"""))
                    )
                )
            );
            _result = await Introspect();
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
        : KeycloakTokenIntrospectionManagerTests
    {
        private TokenIntrospectionResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _clientTimeout = TimeSpan.FromMilliseconds(50);
            _handler.Respond = async (_, token) =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return Response(200, InactiveAnswer);
            };
            _result = await Introspect();
        }

        [Test]
        public void It_is_temporarily_unavailable() => _result.Should().Be(Unavailable("timeout"));

        [Test]
        public void It_had_sent_the_request() => _handler.Requests.Should().ContainSingle();

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
    public class Given_an_answer_that_stalls : KeycloakTokenIntrospectionManagerTests
    {
        private TokenIntrospectionResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _clientTimeout = TimeSpan.FromMilliseconds(50);
            KeycloakAnswers(() => Response(200, new StreamContent(ProbeStream.Stalling())));
            _result = await Introspect();
        }

        [Test]
        public void It_bounds_the_read_and_reports_a_lost_response() =>
            _result.Should().Be(Unavailable("response-lost"));
    }

    [TestFixture]
    public class Given_the_caller_cancels_while_waiting_for_keycloak : KeycloakTokenIntrospectionManagerTests
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
                return Response(200, InactiveAnswer);
            };
            _exception = await Catch(Introspect(cancellationToken: _caller.Token));
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
    public class Given_the_caller_cancels_while_the_answer_is_read : KeycloakTokenIntrospectionManagerTests
    {
        private CancellationTokenSource _caller = null!;
        private Exception? _exception;

        [SetUp]
        public async Task Setup()
        {
            _clientTimeout = Timeout.InfiniteTimeSpan;
            _caller = new CancellationTokenSource();
            KeycloakAnswers(() =>
                Response(200, new StreamContent(ProbeStream.Stalling(onRead: () => _caller.Cancel())))
            );
            _exception = await Catch(Introspect(cancellationToken: _caller.Token));
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
    // Disclosure: provider content never reaches a log, and never a result unless it is disclosed
    // ---------------------------------------------------------------------------------------------

    public static IEnumerable<TestFixtureData> ProviderBodiesCarryingASentinel()
    {
        yield return new TestFixtureData(
            200,
            Answer(body =>
            {
                body["azp"] = Sentinel;
                body["client_id"] = Sentinel;
            })
        ).SetArgDisplayNames("200 another client's token carrying a sentinel everywhere");
        yield return new TestFixtureData(200, Answer(body => body["typ"] = Sentinel)).SetArgDisplayNames(
            "200 sentinel token type"
        );
        yield return new TestFixtureData(200, Answer(body => body["exp"] = Sentinel)).SetArgDisplayNames(
            "200 sentinel exp"
        );
        yield return new TestFixtureData(200, $$"""{"active":"{{Sentinel}}"}""").SetArgDisplayNames(
            "200 sentinel active"
        );
        yield return new TestFixtureData(401, OAuthError("invalid_client", Sentinel)).SetArgDisplayNames(
            "401 invalid_client with a sentinel description"
        );
        yield return new TestFixtureData(403, OAuthError(Sentinel, Sentinel)).SetArgDisplayNames(
            "403 sentinel error"
        );
        yield return new TestFixtureData(500, OAuthError(Sentinel, Sentinel)).SetArgDisplayNames(
            "500 sentinel error"
        );
        yield return new TestFixtureData(200, Sentinel).SetArgDisplayNames("200 non-JSON sentinel body");
        yield return new TestFixtureData(401, $$"""{"error":"{{Sentinel}}" """).SetArgDisplayNames(
            "401 malformed JSON carrying a sentinel"
        );
    }

    [TestFixtureSource(
        typeof(KeycloakTokenIntrospectionManagerTests),
        nameof(ProviderBodiesCarryingASentinel)
    )]
    public class Given_a_provider_body_carrying_a_sentinel(int status, string body)
        : KeycloakTokenIntrospectionManagerTests
    {
        private TokenIntrospectionResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            KeycloakAnswers(() => Response(status, body));
            _result = await Introspect();
        }

        [Test]
        public void It_logs_neither_the_body_nor_any_credential_or_token() => AssertNoDisclosure();

        [Test]
        public void It_returns_no_provider_content() => _result.ToString().Should().NotContain(Sentinel);
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
}
