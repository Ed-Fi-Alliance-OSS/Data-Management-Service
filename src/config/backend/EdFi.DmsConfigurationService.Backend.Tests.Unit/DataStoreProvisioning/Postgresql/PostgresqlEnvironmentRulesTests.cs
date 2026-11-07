// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Postgresql;

/// <summary>
/// Step 3.9 reason codes: the server rules of 7.3 item 4 over read facts (floors as
/// <c>EngineIncompatible</c>; a non-superuser administrator as <c>AdminPrivilegeMissing</c> under
/// <c>Superuser</c>, because the artifact's ownership statements need one; <c>CREATEDB</c>, <c>pg_signal_backend</c> and <c>pg_read_all_stats</c> as
/// <c>AdminPrivilegeMissing</c>, each excused by superuser; the enqueue owner missing without
/// <c>CREATEROLE</c> as <c>AdminPrivilegeMissing</c> and present but unsafe as <c>RoleStateUnsafe</c>, which
/// superuser does not excuse), every failing rule reported; and the <c>psql</c> rule of item 5.
/// </summary>
public class PostgresqlEnvironmentRulesTests
{
    private static PostgresqlAdminPrivileges Privileges(
        bool superuser = false,
        bool createDatabase = true,
        bool createRole = false,
        bool signalBackend = true,
        bool readAllStats = true,
        PostgresqlEnqueueOwnerState owner = PostgresqlEnqueueOwnerState.LockedDown
    ) => new(superuser, createDatabase, createRole, signalBackend, readAllStats, owner);

    private static PostgresqlEnvironmentFacts Facts(
        PostgresqlAdminPrivileges? privileges,
        int serverVersionNum = 180006,
        string? template1Encoding = "UTF8"
    ) => new(serverVersionNum, template1Encoding, privileges);

    private static DmsReadinessReason Reason(
        DmsProvisioningFailureCode code,
        string probe,
        string? operation = null
    ) => new(code, probe, DmsProvisioningDiagnostic.ForOperation(operation ?? probe));

    /// <summary>The reasons other than the superuser requirement, which a dedicated fixture pins.</summary>
    private static IReadOnlyList<DmsReadinessReason> Operational(PostgresqlEnvironmentFacts facts) =>
        [
            .. PostgresqlEnvironment
                .Evaluate(facts)
                .Where(reason => reason.Probe != PostgresqlEnvironment.SuperuserProbe),
        ];

    [TestFixture]
    public class Given_server_facts
    {
        private IReadOnlyList<DmsReadinessReason> _granted = null!;
        private IReadOnlyList<DmsReadinessReason> _superuserWithNothingElse = null!;
        private IReadOnlyList<DmsReadinessReason> _noCreateDatabase = null!;
        private IReadOnlyList<DmsReadinessReason> _noSignalBackend = null!;
        private IReadOnlyList<DmsReadinessReason> _noReadAllStats = null!;
        private IReadOnlyList<DmsReadinessReason> _nothingGranted = null!;
        private IReadOnlyList<DmsReadinessReason> _belowFloor = null!;
        private IReadOnlyList<DmsReadinessReason> _latin1 = null!;
        private IReadOnlyList<DmsReadinessReason> _noTemplate1 = null!;

        [SetUp]
        public void Setup()
        {
            _granted = Operational(Facts(Privileges()));
            _superuserWithNothingElse = Operational(
                Facts(
                    Privileges(
                        superuser: true,
                        createDatabase: false,
                        signalBackend: false,
                        readAllStats: false,
                        owner: PostgresqlEnqueueOwnerState.Absent
                    )
                )
            );
            _noCreateDatabase = Operational(Facts(Privileges(createDatabase: false)));
            _noSignalBackend = Operational(Facts(Privileges(signalBackend: false)));
            _noReadAllStats = Operational(Facts(Privileges(readAllStats: false)));
            _nothingGranted = Operational(
                Facts(Privileges(createDatabase: false, signalBackend: false, readAllStats: false))
            );
            _belowFloor = Operational(Facts(null, serverVersionNum: 179999));
            _latin1 = Operational(Facts(Privileges(), template1Encoding: "LATIN1"));
            _noTemplate1 = Operational(Facts(Privileges(), template1Encoding: null));
        }

        [Test]
        public void It_raises_nothing_operational_for_a_granted_admin() => _granted.Should().BeEmpty();

        [Test]
        public void It_lets_superuser_stand_in_for_every_privilege_and_for_create_role() =>
            _superuserWithNothingElse.Should().BeEmpty();

