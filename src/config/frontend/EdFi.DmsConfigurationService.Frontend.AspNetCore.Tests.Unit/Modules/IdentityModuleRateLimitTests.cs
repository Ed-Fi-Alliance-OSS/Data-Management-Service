// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using NUnit.Framework;
using static EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure.ConnectRateLimitTestHost;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Modules;

/// <summary>
/// The per-client gate inside token, revoke and authenticated introspect (DMS-1482 REQ-1, REQ-2, REQ-3). The
/// source budget is raised out of the way. The managers are fakes, so "the manager was not called" is
/// exactly "the secret was not verified".
/// </summary>
public class IdentityModuleRateLimitTests
{
    private const string Detail =
        "The number of allowed requests has been exceeded. Retry the request later.";

    private static void ManagerCalls(ITokenManager tokenManager, int times) =>
        A.CallTo(() => tokenManager.GetAccessTokenAsync(A<IEnumerable<KeyValuePair<string, string>>>._))
            .MustHaveHappened(times, Times.Exactly);

    private static void RevocationCalls(ITokenRevocationManager revocationManager, int times) =>
        A.CallTo(() =>
                revocationManager.RevokeTokenAsync(A<TokenRevocationRequest>._, A<CancellationToken>._)
            )
            .MustHaveHappened(times, Times.Exactly);

    private static void IntrospectionCalls(ITokenIntrospectionManager manager, int times) =>
        A.CallTo(() => manager.IntrospectTokenAsync(A<TokenIntrospectionRequest>._, A<CancellationToken>._))
            .MustHaveHappened(times, Times.Exactly);

    [TestFixture("/connect/token", false, true)]
    [TestFixture("/connect/token", true, true)]
    [TestFixture("/connect/revoke", false, true)]
    [TestFixture("/connect/revoke", true, true)]
    [TestFixture("/connect/introspect", false, true)]
    [TestFixture("/connect/introspect", true, true)]
    [TestFixture("/connect/introspect", false, false)]
    [TestFixture("/connect/introspect", true, false)]
    public class Given_oversized_client_credentials(string path, bool useBasic, bool enabled)
    {
        private ITokenManager _tokenManager = null!;
        private ITokenRevocationManager _revocationManager = null!;
        private ITokenIntrospectionManager _introspectionManager = null!;
        private List<int> _statuses = [];
        private JsonObject _body = null!;

        private HttpRequestMessage Request(string clientId)
        {
            FormUrlEncodedContent form;
            if (useBasic)
            {
                form = new FormUrlEncodedContent(
                    path == "/connect/token"
                        ? [new("grant_type", "client_credentials")]
                        : [new("token", "opaque-token")]
                );
            }
            else
            {
                form = path == "/connect/token" ? TokenForm(clientId) : RevokeForm(clientId);
            }
            HttpRequestMessage request = Post(path, form);
            if (useBasic)
            {
                request.Headers.Authorization = Basic(clientId);
            }
            return request;
        }

        [SetUp]
        public async Task Setup()
        {
            _tokenManager = SucceedingTokenManager();
            _revocationManager = CompletingRevocationManager();
            _introspectionManager = InactiveIntrospectionManager();
            await using WebApplicationFactory<Program> factory = Create(
                sourcePermitLimit: 1000,
                clientPermitLimit: 1,
                enabled: enabled,
                tokenManager: _tokenManager,
                revocationManager: _revocationManager,
                introspectionManager: _introspectionManager
            );
            using HttpClient client = factory.CreateClient();
            using HttpResponseMessage oversized = await client.SendAsync(Request(new string('x', 1025)));
            _body = JsonNode.Parse(await oversized.Content.ReadAsStringAsync())!.AsObject();
            _statuses = [(int)oversized.StatusCode];
            _statuses.AddRange(
                await SendAll(
                    client,
                    [
                        () => Request("ClientA" + new string(' ', 1_000_000)),
                        () => Request(new string('x', 1024)),
                        () => Request("ClientA"),
                        () => Request("ClientA" + new string(' ', 1018)),
                        () => Request("CLIENTA "),
                    ]
                )
            );
        }

        [Test]
        public void It_rejects_oversized_ids_before_counting_and_preserves_the_boundary() =>
            _statuses.Should().Equal(400, 400, 200, 200, 400, enabled ? 429 : 200);

