// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.OpenIddict.SigningKeys;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.SigningKeys;

public class SigningKeysUnavailableExceptionTests
{
    [TestFixture]
    public class Given_no_snapshot_and_a_failed_retrieval
    {
        // A store failure whose message carries a secret, as a connection-open failure can.
        private readonly InvalidOperationException _cause = new("Host=db;Password=hunter2");
        private SigningKeysUnavailableException _exception = null!;

        [SetUp]
        public void Act() =>
            _exception = new SigningKeysUnavailableException(
                SigningKeysUnavailableReason.NoSnapshot,
                new SigningKeyRefreshOutcome.Failed(SigningKeyFailureKind.Retrieval, _cause)
            );

        // The boundary answers every AuthenticationDependencyUnavailableException with 503.
        [Test]
        public void It_is_an_authentication_dependency_failure() =>
            _exception.Should().BeAssignableTo<AuthenticationDependencyUnavailableException>();

        [Test]
        public void It_names_the_signing_key_store() =>
            _exception.Category.Should().Be(AuthenticationDependencyCategory.SigningKeyStore);

        [Test]
        public void It_keeps_the_reason() =>
            _exception.Reason.Should().Be(SigningKeysUnavailableReason.NoSnapshot);

        [Test]
        public void It_keeps_the_cause_as_the_inner_exception() =>
            _exception.InnerException.Should().BeSameAs(_cause);

        [Test]
        public void It_has_a_fixed_message() =>
            _exception
                .Message.Should()
                .Be("No signing keys have been loaded, and the key store could not be read.");

        [Test]
        public void It_does_not_copy_the_cause_message() => _exception.Message.Should().NotContain("hunter2");
    }

    [TestFixture]
    public class Given_an_expired_snapshot_and_a_processing_failure
    {
        private SigningKeysUnavailableException _exception = null!;

        [SetUp]
        public void Act() =>
            _exception = new SigningKeysUnavailableException(
                SigningKeysUnavailableReason.SnapshotExpired,
                new SigningKeyRefreshOutcome.Failed(SigningKeyFailureKind.Processing, new FormatException())
            );

        [Test]
        public void It_describes_both() =>
            _exception
                .Message.Should()
                .Be(
                    "The signing keys are older than the maximum staleness, and no key record could be used."
                );
    }

    [TestFixture]
    public class Given_a_refused_attempt
    {
        private SigningKeysUnavailableException _exception = null!;

        [SetUp]
        public void Act() =>
            _exception = new SigningKeysUnavailableException(
                SigningKeysUnavailableReason.NoSnapshot,
                new SigningKeyRefreshOutcome.Refused(DateTimeOffset.UnixEpoch)
            );

        [Test]
        public void It_has_no_inner_exception() => _exception.InnerException.Should().BeNull();

        [Test]
        public void It_describes_the_retry_deadline() =>
            _exception
                .Message.Should()
                .Be(
                    "No signing keys have been loaded, and a new load is not allowed before the retry deadline."
                );

        [Test]
        public void It_keeps_the_outcome() =>
            _exception.Outcome.Should().BeOfType<SigningKeyRefreshOutcome.Refused>();
    }

    [TestFixture]
    public class Given_a_successful_outcome
    {
        [Test]
        public void It_is_refused()
        {
            SigningKeyRefreshOutcome succeeded = new SigningKeyRefreshOutcome.Succeeded(
                new SigningKeySnapshot([], DateTimeOffset.UnixEpoch, 1, SigningKeySource.Database)
            );

            Action create = () =>
                _ = new SigningKeysUnavailableException(SigningKeysUnavailableReason.NoSnapshot, succeeded);

            create.Should().Throw<ArgumentException>().WithParameterName("outcome");
        }
    }

    [TestFixture]
    public class Given_a_refusal_because_an_earlier_load_is_still_running
    {
        [Test]
        public void It_says_the_previous_load_has_not_finished() =>
            new SigningKeysUnavailableException(
                SigningKeysUnavailableReason.NoSnapshot,
                new SigningKeyRefreshOutcome.Refused(
                    DateTimeOffset.UnixEpoch,
                    SigningKeyRefusalReason.OperationOutstanding
                )
            )
                .Message.Should()
                .Be("No signing keys have been loaded, and the previous load has not finished.");
    }

    [TestFixture]
    public class Given_a_refusal_because_the_state_changed
    {
        [Test]
        public void It_says_the_state_changed_before_admission() =>
            new SigningKeysUnavailableException(
                SigningKeysUnavailableReason.NoSnapshot,
                new SigningKeyRefreshOutcome.Refused(
                    DateTimeOffset.UnixEpoch,
                    SigningKeyRefusalReason.StateChanged
                )
            )
                .Message.Should()
                .Be(
                    "No signing keys have been loaded, and the key state changed before the load was admitted."
                );
    }

    [TestFixture]
    public class Given_a_token_status_store_failure
    {
        [Test]
        public void It_carries_its_own_category() =>
            new AuthenticationDependencyUnavailableException(
                AuthenticationDependencyCategory.TokenStatusStore,
                "The token status could not be read."
            )
                .Category.Should()
                .Be(AuthenticationDependencyCategory.TokenStatusStore);
    }
}
