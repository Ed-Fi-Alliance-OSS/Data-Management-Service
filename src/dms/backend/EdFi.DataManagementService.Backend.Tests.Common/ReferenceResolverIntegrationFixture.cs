// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.Tests.Common;

public sealed class ReferenceResolverIntegrationFixture
{
    private static readonly DbSchemaName _edFiSchema = new("edfi");

    private ReferenceResolverIntegrationFixture(
        ReferenceResolverSeedData seedData,
        QualifiedResourceName schoolTypeDescriptorResource,
        QualifiedResourceName academicSubjectDescriptorResource
    )
    {
        SeedData = seedData;
        SchoolTypeDescriptorResource = schoolTypeDescriptorResource;
        AcademicSubjectDescriptorResource = academicSubjectDescriptorResource;
    }

    public ReferenceResolverSeedData SeedData { get; }

    public QualifiedResourceName SchoolTypeDescriptorResource { get; }

    public QualifiedResourceName AcademicSubjectDescriptorResource { get; }

    public static ReferenceResolverIntegrationFixture CreateDefault()
    {
        var requestResource = new QualifiedResourceName("Ed-Fi", "Student");
        var schoolResource = new QualifiedResourceName("Ed-Fi", "School");
        var localEducationAgencyResource = new QualifiedResourceName("Ed-Fi", "LocalEducationAgency");
        var educationOrganizationResource = new QualifiedResourceName("Ed-Fi", "EducationOrganization");
        var schoolTypeDescriptorResource = new QualifiedResourceName("Ed-Fi", "SchoolTypeDescriptor");
        var academicSubjectDescriptorResource = new QualifiedResourceName(
            "Ed-Fi",
            "AcademicSubjectDescriptor"
        );

        var schoolReferentialId = CreateReferentialId("00000000-0000-0000-0000-000000000111");
        var educationOrganizationAliasReferentialId = CreateReferentialId(
            "00000000-0000-0000-0000-000000000222"
        );
        var localEducationAgencyReferentialId = CreateReferentialId("00000000-0000-0000-0000-000000000333");
        var schoolTypeDescriptorReferentialId = CreateReferentialId("00000000-0000-0000-0000-000000000444");
        var academicSubjectDescriptorReferentialId = CreateReferentialId(
            "00000000-0000-0000-0000-000000000555"
        );

        const string SchoolTypeDescriptorUri = "uri://ed-fi.org/SchoolTypeDescriptor#Alternative";
        const string AcademicSubjectDescriptorUri = "uri://ed-fi.org/AcademicSubjectDescriptor#English";

        return new(
            seedData: new ReferenceResolverSeedData(
                ResourceKeys:
                [
                    new ReferenceResolverResourceKeySeed(1, requestResource, "1.0", false),
                    new ReferenceResolverResourceKeySeed(11, schoolResource, "1.0", false),
                    new ReferenceResolverResourceKeySeed(12, localEducationAgencyResource, "1.0", false),
                    new ReferenceResolverResourceKeySeed(13, schoolTypeDescriptorResource, "1.0", false),
                    new ReferenceResolverResourceKeySeed(14, academicSubjectDescriptorResource, "1.0", false),
                    new ReferenceResolverResourceKeySeed(30, educationOrganizationResource, "1.0", true),
                ],
                Documents:
                [
                    new ReferenceResolverDocumentSeed(
                        101,
                        Guid.Parse("10000000-0000-0000-0000-000000000101"),
                        11
                    ),
                    new ReferenceResolverDocumentSeed(
                        202,
                        Guid.Parse("20000000-0000-0000-0000-000000000202"),
                        12
                    ),
                    new ReferenceResolverDocumentSeed(
                        303,
                        Guid.Parse("30000000-0000-0000-0000-000000000303"),
                        13
                    ),
                    new ReferenceResolverDocumentSeed(
                        404,
                        Guid.Parse("40000000-0000-0000-0000-000000000404"),
                        14
                    ),
                ],
                ReferentialIdentities:
                [
                    new ReferenceResolverReferentialIdentitySeed(schoolReferentialId, 101, 11),
                    new ReferenceResolverReferentialIdentitySeed(
                        educationOrganizationAliasReferentialId,
                        101,
                        30
                    ),
                    new ReferenceResolverReferentialIdentitySeed(localEducationAgencyReferentialId, 202, 12),
                    new ReferenceResolverReferentialIdentitySeed(schoolTypeDescriptorReferentialId, 303, 13),
                    new ReferenceResolverReferentialIdentitySeed(
                        academicSubjectDescriptorReferentialId,
                        404,
                        14
                    ),
                ],
                Schools: [new ReferenceResolverSchoolSeed(101, 255901)],
                LocalEducationAgencies: [new ReferenceResolverLocalEducationAgencySeed(202, 255901)],
                Descriptors:
                [
                    new ReferenceResolverDescriptorSeed(
                        303,
                        13,
                        "uri://ed-fi.org/SchoolTypeDescriptor",
                        "Alternative",
                        "Alternative",
                        "SchoolTypeDescriptor",
                        SchoolTypeDescriptorUri
                    ),
                    new ReferenceResolverDescriptorSeed(
                        404,
                        14,
                        "uri://ed-fi.org/AcademicSubjectDescriptor",
                        "English",
                        "English",
                        "AcademicSubjectDescriptor",
                        AcademicSubjectDescriptorUri
                    ),
                ]
            ),
            schoolTypeDescriptorResource,
            academicSubjectDescriptorResource
        );
    }

