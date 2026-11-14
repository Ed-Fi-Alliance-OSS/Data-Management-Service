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

    /// <summary>
    /// H-02: a fence the server rejected before it took effect (no fenced-target residue) changed nothing. A retryable
    /// rejection is a transient <c>ServerRejected</c>, any other permanent, and a missing privilege stays
    /// <c>AdminPrivilegeMissing</c> whatever the flag says. Nothing is reversed or dropped and nothing is left over.
    /// </summary>
    [TestFixture]
    public class Given_the_fence_is_rejected_before_it_takes_effect_with_or_without_retryability
    {
        private static readonly (DmsCommandFailureClass Class, string Code, bool Retryable)[] _rejections =
        [
            (DmsCommandFailureClass.Rejected, "53200", true),
            (DmsCommandFailureClass.Rejected, "1205", true),
            (DmsCommandFailureClass.Rejected, "22023", false),
            (DmsCommandFailureClass.PrivilegeMissing, "42501", true),
        ];

        private readonly List<(OwnedTarget Target, DmsDataStoreDeleteResult Result)> _runs = [];

        [SetUp]
        public async Task Setup()
        {
            _runs.Clear();
            foreach ((DmsCommandFailureClass failureClass, string code, bool retryable) in _rejections)
            {
                OwnedTarget target = new();
                target.Harness.Engine.OnFence = (_, _, _, _) =>
                    Task.FromResult<DmsCommandOutcome>(
                        new DmsCommandOutcome.Failed(
                            new DmsCommandFailure(
                                failureClass,
                                new DmsProvisioningDiagnostic("Npgsql.PostgresException", code, "Fence")
                            )
                            {
                                Retryable = retryable,
                            }
                        )
                    );
                _runs.Add((target, await target.DeleteAsync()));
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
                        "53200"
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
                    )
                );

        [Test]
        public void It_neither_reverses_nor_drops_and_leaves_nothing() =>
            _runs
                .Should()
                .AllSatisfy(run =>
                {
                    run.Target.Members.Should().Equal(Seq([.. _owned, SpyDmsTargetEngine.Fence]));
                    ResidueOf(run.Result).Should().BeEmpty();
                });
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

    /// <summary>
    /// F-03: a fence whose result is lost while the holder still holds the lock is settled after the lock is released,
    /// and only what the settlement read shows decides the category. Our instance online and accepting connections is a
    /// retry (<c>Unknown</c>, transient); a fence found standing is <c>ExclusiveAccessNotEstablished</c> with the fenced
    /// target reported; a name that no longer holds our instance is <c>InstanceChanged</c>; an unread name, or our
    /// instance in another state, stays <c>OutcomeUnknown</c>.
    /// </summary>
    [TestFixture]
    public class Given_the_fence_outcome_is_unknown
    {
        private static async Task<(OwnedTarget, DmsDataStoreDeleteResult)> FenceInDoubtAsync(
            DmsLiveTargetState after,
            bool settlementReadFails = false,
            bool holderGone = true
        )
        {
            OwnedTarget target = new();
            bool fenced = false;
            target.Harness.Engine.OnFence = (_, _, _, _) =>
            {
                fenced = true;
                target.State = after;
                return Task.FromResult<DmsCommandOutcome>(new DmsCommandOutcome.Unknown(Session));
            };
            target.Harness.Engine.OnInspectByName = (_, _) =>
                fenced && settlementReadFails
                    ? throw new InvalidOperationException("The target name could not be read.")
                    : Task.FromResult(target.State);
            target.Harness.Engine.OnTerminateSession = (session, _, _) =>
                Task.FromResult<DmsSessionTermination>(
                    session == Session && !holderGone
                        ? new DmsSessionTermination.NotTerminated(DmsSessionTerminationFailure.TimedOut)
                        : new DmsSessionTermination.Terminated()
                );
            return (target, await target.DeleteAsync());
        }

        [Test]
        public async Task It_allows_a_retry_when_the_target_still_accepts_connections()
        {
            (OwnedTarget target, DmsDataStoreDeleteResult result) = await FenceInDoubtAsync(
                new DmsLiveTargetState.Present(_instance, DmsDatabaseState.Online)
            );
            Verdict(result)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.Unknown,
                        DmsProvisioningFailureCategory.Transient,
                        DmsDataStoreProvisioningService.FenceStep
                    )
                );
            ResidueOf(result).Should().BeEmpty();
            target.Members.Should().Equal(Seq([.. _owned, SpyDmsTargetEngine.Fence, .. _settlementReads]));
        }

        [Test]
        public async Task It_reports_a_fence_found_standing_for_inspection_with_the_fenced_target()
        {
            (OwnedTarget target, DmsDataStoreDeleteResult result) = await FenceInDoubtAsync(
                new DmsLiveTargetState.Fenced(_instance)
            );
            Verdict(result)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.ExclusiveAccessNotEstablished,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        DmsDataStoreProvisioningService.FenceStep
                    )
                );
            ResidueOf(result).Should().Equal(_fencedResidue);
            target.Members.Should().Equal(Seq([.. _owned, SpyDmsTargetEngine.Fence, .. _settlementReads]));
        }

        [Test]
        public async Task It_reports_another_instance_or_an_empty_name_as_an_instance_change()
        {
            foreach (
                DmsLiveTargetState after in new DmsLiveTargetState[]
                {
                    new DmsLiveTargetState.Present(_otherInstance, DmsDatabaseState.Online),
                    new DmsLiveTargetState.Absent(),
                }
            )
            {
                (_, DmsDataStoreDeleteResult result) = await FenceInDoubtAsync(after);
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
        }

        [Test]
        public async Task It_keeps_the_outcome_unknown_with_the_target_reported_when_nothing_could_be_read()
        {
            (_, DmsDataStoreDeleteResult unreadable) = await FenceInDoubtAsync(
                new DmsLiveTargetState.Present(_instance, DmsDatabaseState.Online),
                settlementReadFails: true
            );
            (_, DmsDataStoreDeleteResult holderUnproven) = await FenceInDoubtAsync(
                new DmsLiveTargetState.Present(_instance, DmsDatabaseState.Online),
                holderGone: false
            );
            foreach (DmsDataStoreDeleteResult result in new[] { unreadable, holderUnproven })
            {
                Verdict(result)
                    .Should()
                    .Be(
                        (
                            DmsProvisioningFailureCode.OutcomeUnknown,
                            DmsProvisioningFailureCategory.OperatorInspectionRequired,
                            DmsDataStoreProvisioningService.FenceStep
                        )
                    );
                ResidueOf(result).Should().Equal(_fencedResidue);
            }
        }

        [Test]
        public async Task It_keeps_the_outcome_unknown_when_our_instance_is_not_online()
        {
            (_, DmsDataStoreDeleteResult result) = await FenceInDoubtAsync(
                new DmsLiveTargetState.Present(_instance, DmsDatabaseState.Other)
            );
            Verdict(result)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.OutcomeUnknown,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        DmsDataStoreProvisioningService.FenceStep
                    )
                );
        }
    }

    /// <summary>
    /// F-03: the holder is declared lost while the fence it carried is in doubt. The loss is real, so the code stays
    /// <c>LockLost</c>; its category follows settlement: transient only when the read shows our instance online, an
    /// inspection failure with the fenced target reported when the fence stands, and an inspection failure when the
    /// name no longer holds our instance.
    /// </summary>
    [TestFixture]
    public class Given_the_holder_is_lost_while_the_fence_is_in_doubt
    {
        private static async Task<DmsDataStoreDeleteResult> LostInDoubtAsync(DmsLiveTargetState after)
        {
            OwnedTarget target = new();
            target.Harness.Engine.OnFence = (_, _, _, _) =>
            {
                target.Harness.OnlyHolder.Lose();
                target.State = after;
                return Task.FromResult<DmsCommandOutcome>(new DmsCommandOutcome.Unknown(Session));
            };
            return await target.DeleteAsync();
        }

        [Test]
        public async Task It_reports_loss_as_transient_when_the_target_still_accepts_connections()
        {
            DmsDataStoreDeleteResult result = await LostInDoubtAsync(
                new DmsLiveTargetState.Present(_instance, DmsDatabaseState.Online)
            );
            Verdict(result)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.LockLost,
                        DmsProvisioningFailureCategory.Transient,
                        DmsDataStoreProvisioningService.FenceStep
                    )
                );
            ResidueOf(result).Should().BeEmpty();
        }

        [Test]
        public async Task It_keeps_loss_an_inspection_failure_with_the_fenced_target_when_the_fence_stands()
        {
            DmsDataStoreDeleteResult result = await LostInDoubtAsync(
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
        }

        [Test]
        public async Task It_keeps_loss_an_inspection_failure_when_the_name_no_longer_holds_our_instance()
        {
            DmsDataStoreDeleteResult result = await LostInDoubtAsync(
                new DmsLiveTargetState.Present(_otherInstance, DmsDatabaseState.Online)
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
        }
    }

    /// <summary>
    /// F-03 independence: the reversal of a fence is issued because another failure ended the delete, here the fenced
    /// identity re-read finding another identity (<c>TargetUnowned</c>). Whatever happens to that reversal, the delete
    /// keeps requiring inspection even when the settlement read shows our instance online again: a loss during the
    /// reversal hides the original reason and stays <c>LockLost</c> for inspection, and an unknown reversal keeps
    /// <c>TargetUnowned</c>. Neither is ever reclassified as transient.
    /// </summary>
    [TestFixture]
    public class Given_the_fence_reversal_after_another_failure_ends_in_doubt
    {
        private static async Task<DmsDataStoreDeleteResult> UnownedThenReversalAsync(bool loseTheHolder)
        {
            OwnedTarget target = new();
            target.IdentityOnRead(2, () => new DmsIdentityRead.Found(Guid.NewGuid()));
            target.Harness.Engine.OnUnfence = (_, _, _) =>
            {
                if (loseTheHolder)
                {
                    target.Harness.OnlyHolder.Lose();
                }

                target.State = new DmsLiveTargetState.Present(_instance, DmsDatabaseState.Online);
                return Task.FromResult<DmsCommandOutcome>(new DmsCommandOutcome.Unknown(Session));
            };
            return await target.DeleteAsync();
        }

        [Test]
        public async Task It_keeps_a_loss_during_the_reversal_an_inspection_failure() =>
            Verdict(await UnownedThenReversalAsync(loseTheHolder: true))
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.LockLost,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        DmsDataStoreProvisioningService.UnfenceStep
                    )
                );

        [Test]
        public async Task It_keeps_the_unowned_target_when_the_reversal_outcome_is_unknown() =>
            Verdict(await UnownedThenReversalAsync(loseTheHolder: false))
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.TargetUnowned,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        DmsDataStoreProvisioningService.VerifyFencedStep
                    )
                );
    }

    /// <summary>
    /// F-03: an engine may report the caller's cancellation of a dispatched fence as an unknown outcome. The
    /// cancellation wins over any reclassification: the fence in doubt is settled after the lock is released (the
    /// holder proven gone, the name read) before the caller receives <see cref="OperationCanceledException"/>, and
    /// nothing is dropped.
    /// </summary>
    [TestFixture]
    public class Given_the_caller_cancels_while_the_fence_is_dispatched
    {
        private static async Task<(OwnedTarget Target, Exception? Outcome)> CancelledInDoubtAsync(
            DmsLiveTargetState after
        )
        {
            OwnedTarget target = new();
            using CancellationTokenSource caller = new();
            target.Harness.Engine.OnFence = async (_, _, _, _) =>
            {
                await caller.CancelAsync();
                target.State = after;
                return new DmsCommandOutcome.Unknown(Session);
            };
            Exception? outcome = await Record.ExceptionAsync(() => target.DeleteAsync(caller.Token));
            return (target, outcome);
        }

        [Test]
        public async Task It_propagates_the_cancellation_after_settling_the_fence()
        {
            foreach (
                DmsLiveTargetState after in new DmsLiveTargetState[]
                {
                    new DmsLiveTargetState.Present(_instance, DmsDatabaseState.Online),
                    new DmsLiveTargetState.Fenced(_instance),
                }
            )
            {
                (OwnedTarget target, Exception? outcome) = await CancelledInDoubtAsync(after);
                outcome.Should().BeAssignableTo<OperationCanceledException>();
                target.Releases.Should().Be(1);
                target
                    .Members.Should()
                    .Equal(Seq([.. _owned, SpyDmsTargetEngine.Fence, .. _settlementReads]));
            }
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

    /// <summary>
    /// The engine side of the holder gate, as the provider engines run it: the fence, the drop and the unfence
    /// are commands on the real holder's execution gate, and <see cref="Sent"/> lists the ones that ran under it.
    /// <see cref="HoldGateWithProbe"/> starts the heartbeat with a probe that keeps the gate until its task
    /// completes; <see cref="Waiting"/> is signalled as the command named at construction asks for the gate.
    /// </summary>
    private sealed class GatedTarget
    {
        public GatedTarget(string waitingCommand)
        {
            Target = new OwnedTarget();
            SpyDmsTargetEngine engine = Target.Harness.Engine;
            engine.OnFence = (_, _, _, token) =>
                RunGated(
                    SpyDmsTargetEngine.Fence,
                    waitingCommand,
                    new DmsLiveTargetState.Fenced(_instance),
                    token
                );
            engine.OnDropInstance = (_, _, _, token) =>
                RunGated(
                    SpyDmsTargetEngine.DropInstance,
                    waitingCommand,
                    new DmsLiveTargetState.Absent(),
                    token
                );
            engine.OnUnfence = (_, _, token) =>
                RunGated(
                    SpyDmsTargetEngine.Unfence,
                    waitingCommand,
                    new DmsLiveTargetState.Present(_instance, DmsDatabaseState.Online),
                    token
                );
        }

        public OwnedTarget Target { get; }

        public TaskCompletionSource Waiting { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> Probe { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The commands that ran under the gate, with the holder's release count when each ran.</summary>
        public List<(string Command, int Releases)> Sent { get; } = [];

        /// <summary>At <paramref name="point"/>, the heartbeat probe takes the gate and keeps it until <see cref="Probe"/> completes.</summary>
        public void HoldGateWithProbe(DmsProvisioningHookPoint point) =>
            Target.Hooks.Probe = (reached, _) =>
            {
                if (reached == point)
                {
                    FakeDmsTargetLockHolder holder = Target.Harness.OnlyHolder;
                    holder.Probe = probeToken => Probe.Task;
                    holder.StartHeartbeat();
                    Target.Harness.Time.Advance(TimeSpan.FromSeconds(5));
                }

                return Task.CompletedTask;
            };

        private Task<DmsCommandOutcome> RunGated(
            string command,
            string waitingCommand,
            DmsLiveTargetState after,
            CancellationToken token
        )
        {
            if (command == waitingCommand)
            {
                Waiting.TrySetResult();
            }

            return Target.Harness.OnlyHolder.RunAsync(
                _ =>
                {
                    Sent.Add((command, Target.Releases));
                    Target.State = after;
                    return Task.FromResult<DmsCommandOutcome>(new DmsCommandOutcome.Succeeded());
                },
                CommandTimeout,
                token
            );
        }
    }

    /// <summary>
    /// The confirmed dispatch defect: the heartbeat probe holds the holder's execution gate when the drop is handed to
    /// the engine, and the caller cancels while the drop waits for it. The gate refuses the drop before it is invoked, so
    /// the ledger keeps the fence (not a drop in doubt) and the fence is reversed on the healthy holder before the lock
    /// is released, instead of leaving the target fenced for an operator.
    /// </summary>
    [TestFixture]
    public class Given_the_caller_cancels_while_the_drop_waits_for_the_holder_gate
    {
        private GatedTarget _gated = null!;
        private Exception? _outcome;

        [SetUp]
        public async Task Setup()
        {
            _gated = new GatedTarget(SpyDmsTargetEngine.DropInstance);
            _gated.HoldGateWithProbe(DmsProvisioningHookPoint.BeforeDrop);
            using CancellationTokenSource caller = new();
            Task<DmsDataStoreDeleteResult> delete = _gated.Target.DeleteAsync(caller.Token);
            await ReadStartedAsync(_gated.Waiting, delete);
            await caller.CancelAsync();
            _gated.Probe.SetResult(true);
            _outcome = await Record.ExceptionAsync(() => delete);
        }

        [Test]
        public void It_propagates_the_cancellation() =>
            _outcome.Should().BeAssignableTo<OperationCanceledException>();

        [Test]
        public void It_never_sends_the_drop_and_reverses_the_fence_before_the_lock_is_released() =>
            _gated.Sent.Should().Equal((SpyDmsTargetEngine.Fence, 0), (SpyDmsTargetEngine.Unfence, 0));

        [Test]
        public void It_leaves_the_target_accepting_connections_without_settlement_reads()
        {
            _gated.Target.State.Should().BeOfType<DmsLiveTargetState.Present>();
            _gated
                .Target.Members.Should()
                .Equal(Seq([.. _released, SpyDmsTargetEngine.DropInstance, SpyDmsTargetEngine.Unfence]));
            _gated.Target.Releases.Should().Be(1);
        }
    }

    /// <summary>
    /// The conservative side of the same boundary: once the drop ran under the gate, a cancellation that ends it proves
    /// nothing. The drop stays in doubt, the fence is not reversed, and the outcome is read after the lock is released.
    /// </summary>
    [TestFixture]
    public class Given_the_caller_cancels_once_the_drop_was_handed_to_the_driver
    {
        private OwnedTarget _target = null!;
        private Exception? _outcome;

        [SetUp]
        public async Task Setup()
        {
            _target = new OwnedTarget();
            TaskCompletionSource running = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _target.Harness.Engine.OnDropInstance = (_, _, _, token) =>
                _target.Harness.OnlyHolder.RunAsync<DmsCommandOutcome>(
                    async driverToken =>
                    {
                        running.SetResult();
                        await Task.Delay(Timeout.InfiniteTimeSpan, driverToken);
                        return new DmsCommandOutcome.Succeeded();
                    },
                    CommandTimeout,
                    token
                );
            using CancellationTokenSource caller = new();
            Task<DmsDataStoreDeleteResult> delete = _target.DeleteAsync(caller.Token);
            await ReadStartedAsync(running, delete);
            await caller.CancelAsync();
            _outcome = await Record.ExceptionAsync(() => delete);
        }

        [Test]
        public void It_propagates_the_cancellation() =>
            _outcome.Should().BeAssignableTo<OperationCanceledException>();

        [Test]
        public void It_settles_the_drop_by_reading_state_and_never_reverses_the_fence()
        {
            _target
                .Members.Should()
                .Equal(Seq([.. _released, SpyDmsTargetEngine.DropInstance, .. _settlementReads]));
            _target.Harness.Engine.TerminatedSessions.Should().Equal(_targetSession, Session);
            _target.State.Should().BeOfType<DmsLiveTargetState.Fenced>();
        }
    }

    /// <summary>
    /// The deadline exit of the same boundary: the heartbeat probe holds the gate and the drop's <c>CommandTimeout</c>
    /// expires while it waits, with the caller never cancelling. The gate refuses the drop before it is invoked, so the
    /// fence is reversed on the healthy holder before the lock is released and the delete reports the timeout itself,
    /// instead of leaving the target fenced for an operator.
    /// </summary>
    [TestFixture]
    public class Given_the_drop_deadline_expires_while_it_waits_for_the_holder_gate
    {
        private GatedTarget _gated = null!;
        private DmsDataStoreDeleteResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _gated = new GatedTarget(SpyDmsTargetEngine.DropInstance);
            _gated.HoldGateWithProbe(DmsProvisioningHookPoint.BeforeDrop);
            Task<DmsDataStoreDeleteResult> delete = _gated.Target.DeleteAsync();
            await ReadStartedAsync(_gated.Waiting, delete);

            // The drop step's whole allowance: the gate wait plus PostgreSQL's three statements.
            _gated.Target.Harness.Time.Advance(CommandTimeout * 4);
            _gated.Probe.SetResult(true);
            _result = await delete;
        }

        [Test]
        public void It_reports_the_timeout_at_the_drop() =>
            FailureOf(_result)
                .Should()
                .Be(
                    DmsProvisioningFailure.Create(
                        DmsProvisioningFailureCode.TimedOut,
                        DmsProvisioningDiagnostic.ForOperation(DmsDataStoreProvisioningService.DropTargetStep)
                    )
                );

        [Test]
        public void It_never_sends_the_drop_and_reverses_the_fence_before_the_lock_is_released() =>
            _gated.Sent.Should().Equal((SpyDmsTargetEngine.Fence, 0), (SpyDmsTargetEngine.Unfence, 0));

        [Test]
        public void It_leaves_the_target_accepting_connections_without_residue_or_settlement_reads()
        {
            _gated.Target.State.Should().BeOfType<DmsLiveTargetState.Present>();
            ResidueOf(_result).Should().BeEmpty();
            _gated
                .Target.Members.Should()
                .Equal(Seq([.. _released, SpyDmsTargetEngine.DropInstance, SpyDmsTargetEngine.Unfence]));
            _gated.Target.Releases.Should().Be(1);
        }
    }

    /// <summary>
    /// The conservative side of the deadline exit: once the drop ran under the gate, its expired deadline proves
    /// nothing. The fence is not reversed; the drop is settled by reading state after the lock is released, and our
    /// instance found still fenced is a drop failure for inspection with the fenced target reported.
    /// </summary>
    [TestFixture]
    public class Given_the_drop_deadline_expires_once_the_drop_was_handed_to_the_driver
    {
        private OwnedTarget _target = null!;
        private DmsDataStoreDeleteResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _target = new OwnedTarget();
            TaskCompletionSource running = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _target.Harness.Engine.OnDropInstance = (_, _, _, token) =>
                _target.Harness.OnlyHolder.RunAsync<DmsCommandOutcome>(
                    async driverToken =>
                    {
                        running.SetResult();
                        await Task.Delay(Timeout.InfiniteTimeSpan, driverToken);
                        return new DmsCommandOutcome.Succeeded();
                    },
                    CommandTimeout,
                    token
                );
            Task<DmsDataStoreDeleteResult> delete = _target.DeleteAsync();
            await ReadStartedAsync(running, delete);
            _target.Harness.Time.Advance(CommandTimeout);
            _result = await delete;
        }

        [Test]
        public void It_reports_our_fenced_instance_as_drop_failed_for_inspection_with_the_fenced_target()
        {
            Verdict(_result)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.DropFailed,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        DmsDataStoreProvisioningService.ResolveDeletionStep
                    )
                );
            ResidueOf(_result).Should().Equal(_fencedResidue);
        }

        [Test]
        public void It_settles_by_reading_state_and_never_reverses_the_fence() =>
            _target
                .Members.Should()
                .Equal(Seq([.. _released, SpyDmsTargetEngine.DropInstance, .. _settlementReads]));
    }

    /// <summary>
    /// The fence sibling: the heartbeat probe finds the lock gone while the fence waits for the gate. The fence was
    /// never sent, so the loss is transient and nothing is read after the lock, as for loss before the fence.
    /// </summary>
    [TestFixture]
    public class Given_the_holder_is_lost_while_the_fence_waits_for_the_holder_gate
    {
        private GatedTarget _gated = null!;
        private DmsDataStoreDeleteResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _gated = new GatedTarget(SpyDmsTargetEngine.Fence);
            _gated.HoldGateWithProbe(DmsProvisioningHookPoint.BeforeFence);
            Task<DmsDataStoreDeleteResult> delete = _gated.Target.DeleteAsync();
            await ReadStartedAsync(_gated.Waiting, delete);
            _gated.Probe.SetResult(false);
            _result = await delete;
        }

        [Test]
        public void It_is_transient_lock_lost_at_the_fence() =>
            Verdict(_result)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.LockLost,
                        DmsProvisioningFailureCategory.Transient,
                        DmsDataStoreProvisioningService.FenceStep
                    )
                );

        [Test]
        public void It_sends_nothing_and_reads_nothing_after_the_lock()
        {
            _gated.Sent.Should().BeEmpty();
            _gated.Target.Members.Should().Equal(Seq([.. _owned, SpyDmsTargetEngine.Fence]));
            ResidueOf(_result).Should().BeEmpty();
        }
    }

    /// <summary>
    /// The fence's deadline starts before the engine waits for the holder gate. The heartbeat probe holds the gate
    /// for its whole bound (one <c>CommandTimeout</c>) when the fence asks for it, and the fence then runs under the
    /// PostgreSQL holder bound (<see cref="PostgresqlDmsTargetEngine.FenceDeadline"/>) to its last second. The
    /// orchestrator's allowance covers the gate wait, so nothing cancels the fence before the provider's bound,
    /// which leaves the termination loop its whole <c>SessionTerminationTimeout</c>; the fence still ends at that
    /// bound.
    /// </summary>
    [TestFixture]
    public class Given_the_fence_waits_for_the_holder_gate_and_then_uses_its_whole_bound
    {
        // The harness's SessionTerminationTimeout.
        private static readonly TimeSpan _sessionTermination = TimeSpan.FromSeconds(30);

        private bool _cancelledWithinTheBound;
        private bool _cancelledPastTheBound;

        [SetUp]
        public async Task Setup()
        {
            TimeSpan bound = PostgresqlDmsTargetEngine.FenceDeadline(_sessionTermination, CommandTimeout);
            GatedTarget gated = new(SpyDmsTargetEngine.Fence);
            TaskCompletionSource running = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken fenceToken = default;
            gated.Target.Harness.Engine.OnFence = (_, _, _, token) =>
            {
                gated.Waiting.TrySetResult();
                return gated.Target.Harness.OnlyHolder.RunAsync(
                    async holderToken =>
                    {
                        fenceToken = holderToken;
                        running.SetResult();
                        await finish.Task;
                        gated.Target.State = new DmsLiveTargetState.Fenced(_instance);
                        return (DmsCommandOutcome)new DmsCommandOutcome.Succeeded();
                    },
                    bound,
                    token
                );
            };
            gated.HoldGateWithProbe(DmsProvisioningHookPoint.BeforeFence);
            Task<DmsDataStoreDeleteResult> delete = gated.Target.DeleteAsync();
            await ReadStartedAsync(gated.Waiting, delete);

            gated.Target.Harness.Time.Advance(CommandTimeout);
            gated.Probe.SetResult(true);
            await ReadStartedAsync(running, delete);

            gated.Target.Harness.Time.Advance(bound - TimeSpan.FromSeconds(1));
            _cancelledWithinTheBound = fenceToken.IsCancellationRequested;
            gated.Target.Harness.Time.Advance(TimeSpan.FromSeconds(2));
            _cancelledPastTheBound = fenceToken.IsCancellationRequested;

            finish.SetResult();
            await delete;
        }

        [Test]
        public void It_does_not_cancel_the_fence_within_the_provider_bound() =>
            _cancelledWithinTheBound.Should().BeFalse();

        [Test]
        public void It_still_ends_the_fence_at_the_provider_bound() =>
            _cancelledPastTheBound.Should().BeTrue();
    }

    /// <summary>
    /// Records the token a holder command runs under, and lets the test hold it running until <see cref="Finish"/>.
    /// </summary>
    private sealed class HeldCommand
    {
        public TaskCompletionSource Running { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken Token { get; private set; }

        /// <summary>Runs on <paramref name="holder"/>'s gate under <paramref name="bound"/>, as the PostgreSQL engine does.</summary>
        public Task<DmsCommandOutcome> RunAsync(
            FakeDmsTargetLockHolder holder,
            TimeSpan bound,
            Action applied,
            CancellationToken cancellationToken
        ) =>
            holder.RunAsync(
                async holderToken =>
                {
                    Token = holderToken;
                    Running.SetResult();
                    await Finish.Task;
                    applied();
                    return (DmsCommandOutcome)new DmsCommandOutcome.Succeeded();
                },
                bound,
                cancellationToken
            );
    }

    /// <summary>
    /// The drop's deadline starts before the engine waits for the holder gate. The heartbeat probe holds the gate
    /// for its whole bound (one <c>CommandTimeout</c>) when the drop asks for it, and the drop then runs under the
    /// PostgreSQL holder bound (<see cref="PostgresqlDmsTargetEngine.DropDeadline"/>) to its last second. The
    /// orchestrator's allowance covers the gate wait, so nothing cancels the drop before the provider's bound,
    /// which no longer starves the <c>DROP DATABASE</c>; the drop still ends at that bound.
    /// </summary>
    [TestFixture]
    public class Given_the_drop_waits_for_the_holder_gate_and_then_uses_its_whole_bound
    {
        private bool _cancelledWithinTheBound;
        private bool _cancelledPastTheBound;

        [SetUp]
        public async Task Setup()
        {
            TimeSpan bound = PostgresqlDmsTargetEngine.DropDeadline(DmsDropReason.Delete, CommandTimeout);
            GatedTarget gated = new(SpyDmsTargetEngine.DropInstance);
            HeldCommand drop = new();
            gated.Target.Harness.Engine.OnDropInstance = (_, _, _, token) =>
            {
                gated.Waiting.TrySetResult();
                return drop.RunAsync(
                    gated.Target.Harness.OnlyHolder,
                    bound,
                    () => gated.Target.State = new DmsLiveTargetState.Absent(),
                    token
                );
            };
            gated.HoldGateWithProbe(DmsProvisioningHookPoint.BeforeDrop);
            Task<DmsDataStoreDeleteResult> delete = gated.Target.DeleteAsync();
            await ReadStartedAsync(gated.Waiting, delete);

            gated.Target.Harness.Time.Advance(CommandTimeout);
            gated.Probe.SetResult(true);
            await ReadStartedAsync(drop.Running, delete);

            gated.Target.Harness.Time.Advance(bound - TimeSpan.FromSeconds(1));
            _cancelledWithinTheBound = drop.Token.IsCancellationRequested;
            gated.Target.Harness.Time.Advance(TimeSpan.FromSeconds(2));
            _cancelledPastTheBound = drop.Token.IsCancellationRequested;

            drop.Finish.SetResult();
            await delete;
        }

        [Test]
        public void It_does_not_cancel_the_drop_within_the_provider_bound() =>
            _cancelledWithinTheBound.Should().BeFalse();

        [Test]
        public void It_still_ends_the_drop_at_the_provider_bound() =>
            _cancelledPastTheBound.Should().BeTrue();
    }

    /// <summary>
    /// The reversal sibling: a drop refused because a session is still listed leaves our fence standing, and the
    /// heartbeat probe takes the holder gate as the drop returns. The unfence then waits for the gate for the probe's
    /// whole bound and runs under the PostgreSQL holder bound (<see cref="PostgresqlDmsTargetEngine.UnfenceDeadline"/>)
    /// to its last second. The recovery allowance covers the gate wait, so the probe cannot leave the owned target
    /// fenced; the unfence still ends at the provider's bound.
    /// </summary>
    [TestFixture]
    public class Given_the_unfence_waits_for_the_holder_gate_and_then_uses_its_whole_bound
    {
        private bool _cancelledWithinTheBound;
        private bool _cancelledPastTheBound;
        private DmsDataStoreDeleteResult _result = null!;
        private GatedTarget _gated = null!;

        [SetUp]
        public async Task Setup()
        {
            TimeSpan bound = PostgresqlDmsTargetEngine.UnfenceDeadline(CommandTimeout);
            _gated = new GatedTarget(SpyDmsTargetEngine.Unfence);
            HeldCommand unfence = new();
            _gated.Target.Harness.Engine.OnDropInstance = (_, _, _, _) =>
            {
                // The heartbeat probe takes the gate as the refused drop returns, and keeps it.
                FakeDmsTargetLockHolder holder = _gated.Target.Harness.OnlyHolder;
                holder.Probe = _ => _gated.Probe.Task;
                holder.StartHeartbeat();
                _gated.Target.Harness.Time.Advance(TimeSpan.FromSeconds(5));
                return Task.FromResult(
                    Failed(
                        DmsCommandFailureClass.ObjectInUse,
                        PostgresqlDeletionQueries.DropTargetSessionsStep
                    )
                );
            };
            _gated.Target.Harness.Engine.OnUnfence = (_, _, token) =>
            {
                _gated.Waiting.TrySetResult();
                return unfence.RunAsync(
                    _gated.Target.Harness.OnlyHolder,
                    bound,
                    () =>
                        _gated.Target.State = new DmsLiveTargetState.Present(
                            _instance,
                            DmsDatabaseState.Online
                        ),
                    token
                );
            };
            Task<DmsDataStoreDeleteResult> delete = _gated.Target.DeleteAsync();
            await ReadStartedAsync(_gated.Waiting, delete);

            _gated.Target.Harness.Time.Advance(CommandTimeout);
            _gated.Probe.SetResult(true);
            await ReadStartedAsync(unfence.Running, delete);

            _gated.Target.Harness.Time.Advance(bound - TimeSpan.FromSeconds(1));
            _cancelledWithinTheBound = unfence.Token.IsCancellationRequested;
            _gated.Target.Harness.Time.Advance(TimeSpan.FromSeconds(2));
            _cancelledPastTheBound = unfence.Token.IsCancellationRequested;

            unfence.Finish.SetResult();
            _result = await delete;
        }

        [Test]
        public void It_does_not_cancel_the_unfence_within_the_provider_bound() =>
            _cancelledWithinTheBound.Should().BeFalse();

        [Test]
        public void It_still_ends_the_unfence_at_the_provider_bound() =>
            _cancelledPastTheBound.Should().BeTrue();

        [Test]
        public void It_reverses_the_fence_and_reports_the_refused_drop_without_residue()
        {
            Verdict(_result)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.ExclusiveAccessNotEstablished,
                        DmsProvisioningFailureCategory.Transient,
                        PostgresqlDeletionQueries.DropTargetSessionsStep
                    )
                );
            _gated.Target.State.Should().BeOfType<DmsLiveTargetState.Present>();
            ResidueOf(_result).Should().BeEmpty();
        }
    }

    /// <summary>
    /// The heartbeat probe's deadline passes while it holds the gate, and its driver keeps it two seconds longer
    /// completing the acknowledged cancellation (Npgsql's default <c>CancellationTimeout</c>). The fence's
    /// deadline counts from its request, gate wait included, so the overrun is spent from the step's allowance
    /// (<c>SessionTerminationTimeout</c> + 6 x <c>CommandTimeout</c>), which is neither extended nor cut: the fence
    /// runs to the end of that allowance and is ended there, a second before the provider's own bound would end it.
    /// </summary>
    [TestFixture]
    public class Given_a_cancelled_probe_holds_the_gate_past_its_command_timeout
    {
        // The harness's SessionTerminationTimeout.
        private static readonly TimeSpan _sessionTermination = TimeSpan.FromSeconds(30);

        // How long past its deadline an acknowledged cancellation keeps the probe running.
        private static readonly TimeSpan _cancellationCompletion = TimeSpan.FromSeconds(2);

        private bool _cancelledWithinTheAllowance;
        private bool _cancelledPastTheAllowance;

        [SetUp]
        public async Task Setup()
        {
            TimeSpan allowance = _sessionTermination + (CommandTimeout * 6);
            TimeSpan gateWait = CommandTimeout + _cancellationCompletion;
            TimeSpan bound = PostgresqlDmsTargetEngine.FenceDeadline(_sessionTermination, CommandTimeout);
            GatedTarget gated = new(SpyDmsTargetEngine.Fence);
            TaskCompletionSource running = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken fenceToken = default;
            gated.Target.Harness.Engine.OnFence = (_, _, _, token) =>
            {
                gated.Waiting.TrySetResult();
                return gated.Target.Harness.OnlyHolder.RunAsync(
                    async holderToken =>
                    {
                        fenceToken = holderToken;
                        running.SetResult();
                        await finish.Task;
                        gated.Target.State = new DmsLiveTargetState.Fenced(_instance);
                        return (DmsCommandOutcome)new DmsCommandOutcome.Succeeded();
                    },
                    bound,
                    token
                );
            };
            gated.HoldGateWithProbe(DmsProvisioningHookPoint.BeforeFence);
            Task<DmsDataStoreDeleteResult> delete = gated.Target.DeleteAsync();
            await ReadStartedAsync(gated.Waiting, delete);

            gated.Target.Harness.Time.Advance(gateWait);
            gated.Probe.SetResult(true);
            await ReadStartedAsync(running, delete);

            gated.Target.Harness.Time.Advance(allowance - gateWait - TimeSpan.FromSeconds(1));
            _cancelledWithinTheAllowance = fenceToken.IsCancellationRequested;

            // A second past the allowance, and a second short of the provider's bound, which began at the gate.
            gated.Target.Harness.Time.Advance(TimeSpan.FromSeconds(2));
            _cancelledPastTheAllowance = fenceToken.IsCancellationRequested;

            finish.SetResult();
            await delete;
        }

        [Test]
        public void It_does_not_cancel_the_fence_within_the_step_allowance() =>
            _cancelledWithinTheAllowance.Should().BeFalse();

        [Test]
        public void It_ends_the_fence_at_the_step_allowance_counted_from_the_request() =>
            _cancelledPastTheAllowance.Should().BeTrue();
    }

    /// <summary>
    /// Round-1 review finding 1: loss declared while an engine call that was handed the fence or the drop
    /// returns an unknown outcome, or throws. The scope turns both into a loss cancellation; neither proves that
    /// nothing was sent, so the mutation stays in doubt and is settled by reading state after the lock.
    /// </summary>
    [TestFixture]
    public class Given_loss_accompanies_an_uncertain_fence_or_drop
    {
        private static async Task<(OwnedTarget, DmsDataStoreDeleteResult)> FenceAsync(bool throws)
        {
            OwnedTarget target = new();
            target.Harness.Engine.OnFence = (_, _, _, _) =>
            {
                // The fence was applied on the server; the client lost the answer with the holder.
                target.State = new DmsLiveTargetState.Fenced(_instance);
                target.Harness.OnlyHolder.Lose();
                return throws
                    ? throw new InvalidOperationException("connection reset", new IOException())
                    : Task.FromResult<DmsCommandOutcome>(new DmsCommandOutcome.Unknown(Session));
            };
            return (target, await target.DeleteAsync());
        }

        private static async Task<(OwnedTarget, DmsDataStoreDeleteResult)> DropAsync(bool throws)
        {
            OwnedTarget target = new();
            target.Harness.Engine.OnDropInstance = (_, _, _, _) =>
            {
                // The drop committed on the server; the client lost the answer with the holder.
                target.State = new DmsLiveTargetState.Absent();
                target.Harness.OnlyHolder.Lose();
                return throws
                    ? throw new InvalidOperationException("connection reset", new IOException())
                    : Task.FromResult<DmsCommandOutcome>(new DmsCommandOutcome.Unknown(Session));
            };
            return (target, await target.DeleteAsync());
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task It_reports_an_applied_fence_for_inspection_with_the_fenced_target(bool throws)
        {
            (OwnedTarget target, DmsDataStoreDeleteResult result) = await FenceAsync(throws);
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

        [TestCase(false)]
        [TestCase(true)]
        public async Task It_reports_a_committed_drop_as_dropped_once_the_holder_is_proven_gone(bool throws)
        {
            (OwnedTarget target, DmsDataStoreDeleteResult result) = await DropAsync(throws);
            result.Should().Be(new DmsDataStoreDeleteResult.Dropped(DmsCleanupReport.Clean));
            target
                .Members.Should()
                .Equal(Seq([.. _released, SpyDmsTargetEngine.DropInstance, .. _settlementReads]));
            target.Harness.Engine.TerminatedSessions.Should().Equal(_targetSession, Session);
        }
    }

    /// <summary>
    /// AC 10: the drop returned a confirmed success while the holder's loss was declared, alone or together with the
    /// caller's cancellation. The ledger observed the success before the loss check discarded the result, so the
    /// target is known dropped: the delete reports <c>Dropped</c> instead of a retryable <c>LockLost</c> or the
    /// cancellation, and settlement has nothing in doubt to read.
    /// </summary>
    [TestFixture]
    public class Given_loss_accompanies_a_confirmed_drop
    {
        private static async Task<(OwnedTarget, DmsDataStoreDeleteResult)> DropAsync(bool callerCancels)
        {
            OwnedTarget target = new();
            using CancellationTokenSource caller = new();
            target.Harness.Engine.OnDropInstance = async (_, _, _, _) =>
            {
                target.State = new DmsLiveTargetState.Absent();
                target.Harness.OnlyHolder.Lose();
                if (callerCancels)
                {
                    await caller.CancelAsync();
                }

                return new DmsCommandOutcome.Succeeded();
            };
            return (target, await target.DeleteAsync(caller.Token));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task It_reports_the_target_dropped_without_settlement_reads(bool callerCancels)
        {
            (OwnedTarget target, DmsDataStoreDeleteResult result) = await DropAsync(callerCancels);
            result.Should().Be(new DmsDataStoreDeleteResult.Dropped(DmsCleanupReport.Clean));
            target.Members.Should().Equal(Seq([.. _released, SpyDmsTargetEngine.DropInstance]));
            (target.SessionDisposals, target.Releases).Should().Be((1, 1));
        }
    }

    /// <summary>
    /// Round-1 review finding 2: a re-check under the fence that times out or fails unexpectedly, after which the
    /// fence cannot be reversed. Settlement confirms the target fenced, so the failure must require inspection
    /// even though its own code is transient-only; the diagnostic and the residue are kept.
    /// </summary>
    [TestFixture]
    public class Given_a_check_under_the_fence_fails_and_the_fence_cannot_be_reversed
    {
        private static OwnedTarget StuckFence()
        {
            OwnedTarget target = new();
            target.Harness.Engine.OnUnfence = (_, _, _) =>
                Task.FromResult(Failed(DmsCommandFailureClass.Rejected, "Unfence"));
            return target;
        }

        [Test]
        public async Task It_requires_inspection_after_an_unexpected_read_failure()
        {
            OwnedTarget target = StuckFence();
            int reads = 0;
            target.Harness.Engine.OnReadIdentity = (_, _) =>
                ++reads == 2
                    ? throw new InvalidOperationException(
                        "server said: password=hunter2",
                        new TimeoutException()
                    )
                    : Task.FromResult<DmsIdentityRead>(new DmsIdentityRead.Found(_expected));

            DmsDataStoreDeleteResult result = await target.DeleteAsync();

            DmsProvisioningFailure failure = FailureOf(result);
            (failure.Code, failure.Category, failure.Diagnostic)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.ExclusiveAccessNotEstablished,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        new DmsProvisioningDiagnostic(
                            "System.InvalidOperationException/System.TimeoutException",
                            null,
                            DmsDataStoreProvisioningService.VerifyFencedStep
                        )
                    )
                );
            ResidueOf(result).Should().Equal(_fencedResidue);
            target
                .Members.Should()
                .Equal(
                    Seq([
                        .. _owned,
                        SpyDmsTargetEngine.Fence,
                        SpyDmsTargetEngine.ReadIdentity,
                        SpyDmsTargetEngine.Unfence,
                        .. _settlementReads,
                    ])
                );
        }

        [Test]
        public async Task It_requires_inspection_after_a_read_deadline()
        {
            OwnedTarget target = StuckFence();
            TaskCompletionSource readStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            int reads = 0;
            target.Harness.Engine.OnReadIdentity = async (_, token) =>
            {
                if (++reads == 2)
                {
                    readStarted.SetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }

                return new DmsIdentityRead.Found(_expected);
            };

            Task<DmsDataStoreDeleteResult> delete = target.DeleteAsync();
            await ReadStartedAsync(readStarted, delete);
            target.Harness.Time.Advance(CommandTimeout);
            DmsDataStoreDeleteResult result = await delete;

            DmsProvisioningFailure failure = FailureOf(result);
            (failure.Code, failure.Category, failure.Diagnostic)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.ExclusiveAccessNotEstablished,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        DmsProvisioningDiagnostic.ForOperation(
                            DmsDataStoreProvisioningService.VerifyFencedStep
                        )
                    )
                );
            ResidueOf(result).Should().Equal(_fencedResidue);
        }
    }
}