        [Test]
        public void It_verifies_only_the_admitted_credentials()
        {
            int admitted = enabled ? 2 : 3;
            ManagerCalls(_tokenManager, path == "/connect/token" ? admitted : 0);
            RevocationCalls(_revocationManager, path == "/connect/revoke" ? admitted : 0);
            IntrospectionCalls(_introspectionManager, path == "/connect/introspect" ? admitted : 0);
        }

        [Test]
        public void It_uses_the_routes_existing_validation_contract()
        {
            if (path != "/connect/token")
            {
                _body["error"]!.GetValue<string>().Should().Be("invalid_request");
                _body["error_description"]!
                    .GetValue<string>()
                    .Should()
                    .Be("The client_id must not exceed 1024 characters.");
                return;
            }
            _body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:management-api:bad-request:data");
            _body["validationErrors"]!.AsObject().Should().ContainKey("client_id");
        }
    }

    [TestFixture]
    public class Given_a_client_over_its_token_limit
    {
        private ITokenManager _tokenManager = null!;
        private List<int> _statuses = null!;
        private HttpResponseMessage _rejected = null!;
        private JsonObject _body = null!;

        [SetUp]
        public async Task Setup()
        {
            _tokenManager = SucceedingTokenManager();
            await using WebApplicationFactory<Program> factory = Create(
                sourcePermitLimit: 1000,
                clientPermitLimit: 2,
                tokenManager: _tokenManager
            );
            using HttpClient client = factory.CreateClient();
            _statuses = await SendAll(
                client,
                [
                    () => Post("/connect/token", TokenForm("ClientA")),
                    () => Post("/connect/token", TokenForm("ClientA")),
                ]
            );
            _rejected = await client.SendAsync(Post("/connect/token", TokenForm("ClientA")));
            _body = JsonNode.Parse(await _rejected.Content.ReadAsStringAsync())!.AsObject();
        }

        [TearDown]
        public void TearDown() => _rejected.Dispose();

        [Test]
        public void It_admits_up_to_the_limit() => _statuses.Should().Equal(200, 200);

        [Test]
        public void It_answers_429_past_the_limit() => ((int)_rejected.StatusCode).Should().Be(429);

        [Test]
        public void It_uses_the_ed_fi_contract()
        {
            _rejected.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
            _body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:management-api:too-many-requests");
            _body["detail"]!.GetValue<string>().Should().Be(Detail);
            _body["status"]!.GetValue<int>().Should().Be(429);
        }

        [Test]
        public void It_sends_a_whole_second_retry_after() =>
            _rejected.Headers.RetryAfter!.Delta!.Value.TotalSeconds.Should().BeInRange(1, 60);

        [Test]
        public void It_does_not_verify_the_rejected_secret() => ManagerCalls(_tokenManager, 2);
    }

    [TestFixture]
    public class Given_one_client_exhausted_and_other_callers
    {
        private List<int> _statuses = null!;

        [SetUp]
        public async Task Setup()
        {
            await using WebApplicationFactory<Program> factory = Create(
                sourcePermitLimit: 1000,
                clientPermitLimit: 1
            );
            using HttpClient client = factory.CreateClient();
            _statuses = await SendAll(
                client,
                [
                    () => Post("/connect/token", TokenForm("ClientA"), "198.51.100.1"),
                    () => Post("/connect/token", TokenForm("ClientB"), "198.51.100.1"),
                    () => Post("/connect/token", TokenForm("CLIENTA"), "198.51.100.1"),
                    () => Post("/connect/token", TokenForm("ClientA"), "198.51.100.2"),
                ]
            );
        }

        [Test]
        public void It_limits_aliases_at_one_source_and_keeps_other_sources_and_clients_independent() =>
            _statuses.Should().Equal(200, 200, 429, 200);
    }

    [TestFixture]
    public class Given_trailing_space_aliases_of_an_exhausted_client
    {
        private ITokenManager _tokenManager = null!;
        private ITokenRevocationManager _revocationManager = null!;
        private List<int> _statuses = null!;

