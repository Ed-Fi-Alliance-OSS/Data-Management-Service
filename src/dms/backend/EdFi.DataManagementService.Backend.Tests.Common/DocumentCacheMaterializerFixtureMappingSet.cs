// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;

namespace EdFi.DataManagementService.Backend.Tests.Common;

internal static class DocumentCacheMaterializerFixtureMappingSet
{
    private static readonly QualifiedResourceName SchoolResource = new("Ed-Fi", "School");
    private static readonly QualifiedResourceName StudentResource = new("Ed-Fi", "Student");
    private static readonly QualifiedResourceName StudentSchoolAssociationResource = new(
        "Ed-Fi",
        "StudentSchoolAssociation"
    );
    private static readonly QualifiedResourceName SchoolTypeDescriptorResource = new(
        "Ed-Fi",
        "SchoolTypeDescriptor"
    );
    private static readonly QualifiedResourceName EntryGradeLevelDescriptorResource = new(
        "Ed-Fi",
        "GradeLevelDescriptor"
    );
    private static readonly QualifiedResourceName EducationPlanDescriptorResource = new(
        "Ed-Fi",
        "EducationPlanDescriptor"
    );
    private static readonly QualifiedResourceName MembershipTypeDescriptorResource = new(
        "Sample",
        "MembershipTypeDescriptor"
    );

    public static MappingSet CreateDescriptorFixture(SqlDialect dialect)
    {
        var descriptorKey = new ResourceKeyEntry(13, SchoolTypeDescriptorResource, "1.0", false);
        var descriptorModel = CreateConcreteModel(
            descriptorKey,
            ResourceStorageKind.SharedDescriptorTable,
            CreateDescriptorRelationalModel(SchoolTypeDescriptorResource)
        );

        return CreateMappingSet(
            dialect,
            effectiveSchemaHash: "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            concreteModels: [descriptorModel],
            readPlansByResource: new Dictionary<QualifiedResourceName, ResourceReadPlan>()
        );
    }

    public static MappingSet CreateExtensionFixture(SqlDialect dialect)
    {
        var studentSchoolAssociationReadPlan = new ReadPlanCompiler(dialect).Compile(
            CreateStudentSchoolAssociationRelationalModel()
        );
        var studentSchoolAssociationKey = new ResourceKeyEntry(
            310,
            StudentSchoolAssociationResource,
            "5.2.0",
            false
        );
        var schoolKey = new ResourceKeyEntry(244, SchoolResource, "5.2.0", false);
        var studentKey = new ResourceKeyEntry(282, StudentResource, "5.2.0", false);
        var membershipDescriptorKey = new ResourceKeyEntry(
            356,
            MembershipTypeDescriptorResource,
            "5.2.0",
            false
        );
        var educationPlanDescriptorKey = new ResourceKeyEntry(
            103,
            EducationPlanDescriptorResource,
            "5.2.0",
            false
        );
        var entryGradeLevelDescriptorKey = new ResourceKeyEntry(
            123,
            EntryGradeLevelDescriptorResource,
            "5.2.0",
            false
        );

        return CreateMappingSet(
            dialect,
            effectiveSchemaHash: "53ba4ec60123456789abcdef0123456789abcdef0123456789abcdef01234567",
            concreteModels:
            [
                CreateConcreteModel(
                    studentSchoolAssociationKey,
                    ResourceStorageKind.RelationalTables,
                    studentSchoolAssociationReadPlan.Model
                ),
                CreateConcreteModel(schoolKey, ResourceStorageKind.RelationalTables, CreateSchoolModel()),
                CreateConcreteModel(studentKey, ResourceStorageKind.RelationalTables, CreateStudentModel()),
                CreateConcreteModel(
                    membershipDescriptorKey,
                    ResourceStorageKind.SharedDescriptorTable,
                    CreateDescriptorRelationalModel(MembershipTypeDescriptorResource)
                ),
                CreateConcreteModel(
                    educationPlanDescriptorKey,
                    ResourceStorageKind.SharedDescriptorTable,
                    CreateDescriptorRelationalModel(EducationPlanDescriptorResource)
                ),
                CreateConcreteModel(
                    entryGradeLevelDescriptorKey,
                    ResourceStorageKind.SharedDescriptorTable,
                    CreateDescriptorRelationalModel(EntryGradeLevelDescriptorResource)
                ),
            ],
            readPlansByResource: new Dictionary<QualifiedResourceName, ResourceReadPlan>
            {
                [StudentSchoolAssociationResource] = studentSchoolAssociationReadPlan,
            }
        );
    }

