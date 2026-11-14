// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Packaging;
using EdFi.DmsConfigurationService.Backend.Mssql.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Packaging;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Mssql;

/// <summary>
/// The SQL Server staging stage without a server (Step 4.5): the reservation recognizes a database only by the exact
/// primary file name the planner gives the operation's staging database; the guarded drop checks the name, the
/// <c>database_id</c> and the primary file before its one <c>DROP DATABASE</c> and returns an error as a number; and
/// the engine refuses a foreign holder, a staging name that is not the operation's, and a foreign identity before it
/// connects.
/// </summary>
public class MssqlStagingStageTests
{
    private static DmsOperationId Operation(string value) =>
        DmsOperationId.TryParse(value, out DmsOperationId operation)
            ? operation
            : throw new InvalidOperationException("The fixed operation id did not parse.");

    [TestFixture]
    public class Given_a_reservation
    {
        private static readonly DmsOperationId _operation = Operation("45a0000000bb");

        private static readonly DmsInstanceIdentity.MssqlReservation _reservation = new(
            MssqlRestorePlanner.PrimaryFileNameFor(_operation)
        );

        [Test]
        public void It_names_the_staging_database_and_the_operation_tag() =>
            _reservation
                .PrimaryFileName.Should()
                .Be("edfi_dms_provision_stage_45a0000000bb__45a0000000bb.mdf");

        [Test]
        public void It_is_the_name_the_planner_gives_the_primary_file()
        {
            MssqlRestorePlanResult result = MssqlRestorePlanner.Plan(
                new MssqlBackupDescription(
                    [new MssqlBackupHeader(1, 1, "EdFi_Template")],
                    [new MssqlBackupFile("data", "D", 1), new MssqlBackupFile("log", "L", 2)]
                ),
                _operation,
                DmsDatabaseName.TryCreate("EdFi_Target", out DmsDatabaseName target)
                    ? target
                    : throw new InvalidOperationException("The target name did not parse."),
                "/var/opt/mssql/data",
                "/var/opt/mssql/log"
            );
            string primary = ((MssqlRestorePlanResult.Planned)result).Plan.PrimaryDataFilePath;
            _reservation.IsPrimaryFile(primary).Should().BeTrue();
        }

        [TestCase("/var/opt/mssql/data/edfi_dms_provision_stage_45a0000000bb__45a0000000bb.mdf", true)]
        [TestCase(@"D:\SQLData\edfi_dms_provision_stage_45a0000000bb__45a0000000bb.mdf", true)]
        [TestCase(@"\\fileserver\share\edfi_dms_provision_stage_45a0000000bb__45a0000000bb.mdf", true)]
        [TestCase("edfi_dms_provision_stage_45a0000000bb__45a0000000bb.mdf", false)]
        [TestCase("/data/EDFI_DMS_PROVISION_STAGE_45A0000000BB__45A0000000BB.mdf", false)]
        [TestCase("/data/edfi_dms_provision_stage_45a0000000bb__45a0000000bb.mdf.old", false)]
        [TestCase("/data/xedfi_dms_provision_stage_45a0000000bb__45a0000000bb.mdf", false)]
        [TestCase("/data/edfi_dms_provision_stage_45a0000000bb__45a0000000cc.mdf", false)]
        [TestCase("", false)]
        public void It_recognizes_only_the_exact_file_name_after_a_separator(
            string physicalName,
            bool expected
        ) => _reservation.IsPrimaryFile(physicalName).Should().Be(expected);

        [Test]
        public void It_is_realized_only_by_a_sql_server_instance_carrying_the_file()
        {
            (
                _reservation.IsRealizedBy(
                    new DmsInstanceIdentity.Mssql(
                        9,
                        "/d/" + _reservation.PrimaryFileName,
                        DateTimeOffset.UnixEpoch
                    )
                ),
                _reservation.IsRealizedBy(
                    new DmsInstanceIdentity.Mssql(9, "/d/other.mdf", DateTimeOffset.UnixEpoch)
                ),
                _reservation.IsRealizedBy(
                    new DmsInstanceIdentity.Postgresql(9, _reservation.PrimaryFileName)
                ),
                _reservation.IsRealizedBy(_reservation)
            )
                .Should()
                .Be((true, false, false, false));
        }

