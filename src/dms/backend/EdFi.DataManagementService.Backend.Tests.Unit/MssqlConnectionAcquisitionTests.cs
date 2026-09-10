// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using EdFi.DataManagementService.Backend.Mssql;
using EdFi.DataManagementService.Core.External.Backend;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

public class MssqlConnectionAcquisitionTests
{
    private const string PrimaryConnectionString =
        "Server=localhost,1433;Database=edfi;User Id=sa;Password=abcdefgh1!;TrustServerCertificate=true;";

    /// <summary>
    /// A primary target must reach the provider byte for byte. Anything that rebuilt it - even a
    /// round-trip through SqlConnectionStringBuilder that changed nothing semantically - would
    /// reorder or re-case keywords and give the primary a different pool identity than it has today.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_A_Primary_Target
    {
        [Test]
        public void It_realizes_the_configured_string_byte_for_byte()
        {
            MssqlConnectionAcquisition
                .RealizeEffectiveConnectionString(EffectiveDataStoreTarget.Primary(PrimaryConnectionString))
                .Should()
                .Be(PrimaryConnectionString);
        }

        [Test]
        public void It_preserves_an_operator_supplied_pool_blocking_period()
        {
            const string WithOperatorValue = PrimaryConnectionString + "Pool Blocking Period=AlwaysBlock;";

            MssqlConnectionAcquisition
                .RealizeEffectiveConnectionString(EffectiveDataStoreTarget.Primary(WithOperatorValue))
                .Should()
                .Be(WithOperatorValue);
        }

        /// <summary>
        /// A malformed primary string is not this boundary's concern: it is passed through unchanged,
        /// exactly as before, and fails wherever it failed previously.
        /// </summary>
        [Test]
        public void It_does_not_reject_a_provider_invalid_string()
        {
            const string NotAConnectionString = "this is not a connection string at all";

            MssqlConnectionAcquisition
                .RealizeEffectiveConnectionString(EffectiveDataStoreTarget.Primary(NotAConnectionString))
                .Should()
                .Be(NotAConnectionString);
        }

        [Test]
        public async Task It_leases_against_the_configured_string()
        {
            MssqlConnectionAcquisition acquisition = new(
                new SqlClientPoolClearing(),
                NullLogger<MssqlConnectionAcquisition>.Instance,
                _ => new SqlConnection()
            );

            using MssqlConnectionLease lease = await acquisition.AcquireLeaseAsync(
                EffectiveDataStoreTarget.Primary(PrimaryConnectionString)
            );

            lease.EffectiveConnectionString.Should().Be(PrimaryConnectionString);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Derivative_Target
    {
        [TestCase(EffectiveTargetKind.Snapshot)]
        [TestCase(EffectiveTargetKind.ReadReplica)]
        public void It_forces_the_pool_blocking_period_to_never_block(EffectiveTargetKind kind)
        {
            string effective = MssqlConnectionAcquisition.RealizeEffectiveConnectionString(
                new EffectiveDataStoreTarget(kind, PrimaryConnectionString)
            );

            new SqlConnectionStringBuilder(effective)
                .PoolBlockingPeriod.Should()
                .Be(PoolBlockingPeriod.NeverBlock);
        }

        /// <summary>
        /// Enlistment off, overriding whatever the operator configured. A derivative is read-only, so it
        /// has no business joining an ambient distributed transaction - and refusing to enlist is what
        /// leaves the pool-acquisition timeout as the only InvalidOperationException a derivative open
        /// can raise, which is the invariant the restatement in OpenAsync reads.
        /// </summary>
        [TestCase(EffectiveTargetKind.Snapshot)]
        [TestCase(EffectiveTargetKind.ReadReplica)]
        public void It_forces_enlistment_off_over_an_operator_supplied_value(EffectiveTargetKind kind)
        {
            string configured = PrimaryConnectionString + "Enlist=true";

            string effective = MssqlConnectionAcquisition.RealizeEffectiveConnectionString(
                new EffectiveDataStoreTarget(kind, configured)
            );

            new SqlConnectionStringBuilder(effective).Enlist.Should().BeFalse();
        }

        /// <summary>
        /// The forced value overrides whatever the operator configured. Without it, SqlClient replays a
        /// failed login or timeout from its blocking period on the request that immediately follows,
        /// which is precisely the recovery this policy exists to prevent.
        /// </summary>
        [TestCase("AlwaysBlock")]
        [TestCase("AlwaysBlock;")]
        public void It_overrides_an_operator_supplied_pool_blocking_period(string operatorValue)
        {
            string configured = PrimaryConnectionString + $"Pool Blocking Period={operatorValue}";

            string effective = MssqlConnectionAcquisition.RealizeEffectiveConnectionString(
                new EffectiveDataStoreTarget(EffectiveTargetKind.Snapshot, configured)
            );

            new SqlConnectionStringBuilder(effective)
                .PoolBlockingPeriod.Should()
                .Be(PoolBlockingPeriod.NeverBlock);
        }

        /// <summary>
        /// A primary and a derivative whose stored text is byte-identical must not share a pool, because
        /// only the derivative carries the forced setting.
        /// </summary>
        [Test]
        public void It_realizes_a_different_identity_than_a_primary_with_identical_stored_text()
        {
            string primary = MssqlConnectionAcquisition.RealizeEffectiveConnectionString(
                EffectiveDataStoreTarget.Primary(PrimaryConnectionString)
            );
            string derivative = MssqlConnectionAcquisition.RealizeEffectiveConnectionString(
                new EffectiveDataStoreTarget(EffectiveTargetKind.Snapshot, PrimaryConnectionString)
            );

            derivative.Should().NotBe(primary);
        }

        [Test]
        public void It_realizes_one_identity_for_two_derivatives_with_the_same_stored_text()
        {
            string snapshot = MssqlConnectionAcquisition.RealizeEffectiveConnectionString(
                new EffectiveDataStoreTarget(EffectiveTargetKind.Snapshot, PrimaryConnectionString)
            );
            string replica = MssqlConnectionAcquisition.RealizeEffectiveConnectionString(
                new EffectiveDataStoreTarget(EffectiveTargetKind.ReadReplica, PrimaryConnectionString)
            );

            replica.Should().Be(snapshot);
        }

        /// <summary>
        /// A decrypted, non-blank but provider-invalid derivative string is configured and selectable;
        /// it fails here, at acquisition, and nowhere earlier.
        /// </summary>
        [Test]
        public async Task It_rejects_a_provider_invalid_string_at_acquisition()
        {
            MssqlConnectionAcquisition acquisition = new(
                new SqlClientPoolClearing(),
                NullLogger<MssqlConnectionAcquisition>.Instance,
                _ => new SqlConnection()
            );

            Func<Task> acquire = () =>
                acquisition.AcquireLeaseAsync(
                    new EffectiveDataStoreTarget(
                        EffectiveTargetKind.Snapshot,
                        "this is not a connection string at all"
                    )
                );

            await acquire.Should().ThrowAsync<ArgumentException>();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Lease
    {
        private static MssqlConnectionAcquisition AcquisitionReturning(DbConnection connection) =>
            new(
                new SqlClientPoolClearing(),
                NullLogger<MssqlConnectionAcquisition>.Instance,
                _ => connection
            );

        [Test]
        public async Task It_releases_exactly_once_when_disposed_twice()
        {
            using RecordingConnection connection = new();
            MssqlConnectionLease lease = await AcquisitionReturning(connection)
                .AcquireLeaseAsync(EffectiveDataStoreTarget.Primary(PrimaryConnectionString));

            await lease.DisposeAsync();
            lease.IsReleased.Should().BeTrue();

            // Disposing again, and by both routes, is the behavior under test: a caller that releases
            // twice must decrement the claim exactly once.
#pragma warning disable S3966, S6966 // Redundant disposal - deliberate: idempotence is the assertion
            await lease.DisposeAsync();
            lease.Dispose();
#pragma warning restore S3966, S6966

            lease.IsReleased.Should().BeTrue();
        }

        [Test]
        public async Task It_refuses_to_create_a_connection_after_release()
        {
            using RecordingConnection connection = new();
            MssqlConnectionLease lease = await AcquisitionReturning(connection)
                .AcquireLeaseAsync(EffectiveDataStoreTarget.Primary(PrimaryConnectionString));

            await lease.DisposeAsync();

            Action create = () => lease.CreateConnection();

            create.Should().Throw<ObjectDisposedException>();
        }

        [Test]
        public async Task It_disposes_the_connection_and_propagates_when_the_open_fails()
        {
            InvalidOperationException failure = new("open failed");
            using RecordingConnection connection = new() { OpenFailure = failure };
            MssqlConnectionLease lease = await AcquisitionReturning(connection)
                .AcquireLeaseAsync(EffectiveDataStoreTarget.Primary(PrimaryConnectionString));

            Func<Task> open = () => lease.OpenAsync(CancellationToken.None);

            (await open.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(failure);
            connection.DisposeCount.Should().Be(1);
        }

        [Test]
        public async Task It_propagates_cancellation_from_the_open_unchanged()
        {
            using CancellationTokenSource cancellation = new();
            await cancellation.CancelAsync();

            using RecordingConnection connection = new() { OpenFailure = new OperationCanceledException() };
            MssqlConnectionLease lease = await AcquisitionReturning(connection)
                .AcquireLeaseAsync(EffectiveDataStoreTarget.Primary(PrimaryConnectionString));

            Func<Task> open = () => lease.OpenAsync(cancellation.Token);

            await open.Should().ThrowAsync<OperationCanceledException>();
            connection.DisposeCount.Should().Be(1);
        }
    }

    /// <summary>
    /// Cleanup runs while an exception is already in flight. It must not replace that exception with its
    /// own, and it must still release the claim, because a failure to clean up is not a reason to strand
    /// a pool identity.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_Cleanup_Itself_Fails
    {
        [Test]
        public async Task It_preserves_the_open_failure_when_disposing_the_connection_also_fails()
        {
            InvalidOperationException openFailure = new("open failed");
            RecordingConnection connection = new()
            {
                OpenFailure = openFailure,
                DisposeFailure = new IOException("dispose failed"),
            };

            MssqlConnectionLease lease = await new MssqlConnectionAcquisition(
                new SqlClientPoolClearing(),
                NullLogger<MssqlConnectionAcquisition>.Instance,
                _ => connection
            ).AcquireLeaseAsync(EffectiveDataStoreTarget.Primary(PrimaryConnectionString));

            Func<Task> open = () => lease.OpenAsync(CancellationToken.None);

            (await open.Should().ThrowAsync<InvalidOperationException>())
                .Which.Should()
                .BeSameAs(openFailure);
        }

        [Test]
        public async Task It_preserves_the_cancellation_when_disposing_the_connection_also_fails()
        {
            RecordingConnection connection = new()
            {
                OpenFailure = new OperationCanceledException(),
                DisposeFailure = new IOException("dispose failed"),
            };

            MssqlConnectionLease lease = await new MssqlConnectionAcquisition(
                new SqlClientPoolClearing(),
                NullLogger<MssqlConnectionAcquisition>.Instance,
                _ => connection
            ).AcquireLeaseAsync(EffectiveDataStoreTarget.Primary(PrimaryConnectionString));

            Func<Task> open = () => lease.OpenAsync(CancellationToken.None);

            await open.Should().ThrowAsync<OperationCanceledException>();
        }

        [Test]
        public async Task It_releases_the_claim_when_disposing_the_leased_connection_fails()
        {
            IOException disposeFailure = new("dispose failed");
            RecordingConnection connection = new() { DisposeFailure = disposeFailure };

            MssqlLeasedConnection leased = await MssqlLeasedConnection.OpenAsync(
                new MssqlConnectionAcquisition(
                    new SqlClientPoolClearing(),
                    NullLogger<MssqlConnectionAcquisition>.Instance,
                    _ => connection
                ),
                EffectiveDataStoreTarget.Primary(PrimaryConnectionString),
                CancellationToken.None
            );

            Func<Task> dispose = async () => await leased.DisposeAsync();

            (await dispose.Should().ThrowAsync<IOException>()).Which.Should().BeSameAs(disposeFailure);
            ((MssqlConnectionLease)leased.Lease!)
                .IsReleased.Should()
                .BeTrue("a disposal fault must not strand the pool identity");
        }

        [Test]
        public async Task It_preserves_the_open_failure_when_the_leased_open_cleanup_also_fails()
        {
            InvalidOperationException openFailure = new("open failed");
            RecordingConnection connection = new()
            {
                OpenFailure = openFailure,
                DisposeFailure = new IOException("dispose failed"),
            };

            Func<Task> open = () =>
                MssqlLeasedConnection.OpenAsync(
                    new MssqlConnectionAcquisition(
                        new SqlClientPoolClearing(),
                        NullLogger<MssqlConnectionAcquisition>.Instance,
                        _ => connection
                    ),
                    EffectiveDataStoreTarget.Primary(PrimaryConnectionString),
                    CancellationToken.None
                );

            (await open.Should().ThrowAsync<InvalidOperationException>())
                .Which.Should()
                .BeSameAs(openFailure);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Leased_Connection
    {
        [Test]
        public async Task It_disposes_the_connection_before_releasing_the_lease()
        {
            using RecordingConnection connection = new();
            MssqlConnectionAcquisition acquisition = new(
                new SqlClientPoolClearing(),
                NullLogger<MssqlConnectionAcquisition>.Instance,
                _ => connection
            );

            MssqlLeasedConnection leased = await MssqlLeasedConnection.OpenAsync(
                acquisition,
                EffectiveDataStoreTarget.Primary(PrimaryConnectionString),
                CancellationToken.None
            );

            leased.Connection.Should().BeSameAs(connection);

            await leased.DisposeAsync();

            connection.DisposeCount.Should().Be(1);
            ((MssqlConnectionLease)leased.Lease!).IsReleased.Should().BeTrue();
        }

        [Test]
        public async Task It_disposes_exactly_once_when_disposed_twice()
        {
            using RecordingConnection connection = new();
            MssqlConnectionAcquisition acquisition = new(
                new SqlClientPoolClearing(),
                NullLogger<MssqlConnectionAcquisition>.Instance,
                _ => connection
            );

            MssqlLeasedConnection leased = await MssqlLeasedConnection.OpenAsync(
                acquisition,
                EffectiveDataStoreTarget.Primary(PrimaryConnectionString),
                CancellationToken.None
            );

            await leased.DisposeAsync();

            // Deliberate second disposal: the connection must still be disposed exactly once.
#pragma warning disable S3966 // Redundant disposal - deliberate: idempotence is the assertion
            await leased.DisposeAsync();
#pragma warning restore S3966

            connection.DisposeCount.Should().Be(1);
        }

        [Test]
        public async Task It_releases_the_lease_when_the_open_fails()
        {
            InvalidOperationException failure = new("open failed");
            using RecordingConnection connection = new() { OpenFailure = failure };
            MssqlConnectionAcquisition acquisition = new(
                new SqlClientPoolClearing(),
                NullLogger<MssqlConnectionAcquisition>.Instance,
                _ => connection
            );

            Func<Task> open = () =>
                MssqlLeasedConnection.OpenAsync(
                    acquisition,
                    EffectiveDataStoreTarget.Primary(PrimaryConnectionString),
                    CancellationToken.None
                );

            (await open.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(failure);
            connection.DisposeCount.Should().Be(1);
        }

        /// <summary>
        /// SqlClient reports a timeout waiting for a pooled connection as an InvalidOperationException -
        /// only its non-pooled open timeout raises TimeoutException - so an exhausted snapshot pool
        /// would otherwise escape the acquisition boundary unclassified and answer a
        /// service-configuration 503 where the snapshot contract requires Snapshot Not Found. Restating
        /// it as the type the classifier already accepts is what carries it to the right response
        /// without widening the classifier to a type it rejects on purpose.
        /// </summary>
        [Test]
        public async Task It_restates_a_snapshot_pool_acquisition_timeout_as_a_timeout()
        {
            InvalidOperationException poolTimeout = new(
                "Timeout expired. The timeout period elapsed prior to obtaining a connection from the pool."
            );
            using RecordingConnection connection = new() { OpenFailure = poolTimeout };
            MssqlConnectionAcquisition acquisition = new(
                new SqlClientPoolClearing(),
                NullLogger<MssqlConnectionAcquisition>.Instance,
                _ => connection
            );

            Func<Task> open = () =>
                MssqlLeasedConnection.OpenAsync(
                    acquisition,
                    new EffectiveDataStoreTarget(EffectiveTargetKind.Snapshot, PrimaryConnectionString),
                    CancellationToken.None
                );

            TimeoutException restated = (await open.Should().ThrowAsync<TimeoutException>()).Which;

            restated
                .InnerException.Should()
                .BeSameAs(poolTimeout, "the provider failure travels for diagnostics only");
            MssqlConnectionAcquisitionFailure
                .IsExpected(restated)
                .Should()
                .BeTrue("the restated type is the one the classifier already accepts");
            connection.DisposeCount.Should().Be(1);
        }

        /// <summary>
        /// ObjectDisposedException derives from InvalidOperationException, so without an explicit
        /// exclusion the restatement above would catch a disposal too and hand it on as a type the
        /// classifier accepts - answering Snapshot Not Found for a shutdown.
        /// </summary>
        /// <remarks>
        /// The restatement runs inside the acquisition lambda, so a converted disposal would already be
        /// a TimeoutException by the time <c>ConnectionAcquisition.GuardAsync</c> reached its own
        /// <c>catch (ObjectDisposedException)</c> arm. That arm and its test assert the opposite, which
        /// is why this is asserted here rather than left to the guard's.
        /// </remarks>
        [Test]
        public async Task It_leaves_a_snapshot_disposal_exactly_as_it_arrived()
        {
            ObjectDisposedException disposed = new(nameof(SqlConnection));
            using RecordingConnection connection = new() { OpenFailure = disposed };
            MssqlConnectionAcquisition acquisition = new(
                new SqlClientPoolClearing(),
                NullLogger<MssqlConnectionAcquisition>.Instance,
                _ => connection
            );

            Func<Task> open = () =>
                MssqlLeasedConnection.OpenAsync(
                    acquisition,
                    new EffectiveDataStoreTarget(EffectiveTargetKind.Snapshot, PrimaryConnectionString),
                    CancellationToken.None
                );

            (await open.Should().ThrowAsync<ObjectDisposedException>()).Which.Should().BeSameAs(disposed);
            connection.DisposeCount.Should().Be(1);
        }

        /// <summary>
        /// A read replica is a derivative but not a snapshot, and only a snapshot's response depends on
        /// the failure being a connection failure. Restricting the restatement to a snapshot is what
        /// leaves the write-failure mapper and the custom-view DbException sites answering exactly as
        /// they did - and what keeps an unrelated application failure, such as opening inside an
        /// already-completed TransactionScope, from being relabelled a timeout.
        /// </summary>
        [Test]
        public async Task It_leaves_a_read_replica_open_failure_exactly_as_it_arrived()
        {
            InvalidOperationException failure = new("The current TransactionScope is already complete.");
            using RecordingConnection connection = new() { OpenFailure = failure };
            MssqlConnectionAcquisition acquisition = new(
                new SqlClientPoolClearing(),
                NullLogger<MssqlConnectionAcquisition>.Instance,
                _ => connection
            );

            Func<Task> open = () =>
                MssqlLeasedConnection.OpenAsync(
                    acquisition,
                    new EffectiveDataStoreTarget(EffectiveTargetKind.ReadReplica, PrimaryConnectionString),
                    CancellationToken.None
                );

            (await open.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(failure);
            connection.DisposeCount.Should().Be(1);
        }
    }

    /// <summary>
    /// A DbConnection that records disposal and can be told to fail its open, so lease lifecycle can be
    /// asserted without a server.
    /// </summary>
    private sealed class RecordingConnection : DbConnection
    {
        public int DisposeCount { get; private set; }

        public Exception? OpenFailure { get; init; }

        public Exception? DisposeFailure { get; init; }

        [AllowNull]
        public override string ConnectionString { get; set; } = string.Empty;

        public override string Database => string.Empty;

        public override string DataSource => string.Empty;

        public override string ServerVersion => string.Empty;

        public override ConnectionState State => ConnectionState.Closed;

        public override void ChangeDatabase(string databaseName) { }

        public override void Close() { }

        public override void Open()
        {
            if (OpenFailure is not null)
            {
                throw OpenFailure;
            }
        }

        public override Task OpenAsync(CancellationToken cancellationToken) =>
            OpenFailure is null ? Task.CompletedTask : Task.FromException(OpenFailure);

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
            throw new NotSupportedException();

        protected override DbCommand CreateDbCommand() => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposeCount++;

                if (DisposeFailure is not null)
                {
                    throw DisposeFailure;
                }
            }

            base.Dispose(disposing);
        }
    }
}
