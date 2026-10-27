// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Postgresql;
using EdFi.DataManagementService.Tests.Integration.Scenarios;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Postgresql;

/// <summary>
/// PostgreSQL API regression: a key-changing PUT on a Section or StudentSchoolAssociation must
/// cascade into its association referrer. PostgreSQL keeps every cascade edge, so this guards
/// parity with the SQL Server lane.
/// </summary>
public sealed class Given_Postgresql_KeyChangeCascade : PostgresqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.AuthoritativeDs52;

    [Test]
    public Task It_cascades_a_section_session_change_to_the_student_section_association() =>
        KeyChangeCascadeScenario.It_cascades_a_section_session_change_to_the_student_section_association(
            Harness
        );

    [Test]
    public Task It_cascades_a_student_school_association_entry_date_change_to_the_student_assessment_registration() =>
        KeyChangeCascadeScenario.It_cascades_a_student_school_association_entry_date_change_to_the_student_assessment_registration(
            Harness
        );
}
