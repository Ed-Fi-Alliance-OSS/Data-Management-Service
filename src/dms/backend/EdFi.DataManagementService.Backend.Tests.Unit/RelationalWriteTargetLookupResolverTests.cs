// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
public class Given_RelationalWrite_Target_Lookup_Surfaces
{
    private static readonly QualifiedResourceName _requestResource = new("Ed-Fi", "Student");

    [Test]
    public async Task It_returns_create_new_for_post_re_evaluation_when_request_referential_id_does_not_match_an_existing_document()
    {
        var referentialId = new ReferentialId(Guid.NewGuid());
        var candidateDocumentUuid = new DocumentUuid(Guid.NewGuid());
        var writeSession = CreateWriteSession(CreateLookupReader());
        var sut = new RelationalWriteTargetLookupResolver();

        var result = await sut.ResolveForPostAsync(
            CreateMappingSet(SqlDialect.Pgsql),
            _requestResource,
            referentialId,
            candidateDocumentUuid,
            writeSession.Connection,
            writeSession.Transaction
        );

        result
            .Should()
            .BeEquivalentTo(new RelationalWriteTargetLookupResult.CreateNew(candidateDocumentUuid));
        writeSession.Connection.CreateCommandCallCount.Should().Be(1);
        writeSession.Connection.Command.CommandText.Should().Contain("dms.\"ReferentialIdentity\"");
        writeSession
            .Connection.Command.Parameters.Select(parameter => parameter.Value)
            .Should()
            .Equal(referentialId.Value, (short)1);
    }

