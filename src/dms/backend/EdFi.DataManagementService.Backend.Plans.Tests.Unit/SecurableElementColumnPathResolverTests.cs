// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
public class Given_SecurableElementColumnPathResolver
{
    private static readonly DbSchemaName _edfiSchema = new("edfi");
    private static readonly DbColumnName _documentId = new("DocumentId");

    private static DbTableName Table(string name) => new(_edfiSchema, name);

    private static DbColumnName Col(string name) => new(name);

    private static JsonPathExpression Path(string canonical) => new(canonical, []);

    private static ResourceKeyEntry ResourceKey(short id, string project, string resource) =>
        new(id, new QualifiedResourceName(project, resource), "1.0", false);

    private static DbTableModel CreateRootTable(
        DbTableName table,
        IReadOnlyList<DbColumnModel>? columns = null
    ) =>
        new(
            table,
            Path("$"),
            new TableKey("PK_Test", [new DbKeyColumn(_documentId, ColumnKind.Scalar)]),
            columns ?? [],
            []
        );

    private static RelationalResourceModel CreateModel(
        string project,
        string resource,
        DbTableModel root,
        IReadOnlyList<DocumentReferenceBinding>? bindings = null
    ) =>
        new(
            new QualifiedResourceName(project, resource),
            _edfiSchema,
            ResourceStorageKind.RelationalTables,
            root,
            [root],
            bindings ?? [],
            []
        );

    private static ConcreteResourceModel CreateConcrete(
        short keyId,
        string project,
        string resource,
        RelationalResourceModel model,
        ResourceSecurableElements? securableElements = null
    ) =>
        new(ResourceKey(keyId, project, resource), ResourceStorageKind.RelationalTables, model)
        {
            SecurableElements = securableElements ?? ResourceSecurableElements.Empty,
        };

    [TestFixture]
    public class Given_EdOrg_securable_element
    {
        [Test]
        public void It_should_resolve_to_single_step_with_null_target()
        {
            // StudentSchoolAssociation has EdOrg: $.schoolReference.schoolId
            var rootTable = CreateRootTable(
                Table("StudentSchoolAssociation"),
                [
                    new DbColumnModel(
                        Col("SchoolReference_SchoolId"),
                        ColumnKind.Scalar,
                        null,
                        false,
                        Path("$.schoolReference.schoolId"),
                        null
                    ),
                    new DbColumnModel(
                        Col("SchoolReference_DocumentId"),
                        ColumnKind.DocumentFk,
                        null,
                        false,
                        null,
                        new QualifiedResourceName("Ed-Fi", "School")
                    ),
                ]
            );

            var binding = new DocumentReferenceBinding(
                true,
                Path("$.schoolReference"),
                rootTable.Table,
                Col("SchoolReference_DocumentId"),
                new QualifiedResourceName("Ed-Fi", "School"),
                [
                    new ReferenceIdentityBinding(
                        Path("$.schoolReference.schoolId"),
                        Col("SchoolReference_SchoolId")
                    ),
                ]
            );

            var model = CreateModel("Ed-Fi", "StudentSchoolAssociation", rootTable, [binding]);

            var securableElements = new ResourceSecurableElements(
                [new EdOrgSecurableElement("$.schoolReference.schoolId", "SchoolId")],
                [],
                [],
                [],
                []
            );

            var concrete = CreateConcrete(1, "Ed-Fi", "StudentSchoolAssociation", model, securableElements);
            var results = SecurableElementColumnPathResolver.ResolveAll(concrete, [concrete]);

            results.Should().HaveCount(1);
            results[0].Kind.Should().Be(SecurableElementKind.EducationOrganization);
            results[0].Steps.Should().HaveCount(1);
            results[0].Steps[0].SourceTable.Should().Be(Table("StudentSchoolAssociation"));
            results[0].Steps[0].SourceColumnName.Should().Be(Col("SchoolReference_SchoolId"));
            results[0].Steps[0].TargetTable.Should().BeNull();
            results[0].Steps[0].TargetColumnName.Should().BeNull();
        }

