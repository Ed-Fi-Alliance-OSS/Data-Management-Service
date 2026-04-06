// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_Relational_Write_Current_State_Loader
{
    [Test]
    public async Task It_loads_a_single_existing_document_through_the_session_connection_and_transaction()
    {
        var request = CreateLoadRequest();
        var command = new RecordingDbCommand(
            CreateReader(
                CreateDocumentMetadataTable(
                    (
                        345L,
                        Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"),
                        44L,
                        45L,
                        new DateTimeOffset(2026, 4, 2, 12, 0, 0, TimeSpan.Zero),
                        new DateTimeOffset(2026, 4, 2, 12, 1, 0, TimeSpan.Zero)
                    )
                ),
                CreateRootTableRows((345L, "Lincoln High"))
            )
        );
        var connection = new RecordingDbConnection(command);
        var transaction = new RecordingDbTransaction(connection, IsolationLevel.ReadCommitted);
        var session = new TestRelationalWriteSession(connection, transaction);
        var sut = new RelationalWriteCurrentStateLoader(new HydrationBackedSessionDocumentHydrator());

        var result = (await sut.LoadAsync(request, session))!;

        result.DocumentMetadata.DocumentId.Should().Be(345L);
        result.DocumentMetadata.ContentVersion.Should().Be(44L);
        result.TableRowsInDependencyOrder.Should().ContainSingle();
        result.TableRowsInDependencyOrder[0].TableModel.Should().BeSameAs(request.ReadPlan.Model.Root);
        result.TableRowsInDependencyOrder[0].Rows.Should().ContainSingle();
        ((long)result.TableRowsInDependencyOrder[0].Rows[0][0]!).Should().Be(345L);
        ((string)result.TableRowsInDependencyOrder[0].Rows[0][1]!).Should().Be("Lincoln High");
        command.Transaction.Should().BeSameAs(transaction);
        command.CommandText.Should().Contain("@DocumentId");
        command.Parameters.Should().ContainSingle();
        command.Parameters[0].ParameterName.Should().Be("@DocumentId");
        command.Parameters[0].Value.Should().Be(345L);
        result.ReconstitutedDocument.Should().NotBeNull("the loader should reconstitute the stored document");
        result.ReconstitutedDocument!["name"]!.GetValue<string>().Should().Be("Lincoln High");
    }

    [Test]
    public async Task It_returns_null_for_missing_targets_and_rejects_duplicate_document_metadata_rows()
    {
        var request = CreateLoadRequest();
        var sut = new RelationalWriteCurrentStateLoader(new HydrationBackedSessionDocumentHydrator());

        var missingConnection = new RecordingDbConnection(
            new RecordingDbCommand(CreateReader(CreateDocumentMetadataTable(), CreateRootTableRows()))
        );
        var missingSession = new TestRelationalWriteSession(
            missingConnection,
            new RecordingDbTransaction(missingConnection, IsolationLevel.ReadCommitted)
        );

        var duplicateConnection = new RecordingDbConnection(
            new RecordingDbCommand(
                CreateReader(
                    CreateDocumentMetadataTable(
                        (
                            345L,
                            Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"),
                            44L,
                            45L,
                            new DateTimeOffset(2026, 4, 2, 12, 0, 0, TimeSpan.Zero),
                            new DateTimeOffset(2026, 4, 2, 12, 1, 0, TimeSpan.Zero)
                        ),
                        (
                            345L,
                            Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"),
                            44L,
                            45L,
                            new DateTimeOffset(2026, 4, 2, 12, 0, 0, TimeSpan.Zero),
                            new DateTimeOffset(2026, 4, 2, 12, 1, 0, TimeSpan.Zero)
                        )
                    ),
                    CreateRootTableRows()
                )
            )
        );
        var duplicateSession = new TestRelationalWriteSession(
            duplicateConnection,
            new RecordingDbTransaction(duplicateConnection, IsolationLevel.ReadCommitted)
        );

        var missingResult = await sut.LoadAsync(request, missingSession);
        var duplicateAct = async () => await sut.LoadAsync(request, duplicateSession);

        missingResult.Should().BeNull();
        await duplicateAct
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage(
                "Current-state load for document id 345 returned 2 metadata rows, but exactly 1 was expected."
            );
    }

    private static RelationalWriteCurrentStateLoadRequest CreateLoadRequest()
    {
        var writePlan = CreateRootPlan();
        var resourceModel = CreateRelationalResourceModel(writePlan.TableModel);
        var readPlan = new ResourceReadPlan(
            resourceModel,
            KeysetTableConventions.GetKeysetTableContract(SqlDialect.Pgsql),
            [new TableReadPlan(resourceModel.Root, "select \"DocumentId\", \"Name\" from edfi.\"School\"")],
            [],
            []
        );
        return new RelationalWriteCurrentStateLoadRequest(
            readPlan,
            new RelationalWriteTargetContext.ExistingDocument(
                345L,
                new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb")),
                ObservedContentVersion: 44L
            ),
            SqlDialect.Pgsql
        );
    }

    private static RelationalResourceModel CreateRelationalResourceModel(DbTableModel rootTable)
    {
        var resource = new QualifiedResourceName("Ed-Fi", "School");

        return new RelationalResourceModel(
            Resource: resource,
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootTable,
            TablesInDependencyOrder: [rootTable],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );
    }

    private static TableWritePlan CreateRootPlan()
    {
        var tableModel = new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), "School"),
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_School",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            [
                new DbColumnModel(
                    new DbColumnName("DocumentId"),
                    ColumnKind.ParentKeyPart,
                    null,
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("Name"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 75),
                    false,
                    new JsonPathExpression("$.name", [new JsonPathSegment.Property("name")]),
                    null,
                    new ColumnStorage.Stored()
                ),
            ],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.Root,
                [new DbColumnName("DocumentId")],
                [new DbColumnName("DocumentId")],
                [],
                []
            ),
        };

        return new TableWritePlan(
            tableModel,
            InsertSql: "insert into edfi.\"School\" values (@DocumentId, @Name)",
            UpdateSql: "update edfi.\"School\" set \"Name\" = @Name where \"DocumentId\" = @DocumentId",
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, 2, 1000),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    tableModel.Columns[0],
                    new WriteValueSource.DocumentId(),
                    "DocumentId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[1],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression("$.name", [new JsonPathSegment.Property("name")]),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 75)
                    ),
                    "Name"
                ),
            ],
            KeyUnificationPlans: []
        );
    }

    private static DataTableReader CreateReader(params DataTable[] tables)
    {
        var dataSet = new DataSet();

        foreach (var table in tables)
        {
            dataSet.Tables.Add(table);
        }

        return dataSet.CreateDataReader();
    }

    private static DataTable CreateDocumentMetadataTable(
        params (
            long DocumentId,
            Guid DocumentUuid,
            long ContentVersion,
            long IdentityVersion,
            DateTimeOffset ContentLastModifiedAt,
            DateTimeOffset IdentityLastModifiedAt
        )[] rows
    )
    {
        var table = new DataTable();
        table.Columns.Add("DocumentId", typeof(long));
        table.Columns.Add("DocumentUuid", typeof(Guid));
        table.Columns.Add("ContentVersion", typeof(long));
        table.Columns.Add("IdentityVersion", typeof(long));
        table.Columns.Add("ContentLastModifiedAt", typeof(DateTimeOffset));
        table.Columns.Add("IdentityLastModifiedAt", typeof(DateTimeOffset));

        foreach (var row in rows)
        {
            table.Rows.Add(
                row.DocumentId,
                row.DocumentUuid,
                row.ContentVersion,
                row.IdentityVersion,
                row.ContentLastModifiedAt,
                row.IdentityLastModifiedAt
            );
        }

        return table;
    }

    private static DataTable CreateRootTableRows(params (long DocumentId, string Name)[] rows)
    {
        var table = new DataTable();
        table.Columns.Add("DocumentId", typeof(long));
        table.Columns.Add("Name", typeof(string));

        foreach (var row in rows)
        {
            table.Rows.Add(row.DocumentId, row.Name);
        }

        return table;
    }

    private sealed class TestRelationalWriteSession(
        RecordingDbConnection connection,
        RecordingDbTransaction transaction
    ) : IRelationalWriteSession
    {
        public DbConnection Connection { get; } = connection;

        public DbTransaction Transaction { get; } = transaction;

        public DbCommand CreateCommand(RelationalCommand command) => throw new NotSupportedException();

        public Task CommitAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task RollbackAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class HydrationBackedSessionDocumentHydrator : ISessionDocumentHydrator
    {
        public Task<HydratedPage> HydrateAsync(
            DbConnection connection,
            DbTransaction transaction,
            ResourceReadPlan plan,
            PageKeysetSpec keyset,
            CancellationToken cancellationToken = default
        ) =>
            HydrationExecutor.ExecuteAsync(
                connection,
                plan,
                keyset,
                SqlDialect.Pgsql,
                transaction,
                cancellationToken
            );
    }
}
