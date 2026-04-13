// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections;
using System.Collections.Immutable;
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
public sealed class Gate_E_contract_round_trip
{
    [Test]
    public async Task It_emits_identical_sql_for_root_only_insert()
    {
        var rootPlan = CreateRootPlan();
        var writePlan = CreateWritePlan([rootPlan]);
        var selectedBody = JsonNode.Parse("""{"schoolId":255901,"name":"Lincoln High"}""")!;
        var flattenedWriteSet = CreateFlattenedWriteSet(
            rootPlan,
            [FlattenedWriteValue.UnresolvedRootDocumentId.Instance, Literal(255901), Literal("Lincoln High")]
        );
        var request = CreateRequest(writePlan, RelationalWriteOperationKind.Post, selectedBody);

        var result = await RunContractAsync(
            new ContractFixture(
                writePlan,
                flattenedWriteSet,
                CurrentState: null,
                request,
                Responses: [new CommandResponse(ScalarResult: 910L), new CommandResponse()]
            )
        );

        result.Commands.Should().HaveCount(2);
        result.Commands[0].Sql.Should().Contain("INSERT INTO dms.\"Document\"");
        result.Commands[1].Sql.Should().Contain("insert into edfi.\"School\"");
    }

    [Test]
    public async Task It_emits_identical_sql_for_root_only_update_with_changed_column()
    {
        var rootPlan = CreateRootPlan();
        var writePlan = CreateWritePlan([rootPlan]);
        var selectedBody = JsonNode.Parse("""{"schoolId":255901,"name":"Lincoln High Updated"}""")!;
        var flattenedWriteSet = CreateFlattenedWriteSet(
            rootPlan,
            [Literal(345L), Literal(255901), Literal("Lincoln High Updated")]
        );
        var currentState = CreateCurrentState(
            writePlan,
            new TableRows(
                rootPlan,
                [
                    [345L, 255901, "Lincoln High"],
                ]
            )
        );
        var request = CreateRequest(writePlan, RelationalWriteOperationKind.Put, selectedBody);

        var result = await RunContractAsync(
            new ContractFixture(
                writePlan,
                flattenedWriteSet,
                currentState,
                request,
                Responses: [new CommandResponse()]
            )
        );

        result
            .Commands.Should()
            .ContainSingle(command =>
                command.Sql.Contains("update edfi.\"School\"", StringComparison.Ordinal)
            );
    }

    [Test]
    public async Task It_emits_identical_sql_for_collection_insert_update_and_delete_in_one_write()
    {
        var rootPlan = CreateRootPlan();
        var addressPlan = CreateAddressPlan();
        var writePlan = CreateWritePlan([rootPlan, addressPlan]);
        var selectedBody = JsonNode.Parse(
            """
            {
              "schoolId": 255901,
              "name": "Lincoln High",
              "addresses": [
                { "addressType": "Home" },
                { "addressType": "Physical" }
              ]
            }
            """
        )!;
        var newAddressId = FlattenedWriteValue.UnresolvedCollectionItemId.Create();
        var flattenedWriteSet = CreateFlattenedWriteSet(
            rootPlan,
            [Literal(345L), Literal(255901), Literal("Lincoln High")],
            collectionCandidates:
            [
                CreateAddressCandidate(
                    addressPlan,
                    requestOrder: 0,
                    collectionItemId: Literal(11L),
                    rootDocumentId: Literal(345L),
                    addressType: "Home"
                ),
                CreateAddressCandidate(
                    addressPlan,
                    requestOrder: 1,
                    collectionItemId: newAddressId,
                    rootDocumentId: Literal(345L),
                    addressType: "Physical"
                ),
            ]
        );
        var currentState = CreateCurrentState(
            writePlan,
            new TableRows(
                rootPlan,
                [
                    [345L, 255901, "Lincoln High"],
                ]
            ),
            new TableRows(
                addressPlan,
                [
                    [10L, 345L, 0, "Mailing"],
                    [11L, 345L, 1, "Home"],
                ]
            )
        );
        var request = CreateRequest(writePlan, RelationalWriteOperationKind.Put, selectedBody);

        var result = await RunContractAsync(
            new ContractFixture(
                writePlan,
                flattenedWriteSet,
                currentState,
                request,
                Responses:
                [
                    new CommandResponse(),
                    new CommandResponse(),
                    new CommandResponse(ScalarResult: 910L),
                    new CommandResponse(),
                ]
            )
        );

        result
            .Commands.Should()
            .Contain(command =>
                command.Sql.Contains("delete from edfi.\"SchoolAddress\"", StringComparison.Ordinal)
            );
        result
            .Commands.Should()
            .Contain(command =>
                command.Sql.Contains("update edfi.\"SchoolAddress\"", StringComparison.Ordinal)
            );
        result
            .Commands.Should()
            .Contain(command =>
                command.Sql.Contains("insert into edfi.\"SchoolAddress\"", StringComparison.Ordinal)
            );
    }