        [Test]
        public void It_should_resolve_canonical_column_when_unified()
        {
            // EdOrg identity column is an alias over a canonical column
            var rootTable = CreateRootTable(
                Table("StudentSchoolAssociation"),
                [
                    new DbColumnModel(
                        Col("SchoolReference_SchoolId"),
                        ColumnKind.Scalar,
                        null,
                        false,
                        Path("$.schoolReference.schoolId"),
                        null,
                        new ColumnStorage.UnifiedAlias(Col("School_EducationOrganizationId"), null)
                    ),
                    new DbColumnModel(
                        Col("School_EducationOrganizationId"),
                        ColumnKind.Scalar,
                        null,
                        false,
                        null,
                        null
                    ),
                ]
            );

            var binding = new DocumentReferenceBinding(
                true,
                Path("$.schoolReference"),
                rootTable.Table,
                Col("SchoolReference_DocumentId"),
                new QualifiedResourceName("Ed-Fi", "School"),
                [
                    new ReferenceIdentityBinding(
                        Path("$.schoolReference.schoolId"),
                        Col("SchoolReference_SchoolId")
                    ),
                ]
            );

            var model = CreateModel("Ed-Fi", "StudentSchoolAssociation", rootTable, [binding]);

            var securableElements = new ResourceSecurableElements(
                [new EdOrgSecurableElement("$.schoolReference.schoolId", "SchoolId")],
                [],
                [],
                [],
                []
            );

            var concrete = CreateConcrete(1, "Ed-Fi", "StudentSchoolAssociation", model, securableElements);
            var results = SecurableElementColumnPathResolver.ResolveAll(concrete, [concrete]);

            results.Should().HaveCount(1);
            results[0].Kind.Should().Be(SecurableElementKind.EducationOrganization);
            // Should use canonical column, not the alias
            results[0].Steps[0].SourceColumnName.Should().Be(Col("School_EducationOrganizationId"));
        }
    }

    [TestFixture]
    public class Given_Namespace_securable_element
    {
        [Test]
        public void It_should_resolve_namespace_from_reference_identity_binding()
        {
            // StudentAssessmentRegistration has Namespace: $.assessmentAdministrationReference.namespace
            var rootTable = CreateRootTable(
                Table("StudentAssessmentRegistration"),
                [
                    new DbColumnModel(
                        Col("AssessmentAdministration_Namespace"),
                        ColumnKind.Scalar,
                        null,
                        false,
                        Path("$.assessmentAdministrationReference.namespace"),
                        null
                    ),
                ]
            );

            var binding = new DocumentReferenceBinding(
                true,
                Path("$.assessmentAdministrationReference"),
                rootTable.Table,
                Col("AssessmentAdministration_DocumentId"),
                new QualifiedResourceName("Ed-Fi", "AssessmentAdministration"),
                [
                    new ReferenceIdentityBinding(
                        Path("$.assessmentAdministrationReference.namespace"),
                        Col("AssessmentAdministration_Namespace")
                    ),
                ]
            );

            var model = CreateModel("Ed-Fi", "StudentAssessmentRegistration", rootTable, [binding]);

            var securableElements = new ResourceSecurableElements(
                [],
                ["$.assessmentAdministrationReference.namespace"],
                [],
                [],
                []
            );

            var concrete = CreateConcrete(
                1,
                "Ed-Fi",
                "StudentAssessmentRegistration",
                model,
                securableElements
            );
            var results = SecurableElementColumnPathResolver.ResolveAll(concrete, [concrete]);

            results.Should().HaveCount(1);
            results[0].Kind.Should().Be(SecurableElementKind.Namespace);
            results[0].Steps.Should().HaveCount(1);
            results[0].Steps[0].SourceTable.Should().Be(Table("StudentAssessmentRegistration"));
            results[0].Steps[0].SourceColumnName.Should().Be(Col("AssessmentAdministration_Namespace"));
            results[0].Steps[0].TargetTable.Should().BeNull();
            results[0].Steps[0].TargetColumnName.Should().BeNull();
        }

        [Test]
        public void It_should_resolve_namespace_from_direct_scalar_column()
        {
            // A resource whose namespace column is a direct scalar (not part of a reference
            // identity binding). This exercises the fallback path in ResolveEdOrgOrNamespacePath
            // that matches against SourceJsonPath on root-table columns.
            var rootTable = CreateRootTable(
                Table("AcademicWeek"),
                [
                    new DbColumnModel(
                        Col("Namespace"),
                        ColumnKind.Scalar,
                        null,
                        false,
                        Path("$.namespace"),
                        null
                    ),
                ]
            );

            // No DocumentReferenceBinding for the namespace path
            var model = CreateModel("Ed-Fi", "AcademicWeek", rootTable);

            var securableElements = new ResourceSecurableElements([], ["$.namespace"], [], [], []);

            var concrete = CreateConcrete(1, "Ed-Fi", "AcademicWeek", model, securableElements);
            var results = SecurableElementColumnPathResolver.ResolveAll(concrete, [concrete]);

            results.Should().HaveCount(1);
            results[0].Kind.Should().Be(SecurableElementKind.Namespace);
            results[0].Steps.Should().HaveCount(1);
            results[0].Steps[0].SourceTable.Should().Be(Table("AcademicWeek"));
            results[0].Steps[0].SourceColumnName.Should().Be(Col("Namespace"));
            results[0].Steps[0].TargetTable.Should().BeNull();
            results[0].Steps[0].TargetColumnName.Should().BeNull();
        }
    }

