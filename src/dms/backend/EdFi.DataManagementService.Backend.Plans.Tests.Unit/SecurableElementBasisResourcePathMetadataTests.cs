// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Linq;
using EdFi.DataManagementService.Backend.External;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

/// <summary>
/// Covers the terminal-reference metadata the custom view-based basis path resolution carries alongside
/// its column path steps. The metadata names the securable element a custom view decided on, which is what
/// the ProblemDetails wording reports; the steps themselves must be identical to the step-only overloads.
/// </summary>
[TestFixture]
public class Given_SecurableElementColumnPathResolver_BasisPathMetadata
{
    private static readonly DbSchemaName EdFiSchema = new("edfi");
    private static readonly DbSchemaName DmsSchema = new("dms");

    private static DbTableName Table(string name) => new(EdFiSchema, name);

    private static DbTableName DescriptorTable => new(DmsSchema, "Descriptor");

    private static DbColumnName Col(string name) => new(name);

    private static JsonPathExpression Path(string canonical) => new(canonical, []);

    private static ResourceKeyEntry ResourceKey(
        short id,
        string project,
        string resource,
        bool isAbstract = false
    ) => new(id, new QualifiedResourceName(project, resource), "1.0", isAbstract);

    private static DbTableModel CreateRootTable(
        DbTableName table,
        IReadOnlyList<DbColumnModel>? columns = null
    ) =>
        new(
            table,
            Path("$"),
            new TableKey("PK_Test", [new DbKeyColumn(Col("DocumentId"), ColumnKind.Scalar)]),
            columns ?? [],
            []
        );

    private static DbColumnModel DocumentFkColumn(string name, string targetResource) =>
        new(
            Col(name),
            ColumnKind.DocumentFk,
            null,
            false,
            null,
            new QualifiedResourceName("Ed-Fi", targetResource)
        );

    private static RelationalResourceModel CreateModel(
        string project,
        string resource,
        DbTableModel root,
        IReadOnlyList<DocumentReferenceBinding>? bindings = null,
        IReadOnlyList<DescriptorEdgeSource>? descriptorEdges = null
    ) =>
        new(
            new QualifiedResourceName(project, resource),
            EdFiSchema,
            ResourceStorageKind.RelationalTables,
            root,
            [root],
            bindings ?? [],
            descriptorEdges ?? []
        );

    private static ConcreteResourceModel CreateConcrete(
        short keyId,
        string project,
        string resource,
        RelationalResourceModel model
    ) => new(ResourceKey(keyId, project, resource), ResourceStorageKind.RelationalTables, model);

    private static IReadOnlyDictionary<QualifiedResourceName, ConcreteResourceModel> CreateLookup(
        params ConcreteResourceModel[] resources
    ) => resources.ToDictionary(resource => resource.ResourceKey.Resource);

    private static DerivedRelationalModelSet CreateModelSet(
        IReadOnlyList<ConcreteResourceModel> resources,
        IReadOnlyList<AbstractUnionViewInfo>? abstractUnionViews = null
    ) =>
        new(
            new EffectiveSchemaInfo("1.0", "1.0", "test", 0, [], [], []),
            SqlDialect.Pgsql,
            [],
            resources,
            [],
            abstractUnionViews ?? [],
            [],
            []
        );

    private static ReferenceIdentityBinding IdentityBinding(
        string identityJsonPath,
        string referenceJsonPath,
        string column
    ) => new(Path(identityJsonPath), Path(referenceJsonPath), Col(column));

    [Test]
    public void It_should_report_no_terminal_reference_paths_when_the_basis_is_the_subject()
    {
        var subjectRoot = CreateRootTable(Table("Student"));
        var subject = CreateConcrete(1, "Ed-Fi", "Student", CreateModel("Ed-Fi", "Student", subjectRoot));

        var result = SecurableElementColumnPathResolver.ResolveBasisResourcePathWithMetadata(
            subject,
            new QualifiedResourceName("Ed-Fi", "Student"),
            CreateLookup(subject),
            []
        );

        result.Steps.Should().ContainSingle();
        result.Steps[0].SourceColumnName.Should().Be(Col("DocumentId"));
        result.TerminalReferenceJsonPaths.Should().BeEmpty();
    }

