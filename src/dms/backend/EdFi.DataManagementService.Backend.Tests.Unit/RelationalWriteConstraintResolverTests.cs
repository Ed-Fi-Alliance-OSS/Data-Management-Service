// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_Relational_Write_Constraint_Resolver
{
    private static readonly QualifiedResourceName SectionResource = new("Ed-Fi", "Section");
    private static readonly QualifiedResourceName SchoolResource = new("Ed-Fi", "School");
    private static readonly QualifiedResourceName SessionTypeDescriptorResource = new(
        "Ed-Fi",
        "SessionTypeDescriptor"
    );

    private RelationalWriteConstraintResolver _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _sut = new RelationalWriteConstraintResolver();
    }

    [Test]
    public void It_resolves_root_natural_key_unique_constraints_using_compiled_identity_metadata()
    {
        var fixture = CreateFixture();
        var request = new RelationalWriteConstraintResolutionRequest(
            fixture.WritePlan,
            fixture.ReferenceResolverRequest,
            new RelationalWriteExceptionClassification.UniqueConstraintViolation(
                fixture.RootNaturalKeyConstraintName
            )
        );

        var result = _sut.Resolve(request);

        result
            .Should()
            .Be(
                new RelationalWriteConstraintResolution.RootNaturalKeyUnique(
                    fixture.RootNaturalKeyConstraintName
                )
            );
    }

    [Test]
    public void It_resolves_known_document_reference_foreign_keys_using_the_compiled_binding_inventory()
    {
        var fixture = CreateFixture();
        var request = new RelationalWriteConstraintResolutionRequest(
            fixture.WritePlan,
            fixture.ReferenceResolverRequest,
            new RelationalWriteExceptionClassification.ForeignKeyConstraintViolation(
                fixture.DocumentReferenceConstraintName
            )
        );

        var result = _sut.Resolve(request);

        var reference = result
            .Should()
            .BeOfType<RelationalWriteConstraintResolution.RequestReference>()
            .Subject;

        reference.ConstraintName.Should().Be(fixture.DocumentReferenceConstraintName);
        reference.ReferenceKind.Should().Be(RelationalWriteReferenceKind.Document);
        reference.ReferencePath.Canonical.Should().Be("$.schoolReference");
        reference.TargetResource.Should().Be(SchoolResource);
    }

    [Test]
    public void It_resolves_hashed_or_shortened_descriptor_reference_constraint_names_from_compiled_metadata()
    {
        var fixture = CreateFixture();
        var request = new RelationalWriteConstraintResolutionRequest(
            fixture.WritePlan,
            fixture.ReferenceResolverRequest,
            new RelationalWriteExceptionClassification.ForeignKeyConstraintViolation(
                fixture.DescriptorReferenceConstraintName
            )
        );

        var result = _sut.Resolve(request);

        var reference = result
            .Should()
            .BeOfType<RelationalWriteConstraintResolution.RequestReference>()
            .Subject;

        reference.ConstraintName.Should().Be(fixture.DescriptorReferenceConstraintName);
        reference.ReferenceKind.Should().Be(RelationalWriteReferenceKind.Descriptor);
        reference.ReferencePath.Canonical.Should().Be("$.sessionTypeDescriptor");
        reference.TargetResource.Should().Be(SessionTypeDescriptorResource);
    }

    [Test]
    public void It_falls_back_to_unresolved_for_non_request_facing_or_out_of_scope_constraints()
    {
        var fixture = CreateFixture();
        var structuralForeignKey = new RelationalWriteConstraintResolutionRequest(
            fixture.WritePlan,
            fixture.ReferenceResolverRequest,
            new RelationalWriteExceptionClassification.ForeignKeyConstraintViolation(
                fixture.StructuralForeignKeyConstraintName
            )
        );
        var collectionUnique = new RelationalWriteConstraintResolutionRequest(
            fixture.WritePlan,
            fixture.ReferenceResolverRequest,
            new RelationalWriteExceptionClassification.UniqueConstraintViolation(
                fixture.CollectionUniqueConstraintName
            )
        );

        _sut.Resolve(structuralForeignKey)
            .Should()
            .Be(
                new RelationalWriteConstraintResolution.Unresolved(fixture.StructuralForeignKeyConstraintName)
            );
        _sut.Resolve(collectionUnique)
            .Should()
            .Be(new RelationalWriteConstraintResolution.Unresolved(fixture.CollectionUniqueConstraintName));
    }

    [Test]
    public void It_preserves_the_missing_trigger_metadata_diagnostic_when_identity_trigger_inventory_is_missing()
    {
        var fixture = CreateFixture();
        var mappingSetWithoutReferentialIdentityTrigger = fixture.ReferenceResolverRequest.MappingSet with
        {
            Model = fixture.ReferenceResolverRequest.MappingSet.Model with { TriggersInCreateOrder = [] },
        };
        var request = new RelationalWriteConstraintResolutionRequest(
            fixture.WritePlan,
            fixture.ReferenceResolverRequest with
            {
                MappingSet = mappingSetWithoutReferentialIdentityTrigger,
            },
            new RelationalWriteExceptionClassification.UniqueConstraintViolation(
                fixture.RootNaturalKeyConstraintName
            )
        );

        var act = () => _sut.Resolve(request);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Mapping set 'schema-hash/Pgsql/v1' is missing referential-identity trigger metadata for resource 'Ed-Fi.Section'."
            );
    }

    [Test]
    public void It_resolves_abstract_identity_table_natural_key_unique_constraints_to_identity_conflicts()
    {
        var fixture = CreateSchoolAbstractIdentityFixture();
        var request = new RelationalWriteConstraintResolutionRequest(
            fixture.WritePlan,
            fixture.ReferenceResolverRequest,
            new RelationalWriteExceptionClassification.UniqueConstraintViolation(
                fixture.NaturalKeyConstraintName
            )
        );

        var result = _sut.Resolve(request);

        result
            .Should()
            .Be(
                new RelationalWriteConstraintResolution.RootNaturalKeyUnique(fixture.NaturalKeyConstraintName)
            );
    }

    [Test]
    public void It_leaves_abstract_identity_table_reference_key_unique_constraints_unresolved()
    {
        var fixture = CreateSchoolAbstractIdentityFixture();
        var request = new RelationalWriteConstraintResolutionRequest(
            fixture.WritePlan,
            fixture.ReferenceResolverRequest,
            new RelationalWriteExceptionClassification.UniqueConstraintViolation(
                fixture.ReferenceKeyConstraintName
            )
        );

        var result = _sut.Resolve(request);

        result
            .Should()
            .Be(new RelationalWriteConstraintResolution.Unresolved(fixture.ReferenceKeyConstraintName));
    }

    [Test]
    public void It_leaves_unique_constraints_absent_from_concrete_and_abstract_models_unresolved()
    {
        var fixture = CreateSchoolAbstractIdentityFixture();
        const string absentConstraintName = "UX_EducationOrganizationIdentity_Absent";
        var request = new RelationalWriteConstraintResolutionRequest(
            fixture.WritePlan,
            fixture.ReferenceResolverRequest,
            new RelationalWriteExceptionClassification.UniqueConstraintViolation(absentConstraintName)
        );

        var result = _sut.Resolve(request);

        result.Should().Be(new RelationalWriteConstraintResolution.Unresolved(absentConstraintName));
    }

    [Test]
    public void It_resolves_composite_abstract_identity_natural_key_unique_constraints_to_identity_conflicts()
    {
        const string naturalKeyConstraintName = "UX_CompositeIdentity_NK";
        var (writePlan, mappingSet) = AbstractIdentitySchoolTestData.BuildSchoolWriteModel(
            CompositeAbstractIdentityTable()
        );
        var request = new RelationalWriteConstraintResolutionRequest(
            writePlan,
            new ReferenceResolverRequest(mappingSet, AbstractIdentitySchoolTestData.SchoolResource, [], []),
            new RelationalWriteExceptionClassification.UniqueConstraintViolation(naturalKeyConstraintName)
        );

        var result = _sut.Resolve(request);

        result
            .Should()
            .Be(new RelationalWriteConstraintResolution.RootNaturalKeyUnique(naturalKeyConstraintName));
    }

    [Test]
    public void It_leaves_composite_abstract_identity_reference_key_unique_constraints_unresolved()
    {
        const string referenceKeyConstraintName = "UX_CompositeIdentity_RefKey";
        var (writePlan, mappingSet) = AbstractIdentitySchoolTestData.BuildSchoolWriteModel(
            CompositeAbstractIdentityTable()
        );
        var request = new RelationalWriteConstraintResolutionRequest(
            writePlan,
            new ReferenceResolverRequest(mappingSet, AbstractIdentitySchoolTestData.SchoolResource, [], []),
            new RelationalWriteExceptionClassification.UniqueConstraintViolation(referenceKeyConstraintName)
        );

        var result = _sut.Resolve(request);

        result.Should().Be(new RelationalWriteConstraintResolution.Unresolved(referenceKeyConstraintName));
    }

    [Test]
    public void It_resolves_abstract_identity_natural_key_when_the_violated_table_is_not_first_in_name_order()
    {
        var violatedConstraintName =
            AbstractIdentitySchoolTestData.GeneralStudentProgramAssociationNaturalKeyConstraintName;

        // A StudentProgramAssociation write genuinely maintains the GeneralStudentProgramAssociation identity
        // table. Prepend the unrelated EducationOrganization identity table, which sorts first by name, so the
        // resolver must skip a non-matching table before matching the violated one — while still resolving a
        // constraint the written resource actually owns.
        var (writePlan, mappingSet) = AbstractIdentitySchoolTestData.BuildReferenceBackedSubclassWriteModel();

        var mappingSetWithTwoAbstractTables = mappingSet with
        {
            Model = mappingSet.Model with
            {
                AbstractIdentityTablesInNameOrder =
                [
                    new AbstractIdentityTableInfo(
                        new ResourceKeyEntry(
                            3,
                            AbstractIdentitySchoolTestData.EducationOrganizationResource,
                            "1.0.0",
                            true
                        ),
                        AbstractIdentitySchoolTestData.EducationOrganizationIdentityTable()
                    ),
                    .. mappingSet.Model.AbstractIdentityTablesInNameOrder,
                ],
            },
        };
        var request = new RelationalWriteConstraintResolutionRequest(
            writePlan,
            new ReferenceResolverRequest(
                mappingSetWithTwoAbstractTables,
                AbstractIdentitySchoolTestData.StudentProgramAssociationResource,
                [],
                []
            ),
            new RelationalWriteExceptionClassification.UniqueConstraintViolation(violatedConstraintName)
        );

        var result = _sut.Resolve(request);

        result
            .Should()
            .Be(new RelationalWriteConstraintResolution.RootNaturalKeyUnique(violatedConstraintName));
    }

    [Test]
    public void It_resolves_abstract_identity_natural_key_with_reference_backed_identity_columns()
    {
        const string naturalKeyConstraintName = "UX_ReferenceBackedIdentity_NK";
        var (writePlan, mappingSet) = AbstractIdentitySchoolTestData.BuildSchoolWriteModel(
            ReferenceBackedAbstractIdentityTable()
        );
        var request = new RelationalWriteConstraintResolutionRequest(
            writePlan,
            new ReferenceResolverRequest(mappingSet, AbstractIdentitySchoolTestData.SchoolResource, [], []),
            new RelationalWriteExceptionClassification.UniqueConstraintViolation(naturalKeyConstraintName)
        );

        var result = _sut.Resolve(request);

        result
            .Should()
            .Be(new RelationalWriteConstraintResolution.RootNaturalKeyUnique(naturalKeyConstraintName));
    }

    [Test]
    public void It_leaves_abstract_identity_reference_key_with_reference_backed_identity_columns_unresolved()
    {
        const string referenceKeyConstraintName = "UX_ReferenceBackedIdentity_RefKey";
        var (writePlan, mappingSet) = AbstractIdentitySchoolTestData.BuildSchoolWriteModel(
            ReferenceBackedAbstractIdentityTable()
        );
        var request = new RelationalWriteConstraintResolutionRequest(
            writePlan,
            new ReferenceResolverRequest(mappingSet, AbstractIdentitySchoolTestData.SchoolResource, [], []),
            new RelationalWriteExceptionClassification.UniqueConstraintViolation(referenceKeyConstraintName)
        );

        var result = _sut.Resolve(request);

        result.Should().Be(new RelationalWriteConstraintResolution.Unresolved(referenceKeyConstraintName));
    }

    private static ResolverFixture CreateFixture()
    {
        var sectionRootTable = new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), "Section"),
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_Section",
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
                    new DbColumnName("School_DocumentId"),
                    ColumnKind.DocumentFk,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    new JsonPathExpression(
                        "$.schoolReference",
                        [new JsonPathSegment.Property("schoolReference")]
                    ),
                    SchoolResource,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("SchoolReference_SchoolId"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32),
                    false,
                    new JsonPathExpression(
                        "$.schoolReference.schoolId",
                        [
                            new JsonPathSegment.Property("schoolReference"),
                            new JsonPathSegment.Property("schoolId"),
                        ]
                    ),
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("SectionIdentifier"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, 50),
                    false,
                    new JsonPathExpression(
                        "$.sectionIdentifier",
                        [new JsonPathSegment.Property("sectionIdentifier")]
                    ),
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("SessionType_DescriptorId"),
                    ColumnKind.DescriptorFk,
                    new RelationalScalarType(ScalarKind.Int64),
                    true,
                    new JsonPathExpression(
                        "$.sessionTypeDescriptor",
                        [new JsonPathSegment.Property("sessionTypeDescriptor")]
                    ),
                    SessionTypeDescriptorResource,
                    new ColumnStorage.Stored()
                ),
            ],
            [
                new TableConstraint.Unique(
                    "UX_Section_NK_16dd2d8140",
                    [new DbColumnName("School_DocumentId"), new DbColumnName("SectionIdentifier")]
                ),
                new TableConstraint.ForeignKey(
                    "FK_Section_SchoolRef_2ba9f31f84",
                    [new DbColumnName("School_DocumentId"), new DbColumnName("SchoolReference_SchoolId")],
                    new DbTableName(new DbSchemaName("edfi"), "School"),
                    [new DbColumnName("DocumentId"), new DbColumnName("SchoolId")]
                ),
                new TableConstraint.ForeignKey(
                    "FK_Section_SessionType_4a2f508e27",
                    [new DbColumnName("SessionType_DescriptorId")],
                    new DbTableName(new DbSchemaName("dms"), "Descriptor"),
                    [new DbColumnName("DocumentId")]
                ),
                new TableConstraint.ForeignKey(
                    "FK_Section_Document",
                    [new DbColumnName("DocumentId")],
                    new DbTableName(new DbSchemaName("dms"), "Document"),
                    [new DbColumnName("DocumentId")]
                ),
            ]
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

        var sectionMeetingTable = new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), "SectionMeeting"),
            new JsonPathExpression(
                "$.meetingTimes[*]",
                [new JsonPathSegment.Property("meetingTimes"), new JsonPathSegment.AnyArrayElement()]
            ),
            new TableKey(
                "PK_SectionMeeting",
                [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.CollectionKey)]
            ),
            [
                new DbColumnModel(
                    new DbColumnName("CollectionItemId"),
                    ColumnKind.CollectionKey,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
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
                    new DbColumnName("DayName"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, 20),
                    false,
                    new JsonPathExpression(
                        "$.meetingTimes[*].dayName",
                        [
                            new JsonPathSegment.Property("meetingTimes"),
                            new JsonPathSegment.AnyArrayElement(),
                            new JsonPathSegment.Property("dayName"),
                        ]
                    ),
                    null,
                    new ColumnStorage.Stored()
                ),
            ],
            [
                new TableConstraint.Unique(
                    "UX_SectionMeeting_DayName",
                    [new DbColumnName("DocumentId"), new DbColumnName("DayName")]
                ),
            ]
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.Collection,
                [new DbColumnName("CollectionItemId")],
                [new DbColumnName("DocumentId")],
                [new DbColumnName("DocumentId")],
                [
                    new CollectionSemanticIdentityBinding(
                        new JsonPathExpression("$.dayName", []),
                        new DbColumnName("DayName")
                    ),
                ]
            ),
        };

        var documentReferenceBinding = new DocumentReferenceBinding(
            true,
            new JsonPathExpression("$.schoolReference", [new JsonPathSegment.Property("schoolReference")]),
            sectionRootTable.Table,
            new DbColumnName("School_DocumentId"),
            SchoolResource,
            [
                new ReferenceIdentityBinding(
                    new JsonPathExpression(
                        "$.schoolReference.schoolId",
                        [
                            new JsonPathSegment.Property("schoolReference"),
                            new JsonPathSegment.Property("schoolId"),
                        ]
                    ),
                    new JsonPathExpression(
                        "$.schoolReference.schoolId",
                        [
                            new JsonPathSegment.Property("schoolReference"),
                            new JsonPathSegment.Property("schoolId"),
                        ]
                    ),
                    new DbColumnName("SchoolReference_SchoolId")
                ),
            ]
        );

        var resourceModel = new RelationalResourceModel(
            SectionResource,
            new DbSchemaName("edfi"),
            ResourceStorageKind.RelationalTables,
            sectionRootTable,
            [sectionRootTable, sectionMeetingTable],
            [documentReferenceBinding],
            [
                new DescriptorEdgeSource(
                    false,
                    new JsonPathExpression(
                        "$.sessionTypeDescriptor",
                        [new JsonPathSegment.Property("sessionTypeDescriptor")]
                    ),
                    sectionRootTable.Table,
                    new DbColumnName("SessionType_DescriptorId"),
                    SessionTypeDescriptorResource
                ),
            ]
        );

        var writePlan = new ResourceWritePlan(
            resourceModel,
            [
                new TableWritePlan(
                    sectionRootTable,
                    InsertSql: "insert into edfi.\"Section\" values (@DocumentId)",
                    UpdateSql: "update edfi.\"Section\" set \"SectionIdentifier\" = @SectionIdentifier where \"DocumentId\" = @DocumentId",
                    DeleteByParentSql: null,
                    BulkInsertBatching: new BulkInsertBatchingInfo(100, 1, 1000),
                    ColumnBindings:
                    [
                        new WriteColumnBinding(
                            sectionRootTable.Columns[0],
                            new WriteValueSource.DocumentId(),
                            "DocumentId"
                        ),
                    ],
                    KeyUnificationPlans: []
                ),
            ]
        );

        var sectionKey = new ResourceKeyEntry(1, SectionResource, "1.0.0", false);
        var schoolKey = new ResourceKeyEntry(2, SchoolResource, "1.0.0", false);
        var descriptorKey = new ResourceKeyEntry(3, SessionTypeDescriptorResource, "1.0.0", true);

        var mappingSet = new MappingSet(
            new MappingSetKey("schema-hash", SqlDialect.Pgsql, "v1"),
            new DerivedRelationalModelSet(
                new EffectiveSchemaInfo(
                    "1.0",
                    "v1",
                    "schema-hash",
                    3,
                    [1, 2, 3],
                    [new SchemaComponentInfo("ed-fi", "Ed-Fi", "1.0.0", false, "component-hash")],
                    [sectionKey, schoolKey, descriptorKey]
                ),
                SqlDialect.Pgsql,
                [new ProjectSchemaInfo("ed-fi", "Ed-Fi", "1.0.0", false, new DbSchemaName("edfi"))],
                [new ConcreteResourceModel(sectionKey, ResourceStorageKind.RelationalTables, resourceModel)],
                [],
                [],
                [],
                [
                    new DbTriggerInfo(
                        new DbTriggerName("TR_Section_ReferentialIdentity"),
                        sectionRootTable.Table,
                        [new DbColumnName("DocumentId")],
                        [new DbColumnName("SchoolReference_SchoolId"), new DbColumnName("SectionIdentifier")],
                        new TriggerKindParameters.ReferentialIdentityMaintenance(
                            sectionKey.ResourceKeyId,
                            SectionResource.ProjectName,
                            SectionResource.ResourceName,
                            [
                                new IdentityElementMapping(
                                    new DbColumnName("SchoolReference_SchoolId"),
                                    "$.schoolReference.schoolId",
                                    new RelationalScalarType(ScalarKind.Int32)
                                ),
                                new IdentityElementMapping(
                                    new DbColumnName("SectionIdentifier"),
                                    "$.sectionIdentifier",
                                    new RelationalScalarType(ScalarKind.String, 50)
                                ),
                            ]
                        )
                    ),
                ]
            ),
            new Dictionary<QualifiedResourceName, ResourceWritePlan> { [SectionResource] = writePlan },
            new Dictionary<QualifiedResourceName, ResourceReadPlan>(),
            new Dictionary<QualifiedResourceName, short>
            {
                [SectionResource] = sectionKey.ResourceKeyId,
                [SchoolResource] = schoolKey.ResourceKeyId,
                [SessionTypeDescriptorResource] = descriptorKey.ResourceKeyId,
            },
            new Dictionary<short, ResourceKeyEntry>
            {
                [sectionKey.ResourceKeyId] = sectionKey,
                [schoolKey.ResourceKeyId] = schoolKey,
                [descriptorKey.ResourceKeyId] = descriptorKey,
            },
            new Dictionary<QualifiedResourceName, IReadOnlyList<ResolvedSecurableElementPath>>()
        );

        return new ResolverFixture(
            writePlan,
            new ReferenceResolverRequest(mappingSet, SectionResource, [], []),
            RootNaturalKeyConstraintName: "UX_Section_NK_16dd2d8140",
            DocumentReferenceConstraintName: "FK_Section_SchoolRef_2ba9f31f84",
            DescriptorReferenceConstraintName: "FK_Section_SessionType_4a2f508e27",
            StructuralForeignKeyConstraintName: "FK_Section_Document",
            CollectionUniqueConstraintName: "UX_SectionMeeting_DayName"
        );
    }

    private static AbstractIdentityResolverFixture CreateSchoolAbstractIdentityFixture()
    {
        var (writePlan, mappingSet) = AbstractIdentitySchoolTestData.BuildSchoolWriteModel();

        return new AbstractIdentityResolverFixture(
            writePlan,
            new ReferenceResolverRequest(mappingSet, AbstractIdentitySchoolTestData.SchoolResource, [], []),
            NaturalKeyConstraintName: AbstractIdentitySchoolTestData.NaturalKeyConstraintName,
            ReferenceKeyConstraintName: AbstractIdentitySchoolTestData.ReferenceKeyConstraintName
        );
    }

    // Models an abstract identity table for a resource whose identity spans two columns, the way
    // AbstractIdentityTableAndUnionViewDerivationPass builds it: DocumentId primary key, the _NK natural-key
    // unique over both projected identity columns, the _RefKey helper that also appends DocumentId, and the
    // document FK. Proves the resolver matches multi-column natural keys, not just single-column ones.
    private static DbTableModel CompositeAbstractIdentityTable() =>
        new(
            new DbTableName(new DbSchemaName("edfi"), "CompositeIdentity"),
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_CompositeIdentity",
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
                    new DbColumnName("PartOneId"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32),
                    false,
                    new JsonPathExpression("$.partOneId", [new JsonPathSegment.Property("partOneId")]),
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("PartTwoId"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32),
                    false,
                    new JsonPathExpression("$.partTwoId", [new JsonPathSegment.Property("partTwoId")]),
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("Discriminator"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, 256),
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
            ],
            [
                new TableConstraint.Unique(
                    "UX_CompositeIdentity_NK",
                    [new DbColumnName("PartOneId"), new DbColumnName("PartTwoId")]
                ),
                new TableConstraint.Unique(
                    "UX_CompositeIdentity_RefKey",
                    [
                        new DbColumnName("PartOneId"),
                        new DbColumnName("PartTwoId"),
                        new DbColumnName("DocumentId"),
                    ]
                ),
                new TableConstraint.ForeignKey(
                    "FK_CompositeIdentity_Document",
                    [new DbColumnName("DocumentId")],
                    new DbTableName(new DbSchemaName("dms"), "Document"),
                    [new DbColumnName("DocumentId")],
                    OnDelete: ReferentialAction.Cascade
                ),
            ]
        );

    // Models an abstract identity table whose natural key includes a reference-backed identity column named
    // with a `_DocumentId` suffix. That suffix is the convention ReferenceBindingPass uses for document FK
    // columns, and reference-heavy natural keys really do carry such columns: the concrete Section NK is over
    // `School_DocumentId`, and an abstract resource like GeneralStudentProgramAssociation projects reference
    // identity components the same way. Proves the resolver compares NK columns against the *bare* `DocumentId`
    // key column by exact equality, so a `_DocumentId`-suffixed identity column is never mistaken for the
    // surrogate key (which would misclassify the natural-key violation as unresolved / non-409).
    private static DbTableModel ReferenceBackedAbstractIdentityTable() =>
        new(
            new DbTableName(new DbSchemaName("edfi"), "ReferenceBackedIdentity"),
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_ReferenceBackedIdentity",
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
                    new DbColumnName("EducationOrganization_DocumentId"),
                    ColumnKind.DocumentFk,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    new JsonPathExpression(
                        "$.educationOrganizationReference",
                        [new JsonPathSegment.Property("educationOrganizationReference")]
                    ),
                    new QualifiedResourceName("Ed-Fi", "EducationOrganization"),
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("ProgramName"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, 60),
                    false,
                    new JsonPathExpression("$.programName", [new JsonPathSegment.Property("programName")]),
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("Discriminator"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, 256),
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
            ],
            [
                new TableConstraint.Unique(
                    "UX_ReferenceBackedIdentity_NK",
                    [new DbColumnName("EducationOrganization_DocumentId"), new DbColumnName("ProgramName")]
                ),
                new TableConstraint.Unique(
                    "UX_ReferenceBackedIdentity_RefKey",
                    [
                        new DbColumnName("EducationOrganization_DocumentId"),
                        new DbColumnName("ProgramName"),
                        new DbColumnName("DocumentId"),
                    ]
                ),
                new TableConstraint.ForeignKey(
                    "FK_ReferenceBackedIdentity_Document",
                    [new DbColumnName("DocumentId")],
                    new DbTableName(new DbSchemaName("dms"), "Document"),
                    [new DbColumnName("DocumentId")],
                    OnDelete: ReferentialAction.Cascade
                ),
            ]
        );

    private sealed record ResolverFixture(
        ResourceWritePlan WritePlan,
        ReferenceResolverRequest ReferenceResolverRequest,
        string RootNaturalKeyConstraintName,
        string DocumentReferenceConstraintName,
        string DescriptorReferenceConstraintName,
        string StructuralForeignKeyConstraintName,
        string CollectionUniqueConstraintName
    );

    private sealed record AbstractIdentityResolverFixture(
        ResourceWritePlan WritePlan,
        ReferenceResolverRequest ReferenceResolverRequest,
        string NaturalKeyConstraintName,
        string ReferenceKeyConstraintName
    );
}
