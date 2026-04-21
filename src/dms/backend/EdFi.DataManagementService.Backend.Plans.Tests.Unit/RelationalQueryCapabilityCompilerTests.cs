// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Backend.RelationalModel.Schema;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
public class Given_RelationalQueryCapabilityCompiler
{
    private static readonly DbSchemaName _edfiSchema = new("edfi");
    private static readonly QualifiedResourceName _studentResource = new("Ed-Fi", "Student");
    private static readonly QualifiedResourceName _studentAssociationResource = new(
        "Ed-Fi",
        "StudentAssociation"
    );
    private static readonly QualifiedResourceName _studentAcademicRecordResource = new(
        "Ed-Fi",
        "StudentAcademicRecord"
    );
    private static readonly QualifiedResourceName _academicSubjectDescriptorResource = new(
        "Ed-Fi",
        "AcademicSubjectDescriptor"
    );

    [Test]
    public void It_should_keep_unique_exact_root_scalar_and_descriptor_matches_supported()
    {
        var rootTable = CreateRootTable(
            "StudentAssociation",
            [
                ScalarColumn("SchoolYear", "$.schoolYear", ScalarKind.Int32),
                DescriptorColumn(
                    "AcademicSubjectDescriptorId",
                    "$.academicSubjectDescriptor",
                    _academicSubjectDescriptorResource
                ),
            ]
        );
        var model = CreateModel(
            rootTable,
            [],
            [
                DescriptorEdge(
                    "$.academicSubjectDescriptor",
                    rootTable.Table,
                    "AcademicSubjectDescriptorId",
                    _academicSubjectDescriptorResource
                ),
            ]
        );
        var concreteResource = CreateConcreteResource(
            model,
            ("schoolYear", [("$.schoolYear", "number")]),
            ("academicSubjectDescriptor", [("$.academicSubjectDescriptor", "string")])
        );

        var capability = new RelationalQueryCapabilityCompiler().Compile(concreteResource);

        capability.Support.Should().BeOfType<RelationalQuerySupport.Supported>();
        capability.UnsupportedFieldsByQueryField.Should().BeEmpty();
        capability
            .SupportedFieldsByQueryField["schoolYear"]
            .Target.Should()
            .Be(new RelationalQueryFieldTarget.RootColumn(new DbColumnName("SchoolYear")));
        capability
            .SupportedFieldsByQueryField["academicSubjectDescriptor"]
            .Target.Should()
            .Be(
                new RelationalQueryFieldTarget.DescriptorIdColumn(
                    new DbColumnName("AcademicSubjectDescriptorId"),
                    _academicSubjectDescriptorResource
                )
            );
    }

    [Test]
    public void It_should_omit_root_scalar_query_fields_when_api_schema_type_does_not_match_column_type()
    {
        var rootTable = CreateRootTable(
            "StudentAssociation",
            [ScalarColumn("SchoolYear", "$.schoolYear", ScalarKind.Int32)]
        );
        var concreteResource = CreateConcreteResource(
            CreateModel(rootTable, [], []),
            ("schoolYear", [("$.schoolYear", "string")])
        );

        var capability = new RelationalQueryCapabilityCompiler().Compile(concreteResource);

        capability.Support.Should().BeOfType<RelationalQuerySupport.Omitted>();
        capability
            .UnsupportedFieldsByQueryField["schoolYear"]
            .FailureKind.Should()
            .Be(RelationalQueryFieldFailureKind.UnmappedPath);
        capability.SupportedFieldsByQueryField.Should().BeEmpty();
    }

