// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Packaging;
using FluentAssertions;
using static EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.ProvisioningTestValues;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning;

/// <summary>
/// The orchestrator's owned delete (6.6, Step 3.8) over the spy engine: an owned target is opened, fenced
/// with only our session kept, re-verified under the fence, released, and dropped; a target that is not
/// owned, not readable, or not online is refused with nothing mutated; every failure after the fence
/// reverses it first, and a reversal that does not succeed leaves the target reported as fenced for
/// inspection; outcomes in doubt, loss, and the caller's cancellation are settled after the lock is released
/// by reading state only, and never by a further mutation.
/// </summary>
public class DmsDataStoreProvisioningServiceDeleteTests
{
    private static readonly Guid _expected = Guid.Parse("5e6f7081-92a3-44b5-86c7-d8e9f0a1b2c3");
    private static readonly DmsInstanceIdentity.Postgresql _instance = new(16385, "");
    private static readonly DmsInstanceIdentity.Postgresql _otherInstance = new(16999, "");

    private static readonly DmsServerSessionId _targetSession = new(
        8,
        new DateTimeOffset(2026, 11, 5, 0, 0, 1, TimeSpan.Zero)
    );

    private static readonly DmsDataStoreDeleteRequest _request = new(Name("EdFi_Ods_Test"), _expected);

    private static readonly string[] _owned =
    [
        SpyDmsTargetEngine.AcquireTargetLock,
        SpyDmsTargetEngine.ReadServerFacts,
        SpyDmsTargetEngine.Inspect,
        SpyDmsTargetEngine.OpenSession,
        SpyDmsTargetEngine.ReadIdentity,
    ];

    private static readonly string[] _fencedAndVerified =
    [
        .. _owned,
        SpyDmsTargetEngine.Fence,
        SpyDmsTargetEngine.ReadIdentity,
        SpyDmsTargetEngine.Inspect,
    ];

    private static readonly string[] _released = [.. _fencedAndVerified, SpyDmsTargetEngine.TerminateSession];

    private static readonly string[] _settlementReads =
    [
        SpyDmsTargetEngine.TerminateSession,
        SpyDmsTargetEngine.Inspect,
    ];

    private static readonly DmsProvisioningDiagnostic _engineDiagnostic = new(
        "Npgsql.PostgresException",
        "42501",
        "Fence.Exclusivity"
    );

    private static DmsCommandOutcome FencedFailure(DmsCommandFailureClass failureClass) =>
        new DmsCommandOutcome.Failed(new DmsCommandFailure(failureClass, _engineDiagnostic))
        {
            Residue = [new DmsCleanupResidue(DmsResidueKind.FencedTarget, "EdFi_Ods_Test")],
        };

    private static DmsCommandOutcome Failed(DmsCommandFailureClass failureClass, string operation) =>
        new DmsCommandOutcome.Failed(
            new DmsCommandFailure(failureClass, DmsProvisioningDiagnostic.ForOperation(operation))
        );

    private sealed class FakeSession(DmsInstanceIdentity instance) : DmsSession(instance, _targetSession)
    {
        private int _disposals;

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

        public Func<DmsProvisioningHookPoint, CancellationToken, Task>? Probe { get; set; }

        public async ValueTask AtAsync(
            DmsProvisioningHookPoint point,
            DmsOperationId operation,
            CancellationToken cancellationToken
        )
        {
            Points.Add(point);
            if (Probe is not null)
            {
                await Probe(point, cancellationToken);
            }
        }

        public bool ShouldInjectFault(DmsProvisioningFaultPoint point, DmsOperationId operation) => false;
    }

    /// <summary>
    /// A server holding an owned, online target under <see cref="_instance"/>: the fence, the unfence, and
    /// the drop change what inspection reports; our session reads the expected identity; every termination
    /// is proven. Each script can be replaced by a fixture.
    /// </summary>
    private sealed class OwnedTarget
    {
        public OwnedTarget()
        {
            Harness = new Harness(hooks: Hooks);
            State = new DmsLiveTargetState.Present(_instance, DmsDatabaseState.Online);
            Harness.Engine.OnInspectByName = (_, _) => Task.FromResult(State);
            Harness.Engine.OnOpenSession = (_, instance, _) =>
            {
                FakeSession session = new(instance);
                Sessions.Add(session);
                return Task.FromResult<DmsSessionOpenResult>(new DmsSessionOpenResult.Opened(session));
            };
            Harness.Engine.OnReadIdentity = (_, _) =>
                Task.FromResult<DmsIdentityRead>(new DmsIdentityRead.Found(_expected));
            Harness.Engine.OnFence = (_, _, keep, _) =>
            {
                Kept = keep;
                State = new DmsLiveTargetState.Fenced(_instance);
                return Task.FromResult<DmsCommandOutcome>(new DmsCommandOutcome.Succeeded());
            };
            Harness.Engine.OnUnfence = (_, _, _) =>
            {
                State = new DmsLiveTargetState.Present(_instance, DmsDatabaseState.Online);
                return Task.FromResult<DmsCommandOutcome>(new DmsCommandOutcome.Succeeded());
            };
            Harness.Engine.OnTerminateSession = (_, _, _) =>
                Task.FromResult<DmsSessionTermination>(new DmsSessionTermination.Terminated());
            Harness.Engine.OnDropInstance = (_, _, reason, _) =>
            {
                DropReason = reason;
                DisposalsAtDrop = Sessions.Sum(session => session.Disposals);
                State = new DmsLiveTargetState.Absent();
                return Task.FromResult<DmsCommandOutcome>(new DmsCommandOutcome.Succeeded());
            };
        }