    [Test]
    public void It_should_report_the_terminal_reference_identity_path_for_a_direct_basis_reference()
    {
        var studentRoot = CreateRootTable(Table("Student"));
        var subjectRoot = CreateRootTable(
            Table("CourseTranscript"),
            [DocumentFkColumn("Student_DocumentId", "Student")]
        );

        var subjectBinding = new DocumentReferenceBinding(
            true,
            Path("$.studentReference"),
            subjectRoot.Table,
            Col("Student_DocumentId"),
            new QualifiedResourceName("Ed-Fi", "Student"),
            [
                IdentityBinding(
                    "$.studentUniqueId",
                    "$.studentReference.studentUniqueId",
                    "Student_StudentUniqueId"
                ),
            ],
            IsRequired: true
        );

        var subject = CreateConcrete(
            1,
            "Ed-Fi",
            "CourseTranscript",
            CreateModel("Ed-Fi", "CourseTranscript", subjectRoot, [subjectBinding])
        );
        var student = CreateConcrete(2, "Ed-Fi", "Student", CreateModel("Ed-Fi", "Student", studentRoot));

        var result = SecurableElementColumnPathResolver.ResolveBasisResourcePathWithMetadata(
            subject,
            new QualifiedResourceName("Ed-Fi", "Student"),
            CreateLookup(subject, student),
            []
        );

        result.Steps.Should().ContainSingle();
        result.Steps[0].SourceColumnName.Should().Be(Col("Student_DocumentId"));
        result.TerminalReferenceJsonPaths.Should().Equal("$.studentReference.studentUniqueId");
    }

    [Test]
    public void It_should_report_the_last_hops_reference_path_for_an_indirect_basis_reference()
    {
        // CourseTranscript -> StudentAcademicRecord -> Student. auth.md's POC reports 'StudentUniqueId'
        // for this shape, which is the LAST hop's reference path, not the first hop's.
        var studentRoot = CreateRootTable(Table("Student"));
        var academicRecordRoot = CreateRootTable(
            Table("StudentAcademicRecord"),
            [DocumentFkColumn("Student_DocumentId", "Student")]
        );
        var subjectRoot = CreateRootTable(
            Table("CourseTranscript"),
            [DocumentFkColumn("StudentAcademicRecord_DocumentId", "StudentAcademicRecord")]
        );

        var subjectBinding = new DocumentReferenceBinding(
            true,
            Path("$.studentAcademicRecordReference"),
            subjectRoot.Table,
            Col("StudentAcademicRecord_DocumentId"),
            new QualifiedResourceName("Ed-Fi", "StudentAcademicRecord"),
            [
                IdentityBinding(
                    "$.schoolYear",
                    "$.studentAcademicRecordReference.schoolYear",
                    "StudentAcademicRecord_SchoolYear"
                ),
            ],
            IsRequired: true
        );
        var academicRecordBinding = new DocumentReferenceBinding(
            true,
            Path("$.studentReference"),
            academicRecordRoot.Table,
            Col("Student_DocumentId"),
            new QualifiedResourceName("Ed-Fi", "Student"),
            [
                IdentityBinding(
                    "$.studentUniqueId",
                    "$.studentReference.studentUniqueId",
                    "Student_StudentUniqueId"
                ),
            ],
            IsRequired: true
        );

        var subject = CreateConcrete(
            1,
            "Ed-Fi",
            "CourseTranscript",
            CreateModel("Ed-Fi", "CourseTranscript", subjectRoot, [subjectBinding])
        );
        var academicRecord = CreateConcrete(
            2,
            "Ed-Fi",
            "StudentAcademicRecord",
            CreateModel("Ed-Fi", "StudentAcademicRecord", academicRecordRoot, [academicRecordBinding])
        );
        var student = CreateConcrete(3, "Ed-Fi", "Student", CreateModel("Ed-Fi", "Student", studentRoot));

        var result = SecurableElementColumnPathResolver.ResolveBasisResourcePathWithMetadata(
            subject,
            new QualifiedResourceName("Ed-Fi", "Student"),
            CreateLookup(subject, academicRecord, student),
            []
        );

        result.Steps.Should().HaveCount(2);
        result.Steps[^1].SourceColumnName.Should().Be(Col("Student_DocumentId"));
        result.TerminalReferenceJsonPaths.Should().Equal("$.studentReference.studentUniqueId");
        result.TerminalReferenceJsonPaths.Should().NotContain("$.studentAcademicRecordReference.schoolYear");
    }

