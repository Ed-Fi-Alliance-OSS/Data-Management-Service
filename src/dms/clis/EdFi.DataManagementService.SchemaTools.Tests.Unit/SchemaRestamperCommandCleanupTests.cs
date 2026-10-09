// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.SchemaTools.Restamping;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.SchemaTools.Tests.Unit;

[TestFixture]
public class Given_SchemaRestamper_CommandCleanup
{
    [Test]
    public async Task It_reuses_one_connection_and_transaction_and_locks_metadata_in_order()
    {
        var connection = new TimeoutCommandConnection(
            failNonQuery: false,
            failRead: false,
            failDispose: false
        );
        var factoryCalls = 0;
        var target = new EffectiveSchemaInfo("1.0.0", "v3", new string('a', 64), 0, new byte[32], [], []);
        var service = new SchemaRestamper(
            NullLogger.Instance,
            (_, _) =>
            {
                factoryCalls++;
                return connection;
            }
        );

        Func<Task> action = () =>
            service.RestampAsync(
                SqlDialect.Pgsql,
                "Host=localhost;Database=sample",
                41,
                target,
                true,
                CancellationToken.None
            );

        (await action.Should().ThrowAsync<SchemaRestampException>())
            .Which.Failure.Should()
            .Be(SchemaRestampFailure.Validation);
        factoryCalls.Should().Be(1);
        connection.OpenCount.Should().Be(1);
        connection.BeginTransactionCount.Should().Be(1);
        connection
            .Commands.Take(3)
            .Select(command => command.CommandText)
            .Should()
            .Equal(SchemaRestampSql.LockCommands(SqlDialect.Pgsql));
        connection.Commands.Should().OnlyContain(command => command.CommandTimeout == 41);
        connection.Commands.Select(command => command.TransactionUsed).Should().NotContainNulls();
        connection.Commands.Select(command => command.TransactionUsed).Distinct().Should().ContainSingle();
    }

    [Test]
    public async Task It_rolls_back_and_disposes_when_cancelled_during_lock_acquisition()
    {
        var connection = new TimeoutCommandConnection(
            failNonQuery: false,
            failRead: false,
            failDispose: false,
            waitForCancellation: true
        );
        var target = new EffectiveSchemaInfo("1.0.0", "v3", new string('a', 64), 0, new byte[32], [], []);
        var service = new SchemaRestamper(NullLogger.Instance, (_, _) => connection);
        using var cancellation = new CancellationTokenSource();
        var execution = service.RestampAsync(
            SqlDialect.Pgsql,
            "Host=localhost;Database=sample",
            30,
            target,
            true,
            cancellation.Token
        );

        await connection.CommandStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cancellation.CancelAsync();
        Func<Task> action = () => execution;

        (await action.Should().ThrowAsync<SchemaRestampException>())
            .Which.Failure.Should()
            .Be(SchemaRestampFailure.Cancelled);
        connection.Transaction.RollbackCount.Should().Be(1);
        connection.Transaction.DisposeCount.Should().Be(1);
        connection.DisposeCount.Should().Be(1);
    }

    [Test]
    public async Task It_reports_an_uncertain_commit_after_exactly_one_commit_attempt()
    {
        var connection = new TimeoutCommandConnection(
            failNonQuery: false,
            failRead: false,
            failDispose: false,
            failCommit: true,
            provideValidSnapshot: true
        );
        var target = new EffectiveSchemaInfo("1.0.0", "v3", new string('a', 64), 0, new byte[32], [], []);
        var service = new SchemaRestamper(NullLogger.Instance, (_, _) => connection);

        Func<Task> action = () =>
            service.RestampAsync(
                SqlDialect.Pgsql,
                "Host=localhost;Database=sample",
                30,
                target,
                true,
                CancellationToken.None
            );

        var failure = (await action.Should().ThrowAsync<SchemaRestampException>()).Which;
        failure
            .Failure.Should()
            .Be(
                SchemaRestampFailure.CommitOutcomeUnknown,
                $"command sequence: {string.Join(" | ", connection.Commands.Select(command => command.CommandText))}; commits: {connection.Transaction.CommitCount}"
            );
        connection.Transaction.CommitCount.Should().Be(1);
    }

    [Test]
    public async Task It_can_retry_the_old_stamp_after_an_uncertain_commit()
    {
        var connection = new TimeoutCommandConnection(
            failNonQuery: false,
            failRead: false,
            failDispose: false,
            provideValidSnapshot: true
        );
        var target = new EffectiveSchemaInfo("1.0.0", "v3", new string('a', 64), 0, new byte[32], [], []);
        var service = new SchemaRestamper(NullLogger.Instance, (_, _) => connection);

        var result = await service.RestampAsync(
            SqlDialect.Pgsql,
            "Host=localhost;Database=sample",
            30,
            target,
            true,
            CancellationToken.None
        );

        result.Changed.Should().BeTrue();
        result.PreviousHash.Should().Be(new string('b', 64));
        result.TargetHash.Should().Be(target.EffectiveSchemaHash);
        connection.Transaction.CommitCount.Should().Be(1);
    }

