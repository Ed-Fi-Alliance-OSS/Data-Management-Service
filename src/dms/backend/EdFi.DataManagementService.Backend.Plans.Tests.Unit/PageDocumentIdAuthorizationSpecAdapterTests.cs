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
public class Given_PageDocumentIdAuthorizationSpecAdapter
{
    private static readonly DbSchemaName _schema = new("edfi");
    private static readonly DbTableName _rootTable = new(_schema, "School");
    private static readonly DbColumnName _documentIdColumn = new("DocumentId");
    private static readonly QualifiedResourceName _resource = new("Ed-Fi", "School");

    [Test]
    public void It_should_adapt_shared_stored_specs_into_page_query_strategies_without_losing_duplicate_identity()
    {
        var authorizationResult = new RelationshipAuthorizationResult.Authorized(
            [
                CreateCheckSpec(
                    4,
                    0,
                    RelationshipAuthorizationHierarchyDirection.Normal,
                    CreateSubject("LocalEducationAgencyId")
                ),
                CreateCheckSpec(
                    7,
                    1,
                    RelationshipAuthorizationHierarchyDirection.Normal,
                    CreateSubject("LocalEducationAgencyId")
                ),
            ],
            new AuthorizationClaimEducationOrganizationIdParameterization(
                AuthorizationClaimEducationOrganizationIdParameterizationKind.PgsqlArray,
                "ClaimEducationOrganizationIds",
                [100L, 200L],
                ["ClaimEducationOrganizationIds"]
            )
        );

        var authorizationSpec = PageDocumentIdAuthorizationSpecAdapter.Adapt(authorizationResult);

        authorizationSpec.Strategies.Should().HaveCount(2);
        authorizationSpec
            .Strategies.Select(static strategy => strategy.RawConfiguredIndex)
            .Should()
            .Equal(4, 7);
        authorizationSpec
            .Strategies.Select(static strategy => strategy.RelationshipLocalOrder)
            .Should()
            .Equal(0, 1);
        authorizationSpec
            .Strategies.Select(static strategy => strategy.Kind)
            .Should()
            .Equal(
                PageDocumentIdAuthorizationStrategyKind.RelationshipsWithEdOrgsOnly,
                PageDocumentIdAuthorizationStrategyKind.RelationshipsWithEdOrgsOnly
            );
        authorizationSpec
            .Strategies.Select(static strategy => strategy.Subjects)
            .Should()
            .OnlyContain(static subjects =>
                subjects.Count == 1
                && subjects[0].Table.Equals(_rootTable)
                && subjects[0].Column.Equals(new DbColumnName("LocalEducationAgencyId"))
            );
        authorizationSpec
            .Strategies.Select(static strategy => strategy.AllowsDirectClaimMatch)
            .Should()
            .OnlyContain(static allowsDirectClaimMatch => allowsDirectClaimMatch);
        authorizationSpec.ClaimEducationOrganizationIdParameterization.Should().NotBeNull();
        authorizationSpec
            .ClaimEducationOrganizationIdParameterization!.ClaimEducationOrganizationIds.Should()
            .Equal(100L, 200L);
    }

    [Test]
    public void It_should_map_inverted_shared_specs_to_inverted_page_query_strategies()
    {
        var authorizationResult = new RelationshipAuthorizationResult.Authorized(
            [
                CreateCheckSpec(
                    4,
                    0,
                    RelationshipAuthorizationHierarchyDirection.Inverted,
                    CreateSubject("SchoolId")
                ),
            ],
            new AuthorizationClaimEducationOrganizationIdParameterization(
                AuthorizationClaimEducationOrganizationIdParameterizationKind.MssqlScalar,
                "ClaimEducationOrganizationIds",
                [300L],
                ["ClaimEducationOrganizationIds_0"]
            )
        );

        var authorizationSpec = PageDocumentIdAuthorizationSpecAdapter.Adapt(authorizationResult);

        authorizationSpec.Strategies.Should().ContainSingle();
        authorizationSpec
            .Strategies[0]
            .Should()
            .BeEquivalentTo(
                new PageDocumentIdAuthorizationStrategy(
                    PageDocumentIdAuthorizationStrategyKind.RelationshipsWithEdOrgsOnlyInverted,
                    [new PageDocumentIdAuthorizationSubject(_rootTable, new DbColumnName("SchoolId"))],
                    4,
                    0,
                    AllowsDirectClaimMatch: true
                )
            );
    }

    [Test]
    public void It_should_reject_page_query_specs_with_multiple_distinct_edorg_auth_objects()
    {
        var checkSpec = CreateCheckSpec(
            4,
            0,
            RelationshipAuthorizationHierarchyDirection.Normal,
            CreateSubject("LocalEducationAgencyId"),
            CreateSubject("SchoolId")
        );
        checkSpec = checkSpec with
        {
            Subjects =
            [
                checkSpec.Subjects[0],
                checkSpec.Subjects[1] with
                {
                    AuthObject = RelationshipAuthorizationAuthObject.CreateEdOrgHierarchy(
                        RelationshipAuthorizationHierarchyDirection.Inverted
                    ),
                },
            ],
        };
        var authorizationResult = new RelationshipAuthorizationResult.Authorized(
            [checkSpec],
            new AuthorizationClaimEducationOrganizationIdParameterization(
                AuthorizationClaimEducationOrganizationIdParameterizationKind.PgsqlArray,
                "ClaimEducationOrganizationIds",
                [100L],
                ["ClaimEducationOrganizationIds"]
            )
        );

        var adapt = () => PageDocumentIdAuthorizationSpecAdapter.Adapt(authorizationResult);

        adapt
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("PageDocumentId authorization requires exactly one EdOrg auth object.");
    }

