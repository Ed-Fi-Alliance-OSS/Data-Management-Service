// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Mssql.DataStoreProvisioning;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using static EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration.DataStoreProvisioning.MssqlProvisioningTestServer;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration.DataStoreProvisioning;

/// <summary>
/// The orchestrator's SQL Server staging stage end to end (6.4 steps 2 to 9, Step 4.5) over the real catalog and the
/// real engine, with a package whose artifact is a server-made backup of the committed DMS-shaped fixture. A valid
/// package reaches the rename with a validated online staging database carrying the operation's primary file and the
/// selected target never addressed by anything but an inspection (polled by a side session at every hook), and is then
/// published under the target name with nothing left under the staging name (Step 4.6). A backup
/// contaminated with a <c>dmscs</c> schema or drifted by a grant to <c>guest</c> ends with the staging database
/// dropped and the target absent; a staged file replaced after the restore is a staging integrity violation; and a
/// restore whose work session is lost, or that the caller cancels, while the database is restoring leaves nothing.
/// Needs a staging directory shared with the server.
/// </summary>
[Category(Category)]
public class MssqlCreateStagingStageTests
{
    /// <summary>What a side session sees of the staging and target names at a hook point.</summary>
    private sealed record HookObservation(
        DmsProvisioningHookPoint Point,
        DmsLiveTargetState Staging,
        Guid? StagingIdentity,
        DmsLiveTargetState Target
    );

    private static async Task<HookObservation> ObserveAsync(
        DmsProvisioningHookPoint point,
        DmsOperationId operation,
        DmsTargetDatabaseName target
    )
    {
        MssqlDmsTargetEngine side = Engine();
        DmsLiveTargetState staging = await side.InspectAsync(operation.StagingDatabaseName, default);
        Guid? identity = null;
        if (staging is DmsLiveTargetState.Present)
        {
            // The side connection must be gone before the hook returns: a session on the staging database refuses the
            // rename that follows (5030).
            identity = Guid.Parse(
                await new TestDatabase(operation.StagingDatabaseName.Value).ScalarAsync<string>(
                    "SELECT CONVERT(nvarchar(36), [SourceIdentity]) FROM [dms].[DataStoreIdentity];"
                )
            );
            await WaitForNoSessionsOnAsync(operation.StagingDatabaseName.Value);
        }

        return new HookObservation(point, staging, identity, await side.InspectAsync(target, default));
    }

    private static DmsProvisioningFailure FailureOf(DmsDataStoreCreateResult result) =>
        result.Should().BeOfType<DmsDataStoreCreateResult.Failure>().Subject.Detail;

    [TestFixture]
    public class Given_a_valid_package_reaches_the_rename
    {
        private Lane? _lane;
        private readonly LaneHooks _hooks = new();
        private readonly List<HookObservation> _observations = [];
        private DmsDataStoreCreateResult _result = null!;
        private DmsLiveTargetState _stagingAfter = null!;
        private DmsLiveTargetState _targetAfter = null!;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireSharedStagingOrSkip();
            _lane = await Lane.BuildAsync(await ShapedBackup.CreateAsync(), _hooks);
            _hooks.Probe = async (point, operation) =>
                _observations.Add(await ObserveAsync(point, operation, _lane.Target));
            _result = await _lane.CreateAsync();
            MssqlDmsTargetEngine side = Engine();
            _stagingAfter = await side.InspectAsync(_lane.Operation.StagingDatabaseName, default);
            _targetAfter = await side.InspectAsync(_lane.Target, default);
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            if (_lane is not null)
            {
                await _lane.DisposeAsync();
            }
        }

        [Test]
        public void It_had_a_validated_online_staging_database_and_no_target_at_the_rename_hook()
        {
            HookObservation atRename = _observations.Single(observation =>
                observation.Point == DmsProvisioningHookPoint.BeforeRename
            );
            (atRename.StagingIdentity, atRename.Target)
                .Should()
                .Be((MssqlDmsShapedManifest.SourceIdentity, new DmsLiveTargetState.Absent()));
            atRename
                .Staging.Should()
                .BeOfType<DmsLiveTargetState.Present>()
                .Which.State.Should()
                .Be(DmsDatabaseState.Online);
        }

