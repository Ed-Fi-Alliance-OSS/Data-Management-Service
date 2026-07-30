// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using EdFi.DataManagementService.Backend.Composite;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit.Composite;

/// <summary>
/// Covers the ordered result-stream decoder and its failure attribution against a fake reader, so each
/// throw site is exercised deterministically. The live-provider counterpart proves the same rules hold
/// on real PostgreSQL and SQL Server.
/// </summary>
[TestFixture]
public class Given_A_Relational_Composite_Command_Execution
{
    private static RelationalCompositeCommand CreateCompositeCommand(int statementCount) =>
        new(
            new RelationalCommand("SELECT 1;"),
            [
                .. Enumerable
                    .Range(0, statementCount)
                    .Select(ordinal => new RelationalCompositeStatement(
                        ordinal,
                        $"statement-{ordinal}",
                        "SELECT 1;",
                        [],
                        RelationalCompositeResultShape.Sentinel
                    )),
            ]
        );

    [Test]
    public async Task It_decodes_each_statement_in_declared_order()
    {
        var session = new FakeWriteSession(new FakeReaderScript(resultSetCount: 3));

        var outcomes = await new RelationalCompositeCommandExecution().ExecuteAsync(
            session,
            CreateCompositeCommand(3)
        );

        outcomes.Select(outcome => outcome.Ordinal).Should().Equal(0, 1, 2);
        outcomes.Select(outcome => outcome.Label).Should().Equal("statement-0", "statement-1", "statement-2");
    }

    [Test]
    public async Task It_attributes_a_failure_while_opening_the_reader_to_the_first_statement()
    {
        var session = new FakeWriteSession(
            new FakeReaderScript(resultSetCount: 3) { ThrowOnOpen = new FakeDbException("boom") }
        );
        var execution = new RelationalCompositeCommandExecution();

        var act = async () => await execution.ExecuteAsync(session, CreateCompositeCommand(3));

        await act.Should().ThrowAsync<FakeDbException>();
        execution.Failure.Should().NotBeNull();
        execution.Failure!.Ordinal.Should().Be(0);
        execution.Failure.Label.Should().Be("statement-0");
        execution.Failure.Stage.Should().Be(RelationalCompositeFailureStage.OpeningReader);
    }

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    public async Task It_attributes_a_failure_while_advancing_to_result_set_k_to_statement_k(
        int failingOrdinal
    )
    {
        var session = new FakeWriteSession(
            new FakeReaderScript(resultSetCount: 4)
            {
                ThrowOnNextResultAtIndex = failingOrdinal,
                NextResultException = new FakeDbException("mid-batch"),
            }
        );
        var execution = new RelationalCompositeCommandExecution();

        var act = async () => await execution.ExecuteAsync(session, CreateCompositeCommand(4));

        await act.Should().ThrowAsync<FakeDbException>();
        execution.Failure!.Ordinal.Should().Be(failingOrdinal);
        execution.Failure.Label.Should().Be($"statement-{failingOrdinal}");
        execution.Failure.Stage.Should().Be(RelationalCompositeFailureStage.AdvancingResultSet);
    }

    [Test]
    public async Task It_attributes_a_failure_while_reading_rows_to_the_current_statement()
    {
        var session = new FakeWriteSession(
            new FakeReaderScript(resultSetCount: 3)
            {
                ThrowOnReadAtResultSetIndex = 2,
                ReadException = new FakeDbException("row read"),
            }
        );
        var execution = new RelationalCompositeCommandExecution();

        var act = async () => await execution.ExecuteAsync(session, CreateCompositeCommand(3));

        await act.Should().ThrowAsync<FakeDbException>();
        execution.Failure!.Ordinal.Should().Be(2);
        execution.Failure.Stage.Should().Be(RelationalCompositeFailureStage.ReadingRows);
    }

    [Test]
    public async Task It_does_not_fabricate_an_ordinal_for_a_failure_that_carries_no_provider_error()
    {
        var session = new FakeWriteSession(
            new FakeReaderScript(resultSetCount: 3)
            {
                ThrowOnOpen = new InvalidTimeZoneException("connection-level"),
            }
        );
        var execution = new RelationalCompositeCommandExecution();

        var act = async () => await execution.ExecuteAsync(session, CreateCompositeCommand(3));

        await act.Should().ThrowAsync<InvalidTimeZoneException>();
        execution.Failure!.Ordinal.Should().BeNull();
        execution.Failure.Label.Should().BeNull();
        execution.Failure.Stage.Should().Be(RelationalCompositeFailureStage.Unattributable);
    }

