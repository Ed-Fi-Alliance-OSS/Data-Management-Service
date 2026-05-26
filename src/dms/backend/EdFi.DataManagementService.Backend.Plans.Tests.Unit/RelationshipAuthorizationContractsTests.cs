// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_RelationshipAuthorizationContracts
{
    private static readonly DbSchemaName _edfiSchema = new("edfi");
    private static readonly DbColumnName _documentIdColumn = new("DocumentId");

    [Test]
    public void It_preserves_edorg_hierarchy_auth_object_semantics()
    {
        var normal = RelationshipAuthorizationAuthObject.CreateEdOrgHierarchy(
            RelationshipAuthorizationHierarchyDirection.Normal
        );
        var inverted = RelationshipAuthorizationAuthObject.CreateEdOrgHierarchy(
            RelationshipAuthorizationHierarchyDirection.Inverted
        );

        normal.Name.Should().Be(AuthNames.EdOrgIdToEdOrgId);
        normal.SubjectValueColumn.Should().Be(AuthNames.TargetEdOrgId);
        normal.ClaimEducationOrganizationIdColumn.Should().Be(AuthNames.SourceEdOrgId);
        normal.AllowsDirectClaimMatch.Should().BeTrue();
        normal.FailureHint.Should().BeNull();

        inverted.Name.Should().Be(AuthNames.EdOrgIdToEdOrgId);
        inverted.SubjectValueColumn.Should().Be(AuthNames.SourceEdOrgId);
        inverted.ClaimEducationOrganizationIdColumn.Should().Be(AuthNames.TargetEdOrgId);
        inverted.AllowsDirectClaimMatch.Should().BeTrue();
        inverted.FailureHint.Should().BeNull();
    }

    [TestCase(
        RelationshipAuthorizationPersonAuthViewKind.Student,
        "EducationOrganizationIdToStudentDocumentId",
        "Student_DocumentId",
        "You may need to create a corresponding 'StudentSchoolAssociation' item."
    )]
    [TestCase(
        RelationshipAuthorizationPersonAuthViewKind.Contact,
        "EducationOrganizationIdToContactDocumentId",
        "Contact_DocumentId",
        "You may need to create corresponding 'StudentSchoolAssociation' and 'StudentContactAssociation' items."
    )]
    [TestCase(
        RelationshipAuthorizationPersonAuthViewKind.Staff,
        "EducationOrganizationIdToStaffDocumentId",
        "Staff_DocumentId",
        "You may need to create corresponding 'StaffEducationOrganizationEmploymentAssociation' or 'StaffEducationOrganizationAssignmentAssociation' items."
    )]
    [TestCase(
        RelationshipAuthorizationPersonAuthViewKind.StudentThroughResponsibility,
        "EducationOrganizationIdToStudentDocumentIdThroughResponsibility",
        "Student_DocumentId",
        "You may need to create a corresponding 'StudentEducationOrganizationResponsibilityAssociation' item."
    )]
    public void It_creates_people_auth_objects_with_document_id_output_columns_and_hints(
        RelationshipAuthorizationPersonAuthViewKind authViewKind,
        string expectedViewName,
        string expectedPersonDocumentIdColumnName,
        string expectedHint
    )
    {
        var authObject = RelationshipAuthorizationAuthObject.CreatePerson(authViewKind);

        authObject.Name.Should().Be(new DbTableName(AuthNames.AuthSchema, expectedViewName));
        authObject.SubjectValueColumn.Should().Be(new DbColumnName(expectedPersonDocumentIdColumnName));
        authObject.ClaimEducationOrganizationIdColumn.Should().Be(AuthNames.SourceEdOrgId);
        authObject.AllowsDirectClaimMatch.Should().BeFalse();
        authObject.FailureHint.Should().Be(expectedHint);
    }

    [Test]
    public void It_can_represent_direct_transitive_and_self_people_subjects()
    {
        var studentSchoolAssociationTable = Table("StudentSchoolAssociation");
        var courseTranscriptTable = Table("CourseTranscript");
        var studentAcademicRecordTable = Table("StudentAcademicRecord");
        var studentTable = Table("Student");
        var contactTable = Table("Contact");
        var staffTable = Table("Staff");

        var directStudentSubject = CreatePersonSubject(
            RelationshipAuthorizationPersonKind.Student,
            studentSchoolAssociationTable,
            new DbColumnName("Student_DocumentId"),
            RelationshipAuthorizationPersonSubjectPathKind.DirectRootColumn,
            [
                new ColumnPathStep(
                    studentSchoolAssociationTable,
                    new DbColumnName("Student_DocumentId"),
                    studentTable,
                    _documentIdColumn
                ),
            ],
            RelationshipAuthorizationAuthObject.CreatePerson(
                RelationshipAuthorizationPersonAuthViewKind.Student
            ),
            new RelationshipAuthorizationPersonProposedAnchor(
                RelationshipAuthorizationPersonProposedAnchorKind.RootRow,
                new RelationshipAuthorizationProposedValueBinding(
                    studentSchoolAssociationTable,
                    new DbColumnName("Student_DocumentId"),
                    3,
                    "Student_DocumentId",
                    "studentDocumentId"
                )
            )
        );

        var transitiveContactSubject = CreatePersonSubject(
            RelationshipAuthorizationPersonKind.Contact,
            courseTranscriptTable,
            new DbColumnName("StudentAcademicRecord_DocumentId"),
            RelationshipAuthorizationPersonSubjectPathKind.TransitiveJoinPath,
            [
                new ColumnPathStep(
                    courseTranscriptTable,
                    new DbColumnName("StudentAcademicRecord_DocumentId"),
                    studentAcademicRecordTable,
                    _documentIdColumn
                ),
                new ColumnPathStep(
                    studentAcademicRecordTable,
                    new DbColumnName("Contact_DocumentId"),
                    contactTable,
                    _documentIdColumn
                ),
            ],
            RelationshipAuthorizationAuthObject.CreatePerson(
                RelationshipAuthorizationPersonAuthViewKind.Contact
            ),
            new RelationshipAuthorizationPersonProposedAnchor(
                RelationshipAuthorizationPersonProposedAnchorKind.FirstHop,
                new RelationshipAuthorizationProposedValueBinding(
                    courseTranscriptTable,
                    new DbColumnName("StudentAcademicRecord_DocumentId"),
                    7,
                    "StudentAcademicRecord_DocumentId",
                    "studentAcademicRecordDocumentId"
                )
            )
        );

        var selfStaffSubject = CreatePersonSubject(
            RelationshipAuthorizationPersonKind.Staff,
            staffTable,
            _documentIdColumn,
            RelationshipAuthorizationPersonSubjectPathKind.SelfRootDocumentId,
            [],
            RelationshipAuthorizationAuthObject.CreatePerson(
                RelationshipAuthorizationPersonAuthViewKind.Staff
            ),
            null
        );

        directStudentSubject.IsPersonSubject.Should().BeTrue();
        directStudentSubject
            .PersonMetadata!.PersonKind.Should()
            .Be(RelationshipAuthorizationPersonKind.Student);
        directStudentSubject
            .PersonMetadata.Path.Kind.Should()
            .Be(RelationshipAuthorizationPersonSubjectPathKind.DirectRootColumn);
        directStudentSubject.PersonMetadata.Path.Steps.Should().ContainSingle();
        directStudentSubject
            .PersonMetadata.ProposedAnchor!.Kind.Should()
            .Be(RelationshipAuthorizationPersonProposedAnchorKind.RootRow);

        transitiveContactSubject
            .PersonMetadata!.PersonKind.Should()
            .Be(RelationshipAuthorizationPersonKind.Contact);
        transitiveContactSubject
            .PersonMetadata.Path.Kind.Should()
            .Be(RelationshipAuthorizationPersonSubjectPathKind.TransitiveJoinPath);
        transitiveContactSubject
            .PersonMetadata.Path.Steps.Select(static step => step.SourceTable)
            .Should()
            .Equal(courseTranscriptTable, studentAcademicRecordTable);
        transitiveContactSubject
            .PersonMetadata.ProposedAnchor!.Kind.Should()
            .Be(RelationshipAuthorizationPersonProposedAnchorKind.FirstHop);

        selfStaffSubject.PersonMetadata!.PersonKind.Should().Be(RelationshipAuthorizationPersonKind.Staff);
        selfStaffSubject
            .PersonMetadata.Path.Kind.Should()
            .Be(RelationshipAuthorizationPersonSubjectPathKind.SelfRootDocumentId);
        selfStaffSubject.PersonMetadata.Path.Steps.Should().BeEmpty();
        selfStaffSubject.PersonMetadata.StoredAnchor.RootTable.Should().Be(staffTable);
        selfStaffSubject.PersonMetadata.StoredAnchor.RootDocumentIdColumn.Should().Be(_documentIdColumn);
        selfStaffSubject.PersonMetadata.ProposedAnchor.Should().BeNull();
    }

    [Test]
    public void It_rejects_invalid_people_path_shapes()
    {
        var rootTable = Table("StudentSchoolAssociation");
        var step = new ColumnPathStep(
            rootTable,
            new DbColumnName("Student_DocumentId"),
            Table("Student"),
            _documentIdColumn
        );

        var directWithNoSteps = () =>
            new RelationshipAuthorizationPersonSubjectPath(
                RelationshipAuthorizationPersonSubjectPathKind.DirectRootColumn,
                []
            );
        var transitiveWithOneStep = () =>
            new RelationshipAuthorizationPersonSubjectPath(
                RelationshipAuthorizationPersonSubjectPathKind.TransitiveJoinPath,
                [step]
            );
        var selfWithStep = () =>
            new RelationshipAuthorizationPersonSubjectPath(
                RelationshipAuthorizationPersonSubjectPathKind.SelfRootDocumentId,
                [step]
            );

        directWithNoSteps.Should().Throw<ArgumentException>();
        transitiveWithOneStep.Should().Throw<ArgumentException>();
        selfWithStep.Should().Throw<ArgumentException>();
    }

    private static RelationshipAuthorizationSubject CreatePersonSubject(
        RelationshipAuthorizationPersonKind personKind,
        DbTableName table,
        DbColumnName column,
        RelationshipAuthorizationPersonSubjectPathKind pathKind,
        IReadOnlyList<ColumnPathStep> pathSteps,
        RelationshipAuthorizationAuthObject authObject,
        RelationshipAuthorizationPersonProposedAnchor? proposedAnchor
    )
    {
        var personMetadata = new RelationshipAuthorizationPersonSubjectMetadata(
            personKind,
            new RelationshipAuthorizationPersonSubjectPath(pathKind, pathSteps),
            authObject,
            new RelationshipAuthorizationPersonStoredAnchor(table, _documentIdColumn),
            proposedAnchor
        );

        return new RelationshipAuthorizationSubject(
            new QualifiedResourceName("Ed-Fi", table.Name),
            table,
            column,
            [
                new RelationshipAuthorizationSubjectContributor(
                    MapSecurableElementKind(personKind),
                    "$.personReference.personUniqueId",
                    $"{personKind}UniqueId"
                ),
            ],
            personMetadata
        );
    }

    private static SecurableElementKind MapSecurableElementKind(
        RelationshipAuthorizationPersonKind personKind
    ) =>
        personKind switch
        {
            RelationshipAuthorizationPersonKind.Student => SecurableElementKind.Student,
            RelationshipAuthorizationPersonKind.Contact => SecurableElementKind.Contact,
            RelationshipAuthorizationPersonKind.Staff => SecurableElementKind.Staff,
            _ => throw new ArgumentOutOfRangeException(
                nameof(personKind),
                personKind,
                "Unsupported relationship authorization person kind."
            ),
        };

    private static DbTableName Table(string name) => new(_edfiSchema, name);
}