    [TestFixture]
    public class Given_direct_person_reference
    {
        [Test]
        public void It_should_resolve_student_direct_reference()
        {
            // StudentSchoolAssociation -> Student (direct)
            var ssaRoot = CreateRootTable(
                Table("StudentSchoolAssociation"),
                [
                    new DbColumnModel(
                        Col("Student_DocumentId"),
                        ColumnKind.DocumentFk,
                        null,
                        false,
                        null,
                        new QualifiedResourceName("Ed-Fi", "Student")
                    ),
                ]
            );

            var studentRoot = CreateRootTable(Table("Student"));

            var ssaBinding = new DocumentReferenceBinding(
                true,
                Path("$.studentReference"),
                ssaRoot.Table,
                Col("Student_DocumentId"),
                new QualifiedResourceName("Ed-Fi", "Student"),
                [
                    new ReferenceIdentityBinding(
                        Path("$.studentReference.studentUniqueId"),
                        Col("Student_StudentUniqueId")
                    ),
                ]
            );

            var ssaModel = CreateModel("Ed-Fi", "StudentSchoolAssociation", ssaRoot, [ssaBinding]);
            var studentModel = CreateModel("Ed-Fi", "Student", studentRoot);

            var securableElements = new ResourceSecurableElements(
                [],
                [],
                ["$.studentReference.studentUniqueId"],
                [],
                []
            );

            var ssaConcrete = CreateConcrete(
                1,
                "Ed-Fi",
                "StudentSchoolAssociation",
                ssaModel,
                securableElements
            );
            var studentConcrete = CreateConcrete(2, "Ed-Fi", "Student", studentModel);

            var results = SecurableElementColumnPathResolver.ResolveAll(
                ssaConcrete,
                [ssaConcrete, studentConcrete]
            );

            results.Should().HaveCount(1);
            results[0].Kind.Should().Be(SecurableElementKind.Student);
            results[0].Steps.Should().HaveCount(1);
            results[0].Steps[0].SourceTable.Should().Be(Table("StudentSchoolAssociation"));
            results[0].Steps[0].SourceColumnName.Should().Be(Col("Student_DocumentId"));
            results[0].Steps[0].TargetTable.Should().Be(Table("Student"));
            results[0].Steps[0].TargetColumnName.Should().Be(Col("DocumentId"));
        }

        [Test]
        public void It_should_resolve_contact_direct_reference()
        {
            var scaRoot = CreateRootTable(
                Table("StudentContactAssociation"),
                [
                    new DbColumnModel(
                        Col("Contact_DocumentId"),
                        ColumnKind.DocumentFk,
                        null,
                        false,
                        null,
                        new QualifiedResourceName("Ed-Fi", "Contact")
                    ),
                ]
            );

            var contactRoot = CreateRootTable(Table("Contact"));

            var scaBinding = new DocumentReferenceBinding(
                true,
                Path("$.contactReference"),
                scaRoot.Table,
                Col("Contact_DocumentId"),
                new QualifiedResourceName("Ed-Fi", "Contact"),
                [
                    new ReferenceIdentityBinding(
                        Path("$.contactReference.contactUniqueId"),
                        Col("Contact_ContactUniqueId")
                    ),
                ]
            );

            var scaModel = CreateModel("Ed-Fi", "StudentContactAssociation", scaRoot, [scaBinding]);
            var contactModel = CreateModel("Ed-Fi", "Contact", contactRoot);

            var securableElements = new ResourceSecurableElements(
                [],
                [],
                [],
                ["$.contactReference.contactUniqueId"],
                []
            );

            var scaConcrete = CreateConcrete(
                1,
                "Ed-Fi",
                "StudentContactAssociation",
                scaModel,
                securableElements
            );
            var contactConcrete = CreateConcrete(2, "Ed-Fi", "Contact", contactModel);

            var results = SecurableElementColumnPathResolver.ResolveAll(
                scaConcrete,
                [scaConcrete, contactConcrete]
            );

            results.Should().HaveCount(1);
            results[0].Kind.Should().Be(SecurableElementKind.Contact);
            results[0].Steps[0].SourceColumnName.Should().Be(Col("Contact_DocumentId"));
            results[0].Steps[0].TargetTable.Should().Be(Table("Contact"));
        }
    }