    [Test]
    public async Task It_emits_identical_sql_when_reference_derived_bindings_are_present()
    {
        var writePlan = CreateReferenceDerivedWritePlan();
        var rootPlan = writePlan.TablePlansInDependencyOrder.Single();
        var selectedBody = JsonNode.Parse(
            """
            {
              "schoolReference": {
                "schoolId": 255901,
                "schoolCategoryDescriptor": "uri://ed-fi.org/SchoolCategoryDescriptor#Alternative"
              }
            }
            """
        )!;
        var flattenedWriteSet = CreateFlattenedWriteSet(
            rootPlan,
            [FlattenedWriteValue.UnresolvedRootDocumentId.Instance, Literal(901L), Literal(7001L)]
        );
        var request = CreateRequest(writePlan, RelationalWriteOperationKind.Post, selectedBody);

        var result = await RunContractAsync(
            new ContractFixture(
                writePlan,
                flattenedWriteSet,
                CurrentState: null,
                request,
                Responses: [new CommandResponse(ScalarResult: 910L), new CommandResponse()]
            )
        );

        result
            .Commands.Should()
            .ContainSingle(command =>
                command.Parameters.Any(parameter =>
                    parameter.Name == "@SchoolCategoryDescriptorId" && Equals(parameter.Value, 7001L)
                )
            );
    }

    [Test]
    public async Task It_emits_identical_sql_for_root_collection_and_extension_combination()
    {
        var rootPlan = CreateRootPlan();
        var rootExtensionPlan = CreateRootExtensionPlan();
        var addressPlan = CreateAddressPlan();
        var writePlan = CreateWritePlan([rootPlan, rootExtensionPlan, addressPlan]);
        var selectedBody = JsonNode.Parse(
            """
            {
              "schoolId": 255901,
              "name": "Lincoln High",
              "_ext": {
                "sample": {
                  "extensionCode": "BLUE"
                }
              },
              "addresses": [
                { "addressType": "Mailing" }
              ]
            }
            """
        )!;
        var newAddressId = FlattenedWriteValue.UnresolvedCollectionItemId.Create();
        var flattenedWriteSet = CreateFlattenedWriteSet(
            rootPlan,
            [FlattenedWriteValue.UnresolvedRootDocumentId.Instance, Literal(255901), Literal("Lincoln High")],
            rootExtensionRows:
            [
                CreateRootExtensionRow(
                    rootExtensionPlan,
                    FlattenedWriteValue.UnresolvedRootDocumentId.Instance,
                    "BLUE"
                ),
            ],
            collectionCandidates:
            [
                CreateAddressCandidate(
                    addressPlan,
                    requestOrder: 0,
                    collectionItemId: newAddressId,
                    rootDocumentId: FlattenedWriteValue.UnresolvedRootDocumentId.Instance,
                    addressType: "Mailing"
                ),
            ]
        );
        var request = CreateRequest(writePlan, RelationalWriteOperationKind.Post, selectedBody);

        var result = await RunContractAsync(
            new ContractFixture(
                writePlan,
                flattenedWriteSet,
                CurrentState: null,
                request,
                Responses:
                [
                    new CommandResponse(ScalarResult: 910L),
                    new CommandResponse(),
                    new CommandResponse(),
                    new CommandResponse(ScalarResult: 920L),
                    new CommandResponse(),
                ]
            )
        );

        result
            .Commands.Select(command => command.Sql)
            .Should()
            .Contain([rootPlan.InsertSql, rootExtensionPlan.InsertSql, addressPlan.InsertSql]);
    }

