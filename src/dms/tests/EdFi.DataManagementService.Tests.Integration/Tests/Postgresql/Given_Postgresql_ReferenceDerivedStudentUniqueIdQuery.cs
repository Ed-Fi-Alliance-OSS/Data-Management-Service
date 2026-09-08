// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Postgresql;
using EdFi.DataManagementService.Tests.Integration.Scenarios;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Postgresql;

/// <summary>
/// PostgreSQL API regression: reference-derived <c>studentUniqueId</c> GET-many query fields must
/// return 200 with correct filtering rather than 501. Covers the direct-site superclass alias
/// (StudentProgramAssociation) and the through-reference alias (CourseTranscript).
/// </summary>
public sealed class Given_Postgresql_ReferenceDerivedStudentUniqueIdQuery : PostgresqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.AuthoritativeDs52;

    [Test]
    public Task It_filters_student_program_associations_by_student_unique_id() =>
        ReferenceDerivedStudentUniqueIdQueryScenario.It_filters_student_program_associations_by_student_unique_id(
            Harness
        );

    [Test]
    public Task It_filters_course_transcripts_by_student_unique_id() =>
        ReferenceDerivedStudentUniqueIdQueryScenario.It_filters_course_transcripts_by_student_unique_id(
            Harness
        );
}
