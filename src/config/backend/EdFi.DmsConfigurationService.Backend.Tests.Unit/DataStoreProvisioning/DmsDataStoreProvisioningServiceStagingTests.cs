// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Catalog;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Packaging;
using EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Packaging;
using EdFi.DmsConfigurationService.Backend.Tests.Unit.Jobs;
using FluentAssertions;
using static EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.ProvisioningTestValues;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning;

/// <summary>
/// The orchestrator's staging stage (6.3 steps 2 to 7, Step 3.5) over the spy engine and the real
/// catalog: the order of engine calls through the publication that follows, with nothing but an
/// inspection addressing the selected target name before the rename; the staging database dropped on
/// every failure path and after lock loss, and withheld only while a restore's fate is open; residue when
/// the drop fails; the restore's definite and unresolved outcomes mapped to the 6.3 step 5 codes; the
/// holder in doubt after an unknown create; and the D-23 hooks and fault in their places.
/// </summary>
public class DmsDataStoreProvisioningServiceStagingTests
{
    private static readonly DmsInstanceIdentity.Postgresql _stagingInstance = new(4242, "stage-claim");
    private static readonly DmsServerSessionId _stagingSession = new(
        99,
        new DateTimeOffset(2026, 11, 6, 0, 0, 0, TimeSpan.Zero)
    );

    private static DmsCommandOutcome<DmsInstanceIdentity> Staged() =>
        new DmsCommandOutcome<DmsInstanceIdentity>.Succeeded(_stagingInstance);

    private static DmsCommandOutcome<DmsInstanceIdentity> RestoreFailed(
        DmsCommandFailureClass failureClass,
        string operation
    ) =>
        new DmsCommandOutcome<DmsInstanceIdentity>.Failed(
            new DmsCommandFailure(failureClass, DmsProvisioningDiagnostic.ForOperation(operation))
        );

    private sealed class RecordingHooks : IDmsProvisioningTestHooks
    {
        public List<DmsProvisioningHookPoint> Points { get; } = [];

        public HashSet<DmsProvisioningFaultPoint> Faults { get; } = [];

        public ValueTask AtAsync(
            DmsProvisioningHookPoint point,
            DmsOperationId operation,
            CancellationToken cancellationToken
        )
        {
            Points.Add(point);
            return ValueTask.CompletedTask;
        }

        public bool ShouldInjectFault(DmsProvisioningFaultPoint point, DmsOperationId operation) =>
            Faults.Contains(point);
    }

    private sealed class FakeSession(DmsInstanceIdentity instance, DmsServerSessionId session)
        : DmsSession(instance, session)
    {
        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>A harness scripted so the whole staging stage succeeds and the publication that follows creates the target.</summary>
    private static Harness Scripted(
        RecordingHooks? hooks = null,
        Action<DmsDataStoreProvisioningOptions>? configure = null,
        bool systemClock = false
    )
    {
        Harness harness = new(hooks: hooks, configure: configure, systemClock: systemClock);
        SpyDmsTargetEngine engine = harness.Engine;
        engine.OnInspectByName = (_, _) =>
            Task.FromResult<DmsLiveTargetState>(new DmsLiveTargetState.Absent());
        engine.OnCreateStaging = (_, _, _) => Task.FromResult(Staged());
        engine.OnRestoreIntoStaging = (_, artifact, _) =>
        {
            File.Exists(artifact.FilePath).Should().BeTrue("the artifact is extracted before the restore");
            return Task.FromResult(Staged());
        };
        engine.OnOpenSession = (_, instance, _) =>
            Task.FromResult<DmsSessionOpenResult>(
                new DmsSessionOpenResult.Opened(new FakeSession(instance, _stagingSession))
            );
        engine.OnReadIdentity = (_, _) => Task.FromResult<DmsIdentityRead>(TargetFactsSample.Identity());
        engine.OnReadTargetFacts = (_, _) => Task.FromResult(TargetFactsSample.PostgresqlFacts());
        engine.OnTerminateSession = (_, _, _) =>
            Task.FromResult<DmsSessionTermination>(new DmsSessionTermination.Terminated());
        engine.OnDropStaging = (_, _, _) =>
            Task.FromResult<DmsCommandOutcome>(new DmsCommandOutcome.Succeeded());
        engine.OnRenameIntoTarget = (_, instance, _, _) =>
            Task.FromResult<DmsCommandOutcome<DmsInstanceIdentity>>(
                new DmsCommandOutcome<DmsInstanceIdentity>.Succeeded(instance)
            );
        engine.OnAssignMarker = (_, _, _) =>
            Task.FromResult<DmsCommandOutcome>(new DmsCommandOutcome.Succeeded());
        return harness;
    }

    private static Task<DmsDataStoreCreateResult> CreateAsync(
        Harness harness,
        CancellationToken cancellationToken = default
    ) => harness.Service.CreateAsync(CreateRequest(), cancellationToken);

    private static IReadOnlyList<string> Members(Harness harness) => harness.Engine.Members;

    private static int CountOf(Harness harness, string member) =>
        harness.Engine.Calls.Count(call => call.Member == member);

    /// <summary>Every call that named the selected target, other than the lock and inspections.</summary>
    private static IReadOnlyList<SpyDmsTargetEngine.Call> TargetMutations(Harness harness) =>
        [
            .. harness.Engine.Calls.Where(call =>
                call.DatabaseName == CreateRequest().TargetName.Value
                && call.Member is not (SpyDmsTargetEngine.AcquireTargetLock or SpyDmsTargetEngine.Inspect)
            ),
        ];

    [TestFixture]
    public class Given_a_valid_package_is_staged_and_published
    {
        private Harness _harness = null!;
        private RecordingHooks _hooks = null!;
        private DmsDataStoreCreateResult _result = null!;
        private bool _directoryReleased;

        [SetUp]
        public async Task Setup()
        {
            _hooks = new RecordingHooks();
            _harness = Scripted(_hooks);
            _result = await CreateAsync(_harness);
            _directoryReleased = SharedTemplateCatalog.Workspace.OperationEntries.Length == 0;
        }

        [Test]
        public void It_creates_the_target_with_the_staged_pin_and_no_residue() =>
            _result
                .Should()
                .Be(new DmsDataStoreCreateResult.Created(SharedTemplateCatalog.Pin, DmsCleanupReport.Clean));

        [Test]
        public void It_runs_the_stage_in_the_6_3_order_and_publishes_instead_of_dropping()
        {
            string target = CreateRequest().TargetName.Value;
            string stage = _harness
                .Engine.Calls.Single(call => call.Member == SpyDmsTargetEngine.CreateStaging)
                .DatabaseName!;
            stage.Should().StartWith(DmsOperationId.StagingDatabaseNamePrefix);
            _harness
                .Engine.Calls.Should()
                .Equal(
                    new SpyDmsTargetEngine.Call(SpyDmsTargetEngine.AcquireTargetLock, target),
                    new SpyDmsTargetEngine.Call(SpyDmsTargetEngine.ReadServerFacts, null),
                    new SpyDmsTargetEngine.Call(SpyDmsTargetEngine.Inspect, target),
                    new SpyDmsTargetEngine.Call(SpyDmsTargetEngine.Inspect, stage),
                    new SpyDmsTargetEngine.Call(SpyDmsTargetEngine.CreateStaging, stage),
                    new SpyDmsTargetEngine.Call(SpyDmsTargetEngine.RestoreIntoStaging, stage),
                    new SpyDmsTargetEngine.Call(SpyDmsTargetEngine.OpenSession, stage),
                    new SpyDmsTargetEngine.Call(SpyDmsTargetEngine.ReadIdentity, null),
                    new SpyDmsTargetEngine.Call(SpyDmsTargetEngine.ReadTargetFacts, null),
                    new SpyDmsTargetEngine.Call(SpyDmsTargetEngine.TerminateSession, null),
                    new SpyDmsTargetEngine.Call(SpyDmsTargetEngine.Inspect, target),
                    new SpyDmsTargetEngine.Call(SpyDmsTargetEngine.RenameIntoTarget, target),
                    new SpyDmsTargetEngine.Call(SpyDmsTargetEngine.OpenSession, target),
                    new SpyDmsTargetEngine.Call(SpyDmsTargetEngine.ReadIdentity, null),
                    new SpyDmsTargetEngine.Call(SpyDmsTargetEngine.ReadTargetFacts, null),
                    new SpyDmsTargetEngine.Call(SpyDmsTargetEngine.AssignMarker, null)
                );
        }

        [Test]
        public void It_addresses_the_target_only_by_the_rename_and_the_marker_session() =>
            TargetMutations(_harness)
                .Select(call => call.Member)
                .Should()
                .Equal(SpyDmsTargetEngine.RenameIntoTarget, SpyDmsTargetEngine.OpenSession);

        [Test]
        public void It_passes_the_hooks_in_order() =>
            _hooks
                .Points.Should()
                .Equal(
                    DmsProvisioningHookPoint.AfterRestoreStarted,
                    DmsProvisioningHookPoint.BeforeRename,
                    DmsProvisioningHookPoint.BeforeMarker
                );

        [Test]
        public void It_releases_the_holder_and_the_artifact() =>
            (_harness.OnlyHolder.ReleaseCount, _directoryReleased).Should().Be((1, true));
    }

    [TestFixture]
    public class Given_the_stage_is_refused_before_anything_is_created
    {
        private Harness _oldEngine = null!;
        private Harness _nameTaken = null!;
        private DmsDataStoreCreateResult _engineIncompatible = null!;
        private DmsDataStoreCreateResult _collision = null!;

        [SetUp]
        public async Task Setup()
        {
            _oldEngine = Scripted();
            _oldEngine.Engine.OnReadServerFacts = _ =>
                Task.FromResult(SpyDmsTargetEngine.Facts() with { MajorVersion = 17 });
            _engineIncompatible = await CreateAsync(_oldEngine);

            _nameTaken = Scripted();
            _nameTaken.Engine.OnInspectByName = (name, _) =>
                Task.FromResult<DmsLiveTargetState>(
                    name.Value.StartsWith(DmsOperationId.StagingDatabaseNamePrefix, StringComparison.Ordinal)
                        ? new DmsLiveTargetState.Present(_stagingInstance, DmsDatabaseState.Online)
                        : new DmsLiveTargetState.Absent()
                );
            _collision = await CreateAsync(_nameTaken);
        }

        [Test]
        public void It_refuses_a_live_major_below_the_manifest()
        {
            FailureOf(_engineIncompatible).Code.Should().Be(DmsProvisioningFailureCode.EngineIncompatible);
            Members(_oldEngine).Should().NotContain(SpyDmsTargetEngine.CreateStaging);
        }

        [Test]
        public void It_reports_a_taken_staging_name_as_a_transient_collision()
        {
            DmsProvisioningFailure failure = FailureOf(_collision);
            (failure.Code, failure.Category)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.StagingNameCollision,
                        DmsProvisioningFailureCategory.Transient
                    )
                );
            Members(_nameTaken).Should().NotContain(SpyDmsTargetEngine.CreateStaging);
        }