        [SetUp]
        public async Task Setup()
        {
            _tokenManager = SucceedingTokenManager();
            _revocationManager = CompletingRevocationManager();
            await using WebApplicationFactory<Program> factory = Create(
                sourcePermitLimit: 1000,
                clientPermitLimit: 1,
                tokenManager: _tokenManager,
                revocationManager: _revocationManager
            );
            using HttpClient client = factory.CreateClient();
            _statuses = await SendAll(
                client,
                [
                    () => Post("/connect/token", TokenForm("ClientA")),
                    () => Post("/connect/token", TokenForm("ClientA ")),
                    () => Post("/connect/token", TokenForm("ClientA  ")),
                    () => Post("/connect/revoke", RevokeForm("ClientA ")),
                    () => Post("/connect/revoke", RevokeForm("ClientA  ")),
                    () => Post("/connect/revoke", RevokeForm("ClientB")),
                    () => Post("/connect/revoke", RevokeForm("ClientB ")),
                    () => Post("/connect/token", TokenForm("ClientB  ")),
                ]
            );
        }

        [Test]
        public void It_rejects_aliases_on_both_routes_and_keeps_other_clients_independent() =>
            _statuses.Should().Equal(200, 429, 429, 429, 429, 200, 429, 429);

        [Test]
        public void It_does_not_verify_rejected_token_secrets() => ManagerCalls(_tokenManager, 1);

        [Test]
        public void It_does_not_verify_or_revoke_rejected_revocation_requests() =>
            RevocationCalls(_revocationManager, 1);
    }

    [TestFixture]
    public class Given_width_aliases_of_an_exhausted_client
    {
        private ITokenManager _tokenManager = null!;
        private ITokenRevocationManager _revocationManager = null!;
        private List<int> _statuses = null!;

        [SetUp]
        public async Task Setup()
        {
            _tokenManager = SucceedingTokenManager();
            _revocationManager = CompletingRevocationManager();
            await using WebApplicationFactory<Program> factory = Create(
                sourcePermitLimit: 1000,
                clientPermitLimit: 1,
                tokenManager: _tokenManager,
                revocationManager: _revocationManager
            );
            using HttpClient client = factory.CreateClient();
            _statuses = await SendAll(
                client,
                [
                    () => Post("/connect/token", TokenForm("\uFF23lientA"), "198.51.100.1"),
                    () => Post("/connect/token", TokenForm("ClientA"), "198.51.100.1"),
                    () => Post("/connect/revoke", RevokeForm("\uFF23lientA"), "198.51.100.1"),
                    () => Post("/connect/revoke", RevokeForm("\uFF23lientB"), "198.51.100.1"),
                    () => Post("/connect/token", TokenForm("ClientB"), "198.51.100.1"),
                ]
            );
        }

        [Test]
        public void It_shares_the_client_budget_across_routes_at_one_source() =>
            _statuses.Should().Equal(200, 429, 429, 200, 429);

        [Test]
        public void It_does_not_verify_rejected_token_secrets() => ManagerCalls(_tokenManager, 1);

        [Test]
        public void It_preserves_the_admitted_token_credential() =>
            A.CallTo(() =>
                    _tokenManager.GetAccessTokenAsync(
                        A<IEnumerable<KeyValuePair<string, string>>>.That.Matches(parameters =>
                            parameters.Any(pair => pair.Key == "client_id" && pair.Value == "\uFF23lientA")
                        )
                    )
                )
                .MustHaveHappenedOnceExactly();

        [Test]
        public void It_preserves_the_admitted_revocation_credential() =>
            A.CallTo(() =>
                    _revocationManager.RevokeTokenAsync(
                        A<TokenRevocationRequest>.That.Matches(request => request.ClientId == "\uFF23lientB"),
                        A<CancellationToken>._
                    )
                )
                .MustHaveHappenedOnceExactly();

        [Test]
        public void It_does_not_verify_rejected_revocation_secrets() =>
            RevocationCalls(_revocationManager, 1);
    }

    [TestFixture]
    public class Given_an_admitted_client_id_with_trailing_spaces
    {
        private ITokenManager _tokenManager = null!;
        private ITokenRevocationManager _revocationManager = null!;