    public MappingSet CreateMappingSet(SqlDialect dialect)
    {
        const string EffectiveSchemaHash = "reference-resolver-integration-fixture";
        const string RelationalMappingVersion = "v1";

        var resourceKeysInIdOrder = SeedData
            .ResourceKeys.Select(resourceKey => new ResourceKeyEntry(
                resourceKey.ResourceKeyId,
                resourceKey.Resource,
                resourceKey.ResourceVersion,
                resourceKey.IsAbstractResource
            ))
            .ToArray();

        var resourceKeyById = resourceKeysInIdOrder.ToDictionary(
            resourceKey => resourceKey.ResourceKeyId,
            resourceKey => resourceKey
        );

        var schoolKey = resourceKeyById[11];
        var localEducationAgencyKey = resourceKeyById[12];
        var schoolTypeDescriptorKey = resourceKeyById[13];
        var academicSubjectDescriptorKey = resourceKeyById[14];
        var educationOrganizationKey = resourceKeyById[30];
        var requestResourceKey = resourceKeyById[1];

        var effectiveSchema = new EffectiveSchemaInfo(
            ApiSchemaFormatVersion: "1.0",
            RelationalMappingVersion: RelationalMappingVersion,
            EffectiveSchemaHash: EffectiveSchemaHash,
            ResourceKeyCount: checked((short)resourceKeysInIdOrder.Length),
            ResourceKeySeedHash: new byte[32],
            SchemaComponentsInEndpointOrder: [],
            ResourceKeysInIdOrder: resourceKeysInIdOrder
        );

        var model = new DerivedRelationalModelSet(
            EffectiveSchema: effectiveSchema,
            Dialect: dialect,
            ProjectSchemasInEndpointOrder: [],
            ConcreteResourcesInNameOrder:
            [
                CreateConcreteResource(requestResourceKey, "Student"),
                CreateConcreteResource(schoolKey, "School"),
                CreateConcreteResource(localEducationAgencyKey, "LocalEducationAgency"),
                CreateConcreteResource(
                    schoolTypeDescriptorKey,
                    "Descriptor",
                    ResourceStorageKind.SharedDescriptorTable
                ),
                CreateConcreteResource(
                    academicSubjectDescriptorKey,
                    "Descriptor",
                    ResourceStorageKind.SharedDescriptorTable
                ),
            ],
            AbstractIdentityTablesInNameOrder: [],
            AbstractUnionViewsInNameOrder:
            [
                new AbstractUnionViewInfo(
                    educationOrganizationKey,
                    new DbTableName(_edFiSchema, "EducationOrganization_View"),
                    [
                        new AbstractUnionViewOutputColumn(
                            new DbColumnName("DocumentId"),
                            new RelationalScalarType(ScalarKind.Int64),
                            null,
                            null
                        ),
                        new AbstractUnionViewOutputColumn(
                            new DbColumnName("EducationOrganizationId"),
                            new RelationalScalarType(ScalarKind.Int64),
                            new JsonPathExpression("$.educationOrganizationId", []),
                            null
                        ),
                    ],
                    [
                        CreateAbstractUnionArm(schoolKey, "School", "SchoolId"),
                        CreateAbstractUnionArm(
                            localEducationAgencyKey,
                            "LocalEducationAgency",
                            "LocalEducationAgencyId"
                        ),
                    ]
                ),
            ],
            IndexesInCreateOrder: [],
            TriggersInCreateOrder: []
        );

        return new MappingSet(
            Key: new MappingSetKey(EffectiveSchemaHash, dialect, RelationalMappingVersion),
            Model: model,
            WritePlansByResource: new Dictionary<QualifiedResourceName, ResourceWritePlan>(),
            ReadPlansByResource: new Dictionary<QualifiedResourceName, ResourceReadPlan>(),
            ResourceKeyIdByResource: resourceKeysInIdOrder.ToDictionary(
                resourceKey => resourceKey.Resource,
                resourceKey => resourceKey.ResourceKeyId
            ),
            ResourceKeyById: resourceKeyById,
            SecurableElementColumnPathsByResource: new Dictionary<
                QualifiedResourceName,
                IReadOnlyList<ResolvedSecurableElementPath>
            >()
        );
    }

