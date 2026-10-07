// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Models;
using EdFi.DmsConfigurationService.Backend.OpenIddict.SigningKeys;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using NUnit.Framework;
using static EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure.BearerSchemePipelineTests;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Modules;

/// <summary>
/// Spec §5 step 3.3: the JWKS endpoint over the shared snapshot provider, through the real pipeline
/// (<see cref="BearerPipelineHost"/>). A retrieval failure with no usable snapshot is a 503, never the empty key set a
/// successful retrieval of zero keys produces. Labels as in the spec: [F] demonstrates the fix, [C] is compatibility.
/// </summary>
public class JwksEndpointModuleTests
{
    /// <summary>The members of each key, as the endpoint serialized them before this step.</summary>
    private static readonly string[] _keyMembers =
    [
        "alg",
        "e",
        "key_ops",
        "kid",
        "kty",
        "n",
        "oth",
        "use",
        "x5c",
    ];

    /// <summary>The private RSA members a JWK can carry (RFC 7518 §6.3.2).</summary>
    private static readonly string[] _privateMembers = ["d", "p", "q", "dp", "dq", "qi"];

    private static JsonArray KeysOf(JsonObject body) => body["keys"]!.AsArray();

    // 3.3-a [F]: a cold instance whose key store fails answers 503 with Retry-After and the generic problem body, not
    // the 200 {"keys":[]} that hid the failure (F5), and the store's error text never reaches the client.
    [TestFixture]
    public class Given_a_cold_instance_when_the_key_store_fails
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

            _response = await _host.GetJwksAsync();
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
        public void It_does_not_answer_with_an_empty_key_set() => _content.Should().NotContain("\"keys\"");

        [Test]
        public void It_does_not_disclose_the_store_error() => _content.Should().NotContain("10.0.0.9");

        [Test]
        public async Task It_logs_the_failure_once_with_its_category_and_correlation_id()
        {
            string correlationId = (await BodyOf(_response))["correlationId"]!.GetValue<string>();

            var entry = _host.JwksLogger.Entries.Should().ContainSingle().Subject;
            entry.Level.Should().Be(LogLevel.Error);
            entry.Exception.Should().BeOfType<SigningKeysUnavailableException>();
            entry.Message.Should().Contain(nameof(AuthenticationDependencyCategory.SigningKeyStore));
            entry.Message.Should().Contain(correlationId);
        }