    [TestFixture]
    public class Given_transitive_person_reference
    {
        [Test]
        public void It_should_resolve_two_hop_chain()
        {
            // CourseTranscript -> StudentAcademicRecord -> Student

            var ctRoot = CreateRootTable(
                Table("CourseTranscript"),
                [
                    new DbColumnModel(
                        Col("StudentAcademicRecord_DocumentId"),
                        ColumnKind.DocumentFk,
                        null,
                        false,
                        null,
                        new QualifiedResourceName("Ed-Fi", "StudentAcademicRecord")
                    ),
                ]
            );

            var sarRoot = CreateRootTable(
                Table("StudentAcademicRecord"),
                [
                    new DbColumnModel(
                        Col("Student_DocumentId"),
                        ColumnKind.DocumentFk,
                        null,
                        false,
                        null,
                        new QualifiedResourceName("Ed-Fi", "Student")
                    ),
                ]
            );

            var studentRoot = CreateRootTable(Table("Student"));

            var ctBinding = new DocumentReferenceBinding(
                true,
                Path("$.studentAcademicRecordReference"),
                ctRoot.Table,
                Col("StudentAcademicRecord_DocumentId"),
                new QualifiedResourceName("Ed-Fi", "StudentAcademicRecord"),
                [
                    new ReferenceIdentityBinding(
                        Path("$.studentAcademicRecordReference.studentUniqueId"),
                        Col("StudentAcademicRecord_StudentUniqueId")
                    ),
                ]
            );

            var sarBinding = new DocumentReferenceBinding(
                true,
                Path("$.studentReference"),
                sarRoot.Table,
                Col("Student_DocumentId"),
                new QualifiedResourceName("Ed-Fi", "Student"),
                [
                    new ReferenceIdentityBinding(
                        Path("$.studentReference.studentUniqueId"),
                        Col("Student_StudentUniqueId")
                    ),
                ]
            );

            var ctModel = CreateModel("Ed-Fi", "CourseTranscript", ctRoot, [ctBinding]);
            var sarModel = CreateModel("Ed-Fi", "StudentAcademicRecord", sarRoot, [sarBinding]);
            var studentModel = CreateModel("Ed-Fi", "Student", studentRoot);

            var securableElements = new ResourceSecurableElements(
                [],
                [],
                ["$.studentAcademicRecordReference.studentUniqueId"],
                [],
                []
            );

            var ctConcrete = CreateConcrete(1, "Ed-Fi", "CourseTranscript", ctModel, securableElements);
            var sarConcrete = CreateConcrete(2, "Ed-Fi", "StudentAcademicRecord", sarModel);
            var studentConcrete = CreateConcrete(3, "Ed-Fi", "Student", studentModel);

            var results = SecurableElementColumnPathResolver.ResolveAll(
                ctConcrete,
                [ctConcrete, sarConcrete, studentConcrete]
            );

            results.Should().HaveCount(1);
            results[0].Kind.Should().Be(SecurableElementKind.Student);
            results[0].Steps.Should().HaveCount(2);

            // First hop: CourseTranscript -> StudentAcademicRecord
            results[0].Steps[0].SourceTable.Should().Be(Table("CourseTranscript"));
            results[0].Steps[0].SourceColumnName.Should().Be(Col("StudentAcademicRecord_DocumentId"));
            results[0].Steps[0].TargetTable.Should().Be(Table("StudentAcademicRecord"));
            results[0].Steps[0].TargetColumnName.Should().Be(Col("DocumentId"));

            // Second hop: StudentAcademicRecord -> Student
            results[0].Steps[1].SourceTable.Should().Be(Table("StudentAcademicRecord"));
            results[0].Steps[1].SourceColumnName.Should().Be(Col("Student_DocumentId"));
            results[0].Steps[1].TargetTable.Should().Be(Table("Student"));
            results[0].Steps[1].TargetColumnName.Should().Be(Col("DocumentId"));
        }