    [TestCase(
        "CourseTranscript",
        "studentUniqueId",
        "$.studentReference.studentAcademicRecordUniqueId",
        "StudentAcademicRecord",
        "$.studentAcademicRecordReference",
        "$.studentReference.studentUniqueId",
        "$.studentAcademicRecordReference.studentUniqueId",
        "StudentAcademicRecord_DocumentId",
        "StudentAcademicRecord_StudentUniqueId"
    )]
    [TestCase(
        "StudentAssessmentRegistration",
        "studentUniqueId",
        "$.studentReference.studentEducationOrganizationAssociationUniqueId",
        "StudentEducationOrganizationAssociation",
        "$.studentEducationOrganizationAssociationReference",
        "$.studentReference.studentUniqueId",
        "$.studentEducationOrganizationAssociationReference.studentUniqueId",
        "StudentEducationOrganizationAssociation_DocumentId",
        "StudentEducationOrganizationAssociation_StudentUniqueId"
    )]
    [TestCase(
        "StudentAssessmentRegistration",
        "scheduledStudentUniqueId",
        "$.scheduledStudentReference.studentEducationOrganizationAssessmentAccommodationUniqueId",
        "StudentEducationOrganizationAssessmentAccommodation",
        "$.scheduledStudentEducationOrganizationAssessmentAccommodationReference",
        "$.studentReference.studentUniqueId",
        "$.scheduledStudentEducationOrganizationAssessmentAccommodationReference.studentUniqueId",
        "ScheduledStudentEducationOrganizationAssessmentAccommodation_DocumentId",
        "ScheduledStudentEducationOrganizationAssessmentAccommodation_StudentUniqueId"
    )]
    [TestCase(
        "StudentCTEProgramAssociation",
        "studentUniqueId",
        "$.studentReference.generalStudentProgramAssociationUniqueId",
        "Student",
        "$.studentReference",
        "$.studentReference.studentUniqueId",
        "$.studentReference.studentUniqueId",
        "Student_DocumentId",
        "Student_StudentUniqueId"
    )]
    [TestCase(
        "BusRoute",
        "staffUniqueId",
        "$.staffReference.staffEducationOrganizationAssignmentAssociationUniqueId",
        "StaffEducationOrganizationAssignmentAssociation",
        "$.staffEducationOrganizationAssignmentAssociationReference",
        "$.staffReference.staffUniqueId",
        "$.staffEducationOrganizationAssignmentAssociationReference.staffUniqueId",
        "StaffEducationOrganizationAssignmentAssociation_DocumentId",
        "StaffEducationOrganizationAssignmentAssociation_StaffUniqueId"
    )]
    public void It_should_resolve_virtual_reference_identity_aliases_to_local_root_binding_columns(
        string rootTableName,
        string queryFieldName,
        string queryPath,
        string targetResourceName,
        string referenceObjectPath,
        string identityPath,
        string referencePath,
        string fkColumn,
        string expectedColumn
    )
    {
        var targetResource = new QualifiedResourceName("Ed-Fi", targetResourceName);
        var rootTable = CreateRootTable(
            rootTableName,
            [
                DocumentFkColumn(fkColumn, referenceObjectPath, targetResource),
                ScalarColumn(expectedColumn, referencePath),
            ]
        );
        var binding = CreateBinding(
            referenceObjectPath,
            rootTable.Table,
            fkColumn,
            targetResource,
            identityPath,
            referencePath,
            expectedColumn
        );
        var concreteResource = CreateConcreteResource(
            CreateModel(new QualifiedResourceName("Ed-Fi", rootTableName), rootTable, [binding], []),
            (queryFieldName, [(queryPath, "string")])
        );

        var capability = new RelationalQueryCapabilityCompiler().Compile(concreteResource);

        capability.Support.Should().BeOfType<RelationalQuerySupport.Supported>();
        capability.UnsupportedFieldsByQueryField.Should().BeEmpty();

        var supportedField = capability.SupportedFieldsByQueryField[queryFieldName];
        supportedField.Path.Path.Canonical.Should().Be(queryPath);
        supportedField
            .Target.Should()
            .Be(new RelationalQueryFieldTarget.RootColumn(new DbColumnName(expectedColumn)));
    }

    [TestCase("$.studentReference.notStudentUniqueId")]
    [TestCase("$.notStudentReference.studentAcademicRecordUniqueId")]
    public void It_should_not_resolve_virtual_reference_identity_aliases_for_near_miss_paths(string queryPath)
    {
        var rootTable = CreateCourseTranscriptRootTable();
        var concreteResource = CreateConcreteResource(
            CreateModel(
                new QualifiedResourceName("Ed-Fi", "CourseTranscript"),
                rootTable,
                [CreateStudentAcademicRecordBinding(rootTable.Table)],
                []
            ),
            ("studentUniqueId", [(queryPath, "string")])
        );

        var capability = new RelationalQueryCapabilityCompiler().Compile(concreteResource);

        capability.Support.Should().BeOfType<RelationalQuerySupport.Omitted>();
        capability
            .UnsupportedFieldsByQueryField["studentUniqueId"]
            .FailureKind.Should()
            .Be(RelationalQueryFieldFailureKind.UnmappedPath);
        capability.SupportedFieldsByQueryField.Should().BeEmpty();
    }

    [Test]
    public void It_should_not_use_generic_unique_id_fallback_for_non_unique_id_candidates()
    {
        var rootTable = CreateRootTable(
            "StudentAssociation",
            [
                DocumentFkColumn("Student_DocumentId", "$.studentReference", _studentResource),
                ScalarColumn("Student_SchoolId", "$.studentReference.schoolId"),
            ]
        );
        var binding = CreateBinding(
            "$.studentReference",
            rootTable.Table,
            "Student_DocumentId",
            _studentResource,
            "$.studentReference.schoolId",
            "$.studentReference.schoolId",
            "Student_SchoolId"
        );
        var concreteResource = CreateConcreteResource(
            CreateModel(rootTable, [binding], []),
            ("schoolId", [("$.studentReference.generalStudentProgramAssociationUniqueId", "number")])
        );

        var capability = new RelationalQueryCapabilityCompiler().Compile(concreteResource);

        capability.Support.Should().BeOfType<RelationalQuerySupport.Omitted>();
        capability
            .UnsupportedFieldsByQueryField["schoolId"]
            .FailureKind.Should()
            .Be(RelationalQueryFieldFailureKind.UnmappedPath);
        capability.SupportedFieldsByQueryField.Should().BeEmpty();
    }

