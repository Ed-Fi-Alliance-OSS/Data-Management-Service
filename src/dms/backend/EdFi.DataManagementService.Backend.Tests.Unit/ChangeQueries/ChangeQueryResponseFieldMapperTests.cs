// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.ChangeQueries;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit.ChangeQueries;

[TestFixture]
[Parallelizable]
public class Given_ChangeQueryResponseFieldMapper
{
    private static readonly QualifiedResourceName _resource = new("Ed-Fi", "School");
    private static readonly DbSchemaName _schema = new("edfi");
    private static readonly DbTableName _rootTable = new(_schema, "School");

    [Test]
    public void It_maps_identity_scalar_columns_by_exact_query_field_mapping()
    {
        var schoolIdColumn = ScalarIdentityColumn("SchoolId", "$.schoolId");

        IReadOnlyList<ChangeQueryResponseField> fields = ChangeQueryResponseFieldMapper.Map(
            CreateMappingSet(),
            CreateResourceModel(CreateQueryFieldMappings(("schoolId", "$.schoolId", "integer"))),
            CreateTrackedChangeTable(schoolIdColumn)
        );

        fields.Should().ContainSingle();
        fields[0].QueryFieldName.Should().Be("schoolId");
        fields[0].Kind.Should().Be(ChangeQueryResponseFieldKind.Scalar);
        fields[0].OldColumn.OldColumnName.Should().Be(new DbColumnName("OldSchoolId"));
        fields[0].NewColumn.NewColumnName.Should().Be(new DbColumnName("NewSchoolId"));
        fields[0].OldDescriptorCodeValueColumn.Should().BeNull();
        fields[0].NewDescriptorCodeValueColumn.Should().BeNull();
    }

    [Test]
    public void It_pairs_descriptor_namespace_and_code_value_columns_into_one_descriptor_field()
    {
        var namespaceColumn = DescriptorIdentityColumn(
            "TermDescriptor_Namespace",
            "$.termDescriptor",
            TrackedChangeColumnRole.DescriptorNamespace
        );
        var codeValueColumn = DescriptorIdentityColumn(
            "TermDescriptor_CodeValue",
            "$.termDescriptor",
            TrackedChangeColumnRole.DescriptorCodeValue
        );

        IReadOnlyList<ChangeQueryResponseField> fields = ChangeQueryResponseFieldMapper.Map(
            CreateMappingSet(),
            CreateResourceModel(CreateQueryFieldMappings(("termDescriptor", "$.termDescriptor", "string"))),
            CreateTrackedChangeTable(codeValueColumn, namespaceColumn)
        );

        fields.Should().ContainSingle();
        fields[0].QueryFieldName.Should().Be("termDescriptor");
        fields[0].Kind.Should().Be(ChangeQueryResponseFieldKind.Descriptor);
        fields[0].OldColumn.OldColumnName.Should().Be(new DbColumnName("OldTermDescriptor_Namespace"));
        fields[0].NewColumn.NewColumnName.Should().Be(new DbColumnName("NewTermDescriptor_Namespace"));
        fields[0]
            .OldDescriptorCodeValueColumn!.OldColumnName.Should()
            .Be(new DbColumnName("OldTermDescriptor_CodeValue"));
        fields[0]
            .NewDescriptorCodeValueColumn!.NewColumnName.Should()
            .Be(new DbColumnName("NewTermDescriptor_CodeValue"));
    }

    [Test]
    public void It_emits_descriptor_field_at_first_descriptor_member_table_order()
    {
        var namespaceColumn = DescriptorIdentityColumn(
            "TermDescriptor_Namespace",
            "$.termDescriptor",
            TrackedChangeColumnRole.DescriptorNamespace
        );
        var codeValueColumn = DescriptorIdentityColumn(
            "TermDescriptor_CodeValue",
            "$.termDescriptor",
            TrackedChangeColumnRole.DescriptorCodeValue
        );
        var schoolIdColumn = ScalarIdentityColumn("SchoolId", "$.schoolId");

        IReadOnlyList<ChangeQueryResponseField> fields = ChangeQueryResponseFieldMapper.Map(
            CreateMappingSet(),
            CreateResourceModel(
                CreateQueryFieldMappings(
                    ("termDescriptor", "$.termDescriptor", "string"),
                    ("schoolId", "$.schoolId", "integer")
                )
            ),
            CreateTrackedChangeTable(codeValueColumn, schoolIdColumn, namespaceColumn)
        );

        fields.Should().HaveCount(2);
        fields.Select(field => field.QueryFieldName).Should().Equal("termDescriptor", "schoolId");
        fields[0].Kind.Should().Be(ChangeQueryResponseFieldKind.Descriptor);
        fields[1].Kind.Should().Be(ChangeQueryResponseFieldKind.Scalar);
    }

