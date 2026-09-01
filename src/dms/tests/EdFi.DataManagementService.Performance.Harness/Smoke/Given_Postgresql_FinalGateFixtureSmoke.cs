// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Postgresql;

namespace EdFi.DataManagementService.Performance.Harness.Smoke;

[TestFixture]
[Explicit("Loads and verifies live final-gate fixture variants; run manually against a local database")]
[Category("Performance")]
public class Given_Postgresql_FinalGateFixtureSmoke : PostgresqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.AuthoritativeDs52;

    [Test]
    public async Task It_seeds_authorization_and_applies_the_filtered_overlay_on_the_primary_fixture()
    {
        await PerfFinalGateFixtureSmokeScenario.RunPrimaryVariantsAsync(Harness, PerfProvider.Postgresql);
    }

    [Test]
    public async Task It_loads_verifies_and_serves_the_descriptor_fixture()
    {
        await PerfFinalGateFixtureSmokeScenario.RunDescriptorFixtureAsync(Harness, PerfProvider.Postgresql);
    }
}
