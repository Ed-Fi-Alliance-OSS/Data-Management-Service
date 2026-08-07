// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Postgresql;
using EdFi.DataManagementService.Tests.Integration.Scenarios;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Postgresql;

/// <summary>
/// PostgreSQL cross-boundary proof that the query-parameter value selected at the HTTP boundary is
/// the value cursor validation reasons about. Parameter canonicalization and cursor validation are
/// provider-neutral, so there is no SQL Server twin; the leased database is required only because
/// query validation sits behind fingerprint resolution.
/// </summary>
public sealed class Given_Postgresql_CursorParameterCanonicalization : PostgresqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.AuthoritativeDs52;

    [Test]
    public Task It_selects_the_validation_phase_from_the_last_case_variant_value() =>
        CursorParameterCanonicalizationScenario.It_selects_the_validation_phase_from_the_last_case_variant_value(
            Harness
        );

    [Test]
    public Task It_validates_the_last_case_variant_value_within_a_phase() =>
        CursorParameterCanonicalizationScenario.It_validates_the_last_case_variant_value_within_a_phase(
            Harness
        );
}
