// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Mssql.DataStoreProvisioning;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Mssql;

/// <summary>
/// Step 4.9 reason codes: the server rules of 7.3 item 4 over read facts (the version floor and the default
/// directories as <c>EngineIncompatible</c>; <c>sysadmin</c>, <c>ALTER ANY DATABASE</c>,
/// <c>ALTER ANY CONNECTION</c>, server-state visibility, and file-catalog visibility each as
/// <c>AdminPrivilegeMissing</c>, every failing rule reported); the staging directory rules of item 6 (missing, or
/// group or other write); the D-21 verdict mapping; and the probe lock resource.
/// </summary>
public class MssqlEnvironmentRulesTests
{
    private static DmsReadinessReason Reason(
        DmsProvisioningFailureCode code,
        string probe,
        string? operation = null
    ) => new(code, probe, DmsProvisioningDiagnostic.ForOperation(operation ?? probe));

    private static readonly DmsReadinessReason _createDatabase = Reason(
        DmsProvisioningFailureCode.AdminPrivilegeMissing,
        MssqlEnvironment.CreateDatabasePrivilegeProbe,
        MssqlEnvironment.CreateDatabaseRoleOperation
    );

    private static readonly DmsReadinessReason _alterAnyDatabase = Reason(
        DmsProvisioningFailureCode.AdminPrivilegeMissing,
        MssqlEnvironment.AlterAnyDatabasePrivilegeProbe
    );

    private static readonly DmsReadinessReason _alterAnyConnection = Reason(
        DmsProvisioningFailureCode.AdminPrivilegeMissing,
        MssqlEnvironment.AlterAnyConnectionPrivilegeProbe
    );

    private static readonly DmsReadinessReason _viewServerState = Reason(
        DmsProvisioningFailureCode.AdminPrivilegeMissing,
        MssqlEnvironment.ViewServerStatePrivilegeProbe
    );

    private static readonly DmsReadinessReason _fileCatalog = Reason(
        DmsProvisioningFailureCode.AdminPrivilegeMissing,
        MssqlEnvironment.FileCatalogVisibilityProbe
    );

    [TestFixture]
    public class Given_server_facts
    {
        private static MssqlEnvironmentFacts Facts(
            int productMajorVersion = 17,
            string? defaultDataPath = "/var/opt/mssql/data/",
            string? defaultLogPath = "/var/opt/mssql/log/",
            bool sysadmin = true,
            bool alterAnyDatabase = true,
            bool alterAnyConnection = true,
            bool seesAllSessions = true,
            bool seesFileCatalog = true
        ) =>
            new(
                productMajorVersion,
                defaultDataPath,
                defaultLogPath,
                sysadmin,
                alterAnyDatabase,
                alterAnyConnection,
                seesAllSessions,
                seesFileCatalog
            );

        [Test]
        public void It_raises_nothing_for_sysadmin() => MssqlEnvironment.Evaluate(Facts()).Should().BeEmpty();

        /// <summary>
        /// The previously documented privileges (<c>dbcreator</c>, <c>ALTER ANY DATABASE</c>, <c>ALTER ANY
        /// CONNECTION</c>, server-state and file-catalog visibility) without <c>sysadmin</c>: only that is missing.
        /// </summary>
        [Test]
        public void It_requires_sysadmin_even_with_every_other_privilege() =>
            MssqlEnvironment.Evaluate(Facts(sysadmin: false)).Should().Equal(_createDatabase);

        [Test]
        public void It_requires_alter_any_database() =>
            MssqlEnvironment.Evaluate(Facts(alterAnyDatabase: false)).Should().Equal(_alterAnyDatabase);

        [Test]
        public void It_requires_alter_any_connection() =>
            MssqlEnvironment.Evaluate(Facts(alterAnyConnection: false)).Should().Equal(_alterAnyConnection);

        [Test]
        public void It_requires_server_state_visibility() =>
            MssqlEnvironment.Evaluate(Facts(seesAllSessions: false)).Should().Equal(_viewServerState);

        [Test]
        public void It_requires_file_catalog_visibility() =>
            MssqlEnvironment.Evaluate(Facts(seesFileCatalog: false)).Should().Equal(_fileCatalog);

        [Test]
        public void It_reports_every_missing_privilege_at_once_in_order() =>
            MssqlEnvironment
                .Evaluate(
                    Facts(
                        sysadmin: false,
                        alterAnyDatabase: false,
                        alterAnyConnection: false,
                        seesAllSessions: false,
                        seesFileCatalog: false
                    )
                )
                .Should()
                .Equal(
                    _createDatabase,
                    _alterAnyDatabase,
                    _alterAnyConnection,
                    _viewServerState,
                    _fileCatalog
                );