        [Test]
        public void It_requires_createdb() =>
            _noCreateDatabase
                .Should()
                .Equal(
                    Reason(
                        DmsProvisioningFailureCode.AdminPrivilegeMissing,
                        PostgresqlEnvironment.CreateDatabasePrivilegeProbe
                    )
                );

        [Test]
        public void It_requires_pg_signal_backend() =>
            _noSignalBackend
                .Should()
                .Equal(
                    Reason(
                        DmsProvisioningFailureCode.AdminPrivilegeMissing,
                        PostgresqlEnvironment.SignalBackendPrivilegeProbe
                    )
                );

        [Test]
        public void It_requires_pg_read_all_stats() =>
            _noReadAllStats
                .Should()
                .Equal(
                    Reason(
                        DmsProvisioningFailureCode.AdminPrivilegeMissing,
                        PostgresqlEnvironment.ReadAllStatsPrivilegeProbe
                    )
                );

        [Test]
        public void It_reports_every_missing_privilege_at_once() =>
            _nothingGranted
                .Select(reason => reason.Probe)
                .Should()
                .Equal(
                    PostgresqlEnvironment.CreateDatabasePrivilegeProbe,
                    PostgresqlEnvironment.SignalBackendPrivilegeProbe,
                    PostgresqlEnvironment.ReadAllStatsPrivilegeProbe
                );

        [Test]
        public void It_refuses_a_server_below_180000_and_judges_nothing_it_did_not_read() =>
            _belowFloor
                .Should()
                .Equal(
                    Reason(
                        DmsProvisioningFailureCode.EngineIncompatible,
                        PostgresqlEnvironment.ServerVersionProbe
                    )
                );

        [Test]
        public void It_refuses_a_template1_that_is_not_utf8_or_missing() =>
            (_latin1.Single(), _noTemplate1.Single())
                .Should()
                .Be(
                    (
                        Reason(
                            DmsProvisioningFailureCode.EngineIncompatible,
                            PostgresqlEnvironment.Template1EncodingProbe
                        ),
                        Reason(
                            DmsProvisioningFailureCode.EngineIncompatible,
                            PostgresqlEnvironment.Template1EncodingProbe
                        )
                    )
                );
    }

    [TestFixture]
    public class Given_the_artifact_ownership_requirement
    {
        private IReadOnlyList<DmsReadinessReason> _nonSuperuserWithEverything = null!;
        private IReadOnlyList<DmsReadinessReason> _superuser = null!;
        private IReadOnlyList<DmsReadinessReason> _belowFloor = null!;

        [SetUp]
        public void Setup()
        {
            _nonSuperuserWithEverything = PostgresqlEnvironment.Evaluate(Facts(Privileges(createRole: true)));
            _superuser = PostgresqlEnvironment.Evaluate(Facts(Privileges(superuser: true)));
            _belowFloor = PostgresqlEnvironment.Evaluate(Facts(null, serverVersionNum: 170000));
        }

        [Test]
        public void It_refuses_a_non_superuser_holding_every_other_privilege() =>
            _nonSuperuserWithEverything
                .Should()
                .Equal(
                    Reason(
                        DmsProvisioningFailureCode.AdminPrivilegeMissing,
                        PostgresqlEnvironment.SuperuserProbe,
                        PostgresqlEnvironment.ArtifactOwnershipOperation
                    )
                );

        [Test]
        public void It_accepts_a_superuser_on_a_supported_server() => _superuser.Should().BeEmpty();

        [Test]
        public void It_judges_no_role_it_did_not_read() =>
            _belowFloor
                .Select(reason => reason.Probe)
                .Should()
                .Equal(PostgresqlEnvironment.ServerVersionProbe);
    }

    [TestFixture]
    public class Given_the_enqueue_owner_role
    {
        private IReadOnlyList<DmsReadinessReason> _absentCreatable = null!;
        private IReadOnlyList<DmsReadinessReason> _absentNotCreatable = null!;
        private IReadOnlyList<DmsReadinessReason> _unsafeAttributes = null!;
        private IReadOnlyList<DmsReadinessReason> _unsafeMemberships = null!;
        private IReadOnlyList<DmsReadinessReason> _unsafeUnderSuperuser = null!;

