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
                    0
                )
            );
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
                RawConfiguredIndex: rawConfiguredIndex,
                Composition: RelationshipAuthorizationStrategyComposition.And
            ),
            relationshipLocalOrder,
            direction,
            RelationshipAuthorizationValueSource.Stored,
            RelationshipAuthorizationAuthObject.CreateEdOrgHierarchy(direction),
            subjects,
            new RelationshipAuthorizationCheckTarget.Stored(_rootTable, _documentIdColumn)
        );

    private static RelationshipAuthorizationSubject CreateSubject(string columnName) =>
        new(
            _resource,
            _rootTable,
            new DbColumnName(columnName),
            [
                new RelationshipAuthorizationSubjectContributor(
                    SecurableElementKind.EducationOrganization,
                    $"$.{columnName}",
                    columnName
                ),
            ]
        );
}