    public static MappingSet CreateMissingSchoolBodyFixture(SqlDialect dialect)
    {
        var schoolReadPlan = new ReadPlanCompiler(dialect).Compile(CreateSchoolModel());
        var schoolKey = new ResourceKeyEntry(244, SchoolResource, "5.2.0", false);

        return CreateMappingSet(
            dialect,
            effectiveSchemaHash: "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            concreteModels:
            [
                CreateConcreteModel(schoolKey, ResourceStorageKind.RelationalTables, schoolReadPlan.Model),
            ],
            readPlansByResource: new Dictionary<QualifiedResourceName, ResourceReadPlan>
            {
                [SchoolResource] = schoolReadPlan,
            }
        );
    }

    public static MappingSet CreateWithoutSchoolResourceKey(SqlDialect dialect)
    {
        var descriptorKey = new ResourceKeyEntry(13, SchoolTypeDescriptorResource, "1.0", false);

        return CreateMappingSet(
            dialect,
            effectiveSchemaHash: "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            concreteModels:
            [
                CreateConcreteModel(
                    descriptorKey,
                    ResourceStorageKind.SharedDescriptorTable,
                    CreateDescriptorRelationalModel(SchoolTypeDescriptorResource)
                ),
            ],
            readPlansByResource: new Dictionary<QualifiedResourceName, ResourceReadPlan>()
        );
    }

    public static MappingSet CreateSchoolResourceWithoutReadPlan(SqlDialect dialect)
    {
        var schoolKey = new ResourceKeyEntry(244, SchoolResource, "5.2.0", false);

        return CreateMappingSet(
            dialect,
            effectiveSchemaHash: "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            concreteModels:
            [
                CreateConcreteModel(schoolKey, ResourceStorageKind.RelationalTables, CreateSchoolModel()),
            ],
            readPlansByResource: new Dictionary<QualifiedResourceName, ResourceReadPlan>()
        );
    }

    private static MappingSet CreateMappingSet(
        SqlDialect dialect,
        string effectiveSchemaHash,
        IReadOnlyList<ConcreteResourceModel> concreteModels,
        IReadOnlyDictionary<QualifiedResourceName, ResourceReadPlan> readPlansByResource
    )
    {
        var resourceKeys = concreteModels
            .Select(model => model.ResourceKey)
            .OrderBy(key => key.ResourceKeyId)
            .ToArray();
        var effectiveSchema = new EffectiveSchemaInfo(
            ApiSchemaFormatVersion: "1.0",
            RelationalMappingVersion: "v1",
            EffectiveSchemaHash: effectiveSchemaHash,
            ResourceKeyCount: checked((short)resourceKeys.Length),
            ResourceKeySeedHash: new byte[32],
            SchemaComponentsInEndpointOrder: [],
            ResourceKeysInIdOrder: resourceKeys
        );

        return new MappingSet(
            new MappingSetKey(effectiveSchema.EffectiveSchemaHash, dialect, "v1"),
            new DerivedRelationalModelSet(
                effectiveSchema,
                dialect,
                ProjectSchemasInEndpointOrder: [],
                ConcreteResourcesInNameOrder:
                [
                    .. concreteModels.OrderBy(
                        model =>
                            model.ResourceKey.Resource.ProjectName
                            + "."
                            + model.ResourceKey.Resource.ResourceName,
                        StringComparer.Ordinal
                    ),
                ],
                AbstractIdentityTablesInNameOrder: [],
                AbstractUnionViewsInNameOrder: [],
                IndexesInCreateOrder: [],
                TriggersInCreateOrder: []
            ),
            WritePlansByResource: new Dictionary<QualifiedResourceName, ResourceWritePlan>(),
            ReadPlansByResource: readPlansByResource,
            ResourceKeyIdByResource: resourceKeys.ToDictionary(key => key.Resource, key => key.ResourceKeyId),
            ResourceKeyById: resourceKeys.ToDictionary(key => key.ResourceKeyId),
            SecurableElementColumnPathsByResource: new Dictionary<
                QualifiedResourceName,
                IReadOnlyList<ResolvedSecurableElementPath>
            >()
        );
    }

