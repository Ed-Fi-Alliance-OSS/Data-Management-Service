// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Models;
using EdFi.DmsConfigurationService.Backend.OpenIddict.SigningKeys;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.Tokens;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.SigningKeys;

public class SigningKeySnapshotTests
{
    private static readonly DateTimeOffset _retrievedAt = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    // RefreshInterval 300 s, MaxStaleness 3600 s.
    private static readonly SigningKeySettings _settings = SigningKeySettings.FromIdentityOptions(
        new IdentityOptions()
    );

    internal static SigningKeyEntry Entry(string keyId)
    {
        using RSA rsa = RSA.Create(2048);
        return SigningKeyEntry.FromRsaPublicParameters(keyId, rsa.ExportParameters(false));
    }

    private static SigningKeySnapshot Snapshot(params SigningKeyEntry[] keys) =>
        new(keys, _retrievedAt, 0, 1, SigningKeySource.Database);

    /// <summary>A snapshot published now by <paramref name="time"/>, stamped with both of its clocks.</summary>
    private static SigningKeySnapshot PublishedBy(TimeProvider time) =>
        new([Entry("key-1")], time.GetUtcNow(), time.GetTimestamp(), 1, SigningKeySource.Database);

    private static SkewableTimeProvider NewSkewableTime() => new(new FakeTimeProvider(_retrievedAt));

    [TestFixture]
    public class Given_keys_from_a_list_that_later_changes
    {
        private SigningKeySnapshot _snapshot = null!;

        [SetUp]
        public void Act()
        {
            List<SigningKeyEntry> keys = [Entry("key-1"), Entry("key-2")];
            _snapshot = new SigningKeySnapshot(keys, _retrievedAt, 42, 7, SigningKeySource.Certificate);

            keys.Add(Entry("key-3"));
            keys.RemoveAt(0);
        }

        [Test]
        public void It_keeps_the_keys_it_was_given_in_order() =>
            _snapshot.Keys.Select(key => key.KeyId).Should().Equal("key-1", "key-2");

        [Test]
        public void It_creates_validation_keys_in_the_same_order() =>
            _snapshot.CreateSecurityKeys().Select(key => key.KeyId).Should().Equal("key-1", "key-2");

        [Test]
        public void It_keeps_the_version() => _snapshot.Version.Should().Be(7);

        [Test]
        public void It_keeps_the_source() => _snapshot.Source.Should().Be(SigningKeySource.Certificate);

        [Test]
        public void It_keeps_the_retrieval_time() => _snapshot.RetrievedAt.Should().Be(_retrievedAt);

        [Test]
        public void It_keeps_the_retrieval_timestamp() => _snapshot.RetrievedAtTimestamp.Should().Be(42);
    }

    [TestFixture]
    public class Given_no_keys
    {
        [Test]
        public void It_is_a_valid_empty_snapshot() => Snapshot().Keys.Should().BeEmpty();
    }

    [TestFixture]
    public class Given_a_consumer_mutates_the_validation_keys_it_was_given
    {
        private SigningKeySnapshot _snapshot = null!;
        private IReadOnlyList<SecurityKey> _handedOut = null!;
        private byte[] _originalModulus = null!;
        private byte[] _originalExponent = null!;

        [SetUp]
        public void Act()
        {
            _snapshot = Snapshot(Entry("key-1"));
            _originalModulus = _snapshot.Keys[0].PublicParameters.Modulus!;
            _originalExponent = _snapshot.Keys[0].PublicParameters.Exponent!;

            _handedOut = _snapshot.CreateSecurityKeys();
            var key = (RsaSecurityKey)_handedOut[0];
            key.KeyId = "attacker";
            key.Parameters.Modulus![0] ^= 0xFF;
            key.Parameters.Exponent![0] ^= 0xFF;
        }

        [Test]
        public void It_still_finds_the_original_key_id() =>
            _snapshot.ContainsKeyId("key-1").Should().BeTrue();

        [Test]
        public void It_does_not_find_the_injected_key_id() =>
            _snapshot.ContainsKeyId("attacker").Should().BeFalse();

        [Test]
        public void It_creates_later_validation_keys_with_the_original_key_id() =>
            _snapshot.CreateSecurityKeys().Select(key => key.KeyId).Should().Equal("key-1");

        [Test]
        public void It_creates_later_validation_keys_with_the_original_parameters()
        {
            var later = (RsaSecurityKey)_snapshot.CreateSecurityKeys()[0];

            later.Parameters.Modulus.Should().Equal(_originalModulus);
            later.Parameters.Exponent.Should().Equal(_originalExponent);
        }

