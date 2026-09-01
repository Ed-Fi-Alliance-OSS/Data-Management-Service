// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Reflection;
using EdFi.DataManagementService.Backend.Postgresql;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using FluentAssertions;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

/// <summary>
/// The leased-only data-source cache: what it builds, what keeps a data source alive, and what
/// disposes it. Every assertion here is about a real <see cref="NpgsqlDataSource" /> object; none of
/// them opens a connection or needs a server.
/// </summary>
[TestFixture]
public class Given_NpgsqlDataSourceCache
{
    private const string PrimaryConnectionString = "Host=primary;Database=dms;Username=u;Password=p";
    private const string ReplicaConnectionString = "Host=replica;Database=dms;Username=u;Password=p";
    private const string SnapshotConnectionString = "Host=snapshot;Database=dms;Username=u;Password=p";

    private GatedNpgsqlDataSourceLifetime _lifetime = null!;
    private CapturingLogger<NpgsqlDataSourceCache> _logger = null!;
    private NpgsqlDataSourceCache _cache = null!;

    [SetUp]
    public void Setup()
    {
        _lifetime = new GatedNpgsqlDataSourceLifetime();
        _logger = new CapturingLogger<NpgsqlDataSourceCache>();
        _cache = new NpgsqlDataSourceCache(_logger, _lifetime, _lifetime.ReceiveStateLockProbe);
    }

    [TearDown]
    public void TearDown()
    {
        _cache.Dispose();
        _lifetime.LockViolations.Should().BeEmpty("no provider work may run while the state lock is held");
        _lifetime.Dispose();
    }

    [Test]
    public void It_should_build_one_data_source_per_configured_string()
    {
        using NpgsqlDataSourceLease first = _cache.AcquireLease(PrimaryConnectionString);
        using NpgsqlDataSourceLease second = _cache.AcquireLease(PrimaryConnectionString);

        second.DataSource.Should().BeSameAs(first.DataSource);
        _lifetime.BuildCount.Should().Be(1);
    }

    /// <summary>
    /// Two derivatives of one parent are two databases sharing one data-store id, so only the
    /// configured string can key the cache.
    /// </summary>
    [Test]
    public void It_should_build_a_distinct_data_source_for_each_derivative_of_one_parent()
    {
        using NpgsqlDataSourceLease replica = _cache.AcquireLease(ReplicaConnectionString);
        using NpgsqlDataSourceLease snapshot = _cache.AcquireLease(SnapshotConnectionString);

        snapshot.DataSource.Should().NotBeSameAs(replica.DataSource);
        _lifetime.BuildCount.Should().Be(2);
    }

    /// <summary>
    /// The same exact configured string reached through two different parents or tenants is one
    /// database and must be one pool.
    /// </summary>
    [Test]
    public void It_should_share_one_data_source_for_the_same_string_across_parents_and_tenants()
    {
        _cache.Reconcile(
            OwnershipSnapshots.Of(
                1,
                OwnershipSnapshots.Owner(
                    "tenant-a",
                    1,
                    EffectiveTargetKind.Snapshot,
                    PrimaryConnectionString
                ),
                OwnershipSnapshots.Owner("tenant-b", 2, EffectiveTargetKind.Primary, PrimaryConnectionString)
            )
        );

        using NpgsqlDataSourceLease throughA = _cache.AcquireLease(PrimaryConnectionString);
        using NpgsqlDataSourceLease throughB = _cache.AcquireLease(PrimaryConnectionString);

        throughB.DataSource.Should().BeSameAs(throughA.DataSource);
        _lifetime.BuildCount.Should().Be(1);
    }

