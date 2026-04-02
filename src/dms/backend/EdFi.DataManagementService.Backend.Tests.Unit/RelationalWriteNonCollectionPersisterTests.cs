// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_Relational_Write_Non_Collection_Persister
{
    private RelationalWriteNonCollectionPersister _sut = null!;

    [SetUp]
    public void Setup()
    {
        _sut = new RelationalWriteNonCollectionPersister();
    }

    [Test]
    public async Task It_inserts_document_root_and_root_extension_rows_for_create_requests()
    {
        var rootPlan = CreateRootPlan();
        var rootExtensionPlan = CreateRootExtensionPlan();
        var writePlan = CreateWritePlan([rootPlan, rootExtensionPlan]);
        var request = CreateRequest(writePlan, RelationalWriteOperationKind.Post);
        var mergeResult = new RelationalWriteNoProfileMergeResult([
            new RelationalWriteNoProfileTableState(
                rootPlan,
                [],
                [CreateRow(FlattenedWriteValue.UnresolvedRootDocumentId.Instance, 255901, "Lincoln High")]
            ),
            new RelationalWriteNoProfileTableState(
                rootExtensionPlan,
                [],
                [CreateRow(FlattenedWriteValue.UnresolvedRootDocumentId.Instance, "BLUE")]
            ),
        ]);
        var writeSession = new RecordingRelationalWriteSession([
            new CommandResponse(ScalarResult: 910L),
            new CommandResponse(),
            new CommandResponse(),
        ]);

        var persisted = await _sut.TryPersistAsync(request, mergeResult, writeSession);

        persisted.Should().BeTrue();
        writeSession.Commands.Should().HaveCount(3);
        writeSession.Commands[0].CommandText.Should().Contain("INSERT INTO dms.\"Document\"");
        GetParameterValue(writeSession.Commands[0], "@documentUuid")
            .Should()
            .Be(Guid.Parse("cccccccc-1111-2222-3333-dddddddddddd"));
        GetParameterValue(writeSession.Commands[0], "@resourceKeyId").Should().Be((short)1);

        writeSession.Commands[1].CommandText.Should().Be(rootPlan.InsertSql);
        GetParameterValue(writeSession.Commands[1], "@DocumentId").Should().Be(910L);
        GetParameterValue(writeSession.Commands[1], "@SchoolId").Should().Be(255901);
        GetParameterValue(writeSession.Commands[1], "@Name").Should().Be("Lincoln High");

        writeSession.Commands[2].CommandText.Should().Be(rootExtensionPlan.InsertSql);
        GetParameterValue(writeSession.Commands[2], "@DocumentId").Should().Be(910L);
        GetParameterValue(writeSession.Commands[2], "@ExtensionCode").Should().Be("BLUE");
    }

    [Test]
    public async Task It_updates_matched_rows_and_clears_inlined_scope_columns_for_existing_document_requests()
    {
        var rootPlan = CreateRootPlan(includeShortName: true);
        var writePlan = CreateWritePlan([rootPlan]);
        var request = CreateRequest(writePlan, RelationalWriteOperationKind.Put);
        var mergeResult = new RelationalWriteNoProfileMergeResult([
            new RelationalWriteNoProfileTableState(
                rootPlan,
                [CreateRow(345L, 255901, "Lincoln High", "LHS")],
                [CreateRow(345L, 255901, "Lincoln High Updated", null)]
            ),
        ]);
        var writeSession = new RecordingRelationalWriteSession([new CommandResponse()]);

        var persisted = await _sut.TryPersistAsync(request, mergeResult, writeSession);

        persisted.Should().BeTrue();
        writeSession.Commands.Should().ContainSingle();
        writeSession.Commands[0].CommandText.Should().Be(rootPlan.UpdateSql);
        GetParameterValue(writeSession.Commands[0], "@DocumentId").Should().Be(345L);
        GetParameterValue(writeSession.Commands[0], "@SchoolId").Should().Be(255901);
        GetParameterValue(writeSession.Commands[0], "@Name").Should().Be("Lincoln High Updated");
        GetParameterValue(writeSession.Commands[0], "@ShortName").Should().BeNull();
    }

    [Test]
    public async Task It_deletes_omitted_separate_table_rows_when_scope_becomes_absent()
    {
        var rootPlan = CreateRootPlan();
        var rootExtensionPlan = CreateRootExtensionPlan();
        var writePlan = CreateWritePlan([rootPlan, rootExtensionPlan]);
        var request = CreateRequest(writePlan, RelationalWriteOperationKind.Put);
        var mergeResult = new RelationalWriteNoProfileMergeResult([
            new RelationalWriteNoProfileTableState(
                rootPlan,
                [CreateRow(345L, 255901, "Lincoln High")],
                [CreateRow(345L, 255901, "Lincoln High")]
            ),
            new RelationalWriteNoProfileTableState(rootExtensionPlan, [CreateRow(345L, "BLUE")], []),
        ]);
        var writeSession = new RecordingRelationalWriteSession([new CommandResponse()]);

        var persisted = await _sut.TryPersistAsync(request, mergeResult, writeSession);

        persisted.Should().BeTrue();
        writeSession.Commands.Should().ContainSingle();
        writeSession.Commands[0].CommandText.Should().Be(rootExtensionPlan.DeleteByParentSql);
        GetParameterValue(writeSession.Commands[0], "@DocumentId").Should().Be(345L);
        GetParameterValue(writeSession.Commands[0], "@ExtensionCode").Should().Be("BLUE");
    }

    [Test]
    public async Task It_updates_collection_aligned_one_to_one_extension_scopes_when_base_collection_rows_are_unchanged()
    {
        var rootPlan = CreateRootPlan();
        var collectionPlan = CreateCollectionPlan();
        var collectionExtensionScopePlan = CreateCollectionExtensionScopePlan();
        var writePlan = CreateWritePlan([rootPlan, collectionPlan, collectionExtensionScopePlan]);
        var request = CreateRequest(writePlan, RelationalWriteOperationKind.Put);
        var mergeResult = new RelationalWriteNoProfileMergeResult([
            new RelationalWriteNoProfileTableState(
                rootPlan,
                [CreateRow(345L, 255901, "Lincoln High")],
                [CreateRow(345L, 255901, "Lincoln High")]
            ),
            new RelationalWriteNoProfileTableState(
                collectionPlan,
                [CreateRow(44L, 345L, 0, "Mailing")],
                [CreateRow(44L, 345L, 0, "Mailing")]
            ),
            new RelationalWriteNoProfileTableState(
                collectionExtensionScopePlan,
                [CreateRow(44L, "Blue")],
                [CreateRow(44L, "Red")]
            ),
        ]);
        var writeSession = new RecordingRelationalWriteSession([new CommandResponse()]);

        var persisted = await _sut.TryPersistAsync(request, mergeResult, writeSession);

        persisted.Should().BeTrue();
        writeSession.Commands.Should().ContainSingle();
        writeSession.Commands[0].CommandText.Should().Be(collectionExtensionScopePlan.UpdateSql);
        GetParameterValue(writeSession.Commands[0], "@BaseCollectionItemId").Should().Be(44L);
        GetParameterValue(writeSession.Commands[0], "@FavoriteColor").Should().Be("Red");
    }

    [Test]
    public async Task It_deletes_updates_and_inserts_base_collection_rows_using_stable_row_identity()
    {
        var rootPlan = CreateRootPlan();
        var collectionPlan = CreateCollectionPlan();
        var writePlan = CreateWritePlan([rootPlan, collectionPlan]);
        var request = CreateRequest(writePlan, RelationalWriteOperationKind.Put);
        var mergeResult = new RelationalWriteNoProfileMergeResult([
            new RelationalWriteNoProfileTableState(
                rootPlan,
                [CreateRow(345L, 255901, "Lincoln High")],
                [CreateRow(345L, 255901, "Lincoln High")]
            ),
            new RelationalWriteNoProfileTableState(
                collectionPlan,
                [CreateRow(44L, 345L, 0, "Mailing"), CreateRow(45L, 345L, 1, "Home")],
                [CreateRow(45L, 345L, 0, "Home"), CreateRow(NewCollectionItemId(), 345L, 1, "Physical")]
            ),
        ]);
        var writeSession = new RecordingRelationalWriteSession([
            new CommandResponse(),
            new CommandResponse(),
            new CommandResponse(ScalarResult: 91L),
            new CommandResponse(),
        ]);

        var persisted = await _sut.TryPersistAsync(request, mergeResult, writeSession);

        persisted.Should().BeTrue();
        writeSession.Commands.Should().HaveCount(4);

        writeSession
            .Commands[0]
            .CommandText.Should()
            .Be(collectionPlan.CollectionMergePlan!.DeleteByStableRowIdentitySql);
        GetParameterValue(writeSession.Commands[0], "@CollectionItemId").Should().Be(44L);

        writeSession
            .Commands[1]
            .CommandText.Should()
            .Be(collectionPlan.CollectionMergePlan!.UpdateByStableRowIdentitySql);
        GetParameterValue(writeSession.Commands[1], "@CollectionItemId").Should().Be(45L);
        GetParameterValue(writeSession.Commands[1], "@School_DocumentId").Should().Be(345L);
        GetParameterValue(writeSession.Commands[1], "@Ordinal").Should().Be(0);
        GetParameterValue(writeSession.Commands[1], "@AddressType").Should().Be("Home");

        writeSession.Commands[2].CommandText.Should().Contain("CollectionItemIdSequence");
        writeSession.Commands[3].CommandText.Should().Be(collectionPlan.InsertSql);
        GetParameterValue(writeSession.Commands[3], "@CollectionItemId").Should().Be(91L);
        GetParameterValue(writeSession.Commands[3], "@School_DocumentId").Should().Be(345L);
        GetParameterValue(writeSession.Commands[3], "@Ordinal").Should().Be(1);
        GetParameterValue(writeSession.Commands[3], "@AddressType").Should().Be("Physical");
    }

    [Test]
    public async Task It_reserves_collection_ids_for_nested_base_collection_inserts()
    {
        var rootPlan = CreateRootPlan();
        var addressPlan = CreateCollectionPlan();
        var periodPlan = CreatePeriodPlan();
        var writePlan = CreateWritePlan([rootPlan, addressPlan, periodPlan]);
        var request = CreateRequest(writePlan, RelationalWriteOperationKind.Put);
        var addressCollectionItemId = NewCollectionItemId();
        var periodCollectionItemId = NewCollectionItemId();
        var mergeResult = new RelationalWriteNoProfileMergeResult([
            new RelationalWriteNoProfileTableState(
                rootPlan,
                [CreateRow(345L, 255901, "Lincoln High")],
                [CreateRow(345L, 255901, "Lincoln High")]
            ),
            new RelationalWriteNoProfileTableState(
                addressPlan,
                [],
                [CreateRow(addressCollectionItemId, 345L, 0, "Home")]
            ),
            new RelationalWriteNoProfileTableState(
                periodPlan,
                [],
                [CreateRow(periodCollectionItemId, 345L, addressCollectionItemId, 0, "2026-09-01")]
            ),
        ]);
        var writeSession = new RecordingRelationalWriteSession([
            new CommandResponse(ScalarResult: 910L),
            new CommandResponse(),
            new CommandResponse(ScalarResult: 911L),
            new CommandResponse(),
        ]);

        var persisted = await _sut.TryPersistAsync(request, mergeResult, writeSession);

        persisted.Should().BeTrue();
        writeSession.Commands.Should().HaveCount(4);
        writeSession.Commands[0].CommandText.Should().Contain("CollectionItemIdSequence");
        writeSession.Commands[1].CommandText.Should().Be(addressPlan.InsertSql);
        GetParameterValue(writeSession.Commands[1], "@CollectionItemId").Should().Be(910L);
        GetParameterValue(writeSession.Commands[1], "@School_DocumentId").Should().Be(345L);
        GetParameterValue(writeSession.Commands[1], "@Ordinal").Should().Be(0);
        GetParameterValue(writeSession.Commands[1], "@AddressType").Should().Be("Home");

        writeSession.Commands[2].CommandText.Should().Contain("CollectionItemIdSequence");
        writeSession.Commands[3].CommandText.Should().Be(periodPlan.InsertSql);
        GetParameterValue(writeSession.Commands[3], "@CollectionItemId").Should().Be(911L);
        GetParameterValue(writeSession.Commands[3], "@School_DocumentId").Should().Be(345L);
        GetParameterValue(writeSession.Commands[3], "@ParentCollectionItemId").Should().Be(910L);
        GetParameterValue(writeSession.Commands[3], "@Ordinal").Should().Be(0);
        GetParameterValue(writeSession.Commands[3], "@BeginDate").Should().Be("2026-09-01");
    }

    [Test]
    public async Task It_deletes_updates_and_inserts_root_extension_collection_rows_using_stable_row_identity()
    {
        var rootPlan = CreateRootPlan();
        var extensionCollectionPlan = CreateExtensionCollectionPlan();
        var writePlan = CreateWritePlan([rootPlan, extensionCollectionPlan]);
        var request = CreateRequest(writePlan, RelationalWriteOperationKind.Put);
        var mergeResult = new RelationalWriteNoProfileMergeResult([
            new RelationalWriteNoProfileTableState(
                rootPlan,
                [CreateRow(345L, 255901, "Lincoln High")],
                [CreateRow(345L, 255901, "Lincoln High")]
            ),
            new RelationalWriteNoProfileTableState(
                extensionCollectionPlan,
                [CreateRow(44L, 345L, 0, "Tutor"), CreateRow(45L, 345L, 1, "Mentor")],
                [
                    CreateRow(45L, 345L, 0, "Mentor Updated"),
                    CreateRow(NewCollectionItemId(), 345L, 1, "Coach"),
                ]
            ),
        ]);
        var writeSession = new RecordingRelationalWriteSession([
            new CommandResponse(),
            new CommandResponse(),
            new CommandResponse(ScalarResult: 91L),
            new CommandResponse(),
        ]);

        var persisted = await _sut.TryPersistAsync(request, mergeResult, writeSession);

        persisted.Should().BeTrue();
        writeSession.Commands.Should().HaveCount(4);

        writeSession
            .Commands[0]
            .CommandText.Should()
            .Be(extensionCollectionPlan.CollectionMergePlan!.DeleteByStableRowIdentitySql);
        GetParameterValue(writeSession.Commands[0], "@CollectionItemId").Should().Be(44L);

        writeSession
            .Commands[1]
            .CommandText.Should()
            .Be(extensionCollectionPlan.CollectionMergePlan!.UpdateByStableRowIdentitySql);
        GetParameterValue(writeSession.Commands[1], "@CollectionItemId").Should().Be(45L);
        GetParameterValue(writeSession.Commands[1], "@School_DocumentId").Should().Be(345L);
        GetParameterValue(writeSession.Commands[1], "@Ordinal").Should().Be(0);
        GetParameterValue(writeSession.Commands[1], "@InterventionCode").Should().Be("Mentor Updated");

        writeSession.Commands[2].CommandText.Should().Contain("CollectionItemIdSequence");
        writeSession.Commands[3].CommandText.Should().Be(extensionCollectionPlan.InsertSql);
        GetParameterValue(writeSession.Commands[3], "@CollectionItemId").Should().Be(91L);
        GetParameterValue(writeSession.Commands[3], "@School_DocumentId").Should().Be(345L);
        GetParameterValue(writeSession.Commands[3], "@Ordinal").Should().Be(1);
        GetParameterValue(writeSession.Commands[3], "@InterventionCode").Should().Be("Coach");
    }

    [Test]
    public async Task It_deletes_updates_and_inserts_collection_aligned_extension_child_rows_using_base_row_identity()
    {
        var rootPlan = CreateRootPlan();
        var addressPlan = CreateCollectionPlan();
        var collectionAlignedExtensionChildPlan = CreateCollectionAlignedExtensionChildCollectionPlan();
        var writePlan = CreateWritePlan([rootPlan, addressPlan, collectionAlignedExtensionChildPlan]);
        var request = CreateRequest(writePlan, RelationalWriteOperationKind.Put);
        var mergeResult = new RelationalWriteNoProfileMergeResult([
            new RelationalWriteNoProfileTableState(
                rootPlan,
                [CreateRow(345L, 255901, "Lincoln High")],
                [CreateRow(345L, 255901, "Lincoln High")]
            ),
            new RelationalWriteNoProfileTableState(
                addressPlan,
                [CreateRow(44L, 345L, 0, "Home")],
                [CreateRow(44L, 345L, 0, "Home")]
            ),
            new RelationalWriteNoProfileTableState(
                collectionAlignedExtensionChildPlan,
                [CreateRow(500L, 345L, 44L, 0, "Bus"), CreateRow(501L, 345L, 44L, 1, "Meal")],
                [
                    CreateRow(501L, 345L, 44L, 0, "Meal Updated"),
                    CreateRow(NewCollectionItemId(), 345L, 44L, 1, "Tutor"),
                ]
            ),
        ]);
        var writeSession = new RecordingRelationalWriteSession([
            new CommandResponse(),
            new CommandResponse(),
            new CommandResponse(ScalarResult: 91L),
            new CommandResponse(),
        ]);

        var persisted = await _sut.TryPersistAsync(request, mergeResult, writeSession);

        persisted.Should().BeTrue();
        writeSession.Commands.Should().HaveCount(4);

        writeSession
            .Commands[0]
            .CommandText.Should()
            .Be(collectionAlignedExtensionChildPlan.CollectionMergePlan!.DeleteByStableRowIdentitySql);
        GetParameterValue(writeSession.Commands[0], "@CollectionItemId").Should().Be(500L);

        writeSession
            .Commands[1]
            .CommandText.Should()
            .Be(collectionAlignedExtensionChildPlan.CollectionMergePlan!.UpdateByStableRowIdentitySql);
        GetParameterValue(writeSession.Commands[1], "@CollectionItemId").Should().Be(501L);
        GetParameterValue(writeSession.Commands[1], "@School_DocumentId").Should().Be(345L);
        GetParameterValue(writeSession.Commands[1], "@BaseCollectionItemId").Should().Be(44L);
        GetParameterValue(writeSession.Commands[1], "@Ordinal").Should().Be(0);
        GetParameterValue(writeSession.Commands[1], "@ServiceName").Should().Be("Meal Updated");

        writeSession.Commands[2].CommandText.Should().Contain("CollectionItemIdSequence");
        writeSession.Commands[3].CommandText.Should().Be(collectionAlignedExtensionChildPlan.InsertSql);
        GetParameterValue(writeSession.Commands[3], "@CollectionItemId").Should().Be(91L);
        GetParameterValue(writeSession.Commands[3], "@School_DocumentId").Should().Be(345L);
        GetParameterValue(writeSession.Commands[3], "@BaseCollectionItemId").Should().Be(44L);
        GetParameterValue(writeSession.Commands[3], "@Ordinal").Should().Be(1);
        GetParameterValue(writeSession.Commands[3], "@ServiceName").Should().Be("Tutor");
    }

    [Test]
    public async Task It_resolves_new_parent_collection_ids_for_collection_aligned_extension_child_inserts()
    {
        var rootPlan = CreateRootPlan();
        var addressPlan = CreateCollectionPlan();
        var collectionAlignedExtensionChildPlan = CreateCollectionAlignedExtensionChildCollectionPlan();
        var writePlan = CreateWritePlan([rootPlan, addressPlan, collectionAlignedExtensionChildPlan]);
        var request = CreateRequest(writePlan, RelationalWriteOperationKind.Put);
        var addressCollectionItemId = NewCollectionItemId();
        var serviceCollectionItemId = NewCollectionItemId();
        var mergeResult = new RelationalWriteNoProfileMergeResult([
            new RelationalWriteNoProfileTableState(
                rootPlan,
                [CreateRow(345L, 255901, "Lincoln High")],
                [CreateRow(345L, 255901, "Lincoln High")]
            ),
            new RelationalWriteNoProfileTableState(
                addressPlan,
                [],
                [CreateRow(addressCollectionItemId, 345L, 0, "Home")]
            ),
            new RelationalWriteNoProfileTableState(
                collectionAlignedExtensionChildPlan,
                [],
                [CreateRow(serviceCollectionItemId, 345L, addressCollectionItemId, 0, "Bus")]
            ),
        ]);
        var writeSession = new RecordingRelationalWriteSession([
            new CommandResponse(ScalarResult: 910L),
            new CommandResponse(),
            new CommandResponse(ScalarResult: 911L),
            new CommandResponse(),
        ]);

        var persisted = await _sut.TryPersistAsync(request, mergeResult, writeSession);

        persisted.Should().BeTrue();
        writeSession.Commands.Should().HaveCount(4);
        writeSession.Commands[0].CommandText.Should().Contain("CollectionItemIdSequence");
        writeSession.Commands[1].CommandText.Should().Be(addressPlan.InsertSql);
        GetParameterValue(writeSession.Commands[1], "@CollectionItemId").Should().Be(910L);
        GetParameterValue(writeSession.Commands[1], "@School_DocumentId").Should().Be(345L);
        GetParameterValue(writeSession.Commands[1], "@Ordinal").Should().Be(0);
        GetParameterValue(writeSession.Commands[1], "@AddressType").Should().Be("Home");

        writeSession.Commands[2].CommandText.Should().Contain("CollectionItemIdSequence");
        writeSession.Commands[3].CommandText.Should().Be(collectionAlignedExtensionChildPlan.InsertSql);
        GetParameterValue(writeSession.Commands[3], "@CollectionItemId").Should().Be(911L);
        GetParameterValue(writeSession.Commands[3], "@School_DocumentId").Should().Be(345L);
        GetParameterValue(writeSession.Commands[3], "@BaseCollectionItemId").Should().Be(910L);
        GetParameterValue(writeSession.Commands[3], "@Ordinal").Should().Be(0);
        GetParameterValue(writeSession.Commands[3], "@ServiceName").Should().Be("Bus");
    }

    private static object? GetParameterValue(RelationalCommand command, string parameterName)
    {
        return command.Parameters.Single(parameter => parameter.Name == parameterName).Value;
    }

    private static RelationalWriteExecutorRequest CreateRequest(
        ResourceWritePlan writePlan,
        RelationalWriteOperationKind operationKind
    )
    {
        var mappingSet = CreateMappingSet(writePlan.Model);

        return new RelationalWriteExecutorRequest(
            mappingSet,
            operationKind,
            operationKind == RelationalWriteOperationKind.Put
                ? new RelationalWriteTargetContext.ExistingDocument(
                    345L,
                    new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"))
                )
                : new RelationalWriteTargetContext.CreateNew(
                    new DocumentUuid(Guid.Parse("cccccccc-1111-2222-3333-dddddddddddd"))
                ),
            writePlan,
            operationKind == RelationalWriteOperationKind.Put ? CreateReadPlan(writePlan.Model) : null,
            JsonNode.Parse("""{"schoolId":255901,"name":"Lincoln High"}""")!,
            false,
            new TraceId("non-collection-persister-test"),
            new ReferenceResolverRequest(mappingSet, writePlan.Model.Resource, [], [])
        );
    }

    private static MappingSet CreateMappingSet(RelationalResourceModel resourceModel)
    {
        var resource = resourceModel.Resource;
        var resourceKey = new ResourceKeyEntry(1, resource, "1.0.0", false);

        return new MappingSet(
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
                    ResourceKeysInIdOrder: [resourceKey]
                ),
                Dialect: SqlDialect.Pgsql,
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
                TriggersInCreateOrder: []
            ),
            WritePlansByResource: new Dictionary<QualifiedResourceName, ResourceWritePlan>
            {
                [resource] = CreateWritePlan(
                    resourceModel.TablesInDependencyOrder.Select(CreatePlanForModel).ToArray()
                ),
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

    private static ResourceReadPlan CreateReadPlan(RelationalResourceModel resourceModel)
    {
        return new ResourceReadPlan(
            resourceModel,
            KeysetTableConventions.GetKeysetTableContract(SqlDialect.Pgsql),
            resourceModel
                .TablesInDependencyOrder.Select(tableModel => new TableReadPlan(
                    tableModel,
                    $"select * from {tableModel.Table.Schema.Value}.\"{tableModel.Table.Name}\""
                ))
                .ToArray(),
            [],
            []
        );
    }

    private static ResourceWritePlan CreateWritePlan(IReadOnlyList<TableWritePlan> tablePlans)
    {
        var rootTable = tablePlans[0].TableModel;
        var resourceModel = new RelationalResourceModel(
            Resource: new QualifiedResourceName("Ed-Fi", "School"),
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootTable,
            TablesInDependencyOrder: tablePlans.Select(static plan => plan.TableModel).ToArray(),
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );

        return new ResourceWritePlan(resourceModel, tablePlans);
    }

    private static TableWritePlan CreatePlanForModel(DbTableModel tableModel)
    {
        return tableModel.Table.Name switch
        {
            "School" => CreateRootPlan(
                includeShortName: tableModel.Columns.Any(column => column.ColumnName.Value == "ShortName")
            ),
            "SchoolExtension" => CreateRootExtensionPlan(),
            "SchoolAddress" => CreateCollectionPlan(),
            "SchoolAddressPeriod" => CreatePeriodPlan(),
            "SchoolExtensionIntervention" => CreateExtensionCollectionPlan(),
            "SchoolExtensionAddress" => CreateCollectionExtensionScopePlan(),
            "SchoolExtensionAddressService" => CreateCollectionAlignedExtensionChildCollectionPlan(),
            _ => throw new InvalidOperationException($"Unsupported table '{tableModel.Table.Name}'."),
        };
    }

    private static TableWritePlan CreateRootPlan(bool includeShortName = false)
    {
        List<DbColumnModel> columns =
        [
            CreateColumn("DocumentId", ColumnKind.ParentKeyPart),
            CreateColumn("SchoolId", ColumnKind.Scalar),
            CreateColumn("Name", ColumnKind.Scalar),
        ];

        if (includeShortName)
        {
            columns.Add(CreateColumn("ShortName", ColumnKind.Scalar, isNullable: true));
        }

        var tableModel = new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), "School"),
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_School",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            columns,
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

        var bindings = new List<WriteColumnBinding>
        {
            new(tableModel.Columns[0], new WriteValueSource.DocumentId(), "DocumentId"),
            new(
                tableModel.Columns[1],
                new WriteValueSource.Scalar(
                    new JsonPathExpression("$.schoolId", []),
                    new RelationalScalarType(ScalarKind.Int32)
                ),
                "SchoolId"
            ),
            new(
                tableModel.Columns[2],
                new WriteValueSource.Scalar(
                    new JsonPathExpression("$.name", []),
                    new RelationalScalarType(ScalarKind.String, MaxLength: 75)
                ),
                "Name"
            ),
        };

        if (includeShortName)
        {
            bindings.Add(
                new WriteColumnBinding(
                    tableModel.Columns[3],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression("$.shortName", []),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 30)
                    ),
                    "ShortName"
                )
            );
        }

        return new TableWritePlan(
            tableModel,
            InsertSql: includeShortName
                ? """
                insert into edfi."School" values (@DocumentId, @SchoolId, @Name, @ShortName)
                """
                : """
                insert into edfi."School" values (@DocumentId, @SchoolId, @Name)
                """,
            UpdateSql: includeShortName
                ? """
                update edfi."School" set "SchoolId" = @SchoolId, "Name" = @Name, "ShortName" = @ShortName where "DocumentId" = @DocumentId
                """
                : """
                update edfi."School" set "SchoolId" = @SchoolId, "Name" = @Name where "DocumentId" = @DocumentId
                """,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, bindings.Count, 1000),
            ColumnBindings: bindings,
            KeyUnificationPlans: []
        );
    }

    private static TableWritePlan CreateRootExtensionPlan()
    {
        var tableModel = new DbTableModel(
            new DbTableName(new DbSchemaName("sample"), "SchoolExtension"),
            new JsonPathExpression("$._ext.sample", []),
            new TableKey(
                "PK_SchoolExtension",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            [
                CreateColumn("DocumentId", ColumnKind.ParentKeyPart),
                CreateColumn("ExtensionCode", ColumnKind.Scalar),
            ],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.RootExtension,
                [new DbColumnName("DocumentId")],
                [new DbColumnName("DocumentId")],
                [new DbColumnName("DocumentId")],
                []
            ),
        };

        return new TableWritePlan(
            tableModel,
            InsertSql: """
            insert into sample."SchoolExtension" values (@DocumentId, @ExtensionCode)
            """,
            UpdateSql: """
            update sample."SchoolExtension" set "ExtensionCode" = @ExtensionCode where "DocumentId" = @DocumentId
            """,
            DeleteByParentSql: """
            delete from sample."SchoolExtension" where "DocumentId" = @DocumentId
            """,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, 2, 1000),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    tableModel.Columns[0],
                    new WriteValueSource.ParentKeyPart(0),
                    "DocumentId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[1],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression("$.extensionCode", []),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 30)
                    ),
                    "ExtensionCode"
                ),
            ],
            KeyUnificationPlans: []
        );
    }

    private static TableWritePlan CreateCollectionPlan()
    {
        var tableModel = new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), "SchoolAddress"),
            new JsonPathExpression("$.addresses[*]", []),
            new TableKey(
                "PK_SchoolAddress",
                [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.CollectionKey)]
            ),
            [
                CreateColumn("CollectionItemId", ColumnKind.CollectionKey),
                CreateColumn("School_DocumentId", ColumnKind.ParentKeyPart),
                CreateColumn("Ordinal", ColumnKind.Ordinal),
                CreateColumn("AddressType", ColumnKind.Scalar),
            ],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.Collection,
                [new DbColumnName("CollectionItemId")],
                [new DbColumnName("School_DocumentId")],
                [new DbColumnName("School_DocumentId")],
                [
                    new CollectionSemanticIdentityBinding(
                        new JsonPathExpression("$.addressType", []),
                        new DbColumnName("AddressType")
                    ),
                ]
            ),
        };

        return new TableWritePlan(
            tableModel,
            InsertSql: """
            insert into edfi."SchoolAddress" values (@CollectionItemId, @School_DocumentId, @Ordinal, @AddressType)
            """,
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, 4, 1000),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    tableModel.Columns[0],
                    new WriteValueSource.Precomputed(),
                    "CollectionItemId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[1],
                    new WriteValueSource.ParentKeyPart(0),
                    "School_DocumentId"
                ),
                new WriteColumnBinding(tableModel.Columns[2], new WriteValueSource.Ordinal(), "Ordinal"),
                new WriteColumnBinding(
                    tableModel.Columns[3],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression("$.addressType", []),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 30)
                    ),
                    "AddressType"
                ),
            ],
            KeyUnificationPlans: [],
            CollectionMergePlan: new CollectionMergePlan(
                [new CollectionMergeSemanticIdentityBinding(new JsonPathExpression("$.addressType", []), 3)],
                StableRowIdentityBindingIndex: 0,
                UpdateByStableRowIdentitySql: """
                update edfi."SchoolAddress" set "Ordinal" = @Ordinal, "AddressType" = @AddressType where "CollectionItemId" = @CollectionItemId
                """,
                DeleteByStableRowIdentitySql: """
                delete from edfi."SchoolAddress" where "CollectionItemId" = @CollectionItemId
                """,
                OrdinalBindingIndex: 2,
                CompareBindingIndexesInOrder: [0, 1, 2, 3]
            ),
            CollectionKeyPreallocationPlan: new CollectionKeyPreallocationPlan(
                new DbColumnName("CollectionItemId"),
                0
            )
        );
    }

    private static TableWritePlan CreatePeriodPlan()
    {
        var tableModel = new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), "SchoolAddressPeriod"),
            new JsonPathExpression("$.addresses[*].periods[*]", []),
            new TableKey(
                "PK_SchoolAddressPeriod",
                [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.CollectionKey)]
            ),
            [
                CreateColumn("CollectionItemId", ColumnKind.CollectionKey),
                CreateColumn("School_DocumentId", ColumnKind.ParentKeyPart),
                CreateColumn("ParentCollectionItemId", ColumnKind.ParentKeyPart),
                CreateColumn("Ordinal", ColumnKind.Ordinal),
                CreateColumn("BeginDate", ColumnKind.Scalar),
            ],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.Collection,
                [new DbColumnName("CollectionItemId")],
                [new DbColumnName("School_DocumentId")],
                [new DbColumnName("ParentCollectionItemId")],
                [
                    new CollectionSemanticIdentityBinding(
                        new JsonPathExpression("$.beginDate", []),
                        new DbColumnName("BeginDate")
                    ),
                ]
            ),
        };

        return new TableWritePlan(
            tableModel,
            InsertSql: """
            insert into edfi."SchoolAddressPeriod" values (@CollectionItemId, @School_DocumentId, @ParentCollectionItemId, @Ordinal, @BeginDate)
            """,
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, 5, 1000),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    tableModel.Columns[0],
                    new WriteValueSource.Precomputed(),
                    "CollectionItemId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[1],
                    new WriteValueSource.ParentKeyPart(0),
                    "School_DocumentId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[2],
                    new WriteValueSource.ParentKeyPart(1),
                    "ParentCollectionItemId"
                ),
                new WriteColumnBinding(tableModel.Columns[3], new WriteValueSource.Ordinal(), "Ordinal"),
                new WriteColumnBinding(
                    tableModel.Columns[4],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression("$.beginDate", []),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 30)
                    ),
                    "BeginDate"
                ),
            ],
            KeyUnificationPlans: [],
            CollectionMergePlan: new CollectionMergePlan(
                [new CollectionMergeSemanticIdentityBinding(new JsonPathExpression("$.beginDate", []), 4)],
                StableRowIdentityBindingIndex: 0,
                UpdateByStableRowIdentitySql: """
                update edfi."SchoolAddressPeriod" set "Ordinal" = @Ordinal, "BeginDate" = @BeginDate where "CollectionItemId" = @CollectionItemId
                """,
                DeleteByStableRowIdentitySql: """
                delete from edfi."SchoolAddressPeriod" where "CollectionItemId" = @CollectionItemId
                """,
                OrdinalBindingIndex: 3,
                CompareBindingIndexesInOrder: [3, 4]
            ),
            CollectionKeyPreallocationPlan: new CollectionKeyPreallocationPlan(
                new DbColumnName("CollectionItemId"),
                0
            )
        );
    }

    private static TableWritePlan CreateExtensionCollectionPlan()
    {
        var tableModel = new DbTableModel(
            new DbTableName(new DbSchemaName("sample"), "SchoolExtensionIntervention"),
            new JsonPathExpression("$._ext.sample.interventions[*]", []),
            new TableKey(
                "PK_SchoolExtensionIntervention",
                [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.CollectionKey)]
            ),
            [
                CreateColumn("CollectionItemId", ColumnKind.CollectionKey),
                CreateColumn("School_DocumentId", ColumnKind.ParentKeyPart),
                CreateColumn("Ordinal", ColumnKind.Ordinal),
                CreateColumn("InterventionCode", ColumnKind.Scalar),
            ],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.ExtensionCollection,
                [new DbColumnName("CollectionItemId")],
                [new DbColumnName("School_DocumentId")],
                [new DbColumnName("School_DocumentId")],
                [
                    new CollectionSemanticIdentityBinding(
                        new JsonPathExpression("$.interventionCode", []),
                        new DbColumnName("InterventionCode")
                    ),
                ]
            ),
        };

        return new TableWritePlan(
            tableModel,
            InsertSql: """
            insert into sample."SchoolExtensionIntervention" values (@CollectionItemId, @School_DocumentId, @Ordinal, @InterventionCode)
            """,
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, 4, 1000),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    tableModel.Columns[0],
                    new WriteValueSource.Precomputed(),
                    "CollectionItemId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[1],
                    new WriteValueSource.ParentKeyPart(0),
                    "School_DocumentId"
                ),
                new WriteColumnBinding(tableModel.Columns[2], new WriteValueSource.Ordinal(), "Ordinal"),
                new WriteColumnBinding(
                    tableModel.Columns[3],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression("$.interventionCode", []),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 30)
                    ),
                    "InterventionCode"
                ),
            ],
            KeyUnificationPlans: [],
            CollectionMergePlan: new CollectionMergePlan(
                [
                    new CollectionMergeSemanticIdentityBinding(
                        new JsonPathExpression("$.interventionCode", []),
                        3
                    ),
                ],
                StableRowIdentityBindingIndex: 0,
                UpdateByStableRowIdentitySql: """
                update sample."SchoolExtensionIntervention" set "Ordinal" = @Ordinal, "InterventionCode" = @InterventionCode where "CollectionItemId" = @CollectionItemId
                """,
                DeleteByStableRowIdentitySql: """
                delete from sample."SchoolExtensionIntervention" where "CollectionItemId" = @CollectionItemId
                """,
                OrdinalBindingIndex: 2,
                CompareBindingIndexesInOrder: [2, 3]
            ),
            CollectionKeyPreallocationPlan: new CollectionKeyPreallocationPlan(
                new DbColumnName("CollectionItemId"),
                0
            )
        );
    }

    private static TableWritePlan CreateCollectionExtensionScopePlan()
    {
        var tableModel = new DbTableModel(
            new DbTableName(new DbSchemaName("sample"), "SchoolExtensionAddress"),
            new JsonPathExpression("$.addresses[*]._ext.sample", []),
            new TableKey(
                "PK_SchoolExtensionAddress",
                [new DbKeyColumn(new DbColumnName("BaseCollectionItemId"), ColumnKind.ParentKeyPart)]
            ),
            [
                CreateColumn("BaseCollectionItemId", ColumnKind.ParentKeyPart),
                CreateColumn("FavoriteColor", ColumnKind.Scalar),
            ],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.CollectionExtensionScope,
                [new DbColumnName("BaseCollectionItemId")],
                [new DbColumnName("BaseCollectionItemId")],
                [new DbColumnName("BaseCollectionItemId")],
                []
            ),
        };

        return new TableWritePlan(
            tableModel,
            InsertSql: """
            insert into sample."SchoolExtensionAddress" values (@BaseCollectionItemId, @FavoriteColor)
            """,
            UpdateSql: """
            update sample."SchoolExtensionAddress" set "FavoriteColor" = @FavoriteColor where "BaseCollectionItemId" = @BaseCollectionItemId
            """,
            DeleteByParentSql: """
            delete from sample."SchoolExtensionAddress" where "BaseCollectionItemId" = @BaseCollectionItemId
            """,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, 2, 1000),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    tableModel.Columns[0],
                    new WriteValueSource.ParentKeyPart(0),
                    "BaseCollectionItemId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[1],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression("$.favoriteColor", []),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 30)
                    ),
                    "FavoriteColor"
                ),
            ],
            KeyUnificationPlans: []
        );
    }

    private static TableWritePlan CreateCollectionAlignedExtensionChildCollectionPlan()
    {
        var tableModel = new DbTableModel(
            new DbTableName(new DbSchemaName("sample"), "SchoolExtensionAddressService"),
            new JsonPathExpression("$.addresses[*]._ext.sample.services[*]", []),
            new TableKey(
                "PK_SchoolExtensionAddressService",
                [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.CollectionKey)]
            ),
            [
                CreateColumn("CollectionItemId", ColumnKind.CollectionKey),
                CreateColumn("School_DocumentId", ColumnKind.ParentKeyPart),
                CreateColumn("BaseCollectionItemId", ColumnKind.ParentKeyPart),
                CreateColumn("Ordinal", ColumnKind.Ordinal),
                CreateColumn("ServiceName", ColumnKind.Scalar),
            ],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.ExtensionCollection,
                [new DbColumnName("CollectionItemId")],
                [new DbColumnName("School_DocumentId")],
                [new DbColumnName("BaseCollectionItemId")],
                [
                    new CollectionSemanticIdentityBinding(
                        new JsonPathExpression("$.serviceName", []),
                        new DbColumnName("ServiceName")
                    ),
                ]
            ),
        };

        return new TableWritePlan(
            tableModel,
            InsertSql: """
            insert into sample."SchoolExtensionAddressService" values (@CollectionItemId, @School_DocumentId, @BaseCollectionItemId, @Ordinal, @ServiceName)
            """,
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, 5, 1000),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    tableModel.Columns[0],
                    new WriteValueSource.Precomputed(),
                    "CollectionItemId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[1],
                    new WriteValueSource.DocumentId(),
                    "School_DocumentId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[2],
                    new WriteValueSource.ParentKeyPart(0),
                    "BaseCollectionItemId"
                ),
                new WriteColumnBinding(tableModel.Columns[3], new WriteValueSource.Ordinal(), "Ordinal"),
                new WriteColumnBinding(
                    tableModel.Columns[4],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression("$.serviceName", []),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 30)
                    ),
                    "ServiceName"
                ),
            ],
            KeyUnificationPlans: [],
            CollectionMergePlan: new CollectionMergePlan(
                [new CollectionMergeSemanticIdentityBinding(new JsonPathExpression("$.serviceName", []), 4)],
                StableRowIdentityBindingIndex: 0,
                UpdateByStableRowIdentitySql: """
                update sample."SchoolExtensionAddressService" set "Ordinal" = @Ordinal, "ServiceName" = @ServiceName where "CollectionItemId" = @CollectionItemId
                """,
                DeleteByStableRowIdentitySql: """
                delete from sample."SchoolExtensionAddressService" where "CollectionItemId" = @CollectionItemId
                """,
                OrdinalBindingIndex: 3,
                CompareBindingIndexesInOrder: [3, 4]
            ),
            CollectionKeyPreallocationPlan: new CollectionKeyPreallocationPlan(
                new DbColumnName("CollectionItemId"),
                0
            )
        );
    }

    private static DbColumnModel CreateColumn(string name, ColumnKind kind, bool isNullable = false)
    {
        return new DbColumnModel(
            new DbColumnName(name),
            kind,
            kind is ColumnKind.Scalar or ColumnKind.Ordinal
                ? new RelationalScalarType(ScalarKind.String)
                : null,
            isNullable,
            null,
            null,
            new ColumnStorage.Stored()
        );
    }

    private static RelationalWriteNoProfileTableRow CreateRow(params object?[] values)
    {
        return new RelationalWriteNoProfileTableRow(
            values.Select(value =>
                value switch
                {
                    FlattenedWriteValue flattenedWriteValue => flattenedWriteValue,
                    _ => new FlattenedWriteValue.Literal(value),
                }
            ),
            values.Select(value =>
                value switch
                {
                    FlattenedWriteValue flattenedWriteValue => flattenedWriteValue,
                    _ => new FlattenedWriteValue.Literal(value),
                }
            )
        );
    }

    private static FlattenedWriteValue.UnresolvedCollectionItemId NewCollectionItemId() =>
        FlattenedWriteValue.UnresolvedCollectionItemId.Create();

    private sealed record CommandResponse(object? ScalarResult = null, int NonQueryResult = 1);

    private sealed class RecordingRelationalWriteSession : IRelationalWriteSession
    {
        private readonly DbConnection _connection = new StubDbConnection();
        private readonly Queue<CommandResponse> _responses;

        public List<RelationalCommand> Commands { get; } = [];

        public DbConnection Connection => _connection;

        public DbTransaction Transaction { get; }

        public RecordingRelationalWriteSession(IEnumerable<CommandResponse> responses)
        {
            _responses = new Queue<CommandResponse>(responses);
            Transaction = new StubDbTransaction(_connection);
        }

        public DbCommand CreateCommand(RelationalCommand command)
        {
            Commands.Add(command);
            var response = _responses.Count == 0 ? new CommandResponse() : _responses.Dequeue();

            return new RecordingDbCommand(response);
        }

        public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RollbackAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingDbCommand(CommandResponse response) : DbCommand
    {
        protected override DbConnection? DbConnection { get; set; }

        protected override DbParameterCollection DbParameterCollection { get; } =
            new StubDbParameterCollection();

        protected override DbTransaction? DbTransaction { get; set; }

        [AllowNull]
        public override string CommandText { get; set; } = string.Empty;

        public override int CommandTimeout { get; set; }

        public override CommandType CommandType { get; set; }

        public override bool DesignTimeVisible { get; set; }

        public override UpdateRowSource UpdatedRowSource { get; set; }

        public override void Cancel() { }

        public override int ExecuteNonQuery() => response.NonQueryResult;

        public override object? ExecuteScalar() => response.ScalarResult;

        public override void Prepare() { }

        protected override DbParameter CreateDbParameter() => new StubDbParameter();

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            throw new NotSupportedException();
        }

        public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(response.NonQueryResult);
        }

        public override Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(response.ScalarResult);
        }
    }

    private sealed class StubDbParameterCollection : DbParameterCollection
    {
        public override int Count => 0;

        public override object SyncRoot => this;

        public override int Add(object value) => 0;

        public override void AddRange(Array values) { }

        public override void Clear() { }

        public override bool Contains(object value) => false;

        public override bool Contains(string value) => false;

        public override void CopyTo(Array array, int index) { }

        public override IEnumerator GetEnumerator() => Array.Empty<object>().GetEnumerator();

        protected override DbParameter GetParameter(int index) => throw new IndexOutOfRangeException();

        protected override DbParameter GetParameter(string parameterName) =>
            throw new IndexOutOfRangeException();

        public override int IndexOf(object value) => -1;

        public override int IndexOf(string parameterName) => -1;

        public override void Insert(int index, object value) { }

        public override void Remove(object value) { }

        public override void RemoveAt(int index) { }

        public override void RemoveAt(string parameterName) { }

        protected override void SetParameter(int index, DbParameter value) { }

        protected override void SetParameter(string parameterName, DbParameter value) { }
    }

    private sealed class StubDbParameter : DbParameter
    {
        public override DbType DbType { get; set; }

        public override ParameterDirection Direction { get; set; }

        public override bool IsNullable { get; set; }

        [AllowNull]
        public override string ParameterName { get; set; } = string.Empty;

        [AllowNull]
        public override string SourceColumn { get; set; } = string.Empty;

        public override object? Value { get; set; }

        public override bool SourceColumnNullMapping { get; set; }

        public override int Size { get; set; }

        public override void ResetDbType() { }
    }

    private sealed class StubDbConnection : DbConnection
    {
        [AllowNull]
        public override string ConnectionString { get; set; } = string.Empty;

        public override string Database => "stub";

        public override string DataSource => "stub";

        public override string ServerVersion => "1.0";

        public override ConnectionState State => ConnectionState.Open;

        public override void ChangeDatabase(string databaseName) { }

        public override void Close() { }

        public override void Open() { }

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
        {
            throw new NotSupportedException();
        }

        protected override DbCommand CreateDbCommand()
        {
            throw new NotSupportedException();
        }
    }

    private sealed class StubDbTransaction(DbConnection connection) : DbTransaction
    {
        public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;

        protected override DbConnection DbConnection => connection;

        public override void Commit() { }

        public override void Rollback() { }
    }
}
