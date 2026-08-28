// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Postgresql;
using FluentAssertions;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

/// <summary>
/// The orderings the cache's lifetime argument rests on, each forced with a gate rather than waited
/// for. Every interleaving here is driven by blocking the substituted provider seam at an exact point,
/// so none of these tests depends on scheduling or on how long anything takes.
/// </summary>
[TestFixture]
public class Given_NpgsqlDataSourceCache_Under_Concurrency
{
    private const string PrimaryConnectionString = "Host=primary;Database=dms;Username=u;Password=p";
    private const string ReplicaConnectionString = "Host=replica;Database=dms;Username=u;Password=p";

    /// <summary>
    /// Bounds a gate wait so a lifecycle defect fails the run instead of hanging it. Nothing asserts
    /// on elapsed time; the value only has to exceed the time a correct implementation needs, which
    /// is the time to take a monitor.
    /// </summary>
    private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(30);

    private GatedNpgsqlDataSourceLifetime _lifetime = null!;
    private NpgsqlDataSourceCache _cache = null!;

    [SetUp]
    public void Setup()
    {
        _lifetime = new GatedNpgsqlDataSourceLifetime();
        _cache = new NpgsqlDataSourceCache(new CapturingLogger<NpgsqlDataSourceCache>(), _lifetime);
        _lifetime.Cache = _cache;
    }

    [TearDown]
    public void TearDown()
    {
        _cache.Dispose();
        _lifetime.LockViolations.Should().BeEmpty("no provider work may run while the state lock is held");
        _lifetime.Dispose();
    }

    private static Task<T> OnDedicatedThread<T>(Func<T> work) =>
        Task.Factory.StartNew(
            work,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default
        );

    /// <summary>
    /// Two callers miss the entry and both build. Exactly one candidate may survive as the published
    /// entry, both callers must receive that same one, and the loser's candidate must be disposed
    /// rather than left to the garbage collector as an untracked pool.
    /// </summary>
    [Test]
    public void It_should_publish_one_winner_when_two_callers_build_concurrently()
    {
        _cache.Reconcile(OwnershipSnapshots.Of(1, PrimaryConnectionString));

        using Barrier bothInsideBuild = new(2);
        _lifetime.OnBuild = _ => bothInsideBuild.SignalAndWait(GateTimeout);

        Task<NpgsqlDataSourceLease> first = OnDedicatedThread(() =>
            _cache.AcquireLease(PrimaryConnectionString)
        );
        Task<NpgsqlDataSourceLease> second = OnDedicatedThread(() =>
            _cache.AcquireLease(PrimaryConnectionString)
        );

        Task.WaitAll([first, second], GateTimeout).Should().BeTrue();

        using NpgsqlDataSourceLease firstLease = first.Result;
        using NpgsqlDataSourceLease secondLease = second.Result;

        _lifetime.BuildCount.Should().Be(2, "both callers were forced to build before either published");

        firstLease.DataSource.Should().NotBeNull();
        secondLease.DataSource.Should().BeSameAs(firstLease.DataSource);

        NpgsqlDataSource winner = firstLease.DataSource;
        NpgsqlDataSource loser = _lifetime.Built.Single(built => !ReferenceEquals(built, winner));

        _lifetime.DisposeCountOf(loser).Should().Be(1, "the losing candidate is disposed exactly once");
        _lifetime.DisposeCountOf(winner).Should().Be(0);
    }

