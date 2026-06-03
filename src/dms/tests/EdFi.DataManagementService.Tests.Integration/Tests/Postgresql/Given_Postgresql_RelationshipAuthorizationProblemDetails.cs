// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Postgresql;
using EdFi.DataManagementService.Tests.Integration.Scenarios;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Postgresql;

public sealed class Given_Postgresql_RelationshipAuthorizationProblemDetails_For_Read_Delete_And_Update
    : PostgresqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.AuthorizationQuery;

    protected override bool BypassAuthorization => false;

    protected override IReadOnlyList<long> ClientEducationOrganizationIds =>
        [RelationshipAuthorizationProblemDetailsScenario.ClaimEducationOrganizationId];

    protected override IClaimSetProvider CreateClaimSetProvider(FixtureContext fixture) =>
        RelationshipAuthorizationProblemDetailsScenario.CreateReadDeleteUpdateClaimSetProvider(fixture);

    [Test]
    public Task It_returns_relationship_problem_details_for_unauthorized_get_by_id() =>
        RelationshipAuthorizationProblemDetailsScenario.It_returns_relationship_problem_details_for_unauthorized_get_by_id(
            Harness
        );

    [Test]
    public Task It_returns_relationship_problem_details_for_unauthorized_delete() =>
        RelationshipAuthorizationProblemDetailsScenario.It_returns_relationship_problem_details_for_unauthorized_delete(
            Harness
        );

    [Test]
    public Task It_returns_existing_data_problem_details_for_stored_value_put() =>
        RelationshipAuthorizationProblemDetailsScenario.It_returns_existing_data_problem_details_for_stored_value_put(
            Harness
        );

    [Test]
    public Task It_returns_proposed_data_problem_details_for_proposed_value_put() =>
        RelationshipAuthorizationProblemDetailsScenario.It_returns_proposed_data_problem_details_for_proposed_value_put(
            Harness
        );
}

public sealed class Given_Postgresql_RelationshipAuthorizationProblemDetails_For_Create
    : PostgresqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.AuthorizationQuery;

    protected override bool BypassAuthorization => false;

    protected override IReadOnlyList<long> ClientEducationOrganizationIds =>
        [RelationshipAuthorizationProblemDetailsScenario.ClaimEducationOrganizationId];

    protected override IClaimSetProvider CreateClaimSetProvider(FixtureContext fixture) =>
        RelationshipAuthorizationProblemDetailsScenario.CreateCreateClaimSetProvider(fixture);

    [Test]
    public Task It_returns_relationship_problem_details_for_unauthorized_post_create() =>
        RelationshipAuthorizationProblemDetailsScenario.It_returns_relationship_problem_details_for_unauthorized_post_create(
            Harness
        );

    [Test]
    public Task It_returns_proposed_data_problem_details_for_missing_create_securable_element() =>
        RelationshipAuthorizationProblemDetailsScenario.It_returns_proposed_data_problem_details_for_missing_create_securable_element(
            Harness
        );
}

public sealed class Given_Postgresql_RelationshipAuthorizationProblemDetails_For_Empty_EdOrg_Claims
    : PostgresqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.AuthorizationQuery;

    protected override bool BypassAuthorization => false;

    protected override IReadOnlyList<long> ClientEducationOrganizationIds => [];

    protected override IClaimSetProvider CreateClaimSetProvider(FixtureContext fixture) =>
        RelationshipAuthorizationProblemDetailsScenario.CreateReadDeleteUpdateClaimSetProvider(fixture);

    [Test]
    public Task It_renders_empty_edorg_claims_as_none() =>
        RelationshipAuthorizationProblemDetailsScenario.It_renders_empty_edorg_claims_as_none(Harness);
}

public sealed class Given_Postgresql_PeopleRelationshipAuthorizationProblemDetails_For_Read_Update_And_Delete
    : PostgresqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.AuthorizationQuery;

    protected override bool BypassAuthorization => false;

    protected override IReadOnlyList<long> ClientEducationOrganizationIds =>
        [PeopleRelationshipCrudProblemDetailsScenario.ClaimEducationOrganizationId];

    protected override IClaimSetProvider CreateClaimSetProvider(FixtureContext fixture) =>
        PeopleRelationshipCrudProblemDetailsScenario.CreateReadUpdateDeleteStudentsOnlyClaimSetProvider(
            fixture
        );

    [Test]
    public Task It_returns_people_problem_details_for_unauthorized_get_by_id() =>
        PeopleRelationshipCrudProblemDetailsScenario.It_returns_people_problem_details_for_unauthorized_get_by_id(
            Harness
        );

    [Test]
    public Task It_returns_people_stored_data_problem_details_before_put_proposed_values() =>
        PeopleRelationshipCrudProblemDetailsScenario.It_returns_people_stored_data_problem_details_before_put_proposed_values(
            Harness
        );
}