        [Test]
        public void It_accepts_sql_server_2022() =>
            MssqlEnvironment.Evaluate(Facts(productMajorVersion: 16)).Should().BeEmpty();

        [Test]
        public void It_refuses_a_server_below_sql_server_2022_as_engine_incompatible() =>
            MssqlEnvironment
                .Evaluate(Facts(productMajorVersion: 15))
                .Should()
                .Equal(
                    Reason(DmsProvisioningFailureCode.EngineIncompatible, MssqlEnvironment.ServerVersionProbe)
                );

        [Test]
        public void It_applies_a_given_floor() =>
            MssqlEnvironment
                .Evaluate(Facts(productMajorVersion: 17), minimumProductMajorVersion: 18)
                .Should()
                .Equal(
                    Reason(DmsProvisioningFailureCode.EngineIncompatible, MssqlEnvironment.ServerVersionProbe)
                );

        [TestCase(null)]
        [TestCase("")]
        public void It_refuses_a_missing_default_data_directory(string? path) =>
            MssqlEnvironment
                .Evaluate(Facts(defaultDataPath: path))
                .Should()
                .Equal(
                    Reason(
                        DmsProvisioningFailureCode.EngineIncompatible,
                        MssqlEnvironment.DefaultDataPathProbe
                    )
                );

        [TestCase(null)]
        [TestCase("")]
        public void It_refuses_a_missing_default_log_directory(string? path) =>
            MssqlEnvironment
                .Evaluate(Facts(defaultLogPath: path))
                .Should()
                .Equal(
                    Reason(
                        DmsProvisioningFailureCode.EngineIncompatible,
                        MssqlEnvironment.DefaultLogPathProbe
                    )
                );

        [Test]
        public void It_reports_engine_and_privilege_reasons_together() =>
            MssqlEnvironment
                .Evaluate(Facts(productMajorVersion: 15, defaultLogPath: null, alterAnyConnection: false))
                .Select(reason => reason.Probe)
                .Should()
                .Equal(
                    MssqlEnvironment.ServerVersionProbe,
                    MssqlEnvironment.DefaultLogPathProbe,
                    MssqlEnvironment.AlterAnyConnectionPrivilegeProbe
                );
    }

    [TestFixture]
    public class Given_a_staging_directory
    {
        private const UnixFileMode Setgid2750 =
            UnixFileMode.SetGroup
            | UnixFileMode.UserRead
            | UnixFileMode.UserWrite
            | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead
            | UnixFileMode.GroupExecute;

        private static readonly DmsReadinessReason _writable = Reason(
            DmsProvisioningFailureCode.StartupValidationFailed,
            MssqlEnvironment.StagingDirectoryProbe,
            MssqlEnvironment.StagingDirectoryWritableOperation
        );

        [Test]
        public void It_refuses_a_missing_directory() =>
            MssqlEnvironment
                .EvaluateStagingDirectory(exists: false, mode: null, ownedByProcess: null)
                .Should()
                .Be(
                    Reason(
                        DmsProvisioningFailureCode.StartupValidationFailed,
                        MssqlEnvironment.StagingDirectoryProbe,
                        MssqlEnvironment.StagingDirectoryMissingOperation
                    )
                );

        [Test]
        public void It_accepts_the_documented_2750_owned_by_the_process() =>
            MssqlEnvironment
                .EvaluateStagingDirectory(exists: true, Setgid2750, ownedByProcess: true)
                .Should()
                .BeNull();

        [Test]
        public void It_refuses_the_documented_2750_owned_by_another_user() =>
            MssqlEnvironment
                .EvaluateStagingDirectory(exists: true, Setgid2750, ownedByProcess: false)
                .Should()
                .Be(
                    Reason(
                        DmsProvisioningFailureCode.StartupValidationFailed,
                        MssqlEnvironment.StagingDirectoryProbe,
                        MssqlEnvironment.StagingDirectoryNotOwnedOperation
                    )
                );

        [Test]
        public void It_accepts_a_directory_whose_mode_and_owner_are_not_read_on_windows() =>
            MssqlEnvironment
                .EvaluateStagingDirectory(exists: true, mode: null, ownedByProcess: null)
                .Should()
                .BeNull();