        [Test]
        public void It_restored_the_database_carrying_the_operations_primary_file() =>
            new DmsInstanceIdentity.MssqlReservation(MssqlRestorePlanner.PrimaryFileNameFor(_lane!.Operation))
                .IsRealizedBy(
                    (
                        (DmsLiveTargetState.Present)
                            _observations
                                .Single(observation =>
                                    observation.Point == DmsProvisioningHookPoint.BeforeRename
                                )
                                .Staging
                    ).Instance
                )
                .Should()
                .BeTrue();

        [Test]
        public void It_addressed_the_target_only_by_the_rename_and_the_marker_session() =>
            _lane!
                .TargetMutations.Select(call => call.Member)
                .Should()
                .Equal(
                    nameof(IDmsTargetEngine.RenameIntoTargetAsync),
                    nameof(IDmsTargetEngine.OpenSessionAsync)
                );

        [Test]
        public void It_creates_the_target() => _result.Should().BeOfType<DmsDataStoreCreateResult.Created>();

        [Test]
        public void It_publishes_the_validated_staging_instance_under_the_target_name()
        {
            DmsInstanceIdentity validated = (
                (DmsLiveTargetState.Present)
                    _observations
                        .Single(observation => observation.Point == DmsProvisioningHookPoint.BeforeRename)
                        .Staging
            ).Instance;
            (_lane!.Engine.DroppedStagingInstances.Count, _stagingAfter)
                .Should()
                .Be((0, new DmsLiveTargetState.Absent()));
            _targetAfter
                .Should()
                .BeOfType<DmsLiveTargetState.Present>()
                .Which.Instance.IsSameStableInstance(validated)
                .Should()
                .BeTrue();
        }

        [Test]
        public void It_passed_the_hooks_in_order() =>
            _hooks
                .Points.Should()
                .Equal(
                    DmsProvisioningHookPoint.AfterRestoreStarted,
                    DmsProvisioningHookPoint.BeforeRename,
                    DmsProvisioningHookPoint.BeforeMarker,
                    DmsProvisioningHookPoint.BeforeMarkerDispatch
                );

