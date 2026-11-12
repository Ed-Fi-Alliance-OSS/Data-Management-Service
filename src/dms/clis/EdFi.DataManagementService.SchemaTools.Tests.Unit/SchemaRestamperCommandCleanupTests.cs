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

public abstract class SchemaRestamperCommandCleanupTests
{
    [TestFixture]
    public sealed class Given_Invalid_Snapshot : SchemaRestamperCommandCleanupTests
    {
        private TimeoutCommandConnection _connection = null!;
        private int _factoryCalls;
        private SchemaRestampException _failure = null!;

        [SetUp]
        public async Task Setup()
        {
            _connection = new TimeoutCommandConnection(
                failNonQuery: false,
                failRead: false,
                failDispose: false
            );
            _factoryCalls = 0;
            var target = new EffectiveSchemaInfo("1.0.0", "v3", new string('a', 64), 0, new byte[32], [], []);
            var service = new SchemaRestamper(
                NullLogger.Instance,
                (_, _) =>
                {
                    _factoryCalls++;
                    return _connection;
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

            _failure = await CaptureFailureAsync(action);
        }

        [Test]
        public void It_rejects_missing_metadata()
        {
            _failure.Failure.Should().Be(SchemaRestampFailure.Validation);
        }

        [Test]
        public void It_creates_one_connection()
        {
            _factoryCalls.Should().Be(1);
        }

        [Test]
        public void It_opens_once()
        {
            _connection.OpenCount.Should().Be(1);
        }

        [Test]
        public void It_begins_one_transaction()
        {
            _connection.BeginTransactionCount.Should().Be(1);
        }

        [Test]
        public void It_locks_metadata_in_order()
        {
            _connection
                .Commands.Take(3)
                .Select(command => command.CommandText)
                .Should()
                .Equal(SchemaRestampSql.LockCommands(SqlDialect.Pgsql));
        }

        [Test]
        public void It_uses_the_requested_command_timeout()
        {
            _connection.Commands.Should().OnlyContain(command => command.CommandTimeout == 41);
        }

        [Test]
        public void It_uses_a_transaction_for_every_command()
        {
            _connection.Commands.Select(command => command.TransactionUsed).Should().NotContainNulls();
        }

        [Test]
        public void It_shares_one_transaction()
        {
            _connection
                .Commands.Select(command => command.TransactionUsed)
                .Distinct()
                .Should()
                .ContainSingle();
        }
    }

    [TestFixture]
    public sealed class Given_Cancelled_Lock_Acquisition : SchemaRestamperCommandCleanupTests
    {
        private TimeoutCommandConnection _connection = null!;
        private SchemaRestampException _failure = null!;

        [SetUp]
        public async Task Setup()
        {
            _connection = new TimeoutCommandConnection(
                failNonQuery: false,
                failRead: false,
                failDispose: false,
                waitForCancellation: true
            );
            var target = new EffectiveSchemaInfo("1.0.0", "v3", new string('a', 64), 0, new byte[32], [], []);
            var service = new SchemaRestamper(NullLogger.Instance, (_, _) => _connection);
            using var cancellation = new CancellationTokenSource();
            var execution = service.RestampAsync(
                SqlDialect.Pgsql,
                "Host=localhost;Database=sample",
                30,
                target,
                true,
                cancellation.Token
            );

            await _connection.CommandStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await cancellation.CancelAsync();
            Func<Task> action = () => execution;

            _failure = await CaptureFailureAsync(action);
        }

        [Test]
        public void It_reports_cancellation()
        {
            _failure.Failure.Should().Be(SchemaRestampFailure.Cancelled);
        }

        [Test]
        public void It_rolls_back_once()
        {
            _connection.Transaction.RollbackCount.Should().Be(1);
        }

        [Test]
        public void It_disposes_the_transaction()
        {
            _connection.Transaction.DisposeCount.Should().Be(1);
        }

        [Test]
        public void It_disposes_the_connection()
        {
            _connection.DisposeCount.Should().Be(1);
        }
    }

    [TestFixture]
    public sealed class Given_Commit_Transport_Failure : SchemaRestamperCommandCleanupTests
    {
        private TimeoutCommandConnection _connection = null!;
        private SchemaRestampException _failure = null!;

        [SetUp]
        public async Task Setup()
        {
            _connection = new TimeoutCommandConnection(
                failNonQuery: false,
                failRead: false,
                failDispose: false,
                failCommit: true,
                provideValidSnapshot: true
            );
            var target = new EffectiveSchemaInfo("1.0.0", "v3", new string('a', 64), 0, new byte[32], [], []);
            var service = new SchemaRestamper(NullLogger.Instance, (_, _) => _connection);

            Func<Task> action = () =>
                service.RestampAsync(
                    SqlDialect.Pgsql,
                    "Host=localhost;Database=sample",
                    30,
                    target,
                    true,
                    CancellationToken.None
                );

            _failure = await CaptureFailureAsync(action);
        }

        [Test]
        public void It_reports_an_unknown_commit_outcome()
        {
            _failure
                .Failure.Should()
                .Be(
                    SchemaRestampFailure.CommitOutcomeUnknown,
                    $"command sequence: {string.Join(" | ", _connection.Commands.Select(command => command.CommandText))}; commits: {_connection.Transaction.CommitCount}"
                );
        }

        [Test]
        public void It_attempts_commit_once()
        {
            _connection.Transaction.CommitCount.Should().Be(1);
        }
    }

    [TestFixture]
    public sealed class Given_Cancellation_During_Commit : SchemaRestamperCommandCleanupTests
    {
        private TimeoutCommandConnection _connection = null!;
        private SchemaRestampResult _result = null!;
        private bool _cancellationRequested;

        [SetUp]
        public async Task Setup()
        {
            using var cancellation = new CancellationTokenSource();
            _connection = new TimeoutCommandConnection(
                failNonQuery: false,
                failRead: false,
                failDispose: false,
                duringCommit: cancellation.Cancel,
                provideValidSnapshot: true
            );
            var target = new EffectiveSchemaInfo("1.0.0", "v3", new string('a', 64), 0, new byte[32], [], []);
            var service = new SchemaRestamper(NullLogger.Instance, (_, _) => _connection);

            _result = await service.RestampAsync(
                SqlDialect.Pgsql,
                "Host=localhost;Database=sample",
                30,
                target,
                true,
                cancellation.Token
            );
            _cancellationRequested = cancellation.IsCancellationRequested;
        }

        [Test]
        public void It_reports_the_changed_stamp()
        {
            _result.Changed.Should().BeTrue();
        }

        [Test]
        public void It_commits_once()
        {
            _connection.Transaction.CommitCount.Should().Be(1);
        }

        [Test]
        public void It_does_not_cancel_commit()
        {
            _connection.Transaction.CommitCancellationToken.Should().Be(CancellationToken.None);
        }

        [Test]
        public void It_does_not_roll_back_the_commit()
        {
            _connection.Transaction.RollbackCount.Should().Be(0);
        }

        [Test]
        public void It_observes_the_cancellation_request()
        {
            _cancellationRequested.Should().BeTrue();
        }
    }

    [TestFixture]
    public sealed class Given_Old_Stamp_Retry : SchemaRestamperCommandCleanupTests
    {
        private TimeoutCommandConnection _connection = null!;
        private EffectiveSchemaInfo _target = null!;
        private SchemaRestampResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _connection = new TimeoutCommandConnection(
                failNonQuery: false,
                failRead: false,
                failDispose: false,
                provideValidSnapshot: true
            );
            _target = new EffectiveSchemaInfo("1.0.0", "v3", new string('a', 64), 0, new byte[32], [], []);
            var service = new SchemaRestamper(NullLogger.Instance, (_, _) => _connection);

            _result = await service.RestampAsync(
                SqlDialect.Pgsql,
                "Host=localhost;Database=sample",
                30,
                _target,
                true,
                CancellationToken.None
            );
        }

        [Test]
        public void It_applies_the_transition()
        {
            _result.Changed.Should().BeTrue();
        }

        [Test]
        public void It_reports_the_old_hash()
        {
            _result.PreviousHash.Should().Be(new string('b', 64));
        }

        [Test]
        public void It_reports_the_target_hash()
        {
            _result.TargetHash.Should().Be(_target.EffectiveSchemaHash);
        }

        [Test]
        public void It_commits_once()
        {
            _connection.Transaction.CommitCount.Should().Be(1);
        }
    }