        [Test]
        public void It_keeps_the_jwks_projection_unchanged()
        {
            _snapshot.Keys[0].PublicParameters.Modulus.Should().Equal(_originalModulus);
            _snapshot.Keys[0].PublicParameters.Exponent.Should().Equal(_originalExponent);
        }

        [Test]
        public void It_creates_new_instances_each_time() =>
            _snapshot.CreateSecurityKeys()[0].Should().NotBeSameAs(_handedOut[0]);
    }

    [TestFixture]
    public class Given_a_version_below_one
    {
        [Test]
        public void It_is_refused()
        {
            Action create = () =>
                _ = new SigningKeySnapshot([], _retrievedAt, 0, 0, SigningKeySource.Database);

            create.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("version");
        }
    }

    [TestFixture]
    public class Given_a_null_key
    {
        [Test]
        public void It_is_refused()
        {
            Action create = () =>
                _ = new SigningKeySnapshot(
                    [Entry("key-1"), null!],
                    _retrievedAt,
                    0,
                    1,
                    SigningKeySource.Database
                );

            create.Should().Throw<ArgumentException>().WithParameterName("keys");
        }
    }

    [TestFixture]
    public class Given_a_key_id_lookup
    {
        private SigningKeySnapshot _snapshot = null!;

        [SetUp]
        public void Setup() => _snapshot = Snapshot(Entry("Key-A"));

        [Test]
        public void It_finds_the_exact_key_id() => _snapshot.ContainsKeyId("Key-A").Should().BeTrue();

        [Test]
        public void It_compares_key_ids_ordinally() => _snapshot.ContainsKeyId("key-a").Should().BeFalse();

        [Test]
        public void It_does_not_find_an_absent_key_id() =>
            _snapshot.ContainsKeyId("Key-B").Should().BeFalse();
    }

    [TestFixture]
    public class Given_snapshot_ages_around_the_bounds
    {
        private static SigningKeySnapshotState StateAt(TimeSpan age) =>
            SigningKeySnapshot.GetState(age, _settings);

        [Test]
        public void It_is_fresh_at_retrieval() =>
            StateAt(TimeSpan.Zero).Should().Be(SigningKeySnapshotState.Fresh);

        [Test]
        public void It_is_still_fresh_at_exactly_the_refresh_interval() =>
            StateAt(TimeSpan.FromSeconds(300)).Should().Be(SigningKeySnapshotState.Fresh);

        [Test]
        public void It_is_overdue_just_past_the_refresh_interval() =>
            StateAt(TimeSpan.FromSeconds(300) + TimeSpan.FromTicks(1))
                .Should()
                .Be(SigningKeySnapshotState.Overdue);

        // Exactly MaxStaleness old is still served (spec 1.5-o: "just before" is served, "past" is not).
        [Test]
        public void It_is_still_overdue_at_exactly_the_max_staleness() =>
            StateAt(TimeSpan.FromSeconds(3600)).Should().Be(SigningKeySnapshotState.Overdue);

        [Test]
        public void It_is_expired_just_past_the_max_staleness() =>
            StateAt(TimeSpan.FromSeconds(3600) + TimeSpan.FromTicks(1))
                .Should()
                .Be(SigningKeySnapshotState.Expired);

        [Test]
        public void It_refuses_a_negative_age()
        {
            Action classify = () => StateAt(TimeSpan.FromTicks(-1));

            classify.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("age");
        }
    }

    // R3.1 (f): with the wall clock set back, the monotonic age governs, and the bounds hold exactly on it.
    [TestFixture]
    public class Given_the_wall_clock_set_back_after_publication
    {
        private TimeSpan _ageAfter100Seconds;
        private readonly Dictionary<string, SigningKeySnapshotState> _states = [];

        [SetUp]
        public void Act()
        {
            SkewableTimeProvider time = NewSkewableTime();
            SigningKeySnapshot snapshot = PublishedBy(time);
            time.StepWallClock(TimeSpan.FromHours(-2));

            time.Advance(TimeSpan.FromSeconds(100));
            _ageAfter100Seconds = snapshot.GetAge(time);

            time.Advance(TimeSpan.FromSeconds(200));
            _states["refresh interval"] = State(snapshot, time);
            time.Advance(TimeSpan.FromTicks(1));
            _states["refresh interval + 1 tick"] = State(snapshot, time);
            time.Advance(TimeSpan.FromSeconds(3300) - TimeSpan.FromTicks(1));
            _states["max staleness"] = State(snapshot, time);
            time.Advance(TimeSpan.FromTicks(1));
            _states["max staleness + 1 tick"] = State(snapshot, time);
        }

        private static SigningKeySnapshotState State(SigningKeySnapshot snapshot, TimeProvider time) =>
            SigningKeySnapshot.GetState(snapshot.GetAge(time), _settings);

