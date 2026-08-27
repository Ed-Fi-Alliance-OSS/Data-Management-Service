// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Mssql;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

/// <summary>
/// Every SQL Server seam that opens a connection for a request reaches the same acquisition boundary
/// and hands it the same target. That is what makes them share one pool identity: the fingerprint read
/// and the resource-key read are the first two reads of any request, and a seam that acquired
/// elsewhere would occupy a second pool with a different policy, on exactly the reads that fail when a
/// database is unreachable.
/// </summary>
[TestFixture]
[Parallelizable]
public class MssqlSeamAcquisitionIdentityTests
{
    private const string ConnectionString =
        "Server=localhost,1433;Database=edfi;User Id=sa;Password=abcdefgh1!;TrustServerCertificate=true;";

    private static DataStore TestDataStore() =>
        new(
            Id: 1,
            DataStoreType: "Test",
            Name: "Test Instance",
            ConnectionString: ConnectionString,
            RouteContext: []
        );

    private static IDataStoreSelection SelectionOf(DataStore dataStore)
    {
        var selection = A.Fake<IDataStoreSelection>();
        A.CallTo(() => selection.GetSelectedDataStore()).Returns(dataStore);
        return selection;
    }

    /// <summary>
    /// Records the target each seam asks for, then refuses to produce a connection. Refusing keeps the
    /// assertion about routing rather than about what any seam does with a connection afterwards.
    /// </summary>
    private sealed class RecordingAcquisition : IMssqlConnectionAcquisition
    {
        internal const string RefusedMessage = "Recording acquisition refuses to produce a connection.";

        public List<EffectiveDataStoreTarget> Targets { get; } = [];

        public Task<MssqlConnectionLease> AcquireLeaseAsync(
            EffectiveDataStoreTarget target,
            CancellationToken cancellationToken = default
        )
        {
            Targets.Add(target);
            throw new NotSupportedException(RefusedMessage);
        }
    }

    private static async Task<RecordingAcquisition> RecordSeamAcquisition(
        Func<IDataStoreSelection, IMssqlConnectionAcquisition, Task> exercise
    )
    {
        RecordingAcquisition acquisition = new();
        IDataStoreSelection selection = SelectionOf(TestDataStore());

        try
        {
            await exercise(selection, acquisition);
        }
        catch (NotSupportedException exception)
            when (exception.Message == RecordingAcquisition.RefusedMessage)
        {
            // Expected: the seam reached the boundary, which is the whole assertion.
        }

        return acquisition;
    }

    private static async Task<EffectiveDataStoreTarget> TargetRequestedBy(
        Func<IDataStoreSelection, IMssqlConnectionAcquisition, Task> exercise
    )
    {
        RecordingAcquisition acquisition = await RecordSeamAcquisition(exercise);

        acquisition.Targets.Should().ContainSingle("the seam must acquire exactly once");
        return acquisition.Targets[0];
    }

    private static Task ExerciseFingerprintReader(
        IDataStoreSelection selection,
        IMssqlConnectionAcquisition acquisition
    ) =>
        new MssqlDatabaseFingerprintReader(
            acquisition,
            NullLogger<MssqlDatabaseFingerprintReader>.Instance
        ).ReadFingerprintAsync(EffectiveDataStoreTarget.Primary(ConnectionString));

    private static Task ExerciseResourceKeyRowReader(
        IDataStoreSelection selection,
        IMssqlConnectionAcquisition acquisition
    ) =>
        new MssqlResourceKeyRowReader(
            acquisition,
            NullLogger<MssqlResourceKeyRowReader>.Instance
        ).ReadResourceKeyRowsAsync(EffectiveDataStoreTarget.Primary(ConnectionString));

    private static Task ExerciseCommandExecutor(
        IDataStoreSelection selection,
        IMssqlConnectionAcquisition acquisition
    ) =>
        new MssqlRelationalCommandExecutor(
            selection,
            acquisition,
            NullLogger<MssqlRelationalCommandExecutor>.Instance
        ).ExecuteReaderAsync(new RelationalCommand("select 1", []), (_, _) => Task.FromResult(0));

