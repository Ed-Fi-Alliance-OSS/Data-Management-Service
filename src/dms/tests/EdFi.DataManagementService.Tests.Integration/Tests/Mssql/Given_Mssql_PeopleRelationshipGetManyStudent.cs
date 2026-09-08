// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Mssql;
using EdFi.DataManagementService.Tests.Integration.Scenarios;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Mssql;

public sealed class Given_Mssql_PeopleRelationshipGetManyStudent_Self_And_Direct : MssqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.AuthorizationQuery;

    protected override bool BypassAuthorization => false;

    protected override IReadOnlyList<long> ClientEducationOrganizationIds =>
        [PeopleRelationshipGetManyStudentScenario.ClaimEducationOrganizationId];

    protected override IClaimSetProvider CreateClaimSetProvider(FixtureContext fixture) =>
        PeopleRelationshipGetManyStudentScenario.CreateStudentsOnlyReadClaimSetProvider(fixture);

    [Test]
    public Task It_filters_student_self_and_direct_student_get_many() =>
        PeopleRelationshipGetManyStudentScenario.It_filters_student_self_and_direct_student_get_many(Harness);
}

public sealed class Given_Mssql_PeopleRelationshipGetManyStudent_ThroughResponsibility
    : MssqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.AuthorizationQuery;

    protected override bool BypassAuthorization => false;

    protected override IReadOnlyList<long> ClientEducationOrganizationIds =>
        [PeopleRelationshipGetManyStudentScenario.ClaimEducationOrganizationId];

    protected override IClaimSetProvider CreateClaimSetProvider(FixtureContext fixture) =>
        PeopleRelationshipGetManyStudentScenario.CreateStudentsOnlyThroughResponsibilityReadClaimSetProvider(
            fixture
        );

    [Test]
    public Task It_filters_students_only_through_responsibility_get_many() =>
        PeopleRelationshipGetManyStudentScenario.It_filters_students_only_through_responsibility_get_many(
            Harness
        );
}

public sealed class Given_Mssql_PeopleRelationshipGetManyStudent_Transitive : MssqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.AuthorizationQuery;

    protected override bool BypassAuthorization => false;

    protected override IReadOnlyList<long> ClientEducationOrganizationIds =>
        [PeopleRelationshipGetManyStudentScenario.ClaimEducationOrganizationId];

    protected override IClaimSetProvider CreateClaimSetProvider(FixtureContext fixture) =>
        PeopleRelationshipGetManyStudentScenario.CreateTransitiveStudentReadClaimSetProvider(fixture);

    [Test]
    public Task It_filters_transitive_student_academic_record_get_many() =>
        PeopleRelationshipGetManyStudentScenario.It_filters_transitive_student_academic_record_get_many(
            Harness
        );
}
