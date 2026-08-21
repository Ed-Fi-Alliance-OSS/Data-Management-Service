// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Postgresql;
using EdFi.DataManagementService.Tests.Integration.Scenarios;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Postgresql;

/// <summary>
/// Shared PostgreSQL binding for the cursor/partition authorization matrix: the authorization fixture,
/// the real authorization middleware, and the lowered page size the partition sizing depends on. Each
/// row's own class adds only the principal and the strategy under test.
/// </summary>
[Category("Authorization")]
public abstract class PostgresqlCursorPartitionAuthorizationMatrixTestBase : PostgresqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.AuthorizationQuery;

    /// <summary>
    /// The matrix reads through the production authorization path rather than around it, so the middleware
    /// that compiles the candidate relation's predicates has to run.
    /// </summary>
    protected override bool BypassAuthorization => false;

    protected override int? MaximumPageSizeOverride =>
        CursorPartitionAuthorizationMatrixScenario.HostMaximumPageSize;
}

/// <summary>
/// Nothing filters the candidate set, so the two surfaces must still agree on all of it. This is the row
/// that would catch a boundary calculation disagreeing with page selection for a reason that has nothing
/// to do with authorization.
/// </summary>
public sealed class Given_Postgresql_CursorPartitionAuthorizationMatrix_NoFurtherAuthorization
    : PostgresqlCursorPartitionAuthorizationMatrixTestBase
{
    [Test]
    public Task It_agrees_on_the_candidate_set_under_no_further_authorization() =>
        CursorPartitionAuthorizationMatrixScenario.It_agrees_on_the_candidate_set_under_no_further_authorization(
            Harness
        );
}

public sealed class Given_Postgresql_CursorPartitionAuthorizationMatrix_RelationshipAuthorization
    : PostgresqlCursorPartitionAuthorizationMatrixTestBase
{
    protected override IReadOnlyList<long> ClientEducationOrganizationIds =>
        [CursorPartitionAuthorizationMatrixSupport.ClaimEducationOrganizationId];

    protected override IClaimSetProvider CreateClaimSetProvider(FixtureContext fixture) =>
        CursorPartitionAuthorizationMatrixSupport.CreateRelationshipReadClaimSetProvider(fixture);

    [Test]
    public Task It_agrees_on_the_candidate_set_under_relationship_authorization() =>
        CursorPartitionAuthorizationMatrixScenario.It_agrees_on_the_candidate_set_under_relationship_authorization(
            Harness
        );
}

public sealed class Given_Postgresql_CursorPartitionAuthorizationMatrix_NamespaceAuthorization
    : PostgresqlCursorPartitionAuthorizationMatrixTestBase
{
    protected override IReadOnlyList<string> ClientNamespacePrefixes =>
        [CursorPartitionAuthorizationMatrixSupport.AuthorizedNamespacePrefix];

    protected override IClaimSetProvider CreateClaimSetProvider(FixtureContext fixture) =>
        CursorPartitionAuthorizationMatrixSupport.CreateNamespaceReadClaimSetProvider(fixture);

    [Test]
    public Task It_agrees_on_the_candidate_set_under_namespace_authorization() =>
        CursorPartitionAuthorizationMatrixScenario.It_agrees_on_the_candidate_set_under_namespace_authorization(
            Harness
        );
}

public sealed class Given_Postgresql_CursorPartitionAuthorizationMatrix_ViewBasedAuthorization
    : PostgresqlCursorPartitionAuthorizationMatrixTestBase
{
    protected override IClaimSetProvider CreateClaimSetProvider(FixtureContext fixture) =>
        CursorPartitionAuthorizationMatrixSupport.CreateCustomViewReadClaimSetProvider(fixture);

    [Test]
    public Task It_agrees_on_the_candidate_set_under_view_based_authorization() =>
        CursorPartitionAuthorizationMatrixScenario.It_agrees_on_the_candidate_set_under_view_based_authorization(
            Harness
        );
}

