// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
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
        table.Columns.Add("ContentVersion", typeof(long));

        foreach (var row in rows)
        {
            table.Rows.Add(row.DocumentId, row.DocumentUuid, row.ContentVersion);
        }

        return table.CreateDataReader();
    }

    private static TestRelationalWriteSession CreateWriteSession(DataTableReader reader)
    {
        var command = new RecordingDbCommand(reader);
        var connection = new RecordingDbConnection(command);
        var transaction = new RecordingDbTransaction(connection, IsolationLevel.ReadCommitted);

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
        RecordingDbConnection connection,
        RecordingDbTransaction transaction
    ) : IRelationalWriteSession
    {
        public RecordingDbConnection Connection { get; } = connection;

        DbConnection IRelationalWriteSession.Connection => Connection;

        public DbTransaction Transaction { get; } = transaction;

        public DbCommand CreateCommand(RelationalCommand command) => throw new NotSupportedException();

        public Task CommitAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task RollbackAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
