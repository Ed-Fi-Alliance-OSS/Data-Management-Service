// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Catalog;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Manifest;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Packaging;
using EdFi.DmsConfigurationService.Backend.Mssql.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Packaging;
using EdFi.DmsConfigurationService.Backend.Tests.Unit.Jobs;
using FluentAssertions;
using Npgsql;
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
        bool systemClock = false,
        ITemplateArtifactCatalog? catalog = null,
        IDmsDataStoreProvisionerReadiness? readiness = null
    )
    {
        Harness harness = new(
            hooks: hooks,
            configure: configure,
            systemClock: systemClock,
            catalog: catalog,
            readinessSource: readiness
        );
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
                    DmsProvisioningHookPoint.BeforeMarker,
                    DmsProvisioningHookPoint.BeforeMarkerDispatch
                );

        [Test]
        public void It_releases_the_holder_and_the_artifact() =>
            (_harness.OnlyHolder.ReleaseCount, _directoryReleased).Should().Be((1, true));
    }

    /// <summary>
    /// V4-05 end to end over real catalogs whose authenticated manifests name the two engine versions the operation
    /// precheck and target validation once read differently, on a major-18 server: <c>+18.1</c> is refused by the
    /// precheck before any staging database exists, and <c>18beta1</c> passes the precheck and the full validation of
    /// the restored database and is created.
    /// </summary>
    [TestFixture]
    public class Given_manifest_engine_versions_the_precheck_and_the_validation_once_read_differently
    {
        private SharedCatalogBuild _signed = null!;
        private SharedCatalogBuild _suffixed = null!;
        private Harness _signedHarness = null!;
        private Harness _suffixedHarness = null!;
        private DmsDataStoreCreateResult _signedResult = null!;
        private DmsDataStoreCreateResult _suffixedResult = null!;

        [OneTimeSetUp]
        public async Task Setup()
        {
            _signed = SharedTemplateCatalog.BuildIsolated(engineVersion: "+18.1");
            _signedHarness = Scripted(catalog: _signed.Catalog);
            _signedResult = await _signedHarness.Service.CreateAsync(
                CreateRequest(pin: _signed.Pin),
                CancellationToken.None
            );

            _suffixed = SharedTemplateCatalog.BuildIsolated(engineVersion: "18beta1");
            _suffixedHarness = Scripted(catalog: _suffixed.Catalog);
            _suffixedResult = await _suffixedHarness.Service.CreateAsync(
                CreateRequest(pin: _suffixed.Pin),
                CancellationToken.None
            );
        }

        [OneTimeTearDown]
        public void TearDown()
        {
            _signed?.Workspace.Dispose();
            _suffixed?.Workspace.Dispose();
        }

        [Test]
        public void It_refuses_a_signed_major_at_the_precheck() =>
            (FailureOf(_signedResult).Code, FailureOf(_signedResult).Diagnostic.Operation)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.EngineIncompatible,
                        DmsDataStoreProvisioningService.EngineCheckStep
                    )
                );

        [Test]
        public void It_refuses_the_signed_major_before_any_staging_database_exists() =>
            Members(_signedHarness)
                .Should()
                .NotContain([SpyDmsTargetEngine.CreateStaging, SpyDmsTargetEngine.RestoreIntoStaging]);

        [Test]
        public void It_creates_through_a_suffixed_major_both_readings_accept() =>
            _suffixedResult.Should().BeOfType<DmsDataStoreCreateResult.Created>();
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

    /// <summary>
    /// H-02: a staging create the server definitely rejected changed nothing, and whether a retry can succeed is the
    /// provider's separate verdict (<see cref="DmsCommandFailure.Retryable"/>). A retryable rejection is a transient
    /// <c>ServerRejected</c>; any other rejection stays permanent, and retryability never changes the code chosen for a
    /// missing privilege. Nothing is restored, renamed, or dropped, and the cleanup report is empty.
    /// </summary>
    [TestFixture]
    public class Given_the_staging_create_is_rejected_with_or_without_retryability
    {
        private static readonly (DmsCommandFailureClass Class, string Code, bool Retryable)[] _rejections =
        [
            (DmsCommandFailureClass.Rejected, "40P01", true),
            (DmsCommandFailureClass.Rejected, "53300", true),
            (DmsCommandFailureClass.Rejected, "1205", true),
            (DmsCommandFailureClass.Rejected, "22023", false),
            (DmsCommandFailureClass.PrivilegeMissing, "42501", false),
            (DmsCommandFailureClass.PrivilegeMissing, "42501", true),
        ];

        private readonly List<(Harness Harness, DmsDataStoreCreateResult Result)> _runs = [];

        [SetUp]
        public async Task Setup()
        {
            _runs.Clear();
            foreach ((DmsCommandFailureClass failureClass, string code, bool retryable) in _rejections)
            {
                Harness harness = Scripted();
                harness.Engine.OnCreateStaging = (_, _, _) =>
                    Task.FromResult<DmsCommandOutcome<DmsInstanceIdentity>>(
                        new DmsCommandOutcome<DmsInstanceIdentity>.Failed(
                            new DmsCommandFailure(
                                failureClass,
                                new DmsProvisioningDiagnostic(
                                    "Npgsql.PostgresException",
                                    code,
                                    "CreateStaging"
                                )
                            )
                            {
                                Retryable = retryable,
                            }
                        )
                    );
                _runs.Add((harness, await CreateAsync(harness)));
            }
        }

        [Test]
        public void It_is_transient_only_for_a_retryable_server_rejection() =>
            _runs
                .Select(run => FailureOf(run.Result))
                .Select(failure => (failure.Code, failure.Category, failure.Diagnostic.ProviderErrorCode))
                .Should()
                .Equal(
                    (
                        DmsProvisioningFailureCode.ServerRejected,
                        DmsProvisioningFailureCategory.Transient,
                        "40P01"
                    ),
                    (
                        DmsProvisioningFailureCode.ServerRejected,
                        DmsProvisioningFailureCategory.Transient,
                        "53300"
                    ),
                    (
                        DmsProvisioningFailureCode.ServerRejected,
                        DmsProvisioningFailureCategory.Transient,
                        "1205"
                    ),
                    (
                        DmsProvisioningFailureCode.ServerRejected,
                        DmsProvisioningFailureCategory.Permanent,
                        "22023"
                    ),
                    (
                        DmsProvisioningFailureCode.AdminPrivilegeMissing,
                        DmsProvisioningFailureCategory.Permanent,
                        "42501"
                    ),
                    (
                        DmsProvisioningFailureCode.AdminPrivilegeMissing,
                        DmsProvisioningFailureCategory.Permanent,
                        "42501"
                    )
                );

        [Test]
        public void It_restores_renames_and_drops_nothing_and_reports_no_residue() =>
            _runs
                .Should()
                .OnlyContain(run =>
                    !run.Harness.Engine.Members.Contains(SpyDmsTargetEngine.RestoreIntoStaging)
                    && !run.Harness.Engine.Members.Contains(SpyDmsTargetEngine.RenameIntoTarget)
                    && !run.Harness.Engine.Members.Contains(SpyDmsTargetEngine.DropStaging)
                    && !((DmsDataStoreCreateResult.Failure)run.Result).Cleanup.HasResidue
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
            new(
                "gssapi authentication to the staging database",
                RestoreFailed(
                    DmsCommandFailureClass.Rejected,
                    PostgresqlDmsTargetEngine.RestoreGssAuthenticationStep
                ),
                DmsProvisioningFailureCode.RestoreFailed,
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

    /// <summary>
    /// AC 11: <c>psql</c> exit 3 with a SQLSTATE is a definite rollback, and whether a retry can succeed is a
    /// separate question. The PostgreSQL engine's own classification of that exit feeds the real orchestrator: a
    /// serialization failure, a deadlock, a lock not available, or temporary resource exhaustion ends as a
    /// transient <c>RestoreFailed</c>; any other server rejection stays permanent. Either way the staging
    /// database is dropped without validation, as for every definite restore failure.
    /// </summary>
    [TestFixture("40001", DmsProvisioningFailureCategory.Transient)]
    [TestFixture("40P01", DmsProvisioningFailureCategory.Transient)]
    [TestFixture("55P03", DmsProvisioningFailureCategory.Transient)]
    [TestFixture("53100", DmsProvisioningFailureCategory.Transient)]
    [TestFixture("53200", DmsProvisioningFailureCategory.Transient)]
    [TestFixture("42710", DmsProvisioningFailureCategory.Permanent)]
    [TestFixture("22012", DmsProvisioningFailureCategory.Permanent)]
    public class Given_psql_stopped_the_replay_on_a_server_rejection(
        string sqlState,
        DmsProvisioningFailureCategory category
    )
    {
        private Harness _harness = null!;
        private DmsDataStoreCreateResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            DmsCommandOutcome<DmsInstanceIdentity> stopped = PostgresqlDmsTargetEngine.ScriptStopped(
                PostgresqlErrorClassifier.FailureClassOf(sqlState)
                    ?? throw new InvalidOperationException("The SQLSTATE must be a definite failure."),
                sqlState
            );
            _harness = Scripted();
            _harness.Engine.OnRestoreIntoStaging = (_, _, _) => Task.FromResult(stopped);
            _result = await CreateAsync(_harness);
        }

        [Test]
        public void It_reports_restore_failed_in_the_category_the_sqlstate_allows() =>
            (FailureOf(_result).Code, FailureOf(_result).Category)
                .Should()
                .Be((DmsProvisioningFailureCode.RestoreFailed, category));

        [Test]
        public void It_keeps_the_sqlstate_in_the_diagnostic() =>
            FailureOf(_result).Diagnostic.ProviderErrorCode.Should().Be(sqlState);

        [Test]
        public void It_drops_the_staging_database_without_validating() =>
            (
                CountOf(_harness, SpyDmsTargetEngine.DropStaging),
                _harness.Engine.Members.Contains(SpyDmsTargetEngine.OpenSession)
            )
                .Should()
                .Be((1, false));
    }

    /// <summary>
    /// AC 11: the server refuses a session the PostgreSQL replay needs before <c>psql</c> starts. The engine's own
    /// classification feeds the real orchestrator: an access refusal (a per-database <c>pg_hba.conf</c> rule, a
    /// refused privilege) is a permanent <c>AdminPrivilegeMissing</c> with its SQLSTATE, while a server shortage
    /// stays a transient <c>RestoreFailed</c>. Either way nothing committed and the staging database is dropped
    /// without validation.
    /// </summary>
    [TestFixture(
        "28000",
        DmsProvisioningFailureCode.AdminPrivilegeMissing,
        DmsProvisioningFailureCategory.Permanent
    )]
    [TestFixture(
        "42501",
        DmsProvisioningFailureCode.AdminPrivilegeMissing,
        DmsProvisioningFailureCategory.Permanent
    )]
    [TestFixture("53300", DmsProvisioningFailureCode.RestoreFailed, DmsProvisioningFailureCategory.Transient)]
    public class Given_the_server_refuses_a_session_before_the_replay(
        string sqlState,
        DmsProvisioningFailureCode code,
        DmsProvisioningFailureCategory category
    )
    {
        private Harness _harness = null!;
        private DmsDataStoreCreateResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            DmsCommandOutcome<DmsInstanceIdentity> refused = PostgresqlDmsTargetEngine.PreReplayFailed(
                new PostgresException("refused", "FATAL", "FATAL", sqlState),
                CancellationToken.None
            );
            _harness = Scripted();
            _harness.Engine.OnRestoreIntoStaging = (_, _, _) => Task.FromResult(refused);
            _result = await CreateAsync(_harness);
        }

        [Test]
        public void It_reports_the_code_and_category_the_refusal_allows() =>
            (FailureOf(_result).Code, FailureOf(_result).Category).Should().Be((code, category));

        [Test]
        public void It_drops_the_staging_database_without_validating() =>
            (
                CountOf(_harness, SpyDmsTargetEngine.DropStaging),
                _harness.Engine.Members.Contains(SpyDmsTargetEngine.OpenSession)
            )
                .Should()
                .Be((1, false));
    }

    /// <summary>
    /// AC 11 (F1): the D-16 enqueue-owner check before the replay is rejected by the server. The engine's own
    /// classification (<see cref="PostgresqlStagingQueries.ClassifyEnqueueOwnerFailure"/>) feeds the real
    /// orchestrator: a rejection a retry can outlive (an out-of-memory or a deadlock) ends as a transient
    /// <c>RestoreFailed</c>; an unsafe role stays <c>RoleStateUnsafe</c>, a missing privilege stays a permanent
    /// <c>AdminPrivilegeMissing</c>, and any other rejection stays a permanent <c>RestoreFailed</c>. Cleanup is the
    /// same for every one: the staging database is dropped once, without validation, and no residue is reported.
    /// </summary>
    [TestFixture("53200", DmsProvisioningFailureCode.RestoreFailed, DmsProvisioningFailureCategory.Transient)]
    [TestFixture("40P01", DmsProvisioningFailureCode.RestoreFailed, DmsProvisioningFailureCategory.Transient)]
    [TestFixture(
        "55000",
        DmsProvisioningFailureCode.RoleStateUnsafe,
        DmsProvisioningFailureCategory.Permanent
    )]
    [TestFixture(
        "42501",
        DmsProvisioningFailureCode.AdminPrivilegeMissing,
        DmsProvisioningFailureCategory.Permanent
    )]
    [TestFixture("22023", DmsProvisioningFailureCode.RestoreFailed, DmsProvisioningFailureCategory.Permanent)]
    public class Given_the_enqueue_owner_check_is_rejected_before_the_replay(
        string sqlState,
        DmsProvisioningFailureCode code,
        DmsProvisioningFailureCategory category
    )
    {
        private Harness _harness = null!;
        private DmsDataStoreCreateResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            DmsCommandOutcome<DmsInstanceIdentity> refused =
                PostgresqlStagingQueries.ClassifyEnqueueOwnerFailure(
                    new PostgresException("rejected", "ERROR", "ERROR", sqlState),
                    new DmsServerSessionId(42, DateTimeOffset.UnixEpoch)
                );
            _harness = Scripted();
            _harness.Engine.OnRestoreIntoStaging = (_, _, _) => Task.FromResult(refused);
            _result = await CreateAsync(_harness);
        }

        [Test]
        public void It_reports_the_code_and_category_the_rejection_allows() =>
            (FailureOf(_result).Code, FailureOf(_result).Category).Should().Be((code, category));

        [Test]
        public void It_keeps_the_sqlstate_in_the_diagnostic() =>
            FailureOf(_result).Diagnostic.ProviderErrorCode.Should().Be(sqlState);

        [Test]
        public void It_drops_the_staging_database_without_validating_and_reports_no_residue() =>
            (
                CountOf(_harness, SpyDmsTargetEngine.DropStaging),
                _harness.Engine.Members.Contains(SpyDmsTargetEngine.OpenSession),
                CleanupOf(_result).HasResidue
            )
                .Should()
                .Be((1, false, false));
    }

    /// <summary>
    /// AC 11: SQL Server rejected the <c>RESTORE</c> in its own response, which proves nothing committed. The
    /// engine's own classification feeds the real orchestrator: a deadlock victim (<c>1205</c>) or a temporary
    /// memory shortage (<c>701</c>) is a transient <c>RestoreFailed</c>; any other rejection, such as a logical file
    /// the backup does not hold (<c>3234</c>) or a full filegroup (<c>1105</c>), stays permanent. Either way the
    /// staging database is dropped without validation.
    /// </summary>
    [TestFixture(1205, DmsProvisioningFailureCategory.Transient)]
    [TestFixture(701, DmsProvisioningFailureCategory.Transient)]
    [TestFixture(3234, DmsProvisioningFailureCategory.Permanent)]
    [TestFixture(1105, DmsProvisioningFailureCategory.Permanent)]
    public class Given_sql_server_rejected_the_restore_in_its_own_response(
        int number,
        DmsProvisioningFailureCategory category
    )
    {
        private Harness _harness = null!;
        private DmsDataStoreCreateResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            DmsCommandOutcome<DmsInstanceIdentity> rejected =
                new DmsCommandOutcome<DmsInstanceIdentity>.Failed(
                    new DmsCommandFailure(
                        MssqlErrorClassifier.FailureClassOf(number, 16)
                            ?? throw new InvalidOperationException("The number must be a definite failure."),
                        new DmsProvisioningDiagnostic(
                            "Microsoft.Data.SqlClient.SqlException",
                            number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            MssqlDmsTargetEngine.RejectedRestoreStep([
                                number,
                                MssqlErrorClassifier.RestoreTerminatedAbnormally,
                            ])
                        )
                    )
                );
            _harness = Scripted();
            _harness.Engine.OnRestoreIntoStaging = (_, _, _) => Task.FromResult(rejected);
            _result = await CreateAsync(_harness);
        }

        [Test]
        public void It_reports_restore_failed_in_the_category_the_error_allows() =>
            (FailureOf(_result).Code, FailureOf(_result).Category)
                .Should()
                .Be((DmsProvisioningFailureCode.RestoreFailed, category));

        [Test]
        public void It_drops_the_staging_database_without_validating() =>
            (
                CountOf(_harness, SpyDmsTargetEngine.DropStaging),
                _harness.Engine.Members.Contains(SpyDmsTargetEngine.OpenSession)
            )
                .Should()
                .Be((1, false));
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

    /// <summary>
    /// The PostgreSQL engine finishes claiming a created staging database after cancellation, so the create returns a
    /// confirmed success with the caller's cancellation already requested. The restore's scope then stops before
    /// dispatch, so nothing withholds the drop: the owned staging database is dropped, not left as residue.
    /// </summary>
    [TestFixture]
    public class Given_the_caller_cancels_as_the_staging_create_succeeds
    {
        private readonly List<DmsInstanceIdentity> _dropped = [];
        private Harness _harness = null!;
        private Exception? _outcome;

        [SetUp]
        public async Task Setup()
        {
            _dropped.Clear();
            using CancellationTokenSource cancellation = new();
            _harness = Scripted();
            _harness.Engine.OnCreateStaging = async (_, _, _) =>
            {
                await cancellation.CancelAsync();
                return Staged();
            };
            _harness.Engine.OnDropStaging = (_, instance, _) =>
            {
                _dropped.Add(instance);
                return Task.FromResult<DmsCommandOutcome>(new DmsCommandOutcome.Succeeded());
            };
            _outcome = await Record.ExceptionAsync(() => CreateAsync(_harness, cancellation.Token));
        }

        [Test]
        public void It_propagates_the_cancellation() =>
            _outcome.Should().BeAssignableTo<OperationCanceledException>();

        [Test]
        public void It_never_dispatches_the_restore() =>
            Members(_harness).Should().NotContain(SpyDmsTargetEngine.RestoreIntoStaging);

        [Test]
        public void It_drops_the_created_staging_database() => _dropped.Should().Equal(_stagingInstance);

        [Test]
        public void It_releases_the_holder() => _harness.OnlyHolder.ReleaseCount.Should().Be(1);
    }

    /// <summary>
    /// The caller cancels during the inspection of the staging name, which completes normally, so the scope stops
    /// the staging CREATE before dispatch. Recovery is unavailable (the holder session cannot be proven gone), so a
    /// create wrongly recorded as pending would leave cleanup reporting a staging database this operation never
    /// created. Nothing is pending: cleanup neither inspects the name again nor reports it.
    /// </summary>
    [TestFixture]
    public class Given_the_caller_cancels_before_the_staging_create_is_dispatched
    {
        private Harness _harness = null!;
        private Exception? _outcome;

        [SetUp]
        public async Task Setup()
        {
            using CancellationTokenSource cancellation = new();
            _harness = Scripted();
            _harness.Engine.OnInspectByName = async (name, _) =>
            {
                if (name.Value.StartsWith(DmsOperationId.StagingDatabaseNamePrefix, StringComparison.Ordinal))
                {
                    await cancellation.CancelAsync();
                }

                return new DmsLiveTargetState.Absent();
            };
            _harness.Engine.OnTerminateSession = (session, _, _) =>
                Task.FromResult<DmsSessionTermination>(
                    session == Session
                        ? new DmsSessionTermination.NotTerminated(DmsSessionTerminationFailure.TimedOut)
                        : new DmsSessionTermination.Terminated()
                );
            _outcome = await Record.ExceptionAsync(() => CreateAsync(_harness, cancellation.Token));
        }

        [Test]
        public void It_propagates_the_cancellation() =>
            _outcome.Should().BeAssignableTo<OperationCanceledException>();

        [Test]
        public void It_never_dispatches_the_create() =>
            Members(_harness).Should().NotContain(SpyDmsTargetEngine.CreateStaging);

        [Test]
        public void It_neither_recovers_nor_re_inspects_a_create_that_was_not_sent() =>
            (
                _harness.Engine.TerminatedSessions.Contains(Session),
                _harness.Engine.Calls.Count(call =>
                    call.Member == SpyDmsTargetEngine.Inspect
                    && call.DatabaseName is not null
                    && call.DatabaseName.StartsWith(
                        DmsOperationId.StagingDatabaseNamePrefix,
                        StringComparison.Ordinal
                    )
                )
            )
                .Should()
                .Be((false, 1));

        [Test]
        public void It_reports_no_staging_residue() =>
            _harness
                .Logger.Entries.Should()
                .NotContain(entry => entry.EventId == DmsDataStoreProvisioningService.StagingResidue);

        [Test]
        public void It_releases_the_holder() => _harness.OnlyHolder.ReleaseCount.Should().Be(1);
    }

    /// <summary>
    /// The staging CREATE passes the scope's checks and is handed to the engine, which runs it on the real holder's
    /// execution gate; the heartbeat probe holds the gate, then finds the lock gone, so the gate refuses the create
    /// before it is invoked. Recovery is unavailable (the holder session cannot be proven gone), so a create still
    /// recorded as pending would leave cleanup re-inspecting the name and reporting a staging database this
    /// operation never created. The refusal withdraws it: the loss is transient and nothing is reported.
    /// </summary>
    [TestFixture]
    public class Given_the_holder_is_lost_while_the_staging_create_waits_for_the_holder_gate
    {
        private Harness _harness = null!;
        private DmsDataStoreCreateResult _result = null!;
        private int _createsSent;

        [SetUp]
        public async Task Setup()
        {
            _harness = Scripted();
            TaskCompletionSource<bool> probe = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _harness.Engine.OnInspectByName = (name, _) =>
            {
                if (name.Value.StartsWith(DmsOperationId.StagingDatabaseNamePrefix, StringComparison.Ordinal))
                {
                    // The last read before the create: the heartbeat probe takes the gate and keeps it.
                    _harness.OnlyHolder.Probe = _ => probe.Task;
                    _harness.OnlyHolder.StartHeartbeat();
                    _harness.Time.Advance(TimeSpan.FromSeconds(5));
                }

                return Task.FromResult<DmsLiveTargetState>(new DmsLiveTargetState.Absent());
            };
            _harness.Engine.OnCreateStaging = (_, _, token) =>
            {
                waiting.SetResult();
                return _harness.OnlyHolder.RunAsync(
                    _ =>
                    {
                        _createsSent++;
                        return Task.FromResult(Staged());
                    },
                    CommandTimeout,
                    token
                );
            };
            _harness.Engine.OnTerminateSession = (session, _, _) =>
                Task.FromResult<DmsSessionTermination>(
                    session == Session
                        ? new DmsSessionTermination.NotTerminated(DmsSessionTerminationFailure.TimedOut)
                        : new DmsSessionTermination.Terminated()
                );
            Task<DmsDataStoreCreateResult> create = CreateAsync(_harness);
            await ReadStartedAsync(waiting, create);
            probe.SetResult(false);
            _result = await create;
        }

        [Test]
        public void It_ends_as_transient_lock_lost_at_the_create() =>
            (FailureOf(_result).Code, FailureOf(_result).Category, FailureOf(_result).Diagnostic.Operation)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.LockLost,
                        DmsProvisioningFailureCategory.Transient,
                        DmsDataStoreProvisioningService.CreateStagingStep
                    )
                );

        [Test]
        public void It_never_sends_the_create() => _createsSent.Should().Be(0);

        [Test]
        public void It_neither_recovers_nor_re_inspects_a_create_that_was_not_sent() =>
            (
                _harness.Engine.TerminatedSessions.Contains(Session),
                _harness.Engine.Calls.Count(call =>
                    call.Member == SpyDmsTargetEngine.Inspect
                    && call.DatabaseName is not null
                    && call.DatabaseName.StartsWith(
                        DmsOperationId.StagingDatabaseNamePrefix,
                        StringComparison.Ordinal
                    )
                )
            )
                .Should()
                .Be((false, 1));

        [Test]
        public void It_reports_no_staging_residue()
        {
            CleanupOf(_result).HasResidue.Should().BeFalse();
            _harness
                .Logger.Entries.Should()
                .NotContain(entry => entry.EventId == DmsDataStoreProvisioningService.StagingResidue);
        }

        [Test]
        public void It_releases_the_holder() => _harness.OnlyHolder.ReleaseCount.Should().Be(1);
    }

    /// <summary>
    /// The staging create's deadline starts before the engine waits for the holder gate. The heartbeat probe holds
    /// the gate for its whole bound (one <c>CommandTimeout</c>) when the create asks for it, and the
    /// <c>CREATE DATABASE</c> then runs under the PostgreSQL holder bound (one <c>CommandTimeout</c>) to its last
    /// second. The orchestrator's allowance covers the gate wait, so nothing cancels the create before the
    /// provider's bound; the create still ends at that bound.
    /// </summary>
    [TestFixture]
    public class Given_the_staging_create_waits_for_the_holder_gate_and_then_uses_its_whole_bound
    {
        private bool _cancelledWithinTheBound;
        private bool _cancelledPastTheBound;

        [SetUp]
        public async Task Setup()
        {
            Harness harness = Scripted();
            TaskCompletionSource<bool> probe = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource running = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken createToken = default;
            harness.Engine.OnInspectByName = (name, _) =>
            {
                if (name.Value.StartsWith(DmsOperationId.StagingDatabaseNamePrefix, StringComparison.Ordinal))
                {
                    // The last read before the create: the heartbeat probe takes the gate and keeps it.
                    harness.OnlyHolder.Probe = _ => probe.Task;
                    harness.OnlyHolder.StartHeartbeat();
                    harness.Time.Advance(TimeSpan.FromSeconds(5));
                }

                return Task.FromResult<DmsLiveTargetState>(new DmsLiveTargetState.Absent());
            };

            // Runs on the holder's gate under CommandTimeout, as PostgresqlDmsTargetEngine.CreateStagingAsync does.
            harness.Engine.OnCreateStaging = (_, _, token) =>
            {
                waiting.TrySetResult();
                return harness.OnlyHolder.RunAsync(
                    async holderToken =>
                    {
                        createToken = holderToken;
                        running.SetResult();
                        await finish.Task;
                        return Staged();
                    },
                    CommandTimeout,
                    token
                );
            };
            Task<DmsDataStoreCreateResult> create = CreateAsync(harness);
            await ReadStartedAsync(waiting, create);

            harness.Time.Advance(CommandTimeout);
            probe.SetResult(true);
            await ReadStartedAsync(running, create);

            harness.Time.Advance(CommandTimeout - TimeSpan.FromSeconds(1));
            _cancelledWithinTheBound = createToken.IsCancellationRequested;
            harness.Time.Advance(TimeSpan.FromSeconds(2));
            _cancelledPastTheBound = createToken.IsCancellationRequested;

            finish.SetResult();
            await create;
        }

        [Test]
        public void It_does_not_cancel_the_create_within_the_provider_bound() =>
            _cancelledWithinTheBound.Should().BeFalse();

        [Test]
        public void It_still_ends_the_create_at_the_provider_bound() =>
            _cancelledPastTheBound.Should().BeTrue();
    }

    /// <summary>
    /// Only the scope's deadline for the restore expires: the fake clock passes it while the engine holds the
    /// call, and the engine reports the cancellation of its token as a normal result, as both engines do. The
    /// caller never cancelled and the holder was not lost, so the create ends <c>TimedOut</c>, not with an
    /// <see cref="OperationCanceledException"/>.
    /// </summary>
    [TestFixture]
    public class Given_only_the_restore_deadline_expires_and_the_engine_reports_cancellation
    {
        private Harness _harness = null!;
        private DmsDataStoreCreateResult _result = null!;
        private bool _engineTokenCancelled;
        private bool _directoryReleased;

        [SetUp]
        public async Task Setup()
        {
            _harness = Scripted();
            _harness.Engine.OnRestoreIntoStaging = (_, _, token) =>
            {
                _harness.Time.Advance(
                    TimeSpan.FromMinutes(1)
                        + TimeSpan.FromSeconds(30)
                        + CommandTimeout
                        + TimeSpan.FromSeconds(1)
                );
                _engineTokenCancelled = token.IsCancellationRequested;
                return Task.FromResult(
                    RestoreFailed(DmsCommandFailureClass.Rejected, DmsEngineOperations.RestoreCancelled)
                );
            };
            _result = await CreateAsync(_harness);
            _directoryReleased = SharedTemplateCatalog.Workspace.OperationEntries.Length == 0;
        }

        [Test]
        public void It_cancels_the_engine_token_by_the_deadline_alone() =>
            (_engineTokenCancelled, _harness.OnlyHolder.LostToken.IsCancellationRequested)
                .Should()
                .Be((true, false));

        [Test]
        public void It_ends_timed_out_at_the_restore() =>
            FailureOf(_result)
                .Should()
                .BeEquivalentTo(
                    new
                    {
                        Code = DmsProvisioningFailureCode.TimedOut,
                        Diagnostic = new
                        {
                            Operation = DmsDataStoreProvisioningService.RestoreIntoStagingStep,
                        },
                    }
                );

        [Test]
        public void It_cleans_up() =>
            (
                CountOf(_harness, SpyDmsTargetEngine.DropStaging),
                _harness.OnlyHolder.ReleaseCount,
                _directoryReleased
            )
                .Should()
                .Be((1, 1, true));
    }

    /// <summary>
    /// The engine prepares (SQL Server stages, re-hashes and plans the backup) for longer than
    /// <c>SessionTerminationTimeout</c> plus <c>CommandTimeout</c>, then reports the restore starting and uses
    /// its whole allowance. The step deadline restarts at that report, so the preparation does not consume
    /// <c>RestoreTimeout</c>: nothing cancels the restore before the allowance has passed from its start, and
    /// the deadline still ends it, as <c>TimedOut</c>, once it has. The preparation's own bound, the deadline
    /// from the call, is <see cref="Given_only_the_restore_deadline_expires_and_the_engine_reports_cancellation"/>.
    /// </summary>
    [TestFixture]
    public class Given_the_engine_prepares_and_then_the_restore_uses_its_whole_allowance
    {
        private static readonly TimeSpan _preparation = TimeSpan.FromSeconds(90);
        private Harness _harness = null!;
        private DmsDataStoreCreateResult _result = null!;
        private bool _cancelledDuringPreparation;
        private bool _cancelledWithinTheAllowance;
        private bool _cancelledPastTheAllowance;

        [SetUp]
        public async Task Setup()
        {
            _harness = Scripted();
            TimeSpan allowance = TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(30) + CommandTimeout;
            TaskCompletionSource preparing = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource prepared = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken restoreToken = default;
            _harness.Engine.OnRestoreIntoStaging = async (_, _, token) =>
            {
                restoreToken = token;
                Action restoreStarting = _harness.Engine.RestoreStarting!;
                preparing.SetResult();
                await prepared.Task;
                restoreStarting();
                started.SetResult();
                await finish.Task;
                return RestoreFailed(DmsCommandFailureClass.Rejected, DmsEngineOperations.RestoreCancelled);
            };
            Task<DmsDataStoreCreateResult> create = CreateAsync(_harness);
            await ReadStartedAsync(preparing, create);

            _harness.Time.Advance(_preparation);
            _cancelledDuringPreparation = restoreToken.IsCancellationRequested;
            prepared.SetResult();
            await ReadStartedAsync(started, create);

            _harness.Time.Advance(allowance - TimeSpan.FromSeconds(1));
            _cancelledWithinTheAllowance = restoreToken.IsCancellationRequested;
            _harness.Time.Advance(TimeSpan.FromSeconds(2));
            _cancelledPastTheAllowance = restoreToken.IsCancellationRequested;

            finish.SetResult();
            _result = await create;
        }

        [Test]
        public void It_does_not_cancel_the_preparation_within_the_allowance() =>
            _cancelledDuringPreparation.Should().BeFalse();

        [Test]
        public void It_does_not_cancel_the_restore_within_the_allowance_from_its_start() =>
            _cancelledWithinTheAllowance.Should().BeFalse();

        [Test]
        public void It_still_ends_the_restore_once_the_allowance_has_passed() =>
            (_cancelledPastTheAllowance, _harness.OnlyHolder.LostToken.IsCancellationRequested)
                .Should()
                .Be((true, false));

        [Test]
        public void It_ends_timed_out_at_the_restore() =>
            FailureOf(_result)
                .Should()
                .BeEquivalentTo(
                    new
                    {
                        Code = DmsProvisioningFailureCode.TimedOut,
                        Diagnostic = new
                        {
                            Operation = DmsDataStoreProvisioningService.RestoreIntoStagingStep,
                        },
                    }
                );
    }

    /// <summary>
    /// The restarted deadline does not detach the restore from the caller: once the engine has reported the
    /// restore starting, the caller's cancellation still reaches the engine's token at once, and the create
    /// propagates it after cleanup.
    /// </summary>
    [TestFixture]
    public class Given_the_caller_cancels_after_the_restore_started
    {
        private Harness _harness = null!;
        private Exception? _outcome;
        private bool _cancelledBeforeTheCaller;
        private bool _cancelledByTheCaller;

        [SetUp]
        public async Task Setup()
        {
            using CancellationTokenSource cancellation = new();
            _harness = Scripted();
            TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken restoreToken = default;
            _harness.Engine.OnRestoreIntoStaging = async (_, _, token) =>
            {
                restoreToken = token;
                _harness.Engine.RestoreStarting!();
                started.SetResult();
                await finish.Task;
                return RestoreFailed(DmsCommandFailureClass.Rejected, DmsEngineOperations.RestoreCancelled);
            };
            Task<DmsDataStoreCreateResult> create = CreateAsync(_harness, cancellation.Token);
            await ReadStartedAsync(started, create);

            _cancelledBeforeTheCaller = restoreToken.IsCancellationRequested;
            await cancellation.CancelAsync();
            _cancelledByTheCaller = restoreToken.IsCancellationRequested;

            finish.SetResult();
            _outcome = await Record.ExceptionAsync(() => create);
        }

        [Test]
        public void It_cancels_the_restore_with_the_caller() =>
            (_cancelledBeforeTheCaller, _cancelledByTheCaller).Should().Be((false, true));

        [Test]
        public void It_propagates_the_cancellation() =>
            _outcome.Should().BeAssignableTo<OperationCanceledException>();
    }

    /// <summary>
    /// The artifact cannot be written into the operation directory (6.2 step 8) for a file system reason, not
    /// a package defect: the create returns <c>StagingFailed</c> with only the exception's type chain instead
    /// of throwing, before the lock and with nothing sent to the server. An entry already holding the
    /// artifact's name raises <see cref="IOException"/> and the directory is released; a directory that refuses
    /// new entries raises <see cref="UnauthorizedAccessException"/> (Unix), and the same refusal stops the
    /// release, whose residue the failure reports.
    /// </summary>
    [TestFixture]
    public class Given_the_artifact_cannot_be_written_into_the_operation_directory
    {
        private Harness _occupied = null!;
        private DmsDataStoreCreateResult _occupiedResult = null!;
        private bool _occupiedDirectoryReleased;
        private Harness _refusing = null!;
        private DmsDataStoreCreateResult? _refusingResult;
        private Catalog.DeletionTrap? _trap;
        private string _refusingDirectory = string.Empty;

        private static Harness Staging(Action<TrustedTemplateArtifact> afterStaging)
        {
            Harness harness = Scripted();
            harness.Catalog.OnStage = async (template, operation, token) =>
            {
                TemplateArtifactStaging staging = await SharedTemplateCatalog.Catalog.StageAsync(
                    template,
                    operation,
                    token
                );
                afterStaging(((TemplateArtifactStaging.Staged)staging).Artifact);
                return staging;
            };
            return harness;
        }

        [SetUp]
        public async Task Setup()
        {
            _occupied = Staging(artifact =>
                File.WriteAllBytes(
                    Path.Combine(
                        artifact.OperationDirectoryPath,
                        StagedTemplatePackage.ArtifactBaseName
                            + RestoreManifest.ArtifactExtensionFor(artifact.Manifest.DatabaseEngine)
                    ),
                    []
                )
            );
            _occupiedResult = await CreateAsync(_occupied);
            _occupiedDirectoryReleased = SharedTemplateCatalog.Workspace.OperationEntries.Length == 0;

            if (!OperatingSystem.IsWindows())
            {
                _refusing = Staging(artifact =>
                {
                    _refusingDirectory = artifact.OperationDirectoryPath;
                    _trap = new Catalog.DeletionTrap(_refusingDirectory);
                });
                _refusingResult = await CreateAsync(_refusing);
            }
        }

        [TearDown]
        public void TearDown()
        {
            _trap?.Dispose();
            _trap = null;
            if (_refusingDirectory.Length > 0 && Directory.Exists(_refusingDirectory))
            {
                Directory.Delete(_refusingDirectory, recursive: true);
            }
        }

        private DmsDataStoreCreateResult RefusingResult()
        {
            if (_refusingResult is null)
            {
                Assert.Ignore("A directory that refuses new entries is made with Unix modes.");
            }

            return _refusingResult!;
        }

        [Test]
        public void It_returns_staging_failed_for_an_io_failure() =>
            FailureOf(_occupiedResult)
                .Should()
                .BeEquivalentTo(
                    new
                    {
                        Code = DmsProvisioningFailureCode.StagingFailed,
                        Diagnostic = new
                        {
                            ExceptionTypeChain = "System.IO.IOException",
                            Operation = TemplateArtifactCatalog.ExtractArtifactStep,
                        },
                    }
                );

        [Test]
        public void It_sends_nothing_to_the_server_and_releases_the_directory() =>
            (_occupied.Engine.Calls.Count, CleanupOf(_occupiedResult).HasResidue, _occupiedDirectoryReleased)
                .Should()
                .Be((0, false, true));

        [Test]
        public void It_returns_staging_failed_for_a_refused_write()
        {
            DmsProvisioningFailure failure = FailureOf(RefusingResult());
            (failure.Code, failure.Diagnostic.Operation, failure.Diagnostic.ExceptionTypeChain.Split('/')[0])
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.StagingFailed,
                        TemplateArtifactCatalog.ExtractArtifactStep,
                        "System.UnauthorizedAccessException"
                    )
                );
        }

        [Test]
        public void It_sends_nothing_to_the_server_and_reports_what_the_refusal_kept()
        {
            DmsDataStoreCreateResult result = RefusingResult();
            string directoryName = Path.GetFileName(_refusingDirectory);
            _refusing.Engine.Calls.Should().BeEmpty();
            CleanupOf(result)
                .Residue.Should()
                .Equal(
                    new DmsCleanupResidue(
                        DmsResidueKind.PackageCopy,
                        directoryName + "/" + StagedTemplatePackage.PackageCopyFileName
                    ),
                    new DmsCleanupResidue(DmsResidueKind.OperationDirectory, directoryName)
                );
        }
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
    public class Given_the_server_refuses_the_admin_access_to_its_staging_database
    {
        private static readonly DmsProvisioningDiagnostic _refusal = new(
            "Npgsql.PostgresException",
            "42501",
            "EngineOperation"
        );

        private Harness _connectDenied = null!;
        private Harness _identityDenied = null!;
        private Harness _factsDenied = null!;
        private DmsDataStoreCreateResult _connectResult = null!;
        private DmsDataStoreCreateResult _identityResult = null!;
        private DmsDataStoreCreateResult _factsResult = null!;

        [SetUp]
        public async Task Setup()
        {
            _connectDenied = Scripted();
            _connectDenied.Engine.OnOpenSession = (_, _, _) =>
                Task.FromResult<DmsSessionOpenResult>(new DmsSessionOpenResult.AccessDenied(_refusal));
            _connectResult = await CreateAsync(_connectDenied);

            _identityDenied = Scripted();
            _identityDenied.Engine.OnReadIdentity = (_, _) =>
                Task.FromResult<DmsIdentityRead>(
                    new DmsIdentityRead.Invalid(DmsIdentityReadProblem.AccessDenied) { Diagnostic = _refusal }
                );
            _identityResult = await CreateAsync(_identityDenied);

            _factsDenied = Scripted();
            _factsDenied.Engine.OnReadTargetFacts = (_, _) => throw new DmsAccessDeniedException(_refusal);
            _factsResult = await CreateAsync(_factsDenied);
        }

        [Test]
        public void It_reports_a_refused_facts_read_as_the_missing_admin_privilege() =>
            FailureOf(_factsResult)
                .Should()
                .Be(
                    DmsProvisioningFailure.Create(
                        DmsProvisioningFailureCode.AdminPrivilegeMissing,
                        new DmsProvisioningDiagnostic(
                            "Npgsql.PostgresException",
                            "42501",
                            DmsDataStoreProvisioningService.ReadTargetFactsStep
                        )
                    )
                );

        [Test]
        public void It_reports_the_missing_admin_privilege_with_the_sanitized_diagnostic() =>
            (FailureOf(_connectResult), FailureOf(_identityResult))
                .Should()
                .Be(
                    (
                        DmsProvisioningFailure.Create(
                            DmsProvisioningFailureCode.AdminPrivilegeMissing,
                            new DmsProvisioningDiagnostic(
                                "Npgsql.PostgresException",
                                "42501",
                                DmsDataStoreProvisioningService.OpenStagingSessionStep
                            )
                        ),
                        DmsProvisioningFailure.Create(
                            DmsProvisioningFailureCode.AdminPrivilegeMissing,
                            new DmsProvisioningDiagnostic(
                                "Npgsql.PostgresException",
                                "42501",
                                DmsDataStoreProvisioningService.ReadIdentityStep
                            )
                        )
                    )
                );

        [Test]
        public void It_reads_no_facts_and_drops_its_own_staging_database()
        {
            foreach (Harness harness in new[] { _connectDenied, _identityDenied })
            {
                Members(harness).Should().NotContain(SpyDmsTargetEngine.ReadTargetFacts);
                CountOf(harness, SpyDmsTargetEngine.DropStaging).Should().Be(1);
            }

            CountOf(_factsDenied, SpyDmsTargetEngine.DropStaging).Should().Be(1);
        }
    }

    [TestFixture]
    public class Given_the_target_appears_before_publication
    {
        private Harness _harness = null!;
        private RecordingHooks _hooks = null!;
        private DmsDataStoreCreateResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            int targetInspections = 0;
            _hooks = new RecordingHooks();
            _harness = Scripted(_hooks);
            _harness.Engine.OnInspectByName = (name, _) =>
                Task.FromResult<DmsLiveTargetState>(
                    name.Value == CreateRequest().TargetName.Value && ++targetInspections == 2
                        ? new DmsLiveTargetState.Present(
                            new DmsInstanceIdentity.Postgresql(1, ""),
                            DmsDatabaseState.Online
                        )
                        : new DmsLiveTargetState.Absent()
                );
            _result = await CreateAsync(_harness);
        }

        [Test]
        public void It_hands_over_to_reconcile_instead_of_publishing()
        {
            // The database that appeared carries the package value, not the expected identity.
            DmsProvisioningFailure failure = FailureOf(_result);
            (failure.Code, failure.Diagnostic.Operation)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.TargetUnowned,
                        DmsDataStoreProvisioningService.ReconcileIdentityStep
                    )
                );
            TargetMutations(_harness)
                .Select(call => call.Member)
                .Should()
                .Equal(SpyDmsTargetEngine.OpenSession);
        }

        [Test]
        public void It_never_reaches_the_rename() =>
            (
                CountOf(_harness, SpyDmsTargetEngine.RenameIntoTarget),
                _hooks.Points.Contains(DmsProvisioningHookPoint.BeforeRename)
            )
                .Should()
                .Be((0, false));

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

    /// <summary>
    /// F-06 recovery without a restart, through one service instance over a real catalog and the real readiness
    /// singleton recorded <c>Ready</c>: while the trust policy cannot be used, resolve and create fail as
    /// <c>TrustPolicyUnavailable</c> at <c>TrustPolicy</c> with no engine call and readiness untouched; once the
    /// file is usable again, the same calls succeed with the pin the catalog resolved before, because every
    /// operation re-reads the policy.
    /// </summary>
    private sealed class PolicyRecovery : IDisposable
    {
        private readonly SharedCatalogBuild _build = SharedTemplateCatalog.BuildIsolated();

        public PolicyRecovery()
        {
            Readiness.Record(Ready);
            Harness = Scripted(catalog: _build.Catalog, readiness: Readiness);
            Request = CreateRequest(pin: _build.Pin);
        }

        public DmsDataStoreProvisionerReadiness Readiness { get; } = new();

        public DmsProvisionerReadiness Ready { get; } =
            new DmsProvisionerReadiness.Ready(
                "postgresql",
                new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero)
            );

        public Harness Harness { get; }

        public DmsTrustedArtifactPin Pin => _build.Pin;

        public string PolicyPath => _build.Workspace.TrustPolicyPath;

        /// <summary>One create request, presented again unchanged after recovery.</summary>
        public DmsDataStoreCreateRequest Request { get; }

        public async Task<(DmsArtifactResolution Resolved, DmsDataStoreCreateResult Created)> BothAsync() =>
            (
                await Harness.Service.ResolveArtifactAsync(
                    DmsDatabaseTemplate.Minimal,
                    CancellationToken.None
                ),
                await Harness.Service.CreateAsync(Request, CancellationToken.None)
            );

        public void Dispose() => _build.Workspace.Dispose();
    }

    private static (DmsProvisioningFailureCode, DmsProvisioningFailureCategory, string) Verdict(
        DmsProvisioningFailure failure
    ) => (failure.Code, failure.Category, failure.Diagnostic.Operation);

    private static (DmsProvisioningFailureCode, DmsProvisioningFailureCategory, string) PolicyVerdict(
        DmsProvisioningFailureCategory category
    ) =>
        (
            DmsProvisioningFailureCode.TrustPolicyUnavailable,
            category,
            TemplateArtifactCatalog.TrustPolicyStep
        );

    [TestFixture]
    public class Given_the_trust_policy_is_held_and_then_released
    {
        private PolicyRecovery _recovery = null!;
        private DmsArtifactResolution _resolvedWhileHeld = null!;
        private DmsDataStoreCreateResult _createdWhileHeld = null!;
        private int _engineCallsWhileHeld = -1;
        private DmsProvisionerReadiness _readinessWhileHeld = null!;
        private DmsArtifactResolution _resolvedAfter = null!;
        private DmsDataStoreCreateResult _createdAfter = null!;

        [SetUp]
        public async Task Setup()
        {
            _recovery = new PolicyRecovery();
            using (new FileStream(_recovery.PolicyPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                (_resolvedWhileHeld, _createdWhileHeld) = await _recovery.BothAsync();
                _engineCallsWhileHeld = _recovery.Harness.Engine.Calls.Count;
                _readinessWhileHeld = _recovery.Readiness.Current;
            }

            (_resolvedAfter, _createdAfter) = await _recovery.BothAsync();
        }

        [TearDown]
        public void TearDown() => _recovery.Dispose();

        [Test]
        public void It_reports_both_calls_as_transient_while_the_policy_is_held() =>
            new[]
            {
                Verdict(_resolvedWhileHeld.Should().BeOfType<DmsArtifactResolution.Failure>().Subject.Detail),
                Verdict(FailureOf(_createdWhileHeld)),
            }
                .Should()
                .Equal(
                    PolicyVerdict(DmsProvisioningFailureCategory.Transient),
                    PolicyVerdict(DmsProvisioningFailureCategory.Transient)
                );

        [Test]
        public void It_makes_no_engine_call_while_the_policy_is_held() =>
            _engineCallsWhileHeld.Should().Be(0);

        [Test]
        public void It_leaves_the_recorded_readiness_in_place() =>
            new[] { _readinessWhileHeld, _recovery.Readiness.Current }
                .Should()
                .AllSatisfy(current => current.Should().BeSameAs(_recovery.Ready));

        [Test]
        public void It_resolves_the_same_pin_once_the_policy_is_released() =>
            _resolvedAfter
                .Should()
                .BeOfType<DmsArtifactResolution.Resolved>()
                .Which.Pin.Should()
                .Be(_recovery.Pin);

        [Test]
        public void It_creates_through_the_same_service_once_the_policy_is_released() =>
            _createdAfter
                .Should()
                .Be(new DmsDataStoreCreateResult.Created(_recovery.Pin, DmsCleanupReport.Clean));
    }

    [TestFixture]
    public class Given_the_trust_policy_is_malformed_and_then_repaired
    {
        private PolicyRecovery _recovery = null!;
        private DmsArtifactResolution _resolvedWhileMalformed = null!;
        private DmsDataStoreCreateResult _createdWhileMalformed = null!;
        private int _engineCallsWhileMalformed = -1;
        private DmsArtifactResolution _resolvedAfter = null!;
        private DmsDataStoreCreateResult _createdAfter = null!;

        [SetUp]
        public async Task Setup()
        {
            _recovery = new PolicyRecovery();
            byte[] policy = await File.ReadAllBytesAsync(_recovery.PolicyPath);
            await File.WriteAllTextAsync(_recovery.PolicyPath, "{");
            (_resolvedWhileMalformed, _createdWhileMalformed) = await _recovery.BothAsync();
            _engineCallsWhileMalformed = _recovery.Harness.Engine.Calls.Count;

            await File.WriteAllBytesAsync(_recovery.PolicyPath, policy);
            (_resolvedAfter, _createdAfter) = await _recovery.BothAsync();
        }

        [TearDown]
        public void TearDown() => _recovery.Dispose();

        [Test]
        public void It_reports_both_calls_for_inspection_while_the_policy_is_malformed() =>
            new[]
            {
                Verdict(
                    _resolvedWhileMalformed.Should().BeOfType<DmsArtifactResolution.Failure>().Subject.Detail
                ),
                Verdict(FailureOf(_createdWhileMalformed)),
            }
                .Should()
                .Equal(
                    PolicyVerdict(DmsProvisioningFailureCategory.OperatorInspectionRequired),
                    PolicyVerdict(DmsProvisioningFailureCategory.OperatorInspectionRequired)
                );

        [Test]
        public void It_makes_no_engine_call_while_the_policy_is_malformed() =>
            _engineCallsWhileMalformed.Should().Be(0);

        [Test]
        public void It_leaves_the_recorded_readiness_in_place() =>
            _recovery.Readiness.Current.Should().BeSameAs(_recovery.Ready);

        [Test]
        public void It_succeeds_through_the_same_service_once_the_policy_is_repaired()
        {
            _resolvedAfter
                .Should()
                .BeOfType<DmsArtifactResolution.Resolved>()
                .Which.Pin.Should()
                .Be(_recovery.Pin);
            _createdAfter
                .Should()
                .Be(new DmsDataStoreCreateResult.Created(_recovery.Pin, DmsCleanupReport.Clean));
        }
    }
}