        [Test]
        public void It_should_pick_shortest_path_when_multiple_paths_exist()
        {
            // Resource has two paths to Student: one direct, one through intermediate
            var rootTable = CreateRootTable(
                Table("TestResource"),
                [
                    new DbColumnModel(
                        Col("Student_DocumentId"),
                        ColumnKind.DocumentFk,
                        null,
                        false,
                        null,
                        new QualifiedResourceName("Ed-Fi", "Student")
                    ),
                    new DbColumnModel(
                        Col("Intermediate_DocumentId"),
                        ColumnKind.DocumentFk,
                        null,
                        false,
                        null,
                        new QualifiedResourceName("Ed-Fi", "Intermediate")
                    ),
                ]
            );

            var intermediateRoot = CreateRootTable(
                Table("Intermediate"),
                [
                    new DbColumnModel(
                        Col("Student_DocumentId"),
                        ColumnKind.DocumentFk,
                        null,
                        false,
                        null,
                        new QualifiedResourceName("Ed-Fi", "Student")
                    ),
                ]
            );

            var studentRoot = CreateRootTable(Table("Student"));

            // Direct path
            var directBinding = new DocumentReferenceBinding(
                true,
                Path("$.studentReference"),
                rootTable.Table,
                Col("Student_DocumentId"),
                new QualifiedResourceName("Ed-Fi", "Student"),
                [
                    new ReferenceIdentityBinding(
                        Path("$.studentReference.studentUniqueId"),
                        Col("Student_StudentUniqueId")
                    ),
                ]
            );

            // Indirect path
            var indirectBinding = new DocumentReferenceBinding(
                true,
                Path("$.intermediateReference"),
                rootTable.Table,
                Col("Intermediate_DocumentId"),
                new QualifiedResourceName("Ed-Fi", "Intermediate"),
                [
                    new ReferenceIdentityBinding(
                        Path("$.intermediateReference.studentUniqueId"),
                        Col("Intermediate_StudentUniqueId")
                    ),
                ]
            );

            var intermediateToStudent = new DocumentReferenceBinding(
                true,
                Path("$.studentReference"),
                intermediateRoot.Table,
                Col("Student_DocumentId"),
                new QualifiedResourceName("Ed-Fi", "Student"),
                [
                    new ReferenceIdentityBinding(
                        Path("$.studentReference.studentUniqueId"),
                        Col("Student_StudentUniqueId")
                    ),
                ]
            );

            var testModel = CreateModel("Ed-Fi", "TestResource", rootTable, [directBinding, indirectBinding]);
            var intermediateModel = CreateModel(
                "Ed-Fi",
                "Intermediate",
                intermediateRoot,
                [intermediateToStudent]
            );
            var studentModel = CreateModel("Ed-Fi", "Student", studentRoot);

            // Two paths: direct and indirect
            var securableElements = new ResourceSecurableElements(
                [],
                [],
                ["$.studentReference.studentUniqueId", "$.intermediateReference.studentUniqueId"],
                [],
                []
            );

            var testConcrete = CreateConcrete(1, "Ed-Fi", "TestResource", testModel, securableElements);
            var intermediateConcrete = CreateConcrete(2, "Ed-Fi", "Intermediate", intermediateModel);
            var studentConcrete = CreateConcrete(3, "Ed-Fi", "Student", studentModel);

            var results = SecurableElementColumnPathResolver.ResolveAll(
                testConcrete,
                [testConcrete, intermediateConcrete, studentConcrete]
            );

            // Should pick the shortest path (direct, 1 hop) over the indirect (2 hops)
            results.Should().HaveCount(1);
            results[0].Kind.Should().Be(SecurableElementKind.Student);
            results[0].Steps.Should().HaveCount(1);
            results[0].Steps[0].SourceColumnName.Should().Be(Col("Student_DocumentId"));
        }
    }

    [TestFixture]
    public class Given_no_securable_elements
    {
        [Test]
        public void It_should_return_empty_when_no_securable_elements()
        {
            var rootTable = CreateRootTable(Table("SomeResource"));
            var model = CreateModel("Ed-Fi", "SomeResource", rootTable);
            var concrete = CreateConcrete(1, "Ed-Fi", "SomeResource", model);

            var results = SecurableElementColumnPathResolver.ResolveAll(concrete, [concrete]);

            results.Should().BeEmpty();
        }
    }

    [TestFixture]
    public class Given_staff_direct_person_reference
    {
        [Test]
        public void It_should_resolve_staff_direct_reference()
        {
            var root = CreateRootTable(
                Table("StaffSchoolAssociation"),
                [
                    new DbColumnModel(
                        Col("Staff_DocumentId"),
                        ColumnKind.DocumentFk,
                        null,
                        false,
                        null,
                        new QualifiedResourceName("Ed-Fi", "Staff")
                    ),
                ]
            );

            var staffRoot = CreateRootTable(Table("Staff"));

            var binding = new DocumentReferenceBinding(
                true,
                Path("$.staffReference"),
                root.Table,
                Col("Staff_DocumentId"),
                new QualifiedResourceName("Ed-Fi", "Staff"),
                [
                    new ReferenceIdentityBinding(
                        Path("$.staffReference.staffUniqueId"),
                        Col("Staff_StaffUniqueId")
                    ),
                ]
            );

            var model = CreateModel("Ed-Fi", "StaffSchoolAssociation", root, [binding]);
            var staffModel = CreateModel("Ed-Fi", "Staff", staffRoot);

            var securableElements = new ResourceSecurableElements(
                [],
                [],
                [],
                [],
                ["$.staffReference.staffUniqueId"]
            );

            var concrete = CreateConcrete(1, "Ed-Fi", "StaffSchoolAssociation", model, securableElements);
            var staffConcrete = CreateConcrete(2, "Ed-Fi", "Staff", staffModel);

            var results = SecurableElementColumnPathResolver.ResolveAll(concrete, [concrete, staffConcrete]);

            results.Should().HaveCount(1);
            results[0].Kind.Should().Be(SecurableElementKind.Staff);
            results[0].Steps.Should().HaveCount(1);
            results[0].Steps[0].SourceTable.Should().Be(Table("StaffSchoolAssociation"));
            results[0].Steps[0].SourceColumnName.Should().Be(Col("Staff_DocumentId"));
            results[0].Steps[0].TargetTable.Should().Be(Table("Staff"));
            results[0].Steps[0].TargetColumnName.Should().Be(Col("DocumentId"));
        }
    }

