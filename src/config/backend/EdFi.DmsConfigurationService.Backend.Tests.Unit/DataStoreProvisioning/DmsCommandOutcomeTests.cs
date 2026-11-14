// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning;

public class DmsCommandOutcomeTests
{
    private static readonly DmsServerSessionId _session = new(
        4711,
        new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero)
    );

    private static readonly DmsCommandFailure _failure = new(
        DmsCommandFailureClass.DuplicateName,
        DmsProvisioningDiagnostic.ForOperation("RenameIntoTarget")
    );

    [TestFixture]
    public class Given_the_value_free_outcomes
    {
        private DmsCommandOutcome _succeeded = null!;
        private DmsCommandOutcome _failed = null!;
        private DmsCommandOutcome _unknown = null!;

        [SetUp]
        public void Setup()
        {
            _succeeded = new DmsCommandOutcome.Succeeded();
            _failed = new DmsCommandOutcome.Failed(_failure);
            _unknown = new DmsCommandOutcome.Unknown(_session);
        }

        [Test]
        public void It_treats_success_as_definite() => _succeeded.IsDefinite.Should().BeTrue();

        [Test]
        public void It_treats_a_classified_failure_as_definite() => _failed.IsDefinite.Should().BeTrue();

        [Test]
        public void It_treats_unknown_as_not_definite() => _unknown.IsDefinite.Should().BeFalse();

        [Test]
        public void It_keeps_the_failure_class_and_diagnostic() =>
            _failed.Should().BeOfType<DmsCommandOutcome.Failed>().Which.Failure.Should().Be(_failure);

        [Test]
        public void It_names_the_session_to_prove_terminated() =>
            _unknown.Should().BeOfType<DmsCommandOutcome.Unknown>().Which.Session.Should().Be(_session);
    }

    [TestFixture]
    public class Given_the_valued_outcomes
    {
        private DmsInstanceIdentity _instance = null!;
        private DmsCommandOutcome<DmsInstanceIdentity> _succeeded = null!;
        private DmsCommandOutcome<DmsInstanceIdentity> _failed = null!;
        private DmsCommandOutcome<DmsInstanceIdentity> _unknown = null!;

        [SetUp]
        public void Setup()
        {
            _instance = new DmsInstanceIdentity.Postgresql(16385, "edfi-dms-provision:v1:0123456789ab:x");
            _succeeded = new DmsCommandOutcome<DmsInstanceIdentity>.Succeeded(_instance);
            _failed = new DmsCommandOutcome<DmsInstanceIdentity>.Failed(_failure);
            _unknown = new DmsCommandOutcome<DmsInstanceIdentity>.Unknown(_session);
        }

        [Test]
        public void It_carries_the_value_on_success() =>
            _succeeded
                .Should()
                .BeOfType<DmsCommandOutcome<DmsInstanceIdentity>.Succeeded>()
                .Which.Value.Should()
                .BeSameAs(_instance);

        [Test]
        public void It_is_definite_only_when_not_unknown()
        {
            _succeeded.IsDefinite.Should().BeTrue();
            _failed.IsDefinite.Should().BeTrue();
            _unknown.IsDefinite.Should().BeFalse();
        }

        [Test]
        public void It_drops_the_value_from_success() =>
            _succeeded.WithoutValue().Should().BeOfType<DmsCommandOutcome.Succeeded>();

        [Test]
        public void It_keeps_the_failure_when_dropping_the_value() =>
            _failed.WithoutValue().Should().Be(new DmsCommandOutcome.Failed(_failure));

        [Test]
        public void It_keeps_the_session_when_dropping_the_value() =>
            _unknown.WithoutValue().Should().Be(new DmsCommandOutcome.Unknown(_session));
    }

    [TestFixture]
    public class Given_outcomes_that_carry_residue_or_proof_of_quiescence
    {
        private static readonly IReadOnlyList<DmsCleanupResidue> _residue =
        [
            new DmsCleanupResidue(DmsResidueKind.ChildProcess, "dms-provision:0123456789ab/42"),
        ];

        private DmsCommandOutcome _unknownWithoutValue = null!;
        private DmsCommandOutcome _failedWithoutValue = null!;
        private DmsCommandOutcome<int>.Unknown _default = null!;

        [SetUp]
        public void Setup()
        {
            _unknownWithoutValue = new DmsCommandOutcome<int>.Unknown(null)
            {
                Quiescent = true,
                Residue = _residue,
            }.WithoutValue();
            _failedWithoutValue = new DmsCommandOutcome<int>.Failed(
                new DmsCommandFailure(
                    DmsCommandFailureClass.Rejected,
                    DmsProvisioningDiagnostic.ForOperation("CreateStaging")
                )
            )
            {
                Residue = _residue,
            }.WithoutValue();
            _default = new DmsCommandOutcome<int>.Unknown(null);
        }

        [Test]
        public void It_keeps_quiescence_and_residue_when_dropping_the_value() =>
            _unknownWithoutValue
                .Should()
                .BeOfType<DmsCommandOutcome.Unknown>()
                .Which.Should()
                .BeEquivalentTo(new { Quiescent = true, Residue = _residue });

        [Test]
        public void It_keeps_the_residue_of_a_failure_when_dropping_the_value() =>
            _failedWithoutValue.Residue.Should().Equal(_residue);

        [Test]
        public void It_proves_nothing_by_default() =>
            (_default.Quiescent, _default.Residue.Count).Should().Be((false, 0));
    }
}
