// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Generic;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend.OpenIddict.SigningKeys;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;

/// <summary>
/// Spec §5 step 3.1: the default <c>Bearer</c> scheme through the real pipeline (real <see cref="JwtBearerHandler"/>,
/// token manager, snapshot provider and refresh service) over <see cref="BearerPipelineHost"/>. Labels as in the spec:
/// [F] demonstrates the fix, [C] is compatibility.
/// </summary>
public class BearerSchemePipelineTests
{
    private static readonly TimeSpan _pastCooldown = TimeSpan.FromSeconds(31);

    private static async Task<JsonObject> BodyOf(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();

    private static string? RetryAfter(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Retry-After", out var values) ? string.Join(",", values) : null;

    private static string Challenge(HttpResponseMessage response) =>
        string.Join(",", response.Headers.WwwAuthenticate.Select(header => header.Scheme));

    /// <summary>A 401 from the scheme's ordinary challenge, not a dependency 503.</summary>
    private static void ShouldBeAnOrdinaryUnauthorized(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        Challenge(response).Should().Be("Bearer");
        RetryAfter(response).Should().BeNull();
    }

    /// <summary>The dependency answer (D-9): 503, Retry-After, and the generic problem body (I-7).</summary>
    private static async Task ShouldBeADependency503(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        RetryAfter(response).Should().Be("30");
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        response.Headers.WwwAuthenticate.Should().BeEmpty();

        JsonObject body = await BodyOf(response);
        body["status"]!.GetValue<int>().Should().Be(503);
        body["title"]!.GetValue<string>().Should().Be("Service Unavailable");
        body["detail"]!.GetValue<string>().Should().BeEmpty();
    }

    // 3.1-a [F], 3.1-o [F]: 64 concurrent requests on a cold instance all wait on the one startup load, without a
    // thread each, and all succeed once it publishes. Nothing is fetched over HTTP.
    [TestFixture]
    public class Given_64_concurrent_requests_on_a_cold_instance
    {
        private const int Requests = 64;
        private BearerPipelineHost _host = null!;
        private int _completedBeforeTheLoad;
        private HttpResponseMessage[] _responses = [];
        private JsonObject[] _bodies = [];

        [SetUp]
        public async Task Act()
        {
            _host = new BearerPipelineHost();
            _host.Store.AddKey("key-1");
            TaskCompletionSource gate = _host.Store.GateKeyReads();
            await _host.StartAsync();

            Task<HttpResponseMessage>[] requests =
            [
                .. Enumerable
                    .Range(0, Requests)
                    .Select(_ => _host.GetProfileAsync(_host.Store.Mint("key-1").Token)),
            ];
            await BearerPipelineHost.WaitUntilAsync(
                () => _host.Provider.UsableRequests >= Requests,
                () => $"Only {_host.Provider.UsableRequests} requests reached the provider."
            );
            _completedBeforeTheLoad = requests.Count(request => request.IsCompleted);

            gate.SetResult();
            _responses = await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(30));
            _bodies = await Task.WhenAll(_responses.Select(BodyOf));
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var response in _responses)
            {
                response.Dispose();
            }

            _host.Dispose();
        }

        [Test]
        public void It_holds_every_request_until_the_keys_are_loaded() =>
            _completedBeforeTheLoad.Should().Be(0);

        [Test]
        public void It_answers_every_request_with_200() =>
            _responses.Select(response => response.StatusCode).Should().AllBeEquivalentTo(HttpStatusCode.OK);

        [Test]
        public void It_returns_the_expected_profile_to_every_request() =>
            _bodies
                .Select(body => (body["id"]!.GetValue<int>(), body["definition"]!.GetValue<string>()))
                .Should()
                .AllBeEquivalentTo((7, BearerPipelineHost.ProfileDefinition));

        [Test]
        public void It_reads_the_key_table_once() => _host.Store.KeyReads.Should().Be(1);

        [Test]
        public void It_validates_every_request_with_the_managers_configuration() =>
            _host.Manager.Calls.Should().Be(Requests);

        [Test]
        public void It_checks_the_status_of_every_token() => _host.Store.StatusReads.Should().Be(Requests);

        [Test]
        public void It_sends_nothing_over_the_backchannel() => _host.Backchannel.Sent.Should().BeEmpty();
    }