    [Test]
    public void It_throws_when_descriptor_identity_group_is_missing_namespace_column()
    {
        var codeValueColumn = DescriptorIdentityColumn(
            "TermDescriptor_CodeValue",
            "$.termDescriptor",
            TrackedChangeColumnRole.DescriptorCodeValue
        );

        Action act = () =>
            ChangeQueryResponseFieldMapper.Map(
                CreateMappingSet(),
                CreateResourceModel(
                    CreateQueryFieldMappings(("termDescriptor", "$.termDescriptor", "string"))
                ),
                CreateTrackedChangeTable(codeValueColumn)
            );

        act.Should()
            .Throw<NotSupportedException>()
            .WithMessage(
                "Unable to map tracked-change identity path '$.termDescriptor' on resource "
                    + "'Ed-Fi:School' to a Change Query response field."
            );
    }

    [Test]
    public void It_ignores_person_document_id_columns()
    {
        var personDocumentIdColumn = new TrackedChangeColumnInfo(
            OldColumnName: new DbColumnName("OldStudent_DocumentId"),
            NewColumnName: new DbColumnName("NewStudent_DocumentId"),
            SourceJsonPath: "$.studentReference.studentUniqueId",
            CanonicalStorageColumn: null,
            IsOldColumnNullable: false,
            IsNewColumnNullable: true,
            ScalarType: new RelationalScalarType(ScalarKind.Int64),
            Role: TrackedChangeColumnRole.PersonDocumentId,
            Origin: TrackedChangeColumnOrigin.Identity,
            PersonJoinName: "Student"
        );
        var schoolIdColumn = ScalarIdentityColumn("SchoolId", "$.schoolId");

        IReadOnlyList<ChangeQueryResponseField> fields = ChangeQueryResponseFieldMapper.Map(
            CreateMappingSet(),
            CreateResourceModel(CreateQueryFieldMappings(("schoolId", "$.schoolId", "integer"))),
            CreateTrackedChangeTable(personDocumentIdColumn, schoolIdColumn)
        );

        fields.Should().ContainSingle();
        fields[0].QueryFieldName.Should().Be("schoolId");
    }

    [Test]
    public void It_throws_when_exact_query_field_mapping_is_ambiguous()
    {
        var schoolIdColumn = ScalarIdentityColumn("SchoolId", "$.schoolId");

        Action act = () =>
            ChangeQueryResponseFieldMapper.Map(
                CreateMappingSet(),
                CreateResourceModel(
                    CreateQueryFieldMappings(
                        ("schoolId", "$.schoolId", "integer"),
                        ("educationOrganizationId", "$.schoolId", "integer")
                    )
                ),
                CreateTrackedChangeTable(schoolIdColumn)
            );

        act.Should()
            .Throw<NotSupportedException>()
            .WithMessage(
                "Unable to map tracked-change identity path '$.schoolId' on resource "
                    + "'Ed-Fi:School' to a Change Query response field."
            );
    }