    /// <summary>
    /// A build failure belongs only to the caller whose build failed. The concurrent winner keeps its
    /// entry, and the cache is left in the state a single successful acquisition would have produced.
    /// </summary>
    [Test]
    public void It_should_leave_the_winner_intact_when_a_concurrent_build_fails()
    {
        _cache.Reconcile(OwnershipSnapshots.Of(1, PrimaryConnectionString));

        using Barrier bothInsideBuild = new(2);
        int arrivals = 0;
        _lifetime.OnBuild = _ =>
        {
            bothInsideBuild.SignalAndWait(GateTimeout);

            if (Interlocked.Increment(ref arrivals) == 1)
            {
                throw new InvalidOperationException("Simulated provider build failure.");
            }
        };

        Task<NpgsqlDataSourceLease> first = OnDedicatedThread(() =>
            _cache.AcquireLease(PrimaryConnectionString)
        );
        Task<NpgsqlDataSourceLease> second = OnDedicatedThread(() =>
            _cache.AcquireLease(PrimaryConnectionString)
        );

        // Waited on through a continuation rather than WaitAll, because one of these two is expected
        // to fault and WaitAll would rethrow it before anything could be asserted.
        Task<bool> settled = Task.WhenAll(first, second)
            .ContinueWith(completed => completed.IsCompleted, TaskScheduler.Default);
        settled.Wait(GateTimeout).Should().BeTrue();

        Task<NpgsqlDataSourceLease>[] outcomes = [first, second];
        outcomes.Count(task => task.IsFaulted).Should().Be(1);
        outcomes
            .Single(task => task.IsFaulted)
            .Exception!.InnerException.Should()
            .BeOfType<InvalidOperationException>();

        using NpgsqlDataSourceLease survivor = outcomes.Single(task => !task.IsFaulted).Result;

        _lifetime.BuildCount.Should().Be(1, "a failed build produces no data source to track");
        survivor.DataSource.Should().BeSameAs(_lifetime.Built.Single());
        _lifetime.DisposeCountOf(survivor.DataSource).Should().Be(0);

        using NpgsqlDataSourceLease next = _cache.AcquireLease(PrimaryConnectionString);
        next.DataSource.Should().BeSameAs(survivor.DataSource);
    }

    /// <summary>
    /// The acquisition-after-reconciliation ordering: reconciliation runs to completion while a build
    /// is in flight, so the build publishes against an owner set that no longer contains its string.
    /// It must publish retired rather than as an entry nothing will ever clean up.
    /// </summary>
    [Test]
    public void It_should_publish_a_retired_entry_when_the_build_outlives_its_owner()
    {
        _cache.Reconcile(OwnershipSnapshots.Of(1, PrimaryConnectionString));

        using ManualResetEventSlim buildEntered = new();
        using ManualResetEventSlim releaseBuild = new();
        _lifetime.OnBuild = _ =>
        {
            buildEntered.Set();
            releaseBuild.Wait(GateTimeout);
        };

        Task<NpgsqlDataSourceLease> acquisition = OnDedicatedThread(() =>
            _cache.AcquireLease(PrimaryConnectionString)
        );

        buildEntered.Wait(GateTimeout).Should().BeTrue();

        // The whole of reconciliation - candidate capture and owner-set replacement alike - happens
        // here, strictly between the build starting and the entry being published.
        _cache.Reconcile(OwnershipSnapshots.Empty(2));

        releaseBuild.Set();
        acquisition.Wait(GateTimeout).Should().BeTrue();

        NpgsqlDataSourceLease lease = acquisition.Result;
        NpgsqlDataSource dataSource = lease.DataSource;

        dataSource.Should().NotBeNull("a lease never names an entry whose data source is still null");
        _lifetime.DisposeCountOf(dataSource).Should().Be(0, "the lease is still held");

        lease.Dispose();

        _lifetime
            .DisposeCountOf(dataSource)
            .Should()
            .Be(1, "the entry published retired and was cleaned up on last release");
    }

    /// <summary>
    /// The reconciliation-after-acquisition ordering: the entry is published and leased first, then
    /// its owner is removed. The lease parks the cleanup and the last release performs it once.
    /// </summary>
    [Test]
    public void It_should_retire_and_clean_up_an_entry_published_before_reconciliation()
    {
        _cache.Reconcile(OwnershipSnapshots.Of(1, PrimaryConnectionString));

        using ManualResetEventSlim publicationComplete = new();
        Task<NpgsqlDataSourceLease> acquisition = OnDedicatedThread(() =>
        {
            NpgsqlDataSourceLease lease = _cache.AcquireLease(PrimaryConnectionString);
            publicationComplete.Set();
            return lease;
        });

        publicationComplete.Wait(GateTimeout).Should().BeTrue();
        acquisition.Wait(GateTimeout).Should().BeTrue();

        NpgsqlDataSourceLease lease = acquisition.Result;
        NpgsqlDataSource dataSource = lease.DataSource;

        _cache.Reconcile(OwnershipSnapshots.Empty(2));
        _lifetime.DisposeCountOf(dataSource).Should().Be(0);

        lease.Dispose();
        _lifetime.DisposeCountOf(dataSource).Should().Be(1);
    }

