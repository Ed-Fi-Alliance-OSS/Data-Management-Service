// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using EdFi.DataManagementService.Backend.Postgresql;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
public class Given_PostgresqlRelationalCommandExecutor
{
    [Test]
    public async Task It_executes_the_relational_command_against_the_opened_request_connection()
    {
        var documentReferentialId = new ReferentialId(Guid.NewGuid());
        var descriptorReferentialId = new ReferentialId(Guid.NewGuid());
        var connection = new RecordingDbConnection(
            new RecordingDbCommand(
                CreateReader(
                    CreateLookupTable(
                        (
                            documentReferentialId.Value,
                            101L,
                            (short)11,
                            (short)11,
                            false,
                            "$$.schoolId=255901"
                        ),
                        (
                            descriptorReferentialId.Value,
                            202L,
                            (short)12,
                            (short)12,
                            true,
                            "$$.descriptor=uri://ed-fi.org/schooltypedescriptor#alternative"
                        )
                    )
                )
            )
        );
        var openConnectionCallCount = 0;
        var sut = new PostgresqlRelationalCommandExecutor(
            cancellationToken =>
            {
                cancellationToken.Should().Be(CancellationToken.None);
                openConnectionCallCount++;
                return Task.FromResult<DbConnection>(connection);
            },
            NullLogger<PostgresqlRelationalCommandExecutor>.Instance
        );

        var result = await sut.ExecuteReaderAsync(
            new RelationalCommand(
                "select * from dms.lookup where referential_id in (@p0, @p1)",
                [
                    new RelationalParameter("@p0", documentReferentialId.Value),
                    new RelationalParameter("@p1", descriptorReferentialId.Value),
                ]
            ),
            ReferenceLookupResultReader.ReadAsync
        );

        openConnectionCallCount.Should().Be(1);
        connection.CreateCommandCallCount.Should().Be(1);
        connection
            .Command.CommandText.Should()
            .Be("select * from dms.lookup where referential_id in (@p0, @p1)");
        connection.Command.Parameters.Should().HaveCount(2);
        connection.Command.Parameters[0].ParameterName.Should().Be("@p0");
        connection.Command.Parameters[0].Value.Should().Be(documentReferentialId.Value);
        connection.Command.Parameters[1].ParameterName.Should().Be("@p1");
        connection.Command.Parameters[1].Value.Should().Be(descriptorReferentialId.Value);
        connection.Command.ExecuteReaderCallCount.Should().Be(1);
        connection.Command.DisposeCallCount.Should().Be(1);
        connection.DisposeCallCount.Should().Be(1);
        result
            .Should()
            .BeEquivalentTo([
                new ReferenceLookupResult(documentReferentialId, 101L, 11, 11, false, "$$.schoolId=255901"),
                new ReferenceLookupResult(
                    descriptorReferentialId,
                    202L,
                    12,
                    12,
                    true,
                    "$$.descriptor=uri://ed-fi.org/schooltypedescriptor#alternative"
                ),
            ]);
    }

    [Test]
    public async Task It_applies_parameter_configuration_and_converts_null_values_to_dbnull()
    {
        var connection = new RecordingDbConnection(new RecordingDbCommand(CreateReader(CreateLookupTable())));
        var sut = new PostgresqlRelationalCommandExecutor(
            _ => Task.FromResult<DbConnection>(connection),
            NullLogger<PostgresqlRelationalCommandExecutor>.Instance
        );

        var result = await sut.ExecuteReaderAsync(
            new RelationalCommand(
                "select 1",
                [
                    new RelationalParameter(
                        "@nullable",
                        null,
                        parameter =>
                        {
                            parameter.DbType = DbType.Guid;
                            parameter.Direction = ParameterDirection.InputOutput;
                        }
                    ),
                ]
            ),
            static (_, _) => Task.FromResult("done")
        );

        result.Should().Be("done");
        connection.Command.Parameters.Should().ContainSingle();
        connection.Command.Parameters[0].ParameterName.Should().Be("@nullable");
        connection.Command.Parameters[0].Value.Should().Be(DBNull.Value);
        connection.Command.Parameters[0].DbType.Should().Be(DbType.Guid);
        connection.Command.Parameters[0].Direction.Should().Be(ParameterDirection.InputOutput);
    }