        [SetUp]
        public async Task Setup()
        {
            _tokenManager = SucceedingTokenManager();
            _revocationManager = CompletingRevocationManager();
            await using WebApplicationFactory<Program> factory = Create(
                sourcePermitLimit: 1000,
                clientPermitLimit: 2,
                tokenManager: _tokenManager,
                revocationManager: _revocationManager
            );
            using HttpClient client = factory.CreateClient();
            using HttpResponseMessage token = await client.SendAsync(
                Post("/connect/token", TokenForm("ClientA "))
            );
            using HttpResponseMessage revoke = await client.SendAsync(
                Post("/connect/revoke", RevokeForm("ClientA  "))
            );
        }

        [Test]
        public void It_preserves_the_token_authentication_input() =>
            A.CallTo(() =>
                    _tokenManager.GetAccessTokenAsync(
                        A<IEnumerable<KeyValuePair<string, string>>>.That.Matches(parameters =>
                            parameters.Any(pair => pair.Key == "client_id" && pair.Value == "ClientA ")
                        )
                    )
                )
                .MustHaveHappenedOnceExactly();

        [Test]
        public void It_preserves_the_revocation_authentication_input() =>
            A.CallTo(() =>
                    _revocationManager.RevokeTokenAsync(
                        A<TokenRevocationRequest>.That.Matches(request => request.ClientId == "ClientA  "),
                        A<CancellationToken>._
                    )
                )
                .MustHaveHappenedOnceExactly();
    }

    [TestFixture]
    public class Given_a_client_authenticating_with_a_basic_header
    {
        private List<int> _statuses = null!;

        [SetUp]
        public async Task Setup()
        {
            await using WebApplicationFactory<Program> factory = Create(
                sourcePermitLimit: 1000,
                clientPermitLimit: 1
            );
            using HttpClient client = factory.CreateClient();
            _statuses = await SendAll(
                client,
                [
                    () =>
                    {
                        HttpRequestMessage request = Post(
                            "/connect/token",
                            new FormUrlEncodedContent([new("grant_type", "client_credentials")])
                        );
                        request.Headers.Authorization = Basic("ClientA");
                        return request;
                    },
                    () => Post("/connect/token", TokenForm("ClientA")),
                ]
            );
        }

        [Test]
        public void It_keys_on_the_parsed_basic_client_id() => _statuses.Should().Equal(200, 429);
    }

    /// <summary>
    /// Order of operations: requests the handler rejects before secret verification keep their answer
    /// and spend no client budget.
    /// </summary>
    [TestFixture]
    public class Given_token_requests_that_fail_before_secret_verification
    {
        private ITokenManager _tokenManager = null!;
        private List<int> _statuses = null!;

        [SetUp]
        public async Task Setup()
        {
            _tokenManager = SucceedingTokenManager();
            await using WebApplicationFactory<Program> factory = Create(
                sourcePermitLimit: 1000,
                clientPermitLimit: 1,
                tokenManager: _tokenManager
            );
            using HttpClient client = factory.CreateClient();
            _statuses = await SendAll(
                client,
                [
                    () => Post("/connect/token", TokenForm("ClientA", grantType: "password")),
                    () => Post("/connect/token", TokenForm("ClientA", grantType: "password")),
                    () => Post("/connect/token", TokenForm("ClientA")),
                ]
            );
        }

        [Test]
        public void It_keeps_their_answers_and_admits_the_valid_request() =>
            _statuses.Should().Equal(400, 400, 200);

        [Test]
        public void It_verifies_only_the_valid_request() => ManagerCalls(_tokenManager, 1);
    }

    [TestFixture]
    public class Given_a_client_over_its_revocation_limit
    {
        private ITokenRevocationManager _revocationManager = null!;
        private List<int> _statuses = null!;
        private HttpResponseMessage _rejected = null!;
        private JsonObject _body = null!;

        [SetUp]
        public async Task Setup()
        {
            _revocationManager = CompletingRevocationManager();
            await using WebApplicationFactory<Program> factory = Create(
                sourcePermitLimit: 1000,
                clientPermitLimit: 2,
                revocationManager: _revocationManager
            );
            using HttpClient client = factory.CreateClient();
            _statuses = await SendAll(
                client,
                [
                    () => Post("/connect/revoke", RevokeForm("ClientA")),
                    () => Post("/connect/revoke", RevokeForm("ClientA")),
                ]
            );
            _rejected = await client.SendAsync(Post("/connect/revoke", RevokeForm("ClientA")));
            _body = JsonNode.Parse(await _rejected.Content.ReadAsStringAsync())!.AsObject();
        }