    [Test]
    public async Task It_does_not_wrap_or_replace_the_provider_exception()
    {
        var providerException = new FakeDbException("23505");
        var session = new FakeWriteSession(
            new FakeReaderScript(resultSetCount: 2)
            {
                ThrowOnNextResultAtIndex = 1,
                NextResultException = providerException,
            }
        );

        var act = async () =>
            await new RelationalCompositeCommandExecution().ExecuteAsync(session, CreateCompositeCommand(2));

        // The existing classifier, constraint resolver, and AUTH1 dispatcher all read the provider
        // exception directly, so it must arrive unchanged.
        (await act.Should().ThrowAsync<FakeDbException>())
            .Which.Should()
            .BeSameAs(providerException);
    }

    [Test]
    public async Task It_reports_a_provider_failure_raised_while_opening_the_reader_to_the_session()
    {
        var providerException = new FakeDbException("open");
        var session = new FakeWriteSession(
            new FakeReaderScript(resultSetCount: 3) { ThrowOnOpen = providerException }
        );

        var act = async () =>
            await new RelationalCompositeCommandExecution().ExecuteAsync(session, CreateCompositeCommand(3));

        await act.Should().ThrowAsync<FakeDbException>();
        session.ReportedFailures.Should().ContainSingle().Which.Should().BeSameAs(providerException);
    }

    [Test]
    public async Task It_reports_a_provider_failure_raised_while_advancing_a_result_set_to_the_session()
    {
        var providerException = new FakeDbException("advance");
        var session = new FakeWriteSession(
            new FakeReaderScript(resultSetCount: 3)
            {
                ThrowOnNextResultAtIndex = 1,
                NextResultException = providerException,
            }
        );

        var act = async () =>
            await new RelationalCompositeCommandExecution().ExecuteAsync(session, CreateCompositeCommand(3));

        await act.Should().ThrowAsync<FakeDbException>();
        session.ReportedFailures.Should().ContainSingle().Which.Should().BeSameAs(providerException);
    }

    [Test]
    public async Task It_reports_a_provider_failure_raised_while_reading_rows_to_the_session()
    {
        var providerException = new FakeDbException("row read");
        var session = new FakeWriteSession(
            new FakeReaderScript(resultSetCount: 3)
            {
                ThrowOnReadAtResultSetIndex = 2,
                ReadException = providerException,
            }
        );

        var act = async () =>
            await new RelationalCompositeCommandExecution().ExecuteAsync(session, CreateCompositeCommand(3));

        await act.Should().ThrowAsync<FakeDbException>();
        session.ReportedFailures.Should().ContainSingle().Which.Should().BeSameAs(providerException);
    }

    [Test]
    public async Task It_does_not_report_a_failure_that_carries_no_provider_error()
    {
        var session = new FakeWriteSession(
            new FakeReaderScript(resultSetCount: 3)
            {
                ThrowOnOpen = new InvalidTimeZoneException("connection-level"),
            }
        );

        var act = async () =>
            await new RelationalCompositeCommandExecution().ExecuteAsync(session, CreateCompositeCommand(3));

        // A non-provider failure says nothing about the transaction's server-side state, so it must not
        // become grounds for tolerating a rollback.
        await act.Should().ThrowAsync<InvalidTimeZoneException>();
        session.ReportedFailures.Should().BeEmpty();
    }

    [Test]
    public async Task It_does_not_report_a_cancellation()
    {
        var session = new FakeWriteSession(
            new FakeReaderScript(resultSetCount: 3)
            {
                ThrowOnOpen = new OperationCanceledException("cancelled"),
            }
        );

        var act = async () =>
            await new RelationalCompositeCommandExecution().ExecuteAsync(session, CreateCompositeCommand(3));

        await act.Should().ThrowAsync<OperationCanceledException>();
        session.ReportedFailures.Should().BeEmpty();
    }

    [Test]
    public async Task It_reports_nothing_when_every_statement_decodes()
    {
        var session = new FakeWriteSession(new FakeReaderScript(resultSetCount: 3));

        await new RelationalCompositeCommandExecution().ExecuteAsync(session, CreateCompositeCommand(3));

        session.ReportedFailures.Should().BeEmpty();
    }