    private static DbDataReader CreateReader(params DataTable[] resultSets) =>
        resultSets.Length switch
        {
            0 => CreateLookupTable().CreateDataReader(),
            1 => resultSets[0].CreateDataReader(),
            _ => CreateDataSet(resultSets).CreateDataReader(),
        };

    private static DataSet CreateDataSet(params DataTable[] tables)
    {
        var dataSet = new DataSet();

        foreach (var table in tables)
        {
            dataSet.Tables.Add(table);
        }

        return dataSet;
    }

    private static DataTable CreateLookupTable(
        params (
            Guid ReferentialId,
            long DocumentId,
            short ResourceKeyId,
            short ReferentialIdentityResourceKeyId,
            bool IsDescriptor,
            string? VerificationIdentityKey
        )[] rows
    )
    {
        var table = new DataTable();
        table.Columns.Add("ReferentialId", typeof(Guid));
        table.Columns.Add("DocumentId", typeof(long));
        table.Columns.Add("ResourceKeyId", typeof(short));
        table.Columns.Add("ReferentialIdentityResourceKeyId", typeof(short));
        table.Columns.Add("IsDescriptor", typeof(bool));
        table.Columns.Add("VerificationIdentityKey", typeof(string));

        foreach (var row in rows)
        {
            table.Rows.Add(
                row.ReferentialId,
                row.DocumentId,
                row.ResourceKeyId,
                row.ReferentialIdentityResourceKeyId,
                row.IsDescriptor,
                row.VerificationIdentityKey
            );
        }

        return table;
    }
}

internal sealed class RecordingDbConnection(
    RecordingDbCommand command,
    Func<RecordingDbConnection, IsolationLevel, RecordingDbTransaction>? transactionFactory = null
) : DbConnection
{
    private ConnectionState _state = ConnectionState.Open;
    private readonly Func<RecordingDbConnection, IsolationLevel, RecordingDbTransaction> _transactionFactory =
        transactionFactory
        ?? ((connection, isolationLevel) => new RecordingDbTransaction(connection, isolationLevel));

    public RecordingDbCommand Command { get; } = command ?? throw new ArgumentNullException(nameof(command));

    public int CreateCommandCallCount { get; private set; }

    public int DisposeCallCount { get; private set; }

    public int BeginTransactionCallCount { get; private set; }

    public IsolationLevel? LastBeginTransactionIsolationLevel { get; private set; }

    public RecordingDbTransaction? LastTransaction { get; private set; }

    public int OpenAsyncCallCount { get; private set; }

    public CancellationToken? LastOpenAsyncCancellationToken { get; private set; }

    [AllowNull]
    public override string ConnectionString { get; set; } = "Host=localhost;Database=test";

    public override string Database => "test";

    public override string DataSource => "recording";

    public override string ServerVersion => "1.0";

    public override ConnectionState State => _state;

    public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();

    public override void Close() => _state = ConnectionState.Closed;

    public override void Open() => _state = ConnectionState.Open;

    public override Task OpenAsync(CancellationToken cancellationToken)
    {
        OpenAsyncCallCount++;
        LastOpenAsyncCancellationToken = cancellationToken;
        _state = ConnectionState.Open;

        return Task.CompletedTask;
    }

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
    {
        BeginTransactionCallCount++;
        LastBeginTransactionIsolationLevel = isolationLevel;
        LastTransaction = _transactionFactory(this, isolationLevel);
        return LastTransaction;
    }

    protected override DbCommand CreateDbCommand()
    {
        CreateCommandCallCount++;
        Command.Connection = this;
        return Command;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeCallCount++;
            _state = ConnectionState.Closed;
        }

        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync()
    {
        DisposeCallCount++;
        _state = ConnectionState.Closed;
        return ValueTask.CompletedTask;
    }
}

internal sealed class RecordingDbCommand(DbDataReader reader) : DbCommand
{
    private readonly RecordingDbParameterCollection _parameters = [];
    private readonly DbDataReader _reader = reader ?? throw new ArgumentNullException(nameof(reader));

    [AllowNull]
    public override string CommandText { get; set; } = string.Empty;