/// <summary>
/// Two education organization claims that both reach the authorized school, so the authorization relation
/// holds several matching rows per candidate and one row per document is a property of the plan rather
/// than of the data.
/// </summary>
public sealed class Given_Postgresql_CursorPartitionAuthorizationMatrix_SeveralMatchingAuthorizationRows
    : PostgresqlCursorPartitionAuthorizationMatrixTestBase
{
    protected override IReadOnlyList<long> ClientEducationOrganizationIds =>
        [
            CursorPartitionAuthorizationMatrixSupport.ClaimEducationOrganizationId,
            CursorPartitionAuthorizationMatrixSupport.SecondClaimEducationOrganizationId,
        ];

    protected override IClaimSetProvider CreateClaimSetProvider(FixtureContext fixture) =>
        CursorPartitionAuthorizationMatrixSupport.CreateRelationshipReadClaimSetProvider(fixture);

    [Test]
    public Task It_agrees_on_the_candidate_set_when_authorization_matches_several_rows() =>
        CursorPartitionAuthorizationMatrixScenario.It_agrees_on_the_candidate_set_when_authorization_matches_several_rows(
            Harness
        );
}

public sealed class Given_Postgresql_CursorPartitionAuthorizationMatrix_DescriptorNoFurtherAuthorization
    : PostgresqlCursorPartitionAuthorizationMatrixTestBase
{
    [Test]
    public Task It_agrees_on_the_descriptor_candidate_set_under_no_further_authorization() =>
        CursorPartitionAuthorizationMatrixScenario.It_agrees_on_the_descriptor_candidate_set_under_no_further_authorization(
            Harness
        );
}

public sealed class Given_Postgresql_CursorPartitionAuthorizationMatrix_DescriptorNamespaceAuthorization
    : PostgresqlCursorPartitionAuthorizationMatrixTestBase
{
    protected override IReadOnlyList<string> ClientNamespacePrefixes =>
        [CursorPartitionAuthorizationMatrixSupport.AuthorizedNamespacePrefix];

    protected override IClaimSetProvider CreateClaimSetProvider(FixtureContext fixture) =>
        CursorPartitionAuthorizationMatrixSupport.CreateDescriptorNamespaceReadClaimSetProvider(fixture);

    [Test]
    public Task It_agrees_on_the_descriptor_candidate_set_under_namespace_authorization() =>
        CursorPartitionAuthorizationMatrixScenario.It_agrees_on_the_descriptor_candidate_set_under_namespace_authorization(
            Harness
        );
}

/// <summary>
/// The caller holds an education organization claim that reaches nothing, because no hierarchy edge was
/// inserted for it.
/// </summary>
public sealed class Given_Postgresql_CursorPartitionAuthorizationMatrix_NoAccessibleCandidates
    : PostgresqlCursorPartitionAuthorizationMatrixTestBase
{
    protected override IReadOnlyList<long> ClientEducationOrganizationIds =>
        [CursorPartitionAuthorizationMatrixSupport.ClaimEducationOrganizationId];

    protected override IClaimSetProvider CreateClaimSetProvider(FixtureContext fixture) =>
        CursorPartitionAuthorizationMatrixSupport.CreateRelationshipReadClaimSetProvider(fixture);

    [Test]
    public Task It_returns_no_candidates_when_authorization_admits_none() =>
        CursorPartitionAuthorizationMatrixScenario.It_returns_no_candidates_when_authorization_admits_none(
            Harness
        );
}

/// <summary>
/// The caller holds a namespace prefix, so the request is authorized to proceed, but no seeded descriptor
/// starts with it. Holding no prefix at all would instead be refused before any candidate set exists.
/// </summary>
public sealed class Given_Postgresql_CursorPartitionAuthorizationMatrix_NoAccessibleDescriptorCandidates
    : PostgresqlCursorPartitionAuthorizationMatrixTestBase
{
    protected override IReadOnlyList<string> ClientNamespacePrefixes =>
        [CursorPartitionAuthorizationMatrixSupport.UnmatchedNamespacePrefix];

    protected override IClaimSetProvider CreateClaimSetProvider(FixtureContext fixture) =>
        CursorPartitionAuthorizationMatrixSupport.CreateDescriptorNamespaceReadClaimSetProvider(fixture);

    [Test]
    public Task It_returns_no_descriptor_candidates_when_authorization_admits_none() =>
        CursorPartitionAuthorizationMatrixScenario.It_returns_no_descriptor_candidates_when_authorization_admits_none(
            Harness
        );
}