    [TestFixture]
    public class Given_homograph_person_resource
    {
        [Test]
        public void It_should_not_match_homograph_student_as_person_resource()
        {
            // A homograph extension defines "homograph.Student" — it should NOT be
            // matched as the person resource terminal (only Ed-Fi.Student qualifies).
            var rootTable = CreateRootTable(
                Table("TestResource"),
                [
                    new DbColumnModel(
                        Col("Student_DocumentId"),
                        ColumnKind.DocumentFk,
                        null,
                        false,
                        null,
                        new QualifiedResourceName("homograph", "Student")
                    ),
                ]
            );

            var homographStudentRoot = CreateRootTable(Table("HomographStudent"));
            var edfiStudentRoot = CreateRootTable(Table("Student"));

            // Binding points to homograph.Student, not Ed-Fi.Student
            var binding = new DocumentReferenceBinding(
                true,
                Path("$.studentReference"),
                rootTable.Table,
                Col("Student_DocumentId"),
                new QualifiedResourceName("homograph", "Student"),
                [
                    new ReferenceIdentityBinding(
                        Path("$.studentReference.studentUniqueId"),
                        Col("Student_StudentUniqueId")
                    ),
                ]
            );

            var testModel = CreateModel("Ed-Fi", "TestResource", rootTable, [binding]);
            var homographStudentModel = CreateModel("homograph", "Student", homographStudentRoot);
            var edfiStudentModel = CreateModel("Ed-Fi", "Student", edfiStudentRoot);

            var securableElements = new ResourceSecurableElements(
                [],
                [],
                ["$.studentReference.studentUniqueId"],
                [],
                []
            );

            var testConcrete = CreateConcrete(1, "Ed-Fi", "TestResource", testModel, securableElements);
            var homographConcrete = CreateConcrete(2, "homograph", "Student", homographStudentModel);
            var edfiStudentConcrete = CreateConcrete(3, "Ed-Fi", "Student", edfiStudentModel);

            // Should throw because the homograph.Student is not matched as a person resource,
            // leaving the Student securable element path unresolved.
            var act = () =>
                SecurableElementColumnPathResolver.ResolveAll(
                    testConcrete,
                    [testConcrete, homographConcrete, edfiStudentConcrete]
                );

            act.Should()
                .Throw<InvalidOperationException>()
                .WithMessage("*unresolved*$.studentReference.studentUniqueId*");
        }
    }

    [TestFixture]
    public class Given_array_nested_securable_element_paths
    {
        [Test]
        public void It_should_throw_when_all_paths_are_array_nested()
        {
            // Resource has only array-nested securable elements — no root-level paths
            // to resolve. Should throw indicating unsupported child-table traversal.
            var rootTable = CreateRootTable(Table("TestResource"));
            var model = CreateModel("Ed-Fi", "TestResource", rootTable);

            var securableElements = new ResourceSecurableElements(
                [new EdOrgSecurableElement("$.classPeriods[*].classPeriodReference.schoolId", "SchoolId")],
                [],
                [],
                [],
                []
            );

            var concrete = CreateConcrete(1, "Ed-Fi", "TestResource", model, securableElements);

            var act = () => SecurableElementColumnPathResolver.ResolveAll(concrete, [concrete]);

            act.Should()
                .Throw<InvalidOperationException>()
                .WithMessage("*Ed-Fi.TestResource*")
                .WithMessage("*unsupported child-table traversal*")
                .WithMessage("*$.classPeriods[*].classPeriodReference.schoolId*");
        }