    private static Task ExerciseWriteSessionFactory(
        IDataStoreSelection selection,
        IMssqlConnectionAcquisition acquisition
    ) =>
        new MssqlRelationalWriteSessionFactory(
            selection,
            acquisition,
            Options.Create(new DatabaseOptions { IsolationLevel = IsolationLevel.ReadCommitted })
        ).CreateAsync();

    private static Task ExerciseDocumentHydrator(
        IDataStoreSelection selection,
        IMssqlConnectionAcquisition acquisition
    ) => MssqlReferenceResolverTestAccess.HydrateAsync(selection, acquisition);

    private static readonly Func<
        IDataStoreSelection,
        IMssqlConnectionAcquisition,
        Task
    >[] _requestPathSeams =
    [
        ExerciseFingerprintReader,
        ExerciseResourceKeyRowReader,
        ExerciseCommandExecutor,
        ExerciseWriteSessionFactory,
        ExerciseDocumentHydrator,
    ];

    [Test]
    public async Task It_routes_every_seam_through_the_acquisition_boundary()
    {
        foreach (var seam in _requestPathSeams)
        {
            RecordingAcquisition acquisition = await RecordSeamAcquisition(seam);

            acquisition
                .Targets.Should()
                .ContainSingle("every seam must acquire through the one boundary exactly once");
        }
    }

    [Test]
    public async Task It_hands_every_seam_the_same_target()
    {
        List<EffectiveDataStoreTarget> targets = [];

        foreach (var seam in _requestPathSeams)
        {
            targets.Add(await TargetRequestedBy(seam));
        }

        targets.Should().AllSatisfy(target => target.Should().Be(targets[0]));
        targets[0].Kind.Should().Be(EffectiveTargetKind.Primary);
        targets[0].ConnectionString.Should().Be(ConnectionString);
    }

