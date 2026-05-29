// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Mssql;
using EdFi.DataManagementService.Tests.Integration.Scenarios;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Mssql;

public sealed class Given_Mssql_PeopleRelationshipGetManyContact : MssqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.AuthorizationQuery;

    protected override bool BypassAuthorization => false;

    protected override IReadOnlyList<long> ClientEducationOrganizationIds =>
        [PeopleRelationshipGetManyContactStaffScenario.ClaimEducationOrganizationId];

    protected override IClaimSetProvider CreateClaimSetProvider(FixtureContext fixture) =>
        PeopleRelationshipGetManyContactStaffScenario.CreatePeopleOnlyReadClaimSetProvider(fixture);

    [Test]
    public Task It_filters_contact_get_many() =>
        PeopleRelationshipGetManyContactStaffScenario.It_filters_contact_get_many(Harness);
}

public sealed class Given_Mssql_PeopleRelationshipGetManyStaff : MssqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.AuthorizationQuery;

    protected override bool BypassAuthorization => false;

    protected override IReadOnlyList<long> ClientEducationOrganizationIds =>
        [PeopleRelationshipGetManyContactStaffScenario.ClaimEducationOrganizationId];

    protected override IClaimSetProvider CreateClaimSetProvider(FixtureContext fixture) =>
        PeopleRelationshipGetManyContactStaffScenario.CreatePeopleOnlyReadClaimSetProvider(fixture);

    [Test]
    public Task It_filters_staff_get_many() =>
        PeopleRelationshipGetManyContactStaffScenario.It_filters_staff_get_many(Harness);
}