        [Test]
        public void It_should_resolve_root_level_and_skip_array_nested()
        {
            // Resource has both root-level and array-nested EdOrg paths.
            // Only the root-level path should resolve; the array-nested one is skipped.
            var rootTable = CreateRootTable(
                Table("TestResource"),
                [
                    new DbColumnModel(
                        Col("SchoolReference_SchoolId"),
                        ColumnKind.Scalar,
                        null,
                        false,
                        Path("$.schoolReference.schoolId"),
                        null
                    ),
                ]
            );

            var binding = new DocumentReferenceBinding(
                true,
                Path("$.schoolReference"),
                rootTable.Table,
                Col("SchoolReference_DocumentId"),
                new QualifiedResourceName("Ed-Fi", "School"),
                [
                    new ReferenceIdentityBinding(
                        Path("$.schoolReference.schoolId"),
                        Col("SchoolReference_SchoolId")
                    ),
                ]
            );

            var model = CreateModel("Ed-Fi", "TestResource", rootTable, [binding]);

            var securableElements = new ResourceSecurableElements(
                [
                    new EdOrgSecurableElement("$.schoolReference.schoolId", "SchoolId"),
                    new EdOrgSecurableElement("$.classPeriods[*].classPeriodReference.schoolId", "SchoolId"),
                ],
                [],
                [],
                [],
                []
            );

            var concrete = CreateConcrete(1, "Ed-Fi", "TestResource", model, securableElements);
            var results = SecurableElementColumnPathResolver.ResolveAll(concrete, [concrete]);

            results.Should().HaveCount(1);
            results[0].Kind.Should().Be(SecurableElementKind.EducationOrganization);
            results[0].Steps[0].SourceColumnName.Should().Be(Col("SchoolReference_SchoolId"));
        }

        [Test]
        public void It_should_throw_when_all_person_paths_are_array_nested()
        {
            // Resource has only array-nested Student paths — should throw.
            var rootTable = CreateRootTable(Table("TestResource"));
            var model = CreateModel("Ed-Fi", "TestResource", rootTable);

            var securableElements = new ResourceSecurableElements(
                [],
                [],
                ["$.students[*].studentReference.studentUniqueId"],
                [],
                []
            );

            var concrete = CreateConcrete(1, "Ed-Fi", "TestResource", model, securableElements);

            var act = () => SecurableElementColumnPathResolver.ResolveAll(concrete, [concrete]);

            act.Should()
                .Throw<InvalidOperationException>()
                .WithMessage("*Ed-Fi.TestResource*")
                .WithMessage("*unsupported child-table traversal*")
                .WithMessage("*$.students[*].studentReference.studentUniqueId*");
        }
    }

    [TestFixture]
    public class Given_unresolvable_securable_element
    {
        [Test]
        public void It_should_throw_when_edorg_reference_binding_is_missing()
        {
            // Resource declares an EdOrg securable element but has no matching binding
            var rootTable = CreateRootTable(Table("TestResource"));
            var model = CreateModel("Ed-Fi", "TestResource", rootTable);

            var securableElements = new ResourceSecurableElements(
                [new EdOrgSecurableElement("$.schoolReference.schoolId", "SchoolId")],
                [],
                [],
                [],
                []
            );

            var concrete = CreateConcrete(1, "Ed-Fi", "TestResource", model, securableElements);

            var act = () => SecurableElementColumnPathResolver.ResolveAll(concrete, [concrete]);

            act.Should()
                .Throw<InvalidOperationException>()
                .WithMessage("*Ed-Fi.TestResource*")
                .WithMessage("*$.schoolReference.schoolId*");
        }

        [Test]
        public void It_should_throw_when_namespace_path_is_unresolvable()
        {
            // Resource declares a Namespace securable element but has no matching
            // reference identity binding or scalar column on the root table
            var rootTable = CreateRootTable(Table("TestResource"));
            var model = CreateModel("Ed-Fi", "TestResource", rootTable);

            var securableElements = new ResourceSecurableElements(
                [],
                ["$.assessmentAdministrationReference.namespace"],
                [],
                [],
                []
            );

            var concrete = CreateConcrete(1, "Ed-Fi", "TestResource", model, securableElements);

            var act = () => SecurableElementColumnPathResolver.ResolveAll(concrete, [concrete]);

            act.Should()
                .Throw<InvalidOperationException>()
                .WithMessage("*Ed-Fi.TestResource*")
                .WithMessage("*$.assessmentAdministrationReference.namespace*");
        }

        [Test]
        public void It_should_throw_when_person_path_has_no_matching_binding()
        {
            // Resource declares a Student securable element but has no binding for the reference
            var rootTable = CreateRootTable(Table("TestResource"));
            var model = CreateModel("Ed-Fi", "TestResource", rootTable);

            var securableElements = new ResourceSecurableElements(
                [],
                [],
                ["$.studentReference.studentUniqueId"],
                [],
                []
            );

            var concrete = CreateConcrete(1, "Ed-Fi", "TestResource", model, securableElements);

            var act = () => SecurableElementColumnPathResolver.ResolveAll(concrete, [concrete]);

            act.Should()
                .Throw<InvalidOperationException>()
                .WithMessage("*Ed-Fi.TestResource*")
                .WithMessage("*$.studentReference.studentUniqueId*");
        }