    [Test]
    public async Task It_retries_the_target_stamp_as_a_fully_validated_no_op()
    {
        var targetHash = new string('a', 64);
        var connection = new TimeoutCommandConnection(
            failNonQuery: false,
            failRead: false,
            failDispose: false,
            provideValidSnapshot: true,
            storedHash: targetHash
        );
        var target = new EffectiveSchemaInfo("1.0.0", "v3", targetHash, 0, new byte[32], [], []);
        var service = new SchemaRestamper(NullLogger.Instance, (_, _) => connection);

        var result = await service.RestampAsync(
            SqlDialect.Pgsql,
            "Host=localhost;Database=sample",
            30,
            target,
            false,
            CancellationToken.None
        );

        result.Changed.Should().BeFalse();
        connection
            .Commands.Where(command => command.CommandText.StartsWith("DELETE", StringComparison.Ordinal))
            .Should()
            .BeEmpty();
        connection
            .Commands.Where(command => command.CommandText.StartsWith("UPDATE", StringComparison.Ordinal))
            .Should()
            .BeEmpty();
        connection.Transaction.CommitCount.Should().Be(1);
    }

    [Test]
    public async Task It_preserves_the_command_timeout_when_command_disposal_also_fails()
    {
        var connection = new TimeoutCommandConnection();
        var target = new EffectiveSchemaInfo("1.0.0", "v3", new string('a', 64), 0, new byte[32], [], []);
        var service = new SchemaRestamper(NullLogger.Instance, (_, _) => connection);

        Func<Task> action = () =>
            service.RestampAsync(
                SqlDialect.Pgsql,
                "Host=localhost;Database=sample",
                30,
                target,
                true,
                CancellationToken.None
            );

        (await action.Should().ThrowAsync<SchemaRestampException>())
            .Which.Failure.Should()
            .Be(SchemaRestampFailure.Timeout);
    }

    [Test]
    public async Task It_preserves_the_reader_timeout_when_reader_and_command_disposal_fail()
    {
        var connection = new TimeoutCommandConnection(failNonQuery: false, failRead: true);
        var target = new EffectiveSchemaInfo("1.0.0", "v3", new string('a', 64), 0, new byte[32], [], []);
        var service = new SchemaRestamper(NullLogger.Instance, (_, _) => connection);

        Func<Task> action = () =>
            service.RestampAsync(
                SqlDialect.Mssql,
                "Server=localhost;Database=sample",
                30,
                target,
                true,
                CancellationToken.None
            );

        (await action.Should().ThrowAsync<SchemaRestampException>())
            .Which.Failure.Should()
            .Be(SchemaRestampFailure.Timeout);
    }

    private sealed class TimeoutCommandConnection : DbConnection
    {
        private readonly TimeoutCommandTransaction _transaction;
        private readonly bool _failNonQuery;
        private readonly bool _failRead;
        private readonly bool _failDispose;
        private readonly bool _waitForCancellation;
        private readonly bool _provideValidSnapshot;
        private readonly string _storedHash;
        private readonly List<TimeoutCommand> _commands = [];
        private int _readerCount;
        private readonly TaskCompletionSource _commandStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public TimeoutCommandConnection(
            bool failNonQuery = true,
            bool failRead = false,
            bool failDispose = true,
            bool waitForCancellation = false,
            bool failCommit = false,
            bool provideValidSnapshot = false,
            string? storedHash = null
        )
        {
            _failNonQuery = failNonQuery;
            _failRead = failRead;
            _failDispose = failDispose;
            _waitForCancellation = waitForCancellation;
            _provideValidSnapshot = provideValidSnapshot;
            _storedHash = storedHash ?? new string('b', 64);
            _transaction = new TimeoutCommandTransaction(this, failCommit);
        }

        public int OpenCount { get; private set; }

        public int BeginTransactionCount { get; private set; }

        public IReadOnlyList<TimeoutCommand> Commands => _commands;

        public TimeoutCommandTransaction Transaction => _transaction;

        public TaskCompletionSource CommandStarted => _commandStarted;

        public int DisposeCount { get; private set; }

        public bool WaitForCancellation => _waitForCancellation;

        public bool ProvideValidSnapshot => _provideValidSnapshot;