    [TestFixture(SqlDialect.Pgsql, "Host=localhost;Database=sample")]
    [TestFixture(SqlDialect.Mssql, "Server=localhost;Database=sample")]
    public sealed class Given_Target_Stamp_Retry(SqlDialect dialect, string connectionString)
        : SchemaRestamperCommandCleanupTests
    {
        private TimeoutCommandConnection _connection = null!;
        private SchemaRestampResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            var targetHash = new string('a', 64);
            _connection = new TimeoutCommandConnection(
                failNonQuery: false,
                failRead: false,
                failDispose: false,
                provideValidSnapshot: true,
                storedHash: targetHash
            );
            var target = new EffectiveSchemaInfo("1.0.0", "v3", targetHash, 0, new byte[32], [], []);
            var service = new SchemaRestamper(NullLogger.Instance, (_, _) => _connection);

            _result = await service.RestampAsync(
                dialect,
                connectionString,
                30,
                target,
                false,
                CancellationToken.None
            );
        }

        [Test]
        public void It_reports_no_changes()
        {
            _result.Changed.Should().BeFalse();
        }

        [Test]
        public void It_reads_all_metadata()
        {
            _connection.ReaderCount.Should().Be(3, "only the three validation snapshots need readers");
        }

        [Test]
        public void It_does_not_delete_rows()
        {
            _connection
                .Commands.Where(command => command.CommandText.StartsWith("DELETE", StringComparison.Ordinal))
                .Should()
                .BeEmpty();
        }

