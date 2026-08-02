// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Backend.Tests.Common;

public sealed class ReferenceResolverIntegrationFixture
{
    private static readonly DbSchemaName _edFiSchema = new("edfi");
    private static readonly DbColumnName _documentIdColumn = new("DocumentId");

    private ReferenceResolverIntegrationFixture(
        ReferenceResolverSeedData seedData,
        QualifiedResourceName requestResource,
        QualifiedResourceName schoolResource,
        QualifiedResourceName localEducationAgencyResource,
        QualifiedResourceName educationOrganizationResource,
        QualifiedResourceName schoolTypeDescriptorResource,
        QualifiedResourceName academicSubjectDescriptorResource,
        ReferentialId schoolReferentialId,
        ReferentialId educationOrganizationAliasReferentialId,
        ReferentialId localEducationAgencyReferentialId,
        ReferentialId schoolTypeDescriptorReferentialId,
        ReferentialId academicSubjectDescriptorReferentialId,
        ReferentialId missingSchoolReferentialId,
        ReferentialId missingSchoolTypeDescriptorReferentialId,
        string schoolTypeDescriptorUri,
        string academicSubjectDescriptorUri,
        string missingSchoolTypeDescriptorUri
    )
    {
        SeedData = seedData;
        RequestResource = requestResource;
        SchoolResource = schoolResource;
        LocalEducationAgencyResource = localEducationAgencyResource;
        EducationOrganizationResource = educationOrganizationResource;
        SchoolTypeDescriptorResource = schoolTypeDescriptorResource;
        AcademicSubjectDescriptorResource = academicSubjectDescriptorResource;
        SchoolReferentialId = schoolReferentialId;
        EducationOrganizationAliasReferentialId = educationOrganizationAliasReferentialId;
        LocalEducationAgencyReferentialId = localEducationAgencyReferentialId;
        SchoolTypeDescriptorReferentialId = schoolTypeDescriptorReferentialId;
        AcademicSubjectDescriptorReferentialId = academicSubjectDescriptorReferentialId;
        MissingSchoolReferentialId = missingSchoolReferentialId;
        MissingSchoolTypeDescriptorReferentialId = missingSchoolTypeDescriptorReferentialId;
        SchoolTypeDescriptorUri = schoolTypeDescriptorUri;
        AcademicSubjectDescriptorUri = academicSubjectDescriptorUri;
        MissingSchoolTypeDescriptorUri = missingSchoolTypeDescriptorUri;
    }

    public ReferenceResolverSeedData SeedData { get; }

    /// <summary>
    /// The synthetic wide-identity resource: one identity column per scalar kind the natural-key probe has
    /// to type (Int64, Decimal, Date, DateTime, Boolean, String). It replaces the canonicalization canary
    /// the corruption check used to provide — a formatting disagreement between Core and SQL now surfaces
    /// as a silent miss, so each kind needs a resolution assertion of its own.
    /// </summary>
    public QualifiedResourceName WideIdentityResource { get; private init; }

    public ReferentialId WideIdentityReferentialId { get; private init; }

    public const long SchoolIdentityValue = 255901;

    public const long LocalEducationAgencyIdentityValue = 255901;

    public const long MissingEducationOrganizationIdentityValue = 999901;

    public const long WideIdentityInt64Value = 9007199254740993;

    public const string WideIdentityDecimalLiteral = "12.5";

    public const decimal WideIdentityDecimalValue = 12.50m;

    public const string WideIdentityDateLiteral = "2024-03-05";

    public const string WideIdentityDateTimeLiteral = "2024-03-05T13:45:30Z";

    public const string WideIdentityBooleanLiteral = "true";

    public const string WideIdentityStringValue = "Alpha-Beta";

    public static readonly DateOnly WideIdentityDateValue = new(2024, 3, 5);

    public static readonly DateTime WideIdentityDateTimeValue = new(2024, 3, 5, 13, 45, 30, DateTimeKind.Utc);

    public QualifiedResourceName RequestResource { get; }

    public QualifiedResourceName SchoolResource { get; }

    public QualifiedResourceName LocalEducationAgencyResource { get; }

    public QualifiedResourceName EducationOrganizationResource { get; }

    public QualifiedResourceName SchoolTypeDescriptorResource { get; }

    public QualifiedResourceName AcademicSubjectDescriptorResource { get; }

    public ReferentialId SchoolReferentialId { get; }

    public ReferentialId EducationOrganizationAliasReferentialId { get; }