        public DbDataReader CreateReader()
        {
            if (!_provideValidSnapshot || _readerCount++ > 0)
            {
                return new DataTable().CreateDataReader();
            }

            var table = new DataTable();
            table.Columns.Add("singleton", typeof(short));
            table.Columns.Add("format", typeof(string));
            table.Columns.Add("hash", typeof(string));
            table.Columns.Add("keyCount", typeof(short));
            table.Columns.Add("seed", typeof(byte[]));
            table.Rows.Add((short)1, "1.0.0", _storedHash, (short)0, new byte[32]);
            return table.CreateDataReader();
        }

        public async Task<int> WaitForCancellationAsync(CancellationToken cancellationToken)
        {
            _commandStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        [AllowNull]
        public override string ConnectionString { get; set; } = "Host=localhost;Database=sample";

        public override string Database => "sample";

        public override string DataSource => "sample";

        public override string ServerVersion => "1";

        public override ConnectionState State => ConnectionState.Open;

        public override void ChangeDatabase(string databaseName) { }

        public override void Close() { }

        public override void Open() => OpenCount++;

        public override Task OpenAsync(CancellationToken cancellationToken)
        {
            OpenCount++;
            return Task.CompletedTask;
        }

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
        {
            BeginTransactionCount++;
            return _transaction;
        }

        protected override DbCommand CreateDbCommand()
        {
            var command = new TimeoutCommand(this, _failNonQuery, _failRead, _failDispose);
            _commands.Add(command);
            return command;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposeCount++;
            }
            base.Dispose(disposing);
        }
    }

    private sealed class TimeoutCommandTransaction : DbTransaction
    {
        private readonly DbConnection _connection;
        private readonly bool _failCommit;

        public TimeoutCommandTransaction(DbConnection connection, bool failCommit)
        {
            _connection = connection;
            _failCommit = failCommit;
        }

        public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;

        protected override DbConnection DbConnection => _connection;

        public int RollbackCount { get; private set; }

        public int DisposeCount { get; private set; }

        public int CommitCount { get; private set; }

        public override void Commit()
        {
            CommitCount++;
            if (_failCommit)
            {
                throw new IOException("commit transport failed");
            }
        }

        public override void Rollback() => RollbackCount++;

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposeCount++;
            }
            base.Dispose(disposing);
        }
    }

    private sealed class TimeoutCommand : DbCommand
    {
        private readonly TimeoutCommandConnection _connection;
        private readonly bool _failNonQuery;
        private readonly bool _failRead;
        private readonly bool _failDispose;
        private readonly DbParameterCollection _parameters = A.Fake<DbParameterCollection>();

        public TimeoutCommand(DbConnection connection, bool failNonQuery, bool failRead, bool failDispose)
        {
            _connection = (TimeoutCommandConnection)connection;
            DbConnection = connection;
            _failNonQuery = failNonQuery;
            _failRead = failRead;
            _failDispose = failDispose;
        }

        [AllowNull]
        public override string CommandText { get; set; } = string.Empty;

        public override int CommandTimeout { get; set; }

        public override CommandType CommandType { get; set; }

        public override bool DesignTimeVisible { get; set; }

        public override UpdateRowSource UpdatedRowSource { get; set; }

        [AllowNull]
        protected override DbConnection DbConnection { get; set; }

        protected override DbParameterCollection DbParameterCollection => _parameters;

        protected override DbTransaction? DbTransaction { get; set; }

        public DbTransaction? TransactionUsed => DbTransaction;

        public override void Cancel() { }

        public override int ExecuteNonQuery() => GetAffectedRows();

        public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
        {
            if (_connection.WaitForCancellation)
            {
                return _connection.WaitForCancellationAsync(cancellationToken);
            }
            if (_failNonQuery)
            {
                return Task.FromException<int>(new TimeoutException("primary timeout"));
            }
            return Task.FromResult(GetAffectedRows());
        }

        private int GetAffectedRows() => CommandText.StartsWith("UPDATE", StringComparison.Ordinal) ? 1 : 0;

        public override object? ExecuteScalar() => throw new NotSupportedException();

        public override void Prepare() { }

        protected override DbParameter CreateDbParameter() => A.Fake<DbParameter>();

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            if (((TimeoutCommandConnection)DbConnection).ProvideValidSnapshot)
            {
                return ((TimeoutCommandConnection)DbConnection).CreateReader();
            }

            var reader = A.Fake<DbDataReader>();
            A.CallTo(() => reader.ReadAsync(A<CancellationToken>._))
                .Returns(
                    _failRead
                        ? Task.FromException<bool>(new TimeoutException("primary timeout"))
                        : Task.FromResult(false)
                );
            A.CallTo(() => reader.DisposeAsync())
                .Returns(
                    _failDispose
                        ? new ValueTask(
                            Task.FromException(new IOException("secondary reader cleanup failure"))
                        )
                        : ValueTask.CompletedTask
                );
            return reader;
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && _failDispose)
            {
                throw new IOException("secondary command cleanup failure");
            }
        }
    }
}