        [Test]
        public void It_reports_the_monotonic_age() =>
            _ageAfter100Seconds.Should().Be(TimeSpan.FromSeconds(100));

        [Test]
        public void It_is_still_fresh_at_exactly_the_refresh_interval() =>
            _states["refresh interval"].Should().Be(SigningKeySnapshotState.Fresh);

        [Test]
        public void It_is_overdue_just_past_the_refresh_interval() =>
            _states["refresh interval + 1 tick"].Should().Be(SigningKeySnapshotState.Overdue);

        [Test]
        public void It_is_still_overdue_at_exactly_the_max_staleness() =>
            _states["max staleness"].Should().Be(SigningKeySnapshotState.Overdue);

        [Test]
        public void It_is_expired_just_past_the_max_staleness() =>
            _states["max staleness + 1 tick"].Should().Be(SigningKeySnapshotState.Expired);
    }

    // R3.1 (f): with the wall clock set forward and no time elapsed, the wall-clock age governs, at the same bounds.
    [TestFixture]
    public class Given_the_wall_clock_set_forward_after_publication
    {
        private TimeSpan _ageAfterAStep;
        private readonly Dictionary<string, SigningKeySnapshotState> _states = [];

        [SetUp]
        public void Act()
        {
            SkewableTimeProvider time = NewSkewableTime();
            SigningKeySnapshot snapshot = PublishedBy(time);

            time.StepWallClock(TimeSpan.FromSeconds(100));
            _ageAfterAStep = snapshot.GetAge(time);

            time.StepWallClock(TimeSpan.FromSeconds(200));
            _states["refresh interval"] = State(snapshot, time);
            time.StepWallClock(TimeSpan.FromTicks(1));
            _states["refresh interval + 1 tick"] = State(snapshot, time);
            time.StepWallClock(TimeSpan.FromSeconds(3300) - TimeSpan.FromTicks(1));
            _states["max staleness"] = State(snapshot, time);
            time.StepWallClock(TimeSpan.FromTicks(1));
            _states["max staleness + 1 tick"] = State(snapshot, time);
        }

        private static SigningKeySnapshotState State(SigningKeySnapshot snapshot, TimeProvider time) =>
            SigningKeySnapshot.GetState(snapshot.GetAge(time), _settings);

        [Test]
        public void It_reports_the_wall_clock_age() => _ageAfterAStep.Should().Be(TimeSpan.FromSeconds(100));

        [Test]
        public void It_is_still_fresh_at_exactly_the_refresh_interval() =>
            _states["refresh interval"].Should().Be(SigningKeySnapshotState.Fresh);

        [Test]
        public void It_is_overdue_just_past_the_refresh_interval() =>
            _states["refresh interval + 1 tick"].Should().Be(SigningKeySnapshotState.Overdue);

        [Test]
        public void It_is_still_overdue_at_exactly_the_max_staleness() =>
            _states["max staleness"].Should().Be(SigningKeySnapshotState.Overdue);

        [Test]
        public void It_is_expired_just_past_the_max_staleness() =>
            _states["max staleness + 1 tick"].Should().Be(SigningKeySnapshotState.Expired);
    }

    // R3.1 (d) at the snapshot level: expiry is not latched. Reversing a forward step returns the age to the monotonic
    // elapsed time.
    [TestFixture]
    public class Given_a_forward_wall_clock_step_that_is_reversed
    {
        private SigningKeySnapshotState _duringTheStep;
        private TimeSpan _ageAfterTheReversal;
        private SigningKeySnapshotState _afterTheReversal;

        [SetUp]
        public void Act()
        {
            SkewableTimeProvider time = NewSkewableTime();
            SigningKeySnapshot snapshot = PublishedBy(time);
            time.Advance(TimeSpan.FromSeconds(10));

            time.StepWallClock(TimeSpan.FromHours(2));
            _duringTheStep = SigningKeySnapshot.GetState(snapshot.GetAge(time), _settings);

            time.StepWallClock(TimeSpan.FromHours(-2));
            _ageAfterTheReversal = snapshot.GetAge(time);
            _afterTheReversal = SigningKeySnapshot.GetState(_ageAfterTheReversal, _settings);
        }

        [Test]
        public void It_is_expired_while_the_step_stands() =>
            _duringTheStep.Should().Be(SigningKeySnapshotState.Expired);

        [Test]
        public void It_returns_to_the_monotonic_age() =>
            _ageAfterTheReversal.Should().Be(TimeSpan.FromSeconds(10));

        [Test]
        public void It_is_fresh_again() => _afterTheReversal.Should().Be(SigningKeySnapshotState.Fresh);
    }
}
