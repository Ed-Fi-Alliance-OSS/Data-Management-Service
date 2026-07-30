// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

/// <summary>
/// Covers the narrow case where the server has already rolled a transaction back, so a client-side rollback
/// could only throw over an already-mapped database failure. Both preconditions are required, the decision
/// is a pre-check rather than a catch, and every other failure still propagates.
/// </summary>
[TestFixture]
public class Given_A_Relational_Write_Session_Rolling_Back
{
    [Test]
    public async Task It_skips_the_physical_rollback_when_a_failure_was_reported_and_the_probe_proves_completion()
    {
        var transaction = new FakeDbTransaction();
        await using var session = CreateSession(transaction, AlwaysCompletedProbe.Instance);
        session.ReportDatabaseFailure(new FakeDbException());

        await session.RollbackAsync();

        transaction.RollbackCount.Should().Be(0);
    }

    [Test]
    public async Task It_treats_a_tolerated_rollback_as_the_terminal_state()
    {
        var transaction = new FakeDbTransaction();
        await using var session = CreateSession(transaction, AlwaysCompletedProbe.Instance);
        session.ReportDatabaseFailure(new FakeDbException());
        await session.RollbackAsync();

        // Idempotent, and still not committable: tolerating the rollback must not make the session look
        // like it could go on to commit.
        await session.RollbackAsync();
        var commit = async () => await session.CommitAsync();

        transaction.RollbackCount.Should().Be(0);
        transaction.CommitCount.Should().Be(0);
        await commit.Should().ThrowAsync<InvalidOperationException>().WithMessage("*cannot commit*");
    }

    [Test]
    public async Task It_performs_the_physical_rollback_when_the_probe_reports_the_transaction_is_live()
    {
        var transaction = new FakeDbTransaction();
        await using var session = CreateSession(transaction, NeverCompletedProbe.Instance);
        session.ReportDatabaseFailure(new FakeDbException());

        await session.RollbackAsync();

        transaction.RollbackCount.Should().Be(1);
    }

    [Test]
    public async Task It_performs_the_physical_rollback_when_no_failure_was_reported()
    {
        var transaction = new FakeDbTransaction
        {
            RollbackException = new InvalidOperationException("zombie"),
        };
        await using var session = CreateSession(transaction, AlwaysCompletedProbe.Instance);

        var act = async () => await session.RollbackAsync();

        // A detached transaction alone must not buy tolerance. Without a reported database failure the
        // rollback is attempted and whatever it throws surfaces.
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("zombie");
        transaction.RollbackCount.Should().Be(1);
    }

    [Test]
    public async Task It_performs_the_physical_rollback_on_an_ordinary_session()
    {
        var transaction = new FakeDbTransaction();
        await using var session = CreateSession(transaction);

        await session.RollbackAsync();
        await session.RollbackAsync();

        transaction.RollbackCount.Should().Be(1);
    }

    [Test]
    public async Task It_never_tolerates_when_no_probe_is_supplied()
    {
        var transaction = new FakeDbTransaction
        {
            RollbackException = new InvalidOperationException("zombie"),
        };
        await using var session = CreateSession(transaction);
        session.ReportDatabaseFailure(new FakeDbException());

        var act = async () => await session.RollbackAsync();

        // The default is never-tolerant, which is what keeps PostgreSQL behavior identical: an aborted
        // PostgreSQL transaction always accepts a real rollback.
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("zombie");
        transaction.RollbackCount.Should().Be(1);
    }

    [Test]
    public async Task It_propagates_an_unrelated_invalid_operation_failure_from_the_rollback()
    {
        var transaction = new FakeDbTransaction
        {
            RollbackException = new InvalidOperationException("something else entirely"),
        };
        await using var session = CreateSession(transaction, NeverCompletedProbe.Instance);
        session.ReportDatabaseFailure(new FakeDbException());

        var act = async () => await session.RollbackAsync();

        // Nothing is caught, so an unrelated invalid-operation failure cannot be absorbed.
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("something else entirely");
    }

    [Test]
    public async Task It_propagates_a_cancellation_from_the_rollback()
    {
        var transaction = new FakeDbTransaction
        {
            RollbackException = new OperationCanceledException("cancelled"),
        };
        await using var session = CreateSession(transaction, AlwaysCompletedProbe.Instance);

        var act = async () => await session.RollbackAsync(new CancellationToken(true));

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Test]
    public async Task It_propagates_a_connection_failure_from_the_rollback()
    {
        var transaction = new FakeDbTransaction { RollbackException = new FakeDbException() };
        await using var session = CreateSession(transaction, NeverCompletedProbe.Instance);
        session.ReportDatabaseFailure(new FakeDbException());

        var act = async () => await session.RollbackAsync();

        await act.Should().ThrowAsync<FakeDbException>();
    }

