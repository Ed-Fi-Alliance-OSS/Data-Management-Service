// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.Tests.Unit.Handler;

internal sealed record RelationalWriteSeamFixture(
    ResourceInfo ResourceInfo,
    BaseResourceInfo SchoolResourceInfo,
    BaseResourceInfo ProgramTypeDescriptorResourceInfo,
    DocumentUuid DocumentUuid,
    ResourceWritePlan WritePlan,
    ResolvedReferenceSet ResolvedReferences
)
{
    private static readonly QualifiedResourceName _studentResource = new("Ed-Fi", "Student");
    private static readonly QualifiedResourceName _schoolResource = new("Ed-Fi", "School");
    private static readonly QualifiedResourceName _programTypeDescriptorResource = new(
        "Ed-Fi",
        "ProgramTypeDescriptor"
    );

    public static RelationalWriteSeamFixture Create()
    {
        var rootPlan = CreateRootPlan();
        var rootExtensionPlan = CreateRootExtensionPlan();
        var rootExtensionInterventionPlan = CreateRootExtensionInterventionPlan();
        var addressPlan = CreateAddressPlan();
        var addressExtensionPlan = CreateAddressExtensionPlan();
        var addressExtensionServicePlan = CreateAddressExtensionServicePlan();
        var addressPeriodPlan = CreateAddressPeriodPlan();

        var resourceModel = new RelationalResourceModel(
            Resource: _studentResource,
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootPlan.TableModel,
            TablesInDependencyOrder:
            [
                rootPlan.TableModel,
                rootExtensionPlan.TableModel,
                rootExtensionInterventionPlan.TableModel,
                addressPlan.TableModel,
                addressExtensionPlan.TableModel,
                addressExtensionServicePlan.TableModel,
                addressPeriodPlan.TableModel,
            ],
            DocumentReferenceBindings:
            [
                new DocumentReferenceBinding(
                    IsIdentityComponent: false,
                    ReferenceObjectPath: CreatePath(
                        "$.schoolReference",
                        new JsonPathSegment.Property("schoolReference")
                    ),
                    Table: rootPlan.TableModel.Table,
                    FkColumn: new DbColumnName("School_DocumentId"),
                    TargetResource: _schoolResource,
                    IdentityBindings: []
                ),
                new DocumentReferenceBinding(
                    IsIdentityComponent: false,
                    ReferenceObjectPath: CreatePath(
                        "$.addresses[*].periods[*].schoolReference",
                        new JsonPathSegment.Property("addresses"),
                        new JsonPathSegment.AnyArrayElement(),
                        new JsonPathSegment.Property("periods"),
                        new JsonPathSegment.AnyArrayElement(),
                        new JsonPathSegment.Property("schoolReference")
                    ),
                    Table: addressPeriodPlan.TableModel.Table,
                    FkColumn: new DbColumnName("School_DocumentId"),
                    TargetResource: _schoolResource,
                    IdentityBindings: []
                ),
            ],
            DescriptorEdgeSources: []
        );

        return new RelationalWriteSeamFixture(
            ResourceInfo: new ResourceInfo(
                ProjectName: new ProjectName("Ed-Fi"),
                ResourceName: new ResourceName("Student"),
                IsDescriptor: false,
                ResourceVersion: new SemVer("1.0.0"),
                AllowIdentityUpdates: false,
                EducationOrganizationHierarchyInfo: new EducationOrganizationHierarchyInfo(
                    false,
                    default,
                    default
                ),
                AuthorizationSecurableInfo: []
            ),
            SchoolResourceInfo: new BaseResourceInfo(
                new ProjectName("Ed-Fi"),
                new ResourceName("School"),
                false
            ),
            ProgramTypeDescriptorResourceInfo: new BaseResourceInfo(
                new ProjectName("Ed-Fi"),
                new ResourceName("ProgramTypeDescriptor"),
                true
            ),
            DocumentUuid: new DocumentUuid(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")),
            WritePlan: new ResourceWritePlan(
                resourceModel,
                [
                    rootPlan,
                    rootExtensionPlan,
                    rootExtensionInterventionPlan,
                    addressPlan,
                    addressExtensionPlan,
                    addressExtensionServicePlan,
                    addressPeriodPlan,
                ]
            ),
            ResolvedReferences: CreateResolvedReferences()
        );
    }

    public static JsonNode CreateComplexBody()
    {
        return JsonNode.Parse(
            """
            {
              "schoolYear": 2026,
              "schoolReference": {
                "schoolId": 255901
              },
              "programTypeDescriptor": "uri://ed-fi.org/programtypedescriptor#stem",
              "_ext": {
                "sample": {
                  "favoriteColor": "Green",
                  "interventions": [
                    {
                      "interventionCode": "Attendance"
                    },
                    {
                      "interventionCode": "Behavior"
                    }
                  ]
                }
              },
              "addresses": [
                {
                  "addressType": "Home",
                  "addressLine1": "1 Main St",
                  "periods": [
                    {
                      "beginDate": "2026-08-20",
                      "schoolReference": {
                        "schoolId": 255901
                      }
                    },
                    {
                      "beginDate": "2027-08-20",
                      "schoolReference": {
                        "schoolId": 255902
                      }
                    }
                  ],
                  "_ext": {
                    "sample": {
                      "favoriteColor": "Purple",
                      "services": [
                        {
                          "serviceName": "Bus"
                        },
                        {
                          "serviceName": "Meal"
                        }
                      ]
                    }
                  }
                }
              ]
            }
            """
        )!;
    }

    public static string CreateOriginalBodyJson()
    {
        return """
            {
              "schoolYear": 2026,
              "schoolReference": {
                "schoolId": 255901
              },
              "programTypeDescriptor": "uri://ed-fi.org/programtypedescriptor#stem",
              "_ext": {
                "sample": {
                  "favoriteColor": "Blue"
                }
              }
            }
            """;
    }

    public static JsonNode CreateSelectedAuthoritativeBody()
    {
        return JsonNode.Parse(
            """
            {
              "schoolYear": 2030
            }
            """
        )!;
    }

    public MappingSet CreateSupportedMappingSet(SqlDialect dialect)
    {
        var resourceKey = CreateResourceKeyEntry();

        return new MappingSet(
            Key: new MappingSetKey("relational-write-seam", dialect, "v1"),
            Model: CreateDerivedModelSet(dialect, resourceKey),
            WritePlansByResource: new Dictionary<QualifiedResourceName, ResourceWritePlan>
            {
                [_studentResource] = WritePlan,
            },
            ReadPlansByResource: new Dictionary<QualifiedResourceName, ResourceReadPlan>
            {
                [_studentResource] = CreateReadPlan(dialect),
            },
            ResourceKeyIdByResource: new Dictionary<QualifiedResourceName, short>
            {
                [_studentResource] = resourceKey.ResourceKeyId,
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

    public MappingSet CreateMissingWritePlanMappingSet(SqlDialect dialect)
    {
        var resourceKey = CreateResourceKeyEntry();

        return new MappingSet(
            Key: new MappingSetKey("relational-write-seam", dialect, "v1"),
            Model: CreateDerivedModelSet(dialect, resourceKey),
            WritePlansByResource: new Dictionary<QualifiedResourceName, ResourceWritePlan>(),
            ReadPlansByResource: new Dictionary<QualifiedResourceName, ResourceReadPlan>(),
            ResourceKeyIdByResource: new Dictionary<QualifiedResourceName, short>
            {
                [_studentResource] = resourceKey.ResourceKeyId,
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

    public DocumentInfo CreateDocumentInfo(
        bool includeRootSchoolReference = true,
        bool includeNestedPeriodReferences = true,
        bool includeProgramTypeDescriptor = true
    )
    {
        List<DocumentReference> documentReferences = [];

        if (includeRootSchoolReference)
        {
            documentReferences.Add(
                new DocumentReference(
                    SchoolResourceInfo,
                    new DocumentIdentity([new DocumentIdentityElement(new JsonPath("$.schoolId"), "255901")]),
                    new ReferentialId(Guid.Parse("11111111-1111-1111-1111-111111111111")),
                    new JsonPath("$.schoolReference")
                )
            );
        }

        if (includeNestedPeriodReferences)
        {
            documentReferences.Add(
                new DocumentReference(
                    SchoolResourceInfo,
                    new DocumentIdentity([new DocumentIdentityElement(new JsonPath("$.schoolId"), "255901")]),
                    new ReferentialId(Guid.Parse("33333333-3333-3333-3333-333333333333")),
                    new JsonPath("$.addresses[0].periods[0].schoolReference")
                )
            );
            documentReferences.Add(
                new DocumentReference(
                    SchoolResourceInfo,
                    new DocumentIdentity([new DocumentIdentityElement(new JsonPath("$.schoolId"), "255902")]),
                    new ReferentialId(Guid.Parse("44444444-4444-4444-4444-444444444444")),
                    new JsonPath("$.addresses[0].periods[1].schoolReference")
                )
            );
        }

        List<DescriptorReference> descriptorReferences = [];

        if (includeProgramTypeDescriptor)
        {
            descriptorReferences.Add(
                new DescriptorReference(
                    ProgramTypeDescriptorResourceInfo,
                    new DocumentIdentity([
                        new DocumentIdentityElement(
                            DocumentIdentity.DescriptorIdentityJsonPath,
                            "uri://ed-fi.org/programtypedescriptor#stem"
                        ),
                    ]),
                    new ReferentialId(Guid.Parse("22222222-2222-2222-2222-222222222222")),
                    new JsonPath("$.programTypeDescriptor")
                )
            );
        }

        return new DocumentInfo(
            DocumentIdentity: new DocumentIdentity([
                new DocumentIdentityElement(new JsonPath("$.studentUniqueId"), "1000"),
            ]),
            ReferentialId: new ReferentialId(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb")),
            DocumentReferences: [.. documentReferences],
            DocumentReferenceArrays: [],
            DescriptorReferences: [.. descriptorReferences],
            SuperclassIdentity: null
        );
    }

    public static ResolvedReferenceSet CreateEmptyResolvedReferences()
    {
        return new ResolvedReferenceSet(
            SuccessfulDocumentReferencesByPath: new Dictionary<JsonPath, ResolvedDocumentReference>(),
            SuccessfulDescriptorReferencesByPath: new Dictionary<JsonPath, ResolvedDescriptorReference>(),
            LookupsByReferentialId: new Dictionary<ReferentialId, ReferenceLookupSnapshot>(),
            InvalidDocumentReferences: [],
            InvalidDescriptorReferences: [],
            DocumentReferenceOccurrences: [],
            DescriptorReferenceOccurrences: []
        );
    }

    public static ResolvedReferenceSet CreateReferenceFailureSet(
        IEnumerable<DocumentReferenceFailure>? invalidDocumentReferences = null,
        IEnumerable<DescriptorReferenceFailure>? invalidDescriptorReferences = null
    )
    {
        return new ResolvedReferenceSet(
            SuccessfulDocumentReferencesByPath: new Dictionary<JsonPath, ResolvedDocumentReference>(),
            SuccessfulDescriptorReferencesByPath: new Dictionary<JsonPath, ResolvedDescriptorReference>(),
            LookupsByReferentialId: new Dictionary<ReferentialId, ReferenceLookupSnapshot>(),
            InvalidDocumentReferences: [.. invalidDocumentReferences ?? []],
            InvalidDescriptorReferences: [.. invalidDescriptorReferences ?? []],
            DocumentReferenceOccurrences: [],
            DescriptorReferenceOccurrences: []
        );
    }

    public DocumentReference CreateRootSchoolReference()
    {
        return CreateDocumentInfo(
            includeRootSchoolReference: true,
            includeNestedPeriodReferences: false,
            includeProgramTypeDescriptor: false
        )
            .DocumentReferences.Single();
    }

    private static ResolvedReferenceSet CreateResolvedReferences()
    {
        var schoolResourceInfo = new BaseResourceInfo(
            new ProjectName("Ed-Fi"),
            new ResourceName("School"),
            false
        );
        var programTypeDescriptorResourceInfo = new BaseResourceInfo(
            new ProjectName("Ed-Fi"),
            new ResourceName("ProgramTypeDescriptor"),
            true
        );

        return new ResolvedReferenceSet(
            SuccessfulDocumentReferencesByPath: new Dictionary<JsonPath, ResolvedDocumentReference>
            {
                [new JsonPath("$.schoolReference")] = new ResolvedDocumentReference(
                    new DocumentReference(
                        schoolResourceInfo,
                        new DocumentIdentity([
                            new DocumentIdentityElement(new JsonPath("$.schoolId"), "255901"),
                        ]),
                        new ReferentialId(Guid.Parse("11111111-1111-1111-1111-111111111111")),
                        new JsonPath("$.schoolReference")
                    ),
                    DocumentId: 901L,
                    ResourceKeyId: 21
                ),
                [new JsonPath("$.addresses[0].periods[0].schoolReference")] = new ResolvedDocumentReference(
                    new DocumentReference(
                        schoolResourceInfo,
                        new DocumentIdentity([
                            new DocumentIdentityElement(new JsonPath("$.schoolId"), "255901"),
                        ]),
                        new ReferentialId(Guid.Parse("33333333-3333-3333-3333-333333333333")),
                        new JsonPath("$.addresses[0].periods[0].schoolReference")
                    ),
                    DocumentId: 9901L,
                    ResourceKeyId: 21
                ),
                [new JsonPath("$.addresses[0].periods[1].schoolReference")] = new ResolvedDocumentReference(
                    new DocumentReference(
                        schoolResourceInfo,
                        new DocumentIdentity([
                            new DocumentIdentityElement(new JsonPath("$.schoolId"), "255902"),
                        ]),
                        new ReferentialId(Guid.Parse("44444444-4444-4444-4444-444444444444")),
                        new JsonPath("$.addresses[0].periods[1].schoolReference")
                    ),
                    DocumentId: 9902L,
                    ResourceKeyId: 21
                ),
            },
            SuccessfulDescriptorReferencesByPath: new Dictionary<JsonPath, ResolvedDescriptorReference>
            {
                [new JsonPath("$.programTypeDescriptor")] = new ResolvedDescriptorReference(
                    new DescriptorReference(
                        programTypeDescriptorResourceInfo,
                        new DocumentIdentity([
                            new DocumentIdentityElement(
                                DocumentIdentity.DescriptorIdentityJsonPath,
                                "uri://ed-fi.org/programtypedescriptor#stem"
                            ),
                        ]),
                        new ReferentialId(Guid.Parse("22222222-2222-2222-2222-222222222222")),
                        new JsonPath("$.programTypeDescriptor")
                    ),
                    DocumentId: 77L,
                    ResourceKeyId: 31
                ),
            },
            LookupsByReferentialId: new Dictionary<ReferentialId, ReferenceLookupSnapshot>(),
            InvalidDocumentReferences: [],
            InvalidDescriptorReferences: [],
            DocumentReferenceOccurrences: [],
            DescriptorReferenceOccurrences: []
        );
    }

    private static ResourceKeyEntry CreateResourceKeyEntry()
    {
        return new ResourceKeyEntry(
            ResourceKeyId: 1,
            Resource: _studentResource,
            ResourceVersion: "1.0.0",
            IsAbstractResource: false
        );
    }

    private DerivedRelationalModelSet CreateDerivedModelSet(SqlDialect dialect, ResourceKeyEntry resourceKey)
    {
        return new DerivedRelationalModelSet(
            EffectiveSchema: new EffectiveSchemaInfo(
                ApiSchemaFormatVersion: "1.0",
                RelationalMappingVersion: "v1",
                EffectiveSchemaHash: "relational-write-seam",
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
                new ConcreteResourceModel(resourceKey, WritePlan.Model.StorageKind, WritePlan.Model),
            ],
            AbstractIdentityTablesInNameOrder: [],
            AbstractUnionViewsInNameOrder: [],
            IndexesInCreateOrder: [],
            TriggersInCreateOrder: []
        );
    }

    private ResourceReadPlan CreateReadPlan(SqlDialect dialect)
    {
        var existingDocumentReadModel = WritePlan.Model with
        {
            TablesInDependencyOrder = [WritePlan.Model.Root],
            DocumentReferenceBindings = [],
            DescriptorEdgeSources = [],
        };

        return new ReadPlanCompiler(dialect).Compile(existingDocumentReadModel);
    }

    private static TableWritePlan CreateRootPlan()
    {
        var tableModel = new DbTableModel(
            Table: new DbTableName(new DbSchemaName("edfi"), "Student"),
            JsonScope: CreatePath("$"),
            Key: new TableKey(
                ConstraintName: "PK_Student",
                Columns: [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            Columns:
            [
                CreateColumn("DocumentId", ColumnKind.ParentKeyPart, null, false),
                CreateColumn(
                    "SchoolYear",
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32),
                    true,
                    CreatePath("$.schoolYear", new JsonPathSegment.Property("schoolYear"))
                ),
                CreateColumn(
                    "LastModified",
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.DateTime),
                    true,
                    CreatePath("$.lastModified", new JsonPathSegment.Property("lastModified"))
                ),
                CreateColumn(
                    "MeetingTime",
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Time),
                    true,
                    CreatePath("$.meetingTime", new JsonPathSegment.Property("meetingTime"))
                ),
                CreateColumn(
                    "School_DocumentId",
                    ColumnKind.DocumentFk,
                    null,
                    true,
                    targetResource: _schoolResource
                ),
                CreateColumn(
                    "ProgramTypeDescriptorId",
                    ColumnKind.DescriptorFk,
                    null,
                    true,
                    targetResource: _programTypeDescriptorResource
                ),
            ],
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Root,
                PhysicalRowIdentityColumns: [new DbColumnName("DocumentId")],
                RootScopeLocatorColumns: [new DbColumnName("DocumentId")],
                ImmediateParentScopeLocatorColumns: [],
                SemanticIdentityBindings: []
            ),
        };

        return new TableWritePlan(
            TableModel: tableModel,
            InsertSql: "insert into edfi.\"Student\" values (...)",
            UpdateSql: "update edfi.\"Student\" set ...",
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, tableModel.Columns.Count, 1000),
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
                        CreatePath("$.schoolYear", new JsonPathSegment.Property("schoolYear")),
                        new RelationalScalarType(ScalarKind.Int32)
                    ),
                    "SchoolYear"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[2],
                    new WriteValueSource.Scalar(
                        CreatePath("$.lastModified", new JsonPathSegment.Property("lastModified")),
                        new RelationalScalarType(ScalarKind.DateTime)
                    ),
                    "LastModified"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[3],
                    new WriteValueSource.Scalar(
                        CreatePath("$.meetingTime", new JsonPathSegment.Property("meetingTime")),
                        new RelationalScalarType(ScalarKind.Time)
                    ),
                    "MeetingTime"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[4],
                    new WriteValueSource.DocumentReference(0),
                    "School_DocumentId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[5],
                    new WriteValueSource.DescriptorReference(
                        _programTypeDescriptorResource,
                        CreatePath(
                            "$.programTypeDescriptor",
                            new JsonPathSegment.Property("programTypeDescriptor")
                        ),
                        CreatePath(
                            "$.programTypeDescriptor",
                            new JsonPathSegment.Property("programTypeDescriptor")
                        )
                    ),
                    "ProgramTypeDescriptorId"
                ),
            ],
            KeyUnificationPlans: []
        );
    }

    private static TableWritePlan CreateRootExtensionPlan()
    {
        var tableModel = new DbTableModel(
            Table: new DbTableName(new DbSchemaName("sample"), "StudentExtension"),
            JsonScope: CreatePath(
                "$._ext.sample",
                new JsonPathSegment.Property("_ext"),
                new JsonPathSegment.Property("sample")
            ),
            Key: new TableKey(
                ConstraintName: "PK_StudentExtension",
                Columns: [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            Columns:
            [
                CreateColumn("DocumentId", ColumnKind.ParentKeyPart, null, false),
                CreateColumn(
                    "FavoriteColor",
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 30),
                    true,
                    CreatePath("$.favoriteColor", new JsonPathSegment.Property("favoriteColor"))
                ),
            ],
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.RootExtension,
                PhysicalRowIdentityColumns: [new DbColumnName("DocumentId")],
                RootScopeLocatorColumns: [new DbColumnName("DocumentId")],
                ImmediateParentScopeLocatorColumns: [new DbColumnName("DocumentId")],
                SemanticIdentityBindings: []
            ),
        };

        return new TableWritePlan(
            TableModel: tableModel,
            InsertSql: "insert into sample.\"StudentExtension\" values (...)",
            UpdateSql: "update sample.\"StudentExtension\" set ...",
            DeleteByParentSql: "delete from sample.\"StudentExtension\" where ...",
            BulkInsertBatching: new BulkInsertBatchingInfo(100, tableModel.Columns.Count, 1000),
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
                        CreatePath("$.favoriteColor", new JsonPathSegment.Property("favoriteColor")),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 30)
                    ),
                    "FavoriteColor"
                ),
            ],
            KeyUnificationPlans: []
        );
    }

    private static TableWritePlan CreateRootExtensionInterventionPlan()
    {
        var tableModel = new DbTableModel(
            Table: new DbTableName(new DbSchemaName("sample"), "StudentExtensionIntervention"),
            JsonScope: CreatePath(
                "$._ext.sample.interventions[*]",
                new JsonPathSegment.Property("_ext"),
                new JsonPathSegment.Property("sample"),
                new JsonPathSegment.Property("interventions"),
                new JsonPathSegment.AnyArrayElement()
            ),
            Key: new TableKey(
                ConstraintName: "PK_StudentExtensionIntervention",
                Columns: [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.CollectionKey)]
            ),
            Columns:
            [
                CreateColumn("CollectionItemId", ColumnKind.CollectionKey, null, false),
                CreateColumn("Student_DocumentId", ColumnKind.ParentKeyPart, null, false),
                CreateColumn("Ordinal", ColumnKind.Ordinal, null, false),
                CreateColumn(
                    "InterventionCode",
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 30),
                    false,
                    CreatePath("$.interventionCode", new JsonPathSegment.Property("interventionCode"))
                ),
            ],
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.ExtensionCollection,
                PhysicalRowIdentityColumns: [new DbColumnName("CollectionItemId")],
                RootScopeLocatorColumns: [new DbColumnName("Student_DocumentId")],
                ImmediateParentScopeLocatorColumns: [new DbColumnName("Student_DocumentId")],
                SemanticIdentityBindings:
                [
                    new CollectionSemanticIdentityBinding(
                        CreatePath("$.interventionCode", new JsonPathSegment.Property("interventionCode")),
                        new DbColumnName("InterventionCode")
                    ),
                ]
            ),
        };

        return new TableWritePlan(
            TableModel: tableModel,
            InsertSql: "insert into sample.\"StudentExtensionIntervention\" values (...)",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, tableModel.Columns.Count, 1000),
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
                    "Student_DocumentId"
                ),
                new WriteColumnBinding(tableModel.Columns[2], new WriteValueSource.Ordinal(), "Ordinal"),
                new WriteColumnBinding(
                    tableModel.Columns[3],
                    new WriteValueSource.Scalar(
                        CreatePath("$.interventionCode", new JsonPathSegment.Property("interventionCode")),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 30)
                    ),
                    "InterventionCode"
                ),
            ],
            KeyUnificationPlans: [],
            CollectionMergePlan: new CollectionMergePlan(
                SemanticIdentityBindings:
                [
                    new CollectionMergeSemanticIdentityBinding(
                        CreatePath("$.interventionCode", new JsonPathSegment.Property("interventionCode")),
                        3
                    ),
                ],
                StableRowIdentityBindingIndex: 0,
                UpdateByStableRowIdentitySql: "update sample.\"StudentExtensionIntervention\" set ...",
                DeleteByStableRowIdentitySql: "delete from sample.\"StudentExtensionIntervention\" where ...",
                OrdinalBindingIndex: 2,
                CompareBindingIndexesInOrder: [3, 2]
            ),
            CollectionKeyPreallocationPlan: new CollectionKeyPreallocationPlan(
                new DbColumnName("CollectionItemId"),
                0
            )
        );
    }

    private static TableWritePlan CreateAddressPlan()
    {
        var tableModel = new DbTableModel(
            Table: new DbTableName(new DbSchemaName("edfi"), "StudentAddress"),
            JsonScope: CreatePath(
                "$.addresses[*]",
                new JsonPathSegment.Property("addresses"),
                new JsonPathSegment.AnyArrayElement()
            ),
            Key: new TableKey(
                ConstraintName: "PK_StudentAddress",
                Columns: [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.CollectionKey)]
            ),
            Columns:
            [
                CreateColumn("CollectionItemId", ColumnKind.CollectionKey, null, false),
                CreateColumn("Student_DocumentId", ColumnKind.ParentKeyPart, null, false),
                CreateColumn("Ordinal", ColumnKind.Ordinal, null, false),
                CreateColumn(
                    "AddressType",
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 30),
                    false,
                    CreatePath("$.addressType", new JsonPathSegment.Property("addressType"))
                ),
                CreateColumn(
                    "AddressLine1",
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 75),
                    true,
                    CreatePath("$.addressLine1", new JsonPathSegment.Property("addressLine1"))
                ),
            ],
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Collection,
                PhysicalRowIdentityColumns: [new DbColumnName("CollectionItemId")],
                RootScopeLocatorColumns: [new DbColumnName("Student_DocumentId")],
                ImmediateParentScopeLocatorColumns: [new DbColumnName("Student_DocumentId")],
                SemanticIdentityBindings:
                [
                    new CollectionSemanticIdentityBinding(
                        CreatePath("$.addressType", new JsonPathSegment.Property("addressType")),
                        new DbColumnName("AddressType")
                    ),
                ]
            ),
        };

        return new TableWritePlan(
            TableModel: tableModel,
            InsertSql: "insert into edfi.\"StudentAddress\" values (...)",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, tableModel.Columns.Count, 1000),
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
                    "Student_DocumentId"
                ),
                new WriteColumnBinding(tableModel.Columns[2], new WriteValueSource.Ordinal(), "Ordinal"),
                new WriteColumnBinding(
                    tableModel.Columns[3],
                    new WriteValueSource.Scalar(
                        CreatePath("$.addressType", new JsonPathSegment.Property("addressType")),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 30)
                    ),
                    "AddressType"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[4],
                    new WriteValueSource.Scalar(
                        CreatePath("$.addressLine1", new JsonPathSegment.Property("addressLine1")),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 75)
                    ),
                    "AddressLine1"
                ),
            ],
            KeyUnificationPlans: [],
            CollectionMergePlan: new CollectionMergePlan(
                SemanticIdentityBindings:
                [
                    new CollectionMergeSemanticIdentityBinding(
                        CreatePath("$.addressType", new JsonPathSegment.Property("addressType")),
                        3
                    ),
                ],
                StableRowIdentityBindingIndex: 0,
                UpdateByStableRowIdentitySql: "update edfi.\"StudentAddress\" set ...",
                DeleteByStableRowIdentitySql: "delete from edfi.\"StudentAddress\" where ...",
                OrdinalBindingIndex: 2,
                CompareBindingIndexesInOrder: [3, 2]
            ),
            CollectionKeyPreallocationPlan: new CollectionKeyPreallocationPlan(
                new DbColumnName("CollectionItemId"),
                0
            )
        );
    }

    private static TableWritePlan CreateAddressPeriodPlan()
    {
        var tableModel = new DbTableModel(
            Table: new DbTableName(new DbSchemaName("edfi"), "StudentAddressPeriod"),
            JsonScope: CreatePath(
                "$.addresses[*].periods[*]",
                new JsonPathSegment.Property("addresses"),
                new JsonPathSegment.AnyArrayElement(),
                new JsonPathSegment.Property("periods"),
                new JsonPathSegment.AnyArrayElement()
            ),
            Key: new TableKey(
                ConstraintName: "PK_StudentAddressPeriod",
                Columns: [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.CollectionKey)]
            ),
            Columns:
            [
                CreateColumn("CollectionItemId", ColumnKind.CollectionKey, null, false),
                CreateColumn("Student_DocumentId", ColumnKind.ParentKeyPart, null, false),
                CreateColumn("Address_CollectionItemId", ColumnKind.ParentKeyPart, null, false),
                CreateColumn("Ordinal", ColumnKind.Ordinal, null, false),
                CreateColumn(
                    "BeginDate",
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Date),
                    false,
                    CreatePath("$.beginDate", new JsonPathSegment.Property("beginDate"))
                ),
                CreateColumn(
                    "School_DocumentId",
                    ColumnKind.DocumentFk,
                    null,
                    true,
                    targetResource: _schoolResource
                ),
            ],
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Collection,
                PhysicalRowIdentityColumns: [new DbColumnName("CollectionItemId")],
                RootScopeLocatorColumns: [new DbColumnName("Student_DocumentId")],
                ImmediateParentScopeLocatorColumns: [new DbColumnName("Address_CollectionItemId")],
                SemanticIdentityBindings:
                [
                    new CollectionSemanticIdentityBinding(
                        CreatePath("$.beginDate", new JsonPathSegment.Property("beginDate")),
                        new DbColumnName("BeginDate")
                    ),
                ]
            ),
        };

        return new TableWritePlan(
            TableModel: tableModel,
            InsertSql: "insert into edfi.\"StudentAddressPeriod\" values (...)",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, tableModel.Columns.Count, 1000),
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
                    "Student_DocumentId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[2],
                    new WriteValueSource.ParentKeyPart(0),
                    "Address_CollectionItemId"
                ),
                new WriteColumnBinding(tableModel.Columns[3], new WriteValueSource.Ordinal(), "Ordinal"),
                new WriteColumnBinding(
                    tableModel.Columns[4],
                    new WriteValueSource.Scalar(
                        CreatePath("$.beginDate", new JsonPathSegment.Property("beginDate")),
                        new RelationalScalarType(ScalarKind.Date)
                    ),
                    "BeginDate"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[5],
                    new WriteValueSource.DocumentReference(1),
                    "School_DocumentId"
                ),
            ],
            KeyUnificationPlans: [],
            CollectionMergePlan: new CollectionMergePlan(
                SemanticIdentityBindings:
                [
                    new CollectionMergeSemanticIdentityBinding(
                        CreatePath("$.beginDate", new JsonPathSegment.Property("beginDate")),
                        4
                    ),
                ],
                StableRowIdentityBindingIndex: 0,
                UpdateByStableRowIdentitySql: "update edfi.\"StudentAddressPeriod\" set ...",
                DeleteByStableRowIdentitySql: "delete from edfi.\"StudentAddressPeriod\" where ...",
                OrdinalBindingIndex: 3,
                CompareBindingIndexesInOrder: [4, 3]
            ),
            CollectionKeyPreallocationPlan: new CollectionKeyPreallocationPlan(
                new DbColumnName("CollectionItemId"),
                0
            )
        );
    }

    private static TableWritePlan CreateAddressExtensionPlan()
    {
        var tableModel = new DbTableModel(
            Table: new DbTableName(new DbSchemaName("sample"), "StudentExtensionAddress"),
            JsonScope: CreatePath(
                "$.addresses[*]._ext.sample",
                new JsonPathSegment.Property("addresses"),
                new JsonPathSegment.AnyArrayElement(),
                new JsonPathSegment.Property("_ext"),
                new JsonPathSegment.Property("sample")
            ),
            Key: new TableKey(
                ConstraintName: "PK_StudentExtensionAddress",
                Columns: [new DbKeyColumn(new DbColumnName("BaseCollectionItemId"), ColumnKind.ParentKeyPart)]
            ),
            Columns:
            [
                CreateColumn("BaseCollectionItemId", ColumnKind.ParentKeyPart, null, false),
                CreateColumn(
                    "FavoriteColor",
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 30),
                    true,
                    CreatePath("$.favoriteColor", new JsonPathSegment.Property("favoriteColor"))
                ),
            ],
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.CollectionExtensionScope,
                PhysicalRowIdentityColumns: [new DbColumnName("BaseCollectionItemId")],
                RootScopeLocatorColumns: [new DbColumnName("BaseCollectionItemId")],
                ImmediateParentScopeLocatorColumns: [new DbColumnName("BaseCollectionItemId")],
                SemanticIdentityBindings: []
            ),
        };

        return new TableWritePlan(
            TableModel: tableModel,
            InsertSql: "insert into sample.\"StudentExtensionAddress\" values (...)",
            UpdateSql: "update sample.\"StudentExtensionAddress\" set ...",
            DeleteByParentSql: "delete from sample.\"StudentExtensionAddress\" where ...",
            BulkInsertBatching: new BulkInsertBatchingInfo(100, tableModel.Columns.Count, 1000),
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
                        CreatePath("$.favoriteColor", new JsonPathSegment.Property("favoriteColor")),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 30)
                    ),
                    "FavoriteColor"
                ),
            ],
            KeyUnificationPlans: []
        );
    }

    private static TableWritePlan CreateAddressExtensionServicePlan()
    {
        var tableModel = new DbTableModel(
            Table: new DbTableName(new DbSchemaName("sample"), "StudentExtensionAddressService"),
            JsonScope: CreatePath(
                "$.addresses[*]._ext.sample.services[*]",
                new JsonPathSegment.Property("addresses"),
                new JsonPathSegment.AnyArrayElement(),
                new JsonPathSegment.Property("_ext"),
                new JsonPathSegment.Property("sample"),
                new JsonPathSegment.Property("services"),
                new JsonPathSegment.AnyArrayElement()
            ),
            Key: new TableKey(
                ConstraintName: "PK_StudentExtensionAddressService",
                Columns: [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.CollectionKey)]
            ),
            Columns:
            [
                CreateColumn("CollectionItemId", ColumnKind.CollectionKey, null, false),
                CreateColumn("Student_DocumentId", ColumnKind.ParentKeyPart, null, false),
                CreateColumn("BaseCollectionItemId", ColumnKind.ParentKeyPart, null, false),
                CreateColumn("Ordinal", ColumnKind.Ordinal, null, false),
                CreateColumn(
                    "ServiceName",
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 30),
                    false,
                    CreatePath("$.serviceName", new JsonPathSegment.Property("serviceName"))
                ),
            ],
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.ExtensionCollection,
                PhysicalRowIdentityColumns: [new DbColumnName("CollectionItemId")],
                RootScopeLocatorColumns: [new DbColumnName("Student_DocumentId")],
                ImmediateParentScopeLocatorColumns: [new DbColumnName("BaseCollectionItemId")],
                SemanticIdentityBindings:
                [
                    new CollectionSemanticIdentityBinding(
                        CreatePath("$.serviceName", new JsonPathSegment.Property("serviceName")),
                        new DbColumnName("ServiceName")
                    ),
                ]
            ),
        };

        return new TableWritePlan(
            TableModel: tableModel,
            InsertSql: "insert into sample.\"StudentExtensionAddressService\" values (...)",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, tableModel.Columns.Count, 1000),
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
                    "Student_DocumentId"
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
                        CreatePath("$.serviceName", new JsonPathSegment.Property("serviceName")),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 30)
                    ),
                    "ServiceName"
                ),
            ],
            KeyUnificationPlans: [],
            CollectionMergePlan: new CollectionMergePlan(
                SemanticIdentityBindings:
                [
                    new CollectionMergeSemanticIdentityBinding(
                        CreatePath("$.serviceName", new JsonPathSegment.Property("serviceName")),
                        4
                    ),
                ],
                StableRowIdentityBindingIndex: 0,
                UpdateByStableRowIdentitySql: "update sample.\"StudentExtensionAddressService\" set ...",
                DeleteByStableRowIdentitySql: "delete from sample.\"StudentExtensionAddressService\" where ...",
                OrdinalBindingIndex: 3,
                CompareBindingIndexesInOrder: [4, 3]
            ),
            CollectionKeyPreallocationPlan: new CollectionKeyPreallocationPlan(
                new DbColumnName("CollectionItemId"),
                0
            )
        );
    }

    private static DbColumnModel CreateColumn(
        string columnName,
        ColumnKind kind,
        RelationalScalarType? scalarType,
        bool isNullable,
        JsonPathExpression? sourceJsonPath = null,
        QualifiedResourceName? targetResource = null
    )
    {
        return new DbColumnModel(
            ColumnName: new DbColumnName(columnName),
            Kind: kind,
            ScalarType: scalarType,
            IsNullable: isNullable,
            SourceJsonPath: sourceJsonPath,
            TargetResource: targetResource,
            Storage: new ColumnStorage.Stored()
        );
    }

    private static JsonPathExpression CreatePath(string canonical, params JsonPathSegment[] segments)
    {
        return new JsonPathExpression(canonical, segments);
    }
}
