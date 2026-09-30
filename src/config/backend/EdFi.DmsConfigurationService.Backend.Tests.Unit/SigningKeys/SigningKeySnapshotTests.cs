// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Models;
using EdFi.DmsConfigurationService.Backend.OpenIddict.SigningKeys;
using FluentAssertions;

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
        new(keys, _retrievedAt, 1, SigningKeySource.Database);

    [TestFixture]
    public class Given_keys_from_a_list_that_later_changes
    {
        private SigningKeySnapshot _snapshot = null!;

        [SetUp]
        public void Act()
        {
            List<SigningKeyEntry> keys = [Entry("key-1"), Entry("key-2")];
            _snapshot = new SigningKeySnapshot(keys, _retrievedAt, 7, SigningKeySource.Certificate);

            keys.Add(Entry("key-3"));
            keys.RemoveAt(0);
        }

        [Test]
        public void It_keeps_the_keys_it_was_given_in_order() =>
            _snapshot.Keys.Select(key => key.KeyId).Should().Equal("key-1", "key-2");

        [Test]
        public void It_lists_the_security_keys_in_the_same_order() =>
            _snapshot.SecurityKeys.Select(key => key.KeyId).Should().Equal("key-1", "key-2");

        [Test]
        public void It_keeps_the_version() => _snapshot.Version.Should().Be(7);

        [Test]
        public void It_keeps_the_source() => _snapshot.Source.Should().Be(SigningKeySource.Certificate);

        [Test]
        public void It_keeps_the_retrieval_time() => _snapshot.RetrievedAt.Should().Be(_retrievedAt);
    }

    [TestFixture]
    public class Given_no_keys
    {
        [Test]
        public void It_is_a_valid_empty_snapshot() => Snapshot().Keys.Should().BeEmpty();
    }

    [TestFixture]
    public class Given_a_version_below_one
    {
        [Test]
        public void It_is_refused()
        {
            Action create = () => _ = new SigningKeySnapshot([], _retrievedAt, 0, SigningKeySource.Database);

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
        private readonly SigningKeySnapshot _snapshot = Snapshot(Entry("key-1"));

        private SigningKeySnapshotState StateAt(TimeSpan age) =>
            _snapshot.GetState(_retrievedAt + age, _settings);

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
        public void It_treats_a_clock_moved_backwards_as_fresh() =>
            StateAt(TimeSpan.FromSeconds(-30)).Should().Be(SigningKeySnapshotState.Fresh);
    }
}