    [Test]
    public async Task It_fails_when_the_provider_produces_fewer_result_sets_than_declared()
    {
        var session = new FakeWriteSession(new FakeReaderScript(resultSetCount: 2));

        var act = async () =>
            await new RelationalCompositeCommandExecution().ExecuteAsync(session, CreateCompositeCommand(3));

        await act.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*produced no result set for ordinal 2*");
    }

    [Test]
    public async Task It_fails_when_a_sentinel_echoes_a_different_ordinal_than_declared()
    {
        var session = new FakeWriteSession(
            new FakeReaderScript(resultSetCount: 2) { SentinelValueOverride = 99 }
        );

        var act = async () =>
            await new RelationalCompositeCommandExecution().ExecuteAsync(session, CreateCompositeCommand(2));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*echoed 99*");
    }

    [Test]
    public async Task It_returns_null_for_a_scalar_statement_with_no_row()
    {
        var command = new RelationalCompositeCommand(
            new RelationalCommand("SELECT 1;"),
            [
                new RelationalCompositeStatement(
                    0,
                    "capture-target",
                    "SELECT 1;",
                    [],
                    RelationalCompositeResultShape.Scalar
                ),
            ]
        );
        var session = new FakeWriteSession(new FakeReaderScript(resultSetCount: 1) { RowsPerResultSet = 0 });

        var outcomes = await new RelationalCompositeCommandExecution().ExecuteAsync(session, command);

        outcomes.Should().ContainSingle().Which.Value.Should().BeNull();
    }

    [Test]
    public async Task It_counts_rows_for_a_rows_statement_without_a_reader()
    {
        var command = new RelationalCompositeCommand(
            new RelationalCommand("SELECT 1;"),
            [
                new RelationalCompositeStatement(
                    0,
                    "hydrate",
                    "SELECT 1;",
                    [],
                    RelationalCompositeResultShape.Rows
                ),
            ]
        );
        var session = new FakeWriteSession(new FakeReaderScript(resultSetCount: 1) { RowsPerResultSet = 4 });

        var outcomes = await new RelationalCompositeCommandExecution().ExecuteAsync(session, command);

        outcomes.Should().ContainSingle().Which.Value.Should().Be(4);
    }

    private sealed class FakeReaderScript(int resultSetCount)
    {
        public int ResultSetCount { get; } = resultSetCount;

        public int RowsPerResultSet { get; init; } = 1;

        public Exception? ThrowOnOpen { get; init; }

        public int? ThrowOnNextResultAtIndex { get; init; }

        public Exception? NextResultException { get; init; }

        public int? ThrowOnReadAtResultSetIndex { get; init; }

        public Exception? ReadException { get; init; }

        public int? SentinelValueOverride { get; init; }
    }

    private sealed class FakeWriteSession(FakeReaderScript script) : IRelationalWriteSession
    {
        private readonly List<DbException> _reportedFailures = [];

        public IReadOnlyList<DbException> ReportedFailures => _reportedFailures;

        public DbConnection Connection => throw new NotSupportedException();

        public DbTransaction Transaction => throw new NotSupportedException();

        public DbCommand CreateCommand(RelationalCommand command) => new FakeDbCommand(script);

        public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RollbackAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void ReportDatabaseFailure(DbException exception) => _reportedFailures.Add(exception);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeDbCommand(FakeReaderScript script) : DbCommand
    {
        [AllowNull]
        public override string CommandText { get; set; } = string.Empty;
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; }
        protected override DbParameterCollection DbParameterCollection { get; } =
            new FakeDbParameterCollection();
        protected override DbTransaction? DbTransaction { get; set; }

        public override void Cancel() { }

        public override int ExecuteNonQuery() => throw new NotSupportedException();

        public override object? ExecuteScalar() => throw new NotSupportedException();

        public override void Prepare() { }