    [Test]
    public void It_should_classify_generic_unique_id_fallback_matches_across_reference_sites_as_ambiguous()
    {
        var rootTable = CreateRootTable(
            "StudentAssociation",
            [
                DocumentFkColumn("PrimaryStudent_DocumentId", "$.primaryStudentReference", _studentResource),
                ScalarColumn("PrimaryStudent_StudentUniqueId", "$.primaryStudentReference.studentUniqueId"),
                DocumentFkColumn(
                    "SecondaryStudent_DocumentId",
                    "$.secondaryStudentReference",
                    _studentResource
                ),
                ScalarColumn(
                    "SecondaryStudent_StudentUniqueId",
                    "$.secondaryStudentReference.studentUniqueId"
                ),
            ]
        );
        var concreteResource = CreateConcreteResource(
            CreateModel(
                rootTable,
                [
                    CreateBinding(
                        "$.primaryStudentReference",
                        rootTable.Table,
                        "PrimaryStudent_DocumentId",
                        _studentResource,
                        "$.studentReference.studentUniqueId",
                        "$.primaryStudentReference.studentUniqueId",
                        "PrimaryStudent_StudentUniqueId"
                    ),
                    CreateBinding(
                        "$.secondaryStudentReference",
                        rootTable.Table,
                        "SecondaryStudent_DocumentId",
                        _studentResource,
                        "$.studentReference.studentUniqueId",
                        "$.secondaryStudentReference.studentUniqueId",
                        "SecondaryStudent_StudentUniqueId"
                    ),
                ],
                []
            ),
            ("studentUniqueId", [("$.studentReference.generalStudentProgramAssociationUniqueId", "string")])
        );

        var capability = new RelationalQueryCapabilityCompiler().Compile(concreteResource);

        capability.Support.Should().BeOfType<RelationalQuerySupport.Omitted>();
        capability
            .UnsupportedFieldsByQueryField["studentUniqueId"]
            .FailureKind.Should()
            .Be(RelationalQueryFieldFailureKind.AmbiguousRootTarget);
        capability.SupportedFieldsByQueryField.Should().BeEmpty();
    }

    [Test]
    public void It_should_collapse_same_site_virtual_alias_duplicates_to_representative_binding_column()
    {
        var rootTable = CreateRootTable(
            "CourseTranscript",
            [
                DocumentFkColumn(
                    "StudentAcademicRecord_DocumentId",
                    "$.studentAcademicRecordReference",
                    _studentAcademicRecordResource
                ),
                ScalarCanonicalColumn("StudentAcademicRecordStudentUniqueIdCanonical"),
                ScalarColumn(
                    "StudentAcademicRecord_StudentUniqueIdAlias",
                    "$.studentAcademicRecordReference.studentUniqueId",
                    ScalarKind.String,
                    new ColumnStorage.UnifiedAlias(
                        new DbColumnName("StudentAcademicRecordStudentUniqueIdCanonical"),
                        new DbColumnName("StudentAcademicRecord_DocumentId")
                    )
                ),
                ScalarColumn(
                    "StudentAcademicRecord_StudentUniqueIdDuplicateAlias",
                    "$.studentAcademicRecordReference.studentUniqueId",
                    ScalarKind.String,
                    new ColumnStorage.UnifiedAlias(
                        new DbColumnName("StudentAcademicRecordStudentUniqueIdCanonical"),
                        new DbColumnName("StudentAcademicRecord_DocumentId")
                    )
                ),
            ]
        );
        var binding = new DocumentReferenceBinding(
            IsIdentityComponent: false,
            ReferenceObjectPath: Path("$.studentAcademicRecordReference"),
            Table: rootTable.Table,
            FkColumn: new DbColumnName("StudentAcademicRecord_DocumentId"),
            TargetResource: _studentAcademicRecordResource,
            IdentityBindings:
            [
                ReferenceIdentity(
                    "$.studentReference.studentUniqueId",
                    "$.studentAcademicRecordReference.studentUniqueId",
                    "StudentAcademicRecord_StudentUniqueIdAlias"
                ),
                ReferenceIdentity(
                    "$.studentReference.studentUniqueId",
                    "$.studentAcademicRecordReference.studentUniqueId",
                    "StudentAcademicRecord_StudentUniqueIdDuplicateAlias"
                ),
            ]
        );
        var concreteResource = CreateConcreteResource(
            CreateModel(new QualifiedResourceName("Ed-Fi", "CourseTranscript"), rootTable, [binding], []),
            ("studentUniqueId", [("$.studentReference.studentAcademicRecordUniqueId", "string")])
        );

        var capability = new RelationalQueryCapabilityCompiler().Compile(concreteResource);

        capability.Support.Should().BeOfType<RelationalQuerySupport.Supported>();
        capability.UnsupportedFieldsByQueryField.Should().BeEmpty();
        capability
            .SupportedFieldsByQueryField["studentUniqueId"]
            .Target.Should()
            .Be(
                new RelationalQueryFieldTarget.RootColumn(
                    new DbColumnName("StudentAcademicRecord_StudentUniqueIdAlias")
                )
            );
    }