    [Test]
    public async Task It_emits_delete_sql_for_omitted_root_extension_scope_with_current_state()
    {
        var rootPlan = CreateRootPlan();
        var rootExtensionPlan = CreateRootExtensionPlan();
        var writePlan = CreateWritePlan([rootPlan, rootExtensionPlan]);
        var selectedBody = JsonNode.Parse("""{"schoolId":255901,"name":"Lincoln High"}""")!;
        var flattenedWriteSet = CreateFlattenedWriteSet(
            rootPlan,
            [Literal(345L), Literal(255901), Literal("Lincoln High")]
        );
        var currentState = CreateCurrentState(
            writePlan,
            new TableRows(
                rootPlan,
                [
                    [345L, 255901, "Lincoln High"],
                ]
            ),
            new TableRows(
                rootExtensionPlan,
                [
                    [345L, "BLUE"],
                ]
            )
        );
        var request = CreateRequest(writePlan, RelationalWriteOperationKind.Put, selectedBody);

        var result = await RunContractAsync(
            new ContractFixture(
                writePlan,
                flattenedWriteSet,
                currentState,
                request,
                Responses: [new CommandResponse()]
            )
        );

        result
            .Commands.Should()
            .ContainSingle(command =>
                command.Sql.Contains("delete from sample.\"SchoolExtension\"", StringComparison.Ordinal)
            );
    }

    private static async Task<ContractResult> RunContractAsync(ContractFixture fixture)
    {
        var unified = await RunUnifiedPipelineAsync(fixture);
        return new ContractResult(unified);
    }

    private static async Task<IReadOnlyList<CapturedCommand>> RunUnifiedPipelineAsync(ContractFixture fixture)
    {
        var outcome = new RelationalWriteMergeSynthesizer().Synthesize(
            new RelationalWriteMergeRequest(
                fixture.WritePlan,
                fixture.FlattenedWriteSet,
                fixture.CurrentState,
                ProfileRequest: null,
                ProfileContext: null,
                CompiledScopeCatalog: null,
                SelectedBody: fixture.Request.SelectedBody
            )
        );

        if (outcome is not RelationalWriteMergeSynthesisOutcome.Success success)
        {
            throw new AssertionException(
                $"Unified pipeline did not synthesize a success outcome. Actual outcome: {outcome.GetType().Name}."
            );
        }

        var writeSession = new SqlCaptureSession(fixture.Responses);
        await new RelationalWritePersister().PersistAsync(fixture.Request, success.MergeResult, writeSession);

        return CaptureCommands(writeSession.Commands);
    }

    private static IReadOnlyList<CapturedCommand> CaptureCommands(
        IReadOnlyList<RelationalCommand> commands
    ) =>
        [
            .. commands.Select(command => new CapturedCommand(
                command.CommandText,
                [
                    .. command.Parameters.Select(parameter => new CapturedParameter(
                        parameter.Name,
                        NormalizeValue(parameter.Value)
                    )),
                ]
            )),
        ];

    private static object? NormalizeValue(object? value) => value is DBNull ? null : value;

    private static FlattenedWriteSet CreateFlattenedWriteSet(
        TableWritePlan rootPlan,
        IReadOnlyList<FlattenedWriteValue> rootValues,
        IReadOnlyList<RootExtensionWriteRowBuffer>? rootExtensionRows = null,
        IReadOnlyList<CollectionWriteCandidate>? collectionCandidates = null
    ) =>
        new(
            new RootWriteRowBuffer(rootPlan, rootValues, rootExtensionRows ?? [], collectionCandidates ?? [])
        );

    private static RootExtensionWriteRowBuffer CreateRootExtensionRow(
        TableWritePlan rootExtensionPlan,
        FlattenedWriteValue documentId,
        string extensionCode
    ) => new(rootExtensionPlan, [documentId, Literal(extensionCode)]);

    private static CollectionWriteCandidate CreateAddressCandidate(
        TableWritePlan addressPlan,
        int requestOrder,
        FlattenedWriteValue collectionItemId,
        FlattenedWriteValue rootDocumentId,
        string addressType
    ) =>
        new(
            addressPlan,
            ordinalPath: [requestOrder],
            requestOrder: requestOrder,
            values: [collectionItemId, rootDocumentId, Literal(requestOrder), Literal(addressType)],
            semanticIdentityValues: [addressType]
        );

    private static RelationalWriteCurrentState CreateCurrentState(
        ResourceWritePlan writePlan,
        params TableRows[] tableRows
    )
    {
        var rowsByTable = tableRows.ToDictionary(row => row.TableWritePlan.TableModel.Table.Name);

        return new RelationalWriteCurrentState(
            new DocumentMetadataRow(
                345L,
                Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"),
                44L,
                44L,
                new DateTimeOffset(2026, 4, 2, 12, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 4, 2, 12, 0, 0, TimeSpan.Zero)
            ),
            [
                .. writePlan.TablePlansInDependencyOrder.Select(tableWritePlan => new HydratedTableRows(
                    tableWritePlan.TableModel,
                    rowsByTable.TryGetValue(tableWritePlan.TableModel.Table.Name, out var rows)
                        ? rows.Rows
                        : []
                )),
            ]
        );
    }

