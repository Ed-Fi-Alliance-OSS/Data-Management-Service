// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using EdFi.DataManagementService.Backend.External;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
public class Given_SessionRelationalCommandExecutor
{
    [Test]
    public void It_maps_Npgsql_connections_to_PostgreSql()
    {
        var connection = new NpgsqlTestConnection();
        var transaction = new StubDbTransaction(connection);

        var sut = new SessionRelationalCommandExecutor(connection, transaction);

        sut.Dialect.Should().Be(SqlDialect.Pgsql);
    }

    [Test]
    public void It_maps_SqlClient_connections_to_Sql_Server()
    {
        var connection = new SqlClientTestConnection();
        var transaction = new StubDbTransaction(connection);

        var sut = new SessionRelationalCommandExecutor(connection, transaction);

        sut.Dialect.Should().Be(SqlDialect.Mssql);
    }

    [Test]
    public void It_fails_fast_for_unsupported_connection_types()
    {
        var connection = new UnknownTestConnection();
        var transaction = new StubDbTransaction(connection);

        var act = () => new SessionRelationalCommandExecutor(connection, transaction);

        act.Should()
            .Throw<NotSupportedException>()
            .WithMessage(
                "Unsupported DbConnection type '*UnknownTestConnection' for relational dialect detection."
            );
    }

    [Test]
    public async Task It_binds_the_session_transaction_and_configures_parameters()
    {
        var connection = new NpgsqlTestConnection(new TrackingDbCommand(new DataTable().CreateDataReader()));
        var transaction = new StubDbTransaction(connection);
        var sut = new SessionRelationalCommandExecutor(connection, transaction);

        var result = await sut.ExecuteReaderAsync(
            new RelationalCommand(
                "select * from dms.lookup where referential_id = @referentialId and soft_deleted = @softDeleted",
                [
                    new RelationalParameter(
                        "@referentialId",
                        Guid.Parse("00000000-0000-0000-0000-000000000123")
                    ),
                    new RelationalParameter(
                        "@softDeleted",
                        null,
                        parameter =>
                        {
                            parameter.DbType = DbType.Boolean;
                            parameter.Direction = ParameterDirection.InputOutput;
                        }
                    ),
                ]
            ),
            static (_, _) => Task.FromResult("done")
        );

        result.Should().Be("done");
        connection.CreateCommandCallCount.Should().Be(1);
        connection
            .Command.CommandText.Should()
            .Be(
                "select * from dms.lookup where referential_id = @referentialId and soft_deleted = @softDeleted"
            );
        connection.Command.BoundTransaction.Should().BeSameAs(transaction);
        connection.Command.Parameters.Should().HaveCount(2);
        connection.Command.Parameters[0].ParameterName.Should().Be("@referentialId");
        connection
            .Command.Parameters[0]
            .Value.Should()
            .Be(Guid.Parse("00000000-0000-0000-0000-000000000123"));
        connection.Command.Parameters[1].ParameterName.Should().Be("@softDeleted");
        connection.Command.Parameters[1].Value.Should().Be(DBNull.Value);
        connection.Command.Parameters[1].DbType.Should().Be(DbType.Boolean);
        connection.Command.Parameters[1].Direction.Should().Be(ParameterDirection.InputOutput);
        connection.Command.ExecuteReaderCallCount.Should().Be(1);
        connection.Command.DisposeCallCount.Should().Be(1);
    }

    private sealed class NpgsqlTestConnection : StubDbConnection
    {
        public NpgsqlTestConnection(TrackingDbCommand? command = null)
            : base(command) { }

        public override string DataSource => "npgsql";
    }

    private sealed class SqlClientTestConnection : StubDbConnection
    {
        public SqlClientTestConnection(TrackingDbCommand? command = null)
            : base(command) { }

        public override string DataSource => "sqlclient";
    }

    private sealed class UnknownTestConnection : StubDbConnection
    {
        public UnknownTestConnection(TrackingDbCommand? command = null)
            : base(command) { }

        public override string DataSource => "unknown";
    }

    private class StubDbConnection(TrackingDbCommand? command = null) : DbConnection
    {
        private ConnectionState _state = ConnectionState.Open;
        private readonly TrackingDbCommand? _command = command;

        public int CreateCommandCallCount { get; private set; }

        public TrackingDbCommand Command =>
            _command ?? throw new NotSupportedException("No command configured for this test connection.");

        [AllowNull]
        public override string ConnectionString { get; set; } = "Host=localhost;Database=test";

        public override string Database => "test";

        public override string DataSource => "stub";

        public override string ServerVersion => "1.0";

        public override ConnectionState State => _state;

        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();

        public override void Close() => _state = ConnectionState.Closed;