    [Test]
    public void It_uses_key_unification_equivalent_paths_when_exact_mapping_is_absent()
    {
        var trackedColumn = ScalarIdentityColumn(
            "EducationOrganizationId",
            "$.educationOrganizationReference.educationOrganizationId",
            new DbColumnName("EducationOrganizationId_Unified")
        );
        KeyUnificationEqualityConstraintDiagnostics diagnostics =
            KeyUnificationEqualityConstraintDiagnostics.Empty with
            {
                Applied =
                [
                    new KeyUnificationAppliedConstraint(
                        EndpointAPath: Path("$.educationOrganizationReference.educationOrganizationId"),
                        EndpointBPath: Path("$.schoolReference.schoolId"),
                        Table: _rootTable,
                        EndpointAColumn: new DbColumnName("EducationOrganizationId"),
                        EndpointBColumn: new DbColumnName("SchoolId"),
                        CanonicalColumn: new DbColumnName("EducationOrganizationId_Unified")
                    ),
                ],
            };

        IReadOnlyList<ChangeQueryResponseField> fields = ChangeQueryResponseFieldMapper.Map(
            CreateMappingSet(),
            CreateResourceModel(
                CreateQueryFieldMappings(("schoolId", "$.schoolReference.schoolId", "integer")),
                diagnostics
            ),
            CreateTrackedChangeTable(trackedColumn)
        );

        fields.Should().ContainSingle();
        fields[0].QueryFieldName.Should().Be("schoolId");
    }

    [Test]
    public void It_throws_when_key_unification_equivalent_mapping_is_ambiguous()
    {
        var trackedColumn = ScalarIdentityColumn(
            "LocalEducationAgencyId",
            "$.localEducationAgencyReference.localEducationAgencyId",
            new DbColumnName("EducationOrganizationId_Unified")
        );
        KeyUnificationEqualityConstraintDiagnostics diagnostics = KeyUnificationDiagnostics(
            "$.educationOrganizationReference.educationOrganizationId",
            "$.schoolReference.schoolId",
            new DbColumnName("EducationOrganizationId_Unified")
        );

        Action act = () =>
            ChangeQueryResponseFieldMapper.Map(
                CreateMappingSet(),
                CreateResourceModel(
                    CreateQueryFieldMappings(
                        ("schoolId", "$.schoolReference.schoolId", "integer"),
                        (
                            "educationOrganizationId",
                            "$.educationOrganizationReference.educationOrganizationId",
                            "integer"
                        )
                    ),
                    diagnostics
                ),
                CreateTrackedChangeTable(trackedColumn)
            );

        act.Should()
            .Throw<NotSupportedException>()
            .WithMessage(
                "Unable to map tracked-change identity path "
                    + "'$.localEducationAgencyReference.localEducationAgencyId' on resource "
                    + "'Ed-Fi:School' to a Change Query response field."
            );
    }

    [Test]
    public void It_uses_query_capability_alias_when_json_path_mapping_is_absent()
    {
        var trackedColumn = ScalarIdentityColumn(
            "EducationOrganizationId",
            "$.educationOrganizationReference.educationOrganizationId",
            new DbColumnName("EducationOrganizationId_Unified")
        );
        KeyUnificationEqualityConstraintDiagnostics diagnostics = KeyUnificationDiagnostics(
            "$.educationOrganizationReference.educationOrganizationId",
            "$.schoolReference.schoolId",
            new DbColumnName("EducationOrganizationId_Unified")
        );

        IReadOnlyList<ChangeQueryResponseField> fields = ChangeQueryResponseFieldMapper.Map(
            CreateMappingSet(
                CreateQueryCapability(
                    SupportedField(
                        "schoolId",
                        "$.schoolReference.schoolId",
                        "integer",
                        new RelationalQueryFieldTarget.RootColumn(
                            new DbColumnName("EducationOrganizationId_Unified")
                        )
                    )
                )
            ),
            CreateResourceModel(CreateQueryFieldMappings(), diagnostics),
            CreateTrackedChangeTable(trackedColumn)
        );

        fields.Should().ContainSingle();
        fields[0].QueryFieldName.Should().Be("schoolId");
    }

    [Test]
    public void It_uses_unique_query_capability_target_match_as_compiled_alias()
    {
        var trackedColumn = ScalarIdentityColumn(
            "EducationOrganizationId",
            "$.educationOrganizationReference.educationOrganizationId",
            new DbColumnName("EducationOrganizationId_Unified")
        );

        IReadOnlyList<ChangeQueryResponseField> fields = ChangeQueryResponseFieldMapper.Map(
            CreateMappingSet(
                CreateQueryCapability(
                    SupportedField(
                        "localEducationAgencyId",
                        "$.localEducationAgencyReference.localEducationAgencyId",
                        "integer",
                        new RelationalQueryFieldTarget.RootColumn(
                            new DbColumnName("EducationOrganizationId_Unified")
                        )
                    )
                )
            ),
            CreateResourceModel(CreateQueryFieldMappings()),
            CreateTrackedChangeTable(trackedColumn)
        );

        fields.Should().ContainSingle();
        fields[0].QueryFieldName.Should().Be("localEducationAgencyId");
    }