    // 3.1-b [C]
    [TestFixture]
    public class Given_a_revoked_token
    {
        private BearerPipelineHost _host = null!;
        private HttpResponseMessage _response = null!;

        [SetUp]
        public async Task Act()
        {
            _host = new BearerPipelineHost();
            _host.Store.AddKey("key-1");
            await _host.StartWarmAsync();

            _response = await _host.GetProfileAsync(_host.Store.Mint("key-1", status: "revoked").Token);
        }

        [TearDown]
        public void TearDown()
        {
            _response.Dispose();
            _host.Dispose();
        }

        [Test]
        public void It_answers_401() => ShouldBeAnOrdinaryUnauthorized(_response);

        [Test]
        public void It_checks_the_token_status() => _host.Store.StatusReads.Should().Be(1);

        [Test]
        public void It_never_reaches_the_endpoint() =>
            A.CallTo(() => _host.Profiles.GetProfile(A<int>._)).MustNotHaveHappened();
    }

    // 3.1-c [C] expired, wrong audience, wrong issuer; 3.1-j [C] forged signature; 3.1-k [C] missing kid; and a
    // malformed token. Each is an ordinary 401 with warm keys: no key reload, no status read, nothing over HTTP.
    [TestFixture]
    public class Given_a_token_the_scheme_rejects
    {
        private BearerPipelineHost _host = null!;

        [SetUp]
        public async Task SetUp()
        {
            _host = new BearerPipelineHost();
            _host.Store.AddKey("key-1");
            await _host.StartWarmAsync();
        }

        [TearDown]
        public void TearDown() => _host.Dispose();

        private AuthenticationHeaderValue Authorization(string kind)
        {
            PipelineTokenStore store = _host.Store;
            return kind switch
            {
                "expired" => Bearer(store.Mint("key-1", expires: DateTime.UtcNow.AddMinutes(-10)).Token),
                "wrong-audience" => Bearer(store.Mint("key-1", audience: "another-audience").Token),
                "wrong-issuer" => Bearer(store.Mint("key-1", issuer: "http://localhost/realms/other").Token),
                "forged-signature" => Bearer(store.Mint("key-1", signer: RSA.Create(2048)).Token),
                "missing-kid" => Bearer(store.Mint(keyId: null, signer: store.SigningKey("key-1")).Token),
                "malformed" => Bearer("not-a-jwt"),
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
            };

            static AuthenticationHeaderValue Bearer(string token) => new("Bearer", token);
        }

        [TestCase("expired")]
        [TestCase("wrong-audience")]
        [TestCase("wrong-issuer")]
        [TestCase("forged-signature")]
        [TestCase("missing-kid")]
        [TestCase("malformed")]
        public async Task It_answers_an_ordinary_401(string kind)
        {
            using HttpResponseMessage response = await _host.SendAsync(Authorization(kind));

            ShouldBeAnOrdinaryUnauthorized(response);
            _host.Store.KeyReads.Should().Be(1);
            _host.Store.StatusReads.Should().Be(0);
            _host.Backchannel.Sent.Should().BeEmpty();
            A.CallTo(() => _host.Profiles.GetProfile(A<int>._)).MustNotHaveHappened();
        }
    }

    // 3.1-d [F]: a key id the store has never held gets the one eligible refresh, then every request is still 401, and
    // repeating it inside the cooldown loads nothing more.
    [TestFixture]
    public class Given_a_token_with_a_key_id_the_store_never_held
    {
        private const int Requests = 5;
        private BearerPipelineHost _host = null!;
        private List<HttpStatusCode> _statuses = [];

