// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning;

/// <summary>Guards the small invariants the engine-port records enforce in their constructors.</summary>
public class DmsEngineContractGuardTests
{
    [TestFixture]
    public class Given_identity_reads
    {
        private DmsIdentityRead _found = null!;
        private Action _foundEmpty = () => { };

        [SetUp]
        public void Setup()
        {
            _found = new DmsIdentityRead.Found(Guid.Parse("8a6f1c2e-3b4d-4e5f-9a1b-2c3d4e5f6a7b"));
            _foundEmpty = () => _ = new DmsIdentityRead.Found(Guid.Empty);
        }

        [Test]
        public void It_keeps_a_valid_identity() =>
            _found
                .Should()
                .BeOfType<DmsIdentityRead.Found>()
                .Which.SourceIdentity.Should()
                .Be(Guid.Parse("8a6f1c2e-3b4d-4e5f-9a1b-2c3d4e5f6a7b"));

        [Test]
        public void It_refuses_an_all_zero_identity_as_found() =>
            _foundEmpty.Should().Throw<ArgumentException>();
    }

    [TestFixture]
    public class Given_not_ready_verdicts
    {
        private Action _readinessWithoutReasons = () => { };
        private Action _environmentWithoutReasons = () => { };
        private DmsProvisionerReadiness.NotReady _readiness = null!;

        [SetUp]
        public void Setup()
        {
            _readinessWithoutReasons = () => _ = new DmsProvisionerReadiness.NotReady([]);
            _environmentWithoutReasons = () => _ = new DmsEngineEnvironment.NotReady([]);
            _readiness = new DmsProvisionerReadiness.NotReady([
                new DmsReadinessReason(DmsProvisioningFailureCode.StagingNotVisible, "StagingVisibility"),
            ]);
        }

        [Test]
        public void It_requires_at_least_one_reason_for_readiness() =>
            _readinessWithoutReasons.Should().Throw<ArgumentException>();

        [Test]
        public void It_requires_at_least_one_reason_for_the_engine_environment() =>
            _environmentWithoutReasons.Should().Throw<ArgumentException>();

        [Test]
        public void It_keeps_the_reasons() =>
            _readiness
                .Reasons.Should()
                .ContainSingle()
                .Which.Code.Should()
                .Be(DmsProvisioningFailureCode.StagingNotVisible);
    }

    [TestFixture]
    public class Given_instance_identities
    {
        private DmsInstanceIdentity.Mssql _beforeRename = null!;
        private DmsInstanceIdentity.Mssql _afterRename = null!;
        private DmsInstanceIdentity _postgresql = null!;

        [SetUp]
        public void Setup()
        {
            _beforeRename = new DmsInstanceIdentity.Mssql(
                7,
                "__0123456789ab",
                new DateTimeOffset(2026, 11, 5, 1, 0, 0, TimeSpan.Zero)
            );
            _afterRename = _beforeRename.WithCreateDateSnapshot(
                new DateTimeOffset(2026, 11, 5, 1, 5, 0, TimeSpan.Zero)
            );
            _postgresql = new DmsInstanceIdentity.Postgresql(16385, "edfi-dms-provision:v1:0123456789ab:x");
        }

        [Test]
        public void It_recognizes_the_same_sql_server_database_across_a_rename_by_stable_evidence() =>
            _afterRename.IsSameStableInstance(_beforeRename).Should().BeTrue();

        [Test]
        public void It_distinguishes_the_snapshots_by_full_equality() =>
            _afterRename.Should().NotBe(_beforeRename);

        [Test]
        public void It_detects_a_reused_database_id_by_the_tag() =>
            _afterRename
                .IsSameStableInstance(
                    new DmsInstanceIdentity.Mssql(7, "__ffffffffffff", _afterRename.CreateDateSnapshot)
                )
                .Should()
                .BeFalse();

        [Test]
        public void It_never_matches_across_engines() =>
            _postgresql.IsSameStableInstance(_beforeRename).Should().BeFalse();

        [Test]
        public void It_requires_the_claim_to_match_on_postgresql() =>
            _postgresql
                .IsSameStableInstance(new DmsInstanceIdentity.Postgresql(16385, "other"))
                .Should()
                .BeFalse();
    }

    [TestFixture]
    public class Given_a_cleanup_report
    {
        [Test]
        public void It_reports_no_residue_when_clean() =>
            DmsCleanupReport.Clean.HasResidue.Should().BeFalse();

        [Test]
        public void It_reports_residue_when_present() =>
            new DmsCleanupReport([new DmsCleanupResidue(DmsResidueKind.CredentialFile, "pgpass")])
                .HasResidue.Should()
                .BeTrue();
    }
}