    public ReferentialId LocalEducationAgencyReferentialId { get; }

    public ReferentialId SchoolTypeDescriptorReferentialId { get; }

    public ReferentialId AcademicSubjectDescriptorReferentialId { get; }

    public ReferentialId MissingSchoolReferentialId { get; }

    public ReferentialId MissingSchoolTypeDescriptorReferentialId { get; }

    public string SchoolTypeDescriptorUri { get; }

    public string AcademicSubjectDescriptorUri { get; }

    public string MissingSchoolTypeDescriptorUri { get; }

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
        var wideIdentityResource = new QualifiedResourceName("Ed-Fi", "WideIdentityResource");

        var wideIdentityReferentialId = CreateReferentialId("00000000-0000-0000-0000-000000000888");

        var schoolReferentialId = CreateReferentialId("00000000-0000-0000-0000-000000000111");
        var educationOrganizationAliasReferentialId = CreateReferentialId(
            "00000000-0000-0000-0000-000000000222"
        );
        var localEducationAgencyReferentialId = CreateReferentialId("00000000-0000-0000-0000-000000000333");
        var schoolTypeDescriptorReferentialId = CreateReferentialId("00000000-0000-0000-0000-000000000444");
        var academicSubjectDescriptorReferentialId = CreateReferentialId(
            "00000000-0000-0000-0000-000000000555"
        );
        var missingSchoolReferentialId = CreateReferentialId("00000000-0000-0000-0000-000000000666");
        var missingSchoolTypeDescriptorReferentialId = CreateReferentialId(
            "00000000-0000-0000-0000-000000000777"
        );

        const string SchoolTypeDescriptorUri = "uri://ed-fi.org/SchoolTypeDescriptor#Alternative";
        const string AcademicSubjectDescriptorUri = "uri://ed-fi.org/AcademicSubjectDescriptor#English";
        const string MissingSchoolTypeDescriptorUri = "uri://ed-fi.org/SchoolTypeDescriptor#Missing";

