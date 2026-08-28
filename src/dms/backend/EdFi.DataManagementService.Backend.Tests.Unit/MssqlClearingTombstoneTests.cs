// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Mssql;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

/// <summary>
/// The clearing tombstone, and the orderings it exists to make safe. Clearing a SqlClient pool is
/// identity-scoped: an outstanding clear for a string will discard whatever pool that string names by
/// the time it runs, including one a new owner has just started using. So a new lease may not cross an
/// outstanding clear, and every interleaving here is forced with a gate rather than waited for.
/// </summary>
[TestFixture]
public class Given_An_Outstanding_Mssql_Pool_Clear
{
    private const string ReplicaText = "Server=replica;Database=dms;User Id=u;Password=p";
    private const string OtherText = "Server=other;Database=dms;User Id=u;Password=p";

    /// <summary>
    /// Bounds a gate wait so a lifecycle defect fails the run instead of hanging it. Nothing asserts on
    /// elapsed time; the value only has to exceed the time a correct implementation needs.
    /// </summary>
    private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(30);

    private GatedSqlServerPoolClearing _poolClearing = null!;
    private MssqlConnectionAcquisition _acquisition = null!;

    [SetUp]
    public void Setup()
    {
        _poolClearing = new GatedSqlServerPoolClearing();
        _acquisition = new MssqlConnectionAcquisition(
            _poolClearing,
            new CapturingLogger<MssqlConnectionAcquisition>(),
            _ => new OwnershipProbeConnection()
        );
        _poolClearing.Acquisition = _acquisition;
    }

    [TearDown]
    public void TearDown() =>
        _poolClearing
            .LockViolations.Should()
            .BeEmpty("no provider work may run while the state lock is held");

    private static EffectiveDataStoreTarget Replica(string text) =>
        new(EffectiveTargetKind.ReadReplica, text);

    private static DataStoreOwnershipSnapshot Owning(
        long version,
        params EffectiveDataStoreTarget[] targets
    ) =>
        new(
            version,
            [
                .. targets.Select(target => new ConfiguredTargetOwner(
                    "tenant-a",
                    1,
                    target.Kind,
                    target.ConnectionString
                )),
            ]
        );

    private static string Effective(EffectiveDataStoreTarget target) =>
        MssqlConnectionAcquisition.RealizeEffectiveConnectionString(target);

    /// <summary>
    /// Waits until the acquisition reports that <paramref name="expected" /> callers have observed the
    /// tombstone and gone on to wait. Bounded so a defect fails rather than hangs; the condition, not
    /// the elapsed time, is what any assertion reads.
    /// </summary>
    private void WaitUntilTombstoneObserved(long expected)
    {
        SpinWait spin = new();
        DateTime deadline = DateTime.UtcNow + GateTimeout;

        while (_acquisition.TombstoneWaitCount < expected)
        {
            DateTime.UtcNow.Should().BeBefore(deadline, "an acquisition must observe the tombstone");
            spin.SpinOnce();
        }
    }

    private static Task<T> OnDedicatedThread<T>(Func<T> work) =>
        Task.Factory.StartNew(
            work,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default
        );

    /// <summary>
    /// Holds a clear open for one identity and hands back the gate that releases it. Both clear
    /// initiators - reconciliation and last-lease release - are driven through this same helper,
    /// because both must use the same tombstone protocol.
    /// </summary>
    private (ManualResetEventSlim ClearEntered, ManualResetEventSlim ReleaseClear) HoldClearOf(
        string effectiveConnectionString
    )
    {
        ManualResetEventSlim clearEntered = new();
        ManualResetEventSlim releaseClear = new();

        _poolClearing.OnClear = cleared =>
        {
            if (!string.Equals(cleared, effectiveConnectionString, StringComparison.Ordinal))
            {
                return;
            }

            clearEntered.Set();
            releaseClear.Wait(GateTimeout);
        };

        return (clearEntered, releaseClear);
    }

    [Test]
    public void It_should_grant_no_lease_while_a_reconciliation_clear_is_outstanding()
    {
        EffectiveDataStoreTarget replica = Replica(ReplicaText);
        _acquisition.Reconcile(Owning(1, replica));

        MssqlConnectionLease seed = _acquisition.AcquireLeaseAsync(replica).GetAwaiter().GetResult();
        seed.Dispose();

        (ManualResetEventSlim clearEntered, ManualResetEventSlim releaseClear) = HoldClearOf(
            Effective(replica)
        );

        Task<bool> retirement = OnDedicatedThread(() =>
        {
            _acquisition.Reconcile(Owning(2));
            return true;
        });

        clearEntered.Wait(GateTimeout).Should().BeTrue();

        // The owner is configured again, so the acquisition below is a legitimate reacquisition and
        // not a stale request. It still may not cross the clear.
        _acquisition.Reconcile(Owning(3, replica));

        Task<MssqlConnectionLease> reacquisition = OnDedicatedThread(() =>
            _acquisition.AcquireLeaseAsync(replica).GetAwaiter().GetResult()
        );

        WaitUntilTombstoneObserved(1);
        releaseClear.Set();

        retirement.Wait(GateTimeout).Should().BeTrue();
        reacquisition.Wait(GateTimeout).Should().BeTrue();

        MssqlConnectionLease resumed = reacquisition.Result;
        resumed.EffectiveConnectionString.Should().Be(Effective(replica));

        // The fake records a clear only after its gate is released, so a lease granted while the clear
        // was outstanding would have been taken with nothing recorded. This is the exclusion itself
        // rather than an inference from how long anything took.
        _poolClearing
            .ClearCountOf(Effective(replica))
            .Should()
            .Be(1, "the clear completed before the lease was granted");

        // Resumed against fresh state: the identity is owned again, so releasing does not clear it.
        resumed.Dispose();
        _poolClearing.ClearCountOf(Effective(replica)).Should().Be(1);

        clearEntered.Dispose();
        releaseClear.Dispose();
    }