        [Test]
        public void It_reads_the_key_table_only_for_the_startup_load() => _host.Store.KeyReads.Should().Be(1);
    }

    // 3.3-a [F]: active key records that were read but none of which can be used are a failed load, not an empty
    // success, so the endpoint answers 503 rather than an empty key set.
    [TestFixture]
    public class Given_a_cold_instance_whose_key_records_are_all_unusable
    {
        private BearerPipelineHost _host = null!;
        private HttpResponseMessage _response = null!;

        [SetUp]
        public async Task Act()
        {
            _host = new BearerPipelineHost();
            _host.Store.KeyReadBehavior = _ =>
                Task.FromResult<IEnumerable<PublicKeyInfo>>([
                    new PublicKeyInfo { KeyId = "key-1", PublicKey = [1, 2, 3] },
                ]);
            await _host.StartAsync();

            _response = await _host.GetJwksAsync();
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
        public void It_recorded_a_processing_failure() =>
            _host
                .Provider.Status.LastOutcome.Should()
                .BeOfType<SigningKeyRefreshOutcome.Failed>()
                .Which.Kind.Should()
                .Be(SigningKeyFailureKind.Processing);
    }

    // 3.3-a [F], with I-3: while refreshes fail, an overdue snapshot is still published; past the maximum staleness the
    // same instance answers 503, although the old snapshot is still held.
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

            _host.Time.Advance(TimeSpan.FromSeconds(301));
            _overdue = await _host.GetJwksAsync();
            _host.Time.Advance(TimeSpan.FromSeconds(3600 - 301 + 1));
            _expired = await _host.GetJwksAsync();
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

    // 3.3-b [C]: a refresh that fails while a usable snapshot exists does not change the answer: 200 with the keys of
    // that snapshot.
    [TestFixture]
    public class Given_a_failed_refresh_with_a_usable_snapshot
    {
        private BearerPipelineHost _host = null!;
        private long _snapshotVersion;
        private HttpResponseMessage _response = null!;
        private JsonObject _body = null!;

        [SetUp]
        public async Task Act()
        {
            _host = new BearerPipelineHost();
            _host.Store.AddKey("key-1");
            _host.Store.AddKey("key-2");
            await _host.StartWarmAsync();
            _snapshotVersion = _host.Provider.Current!.Version;
            _host.Store.FailKeyReads(new TimeoutException("key store down"));

            // Past the latest jittered refresh deadline (300 s + 10 %), within the maximum staleness: the refresh
            // service's timer load runs and fails.
            _host.Time.Advance(TimeSpan.FromSeconds(331));
            await BearerPipelineHost.WaitUntilAsync(
                () => _host.Provider.Status.LastOutcome is SigningKeyRefreshOutcome.Failed,
                () => "The refresh service's timer load never failed."
            );

            _response = await _host.GetJwksAsync();
            _body = await BodyOf(_response);
        }

        [TearDown]
        public void TearDown()
        {
            _response.Dispose();
            _host.Dispose();
        }

        [Test]
        public void It_answers_200() => _response.StatusCode.Should().Be(HttpStatusCode.OK);

        [Test]
        public void It_serves_the_keys_of_the_snapshot() =>
            KeysOf(_body).Select(key => key!["kid"]!.GetValue<string>()).Should().Equal("key-1", "key-2");

        [Test]
        public void It_kept_the_snapshot_the_failed_refresh_could_not_replace()
        {
            _host.Provider.Current!.Version.Should().Be(_snapshotVersion);
            _host.Provider.Status.ConsecutiveFailures.Should().Be(1);
        }

        // The startup load and the failed timer load; the overdue request's own load is refused by the retry gate.
        [Test]
        public void It_read_the_key_table_for_the_two_loads_only() => _host.Store.KeyReads.Should().Be(2);

        [Test]
        public void It_logs_nothing() => _host.JwksLogger.Entries.Should().BeEmpty();
    }

    // 3.3-c [C]: a successful retrieval of genuinely zero keys is served as the empty key set, distinct from a failure.
    [TestFixture]
    public class Given_a_key_store_with_no_active_key
    {
        private BearerPipelineHost _host = null!;
        private HttpResponseMessage _response = null!;
        private string _content = null!;

        [SetUp]
        public async Task Act()
        {
            _host = new BearerPipelineHost();
            await _host.StartWarmAsync();

            _response = await _host.GetJwksAsync();
            _content = await _response.Content.ReadAsStringAsync();
        }

        [TearDown]
        public void TearDown()
        {
            _response.Dispose();
            _host.Dispose();
        }

        [Test]
        public void It_answers_200() => _response.StatusCode.Should().Be(HttpStatusCode.OK);

        [Test]
        public void It_answers_with_the_empty_key_set() =>
            JsonNode
                .DeepEquals(JsonNode.Parse(_content), JsonNode.Parse("""{"keys":[]}"""))
                .Should()
                .BeTrue();

        [Test]
        public void It_published_an_empty_snapshot() => _host.Provider.Current!.Keys.Should().BeEmpty();

        [Test]
        public void It_does_not_add_a_retry_after() => RetryAfter(_response).Should().BeNull();

        [Test]
        public void It_logs_nothing() => _host.JwksLogger.Entries.Should().BeEmpty();
    }

    // 3.3-d [C]: active keys keep the public-key representation the endpoint has always served, and no private
    // material.
    [TestFixture]
    public class Given_active_keys
    {
        private BearerPipelineHost _host = null!;
        private HttpResponseMessage _response = null!;
        private JsonArray _keys = null!;

        [SetUp]
        public async Task Act()
        {
            _host = new BearerPipelineHost();
            _host.Store.AddKey("key-1");
            _host.Store.AddKey("key-2");
            await _host.StartWarmAsync();

            _response = await _host.GetJwksAsync();
            _keys = KeysOf(await BodyOf(_response));
        }

        [TearDown]
        public void TearDown()
        {
            _response.Dispose();
            _host.Dispose();
        }

        private JsonObject Key(string keyId) =>
            _keys.Single(key => key!["kid"]!.GetValue<string>() == keyId)!.AsObject();

        [Test]
        public void It_answers_200_as_json()
        {
            _response.StatusCode.Should().Be(HttpStatusCode.OK);
            _response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        }

        [Test]
        public void It_serves_every_active_key_in_store_order() =>
            _keys.Select(key => key!["kid"]!.GetValue<string>()).Should().Equal("key-1", "key-2");

        [TestCase("key-1")]
        [TestCase("key-2")]
        public void It_serves_the_rsa_public_key(string keyId)
        {
            RSAParameters expected = _host.Store.SigningKey(keyId).ExportParameters(false);
            JsonObject key = Key(keyId);

            key["kty"]!.GetValue<string>().Should().Be("RSA");
            key["use"]!.GetValue<string>().Should().Be("sig");
            key["alg"]!.GetValue<string>().Should().Be("RS256");
            key["n"]!.GetValue<string>().Should().Be(Base64UrlEncoder.Encode(expected.Modulus));
            key["e"]!.GetValue<string>().Should().Be(Base64UrlEncoder.Encode(expected.Exponent));
        }

        [TestCase("key-1")]
        [TestCase("key-2")]
        public void It_keeps_the_members_of_each_key(string keyId) =>
            Key(keyId).Select(member => member.Key).Order(StringComparer.Ordinal).Should().Equal(_keyMembers);

        [Test]
        public void It_serves_no_private_material() =>
            _keys
                .SelectMany(key => key!.AsObject().Select(member => member.Key))
                .Should()
                .NotContain(_privateMembers);
    }

    // The endpoint reads the shared provider: a request during the startup load waits for that one load and is answered
    // with its keys, rather than reading the key table itself or answering with an empty key set.
    [TestFixture]
    public class Given_a_request_during_the_startup_load
    {
        private BearerPipelineHost _host = null!;
        private bool _completedBeforeTheLoad;
        private HttpResponseMessage _response = null!;
        private JsonObject _body = null!;

        [SetUp]
        public async Task Act()
        {
            _host = new BearerPipelineHost();
            _host.Store.AddKey("key-1");
            TaskCompletionSource gate = _host.Store.GateKeyReads();
            await _host.StartAsync();

            Task<HttpResponseMessage> request = _host.GetJwksAsync();
            await BearerPipelineHost.WaitUntilAsync(
                () => _host.Provider.UsableRequests >= 1,
                () => "The request never reached the provider."
            );
            _completedBeforeTheLoad = request.IsCompleted;

            gate.SetResult();
            _response = await request.WaitAsync(TimeSpan.FromSeconds(30));
            _body = await BodyOf(_response);
        }

        [TearDown]
        public void TearDown()
        {
            _response.Dispose();
            _host.Dispose();
        }

        [Test]
        public void It_holds_the_request_until_the_keys_are_loaded() =>
            _completedBeforeTheLoad.Should().BeFalse();

        [Test]
        public void It_answers_with_the_loaded_keys() =>
            KeysOf(_body).Select(key => key!["kid"]!.GetValue<string>()).Should().Equal("key-1");

        [Test]
        public void It_reads_the_key_table_once() => _host.Store.KeyReads.Should().Be(1);
    }
}
