// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Postgresql;

namespace EdFi.DataManagementService.Performance.Harness.Smoke;

[TestFixture]
[Explicit("Runs live cursor/partition executor cells at smoke scale; run manually against a local database")]
[Category("Performance")]
public class Given_Postgresql_FinalGateExecutorSmoke : PostgresqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.AuthoritativeDs52;

    protected override bool CaptureQueryPlans => true;

    [Test]
    public async Task It_measures_unfiltered_cursor_and_partition_cells()
    {
        await PerfFinalGateExecutorSmokeScenario.RunUnfilteredAsync(Harness, PerfProvider.Postgresql);
    }

    [Test]
    public async Task It_measures_filtered_cursor_and_partition_cells()
    {
        await PerfFinalGateExecutorSmokeScenario.RunFilteredAsync(Harness, PerfProvider.Postgresql);
    }
}