        [TearDown]
        public void TearDown() => _rejected.Dispose();

        [Test]
        public void It_admits_up_to_the_limit() => _statuses.Should().Equal(200, 200);

        [Test]
        public void It_answers_429_in_the_ed_fi_format()
        {
            ((int)_rejected.StatusCode).Should().Be(429);
            _rejected.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
            _body.Count.Should().Be(7);
            _body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:management-api:too-many-requests");
            _body["detail"]!.GetValue<string>().Should().Be(Detail);
            _body["title"]!.GetValue<string>().Should().Be("Too Many Requests");
            _body["status"]!.GetValue<int>().Should().Be(429);
            _body["correlationId"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
            _body["validationErrors"]!.AsObject().Count.Should().Be(0);
            _body["errors"]!.AsArray().Count.Should().Be(0);
        }

        [Test]
        public void It_sends_a_whole_second_retry_after() =>
            _rejected.Headers.RetryAfter!.Delta!.Value.TotalSeconds.Should().BeInRange(1, 60);

        [Test]
        public void It_does_not_revoke_for_the_rejected_request() => RevocationCalls(_revocationManager, 2);
    }

    [TestFixture]
    public class Given_revocations_that_fail_shape_checks
    {
        private ITokenRevocationManager _revocationManager = null!;
        private List<int> _statuses = null!;

        private static FormUrlEncodedContent WithoutToken() =>
            new([new("client_id", "ClientA"), new("client_secret", ClientSecret)]);

        [SetUp]
        public async Task Setup()
        {
            _revocationManager = CompletingRevocationManager();
            await using WebApplicationFactory<Program> factory = Create(
                sourcePermitLimit: 1000,
                clientPermitLimit: 1,
                revocationManager: _revocationManager
            );
            using HttpClient client = factory.CreateClient();
            _statuses = await SendAll(
                client,
                [
                    () => Post("/connect/revoke", WithoutToken()),
                    () => Post("/connect/revoke", WithoutToken()),
                    () => Post("/connect/revoke", RevokeForm("ClientA")),
                ]
            );
        }

        [Test]
        public void It_keeps_their_answers_and_admits_the_valid_request() =>
            _statuses.Should().Equal(400, 400, 200);

        [Test]
        public void It_revokes_only_for_the_valid_request() => RevocationCalls(_revocationManager, 1);
    }

    [TestFixture("/connect/token", "/connect/revoke", "/connect/introspect")]
    [TestFixture("/connect/introspect", "/connect/token", "/connect/revoke")]
    [TestFixture("/connect/revoke", "/connect/introspect", "/connect/token")]
    public class Given_authenticated_connect_requests_for_one_client(
        string first,
        string second,
        string third
    )
    {
        private List<int> _statuses = null!;
        private ITokenManager _tokenManager = null!;
        private ITokenRevocationManager _revocationManager = null!;
        private ITokenIntrospectionManager _introspectionManager = null!;

        private static HttpRequestMessage Request(string path) =>
            Post(path, path == "/connect/token" ? TokenForm("ClientA") : RevokeForm("ClientA"));

        [SetUp]
        public async Task Setup()
        {
            _tokenManager = SucceedingTokenManager();
            _revocationManager = CompletingRevocationManager();
            _introspectionManager = InactiveIntrospectionManager();
            await using WebApplicationFactory<Program> factory = Create(
                sourcePermitLimit: 1000,
                clientPermitLimit: 3,
                tokenManager: _tokenManager,
                revocationManager: _revocationManager,
                introspectionManager: _introspectionManager
            );
            using HttpClient client = factory.CreateClient();
            _statuses = await SendAll(
                client,
                [
                    () => Request(first),
                    () => Request(second),
                    () => Request(third),
                    () => Request(first),
                    () => Request(second),
                    () => Request(third),
                ]
            );
        }

        [Test]
        public void It_spends_one_client_budget_across_all_authenticated_routes() =>
            _statuses.Should().Equal(200, 200, 200, 429, 429, 429);

        [Test]
        public void It_calls_each_manager_only_for_its_admitted_request()
        {
            ManagerCalls(_tokenManager, 1);
            RevocationCalls(_revocationManager, 1);
            IntrospectionCalls(_introspectionManager, 1);
        }
    }