        [SetUp]
        public async Task Act()
        {
            _host = new BearerPipelineHost();
            _host.Store.AddKey("key-1");
            await _host.StartWarmAsync();
            _host.Time.Advance(_pastCooldown);

            string token = _host.Store.Mint("never-stored", signer: RSA.Create(2048)).Token;
            _statuses = [];
            for (int request = 0; request < Requests; request++)
            {
                using HttpResponseMessage response = await _host.GetProfileAsync(token);
                ShouldBeAnOrdinaryUnauthorized(response);
                _statuses.Add(response.StatusCode);
            }
        }

        [TearDown]
        public void TearDown() => _host.Dispose();

        [Test]
        public void It_answers_every_request_with_401() =>
            _statuses.Should().Equal(Enumerable.Repeat(HttpStatusCode.Unauthorized, Requests));

        [Test]
        public void It_refreshes_the_keys_at_most_once() => _host.Store.KeyReads.Should().Be(2);

        [Test]
        public void It_sends_nothing_over_the_backchannel() => _host.Backchannel.Sent.Should().BeEmpty();
    }

    // 3.1-e [F]: a key inserted after the last load is accepted in the first request that carries it, when the
    // unknown-key refresh is eligible (cooldown elapsed, gate open).
    [TestFixture]
    public class Given_a_new_key_rotated_in_when_the_unknown_key_refresh_is_eligible
    {
        private BearerPipelineHost _host = null!;
        private HttpResponseMessage _response = null!;

        [SetUp]
        public async Task Act()
        {
            _host = new BearerPipelineHost();
            _host.Store.AddKey("key-1");
            await _host.StartWarmAsync();
            _host.Time.Advance(_pastCooldown);
            _host.Store.AddKey("key-2");

            _response = await _host.GetProfileAsync(_host.Store.Mint("key-2").Token);
        }

        [TearDown]
        public void TearDown()
        {
            _response.Dispose();
            _host.Dispose();
        }

        [Test]
        public void It_accepts_the_first_request_with_the_new_key() =>
            _response.StatusCode.Should().Be(HttpStatusCode.OK);

        [Test]
        public void It_loads_the_keys_once_for_it() => _host.Store.KeyReads.Should().Be(2);

        [Test]
        public void It_publishes_the_new_key() =>
            _host.Provider.Current!.ContainsKeyId("key-2").Should().BeTrue();

        [Test]
        public void It_sends_nothing_over_the_backchannel() => _host.Backchannel.Sent.Should().BeEmpty();
    }

    // 3.1-q [F]: the same rotation while the cooldown still runs is 401 without a load, then accepted once the cooldown
    // has elapsed.
    [TestFixture]
    public class Given_a_new_key_rotated_in_during_the_cooldown
    {
        private BearerPipelineHost _host = null!;
        private HttpResponseMessage _duringCooldown = null!;
        private int _keyReadsDuringCooldown;
        private HttpResponseMessage _afterCooldown = null!;

        [SetUp]
        public async Task Act()
        {
            _host = new BearerPipelineHost();
            _host.Store.AddKey("key-1");
            await _host.StartWarmAsync();
            _host.Store.AddKey("key-2");
            string token = _host.Store.Mint("key-2").Token;

            _duringCooldown = await _host.GetProfileAsync(token);
            _keyReadsDuringCooldown = _host.Store.KeyReads;
            _host.Time.Advance(_pastCooldown);
            _afterCooldown = await _host.GetProfileAsync(token);
        }

        [TearDown]
        public void TearDown()
        {
            _duringCooldown.Dispose();
            _afterCooldown.Dispose();
            _host.Dispose();
        }

        [Test]
        public void It_answers_401_during_the_cooldown() => ShouldBeAnOrdinaryUnauthorized(_duringCooldown);

        [Test]
        public void It_loads_nothing_during_the_cooldown() => _keyReadsDuringCooldown.Should().Be(1);

        [Test]
        public void It_accepts_the_new_key_after_the_cooldown() =>
            _afterCooldown.StatusCode.Should().Be(HttpStatusCode.OK);