    [Test]
    public void It_should_reject_a_blank_connection_string()
    {
        FluentActions.Invoking(() => _cache.AcquireLease("  ")).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => _cache.AcquireLease(null!)).Should().Throw<ArgumentException>();
    }

    /// <summary>
    /// A build failure touches no shared state, so the exception reaches the caller unchanged and the
    /// next acquisition starts over rather than inheriting a poisoned entry.
    /// </summary>
    [Test]
    public void It_should_propagate_a_build_failure_and_retry_from_scratch()
    {
        _lifetime.FailBuildFor.Add(PrimaryConnectionString);

        FluentActions
            .Invoking(() => _cache.AcquireLease(PrimaryConnectionString))
            .Should()
            .Throw<InvalidOperationException>();

        _lifetime.FailBuildFor.Clear();

        using NpgsqlDataSourceLease retried = _cache.AcquireLease(PrimaryConnectionString);

        retried.DataSource.Should().NotBeNull();
        _lifetime.BuildCount.Should().Be(1, "the failed build produced nothing to count");
    }

    [Test]
    public void It_should_not_dispose_a_data_source_that_is_still_configured()
    {
        _cache.Reconcile(OwnershipSnapshots.Of(1, PrimaryConnectionString));

        NpgsqlDataSourceLease lease = _cache.AcquireLease(PrimaryConnectionString);
        NpgsqlDataSource dataSource = lease.DataSource;
        lease.Dispose();

        _cache.Reconcile(OwnershipSnapshots.Of(2, PrimaryConnectionString));

        _lifetime.DisposeCountOf(dataSource).Should().Be(0);
    }

    [Test]
    public void It_should_dispose_an_unleased_data_source_when_its_owner_is_removed()
    {
        _cache.Reconcile(OwnershipSnapshots.Of(1, PrimaryConnectionString));

        NpgsqlDataSourceLease lease = _cache.AcquireLease(PrimaryConnectionString);
        NpgsqlDataSource dataSource = lease.DataSource;
        lease.Dispose();

        _cache.Reconcile(OwnershipSnapshots.Empty(2));

        _lifetime.DisposeCountOf(dataSource).Should().Be(1);
    }

    /// <summary>
    /// A background projection, a document-cache lookup, or an administrative mutex holds its lease
    /// across work that outlives one statement. Retiring its string must park the disposal rather than
    /// pull the pool out from under it.
    /// </summary>
    [Test]
    public void It_should_park_disposal_until_the_last_lease_of_a_retired_string_is_released()
    {
        _cache.Reconcile(OwnershipSnapshots.Of(1, PrimaryConnectionString));

        NpgsqlDataSourceLease held = _cache.AcquireLease(PrimaryConnectionString);
        NpgsqlDataSourceLease alsoHeld = _cache.AcquireLease(PrimaryConnectionString);
        NpgsqlDataSource dataSource = held.DataSource;

        _cache.Reconcile(OwnershipSnapshots.Empty(2));
        _lifetime.DisposeCountOf(dataSource).Should().Be(0, "two leases are still held");

        held.Dispose();
        _lifetime.DisposeCountOf(dataSource).Should().Be(0, "one lease is still held");

        alsoHeld.Dispose();
        _lifetime.DisposeCountOf(dataSource).Should().Be(1, "the last lease released a retired entry");
    }

    /// <summary>
    /// Several configured owners can name one database. It stops being owned only when the last of
    /// them stops naming it.
    /// </summary>
    [Test]
    public void It_should_retire_a_shared_string_only_after_its_final_owner_is_removed()
    {
        _cache.Reconcile(
            OwnershipSnapshots.Of(
                1,
                OwnershipSnapshots.Owner("tenant-a", 1, EffectiveTargetKind.Primary, PrimaryConnectionString),
                OwnershipSnapshots.Owner(
                    "tenant-b",
                    2,
                    EffectiveTargetKind.ReadReplica,
                    PrimaryConnectionString
                )
            )
        );

        NpgsqlDataSourceLease lease = _cache.AcquireLease(PrimaryConnectionString);
        NpgsqlDataSource dataSource = lease.DataSource;
        lease.Dispose();

        _cache.Reconcile(
            OwnershipSnapshots.Of(
                2,
                OwnershipSnapshots.Owner("tenant-a", 1, EffectiveTargetKind.Primary, PrimaryConnectionString)
            )
        );
        _lifetime.DisposeCountOf(dataSource).Should().Be(0, "the other tenant still names this database");

        _cache.Reconcile(OwnershipSnapshots.Empty(3));
        _lifetime.DisposeCountOf(dataSource).Should().Be(1);
    }

    /// <summary>
    /// Retirement is assigned from current ownership rather than only switched on, so a key removed
    /// and re-added while a lease is held keeps the live data source.
    /// </summary>
    [Test]
    public void It_should_reactivate_a_removed_and_readded_string_without_disposing_it()
    {
        _cache.Reconcile(OwnershipSnapshots.Of(1, PrimaryConnectionString));

        NpgsqlDataSourceLease held = _cache.AcquireLease(PrimaryConnectionString);
        NpgsqlDataSource dataSource = held.DataSource;

        _cache.Reconcile(OwnershipSnapshots.Empty(2));
        _cache.Reconcile(OwnershipSnapshots.Of(3, PrimaryConnectionString));

        held.Dispose();

        _lifetime.DisposeCountOf(dataSource).Should().Be(0, "the string is configured again");
        _lifetime.BuildCount.Should().Be(1);

        using NpgsqlDataSourceLease next = _cache.AcquireLease(PrimaryConnectionString);
        next.DataSource.Should().BeSameAs(dataSource, "the same live data source was reactivated");
        _lifetime.BuildCount.Should().Be(1);
    }

    /// <summary>
    /// Leasing a string nobody configures must not make the cache start owning it; the entry stays
    /// retired and is disposed as soon as the request that needed it is done.
    /// </summary>
    [Test]
    public void It_should_leave_an_unconfigured_string_retired_when_it_is_leased()
    {
        _cache.Reconcile(OwnershipSnapshots.Of(1, ReplicaConnectionString));

        NpgsqlDataSourceLease lease = _cache.AcquireLease(PrimaryConnectionString);
        NpgsqlDataSource dataSource = lease.DataSource;
        lease.Dispose();

        _lifetime.DisposeCountOf(dataSource).Should().Be(1);
    }

    [Test]
    public void It_should_ignore_a_snapshot_whose_version_is_not_greater()
    {
        _cache.Reconcile(OwnershipSnapshots.Of(5, PrimaryConnectionString));

        NpgsqlDataSourceLease lease = _cache.AcquireLease(PrimaryConnectionString);
        NpgsqlDataSource dataSource = lease.DataSource;
        lease.Dispose();

        _cache.Reconcile(OwnershipSnapshots.Empty(5));
        _cache.Reconcile(OwnershipSnapshots.Empty(4));

        _lifetime.DisposeCountOf(dataSource).Should().Be(0, "neither stale snapshot may retire anything");

        _cache.Reconcile(OwnershipSnapshots.Empty(6));
        _lifetime.DisposeCountOf(dataSource).Should().Be(1);
    }

    /// <summary>
    /// Reconciliation only ever performs string set operations, so a value no provider could open
    /// still participates in ownership.
    /// </summary>
    [Test]
    public void It_should_reconcile_a_provider_invalid_string_without_parsing_it()
    {
        const string Unparseable = "this is not a connection string at all";

        FluentActions
            .Invoking(() => _cache.Reconcile(OwnershipSnapshots.Of(1, Unparseable)))
            .Should()
            .NotThrow();

        FluentActions.Invoking(() => _cache.Reconcile(OwnershipSnapshots.Empty(2))).Should().NotThrow();
    }

    [Test]
    public void It_should_decrement_only_once_when_a_lease_is_disposed_twice()
    {
        _cache.Reconcile(OwnershipSnapshots.Of(1, PrimaryConnectionString));

        NpgsqlDataSourceLease first = _cache.AcquireLease(PrimaryConnectionString);
        NpgsqlDataSourceLease second = _cache.AcquireLease(PrimaryConnectionString);
        NpgsqlDataSource dataSource = first.DataSource;

        _cache.Reconcile(OwnershipSnapshots.Empty(2));

        DoubleDisposal.Of(first);
        first.IsReleased.Should().BeTrue();

        _lifetime
            .DisposeCountOf(dataSource)
            .Should()
            .Be(0, "the double disposal must not have released the other lease's claim");

        second.Dispose();
        _lifetime.DisposeCountOf(dataSource).Should().Be(1);
    }

    [Test]
    public async Task It_should_release_the_lease_when_the_open_fails()
    {
        _cache.Reconcile(OwnershipSnapshots.Of(1, PrimaryConnectionString));
        _lifetime.OnOpen = (_, _) => throw new InvalidOperationException("Simulated open failure.");

        Func<Task> act = async () => await _cache.OpenLeasedConnectionAsync(PrimaryConnectionString);
        await act.Should().ThrowAsync<InvalidOperationException>();

        NpgsqlDataSource dataSource = _lifetime.Built.Single();

        _cache.Reconcile(OwnershipSnapshots.Empty(2));

        _lifetime
            .DisposeCountOf(dataSource)
            .Should()
            .Be(1, "a failed open must leave no lease behind to park the disposal");
    }

    [Test]
    public async Task It_should_release_the_lease_when_the_open_is_cancelled()
    {
        _cache.Reconcile(OwnershipSnapshots.Of(1, PrimaryConnectionString));

        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();

        Func<Task> act = async () =>
            await _cache.OpenLeasedConnectionAsync(PrimaryConnectionString, cancellation.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();

        NpgsqlDataSource dataSource = _lifetime.Built.Single();

        _cache.Reconcile(OwnershipSnapshots.Empty(2));

        _lifetime.DisposeCountOf(dataSource).Should().Be(1);
    }

    /// <summary>
    /// The leased connection owns both halves: the connection is disposed first, then the claim is
    /// released, and a second disposal does neither again.
    /// </summary>
    [Test]
    public async Task It_should_release_the_lease_exactly_once_with_its_connection()
    {
        _cache.Reconcile(OwnershipSnapshots.Of(1, PrimaryConnectionString));

        LeasedNpgsqlConnection leased = await _cache.OpenLeasedConnectionAsync(PrimaryConnectionString);
        NpgsqlDataSource dataSource = _lifetime.Built.Single();

        _cache.Reconcile(OwnershipSnapshots.Empty(2));
        _lifetime.DisposeCountOf(dataSource).Should().Be(0, "the leased connection still holds the claim");

        await DoubleDisposal.OfAsync(leased);

        _lifetime.DisposeCountOf(dataSource).Should().Be(1);
    }

    /// <summary>
    /// An owner that holds a transaction over a leased connection must give the claim back even when
    /// disposing the transaction throws. Stranding it does not fail visibly: it parks a retired data
    /// source for the life of the process.
    /// </summary>
    [Test]
    public async Task It_should_release_an_owned_connection_when_the_preceding_disposal_throws()
    {
        _cache.Reconcile(OwnershipSnapshots.Of(1, PrimaryConnectionString));

        LeasedNpgsqlConnection leased = await _cache.OpenLeasedConnectionAsync(PrimaryConnectionString);
        NpgsqlDataSource dataSource = _lifetime.Built.Single();

        Func<Task> act = async () =>
            await LeasedNpgsqlConnection.DisposeOwnedAsync(new ThrowingAsyncDisposable(), leased);

        await act.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("Simulated transaction disposal failure.");

        _cache.Reconcile(OwnershipSnapshots.Empty(2));

        _lifetime
            .DisposeCountOf(dataSource)
            .Should()
            .Be(1, "the claim was released despite the transaction disposal failure");
    }

    /// <summary>
    /// When both disposals fail, the transaction's exception is the one the caller must see: it is
    /// what started the cleanup, and a cleanup fault replacing it would hide the original failure.
    /// </summary>
    [Test]
    public async Task It_should_propagate_the_preceding_failure_when_the_owned_disposal_also_throws()
    {
        Func<Task> act = async () =>
            await LeasedNpgsqlConnection.DisposeOwnedAsync(
                new ThrowingAsyncDisposable(),
                new ThrowingAsyncDisposable("Simulated owned cleanup failure.")
            );

        await act.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("Simulated transaction disposal failure.");
    }

    [Test]
    public async Task It_should_release_an_owned_connection_exactly_once_on_the_ordinary_path()
    {
        _cache.Reconcile(OwnershipSnapshots.Of(1, PrimaryConnectionString));

        LeasedNpgsqlConnection leased = await _cache.OpenLeasedConnectionAsync(PrimaryConnectionString);
        NpgsqlDataSource dataSource = _lifetime.Built.Single();
        RecordingAsyncDisposable transaction = new();

        await LeasedNpgsqlConnection.DisposeOwnedAsync(transaction, leased);
        await LeasedNpgsqlConnection.DisposeOwnedAsync(transaction, leased);

        transaction.DisposeCount.Should().Be(2, "each call disposes what it was handed");

        _cache.Reconcile(OwnershipSnapshots.Empty(2));
        _lifetime
            .DisposeCountOf(dataSource)
            .Should()
            .Be(1, "the second release must not decrement another holder's claim");
    }

    /// <summary>
    /// Ownership publication is the compute half and can throw; everything after it is assignment. If
    /// the version moved first, a retry carrying that same version would look stale and the old owner
    /// set would stay live for good.
    /// </summary>
    [Test]
    public void It_should_apply_a_retried_snapshot_after_a_failed_one_of_the_same_version()
    {
        _cache.Reconcile(OwnershipSnapshots.Of(1, PrimaryConnectionString));

        NpgsqlDataSourceLease lease = _cache.AcquireLease(PrimaryConnectionString);
        NpgsqlDataSource dataSource = lease.DataSource;
        lease.Dispose();

        // A default ImmutableArray throws when enumerated, which is where the compute half faults.
        FluentActions
            .Invoking(() => _cache.Reconcile(new DataStoreOwnershipSnapshot(2, default)))
            .Should()
            .Throw<Exception>();

        _lifetime.DisposeCountOf(dataSource).Should().Be(0, "a failed publication must retire nothing");

        _cache.Reconcile(OwnershipSnapshots.Empty(2));

        _lifetime
            .DisposeCountOf(dataSource)
            .Should()
            .Be(1, "the retry at the same version must apply in full");
    }

    [Test]
    public void It_should_dispose_every_data_source_when_the_cache_is_disposed()
    {
        _cache.Reconcile(OwnershipSnapshots.Of(1, PrimaryConnectionString, ReplicaConnectionString));

        using NpgsqlDataSourceLease primary = _cache.AcquireLease(PrimaryConnectionString);
        using NpgsqlDataSourceLease replica = _cache.AcquireLease(ReplicaConnectionString);

        _cache.Dispose();

        _lifetime.DisposeCountOf(primary.DataSource).Should().Be(1);
        _lifetime.DisposeCountOf(replica.DataSource).Should().Be(1);
    }

    [Test]
    public void It_should_refuse_a_lease_after_the_cache_is_disposed()
    {
        _cache.Dispose();

        FluentActions
            .Invoking(() => _cache.AcquireLease(PrimaryConnectionString))
            .Should()
            .Throw<ObjectDisposedException>();
    }

    /// <summary>
    /// The build runs outside the lock, so the cache can be disposed while one is in flight. The
    /// acquisition still refuses, but the candidate it already built belongs to nobody else and must
    /// be disposed rather than leaked.
    /// </summary>
    [Test]
    public void It_should_dispose_a_candidate_built_while_the_cache_was_being_disposed()
    {
        _lifetime.OnBuild = _ => _cache.Dispose();

        FluentActions
            .Invoking(() => _cache.AcquireLease(PrimaryConnectionString))
            .Should()
            .Throw<ObjectDisposedException>();

        NpgsqlDataSource candidate = _lifetime.Built.Should().ContainSingle().Subject;
        _lifetime.DisposeCountOf(candidate).Should().Be(1);
    }

    /// <summary>
    /// A leaked pool costs memory; one disposed while still in use costs a request. So a failing
    /// disposal parks that one source and every other source is still disposed.
    /// </summary>
    [Test]
    public void It_should_isolate_a_failing_disposal_from_the_others()
    {
        _cache.Reconcile(
            OwnershipSnapshots.Of(
                1,
                PrimaryConnectionString,
                ReplicaConnectionString,
                SnapshotConnectionString
            )
        );

        NpgsqlDataSourceLease primary = _cache.AcquireLease(PrimaryConnectionString);
        NpgsqlDataSourceLease replica = _cache.AcquireLease(ReplicaConnectionString);
        NpgsqlDataSourceLease snapshot = _cache.AcquireLease(SnapshotConnectionString);

        NpgsqlDataSource failing = replica.DataSource;
        _lifetime.FailDisposeFor.Add(failing);

        primary.Dispose();
        replica.Dispose();
        snapshot.Dispose();

        FluentActions
            .Invoking(() => _cache.Reconcile(OwnershipSnapshots.Empty(2)))
            .Should()
            .NotThrow("one provider failure must not abandon the rest of the cleanup");

        _lifetime.DisposeCountOf(primary.DataSource).Should().Be(1);
        _lifetime.DisposeCountOf(failing).Should().Be(1);
        _lifetime.DisposeCountOf(snapshot.DataSource).Should().Be(1);
        _logger.Messages.Should().Contain(message => message.Contains("Error disposing"));
    }

    /// <summary>
    /// Ownership is derived from connection strings, so every log statement on these paths is one
    /// mistake away from publishing a password. The provider exception is made to quote the
    /// connection string itself, as a real Npgsql failure may, and a hash derived from the string is
    /// as unwelcome as the string: nothing but the exception's type may reach the log.
    /// </summary>
    [Test]
    public void It_should_never_log_connection_material()
    {
        _cache.Reconcile(OwnershipSnapshots.Of(1, PrimaryConnectionString));

        NpgsqlDataSourceLease lease = _cache.AcquireLease(PrimaryConnectionString);
        _lifetime.OnDispose = _ =>
            throw new InvalidOperationException(
                $"Simulated provider disposal failure quoting {PrimaryConnectionString}"
            );
        lease.Dispose();

        _cache.Reconcile(OwnershipSnapshots.Empty(2));
        _cache.Dispose();

        string ordinalHash = PrimaryConnectionString
            .GetHashCode(StringComparison.Ordinal)
            .ToString(CultureInfo.InvariantCulture);

        _logger
            .Messages.Should()
            .NotContain(message =>
                message.Contains("Password=", StringComparison.OrdinalIgnoreCase)
                || message.Contains("Host=primary", StringComparison.Ordinal)
                || message.Contains(PrimaryConnectionString, StringComparison.Ordinal)
                || message.Contains(ordinalHash, StringComparison.Ordinal)
            );
    }

    /// <summary>
    /// "No untracked or immortal data source" is only structural if there is no way to obtain one
    /// without the lease that keeps it alive, so the absence of an unleased entry point is asserted
    /// rather than left to review.
    /// </summary>
    [Test]
    public void It_should_expose_no_unleased_public_entry_point()
    {
        Type cacheType = typeof(NpgsqlDataSourceCache);

        cacheType
            .GetMethod(
                "GetOrCreate",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static
            )
            .Should()
            .BeNull("GetOrCreate was removed rather than retained as a shim");

        cacheType
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Where(method => method.ReturnType == typeof(NpgsqlDataSource))
            .Should()
            .BeEmpty();

        cacheType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Should()
            .BeEmpty();

        cacheType
            .GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Should()
            .BeEmpty();

        string[] expectedPublicSurface =
        [
            nameof(NpgsqlDataSourceCache.AcquireLease),
            nameof(NpgsqlDataSourceCache.OpenLeasedConnectionAsync),
            nameof(NpgsqlDataSourceCache.Reconcile),
            nameof(NpgsqlDataSourceCache.Dispose),
        ];

        cacheType
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(method => method.Name)
            .Should()
            .BeEquivalentTo(expectedPublicSurface);
    }
}