        protected override DbParameter CreateDbParameter() => throw new NotSupportedException();

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) =>
            script.ThrowOnOpen is { } exception ? throw exception : new FakeDbDataReader(script);
    }

    private sealed class FakeDbParameterCollection : DbParameterCollection
    {
        private readonly List<object> _values = [];

        public override int Count => _values.Count;

        public override object SyncRoot => _values;

        public override int Add(object value)
        {
            _values.Add(value);
            return _values.Count - 1;
        }

        public override void AddRange(Array values) => throw new NotSupportedException();

        public override void Clear() => _values.Clear();

        public override bool Contains(object value) => _values.Contains(value);

        public override bool Contains(string value) => false;

        public override void CopyTo(Array array, int index) => throw new NotSupportedException();

        public override System.Collections.IEnumerator GetEnumerator() => _values.GetEnumerator();

        public override int IndexOf(object value) => _values.IndexOf(value);

        public override int IndexOf(string parameterName) => -1;

        public override void Insert(int index, object value) => _values.Insert(index, value);

        public override void Remove(object value) => _values.Remove(value);

        public override void RemoveAt(int index) => _values.RemoveAt(index);

        public override void RemoveAt(string parameterName) => throw new NotSupportedException();

        protected override DbParameter GetParameter(int index) => throw new NotSupportedException();

        protected override DbParameter GetParameter(string parameterName) =>
            throw new NotSupportedException();

        protected override void SetParameter(int index, DbParameter value) =>
            throw new NotSupportedException();

        protected override void SetParameter(string parameterName, DbParameter value) =>
            throw new NotSupportedException();
    }

    private sealed class FakeDbDataReader(FakeReaderScript script) : DbDataReader
    {
        private int _resultSetIndex;
        private int _rowIndex;

        public override int FieldCount => 1;
        public override bool HasRows => script.RowsPerResultSet > 0;
        public override bool IsClosed => false;
        public override int RecordsAffected => 0;
        public override int Depth => 0;
        public override object this[int ordinal] => GetValue(ordinal);
        public override object this[string name] => GetValue(0);

        public override Task<bool> ReadAsync(CancellationToken cancellationToken)
        {
            if (
                script.ThrowOnReadAtResultSetIndex == _resultSetIndex
                && script.ReadException is { } exception
            )
            {
                throw exception;
            }

            if (_rowIndex >= script.RowsPerResultSet)
            {
                return Task.FromResult(false);
            }

            _rowIndex++;
            return Task.FromResult(true);
        }

        public override Task<bool> NextResultAsync(CancellationToken cancellationToken)
        {
            var nextIndex = _resultSetIndex + 1;

            if (script.ThrowOnNextResultAtIndex == nextIndex && script.NextResultException is { } exception)
            {
                throw exception;
            }

            if (nextIndex >= script.ResultSetCount)
            {
                return Task.FromResult(false);
            }

            _resultSetIndex = nextIndex;
            _rowIndex = 0;
            return Task.FromResult(true);
        }

        public override object GetValue(int ordinal) => script.SentinelValueOverride ?? _resultSetIndex;

        public override bool IsDBNull(int ordinal) => false;

        public override Task<bool> IsDBNullAsync(int ordinal, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public override bool Read() => ReadAsync(CancellationToken.None).GetAwaiter().GetResult();

        public override bool NextResult() => NextResultAsync(CancellationToken.None).GetAwaiter().GetResult();

        public override bool GetBoolean(int ordinal) => throw new NotSupportedException();

        public override byte GetByte(int ordinal) => throw new NotSupportedException();

        public override long GetBytes(
            int ordinal,
            long dataOffset,
            byte[]? buffer,
            int bufferOffset,
            int length
        ) => throw new NotSupportedException();

        public override char GetChar(int ordinal) => throw new NotSupportedException();

        public override long GetChars(
            int ordinal,
            long dataOffset,
            char[]? buffer,
            int bufferOffset,
            int length
        ) => throw new NotSupportedException();

        public override string GetDataTypeName(int ordinal) => "int";

        public override DateTime GetDateTime(int ordinal) => throw new NotSupportedException();

        public override decimal GetDecimal(int ordinal) => throw new NotSupportedException();

        public override double GetDouble(int ordinal) => throw new NotSupportedException();

        public override System.Collections.IEnumerator GetEnumerator() => throw new NotSupportedException();

        public override Type GetFieldType(int ordinal) => typeof(int);

        public override float GetFloat(int ordinal) => throw new NotSupportedException();

        public override Guid GetGuid(int ordinal) => throw new NotSupportedException();

        public override short GetInt16(int ordinal) => throw new NotSupportedException();

        public override int GetInt32(int ordinal) => (int)GetValue(ordinal);

        public override long GetInt64(int ordinal) => throw new NotSupportedException();

        public override string GetName(int ordinal) => "LogicalStatementOrdinal";

        public override int GetOrdinal(string name) => 0;

        public override string GetString(int ordinal) => throw new NotSupportedException();

        public override int GetValues(object[] values) => throw new NotSupportedException();
    }

    private sealed class FakeDbException(string message) : DbException(message);
}
