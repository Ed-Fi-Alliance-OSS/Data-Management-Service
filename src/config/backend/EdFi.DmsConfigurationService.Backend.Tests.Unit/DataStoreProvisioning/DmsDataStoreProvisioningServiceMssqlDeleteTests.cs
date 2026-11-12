// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using FluentAssertions;
using static EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.ProvisioningTestValues;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning;

/// <summary>
/// The orchestrator's owned delete on SQL Server (6.6, Step 4.8) over the spy engine. The fence (single-user mode with
/// <c>ROLLBACK IMMEDIATE</c>) keeps no session, so our session is closed and proven gone before it and the fence is
/// asked to keep none. The holder that set single-user mode holds the database's one slot, so under the fence the
/// identity is read on the holder (three-part name) and the instance by inspection; a refused, unreadable or other
/// identity there reverses the fence and is reported for inspection.
/// </summary>
public class DmsDataStoreProvisioningServiceMssqlDeleteTests
{
    private static readonly Guid _expected = Guid.Parse("6a7b8c9d-0e1f-4a2b-8c3d-4e5f6a7b8c9d");

    private static readonly DmsInstanceIdentity.Mssql _instance = new(
        7,
        "/var/opt/mssql/data/EdFi_Ods_Test.mdf",
        new DateTimeOffset(2026, 11, 7, 1, 2, 3, TimeSpan.Zero)
    );

    private static readonly DmsServerSessionId _workSession = new(
        61,
        new DateTimeOffset(2026, 11, 7, 2, 0, 0, TimeSpan.Zero)
    );

    private static readonly DmsDataStoreDeleteRequest _request = new(Name("EdFi_Ods_Test"), _expected);

    private sealed class FakeSession(DmsInstanceIdentity instance) : DmsSession(instance, _workSession)
    {
        public int Disposals { get; private set; }