    [Test]
    public async Task It_keeps_rejecting_a_rollback_after_a_commit()
    {
        var transaction = new FakeDbTransaction();
        await using var session = CreateSession(transaction, AlwaysCompletedProbe.Instance);
        await session.CommitAsync();
        session.ReportDatabaseFailure(new FakeDbException());

        var act = async () => await session.RollbackAsync();

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*cannot roll back*");
    }

    [Test]
    public async Task It_keeps_rejecting_both_operations_after_disposal()
    {
        var transaction = new FakeDbTransaction();
        var session = CreateSession(transaction, AlwaysCompletedProbe.Instance);
        session.ReportDatabaseFailure(new FakeDbException());
        await session.DisposeAsync();

        var rollback = async () => await session.RollbackAsync();
        var commit = async () => await session.CommitAsync();

        await rollback.Should().ThrowAsync<ObjectDisposedException>();
        await commit.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Test]
    public void It_rejects_a_null_reported_failure()
    {
        var session = CreateSession(new FakeDbTransaction(), AlwaysCompletedProbe.Instance);

        var act = () => session.ReportDatabaseFailure(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public async Task It_passes_the_connection_transaction_and_reported_failure_to_the_probe()
    {
        var transaction = new FakeDbTransaction();
        var connection = new FakeDbConnection();
        transaction.Connection = connection;
        var reportedFailure = new FakeDbException();
        var probe = new RecordingProbe();
        await using var session = new RelationalWriteSession(connection, transaction, probe);
        session.ReportDatabaseFailure(reportedFailure);

        await session.RollbackAsync();

        // The probe cannot exclude a connection-level fault without all three pieces of evidence.
        probe.Connection.Should().BeSameAs(connection);
        probe.Transaction.Should().BeSameAs(transaction);
        probe.ReportedFailure.Should().BeSameAs(reportedFailure);
    }

    [Test]
    public async Task It_uses_the_most_recently_reported_failure()
    {
        var transaction = new FakeDbTransaction();
        var probe = new RecordingProbe();
        await using var session = CreateSession(transaction, probe);
        session.ReportDatabaseFailure(new FakeDbException());
        var latestFailure = new FakeDbException();
        session.ReportDatabaseFailure(latestFailure);

        await session.RollbackAsync();

        probe.ReportedFailure.Should().BeSameAs(latestFailure);
    }

    private static RelationalWriteSession CreateSession(
        FakeDbTransaction transaction,
        IRelationalTransactionStateProbe? probe = null
    )
    {
        var connection = new FakeDbConnection();
        transaction.Connection = connection;

        return new RelationalWriteSession(connection, transaction, probe);
    }

    private sealed class AlwaysCompletedProbe : IRelationalTransactionStateProbe
    {
        public static readonly AlwaysCompletedProbe Instance = new();

        public bool IsAlreadyCompleted(
            DbConnection connection,
            DbTransaction transaction,
            DbException reportedFailure
        ) => true;
    }

    private sealed class NeverCompletedProbe : IRelationalTransactionStateProbe
    {
        public static readonly NeverCompletedProbe Instance = new();

        public bool IsAlreadyCompleted(
            DbConnection connection,
            DbTransaction transaction,
            DbException reportedFailure
        ) => false;
    }

    private sealed class RecordingProbe : IRelationalTransactionStateProbe
    {
        public DbConnection? Connection { get; private set; }

        public DbTransaction? Transaction { get; private set; }

        public DbException? ReportedFailure { get; private set; }

        public bool IsAlreadyCompleted(
            DbConnection connection,
            DbTransaction transaction,
            DbException reportedFailure
        )
        {
            Connection = connection;
            Transaction = transaction;
            ReportedFailure = reportedFailure;

            return true;
        }
    }

    private sealed class FakeDbException : DbException
    {
        public FakeDbException()
            : base("fake provider failure") { }
    }

    private sealed class FakeDbTransaction : DbTransaction
    {
        public int RollbackCount { get; private set; }

        public int CommitCount { get; private set; }

        public Exception? RollbackException { get; init; }

        public new DbConnection? Connection { get; set; }

        protected override DbConnection? DbConnection => Connection;

        public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;

        public override void Commit() => CommitCount++;

        public override void Rollback()
        {
            RollbackCount++;

            if (RollbackException is { } rollbackException)
            {
                throw rollbackException;
            }
        }
    }

    private sealed class FakeDbConnection : DbConnection
    {
        [AllowNull]
        public override string ConnectionString { get; set; } = "fake";

        public override string Database => "fake";

        public override string DataSource => "fake";

        public override string ServerVersion => "fake";

        public override ConnectionState State => ConnectionState.Open;

        public override void ChangeDatabase(string databaseName) { }

        public override void Close() { }

        public override void Open() { }

        protected override DbCommand CreateDbCommand() => throw new NotSupportedException();

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
            throw new NotSupportedException();
    }
}