    /// <summary>
    /// The other clear initiator. Last-lease release must enter the same tombstone, or an acquisition
    /// could cross a clear started by a release rather than by reconciliation.
    /// </summary>
    [Test]
    public void It_should_grant_no_lease_while_a_release_started_clear_is_outstanding()
    {
        EffectiveDataStoreTarget replica = Replica(ReplicaText);
        _acquisition.Reconcile(Owning(1, replica));

        MssqlConnectionLease held = _acquisition.AcquireLeaseAsync(replica).GetAwaiter().GetResult();
        _acquisition.Reconcile(Owning(2));

        (ManualResetEventSlim clearEntered, ManualResetEventSlim releaseClear) = HoldClearOf(
            Effective(replica)
        );

        Task<bool> release = OnDedicatedThread(() =>
        {
            held.Dispose();
            return true;
        });

        clearEntered.Wait(GateTimeout).Should().BeTrue();

        _acquisition.Reconcile(Owning(3, replica));

        Task<MssqlConnectionLease> reacquisition = OnDedicatedThread(() =>
            _acquisition.AcquireLeaseAsync(replica).GetAwaiter().GetResult()
        );

        WaitUntilTombstoneObserved(1);
        releaseClear.Set();

        release.Wait(GateTimeout).Should().BeTrue();
        reacquisition.Wait(GateTimeout).Should().BeTrue();

        using MssqlConnectionLease resumed = reacquisition.Result;
        resumed.EffectiveConnectionString.Should().Be(Effective(replica));

        _poolClearing
            .ClearCountOf(Effective(replica))
            .Should()
            .Be(1, "a clear started by the last release excludes a new lease just the same");

        clearEntered.Dispose();
        releaseClear.Dispose();
    }

    /// <summary>
    /// The completion is signalled from a finally, so a clear that throws still releases its waiters
    /// rather than stranding them for the life of the process.
    /// </summary>
    [Test]
    public void It_should_release_waiters_when_the_clear_throws()
    {
        EffectiveDataStoreTarget replica = Replica(ReplicaText);
        _acquisition.Reconcile(Owning(1, replica));

        MssqlConnectionLease seed = _acquisition.AcquireLeaseAsync(replica).GetAwaiter().GetResult();
        seed.Dispose();

        _poolClearing.FailClearFor.Add(Effective(replica));

        (ManualResetEventSlim clearEntered, ManualResetEventSlim releaseClear) = HoldClearOf(
            Effective(replica)
        );

        Task<bool> retirement = OnDedicatedThread(() =>
        {
            _acquisition.Reconcile(Owning(2));
            return true;
        });

        clearEntered.Wait(GateTimeout).Should().BeTrue();

        _acquisition.Reconcile(Owning(3, replica));

        Task<MssqlConnectionLease> reacquisition = OnDedicatedThread(() =>
            _acquisition.AcquireLeaseAsync(replica).GetAwaiter().GetResult()
        );

        WaitUntilTombstoneObserved(1);
        releaseClear.Set();

        retirement.Wait(GateTimeout).Should().BeTrue();
        reacquisition.Wait(GateTimeout).Should().BeTrue("a failing clear must still complete its generation");

        using MssqlConnectionLease resumed = reacquisition.Result;
        resumed.EffectiveConnectionString.Should().Be(Effective(replica));

        clearEntered.Dispose();
        releaseClear.Dispose();
    }