    private static RelationalWriteExecutorRequest CreateRequest(
        ResourceWritePlan writePlan,
        RelationalWriteOperationKind operationKind,
        JsonNode selectedBody,
        SqlDialect dialect = SqlDialect.Pgsql
    )
    {
        var mappingSet = CreateMappingSet(writePlan, dialect);

        return new RelationalWriteExecutorRequest(
            mappingSet,
            operationKind,
            operationKind == RelationalWriteOperationKind.Put
                ? new RelationalWriteTargetRequest.Put(
                    new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"))
                )
                : new RelationalWriteTargetRequest.Post(
                    new ReferentialId(Guid.NewGuid()),
                    new DocumentUuid(Guid.Parse("cccccccc-1111-2222-3333-dddddddddddd"))
                ),
            writePlan,
            operationKind == RelationalWriteOperationKind.Put
                ? CreateReadPlan(writePlan.Model, dialect)
                : null,
            selectedBody,
            false,
            new TraceId("gate-e-contract-test"),
            new ReferenceResolverRequest(mappingSet, writePlan.Model.Resource, [], []),
            operationKind == RelationalWriteOperationKind.Put
                ? new RelationalWriteTargetContext.ExistingDocument(
                    345L,
                    new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"))
                )
                : new RelationalWriteTargetContext.CreateNew(
                    new DocumentUuid(Guid.Parse("cccccccc-1111-2222-3333-dddddddddddd"))
                )
        );
    }