    /// <summary>
    /// A request that is mid-open when its target stops being configured still completes. Nothing is
    /// disposed while the lease is held, and the disposal happens once when the request ends.
    /// </summary>
    [Test]
    public async Task It_should_complete_an_in_flight_request_whose_owner_was_removed()
    {
        _cache.Reconcile(OwnershipSnapshots.Of(1, PrimaryConnectionString));

        using ManualResetEventSlim openEntered = new();
        using ManualResetEventSlim releaseOpen = new();
        _lifetime.OnOpen = (_, _) =>
        {
            openEntered.Set();
            releaseOpen.Wait(GateTimeout);
            return Task.CompletedTask;
        };

        Task<LeasedNpgsqlConnection> request = OnDedicatedThread(() =>
            _cache.OpenLeasedConnectionAsync(PrimaryConnectionString).GetAwaiter().GetResult()
        );

        openEntered.Wait(GateTimeout).Should().BeTrue();

        _cache.Reconcile(OwnershipSnapshots.Empty(2));
        NpgsqlDataSource dataSource = _lifetime.Built.Single();
        _lifetime.DisposeCountOf(dataSource).Should().Be(0, "the in-flight request still holds its claim");

        releaseOpen.Set();
        LeasedNpgsqlConnection leased = await request;

        leased.Connection.Should().NotBeNull("the request completed normally despite the removal");
        _lifetime.DisposeCountOf(dataSource).Should().Be(0);

        await leased.DisposeAsync();
        _lifetime.DisposeCountOf(dataSource).Should().Be(1);
    }

    /// <summary>
    /// The reason no provider work may run under the state lock: one slow or hung database would
    /// otherwise stall every other database's acquisitions. Proven by progress rather than by a flag -
    /// a second database is acquired to completion from inside the first one's build, which could not
    /// return at all if the build held the lock.
    /// </summary>
    [Test]
    public void It_should_acquire_another_database_while_one_is_still_building()
    {
        _cache.Reconcile(OwnershipSnapshots.Of(1, PrimaryConnectionString, ReplicaConnectionString));

        Task<NpgsqlDataSourceLease>? concurrent = null;

        _lifetime.OnBuild = connectionString =>
        {
            if (!string.Equals(connectionString, PrimaryConnectionString, StringComparison.Ordinal))
            {
                return;
            }

            concurrent = OnDedicatedThread(() => _cache.AcquireLease(ReplicaConnectionString));
            concurrent
                .Wait(GateTimeout)
                .Should()
                .BeTrue("acquiring a different database must not wait on this build");
        };

        using NpgsqlDataSourceLease primary = _cache.AcquireLease(PrimaryConnectionString);

        concurrent.Should().NotBeNull();
        using NpgsqlDataSourceLease replica = concurrent!.Result;

        replica.DataSource.Should().NotBeSameAs(primary.DataSource);
    }

    /// <summary>
    /// Cleanup is provider work too, so a disposal that blocks must not stall an acquisition of an
    /// unrelated database either.
    /// </summary>
    [Test]
    public void It_should_acquire_another_database_while_one_is_being_disposed()
    {
        _cache.Reconcile(OwnershipSnapshots.Of(1, PrimaryConnectionString, ReplicaConnectionString));

        NpgsqlDataSourceLease primary = _cache.AcquireLease(PrimaryConnectionString);
        NpgsqlDataSource retiring = primary.DataSource;
        primary.Dispose();

        Task<NpgsqlDataSourceLease>? concurrent = null;

        _lifetime.OnDispose = dataSource =>
        {
            if (!ReferenceEquals(dataSource, retiring))
            {
                return;
            }

            concurrent = OnDedicatedThread(() => _cache.AcquireLease(ReplicaConnectionString));
            concurrent
                .Wait(GateTimeout)
                .Should()
                .BeTrue("acquiring a different database must not wait on this disposal");
        };

        _cache.Reconcile(OwnershipSnapshots.Of(2, ReplicaConnectionString));

        concurrent.Should().NotBeNull();
        using NpgsqlDataSourceLease replica = concurrent!.Result;

        _lifetime.DisposeCountOf(retiring).Should().Be(1);
        _lifetime.DisposeCountOf(replica.DataSource).Should().Be(0);
    }
}
