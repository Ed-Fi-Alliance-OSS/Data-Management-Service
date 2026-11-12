// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Mssql.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Mssql;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using static EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.ProvisioningTestValues;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning;

/// <summary>
/// The orchestrator's staging stage on the SQL Server branch (6.4 steps 2 to 8, Step 4.5) over the spy engine:
/// the default directories checked before anything is staged; the reservation the create returns replaced by
/// the instance the restore created, but only one that carries the reserved primary file name, and recorded for
/// cleanup before any loss check can discard it; a restore whose fate is open with no instance to read ending
/// <c>OutcomeUnknown</c>; the target handed to the restore and never addressed; and the SQL Server staging,
/// backup and integrity refusals mapped to their codes, each with the staging name dropped by the reservation.
/// </summary>
public class DmsDataStoreProvisioningServiceMssqlStagingTests
{
    private const string DataPath = "/var/opt/mssql/data";

    private static readonly DateTimeOffset _restoredAt = new(2026, 11, 7, 9, 0, 0, TimeSpan.Zero);

    private static readonly DmsServerSessionId _workSession = new(
        61,
        new DateTimeOffset(2026, 11, 7, 8, 0, 0, TimeSpan.Zero)
    );

    private static DmsServerFacts MssqlFacts(string? dataPath = DataPath, string? logPath = DataPath) =>
        new(18, dataPath, logPath);

    /// <summary>The primary file name the spy reserves for <paramref name="staging"/>.</summary>
    private static string ReservedFileOf(DmsDatabaseName staging) => staging.Value + "__op.mdf";

    internal static DmsInstanceIdentity.Mssql RestoredFor(DmsDatabaseName staging, string? fileName = null) =>
        new(77, DataPath + "/" + (fileName ?? ReservedFileOf(staging)), _restoredAt);

