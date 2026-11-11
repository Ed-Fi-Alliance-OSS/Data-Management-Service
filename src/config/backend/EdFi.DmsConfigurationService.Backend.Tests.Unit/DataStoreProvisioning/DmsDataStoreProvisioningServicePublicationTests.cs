// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Packaging;
using FluentAssertions;
using static EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.ProvisioningTestValues;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning;

/// <summary>
/// The orchestrator's publication stage (6.3 steps 8 to 10, 6.9b, 6.7, Step 3.6) over the spy engine and the
/// real catalog: the rename's definite outcomes and its collision with a database already holding the name
/// (6.5 steps 1 and 2, no mutation); a rename in doubt settled after the lock by the holder's termination and
/// two inspections; the marker's definite failure compensated on the holder, its unknown outcome resolved by
/// a termination and a re-read on a new instance-bound session; loss and the caller's cancellation around
/// both; and an instance that changed after the rename, which is never compensated.
/// </summary>
public class DmsDataStoreProvisioningServicePublicationTests
{
    private static readonly Guid _expected = Guid.Parse("0d6c1a2b-3c4d-4e5f-8a9b-0c1d2e3f4a5b");
    private static readonly Guid _otherIdentity = Guid.Parse("9e8d7c6b-5a49-4382-9170-6f5e4d3c2b1a");
    private static readonly DmsInstanceIdentity.Postgresql _stagingInstance = new(4242, "stage-claim");
    private static readonly DmsInstanceIdentity.Postgresql _foreignInstance = new(5151, "");

    private static readonly DmsServerSessionId _stagingSession = new(
        99,
        new DateTimeOffset(2026, 11, 6, 0, 0, 0, TimeSpan.Zero)
    );

    private static readonly DmsServerSessionId _markerSession = new(
        123,
        new DateTimeOffset(2026, 11, 6, 0, 0, 1, TimeSpan.Zero)
    );

    private static readonly DmsDataStoreCreateRequest _request = CreateRequest(
        expectedSourceIdentity: _expected
    );

    private static string TargetName => _request.TargetName.Value;

    private static bool IsStaging(DmsDatabaseName name) =>
        name.Value.StartsWith(DmsOperationId.StagingDatabaseNamePrefix, StringComparison.Ordinal);

    private static readonly Func<int, DmsLiveTargetState> _alwaysAbsent =
        _ => new DmsLiveTargetState.Absent();

    private static DmsLiveTargetState PresentUnder(DmsInstanceIdentity instance) =>
        new DmsLiveTargetState.Present(instance, DmsDatabaseState.Online);

    private static DmsIdentityRead Identity(Guid value) => new DmsIdentityRead.Found(value);

    private sealed class FakeSession(DmsInstanceIdentity instance, DmsServerSessionId session)
        : DmsSession(instance, session)
    {
        private int _disposals;

        public bool Disposed => _disposals > 0;

        public int Disposals => _disposals;

        public override ValueTask DisposeAsync()
        {
            _disposals++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingHooks : IDmsProvisioningTestHooks
    {
        public List<DmsProvisioningHookPoint> Points { get; } = [];

        public HashSet<DmsProvisioningFaultPoint> Faults { get; } = [];

        public Action<DmsProvisioningHookPoint>? OnPoint { get; set; }

        public ValueTask AtAsync(
            DmsProvisioningHookPoint point,
            DmsOperationId operation,
            CancellationToken cancellationToken
        )
        {
            Points.Add(point);
            OnPoint?.Invoke(point);
            return ValueTask.CompletedTask;
        }

        public bool ShouldInjectFault(DmsProvisioningFaultPoint point, DmsOperationId operation) =>
            Faults.Contains(point);
    }

    /// <summary>
    /// A harness scripted so the whole create succeeds: staging, rename, re-check, marker. Inspections are
    /// scripted per name by their 1-based count (the target is inspected in the preamble, before the rename,
    /// and in any resolution after it; the staging name before its create and in any resolution); a session
    /// opened on the staging name carries <see cref="_stagingSession"/>, one on the target
    /// <see cref="_markerSession"/>; every identity read returns the package value unless scripted.
    /// </summary>
    private static Harness Scripted(
        RecordingHooks? hooks = null,
        Func<int, DmsLiveTargetState>? targetInspection = null,
        Func<int, DmsLiveTargetState>? stagingInspection = null,
        Action<DmsDataStoreProvisioningOptions>? configure = null
    )
    {
        Harness harness = new(hooks: hooks, configure: configure);
        SpyDmsTargetEngine engine = harness.Engine;
        int targetInspections = 0;
        int stagingInspections = 0;
        engine.OnInspectByName = (name, _) =>
            Task.FromResult(
                IsStaging(name)
                    ? (stagingInspection ?? _alwaysAbsent)(++stagingInspections)
                    : (targetInspection ?? _alwaysAbsent)(++targetInspections)
            );
        engine.OnCreateStaging = (_, _, _) =>
            Task.FromResult<DmsCommandOutcome<DmsInstanceIdentity>>(
                new DmsCommandOutcome<DmsInstanceIdentity>.Succeeded(_stagingInstance)
            );
        engine.OnRestoreIntoStaging = (_, _, _) =>
            Task.FromResult<DmsCommandOutcome<DmsInstanceIdentity>>(
                new DmsCommandOutcome<DmsInstanceIdentity>.Succeeded(_stagingInstance)
            );
        engine.OnOpenSession = (name, instance, _) =>
            Task.FromResult<DmsSessionOpenResult>(
                new DmsSessionOpenResult.Opened(
                    new FakeSession(instance, IsStaging(name) ? _stagingSession : _markerSession)
                )
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
        engine.OnDropInstance = (_, _, _, _) =>
            Task.FromResult<DmsCommandOutcome>(new DmsCommandOutcome.Succeeded());
        return harness;
    }

    /// <summary>Identity reads on target sessions: the first (the 6.3 step 9 re-check) returns the package value, later ones <paramref name="afterTheMarker"/>.</summary>
    private static void ReadsAfterTheMarker(Harness harness, DmsIdentityRead afterTheMarker)
    {
        int targetReads = 0;
        harness.Engine.OnReadIdentity = (session, _) =>
            Task.FromResult(
                session.ServerSession == _markerSession && ++targetReads > 1
                    ? afterTheMarker
                    : TargetFactsSample.Identity()
            );
    }

    private static Task<DmsDataStoreCreateResult> CreateAsync(
        Harness harness,
        CancellationToken cancellationToken = default
    ) => harness.Service.CreateAsync(_request, cancellationToken);

    private static int CountOf(Harness harness, string member) =>
        harness.Engine.Calls.Count(call => call.Member == member);

    /// <summary>The calls issued after the rename attempt.</summary>
    private static IReadOnlyList<SpyDmsTargetEngine.Call> AfterTheRename(Harness harness) =>
        [
            .. harness
                .Engine.Calls.SkipWhile(call => call.Member != SpyDmsTargetEngine.RenameIntoTarget)
                .Skip(1),
        ];

    private static (DmsProvisioningFailureCode, DmsProvisioningFailureCategory, string) Verdict(
        DmsDataStoreCreateResult result
    )
    {
        DmsProvisioningFailure failure = FailureOf(result);
        return (failure.Code, failure.Category, failure.Diagnostic.Operation);
    }

    [TestFixture]
    public class Given_the_rename_collides_with_a_database_holding_the_target
    {
        private Harness _foreign = null!;
        private Harness _unreadable = null!;
        private Harness _owned = null!;
        private DmsDataStoreCreateResult _foreignResult = null!;
        private DmsDataStoreCreateResult _unreadableResult = null!;
        private DmsDataStoreCreateResult _ownedResult = null!;

        /// <summary>The rename collides; the database found under the name afterwards answers the identity read with <paramref name="identity"/>.</summary>
        private static Harness Colliding(DmsIdentityRead identity)
        {
            Harness harness = Scripted(targetInspection: inspection =>
                inspection <= 2 ? new DmsLiveTargetState.Absent() : PresentUnder(_foreignInstance)
            );
            harness.Engine.OnRenameIntoTarget = (_, _, _, _) =>
                Task.FromResult<DmsCommandOutcome<DmsInstanceIdentity>>(
                    new DmsCommandOutcome<DmsInstanceIdentity>.Failed(
                        new DmsCommandFailure(
                            DmsCommandFailureClass.DuplicateName,
                            DmsProvisioningDiagnostic.ForOperation("RenameIntoTarget")
                        )
                    )
                );
            harness.Engine.OnReadIdentity = (session, _) =>
                Task.FromResult(
                    session.Instance == _foreignInstance ? identity : TargetFactsSample.Identity()
                );
            return harness;
        }

        [SetUp]
        public async Task Setup()
        {
            _foreign = Colliding(Identity(_otherIdentity));
            _foreignResult = await CreateAsync(_foreign);

            _unreadable = Colliding(new DmsIdentityRead.Invalid(DmsIdentityReadProblem.TableMissing));
            _unreadableResult = await CreateAsync(_unreadable);

            _owned = Colliding(Identity(_expected));
            _ownedResult = await CreateAsync(_owned);
        }

        [Test]
        public void It_reports_another_identity_as_unowned() =>
            Verdict(_foreignResult)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.TargetUnowned,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        DmsDataStoreProvisioningService.ReconcileIdentityStep
                    )
                );

        [Test]
        public void It_reports_an_unreadable_identity_as_unverifiable() =>
            Verdict(_unreadableResult)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.TargetUnverifiable,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        DmsDataStoreProvisioningService.ReconcileIdentityStep
                    )
                );

        [Test]
        public void It_reconciles_a_database_carrying_the_expected_identity_after_the_full_validation()
        {
            _ownedResult
                .Should()
                .Be(
                    new DmsDataStoreCreateResult.Reconciled(SharedTemplateCatalog.Pin, DmsCleanupReport.Clean)
                );
            AfterTheRename(_owned)
                .Select(call => call.Member)
                .Should()
                .Equal(
                    SpyDmsTargetEngine.Inspect,
                    SpyDmsTargetEngine.OpenSession,
                    SpyDmsTargetEngine.ReadIdentity,
                    SpyDmsTargetEngine.ReadTargetFacts,
                    SpyDmsTargetEngine.DropStaging
                );
        }