    [Test]
    public void It_should_classify_cross_site_target_resource_alias_matches_as_ambiguous()
    {
        var rootTable = CreateRootTable(
            "CourseTranscript",
            [
                DocumentFkColumn(
                    "PrimaryStudentAcademicRecord_DocumentId",
                    "$.primaryStudentAcademicRecordReference",
                    _studentAcademicRecordResource
                ),
                ScalarColumn(
                    "PrimaryStudentAcademicRecord_StudentUniqueId",
                    "$.primaryStudentAcademicRecordReference.studentUniqueId"
                ),
                DocumentFkColumn(
                    "SecondaryStudentAcademicRecord_DocumentId",
                    "$.secondaryStudentAcademicRecordReference",
                    _studentAcademicRecordResource
                ),
                ScalarColumn(
                    "SecondaryStudentAcademicRecord_StudentUniqueId",
                    "$.secondaryStudentAcademicRecordReference.studentUniqueId"
                ),
            ]
        );
        var concreteResource = CreateConcreteResource(
            CreateModel(
                new QualifiedResourceName("Ed-Fi", "CourseTranscript"),
                rootTable,
                [
                    CreateBinding(
                        "$.primaryStudentAcademicRecordReference",
                        rootTable.Table,
                        "PrimaryStudentAcademicRecord_DocumentId",
                        _studentAcademicRecordResource,
                        "$.studentReference.studentUniqueId",
                        "$.primaryStudentAcademicRecordReference.studentUniqueId",
                        "PrimaryStudentAcademicRecord_StudentUniqueId"
                    ),
                    CreateBinding(
                        "$.secondaryStudentAcademicRecordReference",
                        rootTable.Table,
                        "SecondaryStudentAcademicRecord_DocumentId",
                        _studentAcademicRecordResource,
                        "$.studentReference.studentUniqueId",
                        "$.secondaryStudentAcademicRecordReference.studentUniqueId",
                        "SecondaryStudentAcademicRecord_StudentUniqueId"
                    ),
                ],
                []
            ),
            ("studentUniqueId", [("$.studentReference.studentAcademicRecordUniqueId", "string")])
        );

        var capability = new RelationalQueryCapabilityCompiler().Compile(concreteResource);

        capability.Support.Should().BeOfType<RelationalQuerySupport.Omitted>();
        capability
            .UnsupportedFieldsByQueryField["studentUniqueId"]
            .FailureKind.Should()
            .Be(RelationalQueryFieldFailureKind.AmbiguousRootTarget);
        capability.SupportedFieldsByQueryField.Should().BeEmpty();
    }

    [Test]
    public void It_should_preserve_array_crossing_classification_before_virtual_alias_fallback()
    {
        var rootTable = CreateCourseTranscriptRootTable();
        var concreteResource = CreateConcreteResource(
            CreateModel(
                new QualifiedResourceName("Ed-Fi", "CourseTranscript"),
                rootTable,
                [CreateStudentAcademicRecordBinding(rootTable.Table)],
                []
            ),
            ("studentUniqueId", [("$.students[*].studentReference.studentAcademicRecordUniqueId", "string")])
        );

        var capability = new RelationalQueryCapabilityCompiler().Compile(concreteResource);

        capability.Support.Should().BeOfType<RelationalQuerySupport.Omitted>();
        capability
            .UnsupportedFieldsByQueryField["studentUniqueId"]
            .FailureKind.Should()
            .Be(RelationalQueryFieldFailureKind.ArrayCrossing);
        capability.SupportedFieldsByQueryField.Should().BeEmpty();
    }

    [Test]
    public void It_should_preserve_non_root_table_classification_before_virtual_alias_fallback()
    {
        var rootTable = CreateCourseTranscriptRootTable();
        var childTable = CreateChildTable(
            "CourseTranscript_Credits",
            [ScalarColumn("StudentAcademicRecordAlias", "$.studentReference.studentAcademicRecordUniqueId")]
        );
        var concreteResource = CreateConcreteResource(
            CreateModel(
                new QualifiedResourceName("Ed-Fi", "CourseTranscript"),
                rootTable,
                [rootTable, childTable],
                [CreateStudentAcademicRecordBinding(rootTable.Table)],
                []
            ),
            ("studentUniqueId", [("$.studentReference.studentAcademicRecordUniqueId", "string")])
        );

        var capability = new RelationalQueryCapabilityCompiler().Compile(concreteResource);

        capability.Support.Should().BeOfType<RelationalQuerySupport.Omitted>();
        capability
            .UnsupportedFieldsByQueryField["studentUniqueId"]
            .FailureKind.Should()
            .Be(RelationalQueryFieldFailureKind.NonRootTable);
        capability.SupportedFieldsByQueryField.Should().BeEmpty();
    }