    [Test]
    public void It_should_report_every_identity_path_of_a_composite_key_terminal_reference_in_binding_order()
    {
        var courseRoot = CreateRootTable(Table("Course"));
        var subjectRoot = CreateRootTable(
            Table("CourseTranscript"),
            [DocumentFkColumn("Course_DocumentId", "Course")]
        );

        var subjectBinding = new DocumentReferenceBinding(
            true,
            Path("$.courseReference"),
            subjectRoot.Table,
            Col("Course_DocumentId"),
            new QualifiedResourceName("Ed-Fi", "Course"),
            [
                IdentityBinding("$.courseCode", "$.courseReference.courseCode", "Course_CourseCode"),
                IdentityBinding(
                    "$.educationOrganizationId",
                    "$.courseReference.educationOrganizationId",
                    "Course_EducationOrganizationId"
                ),
            ],
            IsRequired: true
        );

        var subject = CreateConcrete(
            1,
            "Ed-Fi",
            "CourseTranscript",
            CreateModel("Ed-Fi", "CourseTranscript", subjectRoot, [subjectBinding])
        );
        var course = CreateConcrete(2, "Ed-Fi", "Course", CreateModel("Ed-Fi", "Course", courseRoot));

        var result = SecurableElementColumnPathResolver.ResolveBasisResourcePathWithMetadata(
            subject,
            new QualifiedResourceName("Ed-Fi", "Course"),
            CreateLookup(subject, course),
            []
        );

        result
            .TerminalReferenceJsonPaths.Should()
            .Equal("$.courseReference.courseCode", "$.courseReference.educationOrganizationId");
    }

    [Test]
    public void It_should_report_the_descriptor_value_path_for_a_directly_referenced_descriptor_basis()
    {
        var subjectRoot = CreateRootTable(
            Table("StudentTransportation"),
            [
                new DbColumnModel(
                    Col("TransportationTypeDescriptor_DescriptorId"),
                    ColumnKind.DescriptorFk,
                    null,
                    false,
                    null,
                    null
                ),
            ]
        );

        var descriptorEdge = new DescriptorEdgeSource(
            true,
            Path("$.transportationTypeDescriptor"),
            subjectRoot.Table,
            Col("TransportationTypeDescriptor_DescriptorId"),
            new QualifiedResourceName("Ed-Fi", "TransportationTypeDescriptor")
        );

        var descriptorRoot = CreateRootTable(Table("TransportationTypeDescriptor"));
        var subject = CreateConcrete(
            1,
            "Ed-Fi",
            "StudentTransportation",
            CreateModel("Ed-Fi", "StudentTransportation", subjectRoot, [], [descriptorEdge])
        );
        var descriptor = CreateConcrete(
            2,
            "Ed-Fi",
            "TransportationTypeDescriptor",
            CreateModel("Ed-Fi", "TransportationTypeDescriptor", descriptorRoot)
        );

        var result = SecurableElementColumnPathResolver.ResolveBasisResourcePathWithMetadata(
            subject,
            new QualifiedResourceName("Ed-Fi", "TransportationTypeDescriptor"),
            CreateLookup(subject, descriptor),
            []
        );

        result.Steps.Should().ContainSingle();
        result.Steps[0].TargetTable.Should().Be(DescriptorTable);
        result.TerminalReferenceJsonPaths.Should().Equal("$.transportationTypeDescriptor");
    }