    [TestFixture(false)]
    [TestFixture(true)]
    public class Given_a_client_over_its_introspection_limit(bool useBasic)
    {
        private ITokenIntrospectionManager _manager = null!;
        private List<int> _statuses = [];
        private HttpResponseMessage _rejected = null!;
        private JsonObject _body = null!;

        private HttpRequestMessage Request()
        {
            HttpRequestMessage request = Post(
                "/connect/introspect",
                useBasic ? new FormUrlEncodedContent([new("token", "opaque-token")]) : RevokeForm("ClientA")
            );
            if (useBasic)
            {
                request.Headers.Authorization = Basic("ClientA");
            }
            return request;
        }

        [SetUp]
        public async Task Setup()
        {
            _manager = InactiveIntrospectionManager();
            await using WebApplicationFactory<Program> factory = Create(
                sourcePermitLimit: 1000,
                clientPermitLimit: 2,
                introspectionManager: _manager
            );
            using HttpClient client = factory.CreateClient();
            _statuses = await SendAll(client, [Request, Request]);
            _rejected = await client.SendAsync(Request());
            _body = JsonNode.Parse(await _rejected.Content.ReadAsStringAsync())!.AsObject();
        }

        [TearDown]
        public void TearDown() => _rejected.Dispose();

        [Test]
        public void It_admits_up_to_the_limit() => _statuses.Should().Equal(200, 200);

        [Test]
        public void It_answers_429_with_the_complete_ed_fi_contract()
        {
            ((int)_rejected.StatusCode).Should().Be(429);
            _rejected.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
            _body.Count.Should().Be(7);
            _body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:management-api:too-many-requests");
            _body["detail"]!.GetValue<string>().Should().Be(Detail);
            _body["title"]!.GetValue<string>().Should().Be("Too Many Requests");
            _body["status"]!.GetValue<int>().Should().Be(429);
            _body["correlationId"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
            _body["validationErrors"]!.AsObject().Count.Should().Be(0);
            _body["errors"]!.AsArray().Count.Should().Be(0);
        }

        [Test]
        public void It_sends_retry_after() =>
            _rejected.Headers.RetryAfter!.Delta!.Value.TotalSeconds.Should().BeInRange(1, 60);

        [Test]
        public void It_preserves_security_headers() =>
            _rejected.Headers.GetValues("X-Content-Type-Options").Should().Equal("nosniff");

        [Test]
        public void It_skips_secret_verification_for_the_rejected_request() =>
            IntrospectionCalls(_manager, 2);
    }

    [TestFixture]
    public class Given_introspection_requests_that_fail_shape_checks
    {
        private ITokenIntrospectionManager _manager = null!;
        private List<int> _statuses = [];

        [SetUp]
        public async Task Setup()
        {
            _manager = InactiveIntrospectionManager();
            await using WebApplicationFactory<Program> factory = Create(
                sourcePermitLimit: 1000,
                clientPermitLimit: 1,
                introspectionManager: _manager
            );
            using HttpClient client = factory.CreateClient();
            _statuses = await SendAll(
                client,
                [
                    () =>
                        Post(
                            "/connect/introspect",
                            new FormUrlEncodedContent([
                                new("client_id", "ClientA"),
                                new("client_secret", ClientSecret),
                            ])
                        ),
                    () => Post("/connect/introspect", RevokeForm("ClientA")),
                    () =>
                        Post(
                            "/connect/introspect",
                            new FormUrlEncodedContent([
                                new("client_id", "ClientA"),
                                new("client_secret", ClientSecret),
                            ])
                        ),
                    () => Post("/connect/introspect", RevokeForm("ClientA")),
                ]
            );
        }

        [Test]
        public void It_keeps_shape_errors_before_client_admission_even_after_exhaustion() =>
            _statuses.Should().Equal(400, 200, 400, 429);

        [Test]
        public void It_verifies_only_the_admitted_request() => IntrospectionCalls(_manager, 1);
    }
}