    private sealed class FakeSession(DmsInstanceIdentity instance, DmsServerSessionId session)
        : DmsSession(instance, session)
    {
        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    internal sealed class FaultHooks : IDmsProvisioningTestHooks
    {
        public HashSet<DmsProvisioningFaultPoint> Faults { get; } = [];

        public ValueTask AtAsync(
            DmsProvisioningHookPoint point,
            DmsOperationId operation,
            CancellationToken cancellationToken
        ) => ValueTask.CompletedTask;

        public bool ShouldInjectFault(DmsProvisioningFaultPoint point, DmsOperationId operation) =>
            Faults.Contains(point);
    }

    /// <summary>
    /// A SQL Server engine scripted so the whole create succeeds: the create reserves the name, the restore returns
    /// the instance carrying the reserved file, and every instance the orchestrator opens a session on, renames, or
    /// drops is recorded.
    /// </summary>
    internal sealed class MssqlLane
    {
        public MssqlLane(IDmsProvisioningTestHooks? hooks = null)
        {
            Harness = new Harness(hooks: hooks);
            SpyDmsTargetEngine engine = Harness.Engine;
            engine.Provider = DmsDataStoreProvisioningOptions.MssqlProvider;
            engine.OnReadServerFacts = _ => Task.FromResult(MssqlFacts());
            engine.OnInspectByName = (_, _) =>
                Task.FromResult<DmsLiveTargetState>(new DmsLiveTargetState.Absent());
            engine.OnCreateStaging = (staging, _, _) =>
            {
                Staging = staging;
                return Task.FromResult<DmsCommandOutcome<DmsInstanceIdentity>>(
                    new DmsCommandOutcome<DmsInstanceIdentity>.Succeeded(
                        new DmsInstanceIdentity.MssqlReservation(ReservedFileOf(staging))
                    )
                );
            };
            engine.OnRestoreIntoStaging = (staging, _, _) =>
                Task.FromResult<DmsCommandOutcome<DmsInstanceIdentity>>(
                    new DmsCommandOutcome<DmsInstanceIdentity>.Succeeded(RestoredFor(staging))
                );
            engine.OnOpenSession = (_, instance, _) =>
            {
                Opened.Add(instance);
                return Task.FromResult<DmsSessionOpenResult>(
                    new DmsSessionOpenResult.Opened(new FakeSession(instance, _workSession))
                );
            };
            engine.OnReadIdentity = (_, _) => Task.FromResult<DmsIdentityRead>(TargetFactsSample.Identity());
            engine.OnReadTargetFacts = (_, _) => Task.FromResult(TargetFactsSample.PostgresqlFacts());
            engine.OnTerminateSession = (_, _, _) =>
                Task.FromResult<DmsSessionTermination>(new DmsSessionTermination.Terminated());
            engine.OnDropStaging = (_, instance, _) =>
            {
                Dropped.Add(instance);
                return Task.FromResult<DmsCommandOutcome>(new DmsCommandOutcome.Succeeded());
            };
            engine.OnRenameIntoTarget = (_, instance, _, _) =>
            {
                Renamed.Add(instance);
                return Task.FromResult<DmsCommandOutcome<DmsInstanceIdentity>>(
                    new DmsCommandOutcome<DmsInstanceIdentity>.Succeeded(instance)
                );
            };
            engine.OnAssignMarker = (_, _, _) =>
                Task.FromResult<DmsCommandOutcome>(new DmsCommandOutcome.Succeeded());
        }

        public Harness Harness { get; }

        public SpyDmsTargetEngine Engine => Harness.Engine;

        public DmsDatabaseName Staging { get; private set; }

        public List<DmsInstanceIdentity> Opened { get; } = [];

        public List<DmsInstanceIdentity> Renamed { get; } = [];

        public List<DmsInstanceIdentity> Dropped { get; } = [];

        public DmsDataStoreCreateRequest Request { get; } = CreateRequest();

        public Task<DmsDataStoreCreateResult> CreateAsync() => Harness.Service.CreateAsync(Request, default);

        public int CountOf(string member) => Engine.Calls.Count(call => call.Member == member);
    }

    [TestFixture]
    public class Given_the_restore_creates_the_reserved_database
    {
        private MssqlLane _lane = null!;
        private DmsDataStoreCreateResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _lane = new MssqlLane();
            _result = await _lane.CreateAsync();
        }

        [Test]
        public void It_creates_the_target() => _result.Should().BeOfType<DmsDataStoreCreateResult.Created>();

        [Test]
        public void It_validates_and_publishes_the_restored_instance_not_the_reservation()
        {
            DmsInstanceIdentity restored = RestoredFor(_lane.Staging);
            _lane.Opened[0].Should().Be(restored);
            _lane.Renamed.Should().Equal(restored);
        }

        [Test]
        public void It_hands_the_target_name_to_the_restore() =>
            _lane.Engine.RestoreTargets.Should().Equal(_lane.Request.TargetName.Value);

        [Test]
        public void It_addresses_the_target_only_by_the_rename_and_the_marker_session() =>
            _lane
                .Engine.Calls.Where(call =>
                    call.DatabaseName == _lane.Request.TargetName.Value
                    && call.Member is not (SpyDmsTargetEngine.AcquireTargetLock or SpyDmsTargetEngine.Inspect)
                )
                .Select(call => call.Member)
                .Should()
                .Equal(SpyDmsTargetEngine.RenameIntoTarget, SpyDmsTargetEngine.OpenSession);

        [Test]
        public void It_drops_nothing_after_publishing() => _lane.Dropped.Should().BeEmpty();
    }

    [TestFixture]
    public class Given_the_server_reports_no_default_directory
    {
        private readonly List<MssqlLane> _lanes = [];
        private readonly List<DmsDataStoreCreateResult> _results = [];

        [SetUp]
        public async Task Setup()
        {
            _lanes.Clear();
            _results.Clear();
            foreach (DmsServerFacts facts in new[] { MssqlFacts(dataPath: null), MssqlFacts(logPath: null) })
            {
                MssqlLane lane = new();
                lane.Engine.OnReadServerFacts = _ => Task.FromResult(facts);
                _lanes.Add(lane);
                _results.Add(await lane.CreateAsync());
            }
        }