    private static ConcreteResourceModel CreateConcreteModel(
        ResourceKeyEntry resourceKey,
        ResourceStorageKind storageKind,
        RelationalResourceModel relationalModel
    ) => new(resourceKey, storageKind, relationalModel);

    private static RelationalResourceModel CreateSchoolModel()
    {
        var root = new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), "School"),
            JsonPath("$"),
            new TableKey(
                "PK_School",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            [
                ParentDocumentIdColumn(),
                ScalarColumn("SchoolId", ScalarKind.Int32, "$.schoolId"),
                ScalarColumn("NameOfInstitution", ScalarKind.String, "$.nameOfInstitution"),
            ],
            []
        )
        {
            IdentityMetadata = RootIdentityMetadata(),
        };

        return new RelationalResourceModel(
            SchoolResource,
            new DbSchemaName("edfi"),
            ResourceStorageKind.RelationalTables,
            root,
            [root],
            [],
            []
        );
    }

    private static RelationalResourceModel CreateStudentModel()
    {
        var root = new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), "Student"),
            JsonPath("$"),
            new TableKey(
                "PK_Student",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            [
                ParentDocumentIdColumn(),
                ScalarColumn("StudentUniqueId", ScalarKind.String, "$.studentUniqueId"),
                ScalarColumn("FirstName", ScalarKind.String, "$.firstName"),
                ScalarColumn("LastSurname", ScalarKind.String, "$.lastSurname"),
            ],
            []
        )
        {
            IdentityMetadata = RootIdentityMetadata(),
        };

        return new RelationalResourceModel(
            StudentResource,
            new DbSchemaName("edfi"),
            ResourceStorageKind.RelationalTables,
            root,
            [root],
            [],
            []
        );
    }

    private static RelationalResourceModel CreateStudentSchoolAssociationRelationalModel()
    {
        var root = CreateStudentSchoolAssociationRootTable();
        var educationPlan = CreateEducationPlanTable();
        var extension = CreateStudentSchoolAssociationExtensionTable();

        return new RelationalResourceModel(
            StudentSchoolAssociationResource,
            new DbSchemaName("edfi"),
            ResourceStorageKind.RelationalTables,
            root,
            [root, educationPlan, extension],
            CreateStudentSchoolAssociationReferenceBindings(root.Table),
            CreateStudentSchoolAssociationDescriptorEdges(root.Table, educationPlan.Table, extension.Table)
        );
    }

    private static DbTableModel CreateStudentSchoolAssociationRootTable() =>
        new(
            new DbTableName(new DbSchemaName("edfi"), "StudentSchoolAssociation"),
            JsonPath("$"),
            new TableKey(
                "PK_StudentSchoolAssociation",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            [
                ParentDocumentIdColumn(),
                DocumentFkColumn("School_DocumentId", "$.schoolReference", SchoolResource),
                ScalarColumn("School_SchoolId", ScalarKind.Int32, "$.schoolReference.schoolId"),
                DocumentFkColumn("Student_DocumentId", "$.studentReference", StudentResource),
                ScalarColumn(
                    "Student_StudentUniqueId",
                    ScalarKind.String,
                    "$.studentReference.studentUniqueId"
                ),
                DescriptorColumn(
                    "EntryGradeLevelDescriptor_DescriptorId",
                    "$.entryGradeLevelDescriptor",
                    EntryGradeLevelDescriptorResource
                ),
                ScalarColumn("EntryDate", ScalarKind.Date, "$.entryDate"),
                ScalarColumn("PrimarySchool", ScalarKind.Boolean, "$.primarySchool"),
            ],
            []
        )
        {
            IdentityMetadata = RootIdentityMetadata(),
        };

    private static DbTableModel CreateEducationPlanTable() =>
        new(
            new DbTableName(new DbSchemaName("edfi"), "StudentSchoolAssociationEducationPlan"),
            JsonPath(
                "$.educationPlans[*]",
                new JsonPathSegment.Property("educationPlans"),
                new JsonPathSegment.AnyArrayElement()
            ),
            new TableKey(
                "PK_StudentSchoolAssociationEducationPlan",
                [
                    new DbKeyColumn(
                        new DbColumnName("StudentSchoolAssociation_DocumentId"),
                        ColumnKind.ParentKeyPart
                    ),
                    new DbKeyColumn(new DbColumnName("Ordinal"), ColumnKind.Ordinal),
                ]
            ),
            [
                CollectionItemIdColumn(),
                ParentDocumentIdColumn("StudentSchoolAssociation_DocumentId"),
                OrdinalColumn(),
                DescriptorColumn(
                    "EducationPlanDescriptor_DescriptorId",
                    "$.educationPlans[*].educationPlanDescriptor",
                    EducationPlanDescriptorResource
                ),
            ],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.Collection,
                [new DbColumnName("CollectionItemId")],
                [new DbColumnName("StudentSchoolAssociation_DocumentId")],
                [new DbColumnName("StudentSchoolAssociation_DocumentId")],
                []
            ),
        };

    private static DbTableModel CreateStudentSchoolAssociationExtensionTable() =>
        new(
            new DbTableName(new DbSchemaName("sample"), "StudentSchoolAssociationExtension"),
            JsonPath(
                "$._ext.sample",
                new JsonPathSegment.Property("_ext"),
                new JsonPathSegment.Property("sample")
            ),
            new TableKey(
                "PK_StudentSchoolAssociationExtension",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            [
                ParentDocumentIdColumn(),
                DescriptorColumn(
                    "MembershipTypeDescriptor_DescriptorId",
                    "$._ext.sample.membershipTypeDescriptor",
                    MembershipTypeDescriptorResource
                ),
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

    private static IReadOnlyList<DocumentReferenceBinding> CreateStudentSchoolAssociationReferenceBindings(
        DbTableName rootTable
    )
    {
        var schoolReferencePath = JsonPath(
            "$.schoolReference",
            new JsonPathSegment.Property("schoolReference")
        );
        var schoolIdPath = JsonPath(
            "$.schoolReference.schoolId",
            new JsonPathSegment.Property("schoolReference"),
            new JsonPathSegment.Property("schoolId")
        );
        var studentReferencePath = JsonPath(
            "$.studentReference",
            new JsonPathSegment.Property("studentReference")
        );
        var studentUniqueIdPath = JsonPath(
            "$.studentReference.studentUniqueId",
            new JsonPathSegment.Property("studentReference"),
            new JsonPathSegment.Property("studentUniqueId")
        );

        return
        [
            new DocumentReferenceBinding(
                IsIdentityComponent: true,
                ReferenceObjectPath: schoolReferencePath,
                Table: rootTable,
                FkColumn: new DbColumnName("School_DocumentId"),
                TargetResource: SchoolResource,
                IdentityBindings:
                [
                    new ReferenceIdentityBinding(
                        IdentityJsonPath: schoolIdPath,
                        ReferenceJsonPath: schoolIdPath,
                        Column: new DbColumnName("School_SchoolId")
                    ),
                ]
            ),
            new DocumentReferenceBinding(
                IsIdentityComponent: true,
                ReferenceObjectPath: studentReferencePath,
                Table: rootTable,
                FkColumn: new DbColumnName("Student_DocumentId"),
                TargetResource: StudentResource,
                IdentityBindings:
                [
                    new ReferenceIdentityBinding(
                        IdentityJsonPath: studentUniqueIdPath,
                        ReferenceJsonPath: studentUniqueIdPath,
                        Column: new DbColumnName("Student_StudentUniqueId")
                    ),
                ]
            ),
        ];
    }

    private static IReadOnlyList<DescriptorEdgeSource> CreateStudentSchoolAssociationDescriptorEdges(
        DbTableName rootTable,
        DbTableName educationPlanTable,
        DbTableName extensionTable
    ) =>
        [
            new(
                IsIdentityComponent: false,
                DescriptorValuePath: JsonPath(
                    "$.entryGradeLevelDescriptor",
                    new JsonPathSegment.Property("entryGradeLevelDescriptor")
                ),
                Table: rootTable,
                FkColumn: new DbColumnName("EntryGradeLevelDescriptor_DescriptorId"),
                DescriptorResource: EntryGradeLevelDescriptorResource
            ),
            new(
                IsIdentityComponent: false,
                DescriptorValuePath: JsonPath(
                    "$.educationPlans[*].educationPlanDescriptor",
                    new JsonPathSegment.Property("educationPlans"),
                    new JsonPathSegment.AnyArrayElement(),
                    new JsonPathSegment.Property("educationPlanDescriptor")
                ),
                Table: educationPlanTable,
                FkColumn: new DbColumnName("EducationPlanDescriptor_DescriptorId"),
                DescriptorResource: EducationPlanDescriptorResource
            ),
            new(
                IsIdentityComponent: false,
                DescriptorValuePath: JsonPath(
                    "$._ext.sample.membershipTypeDescriptor",
                    new JsonPathSegment.Property("_ext"),
                    new JsonPathSegment.Property("sample"),
                    new JsonPathSegment.Property("membershipTypeDescriptor")
                ),
                Table: extensionTable,
                FkColumn: new DbColumnName("MembershipTypeDescriptor_DescriptorId"),
                DescriptorResource: MembershipTypeDescriptorResource
            ),
        ];

    private static RelationalResourceModel CreateDescriptorRelationalModel(QualifiedResourceName resource)
    {
        var descriptorTable = new DbTableModel(
            new DbTableName(new DbSchemaName("dms"), "Descriptor"),
            JsonPath("$"),
            new TableKey(
                "PK_Descriptor",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            [ParentDocumentIdColumn()],
            []
        )
        {
            IdentityMetadata = RootIdentityMetadata(),
        };

        return new RelationalResourceModel(
            resource,
            new DbSchemaName("dms"),
            ResourceStorageKind.SharedDescriptorTable,
            descriptorTable,
            [descriptorTable],
            [],
            []
        );
    }

    private static DbColumnModel ParentDocumentIdColumn(string name = "DocumentId") =>
        new(
            new DbColumnName(name),
            ColumnKind.ParentKeyPart,
            new RelationalScalarType(ScalarKind.Int64),
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null
        );

    private static DbColumnModel CollectionItemIdColumn() =>
        new(
            new DbColumnName("CollectionItemId"),
            ColumnKind.CollectionKey,
            new RelationalScalarType(ScalarKind.Int64),
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null
        );

    private static DbColumnModel OrdinalColumn() =>
        new(
            new DbColumnName("Ordinal"),
            ColumnKind.Ordinal,
            new RelationalScalarType(ScalarKind.Int32),
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null
        );

    private static DbColumnModel ScalarColumn(string name, ScalarKind kind, string path) =>
        new(
            new DbColumnName(name),
            ColumnKind.Scalar,
            new RelationalScalarType(kind),
            IsNullable: true,
            SourceJsonPath: JsonPath(path),
            TargetResource: null
        );

    private static DbColumnModel DocumentFkColumn(
        string name,
        string path,
        QualifiedResourceName targetResource
    ) =>
        new(
            new DbColumnName(name),
            ColumnKind.DocumentFk,
            new RelationalScalarType(ScalarKind.Int64),
            IsNullable: true,
            SourceJsonPath: JsonPath(path),
            TargetResource: targetResource
        );

    private static DbColumnModel DescriptorColumn(
        string name,
        string path,
        QualifiedResourceName targetResource
    ) =>
        new(
            new DbColumnName(name),
            ColumnKind.DescriptorFk,
            new RelationalScalarType(ScalarKind.Int64),
            IsNullable: true,
            SourceJsonPath: JsonPath(path),
            TargetResource: targetResource
        );

    private static DbTableIdentityMetadata RootIdentityMetadata() =>
        new(DbTableKind.Root, [new DbColumnName("DocumentId")], [new DbColumnName("DocumentId")], [], []);

    private static JsonPathExpression JsonPath(string canonical, params JsonPathSegment[] segments)
    {
        if (segments.Length == 0 && canonical != "$")
        {
            segments = canonical
                .TrimStart('$', '.')
                .Split('.', StringSplitOptions.RemoveEmptyEntries)
                .Select<string, JsonPathSegment>(segment => new JsonPathSegment.Property(segment))
                .ToArray();
        }

        return new JsonPathExpression(canonical, segments);
    }
}