    [Test]
    public void It_should_report_an_unresolved_path_when_the_subject_resource_is_not_in_the_model_set()
    {
        var student = CreateConcrete(
            1,
            "Ed-Fi",
            "Student",
            CreateModel("Ed-Fi", "Student", CreateRootTable(Table("Student")))
        );

        var result = SecurableElementColumnPathResolver.ResolveBasisResourcePathWithMetadata(
            new QualifiedResourceName("Ed-Fi", "CourseTranscript"),
            new QualifiedResourceName("Ed-Fi", "Student"),
            CreateModelSet([student])
        );

        result.Steps.Should().BeEmpty();
        result.TerminalReferenceJsonPaths.Should().BeEmpty();
    }

    [Test]
    public void It_should_report_an_unresolved_path_when_no_join_path_reaches_the_basis()
    {
        var subject = CreateConcrete(
            1,
            "Ed-Fi",
            "BellSchedule",
            CreateModel("Ed-Fi", "BellSchedule", CreateRootTable(Table("BellSchedule")))
        );
        var student = CreateConcrete(
            2,
            "Ed-Fi",
            "Student",
            CreateModel("Ed-Fi", "Student", CreateRootTable(Table("Student")))
        );

        var result = SecurableElementColumnPathResolver.ResolveBasisResourcePathWithMetadata(
            new QualifiedResourceName("Ed-Fi", "BellSchedule"),
            new QualifiedResourceName("Ed-Fi", "Student"),
            CreateModelSet([subject, student])
        );

        result.Steps.Should().BeEmpty();
        result.TerminalReferenceJsonPaths.Should().BeEmpty();
    }

    [Test]
    public void It_should_return_the_same_steps_as_the_step_only_overloads()
    {
        var studentRoot = CreateRootTable(Table("Student"));
        var subjectRoot = CreateRootTable(
            Table("CourseTranscript"),
            [DocumentFkColumn("Student_DocumentId", "Student")]
        );

        var subjectBinding = new DocumentReferenceBinding(
            true,
            Path("$.studentReference"),
            subjectRoot.Table,
            Col("Student_DocumentId"),
            new QualifiedResourceName("Ed-Fi", "Student"),
            [
                IdentityBinding(
                    "$.studentUniqueId",
                    "$.studentReference.studentUniqueId",
                    "Student_StudentUniqueId"
                ),
            ],
            IsRequired: true
        );

        var subject = CreateConcrete(
            1,
            "Ed-Fi",
            "CourseTranscript",
            CreateModel("Ed-Fi", "CourseTranscript", subjectRoot, [subjectBinding])
        );
        var student = CreateConcrete(2, "Ed-Fi", "Student", CreateModel("Ed-Fi", "Student", studentRoot));
        var lookup = CreateLookup(subject, student);
        var modelSet = CreateModelSet([subject, student]);
        var basis = new QualifiedResourceName("Ed-Fi", "Student");
        var subjectName = new QualifiedResourceName("Ed-Fi", "CourseTranscript");

        var metadata = SecurableElementColumnPathResolver.ResolveBasisResourcePathWithMetadata(
            subject,
            basis,
            lookup,
            []
        );

        SecurableElementColumnPathResolver
            .ResolveBasisResourcePath(subject, basis, lookup, [])
            .Should()
            .Equal(metadata.Steps);
        SecurableElementColumnPathResolver
            .ResolveBasisResourcePath(subjectName, basis, modelSet)
            .Should()
            .Equal(metadata.Steps);
        SecurableElementColumnPathResolver
            .ResolveSecurableElementColumnPath(subjectName, basis, modelSet)
            .Should()
            .Equal(metadata.Steps);
    }