        [Test]
        public void It_is_never_the_same_stable_instance_as_anything() =>
            (
                _reservation.IsSameStableInstance(_reservation),
                new DmsInstanceIdentity.Mssql(
                    9,
                    "/d/" + _reservation.PrimaryFileName,
                    DateTimeOffset.UnixEpoch
                ).IsSameStableInstance(_reservation)
            )
                .Should()
                .Be((false, false));
    }

    [TestFixture]
    public class Given_the_guarded_drop
    {
        private static readonly string _batch = MssqlStagingQueries.GuardedDrop(
            Operation("45a0000000bb").StagingDatabaseName
        );

        [Test]
        public void It_drops_once_by_the_quoted_staging_name() =>
            _batch
                .Split("DROP DATABASE", StringSplitOptions.None)
                .Should()
                .HaveCount(2)
                .And.Subject.Last()
                .Should()
                .StartWith(" [edfi_dms_provision_stage_45a0000000bb];");

        [Test]
        public void It_guards_the_drop_with_the_name_the_id_and_the_primary_file()
        {
            string guard = _batch[.._batch.IndexOf("DROP DATABASE", StringComparison.Ordinal)];
            guard
                .Should()
                .Contain("d.name = @name")
                .And.Contain("d.database_id = @id")
                .And.Contain("= @file");
            guard.Should().Contain("f.file_id = 1");
        }

        [Test]
        public void It_returns_a_drop_error_as_a_number_inside_the_batch() =>
            _batch[_batch.IndexOf("DROP DATABASE", StringComparison.Ordinal)..]
                .Should()
                .Contain("BEGIN CATCH")
                .And.Contain("ERROR_NUMBER()")
                .And.Contain("ERROR_SEVERITY()");
    }

    [TestFixture]
    public class Given_requests_the_engine_must_refuse_before_connecting
    {
        private static readonly DmsOperationId _operation = Operation("45a0000000bb");
        private Exception? _foreignHolder;
        private Exception? _otherStagingOnRestore;
        private Exception? _foreignIdentityOnDrop;

        [SetUp]
        public async Task Setup()
        {
            // Nothing listens on port 1, so a request that reached the connection would fail with a SqlException.
            MssqlDmsTargetEngine engine = new(
                new MssqlAdminConnection(
                    "Server=127.0.0.1,1;User Id=sa;Password=unused;Connect Timeout=1;TrustServerCertificate=true",
                    TimeSpan.FromSeconds(5)
                ),
                MssqlEngineSettingsSample.With(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)),
                TimeProvider.System,
                NullLogger.Instance
            );
            DmsProvisionClaim claim = new(_operation, Guid.NewGuid());
            await using FakeDmsTargetLockHolder holder = new(
                ProvisioningTestValues.Session,
                TimeSpan.FromSeconds(5),
                ProvisioningTestValues.CommandTimeout,
                TimeProvider.System
            );
            _foreignHolder = await Record.ExceptionAsync(() =>
                engine.CreateStagingAsync(holder, _operation.StagingDatabaseName, claim, default)
            );
            _otherStagingOnRestore = await Record.ExceptionAsync(() =>
                engine.RestoreIntoStagingAsync(
                    DmsOperationId.New().StagingDatabaseName,
                    ProvisioningTestValues.Name("EdFi_Target"),
                    new StagedArtifact(
                        "/nowhere/artifact.bak",
                        new string('a', 64),
                        DmsArtifactKind.MssqlBackup
                    ),
                    _operation,
                    default
                )
            );
            _foreignIdentityOnDrop = await Record.ExceptionAsync(() =>
                engine.DropStagingAsync(
                    _operation.StagingDatabaseName,
                    new DmsInstanceIdentity.Postgresql(1, "claim"),
                    default
                )
            );
        }

        [Test]
        public void It_refuses_a_holder_it_did_not_acquire() =>
            _foreignHolder.Should().BeOfType<ArgumentException>().Which.ParamName.Should().Be("holder");

        [Test]
        public void It_refuses_a_restore_into_a_staging_name_that_is_not_the_operations() =>
            _otherStagingOnRestore
                .Should()
                .BeOfType<ArgumentException>()
                .Which.ParamName.Should()
                .Be("staging");

        [Test]
        public void It_refuses_to_drop_a_postgresql_identity() =>
            _foreignIdentityOnDrop
                .Should()
                .BeOfType<ArgumentException>()
                .Which.ParamName.Should()
                .Be("stagingInstance");
    }
}