        public override void Open() => _state = ConnectionState.Open;

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
            new StubDbTransaction(this);

        protected override DbCommand CreateDbCommand()
        {
            CreateCommandCallCount++;
            Command.AttachConnection(this);
            return Command;
        }
    }

    private sealed class StubDbTransaction(DbConnection connection) : DbTransaction
    {
        public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;

        protected override DbConnection DbConnection { get; } = connection;

        public override void Commit() { }

        public override void Rollback() { }
    }

    private sealed class TrackingDbCommand(DbDataReader reader) : DbCommand
    {
        private readonly TrackingDbParameterCollection _parameters = [];
        private readonly DbDataReader _reader = reader;

        [AllowNull]
        public override string CommandText { get; set; } = string.Empty;

        public override int CommandTimeout { get; set; }

        public override CommandType CommandType { get; set; } = CommandType.Text;

        protected override DbConnection? DbConnection { get; set; }

        protected override DbParameterCollection DbParameterCollection => _parameters;

        protected override DbTransaction? DbTransaction { get; set; }

        public override bool DesignTimeVisible { get; set; }

        public override UpdateRowSource UpdatedRowSource { get; set; }

        public new List<TrackingDbParameter> Parameters => _parameters.Items;

        public int ExecuteReaderCallCount { get; private set; }

        public int DisposeCallCount { get; private set; }

        public StubDbTransaction? BoundTransaction => DbTransaction as StubDbTransaction;

        public override void Cancel() { }

        public override int ExecuteNonQuery() => throw new NotSupportedException();

        public override object? ExecuteScalar() => throw new NotSupportedException();

        public override void Prepare() { }

        public void AttachConnection(StubDbConnection connection) => DbConnection = connection;

        protected override DbParameter CreateDbParameter() => new TrackingDbParameter();

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            ExecuteReaderCallCount++;
            return _reader;
        }

        protected override Task<DbDataReader> ExecuteDbDataReaderAsync(
            CommandBehavior behavior,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExecuteReaderCallCount++;
            return Task.FromResult(_reader);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposeCallCount++;
            }

            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync()
        {
            DisposeCallCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TrackingDbParameterCollection : DbParameterCollection
    {
        public List<TrackingDbParameter> Items { get; } = [];

        public override int Count => Items.Count;

        public override object SyncRoot => ((ICollection)Items).SyncRoot!;

        public override int Add(object value)
        {
            Items.Add((TrackingDbParameter)value);
            return Items.Count - 1;
        }

        public override void AddRange(Array values)
        {
            foreach (var value in values)
            {
                Add(value!);
            }
        }

        public override void Clear() => Items.Clear();

        public override bool Contains(object value) => Items.Contains((TrackingDbParameter)value);

        public override bool Contains(string value) =>
            Items.Exists(parameter => parameter.ParameterName == value);

        public override void CopyTo(Array array, int index) => ((ICollection)Items).CopyTo(array, index);

        public override IEnumerator GetEnumerator() => Items.GetEnumerator();

        protected override DbParameter GetParameter(int index) => Items[index];

        protected override DbParameter GetParameter(string parameterName) =>
            Items.Single(parameter => parameter.ParameterName == parameterName);

        public override int IndexOf(object value) => Items.IndexOf((TrackingDbParameter)value);

        public override int IndexOf(string parameterName) =>
            Items.FindIndex(parameter => parameter.ParameterName == parameterName);

        public override void Insert(int index, object value) =>
            Items.Insert(index, (TrackingDbParameter)value);

        public override void Remove(object value) => Items.Remove((TrackingDbParameter)value);

        public override void RemoveAt(int index) => Items.RemoveAt(index);

        public override void RemoveAt(string parameterName)
        {
            var index = IndexOf(parameterName);

            if (index >= 0)
            {
                Items.RemoveAt(index);
            }
        }

        protected override void SetParameter(int index, DbParameter value) =>
            Items[index] = (TrackingDbParameter)value;

        protected override void SetParameter(string parameterName, DbParameter value)
        {
            var index = IndexOf(parameterName);

            if (index < 0)
            {
                Items.Add((TrackingDbParameter)value);
                return;
            }

            Items[index] = (TrackingDbParameter)value;
        }
    }

    private sealed class TrackingDbParameter : DbParameter
    {
        public override DbType DbType { get; set; }

        public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;

        public override bool IsNullable { get; set; }

        [AllowNull]
        public override string ParameterName { get; set; } = string.Empty;

        [AllowNull]
        public override string SourceColumn { get; set; } = string.Empty;

        public override object? Value { get; set; }

        public override bool SourceColumnNullMapping { get; set; }

        public override int Size { get; set; }

        public override void ResetDbType() => DbType = DbType.Object;
    }
}