        [Test]
        public void It_loads_the_keys_once_after_the_cooldown() => _host.Store.KeyReads.Should().Be(2);
    }

    // 3.1-f [F]: a cold instance whose key store fails answers 503 at the boundary; the handler never asks the
    // configuration manager, the token status is never read, and the store's error text never reaches the client.
    [TestFixture]
    public class Given_a_cold_request_when_the_key_store_fails
    {
        private BearerPipelineHost _host = null!;
        private HttpResponseMessage _response = null!;
        private string _content = null!;

        [SetUp]
        public async Task Act()
        {
            _host = new BearerPipelineHost();
            _host.Store.AddKey("key-1");
            _host.Store.FailKeyReads(new TimeoutException("key store down at 10.0.0.9"));
            await _host.StartAsync();

            _response = await _host.GetProfileAsync(_host.Store.Mint("key-1").Token);
            _content = await _response.Content.ReadAsStringAsync();
        }

        [TearDown]
        public void TearDown()
        {
            _response.Dispose();
            _host.Dispose();
        }

        [Test]
        public Task It_answers_a_dependency_503() => ShouldBeADependency503(_response);

        [Test]
        public void It_never_asks_the_configuration_manager() => _host.Manager.Calls.Should().Be(0);

        [Test]
        public void It_never_reads_the_token_status() => _host.Store.StatusReads.Should().Be(0);

        [Test]
        public void It_does_not_disclose_the_store_error() => _content.Should().NotContain("10.0.0.9");

        [Test]
        public void It_never_reaches_the_endpoint() =>
            A.CallTo(() => _host.Profiles.GetProfile(A<int>._)).MustNotHaveHappened();

        [Test]
        public void It_sends_nothing_over_the_backchannel() => _host.Backchannel.Sent.Should().BeEmpty();
    }

    // 3.1-g [F]: warm keys, but the token status cannot be read: 503, not the 401 of an invalid token.
    [TestFixture]
    public class Given_warm_keys_when_the_token_status_store_fails
    {
        private BearerPipelineHost _host = null!;
        private HttpResponseMessage _response = null!;

        [SetUp]
        public async Task Act()
        {
            _host = new BearerPipelineHost();
            _host.Store.AddKey("key-1");
            await _host.StartWarmAsync();
            _host.Store.StatusFailure = new TimeoutException("status store down");

            _response = await _host.GetProfileAsync(_host.Store.Mint("key-1").Token);
        }

        [TearDown]
        public void TearDown()
        {
            _response.Dispose();
            _host.Dispose();
        }

        [Test]
        public Task It_answers_a_dependency_503() => ShouldBeADependency503(_response);

        [Test]
        public void It_validated_the_signature_first() => _host.Manager.Calls.Should().Be(1);

        [Test]
        public void It_never_reaches_the_endpoint() =>
            A.CallTo(() => _host.Profiles.GetProfile(A<int>._)).MustNotHaveHappened();
    }

    // 3.1-h [F]: a retired key stops verifying once the refresh service's own timer has reloaded the keys, with no
    // request asking for it.
    [TestFixture]
    public class Given_a_retired_key_after_the_refresh_interval
    {
        private BearerPipelineHost _host = null!;
        private HttpResponseMessage _beforeRetirement = null!;
        private HttpResponseMessage _afterRetirement = null!;

        [SetUp]
        public async Task Act()
        {
            _host = new BearerPipelineHost();
            _host.Store.AddKey("key-1");
            _host.Store.AddKey("key-2");
            await _host.StartWarmAsync();
            string token = _host.Store.Mint("key-1").Token;
            _beforeRetirement = await _host.GetProfileAsync(token);

            _host.Store.RetireKey("key-1");
            // Past the latest jittered refresh deadline (300 s + 10 %), within the maximum staleness.
            _host.Time.Advance(TimeSpan.FromSeconds(331));
            await BearerPipelineHost.WaitUntilAsync(
                () => _host.Provider.Current is { } current && !current.ContainsKeyId("key-1"),
                () => "The refresh service never published the retirement."
            );

            _afterRetirement = await _host.GetProfileAsync(token);
        }

