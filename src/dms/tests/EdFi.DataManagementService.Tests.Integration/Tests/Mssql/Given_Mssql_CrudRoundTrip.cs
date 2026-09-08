// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Mssql;
using EdFi.DataManagementService.Tests.Integration.Scenarios;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Mssql;

public sealed class Given_Mssql_CrudRoundTrip : MssqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.ProfileRootOnlyMerge;

    [Test]
    public Task It_creates_and_reads_a_student() =>
        CrudRoundTripScenario.It_creates_and_reads_a_student(Harness);

    [Test]
    public Task It_updates_a_student_via_put() => CrudRoundTripScenario.It_updates_a_student_via_put(Harness);

    [Test]
    public Task It_upserts_a_student_via_post() =>
        CrudRoundTripScenario.It_upserts_a_student_via_post(Harness);

    [Test]
    public Task It_deletes_a_student() => CrudRoundTripScenario.It_deletes_a_student(Harness);

    [Test]
    public Task It_pages_students_via_query() => CrudRoundTripScenario.It_pages_students_via_query(Harness);

    [Test]
    public Task It_rejects_create_with_missing_reference() =>
        CrudRoundTripScenario.It_rejects_create_with_missing_reference(Harness);

    [Test]
    public Task It_rejects_delete_when_referenced() =>
        CrudRoundTripScenario.It_rejects_delete_when_referenced(Harness);
}