public sealed class Given_Postgresql_PeopleRelationshipAuthorizationProblemDetails_For_Create
    : PostgresqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.AuthorizationQuery;

    protected override bool BypassAuthorization => false;

    protected override IReadOnlyList<long> ClientEducationOrganizationIds =>
        [PeopleRelationshipCrudProblemDetailsScenario.ClaimEducationOrganizationId];

    protected override IClaimSetProvider CreateClaimSetProvider(FixtureContext fixture) =>
        PeopleRelationshipCrudProblemDetailsScenario.CreateCreateStudentsOnlyClaimSetProvider(fixture);

    [Test]
    public Task It_returns_people_problem_details_for_unauthorized_post_create() =>
        PeopleRelationshipCrudProblemDetailsScenario.It_returns_people_problem_details_for_unauthorized_post_create(
            Harness
        );

    [Test]
    public Task It_returns_people_proposed_data_problem_details_for_missing_post_create_student() =>
        PeopleRelationshipCrudProblemDetailsScenario.It_returns_people_proposed_data_problem_details_for_missing_post_create_student(
            Harness
        );
}

public sealed class Given_Postgresql_PeopleRelationshipAuthorizationProblemDetails_For_Empty_EdOrg_Claims
    : PostgresqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.AuthorizationQuery;

    protected override bool BypassAuthorization => false;

    protected override IReadOnlyList<long> ClientEducationOrganizationIds => [];

    protected override IClaimSetProvider CreateClaimSetProvider(FixtureContext fixture) =>
        PeopleRelationshipCrudProblemDetailsScenario.CreateReadUpdateDeleteStudentsOnlyClaimSetProvider(
            fixture
        );

    [Test]
    public Task It_returns_people_problem_details_for_unauthorized_delete_with_no_claims() =>
        PeopleRelationshipCrudProblemDetailsScenario.It_returns_people_problem_details_for_unauthorized_delete_with_no_claims(
            Harness
        );
}

public sealed class Given_Postgresql_PeopleRelationshipAuthorizationProblemDetails_For_Mixed_EdOrg_And_People
    : PostgresqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.AuthorizationQuery;

    protected override bool BypassAuthorization => false;

    protected override IReadOnlyList<long> ClientEducationOrganizationIds =>
        [PeopleRelationshipCrudProblemDetailsScenario.ClaimEducationOrganizationId];

    protected override IClaimSetProvider CreateClaimSetProvider(FixtureContext fixture) =>
        PeopleRelationshipCrudProblemDetailsScenario.CreateReadEdOrgAndPeopleClaimSetProvider(fixture);

    [Test]
    public Task It_renders_plural_securable_names_for_mixed_people_no_relationship() =>
        PeopleRelationshipCrudProblemDetailsScenario.It_renders_plural_securable_names_for_mixed_people_no_relationship(
            Harness
        );
}

public sealed class Given_Postgresql_PeopleRelationshipAuthorizationProblemDetails_For_Mixed_Put_Precedence
    : PostgresqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.AuthorizationQuery;

    protected override bool BypassAuthorization => false;

    protected override IReadOnlyList<long> ClientEducationOrganizationIds =>
        [PeopleRelationshipCrudProblemDetailsScenario.ClaimEducationOrganizationId];

    protected override IClaimSetProvider CreateClaimSetProvider(FixtureContext fixture) =>
        PeopleRelationshipCrudProblemDetailsScenario.CreateUpdateEdOrgAndPeopleClaimSetProvider(fixture);

    [Test]
    public Task It_prioritizes_people_proposed_data_problem_details_over_mixed_no_relationship() =>
        PeopleRelationshipCrudProblemDetailsScenario.It_prioritizes_people_proposed_data_problem_details_over_mixed_no_relationship(
            Harness
        );
}
