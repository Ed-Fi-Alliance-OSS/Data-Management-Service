// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_Default_Relational_Write_Executor
{
    private RecordingRelationalWriteSessionFactory _writeSessionFactory = null!;
    private DefaultRelationalWriteExecutor _sut = null!;

    [SetUp]
    public void Setup()
    {
        _writeSessionFactory = new RecordingRelationalWriteSessionFactory();
        _sut = new DefaultRelationalWriteExecutor(_writeSessionFactory);
    }

    [Test]
    public async Task It_rolls_back_the_attempt_scoped_session_for_post_requests()
    {
        var request = CreateRequest(RelationalWriteOperationKind.Post);

        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Upsert(
                    new UpsertResult.UnknownFailure(
                        "Relational POST write executor is not implemented for resource 'Ed-Fi.School'. "
                            + "Write-plan selection, target-context resolution, reference resolution, and flattening succeeded, but relational command execution is still pending."
                    )
                )
            );
        _writeSessionFactory.CreateAsyncCallCount.Should().Be(1);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
        _writeSessionFactory.Session.DisposeCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_rolls_back_the_attempt_scoped_session_for_put_requests()
    {
        var request = CreateRequest(RelationalWriteOperationKind.Put);

        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Update(
                    new UpdateResult.UnknownFailure(
                        "Relational PUT write executor is not implemented for resource 'Ed-Fi.School'. "
                            + "Write-plan selection, target-context resolution, reference resolution, and flattening succeeded, but relational command execution is still pending."
                    )
                )
            );
        _writeSessionFactory.CreateAsyncCallCount.Should().Be(1);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
        _writeSessionFactory.Session.DisposeCallCount.Should().Be(1);
    }

    private static RelationalWriteExecutorRequest CreateRequest(RelationalWriteOperationKind operationKind)
    {
        var writePlan = CreateRootPlan();
        var resourceModel = CreateRelationalResourceModel(writePlan.TableModel);
        var flatteningInput = new FlatteningInput(
            operationKind,
            new RelationalWriteTargetContext.CreateNew(new DocumentUuid(Guid.NewGuid())),
            new ResourceWritePlan(resourceModel, [writePlan]),
            JsonNode.Parse("""{"name":"Lincoln High"}""")!,
            new ResolvedReferenceSet(
                SuccessfulDocumentReferencesByPath: new Dictionary<JsonPath, ResolvedDocumentReference>(),
                SuccessfulDescriptorReferencesByPath: new Dictionary<JsonPath, ResolvedDescriptorReference>(),
                LookupsByReferentialId: new Dictionary<ReferentialId, ReferenceLookupSnapshot>(),
                InvalidDocumentReferences: [],
                InvalidDescriptorReferences: [],
                DocumentReferenceOccurrences: [],
                DescriptorReferenceOccurrences: []
            )
        );

        return new RelationalWriteExecutorRequest(
            new MappingSet(
                Key: new MappingSetKey("schema-hash", SqlDialect.Pgsql, "v1"),
                Model: new DerivedRelationalModelSet(
                    EffectiveSchema: new EffectiveSchemaInfo(
                        ApiSchemaFormatVersion: "1.0",
                        RelationalMappingVersion: "v1",
                        EffectiveSchemaHash: "schema-hash",
                        ResourceKeyCount: 1,
                        ResourceKeySeedHash: [1, 2, 3],
                        SchemaComponentsInEndpointOrder:
                        [
                            new SchemaComponentInfo("ed-fi", "Ed-Fi", "1.0.0", false, "component-hash"),
                        ],
                        ResourceKeysInIdOrder:
                        [
                            new ResourceKeyEntry(
                                1,
                                new QualifiedResourceName("Ed-Fi", "School"),
                                "1.0.0",
                                false
                            ),
                        ]
                    ),
                    Dialect: SqlDialect.Pgsql,
                    ProjectSchemasInEndpointOrder:
                    [
                        new ProjectSchemaInfo("ed-fi", "Ed-Fi", "1.0.0", false, new DbSchemaName("edfi")),
                    ],
                    ConcreteResourcesInNameOrder:
                    [
                        new ConcreteResourceModel(
                            new ResourceKeyEntry(
                                1,
                                new QualifiedResourceName("Ed-Fi", "School"),
                                "1.0.0",
                                false
                            ),
                            ResourceStorageKind.RelationalTables,
                            resourceModel
                        ),
                    ],
                    AbstractIdentityTablesInNameOrder: [],
                    AbstractUnionViewsInNameOrder: [],
                    IndexesInCreateOrder: [],
                    TriggersInCreateOrder: []
                ),
                WritePlansByResource: new Dictionary<QualifiedResourceName, ResourceWritePlan>(),
                ReadPlansByResource: new Dictionary<QualifiedResourceName, ResourceReadPlan>(),
                ResourceKeyIdByResource: new Dictionary<QualifiedResourceName, short>(),
                ResourceKeyById: new Dictionary<short, ResourceKeyEntry>(),
                SecurableElementColumnPathsByResource: new Dictionary<
                    QualifiedResourceName,
                    IReadOnlyList<ResolvedSecurableElementPath>
                >()
            ),
            operationKind,
            flatteningInput.TargetContext,
            flatteningInput.WritePlan,
            null,
            flatteningInput.SelectedBody,
            new TraceId("write-executor-test"),
            new RelationalWritePreparedData(
                flatteningInput,
                new FlattenedWriteSet(
                    new RootWriteRowBuffer(
                        writePlan,
                        [
                            FlattenedWriteValue.UnresolvedRootDocumentId.Instance,
                            new FlattenedWriteValue.Literal("Lincoln High"),
                        ]
                    )
                )
            )
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
                    new JsonPathExpression("$.name", []),
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
                        new JsonPathExpression("$.name", []),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 75)
                    ),
                    "Name"
                ),
            ],
            KeyUnificationPlans: []
        );
    }

    private sealed class RecordingRelationalWriteSessionFactory : IRelationalWriteSessionFactory
    {
        public RecordingRelationalWriteSession Session { get; } = new();

        public int CreateAsyncCallCount { get; private set; }

        public Task<IRelationalWriteSession> CreateAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreateAsyncCallCount++;
            return Task.FromResult<IRelationalWriteSession>(Session);
        }
    }

    private sealed class RecordingRelationalWriteSession : IRelationalWriteSession
    {
        public DbConnection Connection => throw new NotSupportedException();

        public DbTransaction Transaction => throw new NotSupportedException();

        public int CommitCallCount { get; private set; }

        public int RollbackCallCount { get; private set; }

        public int DisposeCallCount { get; private set; }

        public DbCommand CreateCommand(RelationalCommand command) => throw new NotSupportedException();

        public Task CommitAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CommitCallCount++;
            return Task.CompletedTask;
        }

        public Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RollbackCallCount++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCallCount++;
            return ValueTask.CompletedTask;
        }
    }
}