        [Test]
        public void It_deleted_the_staged_file() =>
            ShapedBackup.StagedFilesOf(_lane!.Operation).Should().BeEmpty();
    }

    [TestFixture]
    public class Given_the_backup_drifts_from_the_manifest
    {
        private Lane? _contaminated;
        private Lane? _drifted;
        private readonly LaneHooks _contaminatedHooks = new();
        private readonly LaneHooks _driftedHooks = new();
        private readonly List<HookObservation> _observations = [];
        private DmsDataStoreCreateResult _contaminatedResult = null!;
        private DmsDataStoreCreateResult _driftedResult = null!;
        private readonly List<DmsLiveTargetState> _namesAfter = [];

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireSharedStagingOrSkip();
            _contaminated = await Lane.BuildAsync(
                await ShapedBackup.CreateAsync("CREATE SCHEMA [dmscs];"),
                _contaminatedHooks
            );
            _contaminatedHooks.Probe = async (point, operation) =>
                _observations.Add(await ObserveAsync(point, operation, _contaminated.Target));
            _contaminatedResult = await _contaminated.CreateAsync();

            _drifted = await Lane.BuildAsync(
                await ShapedBackup.CreateAsync("GRANT SELECT TO [guest];"),
                _driftedHooks
            );
            _driftedHooks.Probe = async (point, operation) =>
                _observations.Add(await ObserveAsync(point, operation, _drifted.Target));
            _driftedResult = await _drifted.CreateAsync();

            MssqlDmsTargetEngine side = Engine();
            foreach (Lane lane in new[] { _contaminated, _drifted })
            {
                _namesAfter.Add(await side.InspectAsync(lane.Operation.StagingDatabaseName, default));
                _namesAfter.Add(await side.InspectAsync(lane.Target, default));
            }
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            if (_contaminated is not null)
            {
                await _contaminated.DisposeAsync();
            }

            if (_drifted is not null)
            {
                await _drifted.DisposeAsync();
            }
        }

        [Test]
        public void It_reports_the_extra_schema_as_incompatible() =>
            (FailureOf(_contaminatedResult).Code, FailureOf(_contaminatedResult).Diagnostic.Operation)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.TargetIncompatible,
                        DmsDataStoreProvisioningService.ValidateStagingStep
                            + "."
                            + DmsTargetValidator.InventoryHashCheck
                    )
                );

        [Test]
        public void It_reports_the_grant_to_guest_as_a_security_state_violation() =>
            (FailureOf(_driftedResult).Code, FailureOf(_driftedResult).Diagnostic.Operation)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.SecurityStateViolation,
                        DmsDataStoreProvisioningService.ValidateStagingStep
                            + "."
                            + DmsTargetValidator.SecurityStateCheck
                    )
                );

        [Test]
        public void It_never_reached_the_rename_hook_and_never_saw_a_target()
        {
            (_contaminatedHooks.Points, _driftedHooks.Points)
                .Should()
                .BeEquivalentTo(
                    (
                        new[] { DmsProvisioningHookPoint.AfterRestoreStarted },
                        new[] { DmsProvisioningHookPoint.AfterRestoreStarted }
                    )
                );
            _observations
                .Should()
                .OnlyContain(observation => observation.Target is DmsLiveTargetState.Absent);
        }

        [Test]
        public void It_dropped_both_staging_databases_without_residue_and_left_the_targets_absent()
        {
            _namesAfter.Should().OnlyContain(state => state is DmsLiveTargetState.Absent);
            (
                ((DmsDataStoreCreateResult.Failure)_contaminatedResult).Cleanup.HasResidue,
                ((DmsDataStoreCreateResult.Failure)_driftedResult).Cleanup.HasResidue
            )
                .Should()
                .Be((false, false));
        }

        [Test]
        public void It_never_addressed_either_target() =>
            _contaminated!.TargetMutations.Concat(_drifted!.TargetMutations).Should().BeEmpty();
    }

    [TestFixture]
    public class Given_the_staged_file_is_replaced_after_the_restore
    {
        private Lane? _lane;
        private DmsDataStoreCreateResult _result = null!;
        private DmsLiveTargetState _stagingAfter = null!;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireSharedStagingOrSkip();
            MssqlDmsTargetEngine engine = new(
                AdminConnection(),
                Settings(),
                TimeProvider.System,
                NullLogger.Instance
            )
            {
                AfterRestore = backup =>
                    File.WriteAllBytesAsync(backup.LocalPath, RandomNumberGenerator.GetBytes(4096)),
            };
            _lane = await Lane.BuildAsync(await ShapedBackup.CreateAsync(), new LaneHooks(), engine);
            _result = await _lane.CreateAsync();
            _stagingAfter = await Engine().InspectAsync(_lane.Operation.StagingDatabaseName, default);
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            if (_lane is not null)
            {
                await _lane.DisposeAsync();
            }
        }

        [Test]
        public void It_reports_a_staging_integrity_violation_for_inspection() =>
            (FailureOf(_result).Code, FailureOf(_result).Category)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.StagingIntegrityViolation,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired
                    )
                );

        [Test]
        public void It_drops_the_restored_database_by_the_reservation_and_leaves_the_target_absent()
        {
            _lane!
                .Engine.DroppedStagingInstances.Should()
                .ContainSingle()
                .Which.Should()
                .BeOfType<DmsInstanceIdentity.MssqlReservation>();
            _stagingAfter.Should().Be(new DmsLiveTargetState.Absent());
            _lane.TargetMutations.Should().BeEmpty();
        }
    }

    [TestFixture]
    public class Given_the_restore_is_interrupted_while_restoring
    {
        private Lane? _lost;
        private Lane? _cancelled;
        private DmsDataStoreCreateResult _lostResult = null!;
        private Exception? _cancelledThrown;
        private bool _lostInterrupted;
        private bool _cancelledInterrupted;
        private readonly List<DmsLiveTargetState> _namesAfter = [];

        private static MssqlDmsTargetEngine WaitingEngine(string purpose) =>
            new(AdminConnection(), Settings(), TimeProvider.System, NullLogger.Instance)
            {
                RestoreStatement = (plan, serverPath) =>
                    plan.RestoreStatement(serverPath)
                        .Replace("STATS = 10;", "NORECOVERY, STATS = 10;", StringComparison.Ordinal)
                    + $"\nWAITFOR DELAY '00:02:00'; -- dms1438_{purpose}_{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6))}",
            };

        /// <summary>Waits until the create's work session is in the <c>WAITFOR</c> after its <c>NORECOVERY</c> restore; returns its id, or 0.</summary>
        private static async Task<int> WaitingWorkSessionAsync(Lane lane)
        {
            int sessionId = 0;
            bool waiting = await WaitUntilAsync(
                async () =>
                {
                    if (lane.Engine.CountOf(nameof(IDmsTargetEngine.CreateStagingAsync)) == 0)
                    {
                        return false;
                    }

                    sessionId = await SessionIdNamedAsync(lane.Operation.RestoreApplicationName);
                    return sessionId != 0 && await WaitTypeOfAsync(sessionId) == "WAITFOR";
                },
                TimeSpan.FromSeconds(60)
            );
            return waiting ? sessionId : 0;
        }

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireSharedStagingOrSkip();
            byte[] backup = await ShapedBackup.CreateAsync();

            _lost = await Lane.BuildAsync(backup, new LaneHooks(), WaitingEngine("lost"));
            Task<DmsDataStoreCreateResult> lost = _lost.CreateAsync();
            int sessionId = await WaitingWorkSessionAsync(_lost);
            _lostInterrupted = sessionId != 0;
            if (_lostInterrupted)
            {
                await KillAsync(sessionId);
            }

            _lostResult = await lost;

            _cancelled = await Lane.BuildAsync(backup, new LaneHooks(), WaitingEngine("cancelled"));
            using CancellationTokenSource cancellation = new();
            Task<DmsDataStoreCreateResult> cancelled = _cancelled.CreateAsync(cancellation.Token);
            _cancelledInterrupted = await WaitingWorkSessionAsync(_cancelled) != 0;
            await cancellation.CancelAsync();
            try
            {
                await cancelled;
            }
            catch (Exception exception)
            {
                _cancelledThrown = exception;
            }

            MssqlDmsTargetEngine side = Engine();
            foreach (Lane lane in new[] { _lost, _cancelled })
            {
                _namesAfter.Add(await side.InspectAsync(lane.Operation.StagingDatabaseName, default));
                _namesAfter.Add(await side.InspectAsync(lane.Target, default));
            }
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            if (_lost is not null)
            {
                await _lost.DisposeAsync();
            }

            if (_cancelled is not null)
            {
                await _cancelled.DisposeAsync();
            }
        }

        [Test]
        public void It_interrupted_both_while_the_database_was_restoring() =>
            (_lostInterrupted, _cancelledInterrupted).Should().Be((true, true));

        [Test]
        public void It_reports_a_lost_work_session_as_a_transient_restore_failure() =>
            (
                FailureOf(_lostResult).Code,
                FailureOf(_lostResult).Category,
                FailureOf(_lostResult).Diagnostic.Operation
            )
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.RestoreFailed,
                        DmsProvisioningFailureCategory.Transient,
                        DmsEngineOperations.RestoreRolledBack
                    )
                );

        [Test]
        public void It_propagates_the_callers_cancellation() =>
            _cancelledThrown.Should().BeAssignableTo<OperationCanceledException>();

        [Test]
        public void It_drops_both_restoring_databases_and_leaves_the_targets_absent()
        {
            _namesAfter.Should().OnlyContain(state => state is DmsLiveTargetState.Absent);
            ((DmsDataStoreCreateResult.Failure)_lostResult).Cleanup.HasResidue.Should().BeFalse();
        }
    }
}