        [Test]
        public void It_should_throw_when_target_resource_missing_from_all_resources()
        {
            // Binding points to Ed-Fi.Student but Student is not in allResources
            var rootTable = CreateRootTable(
                Table("TestResource"),
                [
                    new DbColumnModel(
                        Col("Student_DocumentId"),
                        ColumnKind.DocumentFk,
                        null,
                        false,
                        null,
                        new QualifiedResourceName("Ed-Fi", "Student")
                    ),
                ]
            );

            var binding = new DocumentReferenceBinding(
                true,
                Path("$.studentReference"),
                rootTable.Table,
                Col("Student_DocumentId"),
                new QualifiedResourceName("Ed-Fi", "Student"),
                [
                    new ReferenceIdentityBinding(
                        Path("$.studentReference.studentUniqueId"),
                        Col("Student_StudentUniqueId")
                    ),
                ]
            );

            var model = CreateModel("Ed-Fi", "TestResource", rootTable, [binding]);

            var securableElements = new ResourceSecurableElements(
                [],
                [],
                ["$.studentReference.studentUniqueId"],
                [],
                []
            );

            // Student concrete is intentionally NOT included in allResources
            var testConcrete = CreateConcrete(1, "Ed-Fi", "TestResource", model, securableElements);

            var act = () => SecurableElementColumnPathResolver.ResolveAll(testConcrete, [testConcrete]);

            act.Should()
                .Throw<InvalidOperationException>()
                .WithMessage("*Ed-Fi.TestResource*")
                .WithMessage("*$.studentReference.studentUniqueId*");
        }

        [Test]
        public void It_should_throw_when_transitive_target_resource_missing()
        {
            // CourseTranscript -> StudentAcademicRecord -> Student
            // but Student is not in allResources
            var ctRoot = CreateRootTable(
                Table("CourseTranscript"),
                [
                    new DbColumnModel(
                        Col("StudentAcademicRecord_DocumentId"),
                        ColumnKind.DocumentFk,
                        null,
                        false,
                        null,
                        new QualifiedResourceName("Ed-Fi", "StudentAcademicRecord")
                    ),
                ]
            );

            var sarRoot = CreateRootTable(
                Table("StudentAcademicRecord"),
                [
                    new DbColumnModel(
                        Col("Student_DocumentId"),
                        ColumnKind.DocumentFk,
                        null,
                        false,
                        null,
                        new QualifiedResourceName("Ed-Fi", "Student")
                    ),
                ]
            );

            var ctBinding = new DocumentReferenceBinding(
                true,
                Path("$.studentAcademicRecordReference"),
                ctRoot.Table,
                Col("StudentAcademicRecord_DocumentId"),
                new QualifiedResourceName("Ed-Fi", "StudentAcademicRecord"),
                [
                    new ReferenceIdentityBinding(
                        Path("$.studentAcademicRecordReference.studentUniqueId"),
                        Col("StudentAcademicRecord_StudentUniqueId")
                    ),
                ]
            );

            var sarBinding = new DocumentReferenceBinding(
                true,
                Path("$.studentReference"),
                sarRoot.Table,
                Col("Student_DocumentId"),
                new QualifiedResourceName("Ed-Fi", "Student"),
                [
                    new ReferenceIdentityBinding(
                        Path("$.studentReference.studentUniqueId"),
                        Col("Student_StudentUniqueId")
                    ),
                ]
            );

            var ctModel = CreateModel("Ed-Fi", "CourseTranscript", ctRoot, [ctBinding]);
            var sarModel = CreateModel("Ed-Fi", "StudentAcademicRecord", sarRoot, [sarBinding]);

            var securableElements = new ResourceSecurableElements(
                [],
                [],
                ["$.studentAcademicRecordReference.studentUniqueId"],
                [],
                []
            );

            var ctConcrete = CreateConcrete(1, "Ed-Fi", "CourseTranscript", ctModel, securableElements);
            var sarConcrete = CreateConcrete(2, "Ed-Fi", "StudentAcademicRecord", sarModel);

            // Student is intentionally NOT in allResources — BFS should fail to resolve
            var act = () =>
                SecurableElementColumnPathResolver.ResolveAll(ctConcrete, [ctConcrete, sarConcrete]);

            act.Should()
                .Throw<InvalidOperationException>()
                .WithMessage("*Ed-Fi.CourseTranscript*")
                .WithMessage("*$.studentAcademicRecordReference.studentUniqueId*");
        }
    }
}
