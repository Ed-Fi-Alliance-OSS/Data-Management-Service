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
/// SQL Server ownership: which configured targets keep a pool identity alive, and what retires one.
/// Clearing a SqlClient pool is identity-scoped rather than object-scoped, so a clear issued for a
/// string discards whatever pool that string currently names - which is why nothing here may be
/// approximate.
/// </summary>
[TestFixture]
public class Given_Mssql_Ownership_Reconciliation
{
    private const string PrimaryText = "Server=primary;Database=dms;User Id=u;Password=p";
    private const string ReplicaText = "Server=replica;Database=dms;User Id=u;Password=p";
    private const string SnapshotText = "Server=snapshot;Database=dms;User Id=u;Password=p";

    private GatedSqlServerPoolClearing _poolClearing = null!;
    private CapturingLogger<MssqlConnectionAcquisition> _logger = null!;
    private MssqlConnectionAcquisition _acquisition = null!;

    [SetUp]
    public void Setup()
    {
        _poolClearing = new GatedSqlServerPoolClearing();
        _logger = new CapturingLogger<MssqlConnectionAcquisition>();
        _acquisition = new MssqlConnectionAcquisition(
            _poolClearing,
            _logger,
            _ => new OwnershipProbeConnection()
        );
        _poolClearing.Acquisition = _acquisition;
    }

    [TearDown]
    public void TearDown() =>
        _poolClearing
            .LockViolations.Should()
            .BeEmpty("no provider work may run while the state lock is held");

    private static EffectiveDataStoreTarget Primary(string text) => EffectiveDataStoreTarget.Primary(text);

    private static EffectiveDataStoreTarget Replica(string text) =>
        new(EffectiveTargetKind.ReadReplica, text);

    private static EffectiveDataStoreTarget Snapshot(string text) => new(EffectiveTargetKind.Snapshot, text);

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

    [Test]
    public async Task It_should_clear_the_exact_pool_when_an_unleased_owner_is_removed()
    {
        EffectiveDataStoreTarget replica = Replica(ReplicaText);
        _acquisition.Reconcile(Owning(1, replica));

        MssqlConnectionLease lease = await _acquisition.AcquireLeaseAsync(replica);
        await lease.DisposeAsync();

        _acquisition.Reconcile(Owning(2));

        _poolClearing.Cleared.Should().ContainSingle().Which.Should().Be(Effective(replica));
    }

    /// <summary>
    /// Ownership state is replaced wholesale by each snapshot, never merged, so a removed owner -
    /// acquired or not - retains no identity on the side. Retention here would be retention of
    /// connection-string material for configuration that no longer exists.
    /// </summary>
    [Test]
    public void It_should_retain_no_identity_for_a_removed_owner_that_was_never_acquired()
    {
        _acquisition.Reconcile(Owning(1, Replica(ReplicaText), Snapshot(SnapshotText)));
        _acquisition.OwnedIdentityCount.Should().Be(2);

        _acquisition.Reconcile(Owning(2, Replica(ReplicaText)));

        _acquisition.OwnedIdentityCount.Should().Be(1);
        _poolClearing.Cleared.Should().BeEmpty("a never-acquired owner has no pool to clear");
    }

    [Test]
    public async Task It_should_clear_nothing_while_the_owner_is_still_configured()
    {
        EffectiveDataStoreTarget replica = Replica(ReplicaText);
        _acquisition.Reconcile(Owning(1, replica));

        MssqlConnectionLease lease = await _acquisition.AcquireLeaseAsync(replica);
        await lease.DisposeAsync();

        _acquisition.Reconcile(Owning(2, replica));

        _poolClearing.Cleared.Should().BeEmpty();
    }

    /// <summary>
    /// A configured owner that no request ever acquired has no pool, so its removal has nothing to
    /// clear. Clearing anyway would discard a pool it never created. Reconciliation realizes the
    /// owner's string - ownership is by effective string - but realization creates no pool.
    /// </summary>
    [Test]
    public void It_should_clear_nothing_for_an_owner_that_was_never_acquired()
    {
        _acquisition.Reconcile(Owning(1, Replica(ReplicaText)));
        _acquisition.Reconcile(Owning(2));

        _poolClearing.Cleared.Should().BeEmpty();
        _acquisition
            .RealizationCount.Should()
            .Be(
                1,
                "reconciliation realizes each configured owner once per publication, and acquires nothing"
            );
    }