        [Test]
        public void It_drops_nothing() =>
            (
                CountOf(_oldEngine, SpyDmsTargetEngine.DropStaging),
                CountOf(_nameTaken, SpyDmsTargetEngine.DropStaging)
            )
                .Should()
                .Be((0, 0));
    }

    [TestFixture]
    public class Given_the_staging_create_fails_definitely
    {
        private readonly Dictionary<
            DmsCommandFailureClass,
            (Harness Harness, DmsDataStoreCreateResult Result)
        > _runs = [];

        [SetUp]
        public async Task Setup()
        {
            _runs.Clear();
            foreach (
                DmsCommandFailureClass failureClass in new[]
                {
                    DmsCommandFailureClass.DuplicateName,
                    DmsCommandFailureClass.PrivilegeMissing,
                    DmsCommandFailureClass.Rejected,
                }
            )
            {
                Harness harness = Scripted();
                harness.Engine.OnCreateStaging = (_, _, _) =>
                    Task.FromResult<DmsCommandOutcome<DmsInstanceIdentity>>(
                        new DmsCommandOutcome<DmsInstanceIdentity>.Failed(
                            new DmsCommandFailure(
                                failureClass,
                                DmsProvisioningDiagnostic.ForOperation("CreateStaging")
                            )
                        )
                    );
                _runs[failureClass] = (harness, await CreateAsync(harness));
            }
        }

        [Test]
        public void It_maps_each_class_to_its_code() =>
            _runs
                .ToDictionary(run => run.Key, run => FailureOf(run.Value.Result).Code)
                .Should()
                .Equal(
                    new Dictionary<DmsCommandFailureClass, DmsProvisioningFailureCode>
                    {
                        [DmsCommandFailureClass.DuplicateName] =
                            DmsProvisioningFailureCode.StagingNameCollision,
                        [DmsCommandFailureClass.PrivilegeMissing] =
                            DmsProvisioningFailureCode.AdminPrivilegeMissing,
                        [DmsCommandFailureClass.Rejected] = DmsProvisioningFailureCode.ServerRejected,
                    }
                );

        [Test]
        public void It_neither_restores_nor_drops() =>
            _runs
                .Values.Should()
                .OnlyContain(run =>
                    !run.Harness.Engine.Members.Contains(SpyDmsTargetEngine.RestoreIntoStaging)
                    && !run.Harness.Engine.Members.Contains(SpyDmsTargetEngine.DropStaging)
                );
    }

    [TestFixture]
    public class Given_the_staging_create_outcome_is_unknown
    {
        private Harness _proven = null!;
        private Harness _unproven = null!;
        private DmsDataStoreCreateResult _provenResult = null!;
        private DmsDataStoreCreateResult _unprovenResult = null!;

        private static Harness InDoubt(DmsSessionTermination verdict)
        {
            Harness harness = Scripted();
            DmsProvisionClaim? claim = null;
            harness.Engine.OnCreateStaging = (_, requested, _) =>
            {
                claim = requested;
                return Task.FromResult<DmsCommandOutcome<DmsInstanceIdentity>>(
                    new DmsCommandOutcome<DmsInstanceIdentity>.Unknown(Session)
                );
            };
            harness.Engine.OnTerminateSession = (_, _, _) => Task.FromResult(verdict);
            harness.Engine.OnInspectByName = (name, _) =>
                Task.FromResult<DmsLiveTargetState>(
                    name.Value.StartsWith(DmsOperationId.StagingDatabaseNamePrefix, StringComparison.Ordinal)
                    && claim is not null
                        ? new DmsLiveTargetState.Present(
                            new DmsInstanceIdentity.Postgresql(7, claim.Text),
                            DmsDatabaseState.Online
                        )
                        : new DmsLiveTargetState.Absent()
                );
            return harness;
        }

        [SetUp]
        public async Task Setup()
        {
            _proven = InDoubt(new DmsSessionTermination.Terminated());
            _provenResult = await CreateAsync(_proven);
            _unproven = InDoubt(
                new DmsSessionTermination.NotTerminated(DmsSessionTerminationFailure.TimedOut)
            );
            _unprovenResult = await CreateAsync(_unproven);
        }

        [Test]
        public void It_ends_as_transient_lock_lost_either_way()
        {
            (FailureOf(_provenResult).Code, FailureOf(_provenResult).Category)
                .Should()
                .Be((DmsProvisioningFailureCode.LockLost, DmsProvisioningFailureCategory.Transient));
            (FailureOf(_unprovenResult).Code, FailureOf(_unprovenResult).Category)
                .Should()
                .Be((DmsProvisioningFailureCode.LockLost, DmsProvisioningFailureCategory.Transient));
        }

        [Test]
        public void It_terminates_the_holder_then_inspects_the_staging_name() =>
            Members(_proven)
                .Skip(3)
                .Should()
                .StartWith([
                    SpyDmsTargetEngine.Inspect,
                    SpyDmsTargetEngine.CreateStaging,
                    SpyDmsTargetEngine.TerminateSession,
                    SpyDmsTargetEngine.Inspect,
                ]);

        [Test]
        public void It_drops_the_claimed_staging_database_once_the_holder_is_proven_gone() =>
            (CountOf(_proven, SpyDmsTargetEngine.DropStaging), CleanupOf(_provenResult).HasResidue)
                .Should()
                .Be((1, false));

        [Test]
        public void It_withholds_the_drop_and_reports_residue_without_proof()
        {
            CountOf(_unproven, SpyDmsTargetEngine.DropStaging).Should().Be(0);
            CleanupOf(_unprovenResult)
                .Residue.Should()
                .ContainSingle(residue => residue.Kind == DmsResidueKind.StagingDatabase);
        }
    }

    [TestFixture]
    public class Given_the_restore_fails_definitely
    {
        private sealed record Run(
            string Name,
            DmsCommandOutcome<DmsInstanceIdentity> Outcome,
            DmsProvisioningFailureCode Code,
            DmsProvisioningFailureCategory Category
        );

        private static readonly Run[] _table =
        [
            new(
                "rejected",
                RestoreFailed(DmsCommandFailureClass.Rejected, "RestoreIntoStaging"),
                DmsProvisioningFailureCode.RestoreFailed,
                DmsProvisioningFailureCategory.Permanent
            ),
            new(
                "rolled back",
                RestoreFailed(DmsCommandFailureClass.Rejected, DmsEngineOperations.RestoreRolledBack),
                DmsProvisioningFailureCode.RestoreFailed,
                DmsProvisioningFailureCategory.Transient
            ),
            new(
                "timed out",
                RestoreFailed(DmsCommandFailureClass.Rejected, DmsEngineOperations.RestoreTimedOut),
                DmsProvisioningFailureCode.TimedOut,
                DmsProvisioningFailureCategory.Transient
            ),
            new(
                "role unsafe",
                RestoreFailed(DmsCommandFailureClass.Rejected, DmsEngineOperations.RoleStateUnsafe),
                DmsProvisioningFailureCode.RoleStateUnsafe,
                DmsProvisioningFailureCategory.Permanent
            ),
            new(
                "privilege",
                RestoreFailed(DmsCommandFailureClass.PrivilegeMissing, "EnsureEnqueueOwner"),
                DmsProvisioningFailureCode.AdminPrivilegeMissing,
                DmsProvisioningFailureCategory.Permanent
            ),
        ];

        private readonly List<(Run Run, Harness Harness, DmsDataStoreCreateResult Result)> _runs = [];

        [SetUp]
        public async Task Setup()
        {
            _runs.Clear();
            foreach (Run run in _table)
            {
                Harness harness = Scripted();
                harness.Engine.OnRestoreIntoStaging = (_, _, _) => Task.FromResult(run.Outcome);
                _runs.Add((run, harness, await CreateAsync(harness)));
            }
        }

        [Test]
        public void It_maps_what_ended_the_replay_to_the_code_and_category() =>
            _runs
                .Select(run => (run.Run.Name, FailureOf(run.Result).Code, FailureOf(run.Result).Category))
                .Should()
                .Equal(_table.Select(run => (run.Name, run.Code, run.Category)));

        [Test]
        public void It_drops_the_staging_database_without_validating() =>
            _runs
                .Should()
                .OnlyContain(run =>
                    run.Harness.Engine.Members.Count(member => member == SpyDmsTargetEngine.DropStaging) == 1
                    && !run.Harness.Engine.Members.Contains(SpyDmsTargetEngine.OpenSession)
                );
    }

    [TestFixture]
    public class Given_the_restore_is_cancelled_by_the_caller
    {
        private Harness _harness = null!;
        private Exception? _outcome;
        private bool _directoryReleased;

        [SetUp]
        public async Task Setup()
        {
            using CancellationTokenSource cancellation = new();
            _harness = Scripted();
            _harness.Engine.OnRestoreIntoStaging = async (_, _, _) =>
            {
                await cancellation.CancelAsync();
                return RestoreFailed(DmsCommandFailureClass.Rejected, DmsEngineOperations.RestoreCancelled);
            };
            _outcome = await Record.ExceptionAsync(() => CreateAsync(_harness, cancellation.Token));
            _directoryReleased = SharedTemplateCatalog.Workspace.OperationEntries.Length == 0;
        }

        [Test]
        public void It_propagates_the_cancellation() =>
            _outcome.Should().BeAssignableTo<OperationCanceledException>();

        [Test]
        public void It_cleans_up_first() =>
            (
                CountOf(_harness, SpyDmsTargetEngine.DropStaging),
                _harness.OnlyHolder.ReleaseCount,
                _directoryReleased
            )
                .Should()
                .Be((1, 1, true));
    }

    [TestFixture]
    public class Given_the_restore_outcome_is_unresolved
    {
        private Harness _rolledBack = null!;
        private Harness _oneTableOnly = null!;
        private Harness _sessionUnproven = null!;
        private Harness _sessionProven = null!;
        private Harness _clientLostTheResult = null!;
        private RecordingHooks _faultHooks = null!;
        private DmsDataStoreCreateResult _rolledBackResult = null!;
        private DmsDataStoreCreateResult _oneTableOnlyResult = null!;
        private DmsDataStoreCreateResult _sessionUnprovenResult = null!;
        private DmsDataStoreCreateResult _sessionProvenResult = null!;
        private DmsDataStoreCreateResult _clientLostResult = null!;

        private static Harness Unresolved(
            DmsServerSessionId? session,
            DmsIdentityRead identity,
            DmsEffectiveSchemaRead effectiveSchema,
            DmsSessionTermination? verdict = null
        )
        {
            Harness harness = Scripted();
            harness.Engine.OnRestoreIntoStaging = (_, _, _) =>
                Task.FromResult<DmsCommandOutcome<DmsInstanceIdentity>>(
                    // A null session here stands for the engine's own proof that the replay is quiescent.
                    new DmsCommandOutcome<DmsInstanceIdentity>.Unknown(session)
                    {
                        Quiescent = session is null,
                    }
                );
            harness.Engine.OnReadIdentity = (_, _) => Task.FromResult(identity);
            harness.Engine.OnReadTargetFacts = (_, _) =>
                Task.FromResult(
                    TargetFactsSample.PostgresqlFacts() with
                    {
                        EffectiveSchema = effectiveSchema,
                    }
                );
            if (verdict is not null)
            {
                harness.Engine.OnTerminateSession = (_, _, _) => Task.FromResult(verdict);
            }

            return harness;
        }

        [SetUp]
        public async Task Setup()
        {
            DmsIdentityRead.Invalid noIdentity = new(DmsIdentityReadProblem.TableMissing);
            DmsEffectiveSchemaRead.Invalid noSchema = new(DmsEffectiveSchemaReadProblem.TableMissing);

            _rolledBack = Unresolved(null, noIdentity, noSchema);
            _rolledBackResult = await CreateAsync(_rolledBack);

            _oneTableOnly = Unresolved(null, noIdentity, TargetFactsSample.EffectiveSchema());
            _oneTableOnlyResult = await CreateAsync(_oneTableOnly);

            _sessionUnproven = Unresolved(
                Session,
                TargetFactsSample.Identity(),
                TargetFactsSample.EffectiveSchema(),
                new DmsSessionTermination.NotTerminated(DmsSessionTerminationFailure.TimedOut)
            );
            _sessionUnprovenResult = await CreateAsync(_sessionUnproven);

            _sessionProven = Unresolved(
                Session,
                TargetFactsSample.Identity(),
                TargetFactsSample.EffectiveSchema(),
                new DmsSessionTermination.AlreadyAbsent()
            );
            _sessionProvenResult = await CreateAsync(_sessionProven);

            _faultHooks = new RecordingHooks();
            _faultHooks.Faults.Add(DmsProvisioningFaultPoint.DropConnectionDuringRestore);
            _clientLostTheResult = Scripted(_faultHooks);
            _clientLostResult = await CreateAsync(_clientLostTheResult);
        }

        [Test]
        public void It_reports_both_tables_missing_as_a_transient_restore_failure_and_drops()
        {
            DmsProvisioningFailure failure = FailureOf(_rolledBackResult);
            (failure.Code, failure.Category, failure.Diagnostic.Operation)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.RestoreFailed,
                        DmsProvisioningFailureCategory.Transient,
                        DmsDataStoreProvisioningService.ResolveRestoreStep
                    )
                );
            CountOf(_rolledBack, SpyDmsTargetEngine.DropStaging).Should().Be(1);
        }

        [Test]
        public void It_leaves_an_inconclusive_state_in_place_as_residue()
        {
            FailureOf(_oneTableOnlyResult).Code.Should().Be(DmsProvisioningFailureCode.OutcomeUnknown);
            CountOf(_oneTableOnly, SpyDmsTargetEngine.DropStaging).Should().Be(0);
            CleanupOf(_oneTableOnlyResult)
                .Residue.Should()
                .ContainSingle(residue => residue.Kind == DmsResidueKind.StagingDatabase);
        }

        [Test]
        public void It_leaves_an_unproven_session_in_place_without_reading_state()
        {
            FailureOf(_sessionUnprovenResult).Code.Should().Be(DmsProvisioningFailureCode.OutcomeUnknown);
            Members(_sessionUnproven)
                .Should()
                .NotContain(SpyDmsTargetEngine.OpenSession)
                .And.NotContain(SpyDmsTargetEngine.DropStaging);
            CleanupOf(_sessionUnprovenResult)
                .Residue.Should()
                .ContainSingle(residue => residue.Kind == DmsResidueKind.StagingDatabase);
        }

        [Test]
        public void It_continues_once_the_session_is_proven_gone_and_the_replay_committed()
        {
            _sessionProvenResult.Should().BeOfType<DmsDataStoreCreateResult.Created>();
            CountOf(_sessionProven, SpyDmsTargetEngine.TerminateSession).Should().Be(2);
        }

        [Test]
        public void It_treats_a_lost_client_result_after_a_commit_as_committed()
        {
            _clientLostResult.Should().BeOfType<DmsDataStoreCreateResult.Created>();
            _faultHooks.Points.Should().Contain(DmsProvisioningHookPoint.BeforeRename);
        }
    }

    [TestFixture]
    public class Given_the_restore_call_ends_without_an_outcome
    {
        private Harness _throwing = null!;
        private Harness _cancelled = null!;
        private Harness _lost = null!;
        private DmsDataStoreCreateResult _throwingResult = null!;
        private Exception? _cancelledOutcome;
        private DmsDataStoreCreateResult _lostResult = null!;

        private static Harness Restoring(SpyDmsTargetEngine.RestoreScript restore)
        {
            Harness harness = Scripted();
            harness.Engine.OnRestoreIntoStaging = restore;
            return harness;
        }

        [SetUp]
        public async Task Setup()
        {
            _throwing = Restoring(
                (_, _, _) =>
                    throw new InvalidOperationException("The session lookup failed inside resolution.")
            );
            _throwingResult = await CreateAsync(_throwing);

            using CancellationTokenSource cancellation = new();
            _cancelled = Restoring(
                (_, _, _) =>
                {
                    cancellation.Cancel();
                    throw new OperationCanceledException(cancellation.Token);
                }
            );
            _cancelledOutcome = await Record.ExceptionAsync(() =>
                CreateAsync(_cancelled, cancellation.Token)
            );

            _lost = Restoring(
                (_, _, _) =>
                {
                    _lost.OnlyHolder.Lose();
                    throw new TestConnectionException();
                }
            );
            _lostResult = await CreateAsync(_lost);
        }

        private static bool LoggedTheStagingDatabaseAsUnresolved(Harness harness) =>
            harness.Logger.Entries.Any(entry =>
                entry.EventId == DmsDataStoreProvisioningService.StagingResidue
                && Equals(entry.Field("Reason"), "outcome unresolved")
            );

        [Test]
        public void It_classifies_each_ending_as_before()
        {
            FailureOf(_throwingResult).Code.Should().Be(DmsProvisioningFailureCode.Unknown);
            _cancelledOutcome.Should().BeAssignableTo<OperationCanceledException>();
            FailureOf(_lostResult).Code.Should().Be(DmsProvisioningFailureCode.LockLost);
        }

        [Test]
        public void It_never_drops_the_staging_database() =>
            new[] { _throwing, _cancelled, _lost }
                .Select(harness => CountOf(harness, SpyDmsTargetEngine.DropStaging))
                .Should()
                .AllBeEquivalentTo(0);

        [Test]
        public void It_reports_the_staging_database_as_residue() =>
            new[] { _throwingResult, _lostResult }
                .Should()
                .OnlyContain(result =>
                    CleanupOf(result).Residue.Any(residue => residue.Kind == DmsResidueKind.StagingDatabase)
                );

        [Test]
        public void It_logs_the_residue_on_every_ending_including_cancellation() =>
            new[] { _throwing, _cancelled, _lost }
                .Should()
                .OnlyContain(harness => LoggedTheStagingDatabaseAsUnresolved(harness));
    }

    [TestFixture]
    public class Given_the_engine_cannot_prove_the_replay_ended
    {
        private static readonly DmsCleanupResidue _process = new(
            DmsResidueKind.ChildProcess,
            "dms-provision:0123456789ab/4321"
        );

        private Harness _nothingNamed = null!;
        private Harness _processLeft = null!;
        private DmsDataStoreCreateResult _nothingNamedResult = null!;
        private DmsDataStoreCreateResult _processLeftResult = null!;

        private static Harness Returning(DmsCommandOutcome<DmsInstanceIdentity> outcome)
        {
            Harness harness = Scripted();
            harness.Engine.OnRestoreIntoStaging = (_, _, _) => Task.FromResult(outcome);
            return harness;
        }

        [SetUp]
        public async Task Setup()
        {
            _nothingNamed = Returning(new DmsCommandOutcome<DmsInstanceIdentity>.Unknown(null));
            _nothingNamedResult = await CreateAsync(_nothingNamed);
            _processLeft = Returning(
                new DmsCommandOutcome<DmsInstanceIdentity>.Unknown(null) { Residue = [_process] }
            );
            _processLeftResult = await CreateAsync(_processLeft);
        }

        [Test]
        public void It_reports_both_as_outcome_unknown() =>
            (FailureOf(_nothingNamedResult).Code, FailureOf(_processLeftResult).Code)
                .Should()
                .Be((DmsProvisioningFailureCode.OutcomeUnknown, DmsProvisioningFailureCode.OutcomeUnknown));

        [Test]
        public void It_neither_reads_the_state_nor_drops_the_staging_database() =>
            new[] { _nothingNamed, _processLeft }
                .Should()
                .OnlyContain(harness =>
                    !harness.Engine.Members.Contains(SpyDmsTargetEngine.OpenSession)
                    && !harness.Engine.Members.Contains(SpyDmsTargetEngine.DropStaging)
                    && !harness.Engine.Members.Contains(SpyDmsTargetEngine.TerminateSession)
                );

        [Test]
        public void It_reports_the_staging_database_as_residue() =>
            new[] { _nothingNamedResult, _processLeftResult }
                .Should()
                .OnlyContain(result =>
                    CleanupOf(result).Residue.Any(residue => residue.Kind == DmsResidueKind.StagingDatabase)
                );

        [Test]
        public void It_carries_the_process_the_engine_could_not_end() =>
            CleanupOf(_processLeftResult).Residue.Should().Contain(_process);
    }

    [TestFixture]
    public class Given_a_staging_create_whose_ownership_must_survive_the_exit
    {
        private readonly List<DmsInstanceIdentity> _droppedAfterLoss = [];
        private DmsInstanceIdentity.Postgresql? _created;
        private Harness _lostAfterSuccess = null!;
        private Harness _threwAfterCreating = null!;
        private Harness _unclaimedLeftover = null!;
        private Harness _failedWithResidue = null!;
        private DmsDataStoreCreateResult _lostAfterSuccessResult = null!;
        private DmsDataStoreCreateResult _threwAfterCreatingResult = null!;
        private DmsDataStoreCreateResult _unclaimedLeftoverResult = null!;
        private DmsDataStoreCreateResult _failedWithResidueResult = null!;

        private static bool IsStaging(DmsDatabaseName name) =>
            name.Value.StartsWith(DmsOperationId.StagingDatabaseNamePrefix, StringComparison.Ordinal);

        /// <summary>A create that throws after the server made a database, which carries the comment <paramref name="leftoverClaim"/> chooses.</summary>
        private static Harness ThrowingAfterCreate(Func<DmsProvisionClaim, string> leftoverClaim)
        {
            Harness harness = Scripted();
            DmsProvisionClaim? sent = null;
            harness.Engine.OnCreateStaging = (_, claim, _) =>
            {
                sent = claim;
                throw new InvalidOperationException(
                    "The driver failed after the server created the database."
                );
            };
            harness.Engine.OnInspectByName = (name, _) =>
                Task.FromResult<DmsLiveTargetState>(
                    sent is not null && IsStaging(name)
                        ? new DmsLiveTargetState.Present(
                            new DmsInstanceIdentity.Postgresql(7, leftoverClaim(sent)),
                            DmsDatabaseState.Online
                        )
                        : new DmsLiveTargetState.Absent()
                );
            return harness;
        }

        [SetUp]
        public async Task Setup()
        {
            _droppedAfterLoss.Clear();
            _lostAfterSuccess = Scripted();
            _lostAfterSuccess.Engine.OnCreateStaging = (_, claim, _) =>
            {
                _created = new DmsInstanceIdentity.Postgresql(4242, claim.Text);
                _lostAfterSuccess.OnlyHolder.Lose();
                return Task.FromResult<DmsCommandOutcome<DmsInstanceIdentity>>(
                    new DmsCommandOutcome<DmsInstanceIdentity>.Succeeded(_created)
                );
            };
            _lostAfterSuccess.Engine.OnDropStaging = (_, instance, _) =>
            {
                _droppedAfterLoss.Add(instance);
                return Task.FromResult<DmsCommandOutcome>(new DmsCommandOutcome.Succeeded());
            };
            _lostAfterSuccessResult = await CreateAsync(_lostAfterSuccess);

            _threwAfterCreating = ThrowingAfterCreate(claim => claim.Text);
            _threwAfterCreatingResult = await CreateAsync(_threwAfterCreating);

            _unclaimedLeftover = ThrowingAfterCreate(_ => string.Empty);
            _unclaimedLeftoverResult = await CreateAsync(_unclaimedLeftover);

            _failedWithResidue = Scripted();
            _failedWithResidue.Engine.OnCreateStaging = (stage, _, _) =>
                Task.FromResult<DmsCommandOutcome<DmsInstanceIdentity>>(
                    new DmsCommandOutcome<DmsInstanceIdentity>.Failed(
                        new DmsCommandFailure(
                            DmsCommandFailureClass.Rejected,
                            DmsProvisioningDiagnostic.ForOperation("CreateStaging.Claim")
                        )
                    )
                    {
                        Residue = [new DmsCleanupResidue(DmsResidueKind.StagingDatabase, stage.Value)],
                    }
                );
            _failedWithResidueResult = await CreateAsync(_failedWithResidue);
        }

        private static bool HasStagingResidue(DmsDataStoreCreateResult result) =>
            CleanupOf(result).Residue.Any(residue => residue.Kind == DmsResidueKind.StagingDatabase);

        [Test]
        public void It_drops_a_created_database_whose_result_the_loss_discarded()
        {
            FailureOf(_lostAfterSuccessResult).Code.Should().Be(DmsProvisioningFailureCode.LockLost);
            _droppedAfterLoss.Should().Equal(_created!);
            HasStagingResidue(_lostAfterSuccessResult).Should().BeFalse();
        }

        [Test]
        public void It_drops_a_claimed_database_a_failed_create_left_behind()
        {
            FailureOf(_threwAfterCreatingResult).Code.Should().Be(DmsProvisioningFailureCode.Unknown);
            CountOf(_threwAfterCreating, SpyDmsTargetEngine.DropStaging).Should().Be(1);
            HasStagingResidue(_threwAfterCreatingResult).Should().BeFalse();
        }

        [Test]
        public void It_reports_an_unclaimed_database_instead_of_dropping_it()
        {
            CountOf(_unclaimedLeftover, SpyDmsTargetEngine.DropStaging).Should().Be(0);
            HasStagingResidue(_unclaimedLeftoverResult).Should().BeTrue();
        }

        [Test]
        public void It_reports_what_the_engine_could_not_drop_without_inspecting_again()
        {
            FailureOf(_failedWithResidueResult).Code.Should().Be(DmsProvisioningFailureCode.ServerRejected);
            HasStagingResidue(_failedWithResidueResult).Should().BeTrue();
            Members(_failedWithResidue)
                .SkipWhile(member => member != SpyDmsTargetEngine.CreateStaging)
                .Should()
                .NotContain([SpyDmsTargetEngine.Inspect, SpyDmsTargetEngine.DropStaging]);
        }
    }

    [TestFixture]
    public class Given_a_staging_create_that_throws_while_the_name_is_still_empty
    {
        private readonly List<DmsServerSessionId> _terminated = [];
        private Harness _unproven = null!;
        private Harness _proven = null!;
        private DmsDataStoreCreateResult _unprovenResult = null!;
        private DmsDataStoreCreateResult _provenResult = null!;

        private Harness Throwing(DmsSessionTermination verdict)
        {
            Harness harness = Scripted();
            harness.Engine.OnCreateStaging = (_, _, _) =>
                throw new InvalidOperationException("The create failed with no outcome.");
            harness.Engine.OnTerminateSession = (session, _, _) =>
            {
                _terminated.Add(session);
                return Task.FromResult(verdict);
            };
            return harness;
        }

        [SetUp]
        public async Task Setup()
        {
            _terminated.Clear();
            _unproven = Throwing(
                new DmsSessionTermination.NotTerminated(DmsSessionTerminationFailure.TimedOut)
            );
            _unprovenResult = await CreateAsync(_unproven);
            _proven = Throwing(new DmsSessionTermination.AlreadyAbsent());
            _provenResult = await CreateAsync(_proven);
        }

        private static bool HasStagingResidue(DmsDataStoreCreateResult result) =>
            CleanupOf(result).Residue.Any(residue => residue.Kind == DmsResidueKind.StagingDatabase);

        [Test]
        public void It_tries_to_prove_the_holder_session_gone() =>
            _terminated.Should().Equal(Session, Session);

        [Test]
        public void It_reports_the_empty_name_as_residue_while_the_create_may_still_commit() =>
            HasStagingResidue(_unprovenResult).Should().BeTrue();

        [Test]
        public void It_reports_nothing_once_the_holder_session_is_proven_gone() =>
            HasStagingResidue(_provenResult).Should().BeFalse();

        [Test]
        public void It_drops_nothing_either_way() =>
            (
                CountOf(_unproven, SpyDmsTargetEngine.DropStaging),
                CountOf(_proven, SpyDmsTargetEngine.DropStaging)
            )
                .Should()
                .Be((0, 0));
    }

    [TestFixture]
    public class Given_the_recovery_of_an_unknown_restore_is_not_proven
    {
        private readonly List<bool> _recoveryTokenCancelled = [];
        private Harness _throwing = null!;
        private Harness _cancelled = null!;
        private Harness _lost = null!;
        private Harness _lostWithTheResult = null!;
        private Harness _lostThenProven = null!;
        private DmsDataStoreCreateResult _throwingResult = null!;
        private DmsDataStoreCreateResult _cancelledResult = null!;
        private DmsDataStoreCreateResult _lostResult = null!;
        private DmsDataStoreCreateResult _lostWithTheResultResult = null!;
        private DmsDataStoreCreateResult _lostThenProvenResult = null!;

        private static Harness InDoubt(SpyDmsTargetEngine.TerminateScript terminate)
        {
            Harness harness = Scripted();
            harness.Engine.OnRestoreIntoStaging = (_, _, _) =>
                Task.FromResult<DmsCommandOutcome<DmsInstanceIdentity>>(
                    new DmsCommandOutcome<DmsInstanceIdentity>.Unknown(Session)
                );
            harness.Engine.OnTerminateSession = terminate;
            return harness;
        }

        [SetUp]
        public async Task Setup()
        {
            _recoveryTokenCancelled.Clear();

            _throwing = InDoubt((_, _, _) => throw new InvalidOperationException("The termination failed."));
            _throwingResult = await CreateAsync(_throwing);

            using CancellationTokenSource cancellation = new();
            _cancelled = InDoubt(
                (_, _, token) =>
                {
                    cancellation.Cancel();
                    _recoveryTokenCancelled.Add(token.IsCancellationRequested);
                    throw new OperationCanceledException();
                }
            );
            _cancelledResult = await CreateAsync(_cancelled, cancellation.Token);

            _lost = InDoubt(
                (_, _, token) =>
                {
                    _lost.OnlyHolder.Lose();
                    _recoveryTokenCancelled.Add(token.IsCancellationRequested);
                    throw new TestConnectionException();
                }
            );
            _lostResult = await CreateAsync(_lost);

            // Loss discards the unknown result itself: the observation must already have withheld the drop.
            _lostWithTheResult = Scripted();
            _lostWithTheResult.Engine.OnRestoreIntoStaging = (_, _, _) =>
            {
                _lostWithTheResult.OnlyHolder.Lose();
                return Task.FromResult<DmsCommandOutcome<DmsInstanceIdentity>>(
                    new DmsCommandOutcome<DmsInstanceIdentity>.Unknown(Session)
                );
            };
            _lostWithTheResultResult = await CreateAsync(_lostWithTheResult);

            // The control: the recovery proves the session gone although the holder is lost meanwhile.
            _lostThenProven = InDoubt(
                (_, _, _) =>
                {
                    _lostThenProven.OnlyHolder.Lose();
                    return Task.FromResult<DmsSessionTermination>(new DmsSessionTermination.Terminated());
                }
            );
            _lostThenProvenResult = await CreateAsync(_lostThenProven);
        }

        [Test]
        public void It_reports_an_unproven_recovery_as_outcome_unknown() =>
            new[] { _throwingResult, _cancelledResult, _lostResult }
                .Select(result => FailureOf(result).Code)
                .Should()
                .AllBeEquivalentTo(DmsProvisioningFailureCode.OutcomeUnknown);

        [Test]
        public void It_ends_a_result_discarded_by_loss_as_lock_lost() =>
            FailureOf(_lostWithTheResultResult).Code.Should().Be(DmsProvisioningFailureCode.LockLost);

        [Test]
        public void It_never_drops_the_staging_database_without_proof() =>
            new[] { _throwing, _cancelled, _lost, _lostWithTheResult }
                .Select(harness => CountOf(harness, SpyDmsTargetEngine.DropStaging))
                .Should()
                .AllBeEquivalentTo(0);

        [Test]
        public void It_reports_the_staging_database_as_residue() =>
            new[] { _throwingResult, _cancelledResult, _lostResult, _lostWithTheResultResult }
                .Should()
                .OnlyContain(result =>
                    CleanupOf(result).Residue.Any(residue => residue.Kind == DmsResidueKind.StagingDatabase)
                );

        [Test]
        public void It_runs_the_recovery_on_a_token_neither_the_caller_nor_the_loss_cancels() =>
            _recoveryTokenCancelled.Should().Equal(false, false);

        [Test]
        public void It_drops_once_the_session_is_proven_gone_even_after_the_loss()
        {
            FailureOf(_lostThenProvenResult).Code.Should().Be(DmsProvisioningFailureCode.LockLost);
            CountOf(_lostThenProven, SpyDmsTargetEngine.DropStaging).Should().Be(1);
            CleanupOf(_lostThenProvenResult)
                .Residue.Should()
                .NotContain(residue => residue.Kind == DmsResidueKind.StagingDatabase);
        }
    }

    [TestFixture]
    public class Given_the_password_file_cannot_be_deleted
    {
        private readonly List<CredentialTrap> _traps = [];
        private DmsDataStoreCreateResult _failedResult = null!;
        private Exception? _cancelledOutcome;
        private string _failedOperation = string.Empty;
        private string _cancelledOperation = string.Empty;

        /// <summary>
        /// A replay that leaves its password file behind, undeletable, then ends with <paramref name="outcome"/>;
        /// the release retries on the system clock (100 ms, 500 ms) before it reports.
        /// </summary>
        private Harness LeavingTheCredential(
            Func<DmsCommandOutcome<DmsInstanceIdentity>> outcome,
            Action? whileRestoring = null
        )
        {
            Harness harness = Scripted(configure: options => options.CleanupAttempts = 3, systemClock: true);
            harness.Engine.OnRestoreIntoStaging = (_, artifact, _) =>
            {
                _traps.Add(new CredentialTrap(Path.GetDirectoryName(artifact.FilePath)!));
                whileRestoring?.Invoke();
                return Task.FromResult(outcome());
            };
            return harness;
        }

        private static string OperationOf(Harness harness) =>
            harness
                .Engine.Calls.Single(call => call.Member == SpyDmsTargetEngine.CreateStaging)
                .DatabaseName![DmsOperationId.StagingDatabaseNamePrefix.Length..];

        private static IEnumerable<LogEntry> CriticalCredentialEntriesFor(string operation) =>
            SharedTemplateCatalog.Workspace.Logger.Entries.Where(entry =>
                entry.EventId == TemplateArtifactCatalog.CredentialResidue
                && entry.Level == Microsoft.Extensions.Logging.LogLevel.Critical
                && Equals(entry.Field("OperationId"), operation)
            );

        [SetUp]
        public async Task Setup()
        {
            Harness failed = LeavingTheCredential(() =>
                RestoreFailed(DmsCommandFailureClass.Rejected, DmsEngineOperations.RestoreRolledBack)
            );
            _failedResult = await CreateAsync(failed);
            _failedOperation = OperationOf(failed);

            using CancellationTokenSource cancellation = new();
            Harness cancelled = LeavingTheCredential(
                () => RestoreFailed(DmsCommandFailureClass.Rejected, DmsEngineOperations.RestoreCancelled),
                cancellation.Cancel
            );
            _cancelledOutcome = await Record.ExceptionAsync(() => CreateAsync(cancelled, cancellation.Token));
            _cancelledOperation = OperationOf(cancelled);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (CredentialTrap trap in _traps)
            {
                trap.Dispose();
            }

            _traps.Clear();
        }

        [Test]
        public void It_reports_the_password_file_as_credential_residue() =>
            CleanupOf(_failedResult)
                .Residue.Should()
                .Contain(
                    new DmsCleanupResidue(
                        DmsResidueKind.CredentialFile,
                        PrivateOperationDirectory.NamePrefix
                            + _failedOperation
                            + "/"
                            + PrivateOperationDirectory.CredentialFileName
                    )
                );

        [Test]
        public void It_logs_the_credential_once_at_critical() =>
            CriticalCredentialEntriesFor(_failedOperation).Should().ContainSingle();

        [Test]
        public void It_still_propagates_a_cancellation() =>
            _cancelledOutcome.Should().BeAssignableTo<OperationCanceledException>();

        [Test]
        public void It_logs_the_credential_at_critical_on_the_cancellation_path_too() =>
            CriticalCredentialEntriesFor(_cancelledOperation).Should().ContainSingle();
    }

    [TestFixture]
    public class Given_the_staging_database_fails_validation
    {
        private Harness _drifted = null!;
        private Harness _expectedIdentity = null!;
        private DmsDataStoreCreateResult _driftedResult = null!;
        private DmsDataStoreCreateResult _expectedIdentityResult = null!;

        [SetUp]
        public async Task Setup()
        {
            // An extra schema changes the inventory, which the validator judges before the content profile.
            _drifted = Scripted();
            _drifted.Engine.OnReadTargetFacts = (_, _) =>
                Task.FromResult(TargetFactsSample.PostgresqlFacts().WithSchemas("dmscs"));
            _driftedResult = await CreateAsync(_drifted);

            Guid expected = Guid.NewGuid();
            _expectedIdentity = Scripted();
            _expectedIdentity.Engine.OnReadIdentity = (_, _) =>
                Task.FromResult<DmsIdentityRead>(new DmsIdentityRead.Found(expected));
            _expectedIdentityResult = await _expectedIdentity.Service.CreateAsync(
                CreateRequest(expectedSourceIdentity: expected),
                CancellationToken.None
            );
        }

        [Test]
        public void It_names_the_failed_check()
        {
            DmsProvisioningFailure failure = FailureOf(_driftedResult);
            (failure.Code, failure.Diagnostic.Operation)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.TargetIncompatible,
                        DmsDataStoreProvisioningService.ValidateStagingStep
                            + "."
                            + DmsTargetValidator.InventoryHashCheck
                    )
                );
        }

        [Test]
        public void It_refuses_a_package_already_carrying_the_expected_identity() =>
            FailureOf(_expectedIdentityResult)
                .Code.Should()
                .Be(DmsProvisioningFailureCode.PackageIdentityInvalid);

        [Test]
        public void It_drops_the_staging_database_and_never_reaches_the_target() =>
            (
                CountOf(_drifted, SpyDmsTargetEngine.DropStaging),
                CountOf(_expectedIdentity, SpyDmsTargetEngine.DropStaging),
                TargetMutations(_drifted).Count,
                TargetMutations(_expectedIdentity).Count
            )
                .Should()
                .Be((1, 1, 0, 0));
    }

    [TestFixture]
    public class Given_the_staging_session_cannot_be_proven_gone
    {
        private Harness _harness = null!;
        private DmsDataStoreCreateResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = Scripted();
            _harness.Engine.OnTerminateSession = (_, _, _) =>
                Task.FromResult<DmsSessionTermination>(
                    new DmsSessionTermination.NotTerminated(DmsSessionTerminationFailure.TimedOut)
                );
            _result = await CreateAsync(_harness);
        }

        [Test]
        public void It_reports_transient_exclusive_access_not_established()
        {
            DmsProvisioningFailure failure = FailureOf(_result);
            (failure.Code, failure.Category)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.ExclusiveAccessNotEstablished,
                        DmsProvisioningFailureCategory.Transient
                    )
                );
        }

        [Test]
        public void It_still_drops_the_staging_database() =>
            CountOf(_harness, SpyDmsTargetEngine.DropStaging).Should().Be(1);
    }

    [TestFixture]
    public class Given_the_restore_returns_another_instance_under_the_staging_name
    {
        private Harness _harness = null!;
        private DmsDataStoreCreateResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = Scripted();
            _harness.Engine.OnRestoreIntoStaging = (_, _, _) =>
                Task.FromResult<DmsCommandOutcome<DmsInstanceIdentity>>(
                    new DmsCommandOutcome<DmsInstanceIdentity>.Succeeded(
                        new DmsInstanceIdentity.Postgresql(_stagingInstance.Oid + 1, _stagingInstance.Claim)
                    )
                );
            _result = await CreateAsync(_harness);
        }

        [Test]
        public void It_reports_the_instance_changed_without_validating()
        {
            DmsProvisioningFailure failure = FailureOf(_result);
            (failure.Code, failure.Diagnostic.Operation)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.InstanceChanged,
                        DmsDataStoreProvisioningService.RestoreIntoStagingStep
                    )
                );
            Members(_harness).Should().NotContain(SpyDmsTargetEngine.OpenSession);
        }

        [Test]
        public void It_still_drops_its_own_staging_instance() =>
            CountOf(_harness, SpyDmsTargetEngine.DropStaging).Should().Be(1);
    }

    [TestFixture]
    public class Given_the_target_appears_before_publication
    {
        private Harness _harness = null!;
        private Exception? _outcome;

        [SetUp]
        public async Task Setup()
        {
            int targetInspections = 0;
            _harness = Scripted();
            _harness.Engine.OnInspectByName = (name, _) =>
                Task.FromResult<DmsLiveTargetState>(
                    name.Value == CreateRequest().TargetName.Value && ++targetInspections == 2
                        ? new DmsLiveTargetState.Present(
                            new DmsInstanceIdentity.Postgresql(1, ""),
                            DmsDatabaseState.Online
                        )
                        : new DmsLiveTargetState.Absent()
                );
            _outcome = await Record.ExceptionAsync(() => CreateAsync(_harness));
        }

        [Test]
        public void It_hands_over_to_reconcile_instead_of_publishing() =>
            _outcome.Should().BeOfType<NotImplementedException>().Which.Message.Should().Contain("Step 3.7");

        [Test]
        public void It_drops_the_staging_database() =>
            CountOf(_harness, SpyDmsTargetEngine.DropStaging).Should().Be(1);
    }

    [TestFixture]
    public class Given_the_staging_drop_fails
    {
        private Harness _harness = null!;
        private DmsDataStoreCreateResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            // The backoff between attempts elapses on the system clock (0.6 s in all); nothing is timed.
            _harness = Scripted(configure: options => options.CleanupAttempts = 3, systemClock: true);
            _harness.Engine.OnReadTargetFacts = (_, _) =>
                Task.FromResult(TargetFactsSample.PostgresqlFacts().WithSchemas("dmscs"));
            _harness.Engine.OnDropStaging = (_, _, _) =>
                Task.FromResult<DmsCommandOutcome>(
                    new DmsCommandOutcome.Failed(
                        new DmsCommandFailure(
                            DmsCommandFailureClass.Rejected,
                            DmsProvisioningDiagnostic.ForOperation("DropStaging")
                        )
                    )
                );
            _result = await CreateAsync(_harness);
        }

        [Test]
        public void It_retries_the_drop_for_every_cleanup_attempt() =>
            CountOf(_harness, SpyDmsTargetEngine.DropStaging).Should().Be(3);

        [Test]
        public void It_keeps_the_outcome_class_and_reports_the_staging_database_as_residue()
        {
            FailureOf(_result).Code.Should().Be(DmsProvisioningFailureCode.TargetIncompatible);
            CleanupOf(_result)
                .Residue.Should()
                .ContainSingle(residue => residue.Kind == DmsResidueKind.StagingDatabase)
                .Which.ResourceId.Should()
                .StartWith(DmsOperationId.StagingDatabaseNamePrefix);
        }
    }

    [TestFixture]
    public class Given_the_holder_is_lost_during_the_restore
    {
        private Harness _harness = null!;
        private DmsDataStoreCreateResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = Scripted();
            _harness.Engine.OnRestoreIntoStaging = (_, _, _) =>
            {
                _harness.OnlyHolder.Lose();
                return Task.FromResult(Staged());
            };
            _result = await CreateAsync(_harness);
        }

        [Test]
        public void It_ends_as_transient_lock_lost_at_the_restore()
        {
            DmsProvisioningFailure failure = FailureOf(_result);
            (failure.Code, failure.Category, failure.Diagnostic.Operation)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.LockLost,
                        DmsProvisioningFailureCategory.Transient,
                        DmsDataStoreProvisioningService.RestoreIntoStagingStep
                    )
                );
        }

        [Test]
        public void It_still_drops_the_staging_database_after_the_loss() =>
            (CountOf(_harness, SpyDmsTargetEngine.DropStaging), Members(_harness)[^1])
                .Should()
                .Be((1, SpyDmsTargetEngine.DropStaging));
    }
}