    private static MappingSet CreateMappingSet(
        ResourceWritePlan writePlan,
        SqlDialect dialect = SqlDialect.Pgsql
    )
    {
        var resource = writePlan.Model.Resource;
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
                        writePlan.Model
                    ),
                ],
                AbstractIdentityTablesInNameOrder: [],
                AbstractUnionViewsInNameOrder: [],
                IndexesInCreateOrder: [],
                TriggersInCreateOrder: []
            ),
            WritePlansByResource: new Dictionary<QualifiedResourceName, ResourceWritePlan>
            {
                [resource] = writePlan,
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

    private static ResourceReadPlan CreateReadPlan(
        RelationalResourceModel resourceModel,
        SqlDialect dialect
    ) =>
        new(
            resourceModel,
            KeysetTableConventions.GetKeysetTableContract(dialect),
            [
                .. resourceModel.TablesInDependencyOrder.Select(tableModel => new TableReadPlan(
                    tableModel,
                    $"select * from {tableModel.Table.Schema.Value}.\"{tableModel.Table.Name}\""
                )),
            ],
            [],
            []
        );

    private static ResourceWritePlan CreateWritePlan(
        IReadOnlyList<TableWritePlan> tablePlans,
        IReadOnlyList<DocumentReferenceBinding>? documentReferenceBindings = null,
        string resourceName = "School"
    )
    {
        var resourceModel = new RelationalResourceModel(
            Resource: new QualifiedResourceName("Ed-Fi", resourceName),
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: tablePlans[0].TableModel,
            TablesInDependencyOrder: tablePlans.Select(static plan => plan.TableModel).ToArray(),
            DocumentReferenceBindings: documentReferenceBindings ?? [],
            DescriptorEdgeSources: []
        );

        return new ResourceWritePlan(resourceModel, tablePlans);
    }

    private static ResourceWritePlan CreateReferenceDerivedWritePlan()
    {
        var schoolResource = new QualifiedResourceName("Ed-Fi", "School");
        var descriptorResource = new QualifiedResourceName("Ed-Fi", "SchoolCategoryDescriptor");
        var schoolReferencePath = new JsonPathExpression(
            "$.schoolReference",
            [new JsonPathSegment.Property("schoolReference")]
        );
        var descriptorPath = new JsonPathExpression(
            "$.schoolReference.schoolCategoryDescriptor",
            [
                new JsonPathSegment.Property("schoolReference"),
                new JsonPathSegment.Property("schoolCategoryDescriptor"),
            ]
        );
        var descriptorIdentityPath = new JsonPathExpression(
            "$.schoolCategoryDescriptor",
            [new JsonPathSegment.Property("schoolCategoryDescriptor")]
        );
        var tableModel = new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), "ProgramDescriptorReferenceDerived"),
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_ProgramDescriptorReferenceDerived",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            [
                new DbColumnModel(
                    new DbColumnName("DocumentId"),
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("School_DocumentId"),
                    ColumnKind.DocumentFk,
                    new RelationalScalarType(ScalarKind.Int64),
                    true,
                    schoolReferencePath,
                    schoolResource,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("SchoolCategoryDescriptorId"),
                    ColumnKind.DescriptorFk,
                    new RelationalScalarType(ScalarKind.Int64),
                    true,
                    descriptorPath,
                    descriptorResource,
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

        var rootPlan = new TableWritePlan(
            tableModel,
            InsertSql: """
            insert into edfi."ProgramDescriptorReferenceDerived" values (@DocumentId, @School_DocumentId, @SchoolCategoryDescriptorId)
            """,
            UpdateSql: null,
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
                    new WriteValueSource.DocumentReference(0),
                    "School_DocumentId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[2],
                    new WriteValueSource.ReferenceDerived(
                        new ReferenceDerivedValueSourceMetadata(
                            BindingIndex: 0,
                            ReferenceObjectPath: schoolReferencePath,
                            IdentityJsonPath: descriptorIdentityPath,
                            ReferenceJsonPath: descriptorPath
                        )
                    ),
                    "SchoolCategoryDescriptorId"
                ),
            ],
            KeyUnificationPlans: []
        );

        return CreateWritePlan(
            [rootPlan],
            documentReferenceBindings:
            [
                new DocumentReferenceBinding(
                    IsIdentityComponent: false,
                    ReferenceObjectPath: schoolReferencePath,
                    Table: tableModel.Table,
                    FkColumn: new DbColumnName("School_DocumentId"),
                    TargetResource: schoolResource,
                    IdentityBindings:
                    [
                        new ReferenceIdentityBinding(
                            IdentityJsonPath: descriptorIdentityPath,
                            ReferenceJsonPath: descriptorPath,
                            Column: new DbColumnName("SchoolCategoryDescriptorId")
                        ),
                    ]
                ),
            ],
            resourceName: "Program"
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
                CreateColumn("DocumentId", ColumnKind.ParentKeyPart),
                CreateColumn("SchoolId", ColumnKind.Scalar),
                CreateColumn("Name", ColumnKind.Scalar),
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
            InsertSql: """
            insert into edfi."School" values (@DocumentId, @SchoolId, @Name)
            """,
            UpdateSql: """
            update edfi."School" set "SchoolId" = @SchoolId, "Name" = @Name where "DocumentId" = @DocumentId
            """,
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
                        new JsonPathExpression("$.schoolId", [new JsonPathSegment.Property("schoolId")]),
                        new RelationalScalarType(ScalarKind.Int32)
                    ),
                    "SchoolId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[2],
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

    private static TableWritePlan CreateRootExtensionPlan()
    {
        var tableModel = new DbTableModel(
            new DbTableName(new DbSchemaName("sample"), "SchoolExtension"),
            new JsonPathExpression(
                "$._ext.sample",
                [new JsonPathSegment.Property("_ext"), new JsonPathSegment.Property("sample")]
            ),
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
                        new JsonPathExpression(
                            "$.extensionCode",
                            [new JsonPathSegment.Property("extensionCode")]
                        ),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 30)
                    ),
                    "ExtensionCode"
                ),
            ],
            KeyUnificationPlans: []
        );
    }

    private static TableWritePlan CreateAddressPlan()
    {
        var tableModel = new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), "SchoolAddress"),
            new JsonPathExpression(
                "$.addresses[*]",
                [new JsonPathSegment.Property("addresses"), new JsonPathSegment.AnyArrayElement()]
            ),
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
                        new JsonPathExpression(
                            "$.addressType",
                            [new JsonPathSegment.Property("addressType")]
                        ),
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
                        new JsonPathExpression(
                            "$.addressType",
                            [new JsonPathSegment.Property("addressType")]
                        ),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 30)
                    ),
                    "AddressType"
                ),
            ],
            KeyUnificationPlans: [],
            CollectionMergePlan: new CollectionMergePlan(
                [
                    new CollectionMergeSemanticIdentityBinding(
                        new JsonPathExpression(
                            "$.addressType",
                            [new JsonPathSegment.Property("addressType")]
                        ),
                        3
                    ),
                ],
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

    private static DbColumnModel CreateColumn(string name, ColumnKind kind, bool isNullable = false) =>
        new(
            new DbColumnName(name),
            kind,
            kind switch
            {
                ColumnKind.Scalar => new RelationalScalarType(ScalarKind.String),
                ColumnKind.DocumentFk => new RelationalScalarType(ScalarKind.Int64),
                ColumnKind.DescriptorFk => new RelationalScalarType(ScalarKind.Int64),
                ColumnKind.CollectionKey => new RelationalScalarType(ScalarKind.Int64),
                ColumnKind.Ordinal => new RelationalScalarType(ScalarKind.Int32),
                ColumnKind.ParentKeyPart => null,
                _ => null,
            },
            isNullable,
            null,
            null,
            new ColumnStorage.Stored()
        );

    private static FlattenedWriteValue Literal(object? value) => new FlattenedWriteValue.Literal(value);
}