    [Test]
    public void It_should_not_resolve_virtual_aliases_by_physical_column_name_convention()
    {
        var rootTable = CreateRootTable(
            "CourseTranscript",
            [
                DocumentFkColumn(
                    "StudentAcademicRecord_DocumentId",
                    "$.studentAcademicRecordReference",
                    _studentAcademicRecordResource
                ),
                ScalarColumn(
                    "StudentAcademicRecord_StudentUniqueId",
                    "$.studentAcademicRecordReference.staffUniqueId"
                ),
            ]
        );
        var binding = CreateBinding(
            "$.studentAcademicRecordReference",
            rootTable.Table,
            "StudentAcademicRecord_DocumentId",
            _studentAcademicRecordResource,
            "$.staffReference.staffUniqueId",
            "$.studentAcademicRecordReference.staffUniqueId",
            "StudentAcademicRecord_StudentUniqueId"
        );
        var concreteResource = CreateConcreteResource(
            CreateModel(new QualifiedResourceName("Ed-Fi", "CourseTranscript"), rootTable, [binding], []),
            ("studentUniqueId", [("$.studentReference.studentAcademicRecordUniqueId", "string")])
        );

        var capability = new RelationalQueryCapabilityCompiler().Compile(concreteResource);

        capability.Support.Should().BeOfType<RelationalQuerySupport.Omitted>();
        capability
            .UnsupportedFieldsByQueryField["studentUniqueId"]
            .FailureKind.Should()
            .Be(RelationalQueryFieldFailureKind.UnmappedPath);
        capability.SupportedFieldsByQueryField.Should().BeEmpty();
    }

    [Test]
    public void It_should_collapse_same_site_exact_root_scalar_duplicates_to_representative_binding_column()
    {
        var rootTable = CreateRootTable(
            "StudentAssociation",
            [
                DocumentFkColumn("Student_DocumentId", "$.studentReference", _studentResource),
                ScalarCanonicalColumn("StudentUniqueIdCanonical"),
                ScalarColumn(
                    "Student_StudentUniqueIdAlias",
                    "$.studentReference.studentUniqueId",
                    ScalarKind.String,
                    new ColumnStorage.UnifiedAlias(
                        new DbColumnName("StudentUniqueIdCanonical"),
                        new DbColumnName("Student_DocumentId")
                    )
                ),
                ScalarColumn(
                    "Student_StudentUniqueIdDuplicateAlias",
                    "$.studentReference.studentUniqueId",
                    ScalarKind.String,
                    new ColumnStorage.UnifiedAlias(
                        new DbColumnName("StudentUniqueIdCanonical"),
                        new DbColumnName("Student_DocumentId")
                    )
                ),
            ]
        );
        var binding = new DocumentReferenceBinding(
            IsIdentityComponent: false,
            ReferenceObjectPath: Path("$.studentReference"),
            Table: rootTable.Table,
            FkColumn: new DbColumnName("Student_DocumentId"),
            TargetResource: _studentResource,
            IdentityBindings:
            [
                ReferenceIdentity(
                    "$.studentReference.studentUniqueId",
                    "$.studentReference.studentUniqueId",
                    "Student_StudentUniqueIdAlias"
                ),
                ReferenceIdentity(
                    "$.studentReference.studentUniqueId",
                    "$.studentReference.studentUniqueId",
                    "Student_StudentUniqueIdDuplicateAlias"
                ),
            ]
        );
        var concreteResource = CreateConcreteResource(
            CreateModel(rootTable, [binding], []),
            ("studentUniqueId", [("$.studentReference.studentUniqueId", "string")])
        );

        var capability = new RelationalQueryCapabilityCompiler().Compile(concreteResource);

        capability.Support.Should().BeOfType<RelationalQuerySupport.Supported>();
        capability.UnsupportedFieldsByQueryField.Should().BeEmpty();
        capability
            .SupportedFieldsByQueryField["studentUniqueId"]
            .Target.Should()
            .Be(new RelationalQueryFieldTarget.RootColumn(new DbColumnName("Student_StudentUniqueIdAlias")));
    }