        public RecordingHooks Hooks { get; } = new();

        public Harness Harness { get; }

        public DmsLiveTargetState State { get; set; }

        public List<FakeSession> Sessions { get; } = [];

        public DmsServerSessionId? Kept { get; private set; }

        public DmsDropReason? DropReason { get; private set; }

        public int DisposalsAtDrop { get; private set; } = -1;

        public IReadOnlyList<string> Members => Harness.Engine.Members;

        public int SessionDisposals => Sessions.Single().Disposals;

        public int Releases => Harness.OnlyHolder.ReleaseCount;

        /// <summary>The identity read the n-th time (1-based); the expected identity otherwise.</summary>
        public void IdentityOnRead(int read, Func<DmsIdentityRead> identity)
        {
            int reads = 0;
            Harness.Engine.OnReadIdentity = (_, _) =>
                Task.FromResult(++reads == read ? identity() : new DmsIdentityRead.Found(_expected));
        }

        public Task<DmsDataStoreDeleteResult> DeleteAsync(CancellationToken cancellationToken = default) =>
            Harness.Service.DeleteAsync(_request, cancellationToken);
    }

    private static (DmsProvisioningFailureCode, DmsProvisioningFailureCategory, string) Verdict(
        DmsDataStoreDeleteResult result
    )
    {
        DmsProvisioningFailure failure = FailureOf(result);
        return (failure.Code, failure.Category, failure.Diagnostic.Operation);
    }

    private static IReadOnlyList<DmsCleanupResidue> ResidueOf(DmsDataStoreDeleteResult result) =>
        result.Should().BeOfType<DmsDataStoreDeleteResult.Failure>().Subject.Cleanup.Residue;

    private static IEnumerable<string> Seq(string[] members) => members;

    private static readonly DmsCleanupResidue _fencedResidue = new(
        DmsResidueKind.FencedTarget,
        "EdFi_Ods_Test"
    );

    [TestFixture]
    public class Given_an_owned_target_is_deleted
    {
        private OwnedTarget _target = null!;
        private DmsDataStoreDeleteResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _target = new OwnedTarget();
            _result = await _target.DeleteAsync();
        }

        [Test]
        public void It_drops_the_target_with_nothing_left_behind() =>
            _result
                .Should()
                .BeOfType<DmsDataStoreDeleteResult.Dropped>()
                .Which.Cleanup.HasResidue.Should()
                .BeFalse();

        [Test]
        public void It_reads_ownership_fences_reverifies_releases_and_drops_in_that_order() =>
            _target.Members.Should().Equal(Seq([.. _released, SpyDmsTargetEngine.DropInstance]));

        [Test]
        public void It_keeps_only_its_own_target_session_through_the_fence() =>
            _target.Kept.Should().Be(_targetSession);

        [Test]
        public void It_proves_its_own_session_gone_before_the_drop() =>
            _target.Harness.Engine.TerminatedSessions.Should().Equal(_targetSession);

        [Test]
        public void It_closes_the_session_once_before_the_drop() =>
            (_target.DisposalsAtDrop, _target.SessionDisposals).Should().Be((1, 1));

        [Test]
        public void It_drops_as_an_owned_delete() => _target.DropReason.Should().Be(DmsDropReason.Delete);

        [Test]
        public void It_passes_the_fence_and_drop_hooks_in_order() =>
            _target
                .Hooks.Points.Should()
                .Equal(DmsProvisioningHookPoint.BeforeFence, DmsProvisioningHookPoint.BeforeDrop);

        [Test]
        public void It_addresses_only_the_selected_target() =>
            _target
                .Harness.Engine.Calls.Where(call => call.DatabaseName is not null)
                .Select(call => call.DatabaseName)
                .Should()
                .AllBe("EdFi_Ods_Test");