internal sealed record ContractFixture(
    ResourceWritePlan WritePlan,
    FlattenedWriteSet FlattenedWriteSet,
    RelationalWriteCurrentState? CurrentState,
    RelationalWriteExecutorRequest Request,
    IReadOnlyList<CommandResponse> Responses
);

internal sealed record ContractResult(IReadOnlyList<CapturedCommand> Commands);

internal sealed record CapturedCommand(string Sql, ImmutableArray<CapturedParameter> Parameters);

internal sealed record CapturedParameter(string Name, object? Value);

internal sealed record TableRows(TableWritePlan TableWritePlan, IReadOnlyList<object?[]> Rows);

internal sealed record ReservedCollectionItemIdRow(int Ordinal, long CollectionItemId);

internal sealed record CommandResponse(
    object? ScalarResult = null,
    int NonQueryResult = 1,
    IReadOnlyList<ReservedCollectionItemIdRow>? ReservationRows = null
);

internal sealed class SqlCaptureSession : IRelationalWriteSession
{
    private readonly DbConnection _connection = new GateEStubDbConnection();
    private readonly Queue<CommandResponse> _responses;

    public SqlCaptureSession(IEnumerable<CommandResponse> responses)
    {
        _responses = new Queue<CommandResponse>(responses);
        Transaction = new GateEStubDbTransaction(_connection);
    }

    public List<RelationalCommand> Commands { get; } = [];

    public DbConnection Connection => _connection;

    public DbTransaction Transaction { get; }

    public DbCommand CreateCommand(RelationalCommand command)
    {
        Commands.Add(command);
        var response = _responses.Count == 0 ? new CommandResponse() : _responses.Dequeue();
        return new GateERecordingDbCommand(response);
    }

    public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task RollbackAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class GateERecordingDbCommand(CommandResponse response) : DbCommand
{
    protected override DbConnection? DbConnection { get; set; }

    protected override DbParameterCollection DbParameterCollection { get; } =
        new GateEStubDbParameterCollection();

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

    protected override DbParameter CreateDbParameter() => new GateEStubDbParameter();

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) =>
        CreateReservationReader(response);

    protected override Task<DbDataReader> ExecuteDbDataReaderAsync(
        CommandBehavior behavior,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<DbDataReader>(CreateReservationReader(response));
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

    private static DbDataReader CreateReservationReader(CommandResponse response)
    {
        var table = new DataTable();
        table.Columns.Add("Ordinal", typeof(int));
        table.Columns.Add("CollectionItemId", typeof(long));

        foreach (var row in response.ReservationRows ?? [])
        {
            table.Rows.Add(row.Ordinal, row.CollectionItemId);
        }

        return table.CreateDataReader();
    }
}

internal sealed class GateEStubDbParameterCollection : DbParameterCollection
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

    protected override DbParameter GetParameter(string parameterName) => throw new IndexOutOfRangeException();

    public override int IndexOf(object value) => -1;

    public override int IndexOf(string parameterName) => -1;

    public override void Insert(int index, object value) { }

    public override void Remove(object value) { }

    public override void RemoveAt(int index) { }

    public override void RemoveAt(string parameterName) { }

    protected override void SetParameter(int index, DbParameter value) { }

    protected override void SetParameter(string parameterName, DbParameter value) { }
}

internal sealed class GateEStubDbParameter : DbParameter
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

internal sealed class GateEStubDbConnection : DbConnection
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

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
        throw new NotSupportedException();

    protected override DbCommand CreateDbCommand() => throw new NotSupportedException();
}

internal sealed class GateEStubDbTransaction(DbConnection connection) : DbTransaction
{
    public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;

    protected override DbConnection DbConnection => connection;

    public override void Commit() { }

    public override void Rollback() { }
}