    public override int CommandTimeout { get; set; }

    public override CommandType CommandType { get; set; } = CommandType.Text;

    protected override DbConnection? DbConnection { get; set; }

    protected override DbParameterCollection DbParameterCollection => _parameters;

    protected override DbTransaction? DbTransaction { get; set; }

    public override bool DesignTimeVisible { get; set; }

    public override UpdateRowSource UpdatedRowSource { get; set; }

    public new List<RecordingDbParameter> Parameters => _parameters.Items;

    public int ExecuteReaderCallCount { get; private set; }

    public int ExecuteNonQueryCallCount { get; private set; }

    public int ExecuteScalarCallCount { get; private set; }

    public int DisposeCallCount { get; private set; }

    public int NonQueryResult { get; set; } = 1;

    public object? ScalarResult { get; set; }

    public new RecordingDbConnection? Connection
    {
        get => DbConnection as RecordingDbConnection;
        set => DbConnection = value;
    }

    public new RecordingDbTransaction? Transaction
    {
        get => DbTransaction as RecordingDbTransaction;
        set => DbTransaction = value;
    }

    public override void Cancel() { }

    public override int ExecuteNonQuery()
    {
        ExecuteNonQueryCallCount++;
        return NonQueryResult;
    }

    public override object? ExecuteScalar()
    {
        ExecuteScalarCallCount++;
        return ScalarResult;
    }

    public override void Prepare() { }

    protected override DbParameter CreateDbParameter() => new RecordingDbParameter();

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

    public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ExecuteNonQueryCallCount++;
        return Task.FromResult(NonQueryResult);
    }

    public override Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ExecuteScalarCallCount++;
        return Task.FromResult(ScalarResult);
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

internal sealed class RecordingDbParameterCollection : DbParameterCollection
{
    public List<RecordingDbParameter> Items { get; } = [];

    public override int Count => Items.Count;

    public override object SyncRoot => ((ICollection)Items).SyncRoot!;

    public override int Add(object value)
    {
        Items.Add((RecordingDbParameter)value);
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

    public override bool Contains(object value) => Items.Contains((RecordingDbParameter)value);

    public override bool Contains(string value) =>
        Items.Exists(parameter => parameter.ParameterName == value);

    public override void CopyTo(Array array, int index) => ((ICollection)Items).CopyTo(array, index);

    public override IEnumerator GetEnumerator() => Items.GetEnumerator();

    protected override DbParameter GetParameter(int index) => Items[index];

    protected override DbParameter GetParameter(string parameterName) =>
        Items.Single(parameter => parameter.ParameterName == parameterName);

    public override int IndexOf(object value) => Items.IndexOf((RecordingDbParameter)value);

    public override int IndexOf(string parameterName) =>
        Items.FindIndex(parameter => parameter.ParameterName == parameterName);

    public override void Insert(int index, object value) => Items.Insert(index, (RecordingDbParameter)value);

    public override void Remove(object value) => Items.Remove((RecordingDbParameter)value);

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
        Items[index] = (RecordingDbParameter)value;

    protected override void SetParameter(string parameterName, DbParameter value)
    {
        var index = IndexOf(parameterName);

        if (index < 0)
        {
            Items.Add((RecordingDbParameter)value);
            return;
        }

        Items[index] = (RecordingDbParameter)value;
    }
}

internal sealed class RecordingDbParameter : DbParameter
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

internal sealed class RecordingDbTransaction(RecordingDbConnection connection, IsolationLevel isolationLevel)
    : DbTransaction
{
    public override IsolationLevel IsolationLevel { get; } = isolationLevel;

    protected override DbConnection DbConnection { get; } =
        connection ?? throw new ArgumentNullException(nameof(connection));

    public int CommitCallCount { get; private set; }

    public int RollbackCallCount { get; private set; }

    public int DisposeCallCount { get; private set; }

    public override void Commit()
    {
        CommitCallCount++;
    }

    public override Task CommitAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CommitCallCount++;
        return Task.CompletedTask;
    }

    public override void Rollback()
    {
        RollbackCallCount++;
    }

    public override Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RollbackCallCount++;
        return Task.CompletedTask;
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