        public override ValueTask DisposeAsync()
        {
            Disposals++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingHooks : IDmsProvisioningTestHooks
    {
        public List<DmsProvisioningHookPoint> Points { get; } = [];

        public ValueTask AtAsync(
            DmsProvisioningHookPoint point,
            DmsOperationId operation,
            CancellationToken cancellationToken
        )
        {
            Points.Add(point);
            return ValueTask.CompletedTask;
        }

        public bool ShouldInjectFault(DmsProvisioningFaultPoint point, DmsOperationId operation) => false;
    }

    /// <summary>
    /// A SQL Server holding an owned, online target under <see cref="_instance"/>: the fence, the unfence and the drop
    /// change what inspection reports; our session and the holder read the expected identity; every termination is
    /// proven. Each script can be replaced by a fixture.
    /// </summary>
    private sealed class OwnedTarget
    {
        public OwnedTarget()
        {
            Harness = new Harness(hooks: Hooks);
            SpyDmsTargetEngine engine = Harness.Engine;
            engine.Provider = DmsDataStoreProvisioningOptions.MssqlProvider;
            State = new DmsLiveTargetState.Present(_instance, DmsDatabaseState.Online);
            engine.OnInspectByName = (_, _) => Task.FromResult(State);
            engine.OnOpenSession = (_, instance, _) =>
            {
                Opened.Add(instance);
                FakeSession session = new(instance);
                Sessions.Add(session);
                return Task.FromResult<DmsSessionOpenResult>(new DmsSessionOpenResult.Opened(session));
            };
            engine.OnReadIdentity = (_, _) =>
                Task.FromResult<DmsIdentityRead>(new DmsIdentityRead.Found(_expected));
            engine.OnReadIdentityOnHolder = (target, _) =>
            {
                HolderReads.Add((target.Value, State));
                return Task.FromResult(HolderIdentity);
            };
            engine.OnTerminateSession = (session, _, _) =>
            {
                Terminated.Add((session, Sessions.Sum(open => open.Disposals)));
                return Task.FromResult(
                    Terminated.Count == UnprovenTermination
                        ? new DmsSessionTermination.NotTerminated(DmsSessionTerminationFailure.TimedOut)
                        : (DmsSessionTermination)new DmsSessionTermination.Terminated()
                );
            };
            engine.OnFence = (_, _, keep, _) =>
            {
                FenceCalls.Add((keep, Sessions.Sum(open => open.Disposals)));
                State = new DmsLiveTargetState.Fenced(_instance);
                return Task.FromResult<DmsCommandOutcome>(new DmsCommandOutcome.Succeeded());
            };
            engine.OnUnfence = (_, _, _) =>
            {
                State = new DmsLiveTargetState.Present(_instance, DmsDatabaseState.Online);
                return Task.FromResult<DmsCommandOutcome>(new DmsCommandOutcome.Succeeded());
            };
            engine.OnDropInstance = (_, _, reason, _) =>
            {
                Drops.Add(reason);
                State = new DmsLiveTargetState.Absent();
                return Task.FromResult<DmsCommandOutcome>(new DmsCommandOutcome.Succeeded());
            };
        }

        public RecordingHooks Hooks { get; } = new();

        public Harness Harness { get; }

        public DmsLiveTargetState State { get; set; }

        public List<FakeSession> Sessions { get; } = [];

        public List<DmsInstanceIdentity> Opened { get; } = [];

        /// <summary>Each holder identity read with the target it named and the state the target was in.</summary>
        public List<(string Target, DmsLiveTargetState State)> HolderReads { get; } = [];

        public DmsIdentityRead HolderIdentity { get; set; } = new DmsIdentityRead.Found(_expected);

        /// <summary>Each termination with the number of session disposals by then.</summary>
        public List<(DmsServerSessionId Session, int Disposals)> Terminated { get; } = [];

        /// <summary>Each fence with what it was asked to keep and the number of session disposals by then.</summary>
        public List<(DmsServerSessionId? Keep, int Disposals)> FenceCalls { get; } = [];

        public List<DmsDropReason> Drops { get; } = [];

        /// <summary>The 1-based termination left unproven; none by default.</summary>
        public int UnprovenTermination { get; init; }

        public IReadOnlyList<string> Members => Harness.Engine.Members;

        public Task<DmsDataStoreDeleteResult> DeleteAsync() => Harness.Service.DeleteAsync(_request, default);
    }

    private static (DmsProvisioningFailureCode, DmsProvisioningFailureCategory, string, string?) Verdict(
        DmsDataStoreDeleteResult result
    )
    {
        DmsProvisioningFailure failure = FailureOf(result);
        return (
            failure.Code,
            failure.Category,
            failure.Diagnostic.Operation,
            failure.Diagnostic.ProviderErrorCode
        );
    }

    private static readonly string[] _ownedAndReleased =
    [
        SpyDmsTargetEngine.AcquireTargetLock,
        SpyDmsTargetEngine.ReadServerFacts,
        SpyDmsTargetEngine.Inspect,
        SpyDmsTargetEngine.OpenSession,
        SpyDmsTargetEngine.ReadIdentity,
        SpyDmsTargetEngine.TerminateSession,
    ];

    private static readonly string[] _deleted =
    [
        .. _ownedAndReleased,
        SpyDmsTargetEngine.Fence,
        SpyDmsTargetEngine.ReadIdentityOnHolder,
        SpyDmsTargetEngine.Inspect,
        SpyDmsTargetEngine.DropInstance,
    ];

    private static readonly string[] _refusedUnderTheFence =
    [
        .. _ownedAndReleased,
        SpyDmsTargetEngine.Fence,
        SpyDmsTargetEngine.ReadIdentityOnHolder,
        SpyDmsTargetEngine.Unfence,
    ];

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
        public void It_drops_the_target() =>
            (_result is DmsDataStoreDeleteResult.Dropped, _target.Drops.Single())
                .Should()
                .Be((true, DmsDropReason.Delete));

        [Test]
        public void It_releases_our_session_before_the_fence_and_reads_the_identity_on_the_holder_under_it() =>
            _target.Members.Should().Equal(_deleted);

        [Test]
        public void It_asks_the_fence_to_keep_no_session_once_ours_is_closed() =>
            _target.FenceCalls.Should().Equal((null, 1));

        [Test]
        public void It_proves_our_session_gone_after_closing_it() =>
            _target.Terminated.Should().Equal((_workSession, 1));

        [Test]
        public void It_reads_the_identity_of_the_target_while_it_is_fenced() =>
            _target
                .HolderReads.Should()
                .Equal(("EdFi_Ods_Test", (DmsLiveTargetState)new DmsLiveTargetState.Fenced(_instance)));

        [Test]
        public void It_opens_one_session_bound_to_the_inspected_instance() =>
            _target.Opened.Should().Equal(_instance);

        [Test]
        public void It_keeps_the_hook_order() =>
            _target
                .Hooks.Points.Should()
                .Equal(DmsProvisioningHookPoint.BeforeFence, DmsProvisioningHookPoint.BeforeDrop);
    }