        [Test]
        public void It_does_not_update_rows()
        {
            _connection
                .Commands.Where(command => command.CommandText.StartsWith("UPDATE", StringComparison.Ordinal))
                .Should()
                .BeEmpty();
        }

        [Test]
        public void It_finishes_the_transaction()
        {
            _connection.Transaction.CommitCount.Should().Be(1);
        }
    }

    [TestFixture]
    public sealed class Given_Command_And_Disposal_Failure : SchemaRestamperCommandCleanupTests
    {
        private SchemaRestampException _failure = null!;

        [SetUp]
        public async Task Setup()
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

            _failure = await CaptureFailureAsync(action);
        }

        [Test]
        public void It_preserves_the_primary_timeout()
        {
            _failure.Failure.Should().Be(SchemaRestampFailure.Timeout);
        }
    }

    [TestFixture]
    public sealed class Given_Reader_And_Disposal_Failure : SchemaRestamperCommandCleanupTests
    {
        private SchemaRestampException _failure = null!;

        [SetUp]
        public async Task Setup()
        {
            var connection = new TimeoutCommandConnection(
                failNonQuery: false,
                failRead: true,
                disposeFailureRequiresReader: true
            );
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

            _failure = await CaptureFailureAsync(action);
        }

        [Test]
        public void It_preserves_the_primary_timeout()
        {
            _failure.Failure.Should().Be(SchemaRestampFailure.Timeout);
        }
    }

    private static async Task<SchemaRestampException> CaptureFailureAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (SchemaRestampException exception)
        {
            return exception;
        }
        throw new InvalidOperationException("Expected a re-stamp failure.");
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
            Action? duringCommit = null,
            bool provideValidSnapshot = false,
            string? storedHash = null,
            bool disposeFailureRequiresReader = false
        )
        {
            _failNonQuery = failNonQuery;
            _failRead = failRead;
            _failDispose = failDispose;
            _waitForCancellation = waitForCancellation;
            _provideValidSnapshot = provideValidSnapshot;
            _storedHash = storedHash ?? new string('b', 64);
            DisposeFailureRequiresReader = disposeFailureRequiresReader;
            _transaction = new TimeoutCommandTransaction(this, failCommit, duringCommit);
        }

        public int OpenCount { get; private set; }

        public int BeginTransactionCount { get; private set; }

        public IReadOnlyList<TimeoutCommand> Commands => _commands;

        public TimeoutCommandTransaction Transaction => _transaction;

        public TaskCompletionSource CommandStarted => _commandStarted;

        public int DisposeCount { get; private set; }

        public bool WaitForCancellation => _waitForCancellation;

        public bool ProvideValidSnapshot => _provideValidSnapshot;

        public bool DisposeFailureRequiresReader { get; }

        public int ReaderCount => _readerCount;

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

        private readonly Action? _duringCommit;

        public TimeoutCommandTransaction(DbConnection connection, bool failCommit, Action? duringCommit)
        {
            _connection = connection;
            _failCommit = failCommit;
            _duringCommit = duringCommit;
        }

        public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;

        protected override DbConnection DbConnection => _connection;

        public int RollbackCount { get; private set; }

        public int DisposeCount { get; private set; }

        public int CommitCount { get; private set; }

        public CancellationToken CommitCancellationToken { get; private set; }

        public override void Commit()
        {
            CommitCount++;
            if (_failCommit)
            {
                throw new IOException("commit transport failed");
            }
        }

        public override Task CommitAsync(CancellationToken cancellationToken = default)
        {
            CommitCount++;
            CommitCancellationToken = cancellationToken;
            _duringCommit?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            if (_failCommit)
            {
                throw new IOException("commit transport failed");
            }

            return Task.CompletedTask;
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
        private bool _readerCreated;

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
            _readerCreated = true;
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
            if (disposing && _failDispose && (!_connection.DisposeFailureRequiresReader || _readerCreated))
            {
                throw new IOException("secondary command cleanup failure");
            }
        }
    }
}