        [Test]
        public void It_releases_the_lock() => _target.Releases.Should().Be(1);
    }

    [TestFixture]
    public class Given_the_target_is_not_owned_or_cannot_be_verified
    {
        private OwnedTarget _unowned = null!;
        private OwnedTarget _missing = null!;
        private OwnedTarget _denied = null!;
        private OwnedTarget _refusedOpen = null!;
        private OwnedTarget _changedAtOpen = null!;
        private OwnedTarget _notOnline = null!;
        private DmsDataStoreDeleteResult _unownedResult = null!;
        private DmsDataStoreDeleteResult _missingResult = null!;
        private DmsDataStoreDeleteResult _deniedResult = null!;
        private DmsDataStoreDeleteResult _refusedOpenResult = null!;
        private DmsDataStoreDeleteResult _changedAtOpenResult = null!;
        private DmsDataStoreDeleteResult _notOnlineResult = null!;
        private readonly DmsProvisioningDiagnostic _refusal = new(
            "Npgsql.PostgresException",
            "42501",
            "ReadIdentity"
        );

        [SetUp]
        public async Task Setup()
        {
            _unowned = new OwnedTarget();
            _unowned.IdentityOnRead(1, () => new DmsIdentityRead.Found(Guid.NewGuid()));
            _unownedResult = await _unowned.DeleteAsync();

            _missing = new OwnedTarget();
            _missing.IdentityOnRead(
                1,
                () => new DmsIdentityRead.Invalid(DmsIdentityReadProblem.TableMissing)
            );
            _missingResult = await _missing.DeleteAsync();

            _denied = new OwnedTarget();
            _denied.IdentityOnRead(
                1,
                () =>
                    new DmsIdentityRead.Invalid(DmsIdentityReadProblem.AccessDenied) { Diagnostic = _refusal }
            );
            _deniedResult = await _denied.DeleteAsync();

            _refusedOpen = new OwnedTarget();
            _refusedOpen.Harness.Engine.OnOpenSession = (_, _, _) =>
                Task.FromResult<DmsSessionOpenResult>(new DmsSessionOpenResult.AccessDenied(_refusal));
            _refusedOpenResult = await _refusedOpen.DeleteAsync();

            _changedAtOpen = new OwnedTarget();
            _changedAtOpen.Harness.Engine.OnOpenSession = (_, _, _) =>
                Task.FromResult<DmsSessionOpenResult>(new DmsSessionOpenResult.InstanceChanged());
            _changedAtOpenResult = await _changedAtOpen.DeleteAsync();

            _notOnline = new OwnedTarget
            {
                State = new DmsLiveTargetState.Present(_instance, DmsDatabaseState.Other),
            };
            _notOnlineResult = await _notOnline.DeleteAsync();
        }

        [Test]
        public void It_reports_another_identity_as_unowned() =>
            Verdict(_unownedResult)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.TargetUnowned,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        DmsDataStoreProvisioningService.DeleteIdentityStep
                    )
                );

        [Test]
        public void It_reports_an_unreadable_identity_as_unverifiable() =>
            Verdict(_missingResult)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.TargetUnverifiable,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        DmsDataStoreProvisioningService.DeleteIdentityStep
                    )
                );

        [Test]
        public void It_reports_a_refused_identity_read_as_unverifiable_with_the_refusal()
        {
            DmsProvisioningFailure failure = FailureOf(_deniedResult);
            (failure.Code, failure.Diagnostic)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.TargetUnverifiable,
                        new DmsProvisioningDiagnostic(
                            "Npgsql.PostgresException",
                            "42501",
                            DmsDataStoreProvisioningService.DeleteIdentityStep
                        )
                    )
                );
        }

        [Test]
        public void It_reports_a_refused_or_displaced_session_as_unverifiable()
        {
            Verdict(_refusedOpenResult)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.TargetUnverifiable,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        DmsDataStoreProvisioningService.DeleteOpenSessionStep
                    )
                );
            FailureOf(_changedAtOpenResult).Code.Should().Be(DmsProvisioningFailureCode.TargetUnverifiable);
        }

        [Test]
        public void It_reports_a_target_that_is_not_online_as_unverifiable() =>
            Verdict(_notOnlineResult)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.TargetUnverifiable,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        DmsDataStoreProvisioningService.InspectTargetStep
                    )
                );

        [Test]
        public void It_never_fences_or_drops()
        {
            foreach (
                OwnedTarget target in new[] { _unowned, _missing, _denied, _refusedOpen, _changedAtOpen }
            )
            {
                target.Members.Should().NotContain(SpyDmsTargetEngine.Fence);
                target.Members.Should().NotContain(SpyDmsTargetEngine.DropInstance);
            }

            _unowned.Members.Should().Equal(_owned);
            _notOnline
                .Members.Should()
                .Equal(
                    SpyDmsTargetEngine.AcquireTargetLock,
                    SpyDmsTargetEngine.ReadServerFacts,
                    SpyDmsTargetEngine.Inspect
                );
        }

        [Test]
        public void It_closes_every_session_it_opened_and_releases_every_lock()
        {
            foreach (OwnedTarget target in new[] { _unowned, _missing, _denied })
            {
                target.SessionDisposals.Should().Be(1);
            }

            foreach (
                OwnedTarget target in new[]
                {
                    _unowned,
                    _missing,
                    _denied,
                    _refusedOpen,
                    _changedAtOpen,
                    _notOnline,
                }
            )
            {
                target.Releases.Should().Be(1);
            }
        }
    }

    [TestFixture]
    public class Given_the_fence_does_not_establish_exclusivity
    {
        private OwnedTarget _notEstablished = null!;
        private OwnedTarget _privilege = null!;
        private DmsDataStoreDeleteResult _notEstablishedResult = null!;
        private DmsDataStoreDeleteResult _privilegeResult = null!;

        [SetUp]
        public async Task Setup()
        {
            _notEstablished = new OwnedTarget();
            _notEstablished.Harness.Engine.OnFence = (_, _, _, _) =>
            {
                _notEstablished.State = new DmsLiveTargetState.Fenced(_instance);
                return Task.FromResult(FencedFailure(DmsCommandFailureClass.ObjectInUse));
            };
            _notEstablishedResult = await _notEstablished.DeleteAsync();

            _privilege = new OwnedTarget();
            _privilege.Harness.Engine.OnFence = (_, _, _, _) =>
            {
                _privilege.State = new DmsLiveTargetState.Fenced(_instance);
                return Task.FromResult(FencedFailure(DmsCommandFailureClass.PrivilegeMissing));
            };
            _privilegeResult = await _privilege.DeleteAsync();
        }

        [Test]
        public void It_reports_a_session_it_could_not_end_as_transient_with_the_engine_diagnostic()
        {
            DmsProvisioningFailure failure = FailureOf(_notEstablishedResult);
            (failure.Code, failure.Category, failure.Diagnostic)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.ExclusiveAccessNotEstablished,
                        DmsProvisioningFailureCategory.Transient,
                        _engineDiagnostic
                    )
                );
        }

        [Test]
        public void It_reports_a_missing_privilege_as_admin_privilege_missing() =>
            FailureOf(_privilegeResult).Code.Should().Be(DmsProvisioningFailureCode.AdminPrivilegeMissing);

        [Test]
        public void It_reverses_the_fence_and_never_drops()
        {
            foreach (OwnedTarget target in new[] { _notEstablished, _privilege })
            {
                target
                    .Members.Should()
                    .Equal(Seq([.. _owned, SpyDmsTargetEngine.Fence, SpyDmsTargetEngine.Unfence]));
                target.State.Should().BeOfType<DmsLiveTargetState.Present>();
            }
        }

        [Test]
        public void It_leaves_nothing_to_report() =>
            (ResidueOf(_notEstablishedResult).Count, ResidueOf(_privilegeResult).Count).Should().Be((0, 0));
    }

    [TestFixture]
    public class Given_the_fence_changes_nothing
    {
        private OwnedTarget _displaced = null!;
        private OwnedTarget _rejected = null!;
        private DmsDataStoreDeleteResult _displacedResult = null!;
        private DmsDataStoreDeleteResult _rejectedResult = null!;

        [SetUp]
        public async Task Setup()
        {
            _displaced = new OwnedTarget();
            _displaced.Harness.Engine.OnFence = (_, _, _, _) =>
                Task.FromResult(Failed(DmsCommandFailureClass.DoesNotExist, "Fence.InstanceChanged"));
            _displacedResult = await _displaced.DeleteAsync();

            _rejected = new OwnedTarget();
            _rejected.Harness.Engine.OnFence = (_, _, _, _) =>
                Task.FromResult(Failed(DmsCommandFailureClass.Rejected, "Fence"));
            _rejectedResult = await _rejected.DeleteAsync();
        }

        [Test]
        public void It_reports_another_instance_under_the_name_as_instance_changed() =>
            Verdict(_displacedResult)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.InstanceChanged,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        "Fence.InstanceChanged"
                    )
                );

        [Test]
        public void It_reports_a_rejected_fence_as_server_rejected() =>
            FailureOf(_rejectedResult).Code.Should().Be(DmsProvisioningFailureCode.ServerRejected);

        [Test]
        public void It_neither_reverses_nor_drops_and_reads_nothing_after_the_lock()
        {
            foreach (OwnedTarget target in new[] { _displaced, _rejected })
            {
                target.Members.Should().Equal(Seq([.. _owned, SpyDmsTargetEngine.Fence]));
            }
        }
    }

    [TestFixture]
    public class Given_the_fence_stands_and_cannot_be_reversed
    {
        private OwnedTarget _target = null!;
        private DmsDataStoreDeleteResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _target = new OwnedTarget();
            _target.Harness.Engine.OnFence = (_, _, _, _) =>
            {
                _target.State = new DmsLiveTargetState.Fenced(_instance);
                return Task.FromResult(FencedFailure(DmsCommandFailureClass.ObjectInUse));
            };
            _target.Harness.Engine.OnUnfence = (_, _, _) =>
                Task.FromResult(Failed(DmsCommandFailureClass.Rejected, "Unfence"));
            _result = await _target.DeleteAsync();
        }

        [Test]
        public void It_requires_inspection_and_reports_the_fenced_target()
        {
            Verdict(_result)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.ExclusiveAccessNotEstablished,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        "Fence.Exclusivity"
                    )
                );
            ResidueOf(_result).Should().Equal(_fencedResidue);
        }

        [Test]
        public void It_reads_the_name_only_after_proving_the_holder_gone() =>
            _target
                .Members.Should()
                .Equal(
                    Seq([
                        .. _owned,
                        SpyDmsTargetEngine.Fence,
                        SpyDmsTargetEngine.Unfence,
                        .. _settlementReads,
                    ])
                );

        [Test]
        public void It_logs_the_fenced_target_at_error() =>
            _target
                .Harness.Logger.Entries.Should()
                .Contain(entry => entry.EventId == DmsDataStoreProvisioningService.TargetResidue);
    }

    [TestFixture]
    public class Given_the_target_changes_under_the_fence
    {
        private OwnedTarget _rewritten = null!;
        private OwnedTarget _replaced = null!;
        private OwnedTarget _reopened = null!;
        private DmsDataStoreDeleteResult _rewrittenResult = null!;
        private DmsDataStoreDeleteResult _replacedResult = null!;
        private DmsDataStoreDeleteResult _reopenedResult = null!;

        [SetUp]
        public async Task Setup()
        {
            _rewritten = new OwnedTarget();
            _rewritten.IdentityOnRead(2, () => new DmsIdentityRead.Found(Guid.NewGuid()));
            _rewrittenResult = await _rewritten.DeleteAsync();

            _replaced = new OwnedTarget();
            _replaced.Harness.Engine.OnFence = (_, _, _, _) =>
            {
                _replaced.State = new DmsLiveTargetState.Fenced(_otherInstance);
                return Task.FromResult<DmsCommandOutcome>(new DmsCommandOutcome.Succeeded());
            };
            _replacedResult = await _replaced.DeleteAsync();

            _reopened = new OwnedTarget();
            _reopened.Harness.Engine.OnFence = (_, _, _, _) =>
            {
                _reopened.State = new DmsLiveTargetState.Present(_instance, DmsDatabaseState.Online);
                return Task.FromResult<DmsCommandOutcome>(new DmsCommandOutcome.Succeeded());
            };
            _reopenedResult = await _reopened.DeleteAsync();
        }

        [Test]
        public void It_reports_a_competing_writer_as_unowned_and_reverses_the_fence()
        {
            Verdict(_rewrittenResult)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.TargetUnowned,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        DmsDataStoreProvisioningService.VerifyFencedStep
                    )
                );
            _rewritten
                .Members.Should()
                .Equal(
                    Seq([
                        .. _owned,
                        SpyDmsTargetEngine.Fence,
                        SpyDmsTargetEngine.ReadIdentity,
                        SpyDmsTargetEngine.Unfence,
                    ])
                );
        }

        [Test]
        public void It_reports_another_instance_as_changed_without_reversing_anything()
        {
            Verdict(_replacedResult)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.InstanceChanged,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        DmsDataStoreProvisioningService.VerifyFencedStep
                    )
                );
            _replaced.Members.Should().Equal(_fencedAndVerified);
            ResidueOf(_replacedResult).Should().BeEmpty();
        }

        [Test]
        public void It_reports_a_target_accepting_connections_again_as_not_exclusive()
        {
            Verdict(_reopenedResult)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.ExclusiveAccessNotEstablished,
                        DmsProvisioningFailureCategory.Transient,
                        DmsDataStoreProvisioningService.VerifyFencedStep
                    )
                );
            _reopened.Members.Should().Equal(Seq([.. _fencedAndVerified, SpyDmsTargetEngine.Unfence]));
        }
    }

    [TestFixture]
    public class Given_our_target_session_cannot_be_proven_gone
    {
        private OwnedTarget _target = null!;
        private DmsDataStoreDeleteResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _target = new OwnedTarget();
            _target.Harness.Engine.OnTerminateSession = (_, _, _) =>
                Task.FromResult<DmsSessionTermination>(
                    new DmsSessionTermination.NotTerminated(DmsSessionTerminationFailure.TimedOut)
                );
            _result = await _target.DeleteAsync();
        }

        [Test]
        public void It_reports_exclusive_access_not_established() =>
            Verdict(_result)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.ExclusiveAccessNotEstablished,
                        DmsProvisioningFailureCategory.Transient,
                        DmsDataStoreProvisioningService.ReleaseTargetSessionStep
                    )
                );

        [Test]
        public void It_reverses_the_fence_instead_of_dropping() =>
            _target.Members.Should().Equal(Seq([.. _released, SpyDmsTargetEngine.Unfence]));
    }

    [TestFixture]
    public class Given_the_drop_fails_definitely
    {
        private OwnedTarget _inUse = null!;
        private OwnedTarget _rejected = null!;
        private OwnedTarget _absent = null!;
        private OwnedTarget _replaced = null!;
        private DmsDataStoreDeleteResult _inUseResult = null!;
        private DmsDataStoreDeleteResult _rejectedResult = null!;
        private DmsDataStoreDeleteResult _absentResult = null!;
        private DmsDataStoreDeleteResult _replacedResult = null!;

        private static async Task<(OwnedTarget, DmsDataStoreDeleteResult)> DropAnsweringAsync(
            DmsCommandOutcome outcome
        )
        {
            OwnedTarget target = new();
            target.Harness.Engine.OnDropInstance = (_, _, _, _) => Task.FromResult(outcome);
            return (target, await target.DeleteAsync());
        }

        [SetUp]
        public async Task Setup()
        {
            (_inUse, _inUseResult) = await DropAnsweringAsync(
                Failed(DmsCommandFailureClass.ObjectInUse, "DropTarget.SessionsPresent")
            );
            (_rejected, _rejectedResult) = await DropAnsweringAsync(
                Failed(DmsCommandFailureClass.Rejected, "DropTarget")
            );
            (_absent, _absentResult) = await DropAnsweringAsync(
                Failed(DmsCommandFailureClass.DoesNotExist, DmsEngineOperations.DropTargetAbsent)
            );
            (_replaced, _replacedResult) = await DropAnsweringAsync(
                Failed(DmsCommandFailureClass.DoesNotExist, "DropTarget.InstanceChanged")
            );
        }

        [Test]
        public void It_reports_a_session_on_the_target_as_not_exclusive_after_reversing_the_fence()
        {
            Verdict(_inUseResult)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.ExclusiveAccessNotEstablished,
                        DmsProvisioningFailureCategory.Transient,
                        "DropTarget.SessionsPresent"
                    )
                );
            _inUse
                .Members.Should()
                .Equal(Seq([.. _released, SpyDmsTargetEngine.DropInstance, SpyDmsTargetEngine.Unfence]));
        }

        [Test]
        public void It_reports_a_rejected_drop_as_transient_drop_failed_after_reversing_the_fence()
        {
            Verdict(_rejectedResult)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.DropFailed,
                        DmsProvisioningFailureCategory.Transient,
                        "DropTarget"
                    )
                );
            _rejected.Members.Should().EndWith([SpyDmsTargetEngine.DropInstance, SpyDmsTargetEngine.Unfence]);
        }

        [Test]
        public void It_reports_a_name_found_empty_as_already_absent() =>
            _absentResult.Should().BeOfType<DmsDataStoreDeleteResult.AlreadyAbsent>();

        [Test]
        public void It_reports_another_instance_as_changed() =>
            FailureOf(_replacedResult).Code.Should().Be(DmsProvisioningFailureCode.InstanceChanged);

        [Test]
        public void It_reverses_nothing_when_the_instance_is_gone_from_the_name()
        {
            foreach (OwnedTarget target in new[] { _absent, _replaced })
            {
                target.Members.Should().Equal(Seq([.. _released, SpyDmsTargetEngine.DropInstance]));
            }
        }
    }

    [TestFixture]
    public class Given_the_drop_outcome_is_unknown
    {
        private static readonly DmsServerSessionId _holder = Session;

        private static async Task<(OwnedTarget, DmsDataStoreDeleteResult)> DropInDoubtAsync(
            DmsLiveTargetState after,
            bool holderGone = true
        )
        {
            OwnedTarget target = new();
            target.Harness.Engine.OnDropInstance = (_, _, _, _) =>
            {
                target.State = after;
                return Task.FromResult<DmsCommandOutcome>(new DmsCommandOutcome.Unknown(_holder));
            };
            target.Harness.Engine.OnTerminateSession = (session, _, _) =>
                Task.FromResult<DmsSessionTermination>(
                    session == _holder && !holderGone
                        ? new DmsSessionTermination.NotTerminated(DmsSessionTerminationFailure.TimedOut)
                        : new DmsSessionTermination.Terminated()
                );
            return (target, await target.DeleteAsync());
        }

        [Test]
        public async Task It_reports_a_name_found_empty_as_dropped()
        {
            (OwnedTarget target, DmsDataStoreDeleteResult result) = await DropInDoubtAsync(
                new DmsLiveTargetState.Absent()
            );
            result.Should().BeOfType<DmsDataStoreDeleteResult.Dropped>();
            target
                .Members.Should()
                .Equal(Seq([.. _released, SpyDmsTargetEngine.DropInstance, .. _settlementReads]));
            target.Harness.Engine.TerminatedSessions.Should().Equal(_targetSession, _holder);
        }

        [Test]
        public async Task It_reports_our_fenced_instance_as_drop_failed_for_inspection_with_the_fenced_target()
        {
            (OwnedTarget target, DmsDataStoreDeleteResult result) = await DropInDoubtAsync(
                new DmsLiveTargetState.Fenced(_instance)
            );
            Verdict(result)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.DropFailed,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        DmsDataStoreProvisioningService.ResolveDeletionStep
                    )
                );
            ResidueOf(result).Should().Equal(_fencedResidue);
            target.Members.Should().NotContain(SpyDmsTargetEngine.Unfence);
        }

        [Test]
        public async Task It_reports_another_instance_as_changed()
        {
            (_, DmsDataStoreDeleteResult result) = await DropInDoubtAsync(
                new DmsLiveTargetState.Present(_otherInstance, DmsDatabaseState.Online)
            );
            Verdict(result)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.InstanceChanged,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        DmsDataStoreProvisioningService.ResolveDeletionStep
                    )
                );
            ResidueOf(result).Should().BeEmpty();
        }

        [Test]
        public async Task It_reads_nothing_and_reports_outcome_unknown_when_the_holder_cannot_be_proven_gone()
        {
            (OwnedTarget target, DmsDataStoreDeleteResult result) = await DropInDoubtAsync(
                new DmsLiveTargetState.Absent(),
                holderGone: false
            );
            FailureOf(result).Code.Should().Be(DmsProvisioningFailureCode.OutcomeUnknown);
            ResidueOf(result).Should().Equal(_fencedResidue);
            target
                .Members.Should()
                .Equal(
                    Seq([.. _released, SpyDmsTargetEngine.DropInstance, SpyDmsTargetEngine.TerminateSession])
                );
        }
    }

    [TestFixture]
    public class Given_the_holder_is_lost
    {
        private OwnedTarget _beforeFence = null!;
        private OwnedTarget _byTheFence = null!;
        private OwnedTarget _underTheFence = null!;
        private DmsDataStoreDeleteResult _beforeFenceResult = null!;
        private DmsDataStoreDeleteResult _byTheFenceResult = null!;
        private DmsDataStoreDeleteResult _underTheFenceResult = null!;

        [SetUp]
        public async Task Setup()
        {
            _beforeFence = new OwnedTarget();
            _beforeFence.Harness.Engine.OnReadIdentity = (_, _) =>
            {
                _beforeFence.Harness.OnlyHolder.Lose();
                return Task.FromResult<DmsIdentityRead>(new DmsIdentityRead.Found(_expected));
            };
            _beforeFenceResult = await _beforeFence.DeleteAsync();

            // The takeover shape: the holder's connection is dead, so the fence's instance read fails and
            // nothing is sent; the loss is declared as the command returns.
            _byTheFence = new OwnedTarget();
            _byTheFence.Harness.Engine.OnFence = (_, _, _, _) =>
            {
                _byTheFence.Harness.OnlyHolder.Lose();
                return Task.FromResult(Failed(DmsCommandFailureClass.Rejected, "Fence.Verify"));
            };
            _byTheFenceResult = await _byTheFence.DeleteAsync();

            _underTheFence = new OwnedTarget();
            int reads = 0;
            _underTheFence.Harness.Engine.OnReadIdentity = (_, _) =>
            {
                if (++reads == 2)
                {
                    _underTheFence.Harness.OnlyHolder.Lose();
                }

                return Task.FromResult<DmsIdentityRead>(new DmsIdentityRead.Found(_expected));
            };
            _underTheFenceResult = await _underTheFence.DeleteAsync();
        }

        [Test]
        public void It_is_transient_while_nothing_was_fenced()
        {
            Verdict(_beforeFenceResult)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.LockLost,
                        DmsProvisioningFailureCategory.Transient,
                        DmsDataStoreProvisioningService.DeleteIdentityStep
                    )
                );
            Verdict(_byTheFenceResult)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.LockLost,
                        DmsProvisioningFailureCategory.Transient,
                        DmsDataStoreProvisioningService.FenceStep
                    )
                );
            _beforeFence.Members.Should().Equal(_owned);
            _byTheFence.Members.Should().Equal(Seq([.. _owned, SpyDmsTargetEngine.Fence]));
        }

        [Test]
        public void It_requires_inspection_once_the_fence_stands_and_reports_the_fenced_target()
        {
            Verdict(_underTheFenceResult)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.LockLost,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        DmsDataStoreProvisioningService.VerifyFencedStep
                    )
                );
            ResidueOf(_underTheFenceResult).Should().Equal(_fencedResidue);
        }

        [Test]
        public void It_sends_no_mutation_after_the_loss_and_only_reads_after_the_lock() =>
            _underTheFence
                .Members.Should()
                .Equal(
                    Seq([
                        .. _owned,
                        SpyDmsTargetEngine.Fence,
                        SpyDmsTargetEngine.ReadIdentity,
                        .. _settlementReads,
                    ])
                );

        [Test]
        public void It_releases_every_lock_and_session() =>
            (
                _beforeFence.Releases,
                _byTheFence.Releases,
                _underTheFence.Releases,
                _underTheFence.SessionDisposals
            )
                .Should()
                .Be((1, 1, 1, 1));
    }

    [TestFixture]
    public class Given_the_fence_outcome_is_unknown
    {
        private static async Task<(OwnedTarget, DmsDataStoreDeleteResult)> FenceInDoubtAsync(
            DmsLiveTargetState after
        )
        {
            OwnedTarget target = new();
            target.Harness.Engine.OnFence = (_, _, _, _) =>
            {
                target.State = after;
                return Task.FromResult<DmsCommandOutcome>(new DmsCommandOutcome.Unknown(Session));
            };
            return (target, await target.DeleteAsync());
        }

        [Test]
        public async Task It_reports_lock_lost_for_inspection_and_the_fence_when_it_stands()
        {
            (OwnedTarget target, DmsDataStoreDeleteResult result) = await FenceInDoubtAsync(
                new DmsLiveTargetState.Fenced(_instance)
            );
            Verdict(result)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.LockLost,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        DmsDataStoreProvisioningService.FenceStep
                    )
                );
            ResidueOf(result).Should().Equal(_fencedResidue);
            target.Members.Should().Equal(Seq([.. _owned, SpyDmsTargetEngine.Fence, .. _settlementReads]));
        }

        [Test]
        public async Task It_reports_no_fence_when_the_target_still_accepts_connections_but_still_requires_inspection()
        {
            (_, DmsDataStoreDeleteResult result) = await FenceInDoubtAsync(
                new DmsLiveTargetState.Present(_instance, DmsDatabaseState.Online)
            );

            // D-12: once a selected-target mutation was sent, loss is never transient, even when the read
            // afterwards shows nothing standing.
            Verdict(result)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.LockLost,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        DmsDataStoreProvisioningService.FenceStep
                    )
                );
            ResidueOf(result).Should().BeEmpty();
        }
    }

    [TestFixture]
    public class Given_the_caller_cancels_under_the_fence
    {
        private OwnedTarget _target = null!;
        private Exception? _outcome;

        [SetUp]
        public async Task Setup()
        {
            _target = new OwnedTarget();
            TaskCompletionSource readStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            int reads = 0;
            _target.Harness.Engine.OnReadIdentity = async (_, token) =>
            {
                if (++reads == 2)
                {
                    readStarted.SetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }

                return new DmsIdentityRead.Found(_expected);
            };
            using CancellationTokenSource caller = new();
            Task<DmsDataStoreDeleteResult> delete = _target.DeleteAsync(caller.Token);
            await ReadStartedAsync(readStarted, delete);
            await caller.CancelAsync();
            _outcome = await Record.ExceptionAsync(() => delete);
        }

        [Test]
        public void It_propagates_the_cancellation() =>
            _outcome.Should().BeAssignableTo<OperationCanceledException>();

        [Test]
        public void It_reverses_the_fence_first_and_never_drops()
        {
            _target
                .Members.Should()
                .Equal(
                    Seq([
                        .. _owned,
                        SpyDmsTargetEngine.Fence,
                        SpyDmsTargetEngine.ReadIdentity,
                        SpyDmsTargetEngine.Unfence,
                    ])
                );
            _target.State.Should().BeOfType<DmsLiveTargetState.Present>();
        }

        [Test]
        public void It_closes_the_session_and_releases_the_lock() =>
            (_target.SessionDisposals, _target.Releases).Should().Be((1, 1));
    }

    [TestFixture]
    public class Given_the_caller_cancels_at_the_last_await_before_the_drop
    {
        private OwnedTarget _target = null!;
        private Exception? _outcome;

        [SetUp]
        public async Task Setup()
        {
            _target = new OwnedTarget();
            using CancellationTokenSource caller = new();
            _target.Hooks.Probe = async (point, _) =>
            {
                if (point == DmsProvisioningHookPoint.BeforeDrop)
                {
                    await caller.CancelAsync();
                }
            };
            _outcome = await Record.ExceptionAsync(() => _target.DeleteAsync(caller.Token));
        }

        [Test]
        public void It_propagates_the_cancellation() =>
            _outcome.Should().BeAssignableTo<OperationCanceledException>();

        [Test]
        public void It_sends_no_drop_and_reverses_the_fence() =>
            _target.Members.Should().Equal(Seq([.. _released, SpyDmsTargetEngine.Unfence]));
    }
}
