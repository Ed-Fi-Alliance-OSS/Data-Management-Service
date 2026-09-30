// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.OpenIddict.SigningKeys;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.SigningKeys;

public class SigningKeyRefreshOutcomeTests
{
    private static SigningKeySnapshot Snapshot(params SigningKeyEntry[] keys) =>
        new(keys, DateTimeOffset.UnixEpoch, 1, SigningKeySource.Database);

    [TestFixture]
    public class Given_a_store_with_no_active_keys
    {
        private SigningKeyRefreshOutcome.Succeeded _outcome = null!;

        [SetUp]
        public void Act() => _outcome = new SigningKeyRefreshOutcome.Succeeded(Snapshot());

        // Succeeded(0): an empty store is a real, publishable result (spec D-4).
        [Test]
        public void It_succeeds_with_zero_keys() => _outcome.KeyCount.Should().Be(0);

        [Test]
        public void It_discarded_nothing() => _outcome.DiscardedEntryCount.Should().Be(0);
    }

    [TestFixture]
    public class Given_some_records_could_not_be_used
    {
        private SigningKeyRefreshOutcome.Succeeded _outcome = null!;

        [SetUp]
        public void Act() =>
            _outcome = new SigningKeyRefreshOutcome.Succeeded(
                Snapshot(SigningKeySnapshotTests.Entry("key-1")),
                discardedEntryCount: 2
            );

        [Test]
        public void It_succeeds_with_the_usable_keys() => _outcome.KeyCount.Should().Be(1);

        [Test]
        public void It_records_the_discarded_records() => _outcome.DiscardedEntryCount.Should().Be(2);
    }

    [TestFixture]
    public class Given_every_record_failed_to_parse
    {
        // Records that exist but none of which parse are Failed(Processing), never an empty key set (I-8).
        [Test]
        public void It_cannot_be_reported_as_an_empty_success()
        {
            Action create = () =>
                _ = new SigningKeyRefreshOutcome.Succeeded(Snapshot(), discardedEntryCount: 3);

            create.Should().Throw<ArgumentException>().WithParameterName("discardedEntryCount");
        }
    }

    [TestFixture]
    public class Given_a_negative_discarded_count
    {
        [Test]
        public void It_is_refused()
        {
            Action create = () =>
                _ = new SigningKeyRefreshOutcome.Succeeded(Snapshot(), discardedEntryCount: -1);

            create.Should().Throw<ArgumentOutOfRangeException>();
        }
    }

    [TestFixture]
    public class Given_a_failed_retrieval
    {
        private readonly TimeoutException _cause = new("timed out");
        private SigningKeyRefreshOutcome.Failed _outcome = null!;

        [SetUp]
        public void Act() =>
            _outcome = new SigningKeyRefreshOutcome.Failed(SigningKeyFailureKind.Retrieval, _cause);

        [Test]
        public void It_keeps_the_kind() => _outcome.Kind.Should().Be(SigningKeyFailureKind.Retrieval);

        [Test]
        public void It_keeps_the_cause() => _outcome.Exception.Should().BeSameAs(_cause);
    }

    [TestFixture]
    public class Given_a_failure_without_a_cause
    {
        [Test]
        public void It_is_refused()
        {
            Action create = () =>
                _ = new SigningKeyRefreshOutcome.Failed(SigningKeyFailureKind.Processing, null!);

            create.Should().Throw<ArgumentNullException>();
        }
    }
}