        [TearDown]
        public void TearDown()
        {
            _beforeRetirement.Dispose();
            _afterRetirement.Dispose();
            _host.Dispose();
        }

        [Test]
        public void It_accepted_the_key_before_it_was_retired() =>
            _beforeRetirement.StatusCode.Should().Be(HttpStatusCode.OK);

        [Test]
        public void It_rejects_the_retired_key() => ShouldBeAnOrdinaryUnauthorized(_afterRetirement);

        // The startup load and the timer's; the rejected request's unknown-key refresh falls inside the cooldown.
        [Test]
        public void It_reloaded_the_keys_only_on_the_timer() => _host.Store.KeyReads.Should().Be(2);
    }

    // 3.1-i [F]: after a cold failure, the next request once the retry deadline has passed succeeds.
    [TestFixture]
    public class Given_the_key_store_recovers
    {
        private BearerPipelineHost _host = null!;
        private HttpResponseMessage _whileFailing = null!;
        private HttpResponseMessage _afterRecovery = null!;

        [SetUp]
        public async Task Act()
        {
            _host = new BearerPipelineHost();
            _host.Store.AddKey("key-1");
            _host.Store.FailKeyReads(new TimeoutException("key store down"));
            await _host.StartAsync();
            string token = _host.Store.Mint("key-1").Token;
            _whileFailing = await _host.GetProfileAsync(token);

            _host.Store.KeyReadBehavior = null;
            // Past the first retry deadline (5 s + 20 %).
            _host.Time.Advance(TimeSpan.FromSeconds(7));
            _afterRecovery = await _host.GetProfileAsync(token);
        }

        [TearDown]
        public void TearDown()
        {
            _whileFailing.Dispose();
            _afterRecovery.Dispose();
            _host.Dispose();
        }

        [Test]
        public Task It_answered_503_while_the_store_failed() => ShouldBeADependency503(_whileFailing);

        [Test]
        public void It_answers_200_after_the_recovery() =>
            _afterRecovery.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // 3.1-l [C]: the token status is read on every request, so a revocation between two requests takes effect at once.
    [TestFixture]
    public class Given_a_token_revoked_between_two_requests
    {
        private BearerPipelineHost _host = null!;
        private HttpResponseMessage _beforeRevocation = null!;
        private HttpResponseMessage _afterRevocation = null!;

        [SetUp]
        public async Task Act()
        {
            _host = new BearerPipelineHost();
            _host.Store.AddKey("key-1");
            await _host.StartWarmAsync();
            var (token, tokenId) = _host.Store.Mint("key-1");

            _beforeRevocation = await _host.GetProfileAsync(token);
            _host.Store.SetStatus(tokenId, "revoked");
            _afterRevocation = await _host.GetProfileAsync(token);
        }

        [TearDown]
        public void TearDown()
        {
            _beforeRevocation.Dispose();
            _afterRevocation.Dispose();
            _host.Dispose();
        }

        [Test]
        public void It_accepts_the_first_request() =>
            _beforeRevocation.StatusCode.Should().Be(HttpStatusCode.OK);

        [Test]
        public void It_rejects_the_second_request() => ShouldBeAnOrdinaryUnauthorized(_afterRevocation);

        [Test]
        public void It_reads_the_status_for_each_request() => _host.Store.StatusReads.Should().Be(2);
    }

    // 3.1-m [F], with I-3: while refreshes fail, an overdue snapshot is still served; past the maximum staleness the
    // same instance fails closed with 503, although the old snapshot is still held.
    [TestFixture]
    public class Given_refreshes_failing_until_past_the_maximum_staleness
    {
        private BearerPipelineHost _host = null!;
        private HttpResponseMessage _overdue = null!;
        private HttpResponseMessage _expired = null!;

