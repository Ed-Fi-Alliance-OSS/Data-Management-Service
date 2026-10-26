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
/// The per-client gate inside /connect/token and /connect/revoke (DMS-1482 REQ-1, REQ-2, REQ-3). The
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

    [TestFixture("/connect/token", false)]
    [TestFixture("/connect/token", true)]
    [TestFixture("/connect/revoke", false)]
    [TestFixture("/connect/revoke", true)]
    public class Given_oversized_client_credentials(string path, bool useBasic)
    {
        private ITokenManager _tokenManager = null!;
        private ITokenRevocationManager _revocationManager = null!;
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
            await using WebApplicationFactory<Program> factory = Create(
                sourcePermitLimit: 1000,
                clientPermitLimit: 1,
                tokenManager: _tokenManager,
                revocationManager: _revocationManager
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
            _statuses.Should().Equal(400, 400, 200, 200, 400, 429);

        [Test]
        public void It_verifies_only_the_two_admitted_credentials()
        {
            ManagerCalls(_tokenManager, path == "/connect/token" ? 2 : 0);
            RevocationCalls(_revocationManager, path == "/connect/revoke" ? 2 : 0);
        }

        [Test]
        public void It_uses_the_routes_existing_validation_contract()
        {
            if (path == "/connect/revoke")
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
        public void It_limits_the_client_across_case_variants_and_sources_but_not_other_clients() =>
            _statuses.Should().Equal(200, 200, 429, 429);
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
                    () => Post("/connect/token", TokenForm("ClientA"), "198.51.100.2"),
                    () => Post("/connect/revoke", RevokeForm("\uFF23lientA"), "198.51.100.3"),
                    () => Post("/connect/revoke", RevokeForm("\uFF23lientB"), "198.51.100.1"),
                    () => Post("/connect/token", TokenForm("ClientB"), "198.51.100.2"),
                ]
            );
        }

        [Test]
        public void It_shares_the_client_budget_across_routes_and_sources() =>
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
        public void It_answers_429_in_the_oauth_format()
        {
            ((int)_rejected.StatusCode).Should().Be(429);
            _rejected.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
            _body.Count.Should().Be(2);
            _body["error"]!.GetValue<string>().Should().Be("temporarily_unavailable");
            _body["error_description"]!.GetValue<string>().Should().Be(Detail);
        }

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

    [TestFixture]
    public class Given_token_and_revocation_requests_for_one_client
    {
        private List<int> _statuses = null!;

        [SetUp]
        public async Task Setup()
        {
            await using WebApplicationFactory<Program> factory = Create(
                sourcePermitLimit: 1000,
                clientPermitLimit: 2
            );
            using HttpClient client = factory.CreateClient();
            _statuses = await SendAll(
                client,
                [
                    () => Post("/connect/token", TokenForm("ClientA")),
                    () => Post("/connect/revoke", RevokeForm("ClientA")),
                    () => Post("/connect/token", TokenForm("ClientA")),
                ]
            );
        }

        [Test]
        public void It_spends_one_client_budget_across_both_routes() =>
            _statuses.Should().Equal(200, 200, 429);
    }
}