        return new(
            seedData: new ReferenceResolverSeedData(
                ResourceKeys:
                [
                    new ReferenceResolverResourceKeySeed(1, requestResource, "1.0", false),
                    new ReferenceResolverResourceKeySeed(11, schoolResource, "1.0", false),
                    new ReferenceResolverResourceKeySeed(12, localEducationAgencyResource, "1.0", false),
                    new ReferenceResolverResourceKeySeed(13, schoolTypeDescriptorResource, "1.0", false),
                    new ReferenceResolverResourceKeySeed(14, academicSubjectDescriptorResource, "1.0", false),
                    new ReferenceResolverResourceKeySeed(15, wideIdentityResource, "1.0", false),
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
                    new ReferenceResolverDocumentSeed(
                        550,
                        Guid.Parse("55000000-0000-0000-0000-000000000550"),
                        15
                    ),
                ],
                Schools: [new ReferenceResolverSchoolSeed(101, SchoolIdentityValue)],
                LocalEducationAgencies:
                [
                    new ReferenceResolverLocalEducationAgencySeed(202, LocalEducationAgencyIdentityValue),
                ],
                Descriptors:
                [
                    new ReferenceResolverDescriptorSeed(
                        303,
                        "uri://ed-fi.org",
                        "Alternative",
                        "Alternative",
                        "SchoolTypeDescriptor",
                        SchoolTypeDescriptorUri
                    ),
                    new ReferenceResolverDescriptorSeed(
                        404,
                        "uri://ed-fi.org",
                        "English",
                        "English",
                        "AcademicSubjectDescriptor",
                        AcademicSubjectDescriptorUri
                    ),
                ]
            )
            {
                // Only the School member is seeded: the fixture deliberately reuses 255901 for both the
                // School and the LocalEducationAgency identity values, and an abstract identity table
                // enforces one row per EducationOrganizationId.
                EducationOrganizationIdentities =
                [
                    new ReferenceResolverAbstractIdentitySeed(101, SchoolIdentityValue, "Ed-Fi:School"),
                ],
                WideIdentities =
                [
                    new ReferenceResolverWideIdentitySeed(
                        550,
                        WideIdentityInt64Value,
                        WideIdentityDecimalValue,
                        WideIdentityDateValue,
                        WideIdentityDateTimeValue,
                        BooleanKey: true,
                        WideIdentityStringValue
                    ),
                ],
            },
            requestResource,
            schoolResource,
            localEducationAgencyResource,
            educationOrganizationResource,
            schoolTypeDescriptorResource,
            academicSubjectDescriptorResource,
            schoolReferentialId,
            educationOrganizationAliasReferentialId,
            localEducationAgencyReferentialId,
            schoolTypeDescriptorReferentialId,
            academicSubjectDescriptorReferentialId,
            missingSchoolReferentialId,
            missingSchoolTypeDescriptorReferentialId,
            SchoolTypeDescriptorUri,
            AcademicSubjectDescriptorUri,
            MissingSchoolTypeDescriptorUri
        )
        {
            WideIdentityResource = wideIdentityResource,
            WideIdentityReferentialId = wideIdentityReferentialId,
        };
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
        var wideIdentityKey = resourceKeyById[15];
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
                CreateConcreteResource(wideIdentityKey, "WideIdentityResource"),
            ],
            // The abstract identity TABLE is what the natural-key probe seeks; the union view below is
            // what the read path projects an abstract reference through.
            AbstractIdentityTablesInNameOrder:
            [
                new AbstractIdentityTableInfo(
                    educationOrganizationKey,
                    CreateEducationOrganizationIdentityTable()
                ),
            ],
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
        )
        {
            NaturalKeyProbeTargets = CreateNaturalKeyProbeTargets(),
            DescriptorProbeTarget = CreateDescriptorProbeTarget(),
        };
    }

    /// <summary>
    /// The compiled probe metadata the natural-key resolver consumes. Hand-built rather than compiled,
    /// because the fixture's model set is itself hand-built (no <c>IdentityJsonPaths</c> to derive from) —
    /// the compiler's derivation is pinned against authoritative mapping sets by
    /// <c>Given_NaturalKeyProbes_Over_Authoritative_MappingSets</c>.
    /// </summary>
    private IReadOnlyDictionary<QualifiedResourceName, NaturalKeyProbeTarget> CreateNaturalKeyProbeTargets()
    {
        return new Dictionary<QualifiedResourceName, NaturalKeyProbeTarget>
        {
            [SchoolResource] = new(
                new DbTableName(_edFiSchema, "School"),
                _documentIdColumn,
                IsAbstract: false,
                [CreateProbeColumn("SchoolId", "$.schoolId", ScalarKind.Int64)]
            ),
            [LocalEducationAgencyResource] = new(
                new DbTableName(_edFiSchema, "LocalEducationAgency"),
                _documentIdColumn,
                IsAbstract: false,
                [CreateProbeColumn("LocalEducationAgencyId", "$.localEducationAgencyId", ScalarKind.Int64)]
            ),
            [EducationOrganizationResource] = new(
                new DbTableName(_edFiSchema, "EducationOrganizationIdentity"),
                _documentIdColumn,
                IsAbstract: true,
                [CreateProbeColumn("EducationOrganizationId", "$.educationOrganizationId", ScalarKind.Int64)]
            ),
            [WideIdentityResource] = new(
                new DbTableName(_edFiSchema, "WideIdentityResource"),
                _documentIdColumn,
                IsAbstract: false,
                [.. WideIdentityColumns.Select(column => column.ProbeColumn)]
            ),
        };
    }

    private DescriptorProbeTarget CreateDescriptorProbeTarget()
    {
        return new(
            new DbTableName(new DbSchemaName("dms"), "Descriptor"),
            DescriptorProbeColumns.UriLowered,
            new DbColumnName("Discriminator"),
            new Dictionary<QualifiedResourceName, string>
            {
                [SchoolTypeDescriptorResource] = SchoolTypeDescriptorResource.ResourceName,
                [AcademicSubjectDescriptorResource] = AcademicSubjectDescriptorResource.ResourceName,
            }
        );
    }

    private static DbTableModel CreateEducationOrganizationIdentityTable()
    {
        return new DbTableModel(
            Table: new DbTableName(_edFiSchema, "EducationOrganizationIdentity"),
            JsonScope: new JsonPathExpression("$", []),
            Key: new TableKey(
                "PK_EducationOrganizationIdentity",
                [new DbKeyColumn(_documentIdColumn, ColumnKind.ParentKeyPart)]
            ),
            Columns:
            [
                new DbColumnModel(
                    _documentIdColumn,
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                CreateIdentityColumn(
                    "EducationOrganizationId",
                    "$.educationOrganizationId",
                    ScalarKind.Int64
                ),
                new DbColumnModel(
                    new DbColumnName("Discriminator"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 256),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
            ],
            Constraints:
            [
                new TableConstraint.Unique(
                    "UX_EducationOrganizationIdentity_NK",
                    [new DbColumnName("EducationOrganizationId")]
                ),
                new TableConstraint.Unique(
                    "UX_EducationOrganizationIdentity_RefKey",
                    [new DbColumnName("EducationOrganizationId"), _documentIdColumn]
                ),
            ]
        );
    }

    /// <summary>
    /// A School reference. <paramref name="schoolId"/> is separately overridable because the resolver
    /// misses on the identity VALUE, not on the referential id: a "missing" case has to vary the value.
    /// </summary>
    public DocumentReference CreateSchoolReference(
        string path,
        ReferentialId? referentialId = null,
        long? schoolId = null
    )
    {
        return CreateDocumentReference(
            SchoolResource,
            new JsonPath("$.schoolId"),
            (schoolId ?? SchoolIdentityValue).ToString(CultureInfo.InvariantCulture),
            referentialId ?? SchoolReferentialId,
            path
        );
    }

    public DocumentReference CreateEducationOrganizationReference(
        string path,
        ReferentialId? referentialId = null,
        long? educationOrganizationId = null
    )
    {
        return CreateDocumentReference(
            EducationOrganizationResource,
            new JsonPath("$.educationOrganizationId"),
            (educationOrganizationId ?? SchoolIdentityValue).ToString(CultureInfo.InvariantCulture),
            referentialId ?? EducationOrganizationAliasReferentialId,
            path
        );
    }

    public DocumentReference CreateLocalEducationAgencyReference(
        string path,
        ReferentialId? referentialId = null,
        long? localEducationAgencyId = null
    )
    {
        return CreateDocumentReference(
            LocalEducationAgencyResource,
            new JsonPath("$.localEducationAgencyId"),
            (localEducationAgencyId ?? LocalEducationAgencyIdentityValue).ToString(
                CultureInfo.InvariantCulture
            ),
            referentialId ?? LocalEducationAgencyReferentialId,
            path
        );
    }

    /// <summary>
    /// A reference to the synthetic wide-identity resource: one identity element per scalar kind, in the
    /// probe's column order. Each part can be overridden so a test can miss on exactly one kind.
    /// </summary>
    public DocumentReference CreateWideIdentityReference(
        string path,
        ReferentialId? referentialId = null,
        string? int64Key = null,
        string? decimalKey = null,
        string? dateKey = null,
        string? dateTimeKey = null,
        string? booleanKey = null,
        string? stringKey = null
    )
    {
        return new(
            ResourceInfo: new BaseResourceInfo(
                new ProjectName(WideIdentityResource.ProjectName),
                new ResourceName(WideIdentityResource.ResourceName),
                IsDescriptor: false
            ),
            DocumentIdentity: new DocumentIdentity([
                new DocumentIdentityElement(
                    new JsonPath("$.int64Key"),
                    int64Key ?? WideIdentityInt64Value.ToString(CultureInfo.InvariantCulture)
                ),
                new DocumentIdentityElement(
                    new JsonPath("$.decimalKey"),
                    decimalKey ?? WideIdentityDecimalLiteral
                ),
                new DocumentIdentityElement(new JsonPath("$.dateKey"), dateKey ?? WideIdentityDateLiteral),
                new DocumentIdentityElement(
                    new JsonPath("$.dateTimeKey"),
                    dateTimeKey ?? WideIdentityDateTimeLiteral
                ),
                new DocumentIdentityElement(
                    new JsonPath("$.booleanKey"),
                    booleanKey ?? WideIdentityBooleanLiteral
                ),
                new DocumentIdentityElement(
                    new JsonPath("$.stringKey"),
                    stringKey ?? WideIdentityStringValue
                ),
            ]),
            ReferentialId: referentialId ?? WideIdentityReferentialId,
            Path: new JsonPath(path)
        );
    }

    public DescriptorReference CreateSchoolTypeDescriptorReference(
        string path,
        ReferentialId? referentialId = null,
        string? uri = null
    )
    {
        return CreateDescriptorReference(
            SchoolTypeDescriptorResource,
            referentialId ?? SchoolTypeDescriptorReferentialId,
            uri ?? SchoolTypeDescriptorUri,
            path
        );
    }

    public DescriptorReference CreateAcademicSubjectDescriptorReference(
        string path,
        ReferentialId? referentialId = null,
        string? uri = null
    )
    {
        return CreateDescriptorReference(
            AcademicSubjectDescriptorResource,
            referentialId ?? AcademicSubjectDescriptorReferentialId,
            uri ?? AcademicSubjectDescriptorUri,
            path
        );
    }

    private static ReferentialId CreateReferentialId(string value) => new(Guid.Parse(value));

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
                _documentIdColumn,
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

        // The natural-key probe seeks UX_<T>_RefKey: identity storage columns leading, DocumentId
        // trailing. Without it the synthetic schema would have nothing to seek, and the suites built on
        // this fixture would prove nothing about the mechanism they exist to prove.
        var identityColumns = columns.Skip(1).Select(column => column.ColumnName).ToArray();
        List<TableConstraint> constraints = [];

        if (identityColumns.Length > 0)
        {
            constraints.Add(
                new TableConstraint.Unique($"UX_{tableName}_RefKey", [.. identityColumns, _documentIdColumn])
            );
        }

        var rootTable = new DbTableModel(
            Table: new DbTableName(_edFiSchema, tableName),
            JsonScope: new JsonPathExpression("$", []),
            Key: new TableKey(
                $"PK_{tableName}",
                [new DbKeyColumn(_documentIdColumn, ColumnKind.ParentKeyPart)]
            ),
            Columns: columns,
            Constraints: constraints
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
            "WideIdentityResource" => [.. WideIdentityColumns.Select(column => column.Column)],
            _ => [],
        };
    }

    private static DbColumnModel CreateIdentityColumn(
        string columnName,
        string jsonPath,
        ScalarKind scalarKind,
        RelationalScalarType? scalarType = null
    ) =>
        new(
            new DbColumnName(columnName),
            ColumnKind.Scalar,
            scalarType ?? new RelationalScalarType(scalarKind),
            IsNullable: false,
            SourceJsonPath: new JsonPathExpression(jsonPath, []),
            TargetResource: null
        );

    private static NaturalKeyProbeColumn CreateProbeColumn(
        string columnName,
        string jsonPath,
        ScalarKind scalarKind,
        RelationalScalarType? scalarType = null
    ) =>
        new(
            new DbColumnName(columnName),
            new JsonPathExpression(jsonPath, []),
            scalarType ?? new RelationalScalarType(scalarKind),
            DescriptorResource: null
        );

    /// <summary>
    /// The wide-identity resource's identity columns, paired with the probe columns that must bind them —
    /// declared once so the DDL and the compiled probe cannot drift apart.
    /// </summary>
    private static readonly IReadOnlyList<WideIdentityColumn> WideIdentityColumns =
    [
        CreateWideIdentityColumn("Int64Key", "$.int64Key", ScalarKind.Int64),
        CreateWideIdentityColumn(
            "DecimalKey",
            "$.decimalKey",
            ScalarKind.Decimal,
            new RelationalScalarType(ScalarKind.Decimal, Decimal: (9, 2))
        ),
        CreateWideIdentityColumn("DateKey", "$.dateKey", ScalarKind.Date),
        CreateWideIdentityColumn("DateTimeKey", "$.dateTimeKey", ScalarKind.DateTime),
        CreateWideIdentityColumn("BooleanKey", "$.booleanKey", ScalarKind.Boolean),
        CreateWideIdentityColumn(
            "StringKey",
            "$.stringKey",
            ScalarKind.String,
            new RelationalScalarType(ScalarKind.String, MaxLength: 60)
        ),
    ];

    private static WideIdentityColumn CreateWideIdentityColumn(
        string columnName,
        string jsonPath,
        ScalarKind scalarKind,
        RelationalScalarType? scalarType = null
    ) =>
        new(
            CreateIdentityColumn(columnName, jsonPath, scalarKind, scalarType),
            CreateProbeColumn(columnName, jsonPath, scalarKind, scalarType)
        );

    private sealed record WideIdentityColumn(DbColumnModel Column, NaturalKeyProbeColumn ProbeColumn);

    private static DocumentReference CreateDocumentReference(
        QualifiedResourceName targetResource,
        JsonPath identityPath,
        string identityValue,
        ReferentialId referentialId,
        string path
    )
    {
        return new(
            ResourceInfo: new BaseResourceInfo(
                new ProjectName(targetResource.ProjectName),
                new ResourceName(targetResource.ResourceName),
                IsDescriptor: false
            ),
            DocumentIdentity: new DocumentIdentity([
                new DocumentIdentityElement(identityPath, identityValue),
            ]),
            ReferentialId: referentialId,
            Path: new JsonPath(path)
        );
    }

    private static DescriptorReference CreateDescriptorReference(
        QualifiedResourceName targetResource,
        ReferentialId referentialId,
        string uri,
        string path
    )
    {
        return new(
            ResourceInfo: new BaseResourceInfo(
                new ProjectName(targetResource.ProjectName),
                new ResourceName(targetResource.ResourceName),
                IsDescriptor: true
            ),
            DocumentIdentity: new DocumentIdentity([
                new DocumentIdentityElement(DocumentIdentity.DescriptorIdentityJsonPath, uri),
            ]),
            ReferentialId: referentialId,
            Path: new JsonPath(path)
        );
    }
}