    /// <summary>
    /// A caller that gives up while waiting takes nothing with it: the cancellation propagates
    /// unchanged and no claim was counted, so the identity is not kept alive by an abandoned request.
    /// </summary>
    [Test]
    public void It_should_propagate_cancellation_while_waiting_and_take_no_lease()
    {
        EffectiveDataStoreTarget replica = Replica(ReplicaText);
        _acquisition.Reconcile(Owning(1, replica));

        MssqlConnectionLease seed = _acquisition.AcquireLeaseAsync(replica).GetAwaiter().GetResult();
        seed.Dispose();

        (ManualResetEventSlim clearEntered, ManualResetEventSlim releaseClear) = HoldClearOf(
            Effective(replica)
        );

        Task<bool> retirement = OnDedicatedThread(() =>
        {
            _acquisition.Reconcile(Owning(2));
            return true;
        });

        clearEntered.Wait(GateTimeout).Should().BeTrue();
        _acquisition.Reconcile(Owning(3, replica));

        using CancellationTokenSource cancellation = new();
        Task<MssqlConnectionLease> abandoned = OnDedicatedThread(() =>
            _acquisition.AcquireLeaseAsync(replica, cancellation.Token).GetAwaiter().GetResult()
        );

        WaitUntilTombstoneObserved(1);
        cancellation.Cancel();

        FluentActions
            .Invoking(() => abandoned.Wait(GateTimeout))
            .Should()
            .Throw<AggregateException>()
            .Which.InnerException.Should()
            .BeAssignableTo<OperationCanceledException>();

        releaseClear.Set();
        retirement.Wait(GateTimeout).Should().BeTrue();

        // The abandoned request counted no claim, so a later acquisition-and-release still retires the
        // identity once its owner is gone.
        _acquisition.Reconcile(Owning(4));

        MssqlConnectionLease after = _acquisition.AcquireLeaseAsync(replica).GetAwaiter().GetResult();
        after.Dispose();

        _poolClearing
            .ClearCountOf(Effective(replica))
            .Should()
            .Be(2, "the first clear plus the one the unowned reacquisition earned");

        clearEntered.Dispose();
        releaseClear.Dispose();
    }

    /// <summary>
    /// The tombstone is identity-scoped, not global. One database being cleared must not stall another
    /// database's acquisitions, which is also why the clear runs outside the state lock: if it did not,
    /// this acquisition could not return at all.
    /// </summary>
    [Test]
    public void It_should_acquire_an_unrelated_identity_while_a_clear_is_outstanding()
    {
        EffectiveDataStoreTarget replica = Replica(ReplicaText);
        EffectiveDataStoreTarget other = Replica(OtherText);
        _acquisition.Reconcile(Owning(1, replica, other));

        MssqlConnectionLease seed = _acquisition.AcquireLeaseAsync(replica).GetAwaiter().GetResult();
        seed.Dispose();

        (ManualResetEventSlim clearEntered, ManualResetEventSlim releaseClear) = HoldClearOf(
            Effective(replica)
        );

        Task<bool> retirement = OnDedicatedThread(() =>
        {
            _acquisition.Reconcile(Owning(2, other));
            return true;
        });

        clearEntered.Wait(GateTimeout).Should().BeTrue();

        Task<MssqlConnectionLease> unrelated = OnDedicatedThread(() =>
            _acquisition.AcquireLeaseAsync(other).GetAwaiter().GetResult()
        );

        unrelated.Wait(GateTimeout).Should().BeTrue("an unrelated identity must not wait on this clear");

        using MssqlConnectionLease unrelatedLease = unrelated.Result;
        unrelatedLease.EffectiveConnectionString.Should().Be(Effective(other));

        releaseClear.Set();
        retirement.Wait(GateTimeout).Should().BeTrue();

        _poolClearing.Cleared.Should().ContainSingle().Which.Should().Be(Effective(replica));

        clearEntered.Dispose();
        releaseClear.Dispose();
    }

    /// <summary>
    /// The acquisition-after-reconciliation ordering. Reconciliation runs to completion while a request
    /// is between realizing its string and taking its claim, so the claim is published against an owner
    /// set that no longer contains it. It must be counted anyway - the request still needs its
    /// connection - and retire on release rather than survive unowned.
    /// </summary>
    [Test]
    public void It_should_count_and_then_retire_a_lease_published_after_its_owner_was_removed()
    {
        EffectiveDataStoreTarget replica = Replica(ReplicaText);
        _acquisition.Reconcile(Owning(1, replica));

        // No pool exists yet for this identity, so the removal below clears nothing and leaves the
        // acquisition to discover, at publication, that its owner is gone.
        _acquisition.Reconcile(Owning(2));
        _poolClearing.Cleared.Should().BeEmpty();

        MssqlConnectionLease lease = _acquisition.AcquireLeaseAsync(replica).GetAwaiter().GetResult();

        _poolClearing.Cleared.Should().BeEmpty("the claim is held");

        lease.Dispose();

        _poolClearing
            .ClearCountOf(Effective(replica))
            .Should()
            .Be(1, "the identity published retired and was cleared on last release");
    }

    /// <summary>
    /// The reconciliation-after-acquisition ordering: the claim is counted first, then the owner is
    /// removed. The clear is parked and happens once, on last release.
    /// </summary>
    [Test]
    public void It_should_retire_an_identity_leased_before_reconciliation()
    {
        EffectiveDataStoreTarget replica = Replica(ReplicaText);
        _acquisition.Reconcile(Owning(1, replica));

        MssqlConnectionLease lease = _acquisition.AcquireLeaseAsync(replica).GetAwaiter().GetResult();

        _acquisition.Reconcile(Owning(2));
        _poolClearing.Cleared.Should().BeEmpty();

        lease.Dispose();
        _poolClearing.ClearCountOf(Effective(replica)).Should().Be(1);
    }
}