        [SetUp]
        public async Task Act()
        {
            _host = new BearerPipelineHost();
            _host.Store.AddKey("key-1");
            await _host.StartWarmAsync();
            _host.Store.FailKeyReads(new TimeoutException("key store down"));
            string token = _host.Store.Mint("key-1").Token;

            _host.Time.Advance(TimeSpan.FromSeconds(301));
            _overdue = await _host.GetProfileAsync(token);
            _host.Time.Advance(TimeSpan.FromSeconds(3600 - 301 + 1));
            _expired = await _host.GetProfileAsync(token);
        }

        [TearDown]
        public void TearDown()
        {
            _overdue.Dispose();
            _expired.Dispose();
            _host.Dispose();
        }

        [Test]
        public void It_serves_the_overdue_snapshot() => _overdue.StatusCode.Should().Be(HttpStatusCode.OK);

        [Test]
        public Task It_answers_503_past_the_maximum_staleness() => ShouldBeADependency503(_expired);

        [Test]
        public void It_still_holds_the_old_snapshot() => _host.Provider.Current.Should().NotBeNull();
    }

    // 3.1-n [F]: waves of requests while every load fails immediately start no load of their own: the gate refuses them
    // until the retry deadline, and each is a 503.
    [TestFixture]
    public class Given_three_waves_of_requests_while_the_key_store_fails
    {
        private const int Waves = 3;
        private const int RequestsPerWave = 16;
        private BearerPipelineHost _host = null!;
        private List<HttpStatusCode> _statuses = [];

        [SetUp]
        public async Task Act()
        {
            _host = new BearerPipelineHost();
            _host.Store.AddKey("key-1");
            _host.Store.FailKeyReads(new TimeoutException("key store down"));
            await _host.StartAsync();
            string token = _host.Store.Mint("key-1").Token;

            _statuses = [];
            for (int wave = 0; wave < Waves; wave++)
            {
                HttpResponseMessage[] responses = await Task.WhenAll(
                    Enumerable.Range(0, RequestsPerWave).Select(_ => _host.GetProfileAsync(token))
                );
                _statuses.AddRange(responses.Select(response => response.StatusCode));
                foreach (var response in responses)
                {
                    response.Dispose();
                }
            }
        }

        [TearDown]
        public void TearDown() => _host.Dispose();

        [Test]
        public void It_answers_every_request_with_503() =>
            _statuses
                .Should()
                .Equal(Enumerable.Repeat(HttpStatusCode.ServiceUnavailable, Waves * RequestsPerWave));

        [Test]
        public void It_reads_the_key_table_once() => _host.Store.KeyReads.Should().Be(1);
    }

    // 3.1-p [C]: a failure after authentication is the endpoint's, unchanged (F8).
    [TestFixture]
    public class Given_a_profile_repository_failure_after_authentication
    {
        private BearerPipelineHost _host = null!;
        private HttpResponseMessage _response = null!;

        [SetUp]
        public async Task Act()
        {
            _host = new BearerPipelineHost();
            _host.Store.AddKey("key-1");
            A.CallTo(() => _host.Profiles.GetProfile(A<int>._))
                .ThrowsAsync(new TimeoutException("profile store down"));
            await _host.StartWarmAsync();

            _response = await _host.GetProfileAsync(_host.Store.Mint("key-1").Token);
        }

        [TearDown]
        public void TearDown()
        {
            _response.Dispose();
            _host.Dispose();
        }

        [Test]
        public void It_answers_500() => _response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        [Test]
        public void It_is_not_classified_as_an_authentication_dependency() =>
            RetryAfter(_response).Should().BeNull();
    }

