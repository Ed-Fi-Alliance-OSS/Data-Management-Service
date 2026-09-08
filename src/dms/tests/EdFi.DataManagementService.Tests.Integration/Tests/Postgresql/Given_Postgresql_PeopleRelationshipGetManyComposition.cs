// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Postgresql;
using EdFi.DataManagementService.Tests.Integration.Scenarios;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Postgresql;

public sealed class Given_Postgresql_PeopleRelationshipGetManyComposition_And
    : PostgresqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.AuthorizationQuery;

    protected override bool BypassAuthorization => false;

    protected override IReadOnlyList<long> ClientEducationOrganizationIds =>
        [PeopleRelationshipGetManyCompositionScenario.ClaimEducationOrganizationId];

    protected override IClaimSetProvider CreateClaimSetProvider(FixtureContext fixture) =>
        PeopleRelationshipGetManyCompositionScenario.CreateEdOrgAndPeopleReadClaimSetProvider(fixture);

    [Test]
    public Task It_ands_edorg_and_people_subjects_within_one_strategy() =>
        PeopleRelationshipGetManyCompositionScenario.It_ands_edorg_and_people_subjects_within_one_strategy(
            Harness
        );
}

public sealed class Given_Postgresql_PeopleRelationshipGetManyComposition_Or
    : PostgresqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.AuthorizationQuery;

    protected override bool BypassAuthorization => false;

    protected override IReadOnlyList<long> ClientEducationOrganizationIds =>
        [PeopleRelationshipGetManyCompositionScenario.ClaimEducationOrganizationId];

    protected override IClaimSetProvider CreateClaimSetProvider(FixtureContext fixture) =>
        PeopleRelationshipGetManyCompositionScenario.CreateEdOrgOrPeopleReadClaimSetProvider(fixture);

    [Test]
    public Task It_ors_edorg_and_people_strategies_without_duplicate_documents() =>
        PeopleRelationshipGetManyCompositionScenario.It_ors_edorg_and_people_strategies_without_duplicate_documents(
            Harness
        );
}

public sealed class Given_Postgresql_PeopleRelationshipGetManyComposition_Pagination
    : PostgresqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.AuthorizationQuery;

    protected override bool BypassAuthorization => false;

    protected override IReadOnlyList<long> ClientEducationOrganizationIds =>
        [PeopleRelationshipGetManyCompositionScenario.ClaimEducationOrganizationId];

    protected override IClaimSetProvider CreateClaimSetProvider(FixtureContext fixture) =>
        PeopleRelationshipGetManyCompositionScenario.CreateStudentsOnlyReadClaimSetProvider(fixture);

    [Test]
    public Task It_applies_authorization_before_paging_and_total_count() =>
        PeopleRelationshipGetManyCompositionScenario.It_applies_authorization_before_paging_and_total_count(
            Harness
        );
}