    [Test]
    public void It_should_collapse_same_site_exact_root_descriptor_duplicates_to_representative_binding_column()
    {
        var rootTable = CreateRootTable(
            "StudentAssociation",
            [
                DocumentFkColumn("Student_DocumentId", "$.studentReference", _studentResource),
                DescriptorCanonicalColumn(
                    "StudentAcademicSubjectDescriptorUnifiedId",
                    _academicSubjectDescriptorResource
                ),
                DescriptorColumn(
                    "Student_AcademicSubjectDescriptorId",
                    "$.studentReference.academicSubjectDescriptor",
                    _academicSubjectDescriptorResource,
                    new ColumnStorage.UnifiedAlias(
                        new DbColumnName("StudentAcademicSubjectDescriptorUnifiedId"),
                        new DbColumnName("Student_DocumentId")
                    )
                ),
                DescriptorColumn(
                    "Student_AcademicSubjectDescriptorDuplicateId",
                    "$.studentReference.academicSubjectDescriptor",
                    _academicSubjectDescriptorResource,
                    new ColumnStorage.UnifiedAlias(
                        new DbColumnName("StudentAcademicSubjectDescriptorUnifiedId"),
                        new DbColumnName("Student_DocumentId")
                    )
                ),
            ]
        );
        var binding = new DocumentReferenceBinding(
            IsIdentityComponent: false,
            ReferenceObjectPath: Path("$.studentReference"),
            Table: rootTable.Table,
            FkColumn: new DbColumnName("Student_DocumentId"),
            TargetResource: _studentResource,
            IdentityBindings:
            [
                ReferenceIdentity(
                    "$.studentReference.academicSubjectDescriptor",
                    "$.studentReference.academicSubjectDescriptor",
                    "Student_AcademicSubjectDescriptorId"
                ),
                ReferenceIdentity(
                    "$.studentReference.academicSubjectDescriptor",
                    "$.studentReference.academicSubjectDescriptor",
                    "Student_AcademicSubjectDescriptorDuplicateId"
                ),
            ]
        );
        var concreteResource = CreateConcreteResource(
            CreateModel(
                rootTable,
                [binding],
                [
                    DescriptorEdge(
                        "$.studentReference.academicSubjectDescriptor",
                        rootTable.Table,
                        "Student_AcademicSubjectDescriptorId",
                        _academicSubjectDescriptorResource
                    ),
                    DescriptorEdge(
                        "$.studentReference.academicSubjectDescriptor",
                        rootTable.Table,
                        "Student_AcademicSubjectDescriptorDuplicateId",
                        _academicSubjectDescriptorResource
                    ),
                ]
            ),
            ("academicSubjectDescriptor", [("$.studentReference.academicSubjectDescriptor", "string")])
        );

        var capability = new RelationalQueryCapabilityCompiler().Compile(concreteResource);

        capability.Support.Should().BeOfType<RelationalQuerySupport.Supported>();
        capability.UnsupportedFieldsByQueryField.Should().BeEmpty();
        capability
            .SupportedFieldsByQueryField["academicSubjectDescriptor"]
            .Target.Should()
            .Be(
                new RelationalQueryFieldTarget.DescriptorIdColumn(
                    new DbColumnName("Student_AcademicSubjectDescriptorId"),
                    _academicSubjectDescriptorResource
                )
            );
    }

    [Test]
    public void It_should_leave_cross_site_exact_root_scalar_duplicates_ambiguous()
    {
        var rootTable = CreateRootTable(
            "StudentAssociation",
            [
                DocumentFkColumn("PrimaryStudent_DocumentId", "$.primaryStudentReference", _studentResource),
                ScalarColumn("PrimaryStudent_StudentUniqueId", "$.studentReference.studentUniqueId"),
                DocumentFkColumn(
                    "SecondaryStudent_DocumentId",
                    "$.secondaryStudentReference",
                    _studentResource
                ),
                ScalarColumn("SecondaryStudent_StudentUniqueId", "$.studentReference.studentUniqueId"),
            ]
        );
        var concreteResource = CreateConcreteResource(
            CreateModel(
                rootTable,
                [
                    CreateBinding(
                        "$.primaryStudentReference",
                        rootTable.Table,
                        "PrimaryStudent_DocumentId",
                        "$.studentReference.studentUniqueId",
                        "$.studentReference.studentUniqueId",
                        "PrimaryStudent_StudentUniqueId"
                    ),
                    CreateBinding(
                        "$.secondaryStudentReference",
                        rootTable.Table,
                        "SecondaryStudent_DocumentId",
                        "$.studentReference.studentUniqueId",
                        "$.studentReference.studentUniqueId",
                        "SecondaryStudent_StudentUniqueId"
                    ),
                ],
                []
            ),
            ("studentUniqueId", [("$.studentReference.studentUniqueId", "string")])
        );

        var capability = new RelationalQueryCapabilityCompiler().Compile(concreteResource);

        capability.Support.Should().BeOfType<RelationalQuerySupport.Omitted>();
        capability
            .UnsupportedFieldsByQueryField["studentUniqueId"]
            .FailureKind.Should()
            .Be(RelationalQueryFieldFailureKind.AmbiguousRootTarget);
        capability.SupportedFieldsByQueryField.Should().BeEmpty();
    }