    private static WebApplicationFactory<Program> CreateFactory(string identityProvider) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("AppSettings:IdentityProvider", identityProvider);
            builder.UseSetting(
                "DatabaseSettings:DatabaseConnection",
                "host=127.0.0.1;port=1;database=unreachable;username=none;timeout=1"
            );
        });

    // 3.1-s [F]: the production wiring, with nothing decorated.
    [TestFixture]
    public class Given_the_bearer_scheme_of_a_self_contained_host
    {
        private WebApplicationFactory<Program> _factory = null!;
        private JwtBearerOptions _options = null!;

        [SetUp]
        public void Act()
        {
            _factory = CreateFactory("self-contained");
            _options = _factory
                .Services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
                .Get(JwtBearerDefaults.AuthenticationScheme);
        }

        [TearDown]
        public void TearDown() => _factory.Dispose();

        [Test]
        public void It_has_no_blocking_key_resolver() =>
            _options.TokenValidationParameters.IssuerSigningKeyResolver.Should().BeNull();

        [Test]
        public void It_uses_the_shared_configuration_manager() =>
            _options
                .ConfigurationManager.Should()
                .BeSameAs(_factory.Services.GetRequiredService<SigningKeyConfigurationManager>());

        // A BaseConfigurationManager would be handed to IdentityModel, whose last-known-good fallback is rejected (V-2).
        [Test]
        public void It_uses_a_plain_configuration_manager() =>
            _options.ConfigurationManager.Should().NotBeAssignableTo<BaseConfigurationManager>();

        [Test]
        public void It_leaves_identity_model_without_a_configuration_manager() =>
            _options.TokenValidationParameters.ConfigurationManager.Should().BeNull();

        [Test]
        public void It_turns_the_framework_unknown_key_refresh_off() =>
            _options.RefreshOnIssuerKeyNotFound.Should().BeFalse();

        [Test]
        public void It_creates_no_backchannel() => _options.Backchannel.Should().BeNull();

        [Test]
        public void It_still_validates_the_issuer()
        {
            _options.TokenValidationParameters.ValidateIssuer.Should().BeTrue();
            _options.TokenValidationParameters.ValidIssuer.Should().Be(BearerPipelineHost.Issuer);
        }

        [Test]
        public void It_still_validates_the_audience()
        {
            _options.TokenValidationParameters.ValidateAudience.Should().BeTrue();
            _options.TokenValidationParameters.ValidAudience.Should().Be(BearerPipelineHost.Audience);
        }

        [Test]
        public void It_still_validates_the_lifetime() =>
            _options.TokenValidationParameters.ValidateLifetime.Should().BeTrue();

        [Test]
        public void It_still_validates_the_signing_key() =>
            _options.TokenValidationParameters.ValidateIssuerSigningKey.Should().BeTrue();

        [Test]
        public void It_runs_the_shared_boundary_on_message_received()
        {
            _options.Events.OnMessageReceived.Target.Should().BeSameAs(BearerEvents());
            _options
                .Events.OnMessageReceived.Method.Name.Should()
                .Be(nameof(SigningKeyBearerEvents.MessageReceivedAsync));
        }

        [Test]
        public void It_runs_the_shared_status_check_on_token_validated()
        {
            _options.Events.OnTokenValidated.Target.Should().BeSameAs(BearerEvents());
            _options
                .Events.OnTokenValidated.Method.Name.Should()
                .Be(nameof(SigningKeyBearerEvents.TokenValidatedAsync));
        }

        private SigningKeyBearerEvents BearerEvents() =>
            _factory.Services.GetRequiredService<SigningKeyBearerEvents>();
    }

    // Keycloak mode keeps the framework's own configuration manager and refresh behavior.
    [TestFixture]
    public class Given_the_bearer_scheme_of_a_keycloak_host
    {
        private WebApplicationFactory<Program> _factory = null!;
        private JwtBearerOptions _options = null!;

        [SetUp]
        public void Act()
        {
            _factory = CreateFactory("keycloak");
            _options = _factory
                .Services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
                .Get(JwtBearerDefaults.AuthenticationScheme);
        }

        [TearDown]
        public void TearDown() => _factory.Dispose();

        [Test]
        public void It_keeps_the_framework_configuration_manager() =>
            _options
                .ConfigurationManager.Should()
                .BeOfType<ConfigurationManager<OpenIdConnectConfiguration>>();

        [Test]
        public void It_keeps_the_framework_unknown_key_refresh() =>
            _options.RefreshOnIssuerKeyNotFound.Should().BeTrue();
    }
}