    [Test]
    public void It_should_keep_the_abstract_basis_ranking_and_report_the_winning_paths_reference()
    {
        // auth.md requires StudentSchoolAssociation -> GraduationPlan -> EducationOrganization to win
        // over the direct School union-arm route. The metadata must describe that winner, proving the
        // added metadata does not perturb candidate ranking.
        var schoolRoot = CreateRootTable(Table("School"));
        var graduationPlanRoot = CreateRootTable(
            Table("GraduationPlan"),
            [DocumentFkColumn("EducationOrganization_DocumentId", "EducationOrganization")]
        );
        var subjectRoot = CreateRootTable(
            Table("StudentSchoolAssociation"),
            [
                DocumentFkColumn("School_DocumentId", "School"),
                DocumentFkColumn("GraduationPlan_DocumentId", "GraduationPlan"),
            ]
        );

        var schoolBinding = new DocumentReferenceBinding(
            true,
            Path("$.schoolReference"),
            subjectRoot.Table,
            Col("School_DocumentId"),
            new QualifiedResourceName("Ed-Fi", "School"),
            [IdentityBinding("$.schoolId", "$.schoolReference.schoolId", "School_SchoolId")],
            IsRequired: true
        );
        var graduationPlanBinding = new DocumentReferenceBinding(
            true,
            Path("$.graduationPlanReference"),
            subjectRoot.Table,
            Col("GraduationPlan_DocumentId"),
            new QualifiedResourceName("Ed-Fi", "GraduationPlan"),
            [
                IdentityBinding(
                    "$.educationOrganizationId",
                    "$.graduationPlanReference.educationOrganizationId",
                    "GraduationPlan_EducationOrganizationId"
                ),
            ],
            IsRequired: true
        );
        var edOrgBinding = new DocumentReferenceBinding(
            true,
            Path("$.educationOrganizationReference"),
            graduationPlanRoot.Table,
            Col("EducationOrganization_DocumentId"),
            new QualifiedResourceName("Ed-Fi", "EducationOrganization"),
            [
                IdentityBinding(
                    "$.educationOrganizationId",
                    "$.educationOrganizationReference.educationOrganizationId",
                    "EducationOrganization_EducationOrganizationId"
                ),
            ],
            IsRequired: true
        );

        var subject = CreateConcrete(
            1,
            "Ed-Fi",
            "StudentSchoolAssociation",
            CreateModel(
                "Ed-Fi",
                "StudentSchoolAssociation",
                subjectRoot,
                [schoolBinding, graduationPlanBinding]
            )
        );
        var graduationPlan = CreateConcrete(
            2,
            "Ed-Fi",
            "GraduationPlan",
            CreateModel("Ed-Fi", "GraduationPlan", graduationPlanRoot, [edOrgBinding])
        );
        var school = CreateConcrete(3, "Ed-Fi", "School", CreateModel("Ed-Fi", "School", schoolRoot));

        var abstractView = new AbstractUnionViewInfo(
            ResourceKey(100, "Ed-Fi", "EducationOrganization", true),
            Table("EducationOrganization"),
            [],
            [new AbstractUnionViewArm(ResourceKey(3, "Ed-Fi", "School"), Table("School"), [])]
        );

        var result = SecurableElementColumnPathResolver.ResolveBasisResourcePathWithMetadata(
            subject,
            new QualifiedResourceName("Ed-Fi", "EducationOrganization"),
            CreateLookup(subject, graduationPlan, school),
            [abstractView]
        );

        result.Steps.Should().HaveCount(2);
        result.Steps[0].SourceColumnName.Should().Be(Col("GraduationPlan_DocumentId"));
        result.Steps[^1].SourceColumnName.Should().Be(Col("EducationOrganization_DocumentId"));
        result
            .TerminalReferenceJsonPaths.Should()
            .Equal("$.educationOrganizationReference.educationOrganizationId");
    }
}