        [Test]
        public void It_reports_the_engine_incompatible_at_the_engine_check() =>
            _results
                .Select(result => (FailureOf(result).Code, FailureOf(result).Diagnostic.Operation))
                .Should()
                .AllBeEquivalentTo(
                    (
                        DmsProvisioningFailureCode.EngineIncompatible,
                        DmsDataStoreProvisioningService.EngineCheckStep
                    )
                );

        [Test]
        public void It_neither_reserves_nor_restores() =>
            _lanes
                .Select(lane =>
                    lane.CountOf(SpyDmsTargetEngine.CreateStaging)
                    + lane.CountOf(SpyDmsTargetEngine.RestoreIntoStaging)
                )
                .Should()
                .AllBeEquivalentTo(0);
    }

    [TestFixture]
    public class Given_the_postgresql_engine_reports_no_default_directory
    {
        private DmsDataStoreCreateResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            // The PostgreSQL facts carry no default directories: the SQL Server check must not apply to them.
            MssqlLane lane = new();
            lane.Engine.Provider = DmsDataStoreProvisioningOptions.PostgresqlProvider;
            lane.Engine.OnReadServerFacts = _ => Task.FromResult(SpyDmsTargetEngine.Facts());
            _result = await lane.CreateAsync();
        }

        [Test]
        public void It_is_not_refused_for_them() =>
            _result.Should().BeOfType<DmsDataStoreCreateResult.Created>();
    }

    [TestFixture]
    public class Given_the_restore_reports_a_database_without_the_reserved_file
    {
        private MssqlLane _lane = null!;
        private DmsDataStoreCreateResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _lane = new MssqlLane();
            _lane.Engine.OnRestoreIntoStaging = (staging, _, _) =>
                Task.FromResult<DmsCommandOutcome<DmsInstanceIdentity>>(
                    new DmsCommandOutcome<DmsInstanceIdentity>.Succeeded(
                        RestoredFor(staging, staging.Value + "__other.mdf")
                    )
                );
            _result = await _lane.CreateAsync();
        }

        [Test]
        public void It_reports_the_instance_changed_at_the_restore() =>
            (FailureOf(_result).Code, FailureOf(_result).Diagnostic.Operation)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.InstanceChanged,
                        DmsDataStoreProvisioningService.RestoreIntoStagingStep
                    )
                );

        [Test]
        public void It_never_opens_a_session_on_it() => _lane.Opened.Should().BeEmpty();

        [Test]
        public void It_drops_only_by_the_reservation() =>
            _lane
                .Dropped.Should()
                .Equal(new DmsInstanceIdentity.MssqlReservation(ReservedFileOf(_lane.Staging)));
    }

    [TestFixture]
    public class Given_a_definite_sql_server_restore_refusal
    {
        private MssqlLane _lane = null!;
        private DmsDataStoreCreateResult _result = null!;

        private static DmsCommandOutcome<DmsInstanceIdentity> RestoreRefused(string operation) =>
            new DmsCommandOutcome<DmsInstanceIdentity>.Failed(
                new DmsCommandFailure(
                    DmsCommandFailureClass.Rejected,
                    DmsProvisioningDiagnostic.ForOperation(operation)
                )
            );

        private async Task CreateRefusedAsync(string operation)
        {
            _lane = new MssqlLane();
            _lane.Engine.OnRestoreIntoStaging = (_, _, _) => Task.FromResult(RestoreRefused(operation));
            _result = await _lane.CreateAsync();
        }

        [TestCase(
            "StageBackup.Copy",
            DmsProvisioningFailureCode.StagingFailed,
            DmsProvisioningFailureCategory.Transient
        )]
        [TestCase(
            "StageBackup.NotVisible",
            DmsProvisioningFailureCode.StagingFailed,
            DmsProvisioningFailureCategory.Transient
        )]
        [TestCase(
            "StageBackup.UnsafeDirectory.ReplaceableEntries",
            DmsProvisioningFailureCode.StagingFailed,
            DmsProvisioningFailureCategory.OperatorInspectionRequired
        )]
        [TestCase(
            "StageBackup.UnsafeDirectory.SymbolicLink",
            DmsProvisioningFailureCode.StagingFailed,
            DmsProvisioningFailureCategory.OperatorInspectionRequired
        )]
        [TestCase(
            "PlanRestore.BackupSetCount",
            DmsProvisioningFailureCode.BackupInvalid,
            DmsProvisioningFailureCategory.OperatorInspectionRequired
        )]
        [TestCase(
            "PlanRestore.Unreadable",
            DmsProvisioningFailureCode.BackupInvalid,
            DmsProvisioningFailureCategory.OperatorInspectionRequired
        )]
        [TestCase(
            DmsEngineOperations.StagedBackupChanged,
            DmsProvisioningFailureCode.StagingIntegrityViolation,
            DmsProvisioningFailureCategory.OperatorInspectionRequired
        )]
        [TestCase(
            DmsEngineOperations.RestoreNotStarted,
            DmsProvisioningFailureCode.RestoreFailed,
            DmsProvisioningFailureCategory.Transient
        )]
        public async Task It_maps_the_engine_operation_to_its_code(
            string operation,
            DmsProvisioningFailureCode code,
            DmsProvisioningFailureCategory category
        )
        {
            await CreateRefusedAsync(operation);
            DmsProvisioningFailure failure = FailureOf(_result);
            (failure.Code, failure.Category, failure.Diagnostic.Operation)
                .Should()
                .Be((code, category, operation));
        }

        [TestCase("StageBackup.Copy")]
        [TestCase("StageBackup.UnsafeDirectory.SymbolicLink")]
        [TestCase("PlanRestore.BackupSetCount")]
        [TestCase(DmsEngineOperations.StagedBackupChanged)]
        public async Task It_drops_the_staging_name_by_the_reservation_and_reports_no_residue(
            string operation
        )
        {
            await CreateRefusedAsync(operation);
            _lane
                .Dropped.Should()
                .Equal(new DmsInstanceIdentity.MssqlReservation(ReservedFileOf(_lane.Staging)));
            CleanupOf(_result).HasResidue.Should().BeFalse();
        }

        [TestCase("StageBackup.Copy")]
        [TestCase("StageBackup.UnsafeDirectory.SymbolicLink")]
        [TestCase(DmsEngineOperations.StagedBackupChanged)]
        public async Task It_never_validates_or_addresses_the_target(string operation)
        {
            await CreateRefusedAsync(operation);
            (_lane.Opened.Count, _lane.Renamed.Count).Should().Be((0, 0));
        }
    }

    /// <summary>
    /// AC 11 (F1): the server rejects the backup header read on the work session, before any <c>RESTORE</c> is sent.
    /// The engine's own decision (<see cref="MssqlDmsTargetEngine.BackupReadRefused"/>) over a real
    /// <see cref="SqlException"/> feeds the real orchestrator: a temporary memory shortage (<c>701</c>) or a deadlock
    /// victim (<c>1205</c>) ends as a transient <c>RestoreFailed</c>, never as an invalid backup; a malformed backup
    /// stays <c>BackupInvalid</c> for inspection, a missing privilege stays a permanent <c>AdminPrivilegeMissing</c>,
    /// and a file the server cannot open stays the staging failure it was. Cleanup is the same for every one: the
    /// staging name is dropped by the reservation, no residue is reported, and the target is never addressed.
    /// </summary>
    [TestFixture(
        MssqlErrorClassifier.InsufficientMemory,
        DmsProvisioningFailureCode.RestoreFailed,
        DmsProvisioningFailureCategory.Transient,
        MssqlDmsTargetEngine.ReadBackupStep
    )]
    [TestFixture(
        MssqlErrorClassifier.DeadlockVictim,
        DmsProvisioningFailureCode.RestoreFailed,
        DmsProvisioningFailureCategory.Transient,
        MssqlDmsTargetEngine.ReadBackupStep
    )]
    [TestFixture(
        MssqlErrorClassifier.BackupMediaFamilyMalformed,
        DmsProvisioningFailureCode.BackupInvalid,
        DmsProvisioningFailureCategory.OperatorInspectionRequired,
        MssqlDmsTargetEngine.BackupUnreadableStep
    )]
    [TestFixture(
        MssqlErrorClassifier.RestoreNoPermission,
        DmsProvisioningFailureCode.AdminPrivilegeMissing,
        DmsProvisioningFailureCategory.Permanent,
        MssqlDmsTargetEngine.ReadBackupStep
    )]
    [TestFixture(
        MssqlErrorClassifier.BackupDeviceCannotOpen,
        DmsProvisioningFailureCode.StagingFailed,
        DmsProvisioningFailureCategory.Transient,
        MssqlBackupStager.NotVisibleStep
    )]
    public class Given_the_server_rejects_the_backup_header_read(
        int number,
        DmsProvisioningFailureCode code,
        DmsProvisioningFailureCategory category,
        string operation
    )
    {
        private MssqlLane _lane = null!;
        private DmsDataStoreCreateResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            SqlException exception =
                MssqlErrorClassifierTests.Given_a_server_error_from_a_rename_or_a_fence.Rejected(number);
            DmsCommandOutcome<DmsInstanceIdentity> refused = MssqlDmsTargetEngine.BackupReadRefused(
                exception,
                MssqlErrorClassifier.FailureClassOf(exception)
                    ?? throw new InvalidOperationException("The number must be a definite failure.")
            );
            _lane = new MssqlLane();
            _lane.Engine.OnRestoreIntoStaging = (_, _, _) => Task.FromResult(refused);
            _result = await _lane.CreateAsync();
        }

        [Test]
        public void It_reports_the_code_and_category_the_rejection_allows()
        {
            DmsProvisioningFailure failure = FailureOf(_result);
            (failure.Code, failure.Category, failure.Diagnostic.Operation)
                .Should()
                .Be((code, category, operation));
        }

        [Test]
        public void It_keeps_the_error_number_in_the_diagnostic() =>
            FailureOf(_result)
                .Diagnostic.ProviderErrorCode.Should()
                .Be(number.ToString(System.Globalization.CultureInfo.InvariantCulture));

        [Test]
        public void It_drops_the_staging_name_by_the_reservation_and_reports_no_residue()
        {
            _lane
                .Dropped.Should()
                .Equal(new DmsInstanceIdentity.MssqlReservation(ReservedFileOf(_lane.Staging)));
            CleanupOf(_result).HasResidue.Should().BeFalse();
        }

        [Test]
        public void It_never_validates_or_addresses_the_target() =>
            (_lane.Opened.Count, _lane.Renamed.Count).Should().Be((0, 0));
    }

    [TestFixture]
    public class Given_the_server_refuses_access_before_the_restore
    {
        private MssqlLane _lane = null!;
        private DmsDataStoreCreateResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            // What the engine returns when the work session's login fails (18456) before any RESTORE is sent.
            _lane = new MssqlLane();
            _lane.Engine.OnRestoreIntoStaging = (_, _, _) =>
                Task.FromResult<DmsCommandOutcome<DmsInstanceIdentity>>(
                    new DmsCommandOutcome<DmsInstanceIdentity>.Failed(
                        new DmsCommandFailure(
                            DmsCommandFailureClass.PrivilegeMissing,
                            new DmsProvisioningDiagnostic(
                                "Microsoft.Data.SqlClient.SqlException",
                                "18456",
                                MssqlDmsTargetEngine.RestoreAccessRefusedStep
                            )
                        )
                    )
                );
            _result = await _lane.CreateAsync();
        }

        [Test]
        public void It_reports_a_permanent_missing_admin_privilege_with_the_error_number()
        {
            DmsProvisioningFailure failure = FailureOf(_result);
            (
                failure.Code,
                failure.Category,
                failure.Diagnostic.Operation,
                failure.Diagnostic.ProviderErrorCode
            )
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.AdminPrivilegeMissing,
                        DmsProvisioningFailureCategory.Permanent,
                        MssqlDmsTargetEngine.RestoreAccessRefusedStep,
                        "18456"
                    )
                );
        }

        [Test]
        public void It_drops_the_staging_name_by_the_reservation_without_validating()
        {
            _lane
                .Dropped.Should()
                .Equal(new DmsInstanceIdentity.MssqlReservation(ReservedFileOf(_lane.Staging)));
            _lane.Opened.Should().BeEmpty();
        }
    }

    [TestFixture]
    public class Given_loss_discards_a_completed_restore
    {
        private MssqlLane _lane = null!;
        private DmsDataStoreCreateResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _lane = new MssqlLane();
            _lane.Engine.OnRestoreIntoStaging = (staging, _, _) =>
            {
                _lane.Harness.OnlyHolder.Lose();
                return Task.FromResult<DmsCommandOutcome<DmsInstanceIdentity>>(
                    new DmsCommandOutcome<DmsInstanceIdentity>.Succeeded(RestoredFor(staging))
                );
            };
            _result = await _lane.CreateAsync();
        }

        [Test]
        public void It_ends_lock_lost() =>
            FailureOf(_result).Code.Should().Be(DmsProvisioningFailureCode.LockLost);

        [Test]
        public void It_drops_the_restored_instance_it_observed_before_the_loss() =>
            _lane.Dropped.Should().Equal(RestoredFor(_lane.Staging));
    }

    [TestFixture]
    public class Given_a_restore_in_doubt_without_an_instance
    {
        private MssqlLane _named = null!;
        private MssqlLane _unnamed = null!;
        private DmsDataStoreCreateResult _namedResult = null!;
        private DmsDataStoreCreateResult _unnamedResult = null!;

        [SetUp]
        public async Task Setup()
        {
            // A work session the engine could not prove gone: the orchestrator proves it, but no instance exists to read.
            _named = new MssqlLane();
            _named.Engine.OnRestoreIntoStaging = (_, _, _) =>
                Task.FromResult<DmsCommandOutcome<DmsInstanceIdentity>>(
                    new DmsCommandOutcome<DmsInstanceIdentity>.Unknown(_workSession)
                );
            _namedResult = await _named.CreateAsync();

            // Nothing the engine could resolve: no session, not quiescent.
            _unnamed = new MssqlLane();
            _unnamed.Engine.OnRestoreIntoStaging = (_, _, _) =>
                Task.FromResult<DmsCommandOutcome<DmsInstanceIdentity>>(
                    new DmsCommandOutcome<DmsInstanceIdentity>.Unknown(null)
                );
            _unnamedResult = await _unnamed.CreateAsync();
        }

        [Test]
        public void It_reports_outcome_unknown_at_the_resolution_either_way() =>
            new[] { _namedResult, _unnamedResult }
                .Select(result => (FailureOf(result).Code, FailureOf(result).Diagnostic.Operation))
                .Should()
                .AllBeEquivalentTo(
                    (
                        DmsProvisioningFailureCode.OutcomeUnknown,
                        DmsDataStoreProvisioningService.ResolveRestoreStep
                    )
                );

        [Test]
        public void It_never_opens_a_session_on_the_reservation() =>
            (_named.Opened.Count, _unnamed.Opened.Count).Should().Be((0, 0));

        [Test]
        public void It_drops_by_the_reservation_only_once_the_work_session_is_proven_gone() =>
            _named
                .Dropped.Should()
                .Equal(new DmsInstanceIdentity.MssqlReservation(ReservedFileOf(_named.Staging)));

        [Test]
        public void It_withholds_the_drop_and_reports_residue_without_that_proof()
        {
            _unnamed.Dropped.Should().BeEmpty();
            CleanupOf(_unnamedResult)
                .Residue.Should()
                .ContainSingle(residue => residue.Kind == DmsResidueKind.StagingDatabase);
        }
    }

    [TestFixture]
    public class Given_the_client_loses_a_completed_restore_result
    {
        private MssqlLane _lane = null!;
        private DmsDataStoreCreateResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            FaultHooks hooks = new();
            hooks.Faults.Add(DmsProvisioningFaultPoint.DropConnectionDuringRestore);
            _lane = new MssqlLane(hooks);
            _result = await _lane.CreateAsync();
        }

        [Test]
        public void It_reads_the_restored_instance_and_goes_on_to_create_the_target()
        {
            _result.Should().BeOfType<DmsDataStoreCreateResult.Created>();
            _lane.Opened[0].Should().Be(RestoredFor(_lane.Staging));
        }
    }
}
