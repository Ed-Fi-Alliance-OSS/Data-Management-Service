// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.OpenIddict.Models;
using EdFi.DmsConfigurationService.Backend.OpenIddict.SigningKeys;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using static EdFi.DmsConfigurationService.Backend.Tests.Unit.SigningKeys.SigningKeyTestSupport;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.SigningKeys;

public class SigningKeyConfigurationManagerTests
{
    private const string Authority = "https://cms.example.test";

    private static SigningKeyConfigurationManager Manager(ISigningKeySnapshotProvider provider) =>
        new(provider, Options.Create(new IdentityOptions { Authority = Authority }));

    private static IEnumerable<string> KeyIdsOf(OpenIdConnectConfiguration configuration) =>
        configuration.SigningKeys.Select(key => key.KeyId);

    // D-2, V-2: a plain manager is awaited per request and copied into that request's parameters. A
    // BaseConfigurationManager would instead be handed to IdentityModel with its last-known-good fallback.
    [TestFixture]
    public class Given_the_configuration_manager_type
    {
        [Test]
        public void It_is_a_configuration_manager() =>
            typeof(SigningKeyConfigurationManager)
                .Should()
                .Implement<IConfigurationManager<OpenIdConnectConfiguration>>();

        [Test]
        public void It_is_not_a_base_configuration_manager() =>
            typeof(SigningKeyConfigurationManager)
                .IsAssignableTo(typeof(BaseConfigurationManager))
                .Should()
                .BeFalse();
    }

    // 2.3-a: one configuration per snapshot version.
    [TestFixture]
    public class Given_one_snapshot_version
    {
        private KeyRepositoryHarness _harness = null!;
        private SigningKeySnapshotProvider _provider = null!;
        private OpenIdConnectConfiguration _first = null!;
        private OpenIdConnectConfiguration _second = null!;

        [SetUp]
        public async Task Act()
        {
            var time = NewTime();
            _harness = new KeyRepositoryHarness(time);
            _harness.ReturnsKeys("key-1", "key-2");
            _provider = Provider(_harness, time);
            SigningKeyConfigurationManager manager = Manager(_provider);

            _first = await manager.GetConfigurationAsync(CancellationToken.None).Bounded();
            _second = await manager.GetConfigurationAsync(CancellationToken.None).Bounded();
        }

        [TearDown]
        public void TearDown() => _provider.Dispose();

        [Test]
        public void It_reuses_the_configuration() => _second.Should().BeSameAs(_first);

        [Test]
        public void It_carries_the_snapshot_keys() => KeyIdsOf(_first).Should().Equal("key-1", "key-2");

        [Test]
        public void It_carries_the_snapshot_key_material() =>
            ((RsaSecurityKey)_first.SigningKeys.First())
                .Parameters.Modulus.Should()
                .Equal(_provider.Current!.Keys[0].PublicParameters.Modulus);

        [Test]
        public void It_declares_the_configured_authority_as_issuer() => _first.Issuer.Should().Be(Authority);

        [Test]
        public void It_reads_the_key_store_once() => _harness.Calls.Should().ContainSingle();
    }

    // 2.3-a: a newly published version gets its own configuration, which is then reused in turn.
    [TestFixture]
    public class Given_a_new_snapshot_version
    {
        private SigningKeySnapshotProvider _provider = null!;
        private OpenIdConnectConfiguration _before = null!;
        private OpenIdConnectConfiguration _after = null!;
        private OpenIdConnectConfiguration _afterAgain = null!;

        [SetUp]
        public async Task Act()
        {
            var time = NewTime();
            KeyRepositoryHarness harness = new(time);
            harness.ReturnsKeys("key-1");
            _provider = Provider(harness, time);
            SigningKeyConfigurationManager manager = Manager(_provider);

            _before = await manager.GetConfigurationAsync(CancellationToken.None).Bounded();
            harness.ReturnsKeys("key-2");
            (await _provider.RefreshAsync(SigningKeyRefreshTrigger.Timer, CancellationToken.None).Bounded())
                .Should()
                .BeOfType<SigningKeyRefreshOutcome.Succeeded>();
            _after = await manager.GetConfigurationAsync(CancellationToken.None).Bounded();
            _afterAgain = await manager.GetConfigurationAsync(CancellationToken.None).Bounded();
        }

        [TearDown]
        public void TearDown() => _provider.Dispose();

        [Test]
        public void It_builds_a_new_configuration() => _after.Should().NotBeSameAs(_before);

        [Test]
        public void It_carries_the_new_keys() => KeyIdsOf(_after).Should().Equal("key-2");

        [Test]
        public void It_leaves_the_earlier_configuration_unchanged() =>
            KeyIdsOf(_before).Should().Equal("key-1");

        [Test]
        public void It_reuses_the_new_configuration() => _afterAgain.Should().BeSameAs(_after);
    }

    // 2.3-a: callers that first see a version together all get the one configuration built for it.
    [TestFixture]
    public class Given_concurrent_first_callers
    {
        private SigningKeySnapshotProvider _provider = null!;
        private OpenIdConnectConfiguration[] _configurations = [];

