// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Security;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_RelationshipAuthorizationEndpointExecutionBoundary
{
    private static readonly DbTableName _rootTable = new(
        new DbSchemaName("edfi"),
        "StudentSchoolAssociation"
    );
    private static readonly DbColumnName _documentIdColumn = new("DocumentId");
    private static readonly QualifiedResourceName _resource = new("Ed-Fi", "StudentSchoolAssociation");

    [Test]
    public void It_should_allow_people_relationship_specs_for_page_document_id_execution()
    {
        var checkSpec = CreatePeopleCheckSpec();

        var enforceBoundary = () =>
            RelationshipAuthorizationEndpointExecutionBoundary.ThrowIfUnsupportedForPageDocumentId(checkSpec);

        enforceBoundary.Should().NotThrow();
    }

    [Test]
    public void It_should_allow_people_relationship_specs_for_single_record_sql_execution()
    {
        var checkSpec = CreatePeopleCheckSpec();

        var enforceBoundary = () =>
            RelationshipAuthorizationEndpointExecutionBoundary.ThrowIfUnsupportedForSingleRecordSql([
                checkSpec,
            ]);

        enforceBoundary.Should().NotThrow();
    }

    [Test]
    public void It_should_reject_single_record_people_specs_with_invalid_people_auth_objects()
    {
        var checkSpec = CreatePeopleCheckSpec() with
        {
            Subjects =
            [
                CreatePeopleCheckSpec().Subjects[0] with
                {
                    AuthObject = RelationshipAuthorizationAuthObject.CreateEdOrgHierarchy(
                        RelationshipAuthorizationHierarchyDirection.Normal
                    ),
                },
            ],
        };

        var enforceBoundary = () =>
            RelationshipAuthorizationEndpointExecutionBoundary.ThrowIfUnsupportedForSingleRecordSql([
                checkSpec,
            ]);

        enforceBoundary
            .Should()
            .Throw<ArgumentException>()
            .WithMessage(
                "*Auth object 'auth.EducationOrganizationIdToEducationOrganizationId' is not supported*"
            );
    }

    [Test]
    public void It_should_reject_single_record_people_specs_with_non_person_contributors()
    {
        var checkSpec = CreatePeopleCheckSpec() with
        {
            Subjects =
            [
                CreatePeopleCheckSpec().Subjects[0] with
                {
                    Contributors =
                    [
                        new RelationshipAuthorizationSubjectContributor(
                            SecurableElementKind.EducationOrganization,
                            "$.schoolReference.schoolId",
                            "SchoolId"
                        ),
                    ],
                },
            ],
        };

        var enforceBoundary = () =>
            RelationshipAuthorizationEndpointExecutionBoundary.ThrowIfUnsupportedForSingleRecordSql([
                checkSpec,
            ]);

        enforceBoundary
            .Should()
            .Throw<ArgumentException>()
            .WithMessage("*Subject column 'Student_DocumentId' is not a People subject*");
    }

    private static RelationshipAuthorizationCheckSpec CreatePeopleCheckSpec() =>
        new(
            new ConfiguredAuthorizationStrategy(
                AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly,
                RawConfiguredIndex: 0
            ),
            RelationshipLocalOrder: 0,
            RelationshipAuthorizationHierarchyDirection.Normal,
            RelationshipAuthorizationValueSource.Stored,
            [
                new RelationshipAuthorizationSubject(
                    _resource,
                    _rootTable,
                    AuthNames.StudentDocumentId,
                    RelationshipAuthorizationAuthObject.CreatePerson(
                        RelationshipAuthorizationPersonAuthViewKind.Student
                    ),
                    [
                        new RelationshipAuthorizationSubjectContributor(
                            SecurableElementKind.Student,
                            "$.studentReference.studentUniqueId",
                            "StudentUniqueId"
                        ),
                    ],
                    new RelationshipAuthorizationPersonSubjectMetadata(
                        RelationshipAuthorizationPersonKind.Student,
                        new RelationshipAuthorizationPersonSubjectPath(
                            RelationshipAuthorizationPersonSubjectPathKind.DirectRootColumn,
                            [new ColumnPathStep(_rootTable, AuthNames.StudentDocumentId, null, null)]
                        ),
                        new RelationshipAuthorizationPersonStoredAnchor(_rootTable, _documentIdColumn),
                        ProposedAnchor: null
                    )
                ),
            ],
            new RelationshipAuthorizationCheckTarget.Stored(_rootTable, _documentIdColumn)
        );
}