    private static Guid CreateReferentialId(string value) => Guid.Parse(value);

    private static ConcreteResourceModel CreateConcreteResource(
        ResourceKeyEntry resourceKey,
        string tableName,
        ResourceStorageKind storageKind = ResourceStorageKind.RelationalTables
    )
    {
        return new(
            resourceKey,
            storageKind,
            CreateRelationalResourceModel(resourceKey.Resource, tableName, storageKind)
        );
    }

    private static RelationalResourceModel CreateRelationalResourceModel(
        QualifiedResourceName resource,
        string tableName,
        ResourceStorageKind storageKind
    )
    {
        List<DbColumnModel> columns =
        [
            new DbColumnModel(
                new DbColumnName("DocumentId"),
                ColumnKind.ParentKeyPart,
                new RelationalScalarType(ScalarKind.Int64),
                IsNullable: false,
                SourceJsonPath: null,
                TargetResource: null
            ),
        ];

        if (storageKind is ResourceStorageKind.RelationalTables)
        {
            columns.AddRange(CreateIdentityColumns(resource));
        }

        var rootTable = new DbTableModel(
            Table: new DbTableName(_edFiSchema, tableName),
            JsonScope: new JsonPathExpression("$", []),
            Key: new TableKey(
                $"PK_{tableName}",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            Columns: columns,
            Constraints: []
        );

        return new RelationalResourceModel(
            Resource: resource,
            PhysicalSchema: _edFiSchema,
            StorageKind: storageKind,
            Root: rootTable,
            TablesInDependencyOrder: [rootTable],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );
    }

    private static AbstractUnionViewArm CreateAbstractUnionArm(
        ResourceKeyEntry concreteMemberResourceKey,
        string tableName,
        string identityColumnName
    )
    {
        return new(
            concreteMemberResourceKey,
            new DbTableName(_edFiSchema, tableName),
            [
                new AbstractUnionViewProjectionExpression.SourceColumn(new DbColumnName("DocumentId")),
                new AbstractUnionViewProjectionExpression.SourceColumn(new DbColumnName(identityColumnName)),
            ]
        );
    }

    private static IReadOnlyList<DbColumnModel> CreateIdentityColumns(QualifiedResourceName resource)
    {
        return resource.ResourceName switch
        {
            "School" => [CreateIdentityColumn("SchoolId", "$.schoolId", ScalarKind.Int64)],
            "LocalEducationAgency" =>
            [
                CreateIdentityColumn("LocalEducationAgencyId", "$.localEducationAgencyId", ScalarKind.Int64),
            ],
            _ => [],
        };
    }

    private static DbColumnModel CreateIdentityColumn(
        string columnName,
        string jsonPath,
        ScalarKind scalarKind
    ) =>
        new(
            new DbColumnName(columnName),
            ColumnKind.Scalar,
            new RelationalScalarType(scalarKind),
            IsNullable: false,
            SourceJsonPath: new JsonPathExpression(jsonPath, []),
            TargetResource: null
        );
}

public sealed record ReferenceResolverSeedData(
    IReadOnlyList<ReferenceResolverResourceKeySeed> ResourceKeys,
    IReadOnlyList<ReferenceResolverDocumentSeed> Documents,
    IReadOnlyList<ReferenceResolverReferentialIdentitySeed> ReferentialIdentities,
    IReadOnlyList<ReferenceResolverSchoolSeed> Schools,
    IReadOnlyList<ReferenceResolverLocalEducationAgencySeed> LocalEducationAgencies,
    IReadOnlyList<ReferenceResolverDescriptorSeed> Descriptors
)
{
    public IReadOnlyList<ReferenceResolverSeedTableBatch> CreateTableBatches()
    {
        return
        [
            new(
                new DbTableName(new DbSchemaName("dms"), "ResourceKey"),
                [
                    new DbColumnName("ResourceKeyId"),
                    new DbColumnName("ProjectName"),
                    new DbColumnName("ResourceName"),
                    new DbColumnName("ResourceVersion"),
                ],
                ResourceKeys
                    .Select(resourceKey =>
                        (IReadOnlyList<object?>)
                            [
                                resourceKey.ResourceKeyId,
                                resourceKey.Resource.ProjectName,
                                resourceKey.Resource.ResourceName,
                                resourceKey.ResourceVersion,
                            ]
                    )
                    .ToArray()
            ),
            new(
                new DbTableName(new DbSchemaName("dms"), "Document"),
                [
                    new DbColumnName("DocumentId"),
                    new DbColumnName("DocumentUuid"),
                    new DbColumnName("ResourceKeyId"),
                ],
                Documents
                    .Select(document =>
                        (IReadOnlyList<object?>)
                            [document.DocumentId, document.DocumentUuid, document.ResourceKeyId]
                    )
                    .ToArray()
            ),
            new(
                new DbTableName(new DbSchemaName("dms"), "ReferentialIdentity"),
                [
                    new DbColumnName("ReferentialId"),
                    new DbColumnName("DocumentId"),
                    new DbColumnName("ResourceKeyId"),
                ],
                ReferentialIdentities
                    .Select(referentialIdentity =>
                        (IReadOnlyList<object?>)
                            [
                                referentialIdentity.ReferentialId,
                                referentialIdentity.DocumentId,
                                referentialIdentity.ResourceKeyId,
                            ]
                    )
                    .ToArray()
            ),
            new(
                new DbTableName(new DbSchemaName("edfi"), "School"),
                [new DbColumnName("DocumentId"), new DbColumnName("SchoolId")],
                Schools
                    .Select(school => (IReadOnlyList<object?>)[school.DocumentId, school.SchoolId])
                    .ToArray()
            ),
            new(
                new DbTableName(new DbSchemaName("edfi"), "LocalEducationAgency"),
                [new DbColumnName("DocumentId"), new DbColumnName("LocalEducationAgencyId")],
                LocalEducationAgencies
                    .Select(localEducationAgency =>
                        (IReadOnlyList<object?>)
                            [localEducationAgency.DocumentId, localEducationAgency.LocalEducationAgencyId]
                    )
                    .ToArray()
            ),
            new(
                new DbTableName(new DbSchemaName("dms"), "Descriptor"),
                [
                    new DbColumnName("DocumentId"),
                    new DbColumnName("ResourceKeyId"),
                    new DbColumnName("Namespace"),
                    new DbColumnName("CodeValue"),
                    new DbColumnName("ShortDescription"),
                ],
                Descriptors
                    .Select(descriptor =>
                        (IReadOnlyList<object?>)
                            [
                                descriptor.DocumentId,
                                descriptor.ResourceKeyId,
                                descriptor.Namespace,
                                descriptor.CodeValue,
                                descriptor.ShortDescription,
                            ]
                    )
                    .ToArray()
            ),
        ];
    }
}

public sealed record ReferenceResolverSeedTableBatch(
    DbTableName Table,
    IReadOnlyList<DbColumnName> Columns,
    IReadOnlyList<IReadOnlyList<object?>> Rows
);

public sealed record ReferenceResolverResourceKeySeed(
    short ResourceKeyId,
    QualifiedResourceName Resource,
    string ResourceVersion,
    bool IsAbstractResource
);

public sealed record ReferenceResolverDocumentSeed(long DocumentId, Guid DocumentUuid, short ResourceKeyId);

public sealed record ReferenceResolverReferentialIdentitySeed(
    Guid ReferentialId,
    long DocumentId,
    short ResourceKeyId
);

public sealed record ReferenceResolverSchoolSeed(long DocumentId, long SchoolId);

public sealed record ReferenceResolverLocalEducationAgencySeed(long DocumentId, long LocalEducationAgencyId);

public sealed record ReferenceResolverDescriptorSeed(
    long DocumentId,
    short ResourceKeyId,
    string Namespace,
    string CodeValue,
    string ShortDescription,
    string Discriminator,
    string Uri
);