    /// <summary>
    /// A derivative whose configured text no provider can parse realizes to nothing - reconciliation
    /// tolerates the parse failure - so it contributes neither a memo entry nor a pool and cannot
    /// disturb publication.
    /// </summary>
    [Test]
    public async Task It_should_clear_nothing_for_a_provider_invalid_owner()
    {
        EffectiveDataStoreTarget invalid = Snapshot("this is not a connection string at all");
        _acquisition.Reconcile(Owning(1, invalid));

        Func<Task> acquire = () => _acquisition.AcquireLeaseAsync(invalid);
        await acquire.Should().ThrowAsync<ArgumentException>();

        _acquisition.Reconcile(Owning(2));

        _poolClearing.Cleared.Should().BeEmpty();
    }

    /// <summary>
    /// Several configured owners can realize to one effective string. It stops being owned only when
    /// the last of them stops naming it, and it is cleared exactly once at that point.
    /// </summary>
    [Test]
    public async Task It_should_clear_a_shared_identity_only_after_its_final_owner_is_removed()
    {
        EffectiveDataStoreTarget replica = Replica(ReplicaText);
        EffectiveDataStoreTarget snapshot = Snapshot(ReplicaText);

        Effective(snapshot)
            .Should()
            .Be(Effective(replica), "both derivatives of one string realize to one identity");

        _acquisition.Reconcile(Owning(1, replica, snapshot));

        MssqlConnectionLease first = await _acquisition.AcquireLeaseAsync(replica);
        await first.DisposeAsync();
        MssqlConnectionLease second = await _acquisition.AcquireLeaseAsync(snapshot);
        await second.DisposeAsync();

        _acquisition.Reconcile(Owning(2, replica));
        _poolClearing.Cleared.Should().BeEmpty("one owner still names this identity");

        _acquisition.Reconcile(Owning(3));
        _poolClearing.ClearCountOf(Effective(replica)).Should().Be(1);
    }

    /// <summary>
    /// The inverse of the case above: only one of two owners sharing a configured string was ever
    /// acquired, and it is the other one that remains. Reconciliation realized the remaining owner at
    /// publication, so its memo entry proves it maps to the acquired owner's identity - and removing
    /// the acquired owner must therefore clear nothing.
    /// </summary>
    [Test]
    public async Task It_should_not_clear_a_shared_identity_while_an_unacquired_owner_remains()
    {
        EffectiveDataStoreTarget replica = Replica(ReplicaText);
        EffectiveDataStoreTarget snapshot = Snapshot(ReplicaText);

        _acquisition.Reconcile(Owning(1, replica, snapshot));

        MssqlConnectionLease lease = await _acquisition.AcquireLeaseAsync(replica);
        await lease.DisposeAsync();

        // The acquired replica is removed; the never-acquired snapshot still names the same text.
        _acquisition.Reconcile(Owning(2, snapshot));

        _poolClearing.Cleared.Should().BeEmpty("the remaining owner realizes to this identity");

        _acquisition.Reconcile(Owning(3));
        _poolClearing.ClearCountOf(Effective(replica)).Should().Be(1);
    }

    /// <summary>
    /// SqlClient canonicalizes keyword synonyms and ordering, so two different configured texts can
    /// realize to one effective string - one physical pool. Ownership is by effective string, so
    /// removing one spelling while the other remains configured - even never acquired - must clear
    /// nothing: a clear would discard the pool the equivalent owner still holds.
    /// </summary>
    [Test]
    public async Task It_should_not_clear_a_pool_while_a_text_different_equivalent_owner_remains()
    {
        EffectiveDataStoreTarget replica = Replica(ReplicaText);
        EffectiveDataStoreTarget equivalent = Replica(
            "Data Source=replica;Initial Catalog=dms;User ID=u;Password=p"
        );

        equivalent
            .ConnectionString.Should()
            .NotBe(replica.ConnectionString, "the whole point is two spellings of one pool");
        Effective(equivalent)
            .Should()
            .Be(Effective(replica), "SqlClient canonicalizes keyword synonyms to one effective string");

        _acquisition.Reconcile(Owning(1, replica, equivalent));

        MssqlConnectionLease lease = await _acquisition.AcquireLeaseAsync(replica);
        await lease.DisposeAsync();

        // The acquired spelling is removed; the never-acquired equivalent spelling remains.
        _acquisition.Reconcile(Owning(2, equivalent));
        _poolClearing.Cleared.Should().BeEmpty("an equivalent configured owner still holds this pool");

        _acquisition.Reconcile(Owning(3));
        _poolClearing.ClearCountOf(Effective(replica)).Should().Be(1);
    }