        [Test]
        public void It_only_reads_the_colliding_database_and_drops_its_own_staging_database()
        {
            foreach (Harness harness in new[] { _foreign, _unreadable })
            {
                AfterTheRename(harness)
                    .Select(call => call.Member)
                    .Should()
                    .Equal(
                        SpyDmsTargetEngine.Inspect,
                        SpyDmsTargetEngine.OpenSession,
                        SpyDmsTargetEngine.ReadIdentity,
                        SpyDmsTargetEngine.DropStaging
                    );
                AfterTheRename(harness)[^1]
                    .DatabaseName.Should()
                    .StartWith(DmsOperationId.StagingDatabaseNamePrefix);
            }
        }

        [Test]
        public void It_reports_no_residue() =>
            (CleanupOf(_foreignResult).HasResidue, CleanupOf(_unreadableResult).HasResidue)
                .Should()
                .Be((false, false));
    }

    [TestFixture]
    public class Given_the_rename_fails_definitely
    {
        private static readonly (DmsCommandFailureClass Class, DmsProvisioningFailureCode Code)[] _table =
        [
            (DmsCommandFailureClass.ObjectInUse, DmsProvisioningFailureCode.ExclusiveAccessNotEstablished),
            (DmsCommandFailureClass.PrivilegeMissing, DmsProvisioningFailureCode.AdminPrivilegeMissing),
            (DmsCommandFailureClass.DoesNotExist, DmsProvisioningFailureCode.InstanceChanged),
            (DmsCommandFailureClass.Rejected, DmsProvisioningFailureCode.ServerRejected),
        ];

        private readonly List<(DmsProvisioningFailure Failure, Harness Harness)> _runs = [];

        [SetUp]
        public async Task Setup()
        {
            _runs.Clear();
            foreach ((DmsCommandFailureClass failureClass, _) in _table)
            {
                Harness harness = Scripted();
                harness.Engine.OnRenameIntoTarget = (_, _, _, _) =>
                    Task.FromResult<DmsCommandOutcome<DmsInstanceIdentity>>(
                        new DmsCommandOutcome<DmsInstanceIdentity>.Failed(
                            new DmsCommandFailure(
                                failureClass,
                                DmsProvisioningDiagnostic.ForOperation("RenameIntoTarget")
                            )
                        )
                    );
                _runs.Add((FailureOf(await CreateAsync(harness)), harness));
            }
        }

        [Test]
        public void It_maps_each_class_to_its_code() =>
            _runs.Select(run => run.Failure.Code).Should().Equal(_table.Select(row => row.Code));

        [Test]
        public void It_keeps_exclusive_access_transient_because_nothing_was_renamed() =>
            _runs[0].Failure.Category.Should().Be(DmsProvisioningFailureCategory.Transient);

        [Test]
        public void It_drops_the_staging_database_and_goes_no_further() =>
            _runs
                .Select(run => AfterTheRename(run.Harness).Select(call => call.Member).ToArray())
                .Should()
                .AllSatisfy(members => members.Should().Equal(SpyDmsTargetEngine.DropStaging));
    }

    /// <summary>
    /// H-02: a rename the server definitely rejected left the target name as it was, and only the provider's separate
    /// retryability verdict makes the <c>ServerRejected</c> that reports it transient; a non-retryable rejection stays
    /// permanent, a missing privilege stays <c>AdminPrivilegeMissing</c> whatever the flag says, and an object in use
    /// keeps its own transient code. The staging database is dropped and nothing else follows, with nothing left over.
    /// </summary>
    [TestFixture]
    public class Given_the_rename_is_rejected_with_or_without_retryability
    {
        private static readonly (
            DmsCommandFailureClass Class,
            string Code,
            bool Retryable,
            DmsProvisioningFailureCode Expected,
            DmsProvisioningFailureCategory Category
        )[] _table =
        [
            (
                DmsCommandFailureClass.Rejected,
                "55P03",
                true,
                DmsProvisioningFailureCode.ServerRejected,
                DmsProvisioningFailureCategory.Transient
            ),
            (
                DmsCommandFailureClass.Rejected,
                "701",
                true,
                DmsProvisioningFailureCode.ServerRejected,
                DmsProvisioningFailureCategory.Transient
            ),
            (
                DmsCommandFailureClass.Rejected,
                "22023",
                false,
                DmsProvisioningFailureCode.ServerRejected,
                DmsProvisioningFailureCategory.Permanent
            ),
            (
                DmsCommandFailureClass.PrivilegeMissing,
                "42501",
                true,
                DmsProvisioningFailureCode.AdminPrivilegeMissing,
                DmsProvisioningFailureCategory.Permanent
            ),
            (
                DmsCommandFailureClass.ObjectInUse,
                "55006",
                false,
                DmsProvisioningFailureCode.ExclusiveAccessNotEstablished,
                DmsProvisioningFailureCategory.Transient
            ),
        ];

        private readonly List<(DmsDataStoreCreateResult Result, Harness Harness)> _runs = [];

        [SetUp]
        public async Task Setup()
        {
            _runs.Clear();
            foreach ((DmsCommandFailureClass failureClass, string code, bool retryable, _, _) in _table)
            {
                Harness harness = Scripted();
                harness.Engine.OnRenameIntoTarget = (_, _, _, _) =>
                    Task.FromResult<DmsCommandOutcome<DmsInstanceIdentity>>(
                        new DmsCommandOutcome<DmsInstanceIdentity>.Failed(
                            new DmsCommandFailure(
                                failureClass,
                                new DmsProvisioningDiagnostic(
                                    "Npgsql.PostgresException",
                                    code,
                                    "RenameIntoTarget"
                                )
                            )
                            {
                                Retryable = retryable,
                            }
                        )
                    );
                _runs.Add((await CreateAsync(harness), harness));
            }
        }

        [Test]
        public void It_is_transient_only_for_a_retryable_server_rejection_or_its_own_transient_code() =>
            _runs
                .Select(run => FailureOf(run.Result))
                .Select(failure => (failure.Code, failure.Category, failure.Diagnostic.ProviderErrorCode))
                .Should()
                .Equal(_table.Select(row => (row.Expected, row.Category, (string?)row.Code)));

        [Test]
        public void It_drops_the_staging_database_goes_no_further_and_leaves_no_residue() =>
            _runs
                .Should()
                .AllSatisfy(run =>
                {
                    AfterTheRename(run.Harness)
                        .Select(call => call.Member)
                        .Should()
                        .Equal(SpyDmsTargetEngine.DropStaging);
                    ((DmsDataStoreCreateResult.Failure)run.Result).Cleanup.HasResidue.Should().BeFalse();
                });
    }

    /// <summary>
    /// F-03 (R2-2): a rename whose result is lost while the holder still holds the lock is settled after the lock is
    /// released, and only what settlement proves decides the category. Not renamed with the target name read absent is
    /// a retry (<c>Unknown</c>, transient); not renamed while another database, a protected one, or a fenced one holds
    /// the name is <c>InstanceChanged</c>; renamed is <c>IdentityAssignmentFailed</c> with the unmarked target; an
    /// unreadable target or an unproven holder stays <c>OutcomeUnknown</c>. The staging database is dropped whenever
    /// the rename is proven not to have happened, whatever holds the target name, and kept otherwise.
    /// </summary>
    [TestFixture]
    public class Given_the_rename_outcome_is_unknown
    {
        private Harness _renamed = null!;
        private Harness _notRenamed = null!;
        private Harness _foreignPresent = null!;
        private Harness _protectedTarget = null!;
        private Harness _foreignFenced = null!;
        private Harness _unreadableTarget = null!;
        private Harness _neither = null!;
        private Harness _holderUnproven = null!;
        private DmsDataStoreCreateResult _renamedResult = null!;
        private DmsDataStoreCreateResult _notRenamedResult = null!;
        private DmsDataStoreCreateResult _foreignPresentResult = null!;
        private DmsDataStoreCreateResult _protectedTargetResult = null!;
        private DmsDataStoreCreateResult _foreignFencedResult = null!;
        private DmsDataStoreCreateResult _unreadableTargetResult = null!;
        private DmsDataStoreCreateResult _neitherResult = null!;
        private DmsDataStoreCreateResult _holderUnprovenResult = null!;
        private readonly List<DmsInstanceIdentity> _foreignPresentDrops = [];

        private static bool HasResidue(DmsDataStoreCreateResult result, DmsResidueKind kind) =>
            CleanupOf(result).Residue.Any(residue => residue.Kind == kind);

        /// <summary>The rename comes back unknown; after the lock the target and staging names show what <paramref name="target"/> and <paramref name="staging"/> say.</summary>
        private static Harness InDoubt(
            Func<DmsLiveTargetState> target,
            DmsLiveTargetState staging,
            bool holderGone = true
        )
        {
            Harness harness = Scripted(
                targetInspection: inspection => inspection <= 2 ? new DmsLiveTargetState.Absent() : target(),
                stagingInspection: inspection => inspection == 1 ? new DmsLiveTargetState.Absent() : staging
            );
            harness.Engine.OnRenameIntoTarget = (_, _, _, _) =>
                Task.FromResult<DmsCommandOutcome<DmsInstanceIdentity>>(
                    new DmsCommandOutcome<DmsInstanceIdentity>.Unknown(Session)
                );
            harness.Engine.OnTerminateSession = (session, _, _) =>
                Task.FromResult<DmsSessionTermination>(
                    session == Session && !holderGone
                        ? new DmsSessionTermination.NotTerminated(DmsSessionTerminationFailure.TimedOut)
                        : new DmsSessionTermination.Terminated()
                );
            return harness;
        }

        private static Harness InDoubt(
            DmsLiveTargetState target,
            DmsLiveTargetState staging,
            bool holderGone = true
        ) => InDoubt(() => target, staging, holderGone);

        [SetUp]
        public async Task Setup()
        {
            _renamed = InDoubt(PresentUnder(_stagingInstance), new DmsLiveTargetState.Absent());
            _renamedResult = await CreateAsync(_renamed);

            _notRenamed = InDoubt(new DmsLiveTargetState.Absent(), PresentUnder(_stagingInstance));
            _notRenamedResult = await CreateAsync(_notRenamed);

            _foreignPresentDrops.Clear();
            _foreignPresent = InDoubt(PresentUnder(_foreignInstance), PresentUnder(_stagingInstance));
            _foreignPresent.Engine.OnDropStaging = (_, instance, _) =>
            {
                _foreignPresentDrops.Add(instance);
                return Task.FromResult<DmsCommandOutcome>(new DmsCommandOutcome.Succeeded());
            };
            _foreignPresentResult = await CreateAsync(_foreignPresent);

            _protectedTarget = InDoubt(new DmsLiveTargetState.Protected(), PresentUnder(_stagingInstance));
            _protectedTargetResult = await CreateAsync(_protectedTarget);

            _foreignFenced = InDoubt(
                new DmsLiveTargetState.Fenced(_foreignInstance),
                PresentUnder(_stagingInstance)
            );
            _foreignFencedResult = await CreateAsync(_foreignFenced);

            _unreadableTarget = InDoubt(
                () => throw new InvalidOperationException("The target name could not be read."),
                PresentUnder(_stagingInstance)
            );
            _unreadableTargetResult = await CreateAsync(_unreadableTarget);

            _neither = InDoubt(new DmsLiveTargetState.Absent(), new DmsLiveTargetState.Absent());
            _neitherResult = await CreateAsync(_neither);

            _holderUnproven = InDoubt(
                PresentUnder(_stagingInstance),
                new DmsLiveTargetState.Absent(),
                holderGone: false
            );
            _holderUnprovenResult = await CreateAsync(_holderUnproven);
        }

        [Test]
        public void It_allows_a_retry_when_the_rename_did_not_happen_and_the_target_name_is_absent() =>
            Verdict(_notRenamedResult)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.Unknown,
                        DmsProvisioningFailureCategory.Transient,
                        DmsDataStoreProvisioningService.RenameIntoTargetStep
                    )
                );

        [Test]
        public void It_drops_the_staging_database_when_it_was_not_renamed()
        {
            CountOf(_notRenamed, SpyDmsTargetEngine.DropStaging).Should().Be(1);
            CleanupOf(_notRenamedResult).HasResidue.Should().BeFalse();
        }

        [Test]
        public void It_requires_inspection_when_another_database_took_the_target_name() =>
            new[] { _foreignPresentResult, _protectedTargetResult, _foreignFencedResult }
                .Select(Verdict)
                .Should()
                .AllBeEquivalentTo(
                    (
                        DmsProvisioningFailureCode.InstanceChanged,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        DmsDataStoreProvisioningService.ResolveRenameStep
                    )
                );

        [Test]
        public void It_still_drops_its_own_staging_database_whatever_took_the_target_name()
        {
            new[] { _foreignPresent, _protectedTarget, _foreignFenced }
                .Select(harness => CountOf(harness, SpyDmsTargetEngine.DropStaging))
                .Should()
                .Equal(1, 1, 1);
            _foreignPresentDrops.Should().Equal(_stagingInstance);
            new[] { _foreignPresentResult, _protectedTargetResult, _foreignFencedResult }
                .Should()
                .AllSatisfy(result => CleanupOf(result).HasResidue.Should().BeFalse());
        }

        [Test]
        public void It_never_addresses_the_target_name_after_the_rename_except_to_read_it() =>
            AfterTheRename(_foreignPresent)
                .Where(call => call.DatabaseName == TargetName)
                .Select(call => call.Member)
                .Should()
                .OnlyContain(member => member == SpyDmsTargetEngine.Inspect);

        [Test]
        public void It_keeps_an_unreadable_target_name_unresolved_and_the_staging_database_in_place()
        {
            Verdict(_unreadableTargetResult)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.OutcomeUnknown,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        DmsDataStoreProvisioningService.ResolveRenameStep
                    )
                );
            CountOf(_unreadableTarget, SpyDmsTargetEngine.DropStaging).Should().Be(0);
            HasResidue(_unreadableTargetResult, DmsResidueKind.StagingDatabase).Should().BeTrue();
        }

        [Test]
        public void It_reports_a_rename_found_done_as_an_unassigned_identity_for_inspection() =>
            Verdict(_renamedResult)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.IdentityAssignmentFailed,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        DmsDataStoreProvisioningService.RenameIntoTargetStep
                    )
                );

        [Test]
        public void It_proves_the_released_holder_gone_before_reading_state()
        {
            _renamed.Engine.TerminatedSessions.Should().Contain(Session);
            _renamed.OnlyHolder.ReleaseCount.Should().Be(1);
            AfterTheRename(_renamed)
                .Select(call => call.Member)
                .Should()
                .StartWith([SpyDmsTargetEngine.TerminateSession, SpyDmsTargetEngine.Inspect]);
        }

        [Test]
        public void It_reports_a_renamed_target_as_unmarked_and_leaves_the_staging_name_alone()
        {
            CleanupOf(_renamedResult)
                .Residue.Should()
                .Equal(new DmsCleanupResidue(DmsResidueKind.UnmarkedTarget, TargetName));
            CountOf(_renamed, SpyDmsTargetEngine.DropStaging).Should().Be(0);
        }

        [Test]
        public void It_reports_an_instance_under_neither_name_as_unknown_and_drops_nothing()
        {
            Verdict(_neitherResult)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.OutcomeUnknown,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        DmsDataStoreProvisioningService.ResolveRenameStep
                    )
                );
            CountOf(_neither, SpyDmsTargetEngine.DropStaging).Should().Be(0);
            HasResidue(_neitherResult, DmsResidueKind.StagingDatabase).Should().BeTrue();
        }

        [Test]
        public void It_reads_nothing_and_drops_nothing_while_the_holder_is_not_proven_gone()
        {
            Verdict(_holderUnprovenResult).Item1.Should().Be(DmsProvisioningFailureCode.OutcomeUnknown);
            AfterTheRename(_holderUnproven)
                .Select(call => call.Member)
                .Should()
                .Equal(SpyDmsTargetEngine.TerminateSession);
            HasResidue(_holderUnprovenResult, DmsResidueKind.StagingDatabase).Should().BeTrue();
        }
    }

    /// <summary>
    /// F-03: the holder is declared lost while the rename it carried is in doubt. The loss is real, so the code stays
    /// <c>LockLost</c>; its category follows settlement: transient only when the rename is proven not to have happened
    /// and the target name is absent, an inspection failure when another database holds the name or the target was
    /// renamed. The staging database follows the rename evidence alone.
    /// </summary>
    [TestFixture]
    public class Given_the_holder_is_lost_while_the_rename_is_in_doubt
    {
        private Harness _notRenamed = null!;
        private Harness _foreignTarget = null!;
        private Harness _renamed = null!;
        private DmsDataStoreCreateResult _notRenamedResult = null!;
        private DmsDataStoreCreateResult _foreignTargetResult = null!;
        private DmsDataStoreCreateResult _renamedResult = null!;

        private static Harness LostInDoubt(DmsLiveTargetState target, DmsLiveTargetState staging)
        {
            Harness harness = Scripted(
                targetInspection: inspection => inspection <= 2 ? new DmsLiveTargetState.Absent() : target,
                stagingInspection: inspection => inspection == 1 ? new DmsLiveTargetState.Absent() : staging
            );
            harness.Engine.OnRenameIntoTarget = (_, _, _, _) =>
            {
                harness.OnlyHolder.Lose();
                return Task.FromResult<DmsCommandOutcome<DmsInstanceIdentity>>(
                    new DmsCommandOutcome<DmsInstanceIdentity>.Unknown(Session)
                );
            };
            return harness;
        }

        [SetUp]
        public async Task Setup()
        {
            _notRenamed = LostInDoubt(new DmsLiveTargetState.Absent(), PresentUnder(_stagingInstance));
            _notRenamedResult = await CreateAsync(_notRenamed);

            _foreignTarget = LostInDoubt(PresentUnder(_foreignInstance), PresentUnder(_stagingInstance));
            _foreignTargetResult = await CreateAsync(_foreignTarget);

            _renamed = LostInDoubt(PresentUnder(_stagingInstance), new DmsLiveTargetState.Absent());
            _renamedResult = await CreateAsync(_renamed);
        }

        [Test]
        public void It_reports_loss_as_transient_when_the_rename_did_not_happen_and_the_target_name_is_absent()
        {
            Verdict(_notRenamedResult)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.LockLost,
                        DmsProvisioningFailureCategory.Transient,
                        DmsDataStoreProvisioningService.RenameIntoTargetStep
                    )
                );
            CountOf(_notRenamed, SpyDmsTargetEngine.DropStaging).Should().Be(1);
        }

        [Test]
        public void It_keeps_loss_an_inspection_failure_when_another_database_holds_the_target_name()
        {
            Verdict(_foreignTargetResult)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.LockLost,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        DmsDataStoreProvisioningService.RenameIntoTargetStep
                    )
                );
            CountOf(_foreignTarget, SpyDmsTargetEngine.DropStaging).Should().Be(1);
        }

        [Test]
        public void It_keeps_loss_an_inspection_failure_with_the_unmarked_target_when_the_rename_happened()
        {
            Verdict(_renamedResult)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.LockLost,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        DmsDataStoreProvisioningService.RenameIntoTargetStep
                    )
                );
            CleanupOf(_renamedResult)
                .Residue.Should()
                .Equal(new DmsCleanupResidue(DmsResidueKind.UnmarkedTarget, TargetName));
        }
    }

    /// <summary>
    /// Independence: a rename whose step deadline ends it (the scope's <c>TimedOut</c>) was never given an in-doubt
    /// classification, so settlement leaves its failure as it is even when it proves the rename did not happen.
    /// </summary>
    [TestFixture]
    public class Given_the_rename_step_deadline_ends_a_rename_that_did_not_happen
    {
        private Harness _harness = null!;
        private DmsDataStoreCreateResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = Scripted(stagingInspection: inspection =>
                inspection == 1 ? new DmsLiveTargetState.Absent() : PresentUnder(_stagingInstance)
            );
            TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _harness.Engine.OnRenameIntoTarget = async (_, _, _, token) =>
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException("The rename was never meant to return.");
            };
            Task<DmsDataStoreCreateResult> create = CreateAsync(_harness);
            await ReadStartedAsync(started, create);
            _harness.Time.Advance(CommandTimeout * 3 + TimeSpan.FromSeconds(1));
            _result = await create;
        }

        [Test]
        public void It_keeps_the_deadline_failure_as_it_was() =>
            Verdict(_result)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.TimedOut,
                        DmsProvisioningFailureCategory.Transient,
                        DmsDataStoreProvisioningService.RenameIntoTargetStep
                    )
                );

        [Test]
        public void It_drops_the_staging_database_the_rename_left_under_its_name() =>
            CountOf(_harness, SpyDmsTargetEngine.DropStaging).Should().Be(1);
    }

    /// <summary>
    /// F-03 (review of Step 3.1): an engine may report the caller's cancellation of a dispatched rename as an unknown
    /// outcome, as SQL Server does. The cancellation wins over any reclassification: the rename in doubt is settled
    /// after the lock is released (the holder proven gone, both names read), the staging database is dropped when the
    /// rename is proven not to have happened and kept when it did, and only then does the caller receive
    /// <see cref="OperationCanceledException"/>.
    /// </summary>
    [TestFixture]
    public class Given_the_caller_cancels_while_the_rename_is_dispatched
    {
        private Harness _notRenamed = null!;
        private Harness _renamed = null!;
        private Exception? _notRenamedOutcome;
        private Exception? _renamedOutcome;

        private static async Task<(Harness Harness, Exception? Outcome)> CancelledInDoubtAsync(
            DmsLiveTargetState target,
            DmsLiveTargetState staging
        )
        {
            Harness harness = Scripted(
                targetInspection: inspection => inspection <= 2 ? new DmsLiveTargetState.Absent() : target,
                stagingInspection: inspection => inspection == 1 ? new DmsLiveTargetState.Absent() : staging
            );
            using CancellationTokenSource caller = new();
            harness.Engine.OnRenameIntoTarget = async (_, _, _, _) =>
            {
                await caller.CancelAsync();
                return new DmsCommandOutcome<DmsInstanceIdentity>.Unknown(Session);
            };
            Exception? outcome = await Record.ExceptionAsync(() => CreateAsync(harness, caller.Token));
            return (harness, outcome);
        }

        [SetUp]
        public async Task Setup()
        {
            (_notRenamed, _notRenamedOutcome) = await CancelledInDoubtAsync(
                new DmsLiveTargetState.Absent(),
                PresentUnder(_stagingInstance)
            );
            (_renamed, _renamedOutcome) = await CancelledInDoubtAsync(
                PresentUnder(_stagingInstance),
                new DmsLiveTargetState.Absent()
            );
        }

        [Test]
        public void It_propagates_the_cancellation_instead_of_a_reclassified_failure()
        {
            _notRenamedOutcome.Should().BeAssignableTo<OperationCanceledException>();
            _renamedOutcome.Should().BeAssignableTo<OperationCanceledException>();
        }

        [Test]
        public void It_settles_the_rename_after_releasing_the_lock_before_the_caller_sees_the_cancellation()
        {
            foreach (Harness harness in new[] { _notRenamed, _renamed })
            {
                harness.OnlyHolder.ReleaseCount.Should().Be(1);
                AfterTheRename(harness)
                    .Select(call => call.Member)
                    .Should()
                    .StartWith([SpyDmsTargetEngine.TerminateSession, SpyDmsTargetEngine.Inspect]);
                harness.Engine.TerminatedSessions.Should().Contain(Session);
            }
        }

        [Test]
        public void It_drops_the_staging_database_the_rename_proved_untouched() =>
            CountOf(_notRenamed, SpyDmsTargetEngine.DropStaging).Should().Be(1);

        [Test]
        public void It_keeps_the_staging_name_alone_when_the_rename_happened() =>
            CountOf(_renamed, SpyDmsTargetEngine.DropStaging).Should().Be(0);
    }

    [TestFixture]
    public class Given_the_holder_is_lost_before_the_rename_is_dispatched
    {
        private Harness _harness = null!;
        private DmsDataStoreCreateResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            RecordingHooks hooks = new();
            _harness = Scripted(hooks);
            hooks.OnPoint = point =>
            {
                if (point == DmsProvisioningHookPoint.BeforeRename)
                {
                    _harness.OnlyHolder.Lose();
                }
            };
            _result = await CreateAsync(_harness);
        }

        [Test]
        public void It_ends_as_transient_lock_lost_at_the_rename() =>
            Verdict(_result)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.LockLost,
                        DmsProvisioningFailureCategory.Transient,
                        DmsDataStoreProvisioningService.RenameIntoTargetStep
                    )
                );

        [Test]
        public void It_sends_neither_the_rename_nor_the_marker() =>
            (
                CountOf(_harness, SpyDmsTargetEngine.RenameIntoTarget),
                CountOf(_harness, SpyDmsTargetEngine.AssignMarker)
            )
                .Should()
                .Be((0, 0));

        [Test]
        public void It_drops_the_staging_database_and_leaves_no_residue()
        {
            CountOf(_harness, SpyDmsTargetEngine.DropStaging).Should().Be(1);
            CleanupOf(_result).Residue.Should().BeEmpty();
        }
    }

    /// <summary>
    /// The rename sibling of the dispatch boundary: the engine runs the rename on the real holder's execution gate,
    /// the heartbeat probe holds the gate when the rename is handed to the engine, and the probe then finds the lock
    /// gone. The gate refuses the rename before it is invoked, so nothing is in doubt: the loss is transient, nothing
    /// is resolved after the lock, and cleanup drops the staging database, as for loss before the rename.
    /// </summary>
    [TestFixture]
    public class Given_the_holder_is_lost_while_the_rename_waits_for_the_holder_gate
    {
        private Harness _harness = null!;
        private DmsDataStoreCreateResult _result = null!;
        private int _renamesSent;

        [SetUp]
        public async Task Setup()
        {
            RecordingHooks hooks = new();
            _harness = Scripted(hooks);
            TaskCompletionSource<bool> probe = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
            hooks.OnPoint = point =>
            {
                if (point == DmsProvisioningHookPoint.BeforeRename)
                {
                    // The heartbeat probe takes the gate and keeps it until the test completes it.
                    _harness.OnlyHolder.Probe = probeToken => probe.Task;
                    _harness.OnlyHolder.StartHeartbeat();
                    _harness.Time.Advance(TimeSpan.FromSeconds(5));
                }
            };
            _harness.Engine.OnRenameIntoTarget = (_, instance, _, token) =>
            {
                waiting.SetResult();
                return _harness.OnlyHolder.RunAsync<DmsCommandOutcome<DmsInstanceIdentity>>(
                    _ =>
                    {
                        _renamesSent++;
                        return Task.FromResult<DmsCommandOutcome<DmsInstanceIdentity>>(
                            new DmsCommandOutcome<DmsInstanceIdentity>.Succeeded(instance)
                        );
                    },
                    CommandTimeout,
                    token
                );
            };
            Task<DmsDataStoreCreateResult> create = CreateAsync(_harness);
            await ReadStartedAsync(waiting, create);
            probe.SetResult(false);
            _result = await create;
        }

        [Test]
        public void It_ends_as_transient_lock_lost_at_the_rename() =>
            Verdict(_result)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.LockLost,
                        DmsProvisioningFailureCategory.Transient,
                        DmsDataStoreProvisioningService.RenameIntoTargetStep
                    )
                );

        [Test]
        public void It_never_sends_the_rename_and_resolves_nothing_after_the_lock()
        {
            _renamesSent.Should().Be(0);
            AfterTheRename(_harness)
                .Select(call => call.Member)
                .Should()
                .Equal(SpyDmsTargetEngine.DropStaging);
        }

        [Test]
        public void It_drops_the_staging_database_and_leaves_no_residue() =>
            CleanupOf(_result).Residue.Should().BeEmpty();
    }

    /// <summary>
    /// The heartbeat probe takes the holder gate and keeps it until <paramref name="probe"/> completes, as it would
    /// when a heartbeat falls due just before the next holder command.
    /// </summary>
    private static void HoldGateWithProbe(Harness harness, TaskCompletionSource<bool> probe)
    {
        harness.OnlyHolder.Probe = _ => probe.Task;
        harness.OnlyHolder.StartHeartbeat();
        harness.Time.Advance(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// The rename's deadline starts before the engine waits for the holder gate. The heartbeat probe holds the gate
    /// for its whole bound (one <c>CommandTimeout</c>) when the rename asks for it, and the rename then runs under the
    /// PostgreSQL holder bound (<see cref="PostgresqlDmsTargetEngine.RenameDeadline"/>) to its last second. The
    /// orchestrator's allowance covers the gate wait, so nothing cancels the rename before the provider's bound;
    /// the rename still ends at that bound.
    /// </summary>
    [TestFixture]
    public class Given_the_rename_waits_for_the_holder_gate_and_then_uses_its_whole_bound
    {
        private bool _cancelledWithinTheBound;
        private bool _cancelledPastTheBound;
        private DmsDataStoreCreateResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            TimeSpan bound = PostgresqlDmsTargetEngine.RenameDeadline(CommandTimeout);
            RecordingHooks hooks = new();
            Harness harness = Scripted(hooks);
            TaskCompletionSource<bool> probe = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource running = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken renameToken = default;
            hooks.OnPoint = point =>
            {
                if (point == DmsProvisioningHookPoint.BeforeRename)
                {
                    HoldGateWithProbe(harness, probe);
                }
            };
            harness.Engine.OnRenameIntoTarget = (_, instance, _, token) =>
            {
                waiting.SetResult();
                return harness.OnlyHolder.RunAsync<DmsCommandOutcome<DmsInstanceIdentity>>(
                    async holderToken =>
                    {
                        renameToken = holderToken;
                        running.SetResult();
                        await finish.Task;
                        return new DmsCommandOutcome<DmsInstanceIdentity>.Succeeded(instance);
                    },
                    bound,
                    token
                );
            };
            Task<DmsDataStoreCreateResult> create = CreateAsync(harness);
            await ReadStartedAsync(waiting, create);

            harness.Time.Advance(CommandTimeout);
            probe.SetResult(true);
            await ReadStartedAsync(running, create);

            harness.Time.Advance(bound - TimeSpan.FromSeconds(1));
            _cancelledWithinTheBound = renameToken.IsCancellationRequested;
            harness.Time.Advance(TimeSpan.FromSeconds(2));
            _cancelledPastTheBound = renameToken.IsCancellationRequested;

            finish.SetResult();
            _result = await create;
        }

        [Test]
        public void It_does_not_cancel_the_rename_within_the_provider_bound() =>
            _cancelledWithinTheBound.Should().BeFalse();

        [Test]
        public void It_still_ends_the_rename_at_the_provider_bound() =>
            _cancelledPastTheBound.Should().BeTrue();

        [Test]
        public void It_publishes_the_renamed_target() =>
            _result.Should().BeOfType<DmsDataStoreCreateResult.Created>();
    }

    /// <summary>
    /// The compensation sibling: the marker fails definitely and the heartbeat probe takes the holder gate as it
    /// returns. The compensation drop then waits for the gate for the probe's whole bound and runs under the
    /// PostgreSQL holder bound (<see cref="PostgresqlDmsTargetEngine.DropDeadline"/> for a compensation) to its last
    /// second. The recovery allowance covers the gate wait, so the probe cannot leave the unmarked target behind; the
    /// drop still ends at the provider's bound.
    /// </summary>
    [TestFixture]
    public class Given_the_compensation_waits_for_the_holder_gate_and_then_uses_its_whole_bound
    {
        private bool _cancelledWithinTheBound;
        private bool _cancelledPastTheBound;
        private DmsDataStoreCreateResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            TimeSpan bound = PostgresqlDmsTargetEngine.DropDeadline(
                DmsDropReason.Compensation,
                CommandTimeout
            );
            Harness harness = Scripted();
            TaskCompletionSource<bool> probe = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource running = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken dropToken = default;
            harness.Engine.OnAssignMarker = (_, _, _) =>
            {
                HoldGateWithProbe(harness, probe);
                return Task.FromResult<DmsCommandOutcome>(
                    new DmsCommandOutcome.Failed(
                        new DmsCommandFailure(
                            DmsCommandFailureClass.Rejected,
                            DmsProvisioningDiagnostic.ForOperation("AssignMarker.NoRow")
                        )
                    )
                );
            };
            harness.Engine.OnDropInstance = (_, _, _, token) =>
            {
                waiting.SetResult();
                return harness.OnlyHolder.RunAsync<DmsCommandOutcome>(
                    async holderToken =>
                    {
                        dropToken = holderToken;
                        running.SetResult();
                        await finish.Task;
                        return new DmsCommandOutcome.Succeeded();
                    },
                    bound,
                    token
                );
            };
            Task<DmsDataStoreCreateResult> create = CreateAsync(harness);
            await ReadStartedAsync(waiting, create);

            harness.Time.Advance(CommandTimeout);
            probe.SetResult(true);
            await ReadStartedAsync(running, create);

            harness.Time.Advance(bound - TimeSpan.FromSeconds(1));
            _cancelledWithinTheBound = dropToken.IsCancellationRequested;
            harness.Time.Advance(TimeSpan.FromSeconds(2));
            _cancelledPastTheBound = dropToken.IsCancellationRequested;

            finish.SetResult();
            _result = await create;
        }

        [Test]
        public void It_does_not_cancel_the_compensation_within_the_provider_bound() =>
            _cancelledWithinTheBound.Should().BeFalse();

        [Test]
        public void It_still_ends_the_compensation_at_the_provider_bound() =>
            _cancelledPastTheBound.Should().BeTrue();

        [Test]
        public void It_compensates_and_reports_the_marker_failure() =>
            Verdict(_result)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.IdentityAssignmentFailed,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        "AssignMarker.NoRow"
                    )
                );
    }

    [TestFixture]
    public class Given_the_holder_is_lost_as_the_rename_returns
    {
        private Harness _harness = null!;
        private DmsDataStoreCreateResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = Scripted();
            _harness.Engine.OnRenameIntoTarget = (_, instance, _, _) =>
            {
                _harness.OnlyHolder.Lose();
                return Task.FromResult<DmsCommandOutcome<DmsInstanceIdentity>>(
                    new DmsCommandOutcome<DmsInstanceIdentity>.Succeeded(instance)
                );
            };
            _result = await CreateAsync(_harness);
        }

        [Test]
        public void It_ends_as_lock_lost_for_inspection_because_the_target_was_renamed() =>
            Verdict(_result)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.LockLost,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        DmsDataStoreProvisioningService.RenameIntoTargetStep
                    )
                );

        [Test]
        public void It_reports_the_unmarked_target_and_never_compensates_without_the_lock()
        {
            CleanupOf(_result)
                .Residue.Should()
                .Equal(new DmsCleanupResidue(DmsResidueKind.UnmarkedTarget, TargetName));
            AfterTheRename(_harness).Should().BeEmpty();
        }
    }

    [TestFixture]
    public class Given_the_marker_fails_definitely
    {
        private Harness _compensated = null!;
        private Harness _compensationFailed = null!;
        private Harness _compensationRefused = null!;
        private DmsDataStoreCreateResult _compensatedResult = null!;
        private DmsDataStoreCreateResult _compensationFailedResult = null!;
        private DmsDataStoreCreateResult _compensationRefusedResult = null!;
        private (DmsInstanceIdentity Instance, DmsDropReason Reason, bool MarkerSessionClosed)? _drop;

        private static DmsCommandOutcome Failed(DmsCommandFailureClass failureClass, string operation) =>
            new DmsCommandOutcome.Failed(
                new DmsCommandFailure(failureClass, DmsProvisioningDiagnostic.ForOperation(operation))
            );

        private Harness Rejecting(DmsCommandOutcome compensation, bool recordDrop = false)
        {
            Harness harness = Scripted();
            FakeSession? markerSession = null;
            harness.Engine.OnOpenSession = (name, instance, _) =>
            {
                FakeSession session = new(instance, IsStaging(name) ? _stagingSession : _markerSession);
                markerSession = IsStaging(name) ? markerSession : session;
                return Task.FromResult<DmsSessionOpenResult>(new DmsSessionOpenResult.Opened(session));
            };
            harness.Engine.OnAssignMarker = (_, _, _) =>
                Task.FromResult(Failed(DmsCommandFailureClass.Rejected, "AssignMarker.NoRow"));
            harness.Engine.OnDropInstance = (_, instance, reason, _) =>
            {
                if (recordDrop)
                {
                    _drop = (instance, reason, markerSession!.Disposed);
                }

                return Task.FromResult(compensation);
            };
            return harness;
        }

        [SetUp]
        public async Task Setup()
        {
            _compensated = Rejecting(new DmsCommandOutcome.Succeeded(), recordDrop: true);
            _compensatedResult = await CreateAsync(_compensated);

            _compensationFailed = Rejecting(Failed(DmsCommandFailureClass.ObjectInUse, "DropInstance"));
            _compensationFailedResult = await CreateAsync(_compensationFailed);

            _compensationRefused = Rejecting(
                Failed(DmsCommandFailureClass.DoesNotExist, "DropInstance.InstanceChanged")
            );
            _compensationRefusedResult = await CreateAsync(_compensationRefused);
        }

        [Test]
        public void It_compensates_and_reports_the_marker_failure() =>
            Verdict(_compensatedResult)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.IdentityAssignmentFailed,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        "AssignMarker.NoRow"
                    )
                );

        [Test]
        public void It_drops_only_the_renamed_instance_as_compensation_after_closing_the_marker_session() =>
            _drop.Should().Be(((DmsInstanceIdentity)_stagingInstance, DmsDropReason.Compensation, true));

        [Test]
        public void It_leaves_nothing_behind_after_compensation()
        {
            CleanupOf(_compensatedResult).HasResidue.Should().BeFalse();
            AfterTheRename(_compensated)
                .Select(call => call.Member)
                .Should()
                .Equal(
                    SpyDmsTargetEngine.OpenSession,
                    SpyDmsTargetEngine.ReadIdentity,
                    SpyDmsTargetEngine.ReadTargetFacts,
                    SpyDmsTargetEngine.AssignMarker,
                    SpyDmsTargetEngine.DropInstance
                );
        }

        [Test]
        public void It_reports_a_failed_compensation_with_the_target_as_residue()
        {
            Verdict(_compensationFailedResult)
                .Item1.Should()
                .Be(DmsProvisioningFailureCode.CompensationFailed);
            CleanupOf(_compensationFailedResult)
                .Residue.Should()
                .Equal(new DmsCleanupResidue(DmsResidueKind.UnmarkedTarget, TargetName));
        }

        [Test]
        public void It_reports_a_refused_compensation_as_an_instance_change_without_residue()
        {
            Verdict(_compensationRefusedResult).Item1.Should().Be(DmsProvisioningFailureCode.InstanceChanged);
            CleanupOf(_compensationRefusedResult).HasResidue.Should().BeFalse();
        }
    }

    [TestFixture]
    public class Given_the_marker_outcome_is_unknown
    {
        private Harness _committed = null!;
        private Harness _uncommitted = null!;
        private Harness _unreadable = null!;
        private Harness _sessionUnproven = null!;
        private DmsDataStoreCreateResult _committedResult = null!;
        private DmsDataStoreCreateResult _uncommittedResult = null!;
        private DmsDataStoreCreateResult _unreadableResult = null!;
        private DmsDataStoreCreateResult _sessionUnprovenResult = null!;

        private static Harness InDoubt(DmsIdentityRead afterTheMarker, bool markerSessionGone = true)
        {
            Harness harness = Scripted();
            harness.Engine.OnAssignMarker = (session, _, _) =>
                Task.FromResult<DmsCommandOutcome>(new DmsCommandOutcome.Unknown(session.ServerSession));
            ReadsAfterTheMarker(harness, afterTheMarker);
            harness.Engine.OnTerminateSession = (session, _, _) =>
                Task.FromResult<DmsSessionTermination>(
                    session == _markerSession && !markerSessionGone
                        ? new DmsSessionTermination.NotTerminated(DmsSessionTerminationFailure.TimedOut)
                        : new DmsSessionTermination.Terminated()
                );
            return harness;
        }

        [SetUp]
        public async Task Setup()
        {
            _committed = InDoubt(Identity(_expected));
            _committedResult = await CreateAsync(_committed);

            _uncommitted = InDoubt(TargetFactsSample.Identity());
            _uncommittedResult = await CreateAsync(_uncommitted);

            _unreadable = InDoubt(new DmsIdentityRead.Invalid(DmsIdentityReadProblem.MultipleRows));
            _unreadableResult = await CreateAsync(_unreadable);

            _sessionUnproven = InDoubt(Identity(_expected), markerSessionGone: false);
            _sessionUnprovenResult = await CreateAsync(_sessionUnproven);
        }

        [Test]
        public void It_returns_created_when_the_re_read_finds_the_expected_identity()
        {
            _committedResult.Should().BeOfType<DmsDataStoreCreateResult.Created>();
            _committed.Engine.TerminatedSessions.Should().Contain(_markerSession);
        }

        [Test]
        public void It_terminates_the_marker_session_before_re_reading_on_a_new_session() =>
            AfterTheRename(_committed)
                .Select(call => call.Member)
                .Should()
                .EndWith([
                    SpyDmsTargetEngine.AssignMarker,
                    SpyDmsTargetEngine.TerminateSession,
                    SpyDmsTargetEngine.OpenSession,
                    SpyDmsTargetEngine.ReadIdentity,
                ]);

        [Test]
        public void It_compensates_when_the_re_read_finds_the_package_identity()
        {
            Verdict(_uncommittedResult)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.IdentityAssignmentFailed,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        DmsDataStoreProvisioningService.ResolveMarkerStep
                    )
                );
            CountOf(_uncommitted, SpyDmsTargetEngine.DropInstance).Should().Be(1);
        }

        [Test]
        public void It_leaves_any_other_read_unresolved_without_compensating()
        {
            Verdict(_unreadableResult)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.OutcomeUnknown,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        DmsDataStoreProvisioningService.ResolveMarkerStep
                    )
                );
            CountOf(_unreadable, SpyDmsTargetEngine.DropInstance).Should().Be(0);
        }

        [Test]
        public void It_reads_nothing_while_the_marker_session_is_not_proven_gone()
        {
            Verdict(_sessionUnprovenResult).Item1.Should().Be(DmsProvisioningFailureCode.OutcomeUnknown);
            CountOf(_sessionUnproven, SpyDmsTargetEngine.OpenSession).Should().Be(2);
            CountOf(_sessionUnproven, SpyDmsTargetEngine.DropInstance).Should().Be(0);
        }
    }

    /// <summary>
    /// 6.9 <c>OutcomeResolutionAttempts</c>: the marker is in doubt and its session proven gone, but the re-read
    /// on a new session throws. With the default of three, one transient failure is followed by a re-read that
    /// finds the committed marker; with a limit of two and every re-read failing, exactly two are made and the
    /// outcome stays unresolved, never compensated.
    /// </summary>
    [TestFixture]
    public class Given_the_marker_re_read_fails
    {
        private Harness _transient = null!;
        private Harness _persistent = null!;
        private DmsDataStoreCreateResult _transientResult = null!;
        private DmsDataStoreCreateResult _persistentResult = null!;

        /// <summary>Identity reads on target sessions after the 6.3 step 9 re-check throw <paramref name="failures"/> times, then find the expected value.</summary>
        private static Harness InDoubt(
            int failures,
            Action<DmsDataStoreProvisioningOptions>? configure = null
        )
        {
            Harness harness = Scripted(configure: configure);
            harness.Engine.OnAssignMarker = (session, _, _) =>
                Task.FromResult<DmsCommandOutcome>(new DmsCommandOutcome.Unknown(session.ServerSession));
            int targetReads = 0;
            harness.Engine.OnReadIdentity = (session, _) =>
            {
                if (session.ServerSession != _markerSession || ++targetReads == 1)
                {
                    return Task.FromResult<DmsIdentityRead>(TargetFactsSample.Identity());
                }

                return targetReads - 1 <= failures
                    ? throw new TestConnectionException()
                    : Task.FromResult(Identity(_expected));
            };
            return harness;
        }

        /// <summary>Identity reads after the marker: every read on a target session but the re-check.</summary>
        private static int RecoveryReads(Harness harness) =>
            AfterTheRename(harness).Count(call => call.Member == SpyDmsTargetEngine.ReadIdentity) - 1;

        [SetUp]
        public async Task Setup()
        {
            _transient = InDoubt(failures: 1);
            _transientResult = await CreateAsync(_transient);

            _persistent = InDoubt(failures: int.MaxValue, options => options.OutcomeResolutionAttempts = 2);
            _persistentResult = await CreateAsync(_persistent);
        }

        [Test]
        public void It_returns_created_after_one_transient_failure()
        {
            _transientResult.Should().BeOfType<DmsDataStoreCreateResult.Created>();
            RecoveryReads(_transient).Should().Be(2);
        }

        [Test]
        public void It_stops_at_the_configured_limit_unresolved_and_uncompensated()
        {
            Verdict(_persistentResult)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.OutcomeUnknown,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        DmsDataStoreProvisioningService.ResolveMarkerStep
                    )
                );
            (RecoveryReads(_persistent), CountOf(_persistent, SpyDmsTargetEngine.DropInstance))
                .Should()
                .Be((2, 0));
        }
    }

    [TestFixture]
    public class Given_the_client_loses_the_marker_result_after_the_commit
    {
        private Harness _harness = null!;
        private RecordingHooks _hooks = null!;
        private DmsDataStoreCreateResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _hooks = new RecordingHooks();
            _hooks.Faults.Add(DmsProvisioningFaultPoint.FailAfterMarkerExecuted);
            _harness = Scripted(_hooks);
            ReadsAfterTheMarker(_harness, Identity(_expected));
            _result = await CreateAsync(_harness);
        }

        [Test]
        public void It_resolves_the_lost_result_as_committed() =>
            (
                _result is DmsDataStoreCreateResult.Created,
                CountOf(_harness, SpyDmsTargetEngine.TerminateSession)
            )
                .Should()
                .Be((true, 2));
    }

    [TestFixture]
    public class Given_the_renamed_instance_is_gone_before_the_marker_session_opens
    {
        private Harness _harness = null!;
        private RecordingHooks _hooks = null!;
        private DmsDataStoreCreateResult _result = null!;
        private int _opensWhenBeforeMarkerPassed = -1;

        [SetUp]
        public async Task Setup()
        {
            _hooks = new RecordingHooks();
            _harness = Scripted(_hooks);
            _hooks.OnPoint = point =>
            {
                if (point == DmsProvisioningHookPoint.BeforeMarker)
                {
                    _opensWhenBeforeMarkerPassed = CountOf(_harness, SpyDmsTargetEngine.OpenSession);
                }
            };
            _harness.Engine.OnOpenSession = (name, instance, _) =>
                Task.FromResult<DmsSessionOpenResult>(
                    IsStaging(name)
                        ? new DmsSessionOpenResult.Opened(new FakeSession(instance, _stagingSession))
                        : new DmsSessionOpenResult.InstanceChanged()
                );
            _result = await CreateAsync(_harness);
        }

        [Test]
        public void It_passes_before_marker_after_the_rename_and_before_the_marker_session_opens() =>
            _opensWhenBeforeMarkerPassed.Should().Be(1);

        [Test]
        public void It_reports_the_instance_change_for_inspection() =>
            Verdict(_result)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.InstanceChanged,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        DmsDataStoreProvisioningService.OpenTargetSessionStep
                    )
                );

        [Test]
        public void It_neither_marks_nor_compensates_and_reports_nothing_under_the_target()
        {
            AfterTheRename(_harness)
                .Select(call => call.Member)
                .Should()
                .Equal(SpyDmsTargetEngine.OpenSession);
            CleanupOf(_result).HasResidue.Should().BeFalse();
        }
    }

    [TestFixture]
    public class Given_the_renamed_target_fails_its_re_check
    {
        private Harness _identityChanged = null!;
        private DmsDataStoreCreateResult _identityChangedResult = null!;
        private DmsDataStoreCreateResult _schemaChangedResult = null!;

        [SetUp]
        public async Task Setup()
        {
            _identityChanged = Scripted();
            _identityChanged.Engine.OnReadIdentity = (session, _) =>
                Task.FromResult<DmsIdentityRead>(
                    session.ServerSession == _markerSession
                        ? Identity(_otherIdentity)
                        : TargetFactsSample.Identity()
                );
            _identityChangedResult = await CreateAsync(_identityChanged);

            Harness schemaChanged = Scripted();
            schemaChanged.Engine.OnReadTargetFacts = (session, _) =>
                Task.FromResult(
                    session.ServerSession == _markerSession
                        ? TargetFactsSample.PostgresqlFacts() with
                        {
                            EffectiveSchema = new DmsEffectiveSchemaRead.Invalid(
                                DmsEffectiveSchemaReadProblem.NoRows
                            ),
                        }
                        : TargetFactsSample.PostgresqlFacts()
                );
            _schemaChangedResult = await CreateAsync(schemaChanged);
        }

        [Test]
        public void It_reports_a_changed_identity_as_an_instance_change() =>
            Verdict(_identityChangedResult)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.InstanceChanged,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        DmsDataStoreProvisioningService.VerifyRenamedTargetStep
                            + "."
                            + DmsTargetValidator.IdentityCheck
                    )
                );

        [Test]
        public void It_reports_a_changed_effective_schema_as_an_instance_change() =>
            Verdict(_schemaChangedResult)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.InstanceChanged,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        DmsDataStoreProvisioningService.VerifyRenamedTargetStep
                            + "."
                            + DmsTargetValidator.EffectiveSchemaCheck
                    )
                );

        [Test]
        public void It_neither_marks_nor_compensates_and_reports_the_unmarked_target()
        {
            CountOf(_identityChanged, SpyDmsTargetEngine.AssignMarker).Should().Be(0);
            CountOf(_identityChanged, SpyDmsTargetEngine.DropInstance).Should().Be(0);
            CleanupOf(_identityChangedResult)
                .Residue.Should()
                .Equal(new DmsCleanupResidue(DmsResidueKind.UnmarkedTarget, TargetName));
        }
    }

    [TestFixture]
    public class Given_the_caller_cancels_while_the_marker_is_dispatched
    {
        private Harness _uncommitted = null!;
        private Exception? _uncommittedOutcome;
        private DmsDataStoreCreateResult? _committedResult;

        /// <summary>The caller cancels as the marker is dispatched; the engine reports the cancellation before sending anything.</summary>
        private static Harness Cancelling(
            CancellationTokenSource cancellation,
            DmsIdentityRead afterTheMarker
        )
        {
            Harness harness = Scripted();
            harness.Engine.OnAssignMarker = (_, _, _) =>
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            };
            ReadsAfterTheMarker(harness, afterTheMarker);
            return harness;
        }

        [SetUp]
        public async Task Setup()
        {
            using CancellationTokenSource uncommittedCancellation = new();
            _uncommitted = Cancelling(uncommittedCancellation, TargetFactsSample.Identity());
            _uncommittedOutcome = await Record.ExceptionAsync(() =>
                CreateAsync(_uncommitted, uncommittedCancellation.Token)
            );

            using CancellationTokenSource committedCancellation = new();
            Harness committed = Cancelling(committedCancellation, Identity(_expected));
            _committedResult = await CreateAsync(committed, committedCancellation.Token);
        }

        [Test]
        public void It_resolves_and_compensates_before_the_cancellation_propagates()
        {
            _uncommittedOutcome.Should().BeAssignableTo<OperationCanceledException>();
            CountOf(_uncommitted, SpyDmsTargetEngine.DropInstance).Should().Be(1);
            _uncommitted.OnlyHolder.ReleaseCount.Should().Be(1);
        }

        [Test]
        public void It_returns_created_despite_the_cancellation_when_the_marker_committed() =>
            _committedResult.Should().BeOfType<DmsDataStoreCreateResult.Created>();
    }

    /// <summary>
    /// The rename succeeded and the re-check passed, then loss is declared before the marker call: the scope
    /// refuses the call, so the marker never reaches the engine. Recovery is unavailable (the marker's
    /// session cannot be proven gone), so a marker wrongly recorded as in doubt would end
    /// <c>OutcomeUnknown</c> with a clean report; the target is a known renamed but unmarked one instead.
    /// </summary>
    [TestFixture]
    public class Given_the_holder_is_lost_before_the_marker_is_dispatched
    {
        private Harness _harness = null!;
        private DmsDataStoreCreateResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            RecordingHooks hooks = new();
            _harness = Scripted(hooks);
            hooks.OnPoint = point =>
            {
                if (point == DmsProvisioningHookPoint.BeforeMarkerDispatch)
                {
                    _harness.OnlyHolder.Lose();
                }
            };
            _harness.Engine.OnTerminateSession = (session, _, _) =>
                Task.FromResult<DmsSessionTermination>(
                    session == _markerSession
                        ? new DmsSessionTermination.NotTerminated(DmsSessionTerminationFailure.TimedOut)
                        : new DmsSessionTermination.Terminated()
                );
            _result = await CreateAsync(_harness);
        }

        [Test]
        public void It_ends_as_lock_lost_for_inspection_at_the_marker() =>
            Verdict(_result)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.LockLost,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        DmsDataStoreProvisioningService.AssignMarkerStep
                    )
                );

        [Test]
        public void It_never_sends_the_marker_or_compensates() =>
            (
                CountOf(_harness, SpyDmsTargetEngine.AssignMarker),
                CountOf(_harness, SpyDmsTargetEngine.DropInstance)
            )
                .Should()
                .Be((0, 0));

        [Test]
        public void It_never_tries_to_resolve_a_marker_that_was_not_sent() =>
            _harness.Engine.TerminatedSessions.Should().NotContain(_markerSession);

        [Test]
        public void It_reports_the_unmarked_target() =>
            CleanupOf(_result)
                .Residue.Should()
                .Equal(new DmsCleanupResidue(DmsResidueKind.UnmarkedTarget, TargetName));
    }

    /// <summary>
    /// The caller cancels during the last read of the re-check, which completes normally, so the scope stops the
    /// marker call before dispatch. There is no marker outcome to resolve: the cancellation propagates without a
    /// termination of the marker's session, a recovery read, or compensation, and the target is reported as
    /// renamed but unmarked.
    /// </summary>
    [TestFixture]
    public class Given_the_caller_cancels_before_the_marker_is_dispatched
    {
        private Harness _harness = null!;
        private Exception? _outcome;

        [SetUp]
        public async Task Setup()
        {
            using CancellationTokenSource cancellation = new();
            _harness = Scripted();
            _harness.Engine.OnReadTargetFacts = async (session, _) =>
            {
                if (session.ServerSession == _markerSession)
                {
                    await cancellation.CancelAsync();
                }

                return TargetFactsSample.PostgresqlFacts();
            };
            _outcome = await Record.ExceptionAsync(() => CreateAsync(_harness, cancellation.Token));
        }

        [Test]
        public void It_propagates_the_cancellation() =>
            _outcome.Should().BeAssignableTo<OperationCanceledException>();

        [Test]
        public void It_never_sends_the_marker_or_compensates() =>
            (
                CountOf(_harness, SpyDmsTargetEngine.AssignMarker),
                CountOf(_harness, SpyDmsTargetEngine.DropInstance)
            )
                .Should()
                .Be((0, 0));

        [Test]
        public void It_never_tries_to_resolve_a_marker_that_was_not_sent() =>
            (
                _harness.Engine.TerminatedSessions.Contains(_markerSession),
                CountOf(_harness, SpyDmsTargetEngine.OpenSession)
            )
                .Should()
                .Be((false, 2));

        [Test]
        public void It_reports_the_unmarked_target_and_releases_the_holder()
        {
            _harness
                .Logger.Entries.Should()
                .ContainSingle(entry => entry.EventId == DmsDataStoreProvisioningService.TargetResidue);
            _harness.OnlyHolder.ReleaseCount.Should().Be(1);
        }
    }

    /// <summary>
    /// The marker passes the scope's checks and is handed to the engine, whose provider finds its token cancelled
    /// before sending the statement (SQL Server's check before the batch) and reports it as not dispatched, once for
    /// loss and once for the caller's cancellation. Recovery is unavailable (the marker's session cannot be proven
    /// gone), so a marker still recorded as in doubt would end <c>OutcomeUnknown</c> without the residue; the
    /// refusal withdraws it, and the target is reported as renamed but unmarked.
    /// </summary>
    [TestFixture]
    public class Given_the_provider_refuses_the_marker_before_sending_it
    {
        private Harness _lost = null!;
        private DmsDataStoreCreateResult _lostResult = null!;
        private Harness _cancelled = null!;
        private Exception? _cancelledOutcome;

        private static Harness Refusing(Func<Harness, CancellationToken> cancel)
        {
            Harness harness = Scripted();
            harness.Engine.OnAssignMarker = (_, _, _) =>
                throw new DmsCommandNotDispatchedException(cancel(harness));
            harness.Engine.OnTerminateSession = (session, _, _) =>
                Task.FromResult<DmsSessionTermination>(
                    session == _markerSession
                        ? new DmsSessionTermination.NotTerminated(DmsSessionTerminationFailure.TimedOut)
                        : new DmsSessionTermination.Terminated()
                );
            return harness;
        }

        [SetUp]
        public async Task Setup()
        {
            _lost = Refusing(harness =>
            {
                harness.OnlyHolder.Lose();
                return harness.OnlyHolder.LostToken;
            });
            _lostResult = await CreateAsync(_lost);

            using CancellationTokenSource cancellation = new();
            _cancelled = Refusing(_ =>
            {
                cancellation.Cancel();
                return cancellation.Token;
            });
            _cancelledOutcome = await Record.ExceptionAsync(() =>
                CreateAsync(_cancelled, cancellation.Token)
            );
        }

        [Test]
        public void It_ends_as_lock_lost_for_inspection_at_the_marker_on_loss() =>
            Verdict(_lostResult)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.LockLost,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        DmsDataStoreProvisioningService.AssignMarkerStep
                    )
                );

        [Test]
        public void It_reports_the_unmarked_target_on_loss() =>
            CleanupOf(_lostResult)
                .Residue.Should()
                .Equal(new DmsCleanupResidue(DmsResidueKind.UnmarkedTarget, TargetName));

        [Test]
        public void It_propagates_the_caller_cancellation_and_reports_the_unmarked_target()
        {
            _cancelledOutcome.Should().BeAssignableTo<OperationCanceledException>();
            _cancelled
                .Logger.Entries.Should()
                .ContainSingle(entry => entry.EventId == DmsDataStoreProvisioningService.TargetResidue);
        }

        [Test]
        public void It_never_tries_to_resolve_or_compensate_a_marker_that_was_not_sent() =>
            new[] { _lost, _cancelled }
                .Select(harness =>
                    (
                        harness.Engine.TerminatedSessions.Contains(_markerSession),
                        CountOf(harness, SpyDmsTargetEngine.DropInstance)
                    )
                )
                .Should()
                .Equal((false, 0), (false, 0));
    }

    [TestFixture]
    public class Given_the_holder_is_lost_around_the_marker
    {
        private Harness _lostAfterCommit = null!;
        private Harness _lostInDoubtUncommitted = null!;
        private DmsDataStoreCreateResult _lostAfterCommitResult = null!;
        private DmsDataStoreCreateResult _lostInDoubtCommittedResult = null!;
        private DmsDataStoreCreateResult _lostInDoubtUncommittedResult = null!;

        private static Harness Losing(Func<DmsSession, DmsCommandOutcome> marker)
        {
            Harness harness = Scripted();
            harness.Engine.OnAssignMarker = (session, _, _) =>
            {
                harness.OnlyHolder.Lose();
                return Task.FromResult(marker(session));
            };
            return harness;
        }

        [SetUp]
        public async Task Setup()
        {
            _lostAfterCommit = Losing(_ => new DmsCommandOutcome.Succeeded());
            _lostAfterCommitResult = await CreateAsync(_lostAfterCommit);

            Harness lostInDoubtCommitted = Losing(session => new DmsCommandOutcome.Unknown(
                session.ServerSession
            ));
            ReadsAfterTheMarker(lostInDoubtCommitted, Identity(_expected));
            _lostInDoubtCommittedResult = await CreateAsync(lostInDoubtCommitted);

            _lostInDoubtUncommitted = Losing(session => new DmsCommandOutcome.Unknown(session.ServerSession));
            ReadsAfterTheMarker(_lostInDoubtUncommitted, TargetFactsSample.Identity());
            _lostInDoubtUncommittedResult = await CreateAsync(_lostInDoubtUncommitted);
        }

        [Test]
        public void It_returns_created_when_the_marker_committed_before_the_loss()
        {
            _lostAfterCommitResult.Should().BeOfType<DmsDataStoreCreateResult.Created>();
            _lostInDoubtCommittedResult.Should().BeOfType<DmsDataStoreCreateResult.Created>();
        }

        [Test]
        public void It_reports_an_uncommitted_marker_after_the_loss_without_compensating()
        {
            Verdict(_lostInDoubtUncommittedResult)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.LockLost,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        DmsDataStoreProvisioningService.AssignMarkerStep
                    )
                );
            CountOf(_lostInDoubtUncommitted, SpyDmsTargetEngine.DropInstance).Should().Be(0);
            CleanupOf(_lostInDoubtUncommittedResult)
                .Residue.Should()
                .Equal(new DmsCleanupResidue(DmsResidueKind.UnmarkedTarget, TargetName));
        }
    }

    [TestFixture]
    public class Given_the_server_refuses_the_admin_access_to_the_renamed_target
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
            _connectDenied.Engine.OnOpenSession = (name, instance, _) =>
                Task.FromResult<DmsSessionOpenResult>(
                    IsStaging(name)
                        ? new DmsSessionOpenResult.Opened(new FakeSession(instance, _stagingSession))
                        : new DmsSessionOpenResult.AccessDenied(_refusal)
                );
            _connectResult = await CreateAsync(_connectDenied);

            _identityDenied = Scripted();
            _identityDenied.Engine.OnReadIdentity = (session, _) =>
                Task.FromResult<DmsIdentityRead>(
                    session.ServerSession == _markerSession
                        ? new DmsIdentityRead.Invalid(DmsIdentityReadProblem.AccessDenied)
                        {
                            Diagnostic = _refusal,
                        }
                        : TargetFactsSample.Identity()
                );
            _identityResult = await CreateAsync(_identityDenied);

            _factsDenied = Scripted();
            _factsDenied.Engine.OnReadTargetFacts = (session, _) =>
                session.ServerSession == _markerSession
                    ? throw new DmsAccessDeniedException(_refusal)
                    : Task.FromResult(TargetFactsSample.PostgresqlFacts());
            _factsResult = await CreateAsync(_factsDenied);
        }

        [Test]
        public void It_reports_a_refused_facts_read_of_the_re_check_for_inspection() =>
            FailureOf(_factsResult)
                .Should()
                .Be(
                    DmsProvisioningFailure.Create(
                        DmsProvisioningFailureCode.IdentityAssignmentFailed,
                        new DmsProvisioningDiagnostic(
                            "Npgsql.PostgresException",
                            "42501",
                            DmsDataStoreProvisioningService.VerifyRenamedTargetStep
                        )
                    )
                );

        [Test]
        public void It_reports_the_unmarked_target_for_inspection_with_the_sanitized_diagnostic() =>
            (FailureOf(_connectResult), FailureOf(_identityResult))
                .Should()
                .Be(
                    (
                        DmsProvisioningFailure.Create(
                            DmsProvisioningFailureCode.IdentityAssignmentFailed,
                            new DmsProvisioningDiagnostic(
                                "Npgsql.PostgresException",
                                "42501",
                                DmsDataStoreProvisioningService.OpenTargetSessionStep
                            )
                        ),
                        DmsProvisioningFailure.Create(
                            DmsProvisioningFailureCode.IdentityAssignmentFailed,
                            new DmsProvisioningDiagnostic(
                                "Npgsql.PostgresException",
                                "42501",
                                DmsDataStoreProvisioningService.VerifyRenamedTargetStep
                            )
                        )
                    )
                );

        [Test]
        public void It_neither_marks_nor_compensates_and_reports_the_target()
        {
            foreach (
                (Harness harness, DmsDataStoreCreateResult result) in new[]
                {
                    (_connectDenied, _connectResult),
                    (_identityDenied, _identityResult),
                    (_factsDenied, _factsResult),
                }
            )
            {
                CleanupOf(result)
                    .Residue.Should()
                    .Equal(new DmsCleanupResidue(DmsResidueKind.UnmarkedTarget, TargetName));
                (
                    CountOf(harness, SpyDmsTargetEngine.AssignMarker),
                    CountOf(harness, SpyDmsTargetEngine.DropInstance)
                )
                    .Should()
                    .Be((0, 0));
            }
        }
    }

    [TestFixture]
    public class Given_a_read_of_the_re_check_fails_after_the_rename
    {
        private Harness _thrown = null!;
        private Harness _timedOut = null!;
        private Harness _cancelled = null!;
        private DmsDataStoreCreateResult _thrownResult = null!;
        private DmsDataStoreCreateResult _timedOutResult = null!;
        private Exception? _cancelledOutcome;

        [SetUp]
        public async Task Setup()
        {
            // The identity read on the renamed target throws a driver-like error.
            _thrown = Scripted();
            _thrown.Engine.OnReadIdentity = (session, _) =>
                session.ServerSession == _markerSession
                    ? throw new InvalidOperationException("The read failed.")
                    : Task.FromResult<DmsIdentityRead>(TargetFactsSample.Identity());
            _thrownResult = await CreateAsync(_thrown);

            // The facts read on the renamed target waits until its CommandTimeout deadline passes.
            _timedOut = Scripted();
            TaskCompletionSource waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _timedOut.Engine.OnReadTargetFacts = async (session, token) =>
            {
                if (session.ServerSession != _markerSession)
                {
                    return TargetFactsSample.PostgresqlFacts();
                }

                waiting.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return TargetFactsSample.PostgresqlFacts();
            };
            Task<DmsDataStoreCreateResult> timingOut = CreateAsync(_timedOut);
            await ReadStartedAsync(waiting, timingOut);
            _timedOut.Time.Advance(CommandTimeout + TimeSpan.FromSeconds(1));
            _timedOutResult = await timingOut;

            // The caller cancels during the re-check.
            using CancellationTokenSource cancellation = new();
            _cancelled = Scripted();
            _cancelled.Engine.OnReadIdentity = (session, _) =>
            {
                if (session.ServerSession != _markerSession)
                {
                    return Task.FromResult<DmsIdentityRead>(TargetFactsSample.Identity());
                }

                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            };
            _cancelledOutcome = await Record.ExceptionAsync(() =>
                CreateAsync(_cancelled, cancellation.Token)
            );
        }

        [Test]
        public void It_reports_a_thrown_read_for_inspection_with_its_diagnostic()
        {
            DmsProvisioningFailure failure = FailureOf(_thrownResult);
            (failure.Code, failure.Category, failure.Diagnostic.Operation)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.IdentityAssignmentFailed,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        DmsDataStoreProvisioningService.VerifyRenamedTargetStep
                    )
                );
            failure.Diagnostic.ExceptionTypeChain.Should().Be(typeof(InvalidOperationException).FullName);
        }

        [Test]
        public void It_reports_a_timed_out_read_for_inspection_with_its_diagnostic() =>
            Verdict(_timedOutResult)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.IdentityAssignmentFailed,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        DmsDataStoreProvisioningService.VerifyRenamedTargetStep
                    )
                );

        [Test]
        public void It_reports_the_unmarked_target_and_neither_marks_nor_compensates()
        {
            foreach (
                (Harness harness, DmsDataStoreCreateResult result) in new[]
                {
                    (_thrown, _thrownResult),
                    (_timedOut, _timedOutResult),
                }
            )
            {
                CleanupOf(result)
                    .Residue.Should()
                    .Equal(new DmsCleanupResidue(DmsResidueKind.UnmarkedTarget, TargetName));
                (
                    CountOf(harness, SpyDmsTargetEngine.AssignMarker),
                    CountOf(harness, SpyDmsTargetEngine.DropInstance)
                )
                    .Should()
                    .Be((0, 0));
            }
        }

        [Test]
        public void It_still_propagates_the_caller_cancellation_without_marking_or_compensating()
        {
            _cancelledOutcome.Should().BeAssignableTo<OperationCanceledException>();
            (
                CountOf(_cancelled, SpyDmsTargetEngine.AssignMarker),
                CountOf(_cancelled, SpyDmsTargetEngine.DropInstance)
            )
                .Should()
                .Be((0, 0));
        }
    }

    [TestFixture]
    public class Given_the_holder_is_lost_as_a_session_open_returns
    {
        private static readonly DmsServerSessionId _collisionSession = new(
            321,
            new DateTimeOffset(2026, 11, 6, 0, 0, 2, TimeSpan.Zero)
        );

        private readonly Dictionary<string, FakeSession> _discarded = [];
        private Harness _staging = null!;
        private Harness _publication = null!;
        private Harness _collision = null!;
        private DmsDataStoreCreateResult _stagingResult = null!;
        private DmsDataStoreCreateResult _publicationResult = null!;
        private DmsDataStoreCreateResult _collisionResult = null!;

        /// <summary>Declares loss as the open selected by <paramref name="losesOn"/> returns its session, and keeps that session.</summary>
        private void LoseOnOpen(
            Harness harness,
            string key,
            Func<DmsDatabaseName, bool> losesOn,
            DmsServerSessionId id
        )
        {
            harness.Engine.OnOpenSession = (name, instance, _) =>
            {
                if (!losesOn(name))
                {
                    return Task.FromResult<DmsSessionOpenResult>(
                        new DmsSessionOpenResult.Opened(
                            new FakeSession(instance, IsStaging(name) ? _stagingSession : _markerSession)
                        )
                    );
                }

                FakeSession session = new(instance, id);
                _discarded[key] = session;
                harness.OnlyHolder.Lose();
                return Task.FromResult<DmsSessionOpenResult>(new DmsSessionOpenResult.Opened(session));
            };
        }

        [SetUp]
        public async Task Setup()
        {
            _discarded.Clear();

            _staging = Scripted();
            LoseOnOpen(_staging, "staging", IsStaging, _stagingSession);
            _stagingResult = await CreateAsync(_staging);

            _publication = Scripted();
            LoseOnOpen(_publication, "publication", name => !IsStaging(name), _markerSession);
            _publicationResult = await CreateAsync(_publication);

            _collision = Scripted(targetInspection: inspection =>
                inspection <= 2 ? new DmsLiveTargetState.Absent() : PresentUnder(_foreignInstance)
            );
            _collision.Engine.OnRenameIntoTarget = (_, _, _, _) =>
                Task.FromResult<DmsCommandOutcome<DmsInstanceIdentity>>(
                    new DmsCommandOutcome<DmsInstanceIdentity>.Failed(
                        new DmsCommandFailure(
                            DmsCommandFailureClass.DuplicateName,
                            DmsProvisioningDiagnostic.ForOperation("RenameIntoTarget")
                        )
                    )
                );
            LoseOnOpen(_collision, "collision", name => !IsStaging(name), _collisionSession);
            _collisionResult = await CreateAsync(_collision);
        }

        [Test]
        public void It_disposes_each_discarded_session_exactly_once() =>
            _discarded
                .ToDictionary(entry => entry.Key, entry => entry.Value.Disposals)
                .Should()
                .BeEquivalentTo(
                    new Dictionary<string, int>
                    {
                        ["staging"] = 1,
                        ["publication"] = 1,
                        ["collision"] = 1,
                    }
                );

        [Test]
        public void It_ends_each_as_lock_lost_at_the_open() =>
            new[] { _stagingResult, _publicationResult, _collisionResult }
                .Select(result => (FailureOf(result).Code, FailureOf(result).Diagnostic.Operation))
                .Should()
                .Equal(
                    (
                        DmsProvisioningFailureCode.LockLost,
                        DmsDataStoreProvisioningService.OpenStagingSessionStep
                    ),
                    (
                        DmsProvisioningFailureCode.LockLost,
                        DmsDataStoreProvisioningService.OpenTargetSessionStep
                    ),
                    (
                        DmsProvisioningFailureCode.LockLost,
                        DmsDataStoreProvisioningService.ReconcileOpenSessionStep
                    )
                );

        [Test]
        public void It_reads_nothing_on_a_discarded_session_and_issues_no_further_mutation()
        {
            foreach (Harness harness in new[] { _staging, _publication, _collision })
            {
                IReadOnlyList<string> members = harness.Engine.Members;
                int open = members.ToList().LastIndexOf(SpyDmsTargetEngine.OpenSession);
                members
                    .Skip(open + 1)
                    .Should()
                    .NotContain([
                        SpyDmsTargetEngine.ReadIdentity,
                        SpyDmsTargetEngine.ReadTargetFacts,
                        SpyDmsTargetEngine.RenameIntoTarget,
                        SpyDmsTargetEngine.AssignMarker,
                        SpyDmsTargetEngine.DropInstance,
                    ]);
            }
        }
    }
}