    [Test]
    public void It_uses_supported_query_capability_fields_when_resource_support_is_omitted()
    {
        var trackedColumn = ScalarIdentityColumn(
            "EducationOrganizationId",
            "$.educationOrganizationReference.educationOrganizationId",
            new DbColumnName("EducationOrganizationId_Unified")
        );

        IReadOnlyList<ChangeQueryResponseField> fields = ChangeQueryResponseFieldMapper.Map(
            CreateMappingSet(
                CreateOmittedQueryCapability(
                    SupportedField(
                        "localEducationAgencyId",
                        "$.localEducationAgencyReference.localEducationAgencyId",
                        "integer",
                        new RelationalQueryFieldTarget.RootColumn(
                            new DbColumnName("EducationOrganizationId_Unified")
                        )
                    )
                )
            ),
            CreateResourceModel(CreateQueryFieldMappings()),
            CreateTrackedChangeTable(trackedColumn)
        );

        fields.Should().ContainSingle();
        fields[0].QueryFieldName.Should().Be("localEducationAgencyId");
    }

    [Test]
    public void It_matches_key_unified_query_capability_targets_to_endpoint_columns()
    {
        var trackedColumn = ScalarIdentityColumn(
            "EducationOrganizationId",
            "$.educationOrganizationReference.educationOrganizationId",
            new DbColumnName("EducationOrganizationId_Unified")
        );
        KeyUnificationEqualityConstraintDiagnostics diagnostics =
            KeyUnificationEqualityConstraintDiagnostics.Empty with
            {
                Applied =
                [
                    new KeyUnificationAppliedConstraint(
                        EndpointAPath: Path("$.educationOrganizationReference.educationOrganizationId"),
                        EndpointBPath: Path("$.schoolReference.schoolId"),
                        Table: _rootTable,
                        EndpointAColumn: new DbColumnName("EducationOrganizationId"),
                        EndpointBColumn: new DbColumnName("SchoolId"),
                        CanonicalColumn: new DbColumnName("EducationOrganizationId_Unified")
                    ),
                ],
            };

        IReadOnlyList<ChangeQueryResponseField> fields = ChangeQueryResponseFieldMapper.Map(
            CreateMappingSet(
                CreateQueryCapability(
                    SupportedField(
                        "schoolId",
                        "$.schoolReference.schoolId",
                        "integer",
                        new RelationalQueryFieldTarget.RootColumn(new DbColumnName("SchoolId"))
                    )
                )
            ),
            CreateResourceModel(CreateQueryFieldMappings(), diagnostics),
            CreateTrackedChangeTable(trackedColumn)
        );

        fields.Should().ContainSingle();
        fields[0].QueryFieldName.Should().Be("schoolId");
    }

    [Test]
    public void It_throws_when_query_capability_alias_matches_are_ambiguous()
    {
        var trackedColumn = ScalarIdentityColumn(
            "EducationOrganizationId",
            "$.educationOrganizationReference.educationOrganizationId",
            new DbColumnName("EducationOrganizationId_Unified")
        );

        Action act = () =>
            ChangeQueryResponseFieldMapper.Map(
                CreateMappingSet(
                    CreateQueryCapability(
                        SupportedField(
                            "schoolId",
                            "$.schoolReference.schoolId",
                            "integer",
                            new RelationalQueryFieldTarget.RootColumn(
                                new DbColumnName("EducationOrganizationId_Unified")
                            )
                        ),
                        SupportedField(
                            "localEducationAgencyId",
                            "$.localEducationAgencyReference.localEducationAgencyId",
                            "integer",
                            new RelationalQueryFieldTarget.RootColumn(
                                new DbColumnName("EducationOrganizationId_Unified")
                            )
                        )
                    )
                ),
                CreateResourceModel(CreateQueryFieldMappings()),
                CreateTrackedChangeTable(trackedColumn)
            );

        act.Should()
            .Throw<NotSupportedException>()
            .WithMessage(
                "Unable to map tracked-change identity path "
                    + "'$.educationOrganizationReference.educationOrganizationId' on resource "
                    + "'Ed-Fi:School' to a Change Query response field."
            );
    }

