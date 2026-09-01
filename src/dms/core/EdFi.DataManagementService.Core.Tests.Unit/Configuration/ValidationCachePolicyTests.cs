// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using FakeItEasy;
using FluentAssertions;
using NUnit.Framework;
using static EdFi.DataManagementService.Core.Tests.Unit.Configuration.ValidationCacheSupport;

namespace EdFi.DataManagementService.Core.Tests.Unit.Configuration;

/// <summary>
/// The per-policy-class outcomes of both validation providers: what stays cached, what is dropped, and
/// what the configured expiration actually works out to.
/// </summary>
[TestFixture]
[Parallelizable]
public class ValidationCachePolicyTests
{
    private static DatabaseFingerprint Fingerprint(string hash = "abc123") =>
        new("1.0", hash, 42, new byte[32].ToImmutableArray());

    private static EffectiveDataStoreTarget PrimaryTarget(string connectionString = ConnectionString) =>
        EffectiveDataStoreTarget.Primary(connectionString);

    private static EffectiveDataStoreTarget ReplicaTarget(string connectionString = ConnectionString) =>
        new(EffectiveTargetKind.ReadReplica, connectionString);

    private static DatabaseFingerprintProvider FingerprintProviderOf(
        IDatabaseFingerprintReader reader,
        TimeProvider timeProvider,
        CacheSettings? cacheSettings = null
    ) => new(reader, timeProvider, cacheSettings ?? SettingsWith());

