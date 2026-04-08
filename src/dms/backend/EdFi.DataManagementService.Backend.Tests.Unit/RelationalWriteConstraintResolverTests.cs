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

    private sealed record ResolverFixture(
        ResourceWritePlan WritePlan,
        ReferenceResolverRequest ReferenceResolverRequest,
        string RootNaturalKeyConstraintName,
        string DocumentReferenceConstraintName,
        string DescriptorReferenceConstraintName,
        string StructuralForeignKeyConstraintName,
        string CollectionUniqueConstraintName
    );
}