    [Test]
    public void It_throws_when_no_response_field_name_can_be_resolved()
    {
        var unmappedColumn = ScalarIdentityColumn("UnknownIdentity", "$.unknownIdentity");

        Action act = () =>
            ChangeQueryResponseFieldMapper.Map(
                CreateMappingSet(),
                CreateResourceModel(CreateQueryFieldMappings()),
                CreateTrackedChangeTable(unmappedColumn)
            );

        act.Should()
            .Throw<NotSupportedException>()
            .WithMessage(
                "Unable to map tracked-change identity path '$.unknownIdentity' on resource "
                    + "'Ed-Fi:School' to a Change Query response field."
            );
    }

    private static TrackedChangeColumnInfo ScalarIdentityColumn(
        string columnName,
        string sourceJsonPath,
        DbColumnName? canonicalStorageColumn = null
    )
    {
        return new TrackedChangeColumnInfo(
            OldColumnName: new DbColumnName($"Old{columnName}"),
            NewColumnName: new DbColumnName($"New{columnName}"),
            SourceJsonPath: sourceJsonPath,
            CanonicalStorageColumn: canonicalStorageColumn,
            IsOldColumnNullable: false,
            IsNewColumnNullable: true,
            ScalarType: new RelationalScalarType(ScalarKind.Int32),
            Role: TrackedChangeColumnRole.Scalar,
            Origin: TrackedChangeColumnOrigin.Identity
        );
    }

    private static TrackedChangeColumnInfo DescriptorIdentityColumn(
        string columnName,
        string sourceJsonPath,
        TrackedChangeColumnRole role
    )
    {
        return new TrackedChangeColumnInfo(
            OldColumnName: new DbColumnName($"Old{columnName}"),
            NewColumnName: new DbColumnName($"New{columnName}"),
            SourceJsonPath: sourceJsonPath,
            CanonicalStorageColumn: null,
            IsOldColumnNullable: false,
            IsNewColumnNullable: true,
            ScalarType: new RelationalScalarType(ScalarKind.String),
            Role: role,
            Origin: TrackedChangeColumnOrigin.Identity,
            DescriptorJoinName: "TermDescriptor"
        );
    }

    private static ConcreteResourceModel CreateResourceModel(
        IReadOnlyDictionary<string, RelationalQueryFieldMapping> queryFieldMappings,
        KeyUnificationEqualityConstraintDiagnostics? keyUnificationDiagnostics = null
    )
    {
        var rootModel = new DbTableModel(
            _rootTable,
            Path("$"),
            new TableKey("PK_School", []),
            Columns: [],
            Constraints: []
        );
        var relationalModel = new RelationalResourceModel(
            _resource,
            _schema,
            ResourceStorageKind.RelationalTables,
            rootModel,
            TablesInDependencyOrder: [rootModel],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        )
        {
            KeyUnificationEqualityConstraints =
                keyUnificationDiagnostics ?? KeyUnificationEqualityConstraintDiagnostics.Empty,
        };

        return new ConcreteResourceModel(
            new ResourceKeyEntry(1, _resource, ResourceVersion: "1.0", IsAbstractResource: false),
            ResourceStorageKind.RelationalTables,
            relationalModel
        )
        {
            QueryFieldMappingsByQueryField = queryFieldMappings,
        };
    }

    private static IReadOnlyDictionary<string, RelationalQueryFieldMapping> CreateQueryFieldMappings(
        params (string FieldName, string Path, string Type)[] mappings
    )
    {
        return mappings.ToDictionary(
            static mapping => mapping.FieldName,
            static mapping => new RelationalQueryFieldMapping(
                mapping.FieldName,
                [new RelationalQueryFieldPath(Path(mapping.Path), mapping.Type)]
            ),
            StringComparer.Ordinal
        );
    }