    /// <summary>
    /// A primary passes through byte for byte, so a primary whose configured text happens to equal a
    /// derivative's realized form names the same physical pool. Ownership is by effective string, so
    /// removing the derivative while that primary remains must clear nothing.
    /// </summary>
    [Test]
    public async Task It_should_not_clear_a_pool_a_primary_with_the_derivative_effective_text_still_owns()
    {
        EffectiveDataStoreTarget replica = Replica(ReplicaText);
        EffectiveDataStoreTarget primary = Primary(Effective(replica));

        Effective(primary).Should().Be(Effective(replica), "a primary passes through byte for byte");

        _acquisition.Reconcile(Owning(1, primary, replica));

        MssqlConnectionLease lease = await _acquisition.AcquireLeaseAsync(replica);
        await lease.DisposeAsync();

        // The derivative is removed; the never-acquired primary still names the same effective pool.
        _acquisition.Reconcile(Owning(2, primary));
        _poolClearing.Cleared.Should().BeEmpty("the primary still owns this effective string");

        _acquisition.Reconcile(Owning(3));
        _poolClearing.ClearCountOf(Effective(replica)).Should().Be(1);
    }

    /// <summary>
    /// A request arriving for an owner that has just been removed still gets its connection, and the
    /// identity it names must not be retired while a different configured owner still realizes to it.
    /// Retiring on the key alone would clear a pool another owner is using.
    /// </summary>
    [Test]
    public async Task It_should_not_retire_a_shared_identity_for_a_stale_request()
    {
        EffectiveDataStoreTarget replica = Replica(ReplicaText);
        EffectiveDataStoreTarget snapshot = Snapshot(ReplicaText);

        _acquisition.Reconcile(Owning(1, replica, snapshot));

        MssqlConnectionLease seed = await _acquisition.AcquireLeaseAsync(snapshot);
        await seed.DisposeAsync();

        // The replica is dropped; the snapshot still names the same identity.
        _acquisition.Reconcile(Owning(2, snapshot));

        MssqlConnectionLease stale = await _acquisition.AcquireLeaseAsync(replica);
        await stale.DisposeAsync();

        _poolClearing
            .Cleared.Should()
            .BeEmpty("the identity is still owned by the snapshot, whatever key this request named");

        _acquisition.Reconcile(Owning(3));
        _poolClearing.ClearCountOf(Effective(snapshot)).Should().Be(1);
    }

    /// <summary>
    /// Only the derivative is rebuilt with NeverBlock, so a primary and a derivative whose stored text
    /// is byte-identical are two identities and two pools.
    /// </summary>
    [Test]
    public async Task It_should_treat_a_primary_and_a_derivative_of_identical_text_as_distinct_pools()
    {
        EffectiveDataStoreTarget primary = Primary(PrimaryText);
        EffectiveDataStoreTarget replica = Replica(PrimaryText);

        Effective(replica).Should().NotBe(Effective(primary));
        Effective(primary).Should().Be(PrimaryText, "a primary passes through byte for byte");

        _acquisition.Reconcile(Owning(1, primary, replica));

        MssqlConnectionLease primaryLease = await _acquisition.AcquireLeaseAsync(primary);
        MssqlConnectionLease replicaLease = await _acquisition.AcquireLeaseAsync(replica);
        await primaryLease.DisposeAsync();
        await replicaLease.DisposeAsync();

        _acquisition.Reconcile(Owning(2, primary));

        _poolClearing.Cleared.Should().ContainSingle().Which.Should().Be(Effective(replica));
        _poolClearing.ClearCountOf(PrimaryText).Should().Be(0, "the primary is still configured");
    }