    [TestFixture]
    public class Given_our_session_is_not_proven_gone_before_the_fence
    {
        private OwnedTarget _target = null!;
        private DmsDataStoreDeleteResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _target = new OwnedTarget { UnprovenTermination = 1 };
            _result = await _target.DeleteAsync();
        }

        [Test]
        public void It_reports_exclusivity_not_established_before_anything_is_fenced()
        {
            Verdict(_result)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.ExclusiveAccessNotEstablished,
                        DmsProvisioningFailureCategory.Transient,
                        DmsDataStoreProvisioningService.ReleaseTargetSessionStep,
                        null
                    )
                );
            _target.Members.Should().Equal(_ownedAndReleased);
        }

        [Test]
        public void It_never_reaches_the_fence_hook() => _target.Hooks.Points.Should().BeEmpty();
    }

    [TestFixture]
    public class Given_the_holder_reads_another_identity_under_the_fence
    {
        private OwnedTarget _target = null!;
        private DmsDataStoreDeleteResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _target = new OwnedTarget { HolderIdentity = new DmsIdentityRead.Found(Guid.NewGuid()) };
            _result = await _target.DeleteAsync();
        }

        [Test]
        public void It_reports_the_target_unowned_under_the_fence() =>
            Verdict(_result)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.TargetUnowned,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        DmsDataStoreProvisioningService.VerifyFencedStep,
                        null
                    )
                );

        [Test]
        public void It_reverses_the_fence_and_never_drops() =>
            (_target.Members, _target.State)
                .Should()
                .Match<(IReadOnlyList<string> Members, DmsLiveTargetState State)>(run =>
                    run.Members.SequenceEqual(_refusedUnderTheFence)
                    && run.State is DmsLiveTargetState.Present
                );
    }

    [TestFixture]
    public class Given_the_holder_is_refused_the_identity_under_the_fence
    {
        private static readonly DmsProvisioningDiagnostic _refusal = new(
            "Microsoft.Data.SqlClient.SqlException",
            "916",
            "ReadIdentity"
        );

        private OwnedTarget _target = null!;
        private DmsDataStoreDeleteResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _target = new OwnedTarget
            {
                HolderIdentity = new DmsIdentityRead.Invalid(DmsIdentityReadProblem.AccessDenied)
                {
                    Diagnostic = _refusal,
                },
            };
            _result = await _target.DeleteAsync();
        }

        [Test]
        public void It_reports_the_target_unverifiable_with_the_refusal() =>
            Verdict(_result)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.TargetUnverifiable,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        DmsDataStoreProvisioningService.VerifyFencedStep,
                        "916"
                    )
                );

        [Test]
        public void It_reverses_the_fence_and_never_drops() =>
            _target.Members.Should().Equal(_refusedUnderTheFence);
    }

    [TestFixture]
    public class Given_the_identity_table_is_missing_under_the_fence
    {
        private OwnedTarget _target = null!;
        private DmsDataStoreDeleteResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _target = new OwnedTarget
            {
                HolderIdentity = new DmsIdentityRead.Invalid(DmsIdentityReadProblem.TableMissing),
            };
            _result = await _target.DeleteAsync();
        }

        [Test]
        public void It_reports_the_target_unverifiable_and_reverses_the_fence()
        {
            Verdict(_result)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.TargetUnverifiable,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        DmsDataStoreProvisioningService.VerifyFencedStep,
                        null
                    )
                );
            _target.Members.Should().Equal(_refusedUnderTheFence);
        }
    }

    /// <summary>
    /// The fenced identity read's deadline starts before the engine waits for the holder gate. The heartbeat probe
    /// takes the gate as the fence returns and holds it for its whole bound (one <c>CommandTimeout</c>), and the
    /// three-part read then runs under the SQL Server holder bound (one <c>CommandTimeout</c>) to its last second.
    /// The orchestrator's allowance covers the gate wait, so nothing cancels the read before the provider's bound,
    /// which would fail the delete of an owned, fenced target and reverse its fence; the read still ends at that
    /// bound.
    /// </summary>
    [TestFixture]
    public class Given_the_fenced_identity_read_waits_for_the_holder_gate_and_then_uses_its_whole_bound
    {
        private bool _cancelledWithinTheBound;
        private bool _cancelledPastTheBound;

        [SetUp]
        public async Task Setup()
        {
            OwnedTarget target = new();
            SpyDmsTargetEngine engine = target.Harness.Engine;
            TaskCompletionSource<bool> probe = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource running = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken readToken = default;
            SpyDmsTargetEngine.FenceScript fence = engine.OnFence;
            engine.OnFence = async (fenced, instance, keep, token) =>
            {
                DmsCommandOutcome outcome = await fence(fenced, instance, keep, token);

                // The heartbeat probe takes the gate as the fence returns, and keeps it.
                FakeDmsTargetLockHolder holder = target.Harness.OnlyHolder;
                holder.Probe = _ => probe.Task;
                holder.StartHeartbeat();
                target.Harness.Time.Advance(TimeSpan.FromSeconds(5));
                return outcome;
            };

            // Runs on the holder's gate under CommandTimeout, as MssqlDmsTargetEngine.ReadIdentityOnHolderAsync does.
            engine.OnReadIdentityOnHolder = (_, token) =>
            {
                waiting.TrySetResult();
                return target.Harness.OnlyHolder.RunAsync(
                    async holderToken =>
                    {
                        readToken = holderToken;
                        running.SetResult();
                        await finish.Task;
                        return target.HolderIdentity;
                    },
                    CommandTimeout,
                    token
                );
            };
            Task<DmsDataStoreDeleteResult> delete = target.DeleteAsync();
            await ReadStartedAsync(waiting, delete);

            target.Harness.Time.Advance(CommandTimeout);
            probe.SetResult(true);
            await ReadStartedAsync(running, delete);

            target.Harness.Time.Advance(CommandTimeout - TimeSpan.FromSeconds(1));
            _cancelledWithinTheBound = readToken.IsCancellationRequested;
            target.Harness.Time.Advance(TimeSpan.FromSeconds(2));
            _cancelledPastTheBound = readToken.IsCancellationRequested;

            finish.SetResult();
            await delete;
        }

        [Test]
        public void It_does_not_cancel_the_read_within_the_provider_bound() =>
            _cancelledWithinTheBound.Should().BeFalse();

        [Test]
        public void It_still_ends_the_read_at_the_provider_bound() =>
            _cancelledPastTheBound.Should().BeTrue();
    }
}