    private static KeyUnificationEqualityConstraintDiagnostics KeyUnificationDiagnostics(
        string trackedPath,
        string aliasPath,
        DbColumnName canonicalColumn
    )
    {
        return KeyUnificationEqualityConstraintDiagnostics.Empty with
        {
            Applied =
            [
                new KeyUnificationAppliedConstraint(
                    EndpointAPath: Path(trackedPath),
                    EndpointBPath: Path(aliasPath),
                    Table: _rootTable,
                    EndpointAColumn: new DbColumnName("TrackedEndpointColumn"),
                    EndpointBColumn: new DbColumnName("AliasEndpointColumn"),
                    CanonicalColumn: canonicalColumn
                ),
            ],
        };
    }

    private static MappingSet CreateMappingSet(RelationalQueryCapability? queryCapability = null)
    {
        var queryCapabilitiesByResource = queryCapability is null
            ? new Dictionary<QualifiedResourceName, RelationalQueryCapability>()
            : new Dictionary<QualifiedResourceName, RelationalQueryCapability>
            {
                [_resource] = queryCapability,
            };

        return new MappingSet(
            Key: new MappingSetKey("schema-hash", SqlDialect.Pgsql, "v1"),
            Model: new DerivedRelationalModelSet(
                new EffectiveSchemaInfo(
                    ApiSchemaFormatVersion: "5.2",
                    RelationalMappingVersion: "v1",
                    EffectiveSchemaHash: "schema-hash",
                    ResourceKeyCount: 1,
                    ResourceKeySeedHash: new byte[32],
                    SchemaComponentsInEndpointOrder: [],
                    ResourceKeysInIdOrder:
                    [
                        new ResourceKeyEntry(1, _resource, ResourceVersion: "1.0", IsAbstractResource: false),
                    ]
                ),
                SqlDialect.Pgsql,
                ProjectSchemasInEndpointOrder: [],
                ConcreteResourcesInNameOrder: [],
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
        )
        {
            QueryCapabilitiesByResource = queryCapabilitiesByResource,
        };
    }

    private static RelationalQueryCapability CreateQueryCapability(
        params SupportedRelationalQueryField[] supportedFields
    )
    {
        return new RelationalQueryCapability(
            new RelationalQuerySupport.Supported(),
            supportedFields.ToDictionary(
                static supportedField => supportedField.QueryFieldName,
                static supportedField => supportedField,
                StringComparer.Ordinal
            ),
            new Dictionary<string, UnsupportedRelationalQueryField>(StringComparer.Ordinal)
        );
    }

    private static RelationalQueryCapability CreateOmittedQueryCapability(
        params SupportedRelationalQueryField[] supportedFields
    )
    {
        return new RelationalQueryCapability(
            new RelationalQuerySupport.Omitted(
                new RelationalQueryCapabilityOmission(
                    RelationalQueryCapabilityOmissionKind.UnsupportedQueryFields,
                    "Unrelated unsupported query fields are present."
                )
            ),
            supportedFields.ToDictionary(
                static supportedField => supportedField.QueryFieldName,
                static supportedField => supportedField,
                StringComparer.Ordinal
            ),
            new Dictionary<string, UnsupportedRelationalQueryField>(StringComparer.Ordinal)
        );
    }

    private static SupportedRelationalQueryField SupportedField(
        string queryFieldName,
        string path,
        string type,
        RelationalQueryFieldTarget target
    )
    {
        return new SupportedRelationalQueryField(
            queryFieldName,
            new RelationalQueryFieldPath(Path(path), type),
            target
        );
    }

    private static TrackedChangeTableInfo CreateTrackedChangeTable(params TrackedChangeColumnInfo[] columns)
    {
        return new TrackedChangeTableInfo(
            Table: new DbTableName(new DbSchemaName("tracked_changes_edfi"), "School"),
            Kind: TrackedChangeTableKind.Resource,
            SourceTable: _rootTable,
            ValueColumnsInTableOrder: columns,
            SystemColumns: [],
            PrimaryKeyColumns: [],
            DescriptorJoins: [],
            PersonJoins: []
        );
    }

    private static JsonPathExpression Path(string canonical) => new(canonical, []);
}