public sealed record ReferenceResolverSeedData(
    IReadOnlyList<ReferenceResolverResourceKeySeed> ResourceKeys,
    IReadOnlyList<ReferenceResolverDocumentSeed> Documents,
    IReadOnlyList<ReferenceResolverSchoolSeed> Schools,
    IReadOnlyList<ReferenceResolverLocalEducationAgencySeed> LocalEducationAgencies,
    IReadOnlyList<ReferenceResolverDescriptorSeed> Descriptors
)
{
    /// <summary>
    /// Rows for the <c>edfi.EducationOrganizationIdentity</c> abstract identity table. Init-only rather
    /// than positional so existing seed builders keep compiling.
    /// </summary>
    public IReadOnlyList<ReferenceResolverAbstractIdentitySeed> EducationOrganizationIdentities { get; init; } =
    [];

    /// <summary>Rows for the synthetic wide-identity resource, one column per scalar kind.</summary>
    public IReadOnlyList<ReferenceResolverWideIdentitySeed> WideIdentities { get; init; } = [];

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
                new DbTableName(new DbSchemaName("edfi"), "EducationOrganizationIdentity"),
                [
                    new DbColumnName("DocumentId"),
                    new DbColumnName("EducationOrganizationId"),
                    new DbColumnName("Discriminator"),
                ],
                EducationOrganizationIdentities
                    .Select(identity =>
                        (IReadOnlyList<object?>)
                            [identity.DocumentId, identity.EducationOrganizationId, identity.Discriminator]
                    )
                    .ToArray()
            ),
            new(
                new DbTableName(new DbSchemaName("edfi"), "WideIdentityResource"),
                [
                    new DbColumnName("DocumentId"),
                    new DbColumnName("Int64Key"),
                    new DbColumnName("DecimalKey"),
                    new DbColumnName("DateKey"),
                    new DbColumnName("DateTimeKey"),
                    new DbColumnName("BooleanKey"),
                    new DbColumnName("StringKey"),
                ],
                WideIdentities
                    .Select(wideIdentity =>
                        (IReadOnlyList<object?>)
                            [
                                wideIdentity.DocumentId,
                                wideIdentity.Int64Key,
                                wideIdentity.DecimalKey,
                                wideIdentity.DateKey,
                                wideIdentity.DateTimeKey,
                                wideIdentity.BooleanKey,
                                wideIdentity.StringKey,
                            ]
                    )
                    .ToArray()
            ),
            new(
                new DbTableName(new DbSchemaName("dms"), "Descriptor"),
                [
                    new DbColumnName("DocumentId"),
                    new DbColumnName("Namespace"),
                    new DbColumnName("CodeValue"),
                    new DbColumnName("ShortDescription"),
                    new DbColumnName("Discriminator"),
                    new DbColumnName("Uri"),
                ],
                Descriptors
                    .Select(descriptor =>
                        (IReadOnlyList<object?>)
                            [
                                descriptor.DocumentId,
                                descriptor.Namespace,
                                descriptor.CodeValue,
                                descriptor.ShortDescription,
                                descriptor.Discriminator,
                                descriptor.Uri,
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

public sealed record ReferenceResolverSchoolSeed(long DocumentId, long SchoolId);

public sealed record ReferenceResolverAbstractIdentitySeed(
    long DocumentId,
    long EducationOrganizationId,
    string Discriminator
);

public sealed record ReferenceResolverWideIdentitySeed(
    long DocumentId,
    long Int64Key,
    decimal DecimalKey,
    DateOnly DateKey,
    DateTime DateTimeKey,
    bool BooleanKey,
    string StringKey
);

public sealed record ReferenceResolverLocalEducationAgencySeed(long DocumentId, long LocalEducationAgencyId);

public sealed record ReferenceResolverDescriptorSeed(
    long DocumentId,
    string Namespace,
    string CodeValue,
    string ShortDescription,
    string Discriminator,
    string Uri
);