        [SetUp]
        public async Task Act()
        {
            var time = NewTime();
            KeyRepositoryHarness harness = new(time);
            harness.ReturnsKeys("key-1");
            _provider = Provider(harness, time);
            SigningKeyConfigurationManager manager = Manager(_provider);
            await _provider.GetUsableAsync(CancellationToken.None).Bounded();

            // Dedicated threads: 32 blocking barrier waits on pool threads would wait on thread-pool growth instead.
            using Barrier start = new(32);
            _configurations = await Task.WhenAll(
                    Enumerable
                        .Range(0, 32)
                        .Select(_ =>
                            Task.Factory.StartNew(
                                    () =>
                                    {
                                        start.SignalAndWait();
                                        return manager.GetConfigurationAsync(CancellationToken.None);
                                    },
                                    CancellationToken.None,
                                    TaskCreationOptions.LongRunning,
                                    TaskScheduler.Default
                                )
                                .Unwrap()
                        )
                )
                .Bounded();
        }

        [TearDown]
        public void TearDown() => _provider.Dispose();

        [Test]
        public void It_gives_every_caller_the_same_configuration() =>
            _configurations.Distinct().Should().ContainSingle();
    }

    // 2.3-b: no usable snapshot is the typed dependency failure.
    [TestFixture]
    public class Given_no_usable_snapshot
    {
        private SigningKeySnapshotProvider _provider = null!;
        private Exception? _thrown;

        [SetUp]
        public async Task Act()
        {
            var time = NewTime();
            KeyRepositoryHarness harness = new(time);
            harness.Fails(new TimeoutException("store down"));
            _provider = Provider(harness, time);

            try
            {
                await Manager(_provider).GetConfigurationAsync(CancellationToken.None).Bounded();
            }
            catch (Exception exception)
            {
                _thrown = exception;
            }
        }

        [TearDown]
        public void TearDown() => _provider.Dispose();

        [Test]
        public void It_throws_signing_keys_unavailable() =>
            _thrown.Should().BeOfType<SigningKeysUnavailableException>();
    }

    // The request's abort token cancels only that request's wait: the shared load continues and serves later callers.
    [TestFixture]
    public class Given_a_caller_that_cancels_its_wait_for_a_cold_load
    {
        private KeyRepositoryHarness _harness = null!;
        private SigningKeySnapshotProvider _provider = null!;
        private Exception? _thrown;
        private OpenIdConnectConfiguration _later = null!;

        [SetUp]
        public async Task Act()
        {
            var time = NewTime();
            _harness = new KeyRepositoryHarness(time);
            TaskCompletionSource<IEnumerable<PublicKeyInfo>> gate = _harness.Gate();
            _provider = Provider(_harness, time);
            SigningKeyConfigurationManager manager = Manager(_provider);

            using CancellationTokenSource requestAborted = new();
            Task<OpenIdConnectConfiguration> waiting = manager.GetConfigurationAsync(requestAborted.Token);
            await _harness.WaitForCallsAsync(1);
            await requestAborted.CancelAsync();
            try
            {
                await waiting.Bounded();
            }
            catch (Exception exception)
            {
                _thrown = exception;
            }

            gate.SetResult([KeyRepositoryHarness.Row("key-1")]);
            _later = await manager.GetConfigurationAsync(CancellationToken.None).Bounded();
        }

        [TearDown]
        public void TearDown() => _provider.Dispose();

        [Test]
        public void It_cancels_the_callers_wait() =>
            _thrown.Should().BeAssignableTo<OperationCanceledException>();

        [Test]
        public void It_leaves_the_shared_load_running() =>
            _harness.Calls.Single().Token.IsCancellationRequested.Should().BeFalse();

        [Test]
        public void It_serves_the_load_to_a_later_caller() => KeyIdsOf(_later).Should().Equal("key-1");

        [Test]
        public void It_reads_the_key_store_once() => _harness.Calls.Should().ContainSingle();
    }

    // Q14/I-6: the handler's key-not-found refresh request carries no key id, so it would bypass the cooldown; it starts
    // nothing.
    [TestFixture]
    public class Given_a_refresh_request
    {
        private SigningKeySnapshotProvider _provider = null!;
        private long _stateBefore;
        private long _stateAfter;

        [SetUp]
        public async Task Act()
        {
            var time = NewTime();
            KeyRepositoryHarness harness = new(time);
            harness.ReturnsKeys("key-1");
            _provider = Provider(harness, time);
            SigningKeyConfigurationManager manager = Manager(_provider);
            await manager.GetConfigurationAsync(CancellationToken.None).Bounded();

            _stateBefore = _provider.Status.StateVersion;
            manager.RequestRefresh();
            _stateAfter = _provider.Status.StateVersion;
        }

        [TearDown]
        public void TearDown() => _provider.Dispose();

        // Starting an attempt changes the state version synchronously, under the gate lock.
        [Test]
        public void It_starts_no_load() => _stateAfter.Should().Be(_stateBefore);
    }
}