    [Test]
    public void It_should_reject_page_query_specs_with_non_edorg_subject_auth_objects()
    {
        var checkSpec = CreateCheckSpec(
            4,
            0,
            RelationshipAuthorizationHierarchyDirection.Normal,
            CreateSubject("Student_DocumentId")
        );
        checkSpec = checkSpec with
        {
            Subjects =
            [
                checkSpec.Subjects[0] with
                {
                    AuthObject = RelationshipAuthorizationAuthObject.CreatePerson(
                        RelationshipAuthorizationPersonAuthViewKind.Student
                    ),
                },
            ],
        };
        var authorizationResult = new RelationshipAuthorizationResult.Authorized(
            [checkSpec],
            new AuthorizationClaimEducationOrganizationIdParameterization(
                AuthorizationClaimEducationOrganizationIdParameterizationKind.PgsqlArray,
                "ClaimEducationOrganizationIds",
                [100L],
                ["ClaimEducationOrganizationIds"]
            )
        );

        var adapt = () => PageDocumentIdAuthorizationSpecAdapter.Adapt(authorizationResult);

        adapt
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "PageDocumentId authorization supports only EdOrg hierarchy relationship checks. Auth object 'auth.EducationOrganizationIdToStudentDocumentId' is not supported."
            );
    }

    [Test]
    public void It_should_reject_people_relationship_specs_until_get_many_integration_consumes_people_core()
    {
        var authorizationResult = new RelationshipAuthorizationResult.Authorized(
            [
                new RelationshipAuthorizationCheckSpec(
                    new ConfiguredAuthorizationStrategy(
                        AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly,
                        RawConfiguredIndex: 0
                    ),
                    RelationshipLocalOrder: 0,
                    RelationshipAuthorizationHierarchyDirection.Normal,
                    RelationshipAuthorizationValueSource.Stored,
                    [CreatePersonSubject()],
                    new RelationshipAuthorizationCheckTarget.Stored(_rootTable, _documentIdColumn)
                ),
            ],
            new AuthorizationClaimEducationOrganizationIdParameterization(
                AuthorizationClaimEducationOrganizationIdParameterizationKind.PgsqlArray,
                "ClaimEducationOrganizationIds",
                [100L],
                ["ClaimEducationOrganizationIds"]
            )
        );

        var adapt = () => PageDocumentIdAuthorizationSpecAdapter.Adapt(authorizationResult);

        adapt
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*RelationshipsWithStudentsOnly*GET-many People relationship execution*");
    }

    [Test]
    public void It_should_keep_people_relationship_specs_staged_for_single_record_sql_execution()
    {
        var checkSpec = new RelationshipAuthorizationCheckSpec(
            new ConfiguredAuthorizationStrategy(
                AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly,
                RawConfiguredIndex: 0
            ),
            RelationshipLocalOrder: 0,
            RelationshipAuthorizationHierarchyDirection.Normal,
            RelationshipAuthorizationValueSource.Stored,
            [CreatePersonSubject()],
            new RelationshipAuthorizationCheckTarget.Stored(_rootTable, _documentIdColumn)
        );

        var enforceBoundary = () =>
            RelationshipAuthorizationEndpointExecutionBoundary.ThrowIfUnsupportedForSingleRecordSql([
                checkSpec,
            ]);

        enforceBoundary
            .Should()
            .Throw<ArgumentException>()
            .WithMessage("*RelationshipsWithStudentsOnly*People relationship CRUD execution*");
    }

    private static RelationshipAuthorizationCheckSpec CreateCheckSpec(
        int rawConfiguredIndex,
        int relationshipLocalOrder,
        RelationshipAuthorizationHierarchyDirection direction,
        params RelationshipAuthorizationSubject[] subjects
    ) =>
        new(
            new ConfiguredAuthorizationStrategy(
                StrategyName: "RelationshipsWithEdOrgsOnly",
                RawConfiguredIndex: rawConfiguredIndex
            ),
            relationshipLocalOrder,
            direction,
            RelationshipAuthorizationValueSource.Stored,
            [
                .. subjects.Select(subject =>
                    subject with
                    {
                        AuthObject = RelationshipAuthorizationAuthObject.CreateEdOrgHierarchy(direction),
                    }
                ),
            ],
            new RelationshipAuthorizationCheckTarget.Stored(_rootTable, _documentIdColumn)
        );

    private static RelationshipAuthorizationSubject CreateSubject(string columnName) =>
        new(
            _resource,
            _rootTable,
            new DbColumnName(columnName),
            RelationshipAuthorizationAuthObject.CreateEdOrgHierarchy(
                RelationshipAuthorizationHierarchyDirection.Normal
            ),
            [
                new RelationshipAuthorizationSubjectContributor(
                    SecurableElementKind.EducationOrganization,
                    $"$.{columnName}",
                    columnName
                ),
            ]
        );

    private static RelationshipAuthorizationSubject CreatePersonSubject() =>
        new(
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
        );
}