    private static ConcreteResourceModel CreateConcreteResource(
        RelationalResourceModel model,
        params (string QueryFieldName, (string Path, string Type)[] Paths)[] queryFields
    )
    {
        return new ConcreteResourceModel(
            new ResourceKeyEntry(1, model.Resource, "5.2.0", false),
            ResourceStorageKind.RelationalTables,
            model
        )
        {
            QueryFieldMappingsByQueryField = CreateQueryFieldMappings(queryFields),
        };
    }

    private static RelationalResourceModel CreateModel(
        DbTableModel rootTable,
        IReadOnlyList<DocumentReferenceBinding> documentReferenceBindings,
        IReadOnlyList<DescriptorEdgeSource> descriptorEdgeSources
    )
    {
        return CreateModel(
            _studentAssociationResource,
            rootTable,
            documentReferenceBindings,
            descriptorEdgeSources
        );
    }

    private static RelationalResourceModel CreateModel(
        QualifiedResourceName resource,
        DbTableModel rootTable,
        IReadOnlyList<DocumentReferenceBinding> documentReferenceBindings,
        IReadOnlyList<DescriptorEdgeSource> descriptorEdgeSources
    )
    {
        return CreateModel(
            resource,
            rootTable,
            [rootTable],
            documentReferenceBindings,
            descriptorEdgeSources
        );
    }

    private static RelationalResourceModel CreateModel(
        QualifiedResourceName resource,
        DbTableModel rootTable,
        IReadOnlyList<DbTableModel> tablesInDependencyOrder,
        IReadOnlyList<DocumentReferenceBinding> documentReferenceBindings,
        IReadOnlyList<DescriptorEdgeSource> descriptorEdgeSources
    )
    {
        return new RelationalResourceModel(
            Resource: resource,
            PhysicalSchema: _edfiSchema,
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootTable,
            TablesInDependencyOrder: tablesInDependencyOrder,
            DocumentReferenceBindings: documentReferenceBindings,
            DescriptorEdgeSources: descriptorEdgeSources
        );
    }

    private static DbTableModel CreateCourseTranscriptRootTable()
    {
        return CreateRootTable(
            "CourseTranscript",
            [
                DocumentFkColumn(
                    "StudentAcademicRecord_DocumentId",
                    "$.studentAcademicRecordReference",
                    _studentAcademicRecordResource
                ),
                ScalarColumn(
                    "StudentAcademicRecord_StudentUniqueId",
                    "$.studentAcademicRecordReference.studentUniqueId"
                ),
            ]
        );
    }