    /// <summary>
    /// A held lease parks the clear. The request that holds it completes normally, and the clear
    /// happens once, when the last claim is given back.
    /// </summary>
    [Test]
    public async Task It_should_park_the_clear_until_the_last_lease_of_a_retired_identity_is_released()
    {
        EffectiveDataStoreTarget replica = Replica(ReplicaText);
        _acquisition.Reconcile(Owning(1, replica));

        MssqlConnectionLease held = await _acquisition.AcquireLeaseAsync(replica);
        MssqlConnectionLease alsoHeld = await _acquisition.AcquireLeaseAsync(replica);

        _acquisition.Reconcile(Owning(2));
        _poolClearing.Cleared.Should().BeEmpty("two claims are still held");

        await held.DisposeAsync();
        _poolClearing.Cleared.Should().BeEmpty("one claim is still held");

        await alsoHeld.DisposeAsync();
        _poolClearing.ClearCountOf(Effective(replica)).Should().Be(1);
    }

    /// <summary>
    /// Retirement is assigned from current ownership rather than only switched on, so an owner removed
    /// and re-added while a lease is held keeps its pool.
    /// </summary>
    [Test]
    public async Task It_should_reactivate_a_removed_and_readded_owner_without_clearing()
    {
        EffectiveDataStoreTarget replica = Replica(ReplicaText);
        _acquisition.Reconcile(Owning(1, replica));

        MssqlConnectionLease held = await _acquisition.AcquireLeaseAsync(replica);

        _acquisition.Reconcile(Owning(2));
        _acquisition.Reconcile(Owning(3, replica));

        await held.DisposeAsync();

        _poolClearing.Cleared.Should().BeEmpty("the owner is configured again");

        long realizationsBefore = _acquisition.RealizationCount;
        MssqlConnectionLease next = await _acquisition.AcquireLeaseAsync(replica);
        await next.DisposeAsync();

        _poolClearing.Cleared.Should().BeEmpty();
        _acquisition
            .RealizationCount.Should()
            .Be(realizationsBefore + 1, "only the new acquisition realized; reactivation reused the memo");
    }

    /// <summary>
    /// Leasing an identity nobody configures must not make it owned; it retires as soon as the request
    /// that needed it is done.
    /// </summary>
    [Test]
    public async Task It_should_leave_an_unconfigured_identity_retired_when_it_is_leased()
    {
        _acquisition.Reconcile(Owning(1, Replica(ReplicaText)));

        EffectiveDataStoreTarget stranger = Snapshot(SnapshotText);
        MssqlConnectionLease lease = await _acquisition.AcquireLeaseAsync(stranger);
        await lease.DisposeAsync();

        _poolClearing.ClearCountOf(Effective(stranger)).Should().Be(1);
    }

    [Test]
    public async Task It_should_ignore_a_snapshot_whose_version_is_not_greater()
    {
        EffectiveDataStoreTarget replica = Replica(ReplicaText);
        _acquisition.Reconcile(Owning(5, replica));

        MssqlConnectionLease lease = await _acquisition.AcquireLeaseAsync(replica);
        await lease.DisposeAsync();

        _acquisition.Reconcile(Owning(5));
        _acquisition.Reconcile(Owning(4));
        _poolClearing.Cleared.Should().BeEmpty("neither stale snapshot may retire anything");

        _acquisition.Reconcile(Owning(6));
        _poolClearing.ClearCountOf(Effective(replica)).Should().Be(1);
    }

    /// <summary>
    /// Publication is the compute half and can throw; everything after it is assignment. If the version
    /// moved first, a retry carrying that same version would look stale and the old owner set would
    /// stay live for good.
    /// </summary>
    [Test]
    public async Task It_should_apply_a_retried_snapshot_after_a_failed_one_of_the_same_version()
    {
        EffectiveDataStoreTarget replica = Replica(ReplicaText);
        _acquisition.Reconcile(Owning(1, replica));

        MssqlConnectionLease lease = await _acquisition.AcquireLeaseAsync(replica);
        await lease.DisposeAsync();

        // A default ImmutableArray throws when enumerated, which is where the compute half faults.
        FluentActions
            .Invoking(() => _acquisition.Reconcile(new DataStoreOwnershipSnapshot(2, default)))
            .Should()
            .Throw<Exception>();

        _poolClearing.Cleared.Should().BeEmpty("a failed publication must retire nothing");

        _acquisition.Reconcile(Owning(2));
        _poolClearing.ClearCountOf(Effective(replica)).Should().Be(1);
    }

