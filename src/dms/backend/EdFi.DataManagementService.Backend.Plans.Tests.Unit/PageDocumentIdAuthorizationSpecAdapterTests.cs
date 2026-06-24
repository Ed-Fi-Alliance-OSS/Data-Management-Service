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
    public void It_should_adapt_shared_stored_specs_into_page_query_strategies_preserving_effective_order()
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
            .Strategies.Select(static strategy => strategy.StrategyName)
            .Should()
            .Equal(
                AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
                AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
            );
        authorizationSpec
            .Strategies.Select(static strategy => strategy.Subjects)
            .Should()
            .OnlyContain(static subjects =>
                subjects.Count == 1
                && subjects[0] is PageDocumentIdAuthorizationEdOrgSubject
                && subjects[0].Table.Equals(_rootTable)
                && subjects[0].Column.Equals(new DbColumnName("LocalEducationAgencyId"))
            );
        authorizationSpec
            .Strategies.SelectMany(static strategy => strategy.Subjects)
            .Select(static subject => subject.AuthObject.AllowsDirectClaimMatch)
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
        var strategy = authorizationSpec.Strategies[0];
        strategy
            .StrategyName.Should()
            .Be(AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnlyInverted);
        strategy.Subjects.Should().ContainSingle();

        var subject = strategy
            .Subjects[0]
            .Should()
            .BeOfType<PageDocumentIdAuthorizationEdOrgSubject>()
            .Subject;
        subject.Table.Should().Be(_rootTable);
        subject.Column.Should().Be(new DbColumnName("SchoolId"));
        subject
            .AuthObject.Should()
            .Be(
                RelationshipAuthorizationAuthObject.CreateEdOrgHierarchy(
                    RelationshipAuthorizationHierarchyDirection.Inverted
                )
            );
    }

    [Test]
    public void It_should_preserve_ordered_subject_auth_object_metadata()
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

        var authorizationSpec = PageDocumentIdAuthorizationSpecAdapter.Adapt(authorizationResult);

        authorizationSpec.Strategies.Should().ContainSingle();
        authorizationSpec
            .Strategies[0]
            .Subjects.Select(static subject => subject.AuthObject)
            .Should()
            .Equal(
                RelationshipAuthorizationAuthObject.CreateEdOrgHierarchy(
                    RelationshipAuthorizationHierarchyDirection.Normal
                ),
                RelationshipAuthorizationAuthObject.CreateEdOrgHierarchy(
                    RelationshipAuthorizationHierarchyDirection.Inverted
                )
            );
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
                "PageDocumentId authorization supports only EdOrg hierarchy or People relationship checks. Auth object 'auth.EducationOrganizationIdToStudentDocumentId' is not supported."
            );
    }

    [Test]
    public void It_should_reject_authorized_results_without_claim_education_organization_parameterization()
    {
        var authorizationResult = new RelationshipAuthorizationResult.Authorized([
            CreateCheckSpec(
                4,
                0,
                RelationshipAuthorizationHierarchyDirection.Normal,
                CreateSubject("SchoolId")
            ),
        ]);

        var adapt = () => PageDocumentIdAuthorizationSpecAdapter.Adapt(authorizationResult);

        adapt
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "PageDocumentId authorization requires claim EducationOrganization parameterization."
            );
    }

    [Test]
    public void It_should_reject_edorg_subjects_that_do_not_match_the_query_root_table()
    {
        var alternateTable = new DbTableName(_schema, "LocalEducationAgency");
        var authorizationResult = new RelationshipAuthorizationResult.Authorized(
            [
                CreateCheckSpec(
                    4,
                    0,
                    RelationshipAuthorizationHierarchyDirection.Normal,
                    CreateSubject("LocalEducationAgencyId") with
                    {
                        Table = alternateTable,
                    }
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
            .WithMessage(
                $"Authorization subject table '{alternateTable}' does not match query root table '{_rootTable}'. PageDocumentId authorization supports only concrete root-table subjects."
            );
    }

    [Test]
    public void It_should_reject_people_subjects_that_do_not_match_the_query_root_table()
    {
        var alternateTable = new DbTableName(_schema, "Student");
        var personSubject = CreatePersonSubject();
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
                    [
                        personSubject with
                        {
                            PersonMetadata = personSubject.PersonMetadata! with
                            {
                                StoredAnchor = new RelationshipAuthorizationPersonStoredAnchor(
                                    alternateTable,
                                    _documentIdColumn
                                ),
                            },
                        },
                    ],
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
            .WithMessage(
                $"People authorization subject root table '{alternateTable}' does not match query root table '{_rootTable}'."
            );
    }

    [TestCaseSource(nameof(PeopleRelationshipStrategyCases))]
    public void It_should_adapt_stored_people_relationship_specs_for_get_many(
        string strategyName,
        RelationshipAuthorizationHierarchyDirection direction,
        RelationshipAuthorizationPersonAuthViewKind authViewKind,
        RelationshipAuthorizationPersonKind personKind,
        DbColumnName expectedOutputColumn
    )
    {
        var authorizationResult = new RelationshipAuthorizationResult.Authorized(
            [
                new RelationshipAuthorizationCheckSpec(
                    new ConfiguredAuthorizationStrategy(strategyName, RawConfiguredIndex: 12),
                    RelationshipLocalOrder: 3,
                    direction,
                    RelationshipAuthorizationValueSource.Stored,
                    [CreatePersonSubject(authViewKind, personKind, expectedOutputColumn)],
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

        var authorizationSpec = PageDocumentIdAuthorizationSpecAdapter.Adapt(authorizationResult);

        authorizationSpec.Strategies.Should().ContainSingle();
        var strategy = authorizationSpec.Strategies[0];
        strategy.StrategyName.Should().Be(strategyName);

        var subject = strategy
            .Subjects[0]
            .Should()
            .BeOfType<PageDocumentIdAuthorizationPersonSubject>()
            .Subject;
        subject.Table.Should().Be(_rootTable);
        subject.Column.Should().Be(expectedOutputColumn);
        subject
            .AuthObject.Name.Should()
            .Be(RelationshipAuthorizationAuthObject.CreatePerson(authViewKind).Name);
        subject.AuthObject.SubjectValueColumn.Should().Be(expectedOutputColumn);
        subject.AuthObject.ClaimEducationOrganizationIdColumn.Should().Be(AuthNames.SourceEdOrgId);
        subject.PersonMetadata.PersonKind.Should().Be(personKind);
        subject.PersonMetadata.StoredAnchor.RootTable.Should().Be(_rootTable);
        subject.PersonMetadata.StoredAnchor.RootDocumentIdColumn.Should().Be(_documentIdColumn);
        subject
            .PersonMetadata.Path.Kind.Should()
            .Be(RelationshipAuthorizationPersonSubjectPathKind.DirectRootColumn);
    }

    [Test]
    public void It_should_adapt_edorg_and_people_subjects_in_the_same_strategy_without_carrying_planner_diagnostics()
    {
        var skippedContributor = new RelationshipAuthorizationSkippedSubjectContributor(
            SecurableElementKind.Student,
            "$.students[*].studentReference.studentUniqueId",
            "StudentUniqueId",
            ContributionOrder: 5,
            Reason: RelationshipAuthorizationSkippedSubjectReason.ChildCollectionPersonPathOutsideSubjectScope,
            PersonKind: RelationshipAuthorizationPersonKind.Student,
            AuthObject: RelationshipAuthorizationAuthObject.CreatePerson(
                RelationshipAuthorizationPersonAuthViewKind.Student
            ),
            Table: _rootTable,
            Column: AuthNames.StudentDocumentId
        );
        var checkSpec = new RelationshipAuthorizationCheckSpec(
            new ConfiguredAuthorizationStrategy(
                AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsAndPeopleInverted,
                RawConfiguredIndex: 9
            ),
            RelationshipLocalOrder: 2,
            RelationshipAuthorizationHierarchyDirection.Inverted,
            RelationshipAuthorizationValueSource.Stored,
            [
                CreateSubject("SchoolId") with
                {
                    AuthObject = RelationshipAuthorizationAuthObject.CreateEdOrgHierarchy(
                        RelationshipAuthorizationHierarchyDirection.Inverted
                    ),
                },
                CreatePersonSubject(
                    RelationshipAuthorizationPersonAuthViewKind.Student,
                    RelationshipAuthorizationPersonKind.Student,
                    AuthNames.StudentDocumentId
                ),
            ],
            new RelationshipAuthorizationCheckTarget.Stored(_rootTable, _documentIdColumn)
        )
        {
            SkippedContributors = [skippedContributor],
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

        var authorizationSpec = PageDocumentIdAuthorizationSpecAdapter.Adapt(authorizationResult);

        authorizationSpec.Strategies.Should().ContainSingle();
        var strategy = authorizationSpec.Strategies[0];
        strategy
            .StrategyName.Should()
            .Be(AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsAndPeopleInverted);
        strategy
            .Subjects.Select(static subject => subject.GetType())
            .Should()
            .Equal(
                typeof(PageDocumentIdAuthorizationEdOrgSubject),
                typeof(PageDocumentIdAuthorizationPersonSubject)
            );
        strategy.Subjects[0].AuthObject.SubjectValueColumn.Should().Be(AuthNames.SourceEdOrgId);
        strategy
            .Subjects[1]
            .AuthObject.Name.Should()
            .Be(
                RelationshipAuthorizationAuthObject
                    .CreatePerson(RelationshipAuthorizationPersonAuthViewKind.Student)
                    .Name
            );
    }

    [Test]
    public void It_should_allow_people_relationship_specs_for_single_record_sql_execution()
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

        enforceBoundary.Should().NotThrow();
    }

    private static IEnumerable<TestCaseData> PeopleRelationshipStrategyCases()
    {
        yield return new TestCaseData(
            AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsAndPeople,
            RelationshipAuthorizationHierarchyDirection.Normal,
            RelationshipAuthorizationPersonAuthViewKind.Student,
            RelationshipAuthorizationPersonKind.Student,
            AuthNames.StudentDocumentId
        ).SetName("RelationshipsWithEdOrgsAndPeople");

        yield return new TestCaseData(
            AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsAndPeopleInverted,
            RelationshipAuthorizationHierarchyDirection.Inverted,
            RelationshipAuthorizationPersonAuthViewKind.Staff,
            RelationshipAuthorizationPersonKind.Staff,
            AuthNames.StaffDocumentId
        ).SetName("RelationshipsWithEdOrgsAndPeopleInverted");

        yield return new TestCaseData(
            AuthorizationStrategyNameConstants.RelationshipsWithPeopleOnly,
            RelationshipAuthorizationHierarchyDirection.Normal,
            RelationshipAuthorizationPersonAuthViewKind.Contact,
            RelationshipAuthorizationPersonKind.Contact,
            AuthNames.ContactDocumentId
        ).SetName("RelationshipsWithPeopleOnly");

        yield return new TestCaseData(
            AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly,
            RelationshipAuthorizationHierarchyDirection.Normal,
            RelationshipAuthorizationPersonAuthViewKind.Student,
            RelationshipAuthorizationPersonKind.Student,
            AuthNames.StudentDocumentId
        ).SetName("RelationshipsWithStudentsOnly");

        yield return new TestCaseData(
            AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnlyThroughResponsibility,
            RelationshipAuthorizationHierarchyDirection.Normal,
            RelationshipAuthorizationPersonAuthViewKind.StudentThroughResponsibility,
            RelationshipAuthorizationPersonKind.Student,
            AuthNames.StudentDocumentId
        ).SetName("RelationshipsWithStudentsOnlyThroughResponsibility");
    }

    private static RelationshipAuthorizationCheckSpec CreateCheckSpec(
        int rawConfiguredIndex,
        int relationshipLocalOrder,
        RelationshipAuthorizationHierarchyDirection direction,
        params RelationshipAuthorizationSubject[] subjects
    ) =>
        new(
            new ConfiguredAuthorizationStrategy(
                StrategyName: direction is RelationshipAuthorizationHierarchyDirection.Inverted
                    ? AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnlyInverted
                    : AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
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
        CreatePersonSubject(
            RelationshipAuthorizationPersonAuthViewKind.Student,
            RelationshipAuthorizationPersonKind.Student,
            AuthNames.StudentDocumentId
        );

    private static RelationshipAuthorizationSubject CreatePersonSubject(
        RelationshipAuthorizationPersonAuthViewKind authViewKind,
        RelationshipAuthorizationPersonKind personKind,
        DbColumnName personDocumentIdColumn
    ) =>
        new(
            _resource,
            _rootTable,
            personDocumentIdColumn,
            RelationshipAuthorizationAuthObject.CreatePerson(authViewKind),
            [
                new RelationshipAuthorizationSubjectContributor(
                    MapPersonKind(personKind),
                    $"$.{personKind.ToString().ToLowerInvariant()}Reference.{personKind.ToString().ToLowerInvariant()}UniqueId",
                    $"{personKind}UniqueId"
                ),
            ],
            new RelationshipAuthorizationPersonSubjectMetadata(
                personKind,
                new RelationshipAuthorizationPersonSubjectPath(
                    RelationshipAuthorizationPersonSubjectPathKind.DirectRootColumn,
                    [new ColumnPathStep(_rootTable, personDocumentIdColumn, null, null)]
                ),
                new RelationshipAuthorizationPersonStoredAnchor(_rootTable, _documentIdColumn),
                ProposedAnchor: null
            )
        );

    private static SecurableElementKind MapPersonKind(RelationshipAuthorizationPersonKind personKind) =>
        personKind switch
        {
            RelationshipAuthorizationPersonKind.Student => SecurableElementKind.Student,
            RelationshipAuthorizationPersonKind.Contact => SecurableElementKind.Contact,
            RelationshipAuthorizationPersonKind.Staff => SecurableElementKind.Staff,
            _ => throw new ArgumentOutOfRangeException(
                nameof(personKind),
                personKind,
                "Unsupported person kind."
            ),
        };
}
