// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
public class Given_Relational_Write_Freshness_Checker
{
    private RecordingDbCommand _command = null!;
    private RecordingDbConnection _connection = null!;
    private RelationalWriteSession _writeSession = null!;
    private RelationalWriteFreshnessChecker _sut = null!;
    private RelationalWriteTargetContext.ExistingDocument _targetContext = null!;

    [SetUp]
    public void Setup()
    {
        _command = new RecordingDbCommand(new DataTable().CreateDataReader()) { ScalarResult = 44L };
        _connection = new RecordingDbConnection(_command);
        _writeSession = new RelationalWriteSession(
            _connection,
            new RecordingDbTransaction(_connection, IsolationLevel.ReadCommitted)
        );
        _sut = new RelationalWriteFreshnessChecker();
        _targetContext = new RelationalWriteTargetContext.ExistingDocument(
            345L,
            new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb")),
            44L
        );
    }

    [Test]
    public async Task It_uses_a_postgresql_row_lock_when_rechecking_guarded_no_op_freshness()
    {
        var isCurrent = await _sut.IsCurrentAsync(
            CreateRequest(SqlDialect.Pgsql),
            _targetContext,
            _writeSession
        );

        isCurrent.Should().BeTrue();
        _command.ExecuteScalarCallCount.Should().Be(1);
        _command.CommandText.Should().Contain("FOR UPDATE");
        _command.CommandText.Should().Contain("WHERE document.\"DocumentId\" = @documentId");
        _command.Parameters.Should().ContainSingle();
        _command.Parameters[0].ParameterName.Should().Be("@documentId");
        _command.Parameters[0].Value.Should().Be(345L);
    }

    [Test]
    public async Task It_uses_a_sql_server_update_lock_when_rechecking_guarded_no_op_freshness()
    {
        var isCurrent = await _sut.IsCurrentAsync(
            CreateRequest(SqlDialect.Mssql),
            _targetContext,
            _writeSession
        );

        isCurrent.Should().BeTrue();
        _command.ExecuteScalarCallCount.Should().Be(1);
        _command.CommandText.Should().Contain("WITH (UPDLOCK, HOLDLOCK, ROWLOCK)");
        _command.CommandText.Should().Contain("WHERE document.[DocumentId] = @documentId");
        _command.Parameters.Should().ContainSingle();
        _command.Parameters[0].ParameterName.Should().Be("@documentId");
        _command.Parameters[0].Value.Should().Be(345L);
    }

    private static RelationalWriteExecutorRequest CreateRequest(SqlDialect dialect)
    {
        var writePlan = CreateRootPlan();
        var resourceModel = CreateRelationalResourceModel(writePlan.TableModel);
        var resourceWritePlan = new ResourceWritePlan(resourceModel, [writePlan]);
        var mappingSet = CreateMappingSet(resourceModel, dialect);

        return new RelationalWriteExecutorRequest(
            mappingSet,
            RelationalWriteOperationKind.Put,
            new RelationalWriteTargetRequest.Put(
                new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"))
            ),
            resourceWritePlan,
            CreateReadPlan(resourceModel, dialect),
            JsonNode.Parse("""{"name":"Lincoln High"}""")!,
            false,
            new TraceId("guarded-no-op-freshness-test"),
            new ReferenceResolverRequest(mappingSet, resourceWritePlan.Model.Resource, [], []),
            targetContext: new RelationalWriteTargetContext.ExistingDocument(
                345L,
                new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb")),
                44L
            )
        );
    }

    private static MappingSet CreateMappingSet(RelationalResourceModel resourceModel, SqlDialect dialect)
    {
        var resource = resourceModel.Resource;
        var resourceKey = new ResourceKeyEntry(1, resource, "1.0.0", false);

        return new MappingSet(
            Key: new MappingSetKey("schema-hash", dialect, "v1"),
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
                    ResourceKeysInIdOrder: [resourceKey]
                ),
                Dialect: dialect,
                ProjectSchemasInEndpointOrder:
                [
                    new ProjectSchemaInfo("ed-fi", "Ed-Fi", "1.0.0", false, new DbSchemaName("edfi")),
                ],
                ConcreteResourcesInNameOrder:
                [
                    new ConcreteResourceModel(
                        resourceKey,
                        ResourceStorageKind.RelationalTables,
                        resourceModel
                    ),
                ],
                AbstractIdentityTablesInNameOrder: [],
                AbstractUnionViewsInNameOrder: [],
                IndexesInCreateOrder: [],
                TriggersInCreateOrder:
                [
                    new DbTriggerInfo(
                        new DbTriggerName("TR_School_DocumentStamping"),
                        resourceModel.Root.Table,
                        [new DbColumnName("DocumentId")],
                        [new DbColumnName("SchoolId")],
                        new TriggerKindParameters.DocumentStamping()
                    ),
                ]
            ),
            WritePlansByResource: new Dictionary<QualifiedResourceName, ResourceWritePlan>
            {
                [resource] = new ResourceWritePlan(resourceModel, [CreateRootPlan()]),
            },
            ReadPlansByResource: new Dictionary<QualifiedResourceName, ResourceReadPlan>(),
            ResourceKeyIdByResource: new Dictionary<QualifiedResourceName, short>
            {
                [resource] = resourceKey.ResourceKeyId,
            },
            ResourceKeyById: new Dictionary<short, ResourceKeyEntry>
            {
                [resourceKey.ResourceKeyId] = resourceKey,
            },
            SecurableElementColumnPathsByResource: new Dictionary<
                QualifiedResourceName,
                IReadOnlyList<ResolvedSecurableElementPath>
            >()
        );
    }

    private static ResourceReadPlan CreateReadPlan(RelationalResourceModel resourceModel, SqlDialect dialect)
    {
        return new ResourceReadPlan(
            resourceModel,
            KeysetTableConventions.GetKeysetTableContract(dialect),
            [
                new TableReadPlan(
                    resourceModel.Root,
                    """
                    select "DocumentId", "SchoolId", "Name" from edfi."School"
                    """
                ),
            ],
            [],
            []
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
                    new DbColumnName("SchoolId"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32),
                    false,
                    new JsonPathExpression("$.schoolId", []),
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
            InsertSql: """insert into edfi."School" values (@DocumentId, @SchoolId, @Name)""",
            UpdateSql: """update edfi."School" set "SchoolId" = @SchoolId, "Name" = @Name where "DocumentId" = @DocumentId""",
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, 3, 1000),
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
                        new JsonPathExpression("$.schoolId", []),
                        new RelationalScalarType(ScalarKind.Int32)
                    ),
                    "SchoolId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[2],
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
}