        [SetUp]
        public void Setup()
        {
            _absentCreatable = Operational(
                Facts(Privileges(createRole: true, owner: PostgresqlEnqueueOwnerState.Absent))
            );
            _absentNotCreatable = Operational(
                Facts(Privileges(createRole: false, owner: PostgresqlEnqueueOwnerState.Absent))
            );
            _unsafeAttributes = Operational(
                Facts(Privileges(createRole: true, owner: PostgresqlEnqueueOwnerState.UnsafeAttributes))
            );
            _unsafeMemberships = Operational(
                Facts(Privileges(owner: PostgresqlEnqueueOwnerState.UnsafeMemberships))
            );
            _unsafeUnderSuperuser = Operational(
                Facts(Privileges(superuser: true, owner: PostgresqlEnqueueOwnerState.UnsafeAttributes))
            );
        }

        [Test]
        public void It_accepts_a_missing_role_the_admin_may_create() => _absentCreatable.Should().BeEmpty();

        [Test]
        public void It_requires_createrole_for_a_missing_role() =>
            _absentNotCreatable
                .Should()
                .Equal(
                    Reason(
                        DmsProvisioningFailureCode.AdminPrivilegeMissing,
                        PostgresqlEnvironment.EnqueueOwnerProbe,
                        PostgresqlEnvironment.EnqueueOwnerMissingOperation
                    )
                );

        [Test]
        public void It_refuses_a_role_with_login_inheritance_or_an_elevated_attribute() =>
            _unsafeAttributes
                .Should()
                .Equal(
                    Reason(
                        DmsProvisioningFailureCode.RoleStateUnsafe,
                        PostgresqlEnvironment.EnqueueOwnerProbe,
                        PostgresqlEnvironment.EnqueueOwnerAttributesOperation
                    )
                );

        [Test]
        public void It_refuses_a_role_with_a_privilege_bearing_membership() =>
            _unsafeMemberships
                .Should()
                .Equal(
                    Reason(
                        DmsProvisioningFailureCode.RoleStateUnsafe,
                        PostgresqlEnvironment.EnqueueOwnerProbe,
                        PostgresqlEnvironment.EnqueueOwnerMembershipsOperation
                    )
                );

        [Test]
        public void It_does_not_let_superuser_excuse_an_unsafe_role() =>
            _unsafeUnderSuperuser
                .Should()
                .ContainSingle()
                .Which.Code.Should()
                .Be(DmsProvisioningFailureCode.RoleStateUnsafe);
    }

    [TestFixture]
    public class Given_psql_probe_results
    {
        [Test]
        public void It_accepts_the_floor_and_above() =>
            new[]
            {
                PostgresqlEnvironment.EvaluatePsql(new PsqlVersionProbeResult.Parsed(18, 0), 18),
                PostgresqlEnvironment.EvaluatePsql(new PsqlVersionProbeResult.Parsed(19, 2), 18),
            }
                .Should()
                .AllSatisfy(reason => reason.Should().BeNull());

        [Test]
        public void It_refuses_a_major_below_the_floor_as_engine_incompatible() =>
            PostgresqlEnvironment
                .EvaluatePsql(new PsqlVersionProbeResult.Parsed(17, 9), 18)
                .Should()
                .Be(
                    Reason(
                        DmsProvisioningFailureCode.EngineIncompatible,
                        PostgresqlEnvironment.PsqlProbe,
                        PostgresqlEnvironment.PsqlBelowFloorOperation
                    )
                );

        [Test]
        public void It_refuses_an_executable_that_does_not_announce_psql() =>
            PostgresqlEnvironment
                .EvaluatePsql(new PsqlVersionProbeResult.Unparsable(), 18)
                .Should()
                .Be(
                    Reason(
                        DmsProvisioningFailureCode.StartupValidationFailed,
                        PostgresqlEnvironment.PsqlProbe,
                        PostgresqlEnvironment.PsqlUnparsableOperation
                    )
                );

        [Test]
        public void It_refuses_a_probe_that_failed_naming_the_reason_and_type_chain() =>
            PostgresqlEnvironment
                .EvaluatePsql(
                    new PsqlVersionProbeResult.Failed(
                        PsqlProbeFailure.StartFailed,
                        "System.ComponentModel.Win32Exception"
                    ),
                    18
                )
                .Should()
                .Be(
                    new DmsReadinessReason(
                        DmsProvisioningFailureCode.StartupValidationFailed,
                        PostgresqlEnvironment.PsqlProbe,
                        new DmsProvisioningDiagnostic(
                            "System.ComponentModel.Win32Exception",
                            null,
                            "ProbePsqlVersion.StartFailed"
                        )
                    )
                );
    }
}
