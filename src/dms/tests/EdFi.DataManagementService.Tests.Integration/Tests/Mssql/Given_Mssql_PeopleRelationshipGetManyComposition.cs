// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Mssql;
using EdFi.DataManagementService.Tests.Integration.Scenarios;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Mssql;

public sealed class Given_Mssql_PeopleRelationshipGetManyComposition_And : MssqlApiIntegrationTestBase
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

public sealed class Given_Mssql_PeopleRelationshipGetManyComposition_Or : MssqlApiIntegrationTestBase
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

public sealed class Given_Mssql_PeopleRelationshipGetManyComposition_Pagination : MssqlApiIntegrationTestBase
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

public sealed class Given_Mssql_PeopleRelationshipGetManyComposition_ScalarParameters
    : MssqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.AuthorizationQuery;

    protected override bool BypassAuthorization => false;

    protected override bool CaptureQueryPlans => true;

    protected override IReadOnlyList<long> ClientEducationOrganizationIds =>
        PeopleRelationshipGetManyCompositionScenario.CreateUniqueClaimEducationOrganizationIds(1999);

    protected override IClaimSetProvider CreateClaimSetProvider(FixtureContext fixture) =>
        PeopleRelationshipGetManyCompositionScenario.CreateStudentsOnlyReadClaimSetProvider(fixture);

    [Test]
    public Task It_uses_expanded_scalar_parameters_for_1999_unique_claim_edorg_ids() =>
        PeopleRelationshipGetManyCompositionScenario.It_uses_expanded_scalar_parameters_for_1999_unique_claim_edorg_ids(
            Harness
        );
}

public sealed class Given_Mssql_PeopleRelationshipGetManyComposition_StructuredParameters
    : MssqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.AuthorizationQuery;

    protected override bool BypassAuthorization => false;

    protected override bool CaptureQueryPlans => true;

    protected override IReadOnlyList<long> ClientEducationOrganizationIds =>
        PeopleRelationshipGetManyCompositionScenario.CreateUniqueClaimEducationOrganizationIds(2000);

    protected override IClaimSetProvider CreateClaimSetProvider(FixtureContext fixture) =>
        PeopleRelationshipGetManyCompositionScenario.CreateStudentsOnlyReadClaimSetProvider(fixture);

    [Test]
    public Task It_uses_a_structured_tvp_for_2000_unique_claim_edorg_ids() =>
        PeopleRelationshipGetManyCompositionScenario.It_uses_a_structured_tvp_for_2000_unique_claim_edorg_ids(
            Harness
        );
}

public sealed class Given_Mssql_PeopleRelationshipGetManyComposition_DeduplicatedParameters
    : MssqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.AuthorizationQuery;

    protected override bool BypassAuthorization => false;

    protected override bool CaptureQueryPlans => true;

    protected override IReadOnlyList<long> ClientEducationOrganizationIds =>
        PeopleRelationshipGetManyCompositionScenario.CreateDuplicateHeavyClaimEducationOrganizationIds();

    protected override IClaimSetProvider CreateClaimSetProvider(FixtureContext fixture) =>
        PeopleRelationshipGetManyCompositionScenario.CreateStudentsOnlyReadClaimSetProvider(fixture);

    [Test]
    public Task It_deduplicates_claim_edorg_ids_before_selecting_threshold() =>
        PeopleRelationshipGetManyCompositionScenario.It_deduplicates_claim_edorg_ids_before_selecting_threshold(
            Harness
        );
}