    [Test]
    public async Task It_returns_existing_document_for_post_re_evaluation_when_request_referential_id_matches_a_persisted_document()
    {
        var referentialId = new ReferentialId(Guid.NewGuid());
        var candidateDocumentUuid = new DocumentUuid(Guid.NewGuid());
        var existingDocumentUuid = new DocumentUuid(Guid.NewGuid());
        const long observedContentVersion = 701L;
        var writeSession = CreateWriteSession(
            CreateLookupReader((101L, existingDocumentUuid.Value, observedContentVersion))
        );
        var sut = new RelationalWriteTargetLookupResolver();

        var result = await sut.ResolveForPostAsync(
            CreateMappingSet(SqlDialect.Pgsql),
            _requestResource,
            referentialId,
            candidateDocumentUuid,
            writeSession.Connection,
            writeSession.Transaction
        );

        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteTargetLookupResult.ExistingDocument(
                    101L,
                    existingDocumentUuid,
                    observedContentVersion
                )
            );
    }

    [TestCase(SqlDialect.Pgsql, "dms.\"Document\"")]
    [TestCase(SqlDialect.Mssql, "[dms].[Document]")]
    public async Task It_returns_not_found_for_repository_put_lookup_when_requested_document_uuid_does_not_match_a_persisted_document(
        SqlDialect dialect,
        string expectedTableFragment
    )
    {
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        var commandExecutor = new RecordingRelationalCommandExecutor(CreateLookupReader());
        var sut = new RelationalWriteTargetLookupService(commandExecutor);

        var result = await sut.ResolveForPutAsync(CreateMappingSet(dialect), _requestResource, documentUuid);

        result.Should().BeOfType<RelationalWriteTargetLookupResult.NotFound>();
        commandExecutor.ExecuteReaderAsyncCallCount.Should().Be(1);
        commandExecutor.CapturedCommand.Should().NotBeNull();
        commandExecutor.CapturedCommand!.CommandText.Should().Contain(expectedTableFragment);
        commandExecutor
            .CapturedCommand.Parameters.Select(parameter => parameter.Value)
            .Should()
            .Equal(documentUuid.Value, (short)1);
    }

    [TestCase(SqlDialect.Pgsql, "dms.\"Document\"")]
    [TestCase(SqlDialect.Mssql, "[dms].[Document]")]
    public async Task It_returns_existing_document_for_repository_put_lookup_when_requested_document_uuid_matches_a_persisted_document(
        SqlDialect dialect,
        string expectedTableFragment
    )
    {
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        const long observedContentVersion = 907L;
        var commandExecutor = new RecordingRelationalCommandExecutor(
            CreateLookupReader((404L, documentUuid.Value, observedContentVersion))
        );
        var sut = new RelationalWriteTargetLookupService(commandExecutor);

        var result = await sut.ResolveForPutAsync(CreateMappingSet(dialect), _requestResource, documentUuid);

        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteTargetLookupResult.ExistingDocument(
                    404L,
                    documentUuid,
                    observedContentVersion
                )
            );
        commandExecutor.ExecuteReaderAsyncCallCount.Should().Be(1);
        commandExecutor.CapturedCommand.Should().NotBeNull();
        commandExecutor.CapturedCommand!.CommandText.Should().Contain(expectedTableFragment);
        commandExecutor
            .CapturedCommand.Parameters.Select(parameter => parameter.Value)
            .Should()
            .Equal(documentUuid.Value, (short)1);
    }

    private static MappingSet CreateMappingSet(SqlDialect dialect)
    {
        var mappingSet = RelationalAccessTestData.CreateMappingSet(_requestResource);

        return mappingSet with
        {
            Key = new MappingSetKey(
                mappingSet.Key.EffectiveSchemaHash,
                dialect,
                mappingSet.Key.RelationalMappingVersion
            ),
            Model = mappingSet.Model with { Dialect = dialect },
        };
    }

    private static DataTableReader CreateLookupReader(
        params (long DocumentId, Guid DocumentUuid, long ContentVersion)[] rows
    )
    {
        var table = new DataTable();
        table.Columns.Add("DocumentId", typeof(long));
        table.Columns.Add("DocumentUuid", typeof(Guid));
        table.Columns.Add("ResourceKeyId", typeof(short));
        table.Columns.Add("ContentVersion", typeof(long));

        foreach (var row in rows)
        {
            table.Rows.Add(row.DocumentId, row.DocumentUuid, (short)1, row.ContentVersion);
        }

        return table.CreateDataReader();
    }

    private static TestRelationalWriteSession CreateWriteSession(DataTableReader reader)
    {
        var command = new TrackingDbCommand(reader);
        var connection = new NpgsqlTestDbConnection(command);
        var transaction = new TestDbTransaction(connection);

        return new TestRelationalWriteSession(connection, transaction);
    }

    private sealed class RecordingRelationalCommandExecutor(DataTableReader reader)
        : IRelationalCommandExecutor
    {
        private readonly DataTableReader _reader = reader;

        public SqlDialect Dialect => SqlDialect.Pgsql;

        public int ExecuteReaderAsyncCallCount { get; private set; }

        public RelationalCommand? CapturedCommand { get; private set; }

        public async Task<TResult> ExecuteReaderAsync<TResult>(
            RelationalCommand command,
            Func<IRelationalCommandReader, CancellationToken, Task<TResult>> readAsync,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            CapturedCommand = command;
            ExecuteReaderAsyncCallCount++;

            await using var relationalReader = new DbRelationalCommandReader(_reader);
            return await readAsync(relationalReader, cancellationToken);
        }
    }

    private sealed class TestRelationalWriteSession(
        NpgsqlTestDbConnection connection,
        TestDbTransaction transaction
    ) : IRelationalWriteSession
    {
        public NpgsqlTestDbConnection Connection { get; } = connection;

        DbConnection IRelationalWriteSession.Connection => Connection;

        public DbTransaction Transaction { get; } = transaction;

        public DbCommand CreateCommand(RelationalCommand command) => throw new NotSupportedException();

        public IRelationalCommandExecutor CommandExecutor => throw new NotSupportedException();

        public Task CommitAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task RollbackAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NpgsqlTestDbConnection(TrackingDbCommand command) : DbConnection
    {
        private ConnectionState _state = ConnectionState.Open;

        public TrackingDbCommand Command { get; } = command;

        public int CreateCommandCallCount { get; private set; }

        [AllowNull]
        public override string ConnectionString { get; set; } = "Host=localhost;Database=test";

        public override string Database => "test";

        public override string DataSource => "npgsql";

        public override string ServerVersion => "1.0";

        public override ConnectionState State => _state;

        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();

        public override void Close() => _state = ConnectionState.Closed;

        public override void Open() => _state = ConnectionState.Open;

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
            new TestDbTransaction(this);

        protected override DbCommand CreateDbCommand()
        {
            CreateCommandCallCount++;
            Command.AttachConnection(this);
            return Command;
        }
    }

    private sealed class TestDbTransaction(DbConnection connection) : DbTransaction
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

        public void AttachConnection(NpgsqlTestDbConnection connection) => DbConnection = connection;

        public override void Cancel() { }

        public override int ExecuteNonQuery() => throw new NotSupportedException();

        public override object? ExecuteScalar() => throw new NotSupportedException();

        public override void Prepare() { }

        protected override DbParameter CreateDbParameter() => new TrackingDbParameter();

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => _reader;

        protected override Task<DbDataReader> ExecuteDbDataReaderAsync(
            CommandBehavior behavior,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_reader);
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