    /// <summary>
    /// The mapping every call site uses. A wrong mapping here would silently give a derivative the
    /// primary's permanent lifetime, which is the failure the policy split exists to prevent.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_A_Target : ValidationCachePolicyTests
    {
        [Test]
        public void It_maps_a_primary_to_the_primary_policy_class()
        {
            ValidationCacheKey
                .For(PrimaryTarget())
                .PolicyClass.Should()
                .Be(ValidationCachePolicyClass.Primary);
        }

        [Test]
        public void It_maps_a_read_replica_to_the_derivative_policy_class()
        {
            ValidationCacheKey
                .For(ReplicaTarget())
                .PolicyClass.Should()
                .Be(ValidationCachePolicyClass.Derivative);
        }

        [Test]
        public void It_maps_a_snapshot_to_the_derivative_policy_class()
        {
            ValidationCacheKey
                .For(new EffectiveDataStoreTarget(EffectiveTargetKind.Snapshot, ConnectionString))
                .PolicyClass.Should()
                .Be(ValidationCachePolicyClass.Derivative);
        }

        [Test]
        public void It_carries_the_configured_string_unchanged()
        {
            const string ExactText = "  Server=replica ; Database=edfi;;  ";

            ValidationCacheKey
                .For(new EffectiveDataStoreTarget(EffectiveTargetKind.Snapshot, ExactText))
                .ConfiguredConnectionString.Should()
                .Be(ExactText);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_Fingerprint_Provider_And_A_Primary_Target : ValidationCachePolicyTests
    {
        [Test]
        public async Task It_caches_a_null_result_permanently()
        {
            ControlledTimeProvider time = new(Start);
            var reader = A.Fake<IDatabaseFingerprintReader>();
            A.CallTo(() => reader.ReadFingerprintAsync(PrimaryTarget())).Returns((DatabaseFingerprint?)null);

            var provider = FingerprintProviderOf(reader, time);
            await provider.ReadFingerprint(PrimaryKey(), PrimaryTarget()).Value;

            time.Advance(TimeSpan.FromDays(1));
            var second = await provider.ReadFingerprint(PrimaryKey(), PrimaryTarget()).Value;

            second.Should().BeNull();
            A.CallTo(() => reader.ReadFingerprintAsync(PrimaryTarget())).MustHaveHappenedOnceExactly();
        }

        /// <summary>
        /// The one retained fault. A malformed primary needs an operator and a restart, so re-reading
        /// it every request would only repeat the same failure against a live database.
        /// </summary>
        [Test]
        public async Task It_retains_a_malformed_fingerprint_failure_permanently()
        {
            ControlledTimeProvider time = new(Start);
            var reader = A.Fake<IDatabaseFingerprintReader>();
            A.CallTo(() => reader.ReadFingerprintAsync(PrimaryTarget()))
                .Throws(() => new DatabaseFingerprintValidationException(["malformed"]));

            var provider = FingerprintProviderOf(reader, time);
            await CatchAsync(provider.ReadFingerprint(PrimaryKey(), PrimaryTarget()).Value);

            time.Advance(TimeSpan.FromDays(1));
            Exception? second = await CatchAsync(
                provider.ReadFingerprint(PrimaryKey(), PrimaryTarget()).Value
            );

            second.Should().BeOfType<DatabaseFingerprintValidationException>();
            A.CallTo(() => reader.ReadFingerprintAsync(PrimaryTarget())).MustHaveHappenedOnceExactly();
        }

        [Test]
        public async Task It_evicts_a_transient_failure()
        {
            ControlledTimeProvider time = new(Start);
            var reader = A.Fake<IDatabaseFingerprintReader>();
            A.CallTo(() => reader.ReadFingerprintAsync(PrimaryTarget()))
                .Throws(() => new TimeoutException())
                .Once()
                .Then.Returns(Fingerprint());

            var provider = FingerprintProviderOf(reader, time);
            await CatchAsync(provider.ReadFingerprint(PrimaryKey(), PrimaryTarget()).Value);

            var second = await provider.ReadFingerprint(PrimaryKey(), PrimaryTarget()).Value;

            second.Should().NotBeNull();
            A.CallTo(() => reader.ReadFingerprintAsync(PrimaryTarget())).MustHaveHappenedTwiceExactly();
        }

        [Test]
        public async Task It_hands_back_a_no_op_token()
        {
            ControlledTimeProvider time = new(Start);
            var reader = A.Fake<IDatabaseFingerprintReader>();
            A.CallTo(() => reader.ReadFingerprintAsync(PrimaryTarget())).Returns(Fingerprint());

            var provider = FingerprintProviderOf(reader, time);
            var read = provider.ReadFingerprint(PrimaryKey(), PrimaryTarget());
            await read.Value;

            read.Token.Invalidate();
            await provider.ReadFingerprint(PrimaryKey(), PrimaryTarget()).Value;

            A.CallTo(() => reader.ReadFingerprintAsync(PrimaryTarget())).MustHaveHappenedOnceExactly();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_Fingerprint_Provider_And_A_Derivative_Target : ValidationCachePolicyTests
    {
        [Test]
        public async Task It_caches_a_success_until_the_expiration_elapses()
        {
            ControlledTimeProvider time = new(Start);
            var reader = A.Fake<IDatabaseFingerprintReader>();
            A.CallTo(() => reader.ReadFingerprintAsync(ReplicaTarget())).Returns(Fingerprint());

            var provider = FingerprintProviderOf(reader, time, SettingsWith(derivativeSeconds: 600));
            await provider.ReadFingerprint(DerivativeKey(), ReplicaTarget()).Value;

            time.Advance(TimeSpan.FromSeconds(599));
            await provider.ReadFingerprint(DerivativeKey(), ReplicaTarget()).Value;
            A.CallTo(() => reader.ReadFingerprintAsync(ReplicaTarget())).MustHaveHappenedOnceExactly();

            time.Advance(TimeSpan.FromSeconds(1));
            await provider.ReadFingerprint(DerivativeKey(), ReplicaTarget()).Value;
            A.CallTo(() => reader.ReadFingerprintAsync(ReplicaTarget())).MustHaveHappenedTwiceExactly();
        }

        /// <summary>
        /// Unlike a primary, a malformed derivative is not retained: the database can be rebuilt under
        /// a running service, so the next request must be able to find it repaired.
        /// </summary>
        [Test]
        public async Task It_evicts_a_malformed_fingerprint_failure()
        {
            ControlledTimeProvider time = new(Start);
            var reader = A.Fake<IDatabaseFingerprintReader>();
            A.CallTo(() => reader.ReadFingerprintAsync(ReplicaTarget()))
                .Throws(() => new DatabaseFingerprintValidationException(["malformed"]))
                .Once()
                .Then.Returns(Fingerprint());

            var provider = FingerprintProviderOf(reader, time);
            await CatchAsync(provider.ReadFingerprint(DerivativeKey(), ReplicaTarget()).Value);

            var second = await provider.ReadFingerprint(DerivativeKey(), ReplicaTarget()).Value;

            second.Should().NotBeNull();
            A.CallTo(() => reader.ReadFingerprintAsync(ReplicaTarget())).MustHaveHappenedTwiceExactly();
        }

        /// <summary>
        /// A configured string no provider can open fails at acquisition, which reaches the cache as an
        /// ordinary fault. It must not be cached, or a corrected configuration would be ignored until
        /// restart.
        /// </summary>
        [Test]
        public async Task It_evicts_a_provider_invalid_connection_string_failure()
        {
            ControlledTimeProvider time = new(Start);
            var reader = A.Fake<IDatabaseFingerprintReader>();
            A.CallTo(() => reader.ReadFingerprintAsync(ReplicaTarget()))
                .Throws(() => new ArgumentException("keyword not supported"))
                .Once()
                .Then.Returns(Fingerprint());

            var provider = FingerprintProviderOf(reader, time);
            await CatchAsync(provider.ReadFingerprint(DerivativeKey(), ReplicaTarget()).Value);
            await provider.ReadFingerprint(DerivativeKey(), ReplicaTarget()).Value;

            A.CallTo(() => reader.ReadFingerprintAsync(ReplicaTarget())).MustHaveHappenedTwiceExactly();
        }

        [Test]
        public async Task It_evicts_a_cancellation_surfacing_as_a_fault()
        {
            ControlledTimeProvider time = new(Start);
            var reader = A.Fake<IDatabaseFingerprintReader>();
            A.CallTo(() => reader.ReadFingerprintAsync(ReplicaTarget()))
                .Throws(() => new OperationCanceledException())
                .Once()
                .Then.Returns(Fingerprint());

            var provider = FingerprintProviderOf(reader, time);
            await CatchAsync(provider.ReadFingerprint(DerivativeKey(), ReplicaTarget()).Value);
            await provider.ReadFingerprint(DerivativeKey(), ReplicaTarget()).Value;

            A.CallTo(() => reader.ReadFingerprintAsync(ReplicaTarget())).MustHaveHappenedTwiceExactly();
        }

        [Test]
        public async Task It_hands_back_a_token_that_drops_the_entry()
        {
            ControlledTimeProvider time = new(Start);
            var reader = A.Fake<IDatabaseFingerprintReader>();
            A.CallTo(() => reader.ReadFingerprintAsync(ReplicaTarget())).Returns(Fingerprint());

            var provider = FingerprintProviderOf(reader, time);
            var read = provider.ReadFingerprint(DerivativeKey(), ReplicaTarget());
            await read.Value;

            read.Token.Invalidate();
            await provider.ReadFingerprint(DerivativeKey(), ReplicaTarget()).Value;

            A.CallTo(() => reader.ReadFingerprintAsync(ReplicaTarget())).MustHaveHappenedTwiceExactly();
        }
    }

    /// <summary>
    /// The unit 5 bound, observed through the cache rather than through the resolver: what an operator
    /// configures is not always what the entry lives for.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_The_Effective_Expiration : ValidationCachePolicyTests
    {
        /// <summary>
        /// How many database reads two requests separated by <paramref name="elapsed" /> cost. One
        /// means the entry survived that long; two means it expired.
        /// </summary>
        private static async Task<int> ReadsWithinAsync(CacheSettings cacheSettings, TimeSpan elapsed)
        {
            ControlledTimeProvider time = new(Start);
            int reads = 0;

            CountingFingerprintReader reader = new(() =>
            {
                reads++;
                return Fingerprint();
            });

            var provider = FingerprintProviderOf(reader, time, cacheSettings);
            await provider.ReadFingerprint(DerivativeKey(), ReplicaTarget()).Value;

            time.Advance(elapsed);
            await provider.ReadFingerprint(DerivativeKey(), ReplicaTarget()).Value;

            return reads;
        }

        private sealed class CountingFingerprintReader(Func<DatabaseFingerprint?> read)
            : IDatabaseFingerprintReader
        {
            public Task<DatabaseFingerprint?> ReadFingerprintAsync(EffectiveDataStoreTarget target) =>
                Task.FromResult(read());
        }

        /// <summary>
        /// A shorter data store TTL wins, so a verdict cannot outlive the configuration its connection
        /// string came from.
        /// </summary>
        [Test]
        public async Task It_is_bounded_by_a_shorter_data_store_ttl()
        {
            CacheSettings cacheSettings = SettingsWith(
                derivativeSeconds: 600,
                dataStoreRefreshEnabled: true,
                dataStoreSeconds: 120
            );

            (await ReadsWithinAsync(cacheSettings, TimeSpan.FromSeconds(120))).Should().Be(2);
            (await ReadsWithinAsync(cacheSettings, TimeSpan.FromSeconds(119))).Should().Be(1);
        }

        /// <summary>
        /// With refresh disabled the data store configuration is held until an explicit reload, so
        /// there is no shorter lifetime and the configured value stands.
        /// </summary>
        [Test]
        public async Task It_is_not_bounded_when_data_store_refresh_is_disabled()
        {
            CacheSettings cacheSettings = SettingsWith(
                derivativeSeconds: 600,
                dataStoreRefreshEnabled: false,
                dataStoreSeconds: 120
            );

            (await ReadsWithinAsync(cacheSettings, TimeSpan.FromSeconds(599))).Should().Be(1);
            (await ReadsWithinAsync(cacheSettings, TimeSpan.FromSeconds(600))).Should().Be(2);
        }

        [Test]
        public async Task It_is_not_bounded_when_the_data_store_ttl_is_not_positive()
        {
            CacheSettings cacheSettings = SettingsWith(
                derivativeSeconds: 600,
                dataStoreRefreshEnabled: true,
                dataStoreSeconds: 0
            );

            (await ReadsWithinAsync(cacheSettings, TimeSpan.FromSeconds(599))).Should().Be(1);
            (await ReadsWithinAsync(cacheSettings, TimeSpan.FromSeconds(600))).Should().Be(2);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_Resource_Key_Provider : ValidationCachePolicyTests
    {
        private static readonly ResourceKeyValidationResult _success =
            new ResourceKeyValidationResult.ValidationSuccess();

        private static readonly ResourceKeyValidationResult _failure =
            new ResourceKeyValidationResult.ValidationFailure("diff");

        [Test]
        public async Task It_caches_a_primary_failure_permanently()
        {
            ControlledTimeProvider time = new(Start);
            ResourceKeyValidationCacheProvider provider = new(time, SettingsWith());
            int validations = 0;

            Task<ResourceKeyValidationResult> Validate()
            {
                validations++;
                return Task.FromResult(_failure);
            }

            await provider.Read(PrimaryKey(), Validate).Value;
            time.Advance(TimeSpan.FromDays(1));
            var read = provider.Read(PrimaryKey(), Validate);
            await read.Value;

            // The middleware invalidates on a failure; for a primary that must change nothing.
            read.Token.Invalidate();
            await provider.Read(PrimaryKey(), Validate).Value;

            validations.Should().Be(1);
        }

        [Test]
        public async Task It_expires_a_derivative_success()
        {
            ControlledTimeProvider time = new(Start);
            ResourceKeyValidationCacheProvider provider = new(time, SettingsWith(derivativeSeconds: 600));
            int validations = 0;

            Task<ResourceKeyValidationResult> Validate()
            {
                validations++;
                return Task.FromResult(_success);
            }

            await provider.Read(DerivativeKey(), Validate).Value;
            time.Advance(TimeSpan.FromSeconds(600));
            await provider.Read(DerivativeKey(), Validate).Value;

            validations.Should().Be(2);
        }

        [Test]
        public async Task It_drops_a_derivative_entry_its_reader_invalidates()
        {
            ControlledTimeProvider time = new(Start);
            ResourceKeyValidationCacheProvider provider = new(time, SettingsWith());
            int validations = 0;

            Task<ResourceKeyValidationResult> Validate()
            {
                validations++;
                return Task.FromResult(_failure);
            }

            var read = provider.Read(DerivativeKey(), Validate);
            await read.Value;
            read.Token.Invalidate();
            await provider.Read(DerivativeKey(), Validate).Value;

            validations.Should().Be(2);
        }

        /// <summary>
        /// This provider retains no fault at all: unlike the fingerprint reader it has no
        /// deterministic-failure exception, because a resource-key mismatch is a returned result.
        /// </summary>
        [TestCase(true)]
        [TestCase(false)]
        public async Task It_evicts_every_fault_on_both_policy_classes(bool primary)
        {
            ControlledTimeProvider time = new(Start);
            ResourceKeyValidationCacheProvider provider = new(time, SettingsWith());
            ValidationCacheKey key = primary ? PrimaryKey() : DerivativeKey();
            int validations = 0;

            Task<ResourceKeyValidationResult> Throwing()
            {
                validations++;
                throw new TimeoutException();
            }

            await CatchAsync(provider.Read(key, Throwing).Value);
            await CatchAsync(provider.Read(key, Throwing).Value);

            validations.Should().Be(2);
        }

        [Test]
        public async Task It_treats_an_asynchronous_factory_fault_the_same_way()
        {
            ControlledTimeProvider time = new(Start);
            ResourceKeyValidationCacheProvider provider = new(time, SettingsWith());
            int validations = 0;

            Task<ResourceKeyValidationResult> Faulting()
            {
                validations++;
                return Task.FromException<ResourceKeyValidationResult>(new TimeoutException());
            }

            await CatchAsync(provider.Read(DerivativeKey(), Faulting).Value);
            await CatchAsync(provider.Read(DerivativeKey(), Faulting).Value);

            validations.Should().Be(2);
        }
    }
}
