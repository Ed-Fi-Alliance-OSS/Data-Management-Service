// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.OpenIddict.SigningKeys;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.SigningKeys;

public class SigningKeyProviderStatusTests
{
    private static readonly SigningKeySnapshot _snapshot = new(
        [],
        DateTimeOffset.UnixEpoch,
        retrievedAtTimestamp: 0,
        1,
        SigningKeySource.Database
    );

    [TestFixture]
    public class Given_a_state_of_none_with_a_snapshot
    {
        [Test]
        public void It_is_refused()
        {
            Action create = () =>
                _ = new SigningKeyProviderStatus(
                    SigningKeySnapshotState.None,
                    _snapshot,
                    0,
                    DateTimeOffset.UnixEpoch,
                    false,
                    null
                );

            create.Should().Throw<ArgumentException>().WithParameterName("state");
        }
    }

    [TestFixture]
    public class Given_a_usable_state_without_a_snapshot
    {
        [Test]
        public void It_is_refused()
        {
            Action create = () =>
                _ = new SigningKeyProviderStatus(
                    SigningKeySnapshotState.Fresh,
                    null,
                    0,
                    DateTimeOffset.UnixEpoch,
                    false,
                    null
                );

            create.Should().Throw<ArgumentException>().WithParameterName("state");
        }
    }

    [TestFixture]
    public class Given_a_consistent_status
    {
        private SigningKeyProviderStatus _status = null!;

        [SetUp]
        public void Act() =>
            _status = new SigningKeyProviderStatus(
                SigningKeySnapshotState.Overdue,
                _snapshot,
                2,
                DateTimeOffset.UnixEpoch.AddSeconds(10),
                true,
                new SigningKeyRefreshOutcome.Refused(DateTimeOffset.UnixEpoch.AddSeconds(10))
            );

        [Test]
        public void It_keeps_the_failure_count() => _status.ConsecutiveFailures.Should().Be(2);

        [Test]
        public void It_keeps_the_next_attempt_time() =>
            _status.NextAttemptAt.Should().Be(DateTimeOffset.UnixEpoch.AddSeconds(10));

        [Test]
        public void It_keeps_the_snapshot() => _status.Current.Should().BeSameAs(_snapshot);
    }
}