    private static DbTableModel CreateRootTable(string tableName, IReadOnlyList<DbColumnModel> extraColumns)
    {
        return new DbTableModel(
            Table: new DbTableName(_edfiSchema, tableName),
            JsonScope: Path("$"),
            Key: new TableKey(
                ConstraintName: $"PK_{tableName}",
                Columns: [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            Columns:
            [
                new DbColumnModel(
                    ColumnName: new DbColumnName("DocumentId"),
                    Kind: ColumnKind.ParentKeyPart,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                .. extraColumns,
            ],
            Constraints: []
        );
    }

    private static DbTableModel CreateChildTable(string tableName, IReadOnlyList<DbColumnModel> extraColumns)
    {
        return new DbTableModel(
            Table: new DbTableName(_edfiSchema, tableName),
            JsonScope: Path("$.items[*]"),
            Key: new TableKey(
                ConstraintName: $"PK_{tableName}",
                Columns:
                [
                    new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart),
                    new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.CollectionKey),
                ]
            ),
            Columns:
            [
                new DbColumnModel(
                    ColumnName: new DbColumnName("DocumentId"),
                    Kind: ColumnKind.ParentKeyPart,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("CollectionItemId"),
                    Kind: ColumnKind.CollectionKey,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                .. extraColumns,
            ],
            Constraints: []
        );
    }

    private static DbColumnModel DocumentFkColumn(
        string columnName,
        string sourcePath,
        QualifiedResourceName targetResource
    )
    {
        return new DbColumnModel(
            ColumnName: new DbColumnName(columnName),
            Kind: ColumnKind.DocumentFk,
            ScalarType: new RelationalScalarType(ScalarKind.Int64),
            IsNullable: true,
            SourceJsonPath: Path(sourcePath),
            TargetResource: targetResource
        );
    }

    private static DbColumnModel ScalarCanonicalColumn(string columnName)
    {
        return new DbColumnModel(
            ColumnName: new DbColumnName(columnName),
            Kind: ColumnKind.Scalar,
            ScalarType: new RelationalScalarType(ScalarKind.String, MaxLength: 32),
            IsNullable: true,
            SourceJsonPath: null,
            TargetResource: null
        );
    }

    private static DbColumnModel ScalarColumn(string columnName, string sourcePath)
    {
        return ScalarColumn(columnName, sourcePath, ScalarKind.String);
    }

    private static DbColumnModel ScalarColumn(
        string columnName,
        string sourcePath,
        ScalarKind scalarKind,
        ColumnStorage? storage = null
    )
    {
        return new DbColumnModel(
            ColumnName: new DbColumnName(columnName),
            Kind: ColumnKind.Scalar,
            ScalarType: scalarKind == ScalarKind.String
                ? new RelationalScalarType(scalarKind, MaxLength: 32)
                : new RelationalScalarType(scalarKind),
            IsNullable: true,
            SourceJsonPath: Path(sourcePath),
            TargetResource: null,
            Storage: storage ?? new ColumnStorage.Stored()
        );
    }

    private static DbColumnModel DescriptorCanonicalColumn(
        string columnName,
        QualifiedResourceName descriptorResource
    )
    {
        return new DbColumnModel(
            ColumnName: new DbColumnName(columnName),
            Kind: ColumnKind.DescriptorFk,
            ScalarType: new RelationalScalarType(ScalarKind.Int64),
            IsNullable: true,
            SourceJsonPath: null,
            TargetResource: descriptorResource
        );
    }

    private static DbColumnModel DescriptorColumn(
        string columnName,
        string sourcePath,
        QualifiedResourceName descriptorResource,
        ColumnStorage? storage = null
    )
    {
        return new DbColumnModel(
            ColumnName: new DbColumnName(columnName),
            Kind: ColumnKind.DescriptorFk,
            ScalarType: new RelationalScalarType(ScalarKind.Int64),
            IsNullable: true,
            SourceJsonPath: Path(sourcePath),
            TargetResource: descriptorResource,
            Storage: storage ?? new ColumnStorage.Stored()
        );
    }

    private static DocumentReferenceBinding CreateBinding(
        string referenceObjectPath,
        DbTableName table,
        string fkColumn,
        string identityPath,
        string referencePath,
        string column
    )
    {
        return CreateBinding(
            referenceObjectPath,
            table,
            fkColumn,
            _studentResource,
            identityPath,
            referencePath,
            column
        );
    }

    private static DocumentReferenceBinding CreateBinding(
        string referenceObjectPath,
        DbTableName table,
        string fkColumn,
        QualifiedResourceName targetResource,
        string identityPath,
        string referencePath,
        string column
    )
    {
        return new DocumentReferenceBinding(
            IsIdentityComponent: false,
            ReferenceObjectPath: Path(referenceObjectPath),
            Table: table,
            FkColumn: new DbColumnName(fkColumn),
            TargetResource: targetResource,
            IdentityBindings: [ReferenceIdentity(identityPath, referencePath, column)]
        );
    }

    private static DocumentReferenceBinding CreateStudentAcademicRecordBinding(DbTableName table)
    {
        return CreateBinding(
            "$.studentAcademicRecordReference",
            table,
            "StudentAcademicRecord_DocumentId",
            _studentAcademicRecordResource,
            "$.studentReference.studentUniqueId",
            "$.studentAcademicRecordReference.studentUniqueId",
            "StudentAcademicRecord_StudentUniqueId"
        );
    }

    private static ReferenceIdentityBinding ReferenceIdentity(
        string identityPath,
        string referencePath,
        string column
    )
    {
        return new ReferenceIdentityBinding(
            IdentityJsonPath: Path(identityPath),
            ReferenceJsonPath: Path(referencePath),
            Column: new DbColumnName(column)
        );
    }

    private static DescriptorEdgeSource DescriptorEdge(
        string descriptorValuePath,
        DbTableName table,
        string fkColumn,
        QualifiedResourceName descriptorResource
    )
    {
        return new DescriptorEdgeSource(
            IsIdentityComponent: false,
            DescriptorValuePath: Path(descriptorValuePath),
            Table: table,
            FkColumn: new DbColumnName(fkColumn),
            DescriptorResource: descriptorResource
        );
    }

    private static IReadOnlyDictionary<string, RelationalQueryFieldMapping> CreateQueryFieldMappings(
        params (string QueryFieldName, (string Path, string Type)[] Paths)[] queryFields
    )
    {
        return queryFields.ToDictionary(
            static queryField => queryField.QueryFieldName,
            static queryField => new RelationalQueryFieldMapping(
                queryField.QueryFieldName,
                queryField
                    .Paths.Select(static path => new RelationalQueryFieldPath(
                        JsonPathExpressionCompiler.Compile(path.Path),
                        path.Type
                    ))
                    .ToArray()
            ),
            StringComparer.Ordinal
        );
    }

    private static JsonPathExpression Path(string canonical) => JsonPathExpressionCompiler.Compile(canonical);
}