        [Test]
        public void It_refuses_group_write() =>
            MssqlEnvironment
                .EvaluateStagingDirectory(
                    exists: true,
                    Setgid2750 | UnixFileMode.GroupWrite,
                    ownedByProcess: true
                )
                .Should()
                .Be(_writable);

        [Test]
        public void It_refuses_other_write() =>
            MssqlEnvironment
                .EvaluateStagingDirectory(
                    exists: true,
                    Setgid2750 | UnixFileMode.OtherWrite,
                    ownedByProcess: true
                )
                .Should()
                .Be(_writable);
    }

    [TestFixture]
    public class Given_a_staging_probe_result
    {
        private static readonly DmsProvisioningDiagnostic _serverAnswer = new(
            "SqlException",
            "3201",
            "StagingProbe.Header"
        );

        private static IReadOnlyList<DmsReadinessReason> Evaluate(
            MssqlStagingVisibility visibility,
            DmsProvisioningDiagnostic? diagnostic = null,
            bool deleted = true
        ) =>
            MssqlEnvironment.EvaluateStagingProbe(
                new MssqlStagingProbeResult(visibility, diagnostic, deleted)
            );

        [Test]
        public void It_accepts_a_visible_probe_it_deleted() =>
            Evaluate(MssqlStagingVisibility.Visible).Should().BeEmpty();

        [Test]
        public void It_reports_not_visible_as_staging_not_visible_with_the_servers_diagnostic() =>
            Evaluate(MssqlStagingVisibility.NotVisible, _serverAnswer)
                .Should()
                .Equal(
                    new DmsReadinessReason(
                        DmsProvisioningFailureCode.StagingNotVisible,
                        MssqlEnvironment.StagingVisibilityProbe,
                        _serverAnswer
                    )
                );

        [Test]
        public void It_reports_an_unwritable_probe_as_staging_failed() =>
            Evaluate(
                    MssqlStagingVisibility.NotWritable,
                    DmsProvisioningDiagnostic.ForOperation("StageBackup.Create")
                )
                .Should()
                .Equal(
                    new DmsReadinessReason(
                        DmsProvisioningFailureCode.StagingFailed,
                        MssqlEnvironment.StagingVisibilityProbe,
                        DmsProvisioningDiagnostic.ForOperation("StageBackup.Create")
                    )
                );

        [Test]
        public void It_reports_an_inconclusive_answer_as_not_ready() =>
            Evaluate(
                    MssqlStagingVisibility.Inconclusive,
                    DmsProvisioningDiagnostic.ForOperation("StagingProbe.Header")
                )
                .Should()
                .Equal(
                    new DmsReadinessReason(
                        DmsProvisioningFailureCode.StartupValidationFailed,
                        MssqlEnvironment.StagingVisibilityProbe,
                        DmsProvisioningDiagnostic.ForOperation("StagingProbe.Header")
                    )
                );

        [Test]
        public void It_reports_a_probe_left_behind_even_when_visible() =>
            Evaluate(MssqlStagingVisibility.Visible, deleted: false)
                .Should()
                .Equal(
                    Reason(
                        DmsProvisioningFailureCode.StagingFailed,
                        MssqlEnvironment.StagingVisibilityProbe,
                        MssqlEnvironment.StagingProbeDeleteOperation
                    )
                );

        [Test]
        public void It_reports_both_a_verdict_and_a_probe_left_behind() =>
            Evaluate(
                    MssqlStagingVisibility.NotWritable,
                    DmsProvisioningDiagnostic.ForOperation("StageBackup.Copy"),
                    deleted: false
                )
                .Select(reason => (reason.Code, reason.Diagnostic!.Operation))
                .Should()
                .Equal(
                    (DmsProvisioningFailureCode.StagingFailed, "StageBackup.Copy"),
                    (DmsProvisioningFailureCode.StagingFailed, MssqlEnvironment.StagingProbeDeleteOperation)
                );
    }

    [TestFixture]
    public class Given_the_readiness_lock_probe
    {
        [Test]
        public void It_uses_the_documented_probe_resource() =>
            MssqlTargetLockSession.ProbeResource.Should().Be("dms-provision-target:v1:__probe__");

        [Test]
        public void It_cannot_be_reached_by_a_target_name() =>
            DmsTargetDatabaseName
                .TryCreate("__probe__", ProtectedDatabaseNames.Create("edfi_configurationservice", []), out _)
                .Should()
                .NotBe(DmsTargetNameValidation.Valid);
    }
}