    /// <summary>
    /// A write session outlives the factory call that created it, so the claim on the pool identity has
    /// to travel with it. These pin that the claim is released exactly once, and only after the
    /// connection, on both the success path and the transaction-start failure path.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_A_Write_Session : MssqlSeamAcquisitionIdentityTests
    {
        private static MssqlRelationalWriteSessionFactory FactoryReturning(
            TransactionCapableConnection connection
        ) =>
            new(
                SelectionOf(TestDataStore()),
                new MssqlConnectionAcquisition(_ => connection),
                Options.Create(new DatabaseOptions { IsolationLevel = IsolationLevel.ReadCommitted })
            );

        [Test]
        public async Task It_releases_the_claim_after_the_connection_when_the_session_is_disposed()
        {
            using TransactionCapableConnection connection = new();

            IRelationalWriteSession session = await FactoryReturning(connection).CreateAsync();

            connection.DisposeCount.Should().Be(0, "the session holds the connection until it is disposed");

            await session.DisposeAsync();

            connection.DisposeCount.Should().Be(1);
            connection.DisposedAfterTransaction.Should().BeTrue();
        }

        [Test]
        public async Task It_disposes_the_connection_when_the_transaction_cannot_start()
        {
            InvalidOperationException failure = new("cannot begin");
            using TransactionCapableConnection connection = new() { BeginTransactionFailure = failure };

            Func<Task> create = () => FactoryReturning(connection).CreateAsync();

            (await create.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(failure);
            connection.DisposeCount.Should().Be(1);
        }

        /// <summary>
        /// Cleanup runs while the transaction failure is already in flight, so a fault raised by the
        /// cleanup must not replace the exception the caller needs to see.
        /// </summary>
        [Test]
        public async Task It_preserves_the_transaction_failure_when_cleanup_also_fails()
        {
            InvalidOperationException failure = new("cannot begin");
            TransactionCapableConnection connection = new()
            {
                BeginTransactionFailure = failure,
                DisposeFailure = new IOException("dispose failed"),
            };

            Func<Task> create = () => FactoryReturning(connection).CreateAsync();

            (await create.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(failure);
        }

        /// <summary>
        /// A disposal fault inside the session must not strand the claim on the pool identity.
        /// </summary>
        [Test]
        public async Task It_releases_the_claim_when_disposing_the_connection_fails()
        {
            IOException disposeFailure = new("dispose failed");
            TransactionCapableConnection connection = new() { DisposeFailure = disposeFailure };
            RecordingLease lease = new();

            IRelationalWriteSession session = new RelationalWriteSession(
                connection,
                await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted),
                null,
                ownedLease: lease
            );

            Func<Task> dispose = async () => await session.DisposeAsync();

            (await dispose.Should().ThrowAsync<IOException>()).Which.Should().BeSameAs(disposeFailure);
            lease.ReleaseCount.Should().Be(1);
        }

        private sealed class RecordingLease : IAsyncDisposable
        {
            public int ReleaseCount { get; private set; }

            public ValueTask DisposeAsync()
            {
                ReleaseCount++;
                return ValueTask.CompletedTask;
            }
        }
    }

    /// <summary>
    /// The seams that read the resolved data store must not silently substitute anything when it has no
    /// connection string. Acquisition is not reached at all in that case.
    /// </summary>
    [Test]
    public async Task It_does_not_acquire_when_the_resolved_data_store_has_no_connection_string()
    {
        RecordingAcquisition acquisition = new();
        IDataStoreSelection selection = SelectionOf(TestDataStore() with { ConnectionString = null });

        Func<Task> execute = () => ExerciseCommandExecutor(selection, acquisition);

        await execute.Should().ThrowAsync<InvalidOperationException>();
        acquisition.Targets.Should().BeEmpty();
    }
}

/// <summary>
/// A connection that can start a transaction, so write-session ownership can be exercised without a
/// server. It records disposal and whether the transaction was disposed first.
/// </summary>
internal sealed class TransactionCapableConnection : DbConnection
{
    private readonly RecordingTransaction _transaction;

    public TransactionCapableConnection()
    {
        _transaction = new RecordingTransaction(this);
    }

    public int DisposeCount { get; private set; }

    public bool DisposedAfterTransaction { get; private set; }

    public Exception? BeginTransactionFailure { get; init; }

    public Exception? DisposeFailure { get; init; }

    [AllowNull]
    public override string ConnectionString { get; set; } = string.Empty;

    public override string Database => string.Empty;

    public override string DataSource => string.Empty;

    public override string ServerVersion => string.Empty;

    public override ConnectionState State => ConnectionState.Open;

    public override void ChangeDatabase(string databaseName) { }

    public override void Close() { }

    public override void Open() { }

    public override Task OpenAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
        BeginTransactionFailure is null ? _transaction : throw BeginTransactionFailure;

    protected override DbCommand CreateDbCommand() => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeCount++;
            DisposedAfterTransaction = _transaction.IsDisposed;

            if (DisposeFailure is not null)
            {
                throw DisposeFailure;
            }
        }

        base.Dispose(disposing);
    }

    private sealed class RecordingTransaction(DbConnection connection) : DbTransaction
    {
        public bool IsDisposed { get; private set; }

        public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;

        protected override DbConnection DbConnection => connection;

        public override void Commit() { }

        public override void Rollback() { }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                IsDisposed = true;
            }

            base.Dispose(disposing);
        }
    }
}

/// <summary>
/// The document hydrator is constructed by an internal factory rather than exposed directly, so this
/// reaches it the same way the reference-resolver registration does.
/// </summary>
internal static class MssqlReferenceResolverTestAccess
{
    public static Task HydrateAsync(IDataStoreSelection selection, IMssqlConnectionAcquisition acquisition) =>
        new MssqlDocumentHydrator(selection, acquisition).HydrateAsync(
            null!,
            null!,
            new HydrationExecutionOptions(),
            CancellationToken.None
        );
}
