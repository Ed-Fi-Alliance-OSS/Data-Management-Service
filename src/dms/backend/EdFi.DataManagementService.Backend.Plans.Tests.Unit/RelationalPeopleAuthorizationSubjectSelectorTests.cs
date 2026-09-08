// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.External.Security;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_RelationalPeopleAuthorizationSubjectSelector
{
    private static readonly DbSchemaName _edfiSchema = new("edfi");
    private static readonly DbColumnName _documentId = new("DocumentId");

    [Test]
    public void It_should_select_a_direct_student_document_id_reference()
    {
        var resource = new QualifiedResourceName("Ed-Fi", "StudentCarrier");
        var studentReferencePath = "$.studentReference.studentUniqueId";
        var mappingSet = CreateMappingSet(
            CreateCarrierResource(
                resource.ResourceName,
                new PersonReferenceSpec(
                    SecurableElementKind.Student,
                    studentReferencePath,
                    Col("Student_DocumentId")
                )
            ),
            CreatePersonResource(SecurableElementKind.Student)
        );

        var result = SelectSubjects(
            mappingSet,
            resource,
            AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly
        );

        result.SecurityConfigurationFailures.Should().BeEmpty();
        result.StrategySubjectSelections.Should().ContainSingle();

        var subject = result.StrategySubjectSelections[0].Subjects.Should().ContainSingle().Subject;

        subject.IsPersonSubject.Should().BeTrue();
        subject.Table.Should().Be(Table("StudentCarrier"));
        subject.Column.Should().Be(Col("Student_DocumentId"));
        subject.Column.Value.Should().NotContain("UniqueId").And.NotContain("USI");
        subject
            .Contributors.Should()
            .ContainSingle()
            .Which.Should()
            .Be(
                new RelationshipAuthorizationSubjectContributor(
                    SecurableElementKind.Student,
                    studentReferencePath,
                    "StudentUniqueId",
                    0
                )
            );
        subject.PersonMetadata!.PersonKind.Should().Be(RelationshipAuthorizationPersonKind.Student);
        subject
            .PersonMetadata.Path.Kind.Should()
            .Be(RelationshipAuthorizationPersonSubjectPathKind.DirectRootColumn);
        subject.PersonMetadata.Path.Steps.Should().ContainSingle();
        subject.PersonMetadata.Path.Steps[0].SourceTable.Should().Be(Table("StudentCarrier"));
        subject.PersonMetadata.Path.Steps[0].SourceColumnName.Should().Be(Col("Student_DocumentId"));
        subject.PersonMetadata.Path.Steps[0].TargetTable.Should().Be(Table("Student"));
        subject.PersonMetadata.Path.Steps[0].TargetColumnName.Should().Be(_documentId);
        subject
            .AuthObject.Should()
            .Be(
                RelationshipAuthorizationAuthObject.CreatePerson(
                    RelationshipAuthorizationPersonAuthViewKind.Student
                )
            );
        subject.PersonMetadata.StoredAnchor.RootTable.Should().Be(Table("StudentCarrier"));
        subject.PersonMetadata.StoredAnchor.RootDocumentIdColumn.Should().Be(_documentId);
    }

    [TestCase(SecurableElementKind.Contact, RelationshipAuthorizationPersonKind.Contact)]
    [TestCase(SecurableElementKind.Staff, RelationshipAuthorizationPersonKind.Staff)]
    public void It_should_select_direct_contact_and_staff_document_id_references(
        SecurableElementKind securableElementKind,
        RelationshipAuthorizationPersonKind personKind
    )
    {
        var resource = new QualifiedResourceName("Ed-Fi", $"{personKind}Carrier");
        var referencePath = GetPersonReferenceJsonPath(securableElementKind);
        var documentIdColumn = Col($"{personKind}_DocumentId");
        var mappingSet = CreateMappingSet(
            CreateCarrierResource(
                resource.ResourceName,
                new PersonReferenceSpec(securableElementKind, referencePath, documentIdColumn)
            ),
            CreatePersonResource(securableElementKind)
        );

        var result = SelectSubjects(
            mappingSet,
            resource,
            AuthorizationStrategyNameConstants.RelationshipsWithPeopleOnly
        );

        result.SecurityConfigurationFailures.Should().BeEmpty();

        var subject = result.StrategySubjectSelections.Single().Subjects.Should().ContainSingle().Subject;

        subject.Table.Should().Be(Table(resource.ResourceName));
        subject.Column.Should().Be(documentIdColumn);
        subject.Column.Value.Should().NotContain("UniqueId").And.NotContain("USI");
        subject.Contributors[0].Kind.Should().Be(securableElementKind);
        subject.Contributors[0].JsonPath.Should().Be(referencePath);
        subject.PersonMetadata!.PersonKind.Should().Be(personKind);
        subject
            .PersonMetadata.Path.Kind.Should()
            .Be(RelationshipAuthorizationPersonSubjectPathKind.DirectRootColumn);
        subject
            .AuthObject.Should()
            .Be(
                RelationshipAuthorizationAuthObject.CreatePerson(GetPersonAuthViewKind(securableElementKind))
            );
    }

    [Test]
    public void It_should_stamp_people_only_student_and_contact_subjects_with_distinct_auth_objects()
    {
        var resource = new QualifiedResourceName("Ed-Fi", "StudentContactCarrier");
        var studentReferencePath = "$.studentReference.studentUniqueId";
        var contactReferencePath = "$.contactReference.contactUniqueId";
        var mappingSet = CreateMappingSet(
            CreateCarrierResource(
                resource.ResourceName,
                new PersonReferenceSpec(
                    SecurableElementKind.Student,
                    studentReferencePath,
                    Col("Student_DocumentId")
                ),
                new PersonReferenceSpec(
                    SecurableElementKind.Contact,
                    contactReferencePath,
                    Col("Contact_DocumentId")
                )
            ),
            CreatePersonResource(SecurableElementKind.Student),
            CreatePersonResource(SecurableElementKind.Contact)
        );

        var result = SelectSubjects(
            mappingSet,
            resource,
            AuthorizationStrategyNameConstants.RelationshipsWithPeopleOnly
        );

        result.SecurityConfigurationFailures.Should().BeEmpty();

        var strategySelection = result.StrategySubjectSelections.Should().ContainSingle().Subject;

        strategySelection.Subjects.Should().HaveCount(2);

        var subjectsByKind = strategySelection.Subjects.ToDictionary(static subject =>
            subject.Contributors.Single().Kind
        );

        subjectsByKind[SecurableElementKind.Student]
            .AuthObject.Should()
            .Be(
                RelationshipAuthorizationAuthObject.CreatePerson(
                    RelationshipAuthorizationPersonAuthViewKind.Student
                )
            );
        subjectsByKind[SecurableElementKind.Contact]
            .AuthObject.Should()
            .Be(
                RelationshipAuthorizationAuthObject.CreatePerson(
                    RelationshipAuthorizationPersonAuthViewKind.Contact
                )
            );
        subjectsByKind[SecurableElementKind.Student]
            .AuthObject.Should()
            .NotBe(subjectsByKind[SecurableElementKind.Contact].AuthObject);
    }

    [Test]
    public void It_should_order_people_only_subjects_by_strategy_eligibility_before_physical_columns()
    {
        var resource = new QualifiedResourceName("Ed-Fi", "PeopleOrdinalCarrier");
        var studentReferencePath = "$.zStudentReference.studentUniqueId";
        var contactReferencePath = "$.aContactReference.contactUniqueId";
        var staffReferencePath = "$.bStaffReference.staffUniqueId";
        var mappingSet = CreateMappingSet(
            CreateCarrierResource(
                resource.ResourceName,
                new PersonReferenceSpec(
                    SecurableElementKind.Student,
                    studentReferencePath,
                    Col("ZStudent_DocumentId")
                ),
                new PersonReferenceSpec(
                    SecurableElementKind.Contact,
                    contactReferencePath,
                    Col("AContact_DocumentId")
                ),
                new PersonReferenceSpec(
                    SecurableElementKind.Staff,
                    staffReferencePath,
                    Col("BStaff_DocumentId")
                )
            ),
            CreatePersonResource(SecurableElementKind.Student),
            CreatePersonResource(SecurableElementKind.Contact),
            CreatePersonResource(SecurableElementKind.Staff)
        );

        var result = SelectSubjects(
            mappingSet,
            resource,
            AuthorizationStrategyNameConstants.RelationshipsWithPeopleOnly
        );

        result.SecurityConfigurationFailures.Should().BeEmpty();

        var subjects = result.StrategySubjectSelections.Single().Subjects;

        subjects
            .Select(static subject => subject.Contributors.Single().Kind)
            .Should()
            .Equal(SecurableElementKind.Student, SecurableElementKind.Contact, SecurableElementKind.Staff);
        subjects
            .Select(static subject => subject.Column.Value)
            .Should()
            .Equal("ZStudent_DocumentId", "AContact_DocumentId", "BStaff_DocumentId");
    }

    [Test]
    public void It_should_report_no_applicable_strategy_when_no_matching_people_path_or_skipped_contributor_exists()
    {
        var resource = new QualifiedResourceName("Ed-Fi", "StudentCarrier");
        var studentReferencePath = "$.studentReference.studentUniqueId";
        var mappingSet = CreateMappingSet(
            CreateCarrierResource(
                resource.ResourceName,
                new PersonReferenceSpec(
                    SecurableElementKind.Student,
                    studentReferencePath,
                    Col("Student_DocumentId")
                )
            ),
            CreatePersonResource(SecurableElementKind.Student)
        );
        var expectedContactAuthObject = RelationshipAuthorizationAuthObject.CreatePerson(
            RelationshipAuthorizationPersonAuthViewKind.Contact
        );

        var result = RelationalPeopleAuthorizationSubjectSelector.Select(
            mappingSet,
            resource,
            [
                CreateSinglePeopleSupportedStrategy(
                    RelationshipAuthorizationStrategyKind.RelationshipsWithStudentsOnly,
                    AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly,
                    0,
                    SecurableElementKind.Student,
                    RelationshipAuthorizationPersonAuthViewKind.Student
                ),
                CreateSinglePeopleSupportedStrategy(
                    RelationshipAuthorizationStrategyKind.RelationshipsWithPeopleOnly,
                    AuthorizationStrategyNameConstants.RelationshipsWithPeopleOnly,
                    1,
                    SecurableElementKind.Contact,
                    RelationshipAuthorizationPersonAuthViewKind.Contact
                ),
            ]
        );

        var strategySelection = result.StrategySubjectSelections.Should().ContainSingle().Subject;

        strategySelection.ConfiguredStrategy.RawConfiguredIndex.Should().Be(0);
        strategySelection
            .Subjects.Should()
            .ContainSingle()
            .Which.Column.Should()
            .Be(Col("Student_DocumentId"));

        var failure = result.SecurityConfigurationFailures.Should().ContainSingle().Subject;

        failure.FailureKind.Should().Be(RelationshipAuthorizationFailureKind.NoApplicableRootSubject);
        failure.ConfiguredStrategy?.RawConfiguredIndex.Should().Be(1);
        failure.RelationshipLocalOrder.Should().Be(1);
        failure.AuthObject.Should().Be(expectedContactAuthObject);
        failure.Location!.Kind.Should().Be(SecurableElementKind.Contact);
        failure.Location.AuthorizationObjectName.Should().Be(expectedContactAuthObject.Name.ToString());
        failure.PersonMetadata!.PersonKind.Should().Be(RelationshipAuthorizationPersonKind.Contact);
        failure.PersonMetadata.AuthObject.Should().Be(expectedContactAuthObject);
        failure.SkippedContributors.Should().BeEmpty();
    }

    [Test]
    public void It_should_select_a_transitive_course_transcript_to_student_path()
    {
        var courseTranscript = new QualifiedResourceName("Ed-Fi", "CourseTranscript");
        var courseTranscriptStudentPath = "$.studentAcademicRecordReference.studentUniqueId";
        var studentAcademicRecordStudentPath = "$.studentReference.studentUniqueId";

        var courseTranscriptRoot = CreateRootTable(Table("CourseTranscript"));
        var courseTranscriptModel = CreateModelWithTables(
            "CourseTranscript",
            courseTranscriptRoot,
            [
                new DocumentReferenceBinding(
                    true,
                    Path("$.studentAcademicRecordReference"),
                    courseTranscriptRoot.Table,
                    Col("StudentAcademicRecord_DocumentId"),
                    new QualifiedResourceName("Ed-Fi", "StudentAcademicRecord"),
                    [
                        new ReferenceIdentityBinding(
                            Path(courseTranscriptStudentPath),
                            Path(courseTranscriptStudentPath),
                            Col("StudentAcademicRecord_StudentUniqueId")
                        ),
                    ]
                ),
            ]
        );
        var studentAcademicRecordRoot = CreateRootTable(Table("StudentAcademicRecord"));
        var studentAcademicRecordModel = CreateModelWithTables(
            "StudentAcademicRecord",
            studentAcademicRecordRoot,
            [
                new DocumentReferenceBinding(
                    true,
                    Path("$.studentReference"),
                    studentAcademicRecordRoot.Table,
                    Col("Student_DocumentId"),
                    new QualifiedResourceName("Ed-Fi", "Student"),
                    [
                        new ReferenceIdentityBinding(
                            Path(studentAcademicRecordStudentPath),
                            Path(studentAcademicRecordStudentPath),
                            Col("Student_StudentUniqueId")
                        ),
                    ]
                ),
            ]
        );
        var mappingSet = CreateMappingSet(
            CreateConcrete(
                "CourseTranscript",
                courseTranscriptModel,
                new ResourceSecurableElements([], [], [courseTranscriptStudentPath], [], [])
            ),
            CreateConcrete(
                "StudentAcademicRecord",
                studentAcademicRecordModel,
                new ResourceSecurableElements([], [], [studentAcademicRecordStudentPath], [], [])
            ),
            CreatePersonResource(SecurableElementKind.Student)
        );

        var result = SelectSubjects(
            mappingSet,
            courseTranscript,
            AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly
        );

        result.SecurityConfigurationFailures.Should().BeEmpty();

        var subject = result.StrategySubjectSelections.Single().Subjects.Should().ContainSingle().Subject;

        subject.Table.Should().Be(Table("StudentAcademicRecord"));
        subject.Column.Should().Be(Col("Student_DocumentId"));
        subject
            .PersonMetadata!.Path.Kind.Should()
            .Be(RelationshipAuthorizationPersonSubjectPathKind.TransitiveJoinPath);
        subject.PersonMetadata.Path.Steps.Should().HaveCount(2);
        subject
            .PersonMetadata.Path.Steps.Select(static step => step.SourceColumnName.Value)
            .Should()
            .Equal("StudentAcademicRecord_DocumentId", "Student_DocumentId");
        subject
            .PersonMetadata.Path.Steps.Select(static step => step.SourceColumnName.Value)
            .Should()
            .OnlyContain(static column => !column.Contains("UniqueId") && !column.Contains("USI"));
        subject
            .Contributors.Should()
            .ContainSingle()
            .Which.Should()
            .Be(
                new RelationshipAuthorizationSubjectContributor(
                    SecurableElementKind.Student,
                    courseTranscriptStudentPath,
                    "StudentUniqueId",
                    0
                )
            );
    }

    [TestCase(SecurableElementKind.Student, RelationshipAuthorizationPersonKind.Student)]
    [TestCase(SecurableElementKind.Contact, RelationshipAuthorizationPersonKind.Contact)]
    [TestCase(SecurableElementKind.Staff, RelationshipAuthorizationPersonKind.Staff)]
    public void It_should_select_zero_hop_self_person_document_id_subjects(
        SecurableElementKind securableElementKind,
        RelationshipAuthorizationPersonKind personKind
    )
    {
        var resource = new QualifiedResourceName("Ed-Fi", GetPersonResourceName(securableElementKind));
        var mappingSet = CreateMappingSet(CreatePersonResource(securableElementKind));

        var result = SelectSubjects(mappingSet, resource, GetPersonStrategyName(securableElementKind));

        result.SecurityConfigurationFailures.Should().BeEmpty();

        var subject = result.StrategySubjectSelections.Single().Subjects.Should().ContainSingle().Subject;

        subject.Table.Should().Be(Table(resource.ResourceName));
        subject.Column.Should().Be(_documentId);
        subject.PersonMetadata!.PersonKind.Should().Be(personKind);
        subject
            .PersonMetadata.Path.Kind.Should()
            .Be(RelationshipAuthorizationPersonSubjectPathKind.SelfRootDocumentId);
        subject.PersonMetadata.Path.Steps.Should().BeEmpty();
        subject
            .Contributors.Should()
            .ContainSingle()
            .Which.Should()
            .Be(
                new RelationshipAuthorizationSubjectContributor(
                    securableElementKind,
                    GetPersonSelfJsonPath(securableElementKind),
                    $"{personKind}UniqueId",
                    0
                )
            );
    }

    [Test]
    public void It_should_resolve_a_same_kind_person_reference_on_a_person_resource_through_the_reference_document_id()
    {
        var resource = new QualifiedResourceName("Ed-Fi", "Student");
        var relatedStudentReferencePath = "$.relatedStudentReference.studentUniqueId";
        var relatedStudentDocumentIdColumn = Col("RelatedStudent_DocumentId");
        var mappingSet = CreateMappingSet(
            CreateCarrierResource(
                resource.ResourceName,
                new PersonReferenceSpec(
                    SecurableElementKind.Student,
                    relatedStudentReferencePath,
                    relatedStudentDocumentIdColumn
                )
            )
        );

        var result = SelectSubjects(
            mappingSet,
            resource,
            AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly
        );

        result.SecurityConfigurationFailures.Should().BeEmpty();

        var subject = result.StrategySubjectSelections.Single().Subjects.Should().ContainSingle().Subject;

        subject.Table.Should().Be(Table(resource.ResourceName));
        subject.Column.Should().Be(relatedStudentDocumentIdColumn);
        subject
            .PersonMetadata!.Path.Kind.Should()
            .Be(RelationshipAuthorizationPersonSubjectPathKind.DirectRootColumn);
        subject.PersonMetadata.Path.Steps.Should().ContainSingle();
        subject.PersonMetadata.Path.Steps[0].SourceTable.Should().Be(Table(resource.ResourceName));
        subject.PersonMetadata.Path.Steps[0].SourceColumnName.Should().Be(relatedStudentDocumentIdColumn);
        subject.PersonMetadata.Path.Steps[0].TargetTable.Should().Be(Table(resource.ResourceName));
        subject.PersonMetadata.Path.Steps[0].TargetColumnName.Should().Be(_documentId);
        subject
            .Contributors.Should()
            .ContainSingle()
            .Which.Should()
            .Be(
                new RelationshipAuthorizationSubjectContributor(
                    SecurableElementKind.Student,
                    relatedStudentReferencePath,
                    "StudentUniqueId",
                    0
                )
            );
    }

    [Test]
    public void It_should_fail_unresolved_non_self_same_kind_person_paths_on_person_resources()
    {
        var resource = new QualifiedResourceName("Ed-Fi", "Student");
        var unresolvedStudentReferencePath = "$.relatedStudentReference.studentUniqueId";
        var mappingSet = CreateMappingSet(
            CreateConcrete(
                resource.ResourceName,
                CreateModelWithTables(
                    resource.ResourceName,
                    CreateRootTable(Table(resource.ResourceName)),
                    []
                ),
                new ResourceSecurableElements([], [], [unresolvedStudentReferencePath], [], [])
            )
        );

        var result = SelectSubjects(
            mappingSet,
            resource,
            AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly
        );

        result.StrategySubjectSelections.Should().BeEmpty();

        var failure = result.SecurityConfigurationFailures.Should().ContainSingle().Subject;

        failure.FailureKind.Should().Be(RelationshipAuthorizationFailureKind.UnresolvedSecurableElement);
        failure.Location!.Kind.Should().Be(SecurableElementKind.Student);
        failure.Location.JsonPath.Should().Be(unresolvedStudentReferencePath);
        failure.Location.ReadableName.Should().Be("StudentUniqueId");
        failure.PersonMetadata!.PersonKind.Should().Be(RelationshipAuthorizationPersonKind.Student);
        failure
            .PersonMetadata.AuthObject.Should()
            .Be(
                RelationshipAuthorizationAuthObject.CreatePerson(
                    RelationshipAuthorizationPersonAuthViewKind.Student
                )
            );
    }

    [Test]
    public void It_should_preserve_self_person_subject_and_fail_broken_same_kind_sibling_path()
    {
        var resource = new QualifiedResourceName("Ed-Fi", "Student");
        var unresolvedStudentReferencePath = "$.nonexistentReference.studentUniqueId";
        var mappingSet = CreateMappingSet(
            CreateConcrete(
                resource.ResourceName,
                CreateModelWithTables(
                    resource.ResourceName,
                    CreateRootTable(Table(resource.ResourceName)),
                    []
                ),
                new ResourceSecurableElements(
                    [],
                    [],
                    ["$.studentUniqueId", unresolvedStudentReferencePath],
                    [],
                    []
                )
            )
        );

        var result = SelectSubjects(
            mappingSet,
            resource,
            AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly
        );

        var selectedSubject = result
            .StrategySubjectSelections.Single()
            .Subjects.Should()
            .ContainSingle()
            .Subject;
        selectedSubject
            .PersonMetadata!.Path.Kind.Should()
            .Be(RelationshipAuthorizationPersonSubjectPathKind.SelfRootDocumentId);
        selectedSubject
            .Contributors.Select(static contributor => contributor.JsonPath)
            .Should()
            .Equal("$.studentUniqueId");

        var failure = result.SecurityConfigurationFailures.Should().ContainSingle().Subject;
        failure.FailureKind.Should().Be(RelationshipAuthorizationFailureKind.UnresolvedSecurableElement);
        failure.Location!.Kind.Should().Be(SecurableElementKind.Student);
        failure.Location.JsonPath.Should().Be(unresolvedStudentReferencePath);
        failure.Contributors.Should().ContainSingle().Which.ContributionOrder.Should().Be(1);
    }

    [Test]
    public void It_should_preserve_resolved_people_subjects_when_other_same_strategy_people_paths_are_unresolved()
    {
        var resource = new QualifiedResourceName("Ed-Fi", "PartiallyResolvedStudentCarrier");
        var resolvedStudentPath = "$.studentReference.studentUniqueId";
        var unresolvedStudentPath = "$.missingStudentReference.studentUniqueId";
        var studentDocumentIdColumn = Col("Student_DocumentId");
        var rootTable = CreateRootTable(Table(resource.ResourceName));
        var model = CreateModelWithTables(
            resource.ResourceName,
            rootTable,
            [
                new DocumentReferenceBinding(
                    true,
                    Path("$.studentReference"),
                    rootTable.Table,
                    studentDocumentIdColumn,
                    new QualifiedResourceName("Ed-Fi", "Student"),
                    [
                        new ReferenceIdentityBinding(
                            Path(resolvedStudentPath),
                            Path(resolvedStudentPath),
                            Col("StudentUniqueId")
                        ),
                    ]
                ),
            ]
        );
        var mappingSet = CreateMappingSet(
            CreateConcrete(
                resource.ResourceName,
                model,
                new ResourceSecurableElements([], [], [resolvedStudentPath, unresolvedStudentPath], [], [])
            ),
            CreatePersonResource(SecurableElementKind.Student)
        );

        var result = SelectSubjects(
            mappingSet,
            resource,
            AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly
        );

        var strategySelection = result.StrategySubjectSelections.Should().ContainSingle().Subject;
        var subject = strategySelection.Subjects.Should().ContainSingle().Subject;

        subject.Table.Should().Be(Table(resource.ResourceName));
        subject.Column.Should().Be(studentDocumentIdColumn);
        subject.Contributors.Should().ContainSingle().Which.JsonPath.Should().Be(resolvedStudentPath);

        var failure = result.SecurityConfigurationFailures.Should().ContainSingle().Subject;

        failure.FailureKind.Should().Be(RelationshipAuthorizationFailureKind.UnresolvedSecurableElement);
        failure.Location!.Kind.Should().Be(SecurableElementKind.Student);
        failure.Location.JsonPath.Should().Be(unresolvedStudentPath);
        failure.Contributors.Should().ContainSingle().Which.JsonPath.Should().Be(unresolvedStudentPath);
    }

    [Test]
    public void It_should_preserve_contribution_order_for_multiple_unresolved_people_paths()
    {
        var resource = new QualifiedResourceName("Ed-Fi", "UnresolvedStudentCarrier");
        var firstConfiguredPath = "$.zStudentReference.studentUniqueId";
        var secondConfiguredPath = "$.aStudentReference.studentUniqueId";
        var mappingSet = CreateMappingSet(
            CreateConcrete(
                resource.ResourceName,
                CreateModelWithTables(
                    resource.ResourceName,
                    CreateRootTable(Table(resource.ResourceName)),
                    []
                ),
                new ResourceSecurableElements([], [], [firstConfiguredPath, secondConfiguredPath], [], [])
            )
        );

        var result = SelectSubjects(
            mappingSet,
            resource,
            AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly
        );

        result
            .SecurityConfigurationFailures.Select(static failure =>
                (failure.Location!.JsonPath, failure.Contributors.Single().ContributionOrder)
            )
            .Should()
            .Equal((firstConfiguredPath, 0), (secondConfiguredPath, 1));
    }

    [Test]
    public void It_should_create_one_subject_per_independent_declared_person_path()
    {
        var resource = new QualifiedResourceName("Ed-Fi", "DualStudentCarrier");
        var mappingSet = CreateMappingSet(
            CreateCarrierResource(
                resource.ResourceName,
                new PersonReferenceSpec(
                    SecurableElementKind.Student,
                    "$.primaryStudentReference.studentUniqueId",
                    Col("PrimaryStudent_DocumentId")
                ),
                new PersonReferenceSpec(
                    SecurableElementKind.Student,
                    "$.secondaryStudentReference.studentUniqueId",
                    Col("SecondaryStudent_DocumentId")
                )
            ),
            CreatePersonResource(SecurableElementKind.Student)
        );

        var result = SelectSubjects(
            mappingSet,
            resource,
            AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly
        );

        result.SecurityConfigurationFailures.Should().BeEmpty();

        result
            .StrategySubjectSelections.Single()
            .Subjects.Select(static subject => subject.Column.Value)
            .Should()
            .Equal("PrimaryStudent_DocumentId", "SecondaryStudent_DocumentId");
        result
            .StrategySubjectSelections.Single()
            .Subjects.Select(static subject => subject.Contributors.Single().ContributionOrder)
            .Should()
            .Equal(0, 1);
    }

    [Test]
    public void It_should_dedupe_identical_person_predicates_and_preserve_contributors()
    {
        var resource = new QualifiedResourceName("Ed-Fi", "AliasStudentCarrier");
        var firstPath = "$.studentReference.studentUniqueId";
        var secondPath = "$.alternateStudentReference.studentUniqueId";
        var studentDocumentIdColumn = Col("Student_DocumentId");
        var rootTable = CreateRootTable(Table(resource.ResourceName));
        var model = CreateModelWithTables(
            resource.ResourceName,
            rootTable,
            [
                new DocumentReferenceBinding(
                    true,
                    Path("$.studentReference"),
                    rootTable.Table,
                    studentDocumentIdColumn,
                    new QualifiedResourceName("Ed-Fi", "Student"),
                    [
                        new ReferenceIdentityBinding(
                            Path(firstPath),
                            Path(firstPath),
                            Col("StudentUniqueId")
                        ),
                        new ReferenceIdentityBinding(
                            Path(secondPath),
                            Path(secondPath),
                            Col("AlternateStudentUniqueId")
                        ),
                    ]
                ),
            ]
        );
        var mappingSet = CreateMappingSet(
            CreateConcrete(
                resource.ResourceName,
                model,
                new ResourceSecurableElements([], [], [firstPath, secondPath], [], [])
            ),
            CreatePersonResource(SecurableElementKind.Student)
        );

        var result = SelectSubjects(
            mappingSet,
            resource,
            AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly
        );

        result.SecurityConfigurationFailures.Should().BeEmpty();

        var subject = result.StrategySubjectSelections.Single().Subjects.Should().ContainSingle().Subject;

        subject.Column.Should().Be(studentDocumentIdColumn);
        subject
            .Contributors.Select(static contributor => (contributor.JsonPath, contributor.ContributionOrder))
            .Should()
            .Equal((firstPath, 0), (secondPath, 1));
    }

    [Test]
    public void It_should_exclude_child_collection_people_paths_and_retain_skipped_metadata()
    {
        var resource = new QualifiedResourceName("Ed-Fi", "StudentCollectionCarrier");
        var rootPath = "$.studentReference.studentUniqueId";
        var childPath = "$.studentReferences[*].studentReference.studentUniqueId";
        var rootTable = CreateRootTable(Table(resource.ResourceName));
        var childTableName = Table("StudentCollectionCarrierStudentReference");
        var childTable = CreateChildTable(childTableName, "$.studentReferences[*]");
        var model = CreateModelWithTables(
            resource.ResourceName,
            rootTable,
            [childTable],
            [
                new DocumentReferenceBinding(
                    true,
                    Path("$.studentReference"),
                    rootTable.Table,
                    Col("Student_DocumentId"),
                    new QualifiedResourceName("Ed-Fi", "Student"),
                    [new ReferenceIdentityBinding(Path(rootPath), Path(rootPath), Col("StudentUniqueId"))]
                ),
                new DocumentReferenceBinding(
                    true,
                    Path("$.studentReferences[*].studentReference"),
                    childTableName,
                    Col("CollectionStudent_DocumentId"),
                    new QualifiedResourceName("Ed-Fi", "Student"),
                    [
                        new ReferenceIdentityBinding(
                            Path(childPath),
                            Path(childPath),
                            Col("CollectionStudentUniqueId")
                        ),
                    ]
                ),
            ]
        );
        var mappingSet = CreateMappingSet(
            CreateConcrete(
                resource.ResourceName,
                model,
                new ResourceSecurableElements([], [], [rootPath, childPath], [], [])
            ),
            CreatePersonResource(SecurableElementKind.Student)
        );

        var result = SelectSubjects(
            mappingSet,
            resource,
            AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly
        );

        result.SecurityConfigurationFailures.Should().BeEmpty();

        var strategySelection = result.StrategySubjectSelections.Should().ContainSingle().Subject;

        strategySelection.Subjects.Should().ContainSingle();
        strategySelection.Subjects[0].Column.Should().Be(Col("Student_DocumentId"));
        strategySelection
            .SkippedContributors.Should()
            .ContainSingle()
            .Which.Should()
            .Be(
                new RelationshipAuthorizationSkippedSubjectContributor(
                    SecurableElementKind.Student,
                    childPath,
                    "StudentUniqueId",
                    1,
                    RelationshipAuthorizationSkippedSubjectReason.ChildCollectionPersonPathOutsideSubjectScope,
                    RelationshipAuthorizationPersonKind.Student,
                    RelationshipAuthorizationAuthObject.CreatePerson(
                        RelationshipAuthorizationPersonAuthViewKind.Student
                    ),
                    childTableName,
                    Col("CollectionStudent_DocumentId")
                )
            );
    }

    [Test]
    public void It_should_fail_with_skipped_path_diagnostics_when_filtering_leaves_no_people_subjects()
    {
        var resource = new QualifiedResourceName("Ed-Fi", "StudentCollectionCarrier");
        var childPath = "$.studentReferences[*].studentReference.studentUniqueId";
        var rootTable = CreateRootTable(Table(resource.ResourceName));
        var childTableName = Table("StudentCollectionCarrierStudentReference");
        var childTable = CreateChildTable(childTableName, "$.studentReferences[*]");
        var model = CreateModelWithTables(
            resource.ResourceName,
            rootTable,
            [childTable],
            [
                new DocumentReferenceBinding(
                    true,
                    Path("$.studentReferences[*].studentReference"),
                    childTableName,
                    Col("CollectionStudent_DocumentId"),
                    new QualifiedResourceName("Ed-Fi", "Student"),
                    [
                        new ReferenceIdentityBinding(
                            Path(childPath),
                            Path(childPath),
                            Col("CollectionStudentUniqueId")
                        ),
                    ]
                ),
            ]
        );
        var mappingSet = CreateMappingSet(
            CreateConcrete(
                resource.ResourceName,
                model,
                new ResourceSecurableElements([], [], [childPath], [], [])
            ),
            CreatePersonResource(SecurableElementKind.Student)
        );

        var result = SelectSubjects(
            mappingSet,
            resource,
            AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly
        );

        result.StrategySubjectSelections.Should().BeEmpty();
        result
            .StrategySkippedContributors.Should()
            .ContainSingle()
            .Which.SkippedContributors.Should()
            .ContainSingle()
            .Which.Reason.Should()
            .Be(RelationshipAuthorizationSkippedSubjectReason.ChildCollectionPersonPathOutsideSubjectScope);

        var failure = result.SecurityConfigurationFailures.Should().ContainSingle().Subject;

        failure.FailureKind.Should().Be(RelationshipAuthorizationFailureKind.NoApplicableRootSubject);
        failure.Location!.Kind.Should().Be(SecurableElementKind.Student);
        failure.Location.JsonPath.Should().Be(childPath);
        failure.Location.Table.Should().Be(childTableName);
        failure.Location.Column.Should().Be(Col("CollectionStudent_DocumentId"));
        failure
            .Hint.Should()
            .Contain(
                nameof(
                    RelationshipAuthorizationSkippedSubjectReason.ChildCollectionPersonPathOutsideSubjectScope
                )
            );
        failure
            .SkippedContributors.Should()
            .ContainSingle()
            .Which.Reason.Should()
            .Be(RelationshipAuthorizationSkippedSubjectReason.ChildCollectionPersonPathOutsideSubjectScope);
    }

    [Test]
    public void It_should_preserve_duplicate_configured_strategies_as_separate_or_entries()
    {
        var resource = new QualifiedResourceName("Ed-Fi", "StudentCarrier");
        var studentReferencePath = "$.studentReference.studentUniqueId";
        var mappingSet = CreateMappingSet(
            CreateCarrierResource(
                resource.ResourceName,
                new PersonReferenceSpec(
                    SecurableElementKind.Student,
                    studentReferencePath,
                    Col("Student_DocumentId")
                )
            ),
            CreatePersonResource(SecurableElementKind.Student)
        );

        var result = SelectSubjects(
            mappingSet,
            resource,
            AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly,
            AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly
        );

        result.SecurityConfigurationFailures.Should().BeEmpty();
        result.StrategySubjectSelections.Should().HaveCount(2);
        result
            .StrategySubjectSelections.Select(static selection =>
                selection.ConfiguredStrategy.RawConfiguredIndex
            )
            .Should()
            .Equal(0, 1);
        result
            .StrategySubjectSelections.Select(static selection => selection.Subjects.Single().Column.Value)
            .Should()
            .Equal("Student_DocumentId", "Student_DocumentId");
        result
            .StrategySubjectSelections.Select(static selection => selection.Subjects.Single().AuthObject)
            .Should()
            .OnlyContain(static authObject =>
                authObject
                == RelationshipAuthorizationAuthObject.CreatePerson(
                    RelationshipAuthorizationPersonAuthViewKind.Student
                )
            );
    }

    [Test]
    public void It_should_keep_distinct_student_auth_views_as_separate_strategy_subjects()
    {
        var resource = new QualifiedResourceName("Ed-Fi", "StudentCarrier");
        var studentReferencePath = "$.studentReference.studentUniqueId";
        var mappingSet = CreateMappingSet(
            CreateCarrierResource(
                resource.ResourceName,
                new PersonReferenceSpec(
                    SecurableElementKind.Student,
                    studentReferencePath,
                    Col("Student_DocumentId")
                )
            ),
            CreatePersonResource(SecurableElementKind.Student)
        );

        var result = SelectSubjects(
            mappingSet,
            resource,
            AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly,
            AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnlyThroughResponsibility
        );

        result.SecurityConfigurationFailures.Should().BeEmpty();
        result.StrategySubjectSelections.Should().HaveCount(2);
        result
            .StrategySubjectSelections.Select(static selection =>
                selection.Subjects.Single().AuthObject.Name.Name
            )
            .Should()
            .Equal(
                "EducationOrganizationIdToStudentDocumentId",
                "EducationOrganizationIdToStudentDocumentIdThroughResponsibility"
            );
    }

    [Test]
    public void It_should_keep_normal_and_inverted_mixed_strategies_separate()
    {
        var resource = new QualifiedResourceName("Ed-Fi", "StudentCarrier");
        var studentReferencePath = "$.studentReference.studentUniqueId";
        var mappingSet = CreateMappingSet(
            CreateCarrierResource(
                resource.ResourceName,
                new PersonReferenceSpec(
                    SecurableElementKind.Student,
                    studentReferencePath,
                    Col("Student_DocumentId")
                )
            ),
            CreatePersonResource(SecurableElementKind.Student)
        );

        var result = SelectSubjects(
            mappingSet,
            resource,
            AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsAndPeople,
            AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsAndPeopleInverted
        );

        result.SecurityConfigurationFailures.Should().BeEmpty();
        result.StrategySubjectSelections.Should().HaveCount(2);
        result
            .StrategySubjectSelections.Select(static selection => selection.ConfiguredStrategy.StrategyName)
            .Should()
            .Equal(
                AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsAndPeople,
                AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsAndPeopleInverted
            );
        result
            .StrategySubjectSelections.Select(static selection => selection.Subjects.Single().Column.Value)
            .Should()
            .Equal("Student_DocumentId", "Student_DocumentId");
    }

    private static RelationalPeopleAuthorizationSubjectSelection SelectSubjects(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        params string[] strategyNames
    ) =>
        RelationalPeopleAuthorizationSubjectSelector.Select(
            mappingSet,
            resource,
            CreateSupportedStrategies(strategyNames)
        );

    private static IReadOnlyList<SupportedRelationshipAuthorizationStrategy> CreateSupportedStrategies(
        IReadOnlyList<string> strategyNames
    ) =>
        [
            .. strategyNames.Select(
                static (strategyName, index) =>
                    strategyName switch
                    {
                        AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly =>
                            new SupportedRelationshipAuthorizationStrategy(
                                RelationshipAuthorizationStrategyKind.RelationshipsWithStudentsOnly,
                                RelationshipAuthorizationHierarchyDirection.Normal,
                                new ConfiguredAuthorizationStrategy(strategyName, index),
                                index,
                                [
                                    new(
                                        SecurableElementKind.Student,
                                        RelationshipAuthorizationPersonAuthViewKind.Student
                                    ),
                                ]
                            ),
                        AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnlyThroughResponsibility =>
                            new SupportedRelationshipAuthorizationStrategy(
                                RelationshipAuthorizationStrategyKind.RelationshipsWithStudentsOnlyThroughResponsibility,
                                RelationshipAuthorizationHierarchyDirection.Normal,
                                new ConfiguredAuthorizationStrategy(strategyName, index),
                                index,
                                [
                                    new(
                                        SecurableElementKind.Student,
                                        RelationshipAuthorizationPersonAuthViewKind.StudentThroughResponsibility
                                    ),
                                ]
                            ),
                        AuthorizationStrategyNameConstants.RelationshipsWithPeopleOnly =>
                            new SupportedRelationshipAuthorizationStrategy(
                                RelationshipAuthorizationStrategyKind.RelationshipsWithPeopleOnly,
                                RelationshipAuthorizationHierarchyDirection.Normal,
                                new ConfiguredAuthorizationStrategy(strategyName, index),
                                index,
                                [
                                    new(
                                        SecurableElementKind.Student,
                                        RelationshipAuthorizationPersonAuthViewKind.Student
                                    ),
                                    new(
                                        SecurableElementKind.Contact,
                                        RelationshipAuthorizationPersonAuthViewKind.Contact
                                    ),
                                    new(
                                        SecurableElementKind.Staff,
                                        RelationshipAuthorizationPersonAuthViewKind.Staff
                                    ),
                                ]
                            ),
                        AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsAndPeople =>
                            new SupportedRelationshipAuthorizationStrategy(
                                RelationshipAuthorizationStrategyKind.RelationshipsWithEdOrgsAndPeople,
                                RelationshipAuthorizationHierarchyDirection.Normal,
                                new ConfiguredAuthorizationStrategy(strategyName, index),
                                index,
                                [
                                    new(SecurableElementKind.EducationOrganization),
                                    new(
                                        SecurableElementKind.Student,
                                        RelationshipAuthorizationPersonAuthViewKind.Student
                                    ),
                                    new(
                                        SecurableElementKind.Contact,
                                        RelationshipAuthorizationPersonAuthViewKind.Contact
                                    ),
                                    new(
                                        SecurableElementKind.Staff,
                                        RelationshipAuthorizationPersonAuthViewKind.Staff
                                    ),
                                ]
                            ),
                        AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsAndPeopleInverted =>
                            new SupportedRelationshipAuthorizationStrategy(
                                RelationshipAuthorizationStrategyKind.RelationshipsWithEdOrgsAndPeopleInverted,
                                RelationshipAuthorizationHierarchyDirection.Inverted,
                                new ConfiguredAuthorizationStrategy(strategyName, index),
                                index,
                                [
                                    new(SecurableElementKind.EducationOrganization),
                                    new(
                                        SecurableElementKind.Student,
                                        RelationshipAuthorizationPersonAuthViewKind.Student
                                    ),
                                    new(
                                        SecurableElementKind.Contact,
                                        RelationshipAuthorizationPersonAuthViewKind.Contact
                                    ),
                                    new(
                                        SecurableElementKind.Staff,
                                        RelationshipAuthorizationPersonAuthViewKind.Staff
                                    ),
                                ]
                            ),
                        _ => throw new ArgumentOutOfRangeException(
                            nameof(strategyName),
                            strategyName,
                            "Unsupported selector test strategy."
                        ),
                    }
            ),
        ];

    private static SupportedRelationshipAuthorizationStrategy CreateSinglePeopleSupportedStrategy(
        RelationshipAuthorizationStrategyKind strategyKind,
        string strategyName,
        int index,
        SecurableElementKind subjectKind,
        RelationshipAuthorizationPersonAuthViewKind authViewKind
    ) =>
        new(
            strategyKind,
            RelationshipAuthorizationHierarchyDirection.Normal,
            new ConfiguredAuthorizationStrategy(strategyName, index),
            index,
            [new RelationshipAuthorizationStrategySubjectEligibility(subjectKind, authViewKind)]
        );

    private static ConcreteResourceModel CreateCarrierResource(
        string resourceName,
        params PersonReferenceSpec[] references
    )
    {
        var rootTable = CreateRootTable(Table(resourceName));
        var model = CreateModelWithTables(
            resourceName,
            rootTable,
            [
                .. references.Select(reference => new DocumentReferenceBinding(
                    true,
                    Path(GetReferenceObjectPath(reference.JsonPath)),
                    rootTable.Table,
                    reference.DocumentIdColumn,
                    new QualifiedResourceName("Ed-Fi", GetPersonResourceName(reference.Kind)),
                    [
                        new ReferenceIdentityBinding(
                            Path(reference.JsonPath),
                            Path(reference.JsonPath),
                            Col($"{GetPersonResourceName(reference.Kind)}UniqueId")
                        ),
                    ]
                )),
            ]
        );

        return CreateConcrete(
            resourceName,
            model,
            new ResourceSecurableElements(
                [],
                [],
                [
                    .. references
                        .Where(static reference => reference.Kind is SecurableElementKind.Student)
                        .Select(static reference => reference.JsonPath),
                ],
                [
                    .. references
                        .Where(static reference => reference.Kind is SecurableElementKind.Contact)
                        .Select(static reference => reference.JsonPath),
                ],
                [
                    .. references
                        .Where(static reference => reference.Kind is SecurableElementKind.Staff)
                        .Select(static reference => reference.JsonPath),
                ]
            )
        );
    }

    private static ConcreteResourceModel CreatePersonResource(SecurableElementKind securableElementKind)
    {
        var resourceName = GetPersonResourceName(securableElementKind);

        return CreateConcrete(
            resourceName,
            CreateModelWithTables(resourceName, CreateRootTable(Table(resourceName)), []),
            securableElementKind switch
            {
                SecurableElementKind.Student => new ResourceSecurableElements(
                    [],
                    [],
                    [GetPersonSelfJsonPath(securableElementKind)],
                    [],
                    []
                ),
                SecurableElementKind.Contact => new ResourceSecurableElements(
                    [],
                    [],
                    [],
                    [GetPersonSelfJsonPath(securableElementKind)],
                    []
                ),
                SecurableElementKind.Staff => new ResourceSecurableElements(
                    [],
                    [],
                    [],
                    [],
                    [GetPersonSelfJsonPath(securableElementKind)]
                ),
                _ => ResourceSecurableElements.Empty,
            }
        );
    }

    private static MappingSet CreateMappingSet(params ConcreteResourceModel[] concreteResourceModels)
    {
        var resourceKeysInIdOrder = concreteResourceModels
            .Select(
                static (concreteResourceModel, index) =>
                    concreteResourceModel.ResourceKey with
                    {
                        ResourceKeyId = checked((short)(index + 1)),
                    }
            )
            .ToArray();
        var resourcesWithKeys = concreteResourceModels
            .Zip(
                resourceKeysInIdOrder,
                static (concreteResourceModel, resourceKey) =>
                    concreteResourceModel with
                    {
                        ResourceKey = resourceKey,
                    }
            )
            .ToArray();

        return new MappingSet(
            Key: new MappingSetKey("schema-hash", SqlDialect.Pgsql, "v1"),
            Model: new DerivedRelationalModelSet(
                EffectiveSchema: new EffectiveSchemaInfo(
                    ApiSchemaFormatVersion: "1.0",
                    RelationalMappingVersion: "v1",
                    EffectiveSchemaHash: "schema-hash",
                    ResourceKeyCount: checked((short)resourcesWithKeys.Length),
                    ResourceKeySeedHash: [1, 2, 3],
                    SchemaComponentsInEndpointOrder:
                    [
                        new SchemaComponentInfo("ed-fi", "Ed-Fi", "1.0.0", false, "component-hash"),
                    ],
                    ResourceKeysInIdOrder: resourceKeysInIdOrder
                ),
                Dialect: SqlDialect.Pgsql,
                ProjectSchemasInEndpointOrder:
                [
                    new ProjectSchemaInfo("ed-fi", "Ed-Fi", "1.0.0", false, _edfiSchema),
                ],
                ConcreteResourcesInNameOrder: resourcesWithKeys,
                AbstractIdentityTablesInNameOrder: [],
                AbstractUnionViewsInNameOrder: [],
                IndexesInCreateOrder: [],
                TriggersInCreateOrder: []
            ),
            WritePlansByResource: new Dictionary<QualifiedResourceName, ResourceWritePlan>(),
            ReadPlansByResource: new Dictionary<QualifiedResourceName, ResourceReadPlan>(),
            ResourceKeyIdByResource: resourceKeysInIdOrder.ToDictionary(
                static resourceKey => resourceKey.Resource,
                static resourceKey => resourceKey.ResourceKeyId
            ),
            ResourceKeyById: resourceKeysInIdOrder.ToDictionary(static resourceKey =>
                resourceKey.ResourceKeyId
            ),
            SecurableElementColumnPathsByResource: new Dictionary<
                QualifiedResourceName,
                IReadOnlyList<ResolvedSecurableElementPath>
            >()
        );
    }

    private static DbTableName Table(string name) => new(_edfiSchema, name);

    private static DbColumnName Col(string name) => new(name);

    private static JsonPathExpression Path(string canonical) => new(canonical, []);

    private static ResourceKeyEntry ResourceKey(string resource) =>
        new(1, new QualifiedResourceName("Ed-Fi", resource), "1.0", false);

    private static DbTableModel CreateRootTable(DbTableName table) =>
        new(
            table,
            Path("$"),
            new TableKey($"PK_{table.Name}", [new DbKeyColumn(_documentId, ColumnKind.ParentKeyPart)]),
            [],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.Root,
                [_documentId],
                [_documentId],
                [],
                []
            ),
        };

    private static DbTableModel CreateChildTable(DbTableName table, string jsonScope) =>
        new(
            table,
            Path(jsonScope),
            new TableKey($"PK_{table.Name}", [new DbKeyColumn(Col("CollectionItemId"), ColumnKind.Scalar)]),
            [],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.Collection,
                [Col("CollectionItemId")],
                [_documentId],
                [],
                []
            ),
        };

    private static RelationalResourceModel CreateModelWithTables(
        string resource,
        DbTableModel rootTable,
        IReadOnlyList<DbTableModel> childTables,
        IReadOnlyList<DocumentReferenceBinding> documentReferenceBindings
    ) =>
        new(
            new QualifiedResourceName("Ed-Fi", resource),
            _edfiSchema,
            ResourceStorageKind.RelationalTables,
            rootTable,
            [rootTable, .. childTables],
            documentReferenceBindings,
            []
        );

    private static RelationalResourceModel CreateModelWithTables(
        string resource,
        DbTableModel rootTable,
        IReadOnlyList<DocumentReferenceBinding> documentReferenceBindings
    ) =>
        new(
            new QualifiedResourceName("Ed-Fi", resource),
            _edfiSchema,
            ResourceStorageKind.RelationalTables,
            rootTable,
            [rootTable],
            documentReferenceBindings,
            []
        );

    private static ConcreteResourceModel CreateConcrete(
        string resource,
        RelationalResourceModel model,
        ResourceSecurableElements securableElements
    ) =>
        new(ResourceKey(resource), ResourceStorageKind.RelationalTables, model)
        {
            SecurableElements = securableElements,
        };

    private static string GetReferenceObjectPath(string jsonPath) =>
        jsonPath[..jsonPath.LastIndexOf(".", StringComparison.Ordinal)];

    private static string GetPersonReferenceJsonPath(SecurableElementKind securableElementKind) =>
        securableElementKind switch
        {
            SecurableElementKind.Student => "$.studentReference.studentUniqueId",
            SecurableElementKind.Contact => "$.contactReference.contactUniqueId",
            SecurableElementKind.Staff => "$.staffReference.staffUniqueId",
            _ => throw new ArgumentOutOfRangeException(
                nameof(securableElementKind),
                securableElementKind,
                "Unsupported people relationship authorization securable element kind."
            ),
        };

    private static string GetPersonSelfJsonPath(SecurableElementKind securableElementKind) =>
        securableElementKind switch
        {
            SecurableElementKind.Student => "$.studentUniqueId",
            SecurableElementKind.Contact => "$.contactUniqueId",
            SecurableElementKind.Staff => "$.staffUniqueId",
            _ => throw new ArgumentOutOfRangeException(
                nameof(securableElementKind),
                securableElementKind,
                "Unsupported people relationship authorization securable element kind."
            ),
        };

    private static string GetPersonResourceName(SecurableElementKind securableElementKind) =>
        securableElementKind switch
        {
            SecurableElementKind.Student => "Student",
            SecurableElementKind.Contact => "Contact",
            SecurableElementKind.Staff => "Staff",
            _ => throw new ArgumentOutOfRangeException(
                nameof(securableElementKind),
                securableElementKind,
                "Unsupported people relationship authorization securable element kind."
            ),
        };

    private static string GetPersonStrategyName(SecurableElementKind securableElementKind) =>
        securableElementKind is SecurableElementKind.Student
            ? AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly
            : AuthorizationStrategyNameConstants.RelationshipsWithPeopleOnly;

    private static RelationshipAuthorizationPersonAuthViewKind GetPersonAuthViewKind(
        SecurableElementKind securableElementKind
    ) =>
        securableElementKind switch
        {
            SecurableElementKind.Student => RelationshipAuthorizationPersonAuthViewKind.Student,
            SecurableElementKind.Contact => RelationshipAuthorizationPersonAuthViewKind.Contact,
            SecurableElementKind.Staff => RelationshipAuthorizationPersonAuthViewKind.Staff,
            _ => throw new ArgumentOutOfRangeException(
                nameof(securableElementKind),
                securableElementKind,
                "Unsupported people relationship authorization securable element kind."
            ),
        };

    private sealed record PersonReferenceSpec(
        SecurableElementKind Kind,
        string JsonPath,
        DbColumnName DocumentIdColumn
    );
}