    [Test]
    public async Task It_should_release_the_claim_exactly_once_when_a_lease_is_disposed_twice()
    {
        EffectiveDataStoreTarget replica = Replica(ReplicaText);
        _acquisition.Reconcile(Owning(1, replica));

        MssqlConnectionLease first = await _acquisition.AcquireLeaseAsync(replica);
        MssqlConnectionLease second = await _acquisition.AcquireLeaseAsync(replica);

        _acquisition.Reconcile(Owning(2));

        await DoubleDisposal.OfAsync(first);
        _poolClearing.Cleared.Should().BeEmpty("the other holder's claim must still be counted");

        await second.DisposeAsync();
        _poolClearing.ClearCountOf(Effective(replica)).Should().Be(1);
    }

    [Test]
    public async Task It_should_release_the_claim_when_the_open_fails()
    {
        EffectiveDataStoreTarget replica = Replica(ReplicaText);
        MssqlConnectionAcquisition acquisition = new(
            _poolClearing,
            _logger,
            _ => throw new InvalidOperationException("Simulated connection construction failure.")
        );
        _poolClearing.Acquisition = acquisition;

        acquisition.Reconcile(Owning(1, replica));

        MssqlConnectionLease lease = await acquisition.AcquireLeaseAsync(replica);
        Func<Task> open = () => lease.OpenAsync(CancellationToken.None);
        await open.Should().ThrowAsync<InvalidOperationException>();

        acquisition.Reconcile(Owning(2));
        _poolClearing.Cleared.Should().BeEmpty("the caller still holds its claim");

        await lease.DisposeAsync();
        _poolClearing.ClearCountOf(Effective(replica)).Should().Be(1);
    }

    [Test]
    public async Task It_should_reject_an_acquisition_cancelled_before_it_starts()
    {
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();

        Func<Task> acquire = () => _acquisition.AcquireLeaseAsync(Replica(ReplicaText), cancellation.Token);

        await acquire.Should().ThrowAsync<OperationCanceledException>();
        _acquisition.RealizationCount.Should().Be(0, "nothing was realized for a cancelled request");
    }

    /// <summary>
    /// Clearing constructs a SqlConnection from the effective string, so a provider failure there
    /// quotes the offending keyword back. Nothing of it may reach the log.
    /// </summary>
    [Test]
    public async Task It_should_never_log_connection_material_when_a_clear_fails()
    {
        EffectiveDataStoreTarget replica = Replica(ReplicaText);
        _acquisition.Reconcile(Owning(1, replica));

        MssqlConnectionLease lease = await _acquisition.AcquireLeaseAsync(replica);
        await lease.DisposeAsync();

        _poolClearing.FailClearFor.Add(Effective(replica));

        FluentActions
            .Invoking(() => _acquisition.Reconcile(Owning(2)))
            .Should()
            .NotThrow("a failed clear leaves the pool to the driver rather than failing publication");

        _logger.Messages.Should().Contain(message => message.Contains("Error clearing"));
        _logger
            .Messages.Should()
            .NotContain(message =>
                message.Contains("Password=", StringComparison.OrdinalIgnoreCase)
                || message.Contains("Server=replica", StringComparison.Ordinal)
                || message.Contains(ReplicaText, StringComparison.Ordinal)
            );
    }

    /// <summary>
    /// Clearing every pool would discard identities that are still configured and in use, so the only
    /// clearing the backend can reach is the exact-pool one.
    /// </summary>
    [Test]
    public void It_should_expose_only_exact_pool_clearing()
    {
        string[] surface = [nameof(ISqlServerPoolClearing.ClearPool)];

        typeof(ISqlServerPoolClearing)
            .GetMethods()
            .Select(method => method.Name)
            .Should()
            .BeEquivalentTo(surface);
    }
}
