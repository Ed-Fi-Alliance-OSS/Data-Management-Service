// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Mssql;

namespace EdFi.DataManagementService.Performance.Harness.Smoke;

[TestFixture]
[Explicit("Runs the six-cell matrix against a live 10,000-row fixture; run manually")]
[Category("Performance")]
public class Given_Mssql_ScenarioExecutorSmoke : MssqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.AuthoritativeDs52;

    protected override bool CaptureQueryPlans => true;

    [Test]
    public async Task It_measures_all_six_cells_with_guardrails()
    {
        await ScenarioExecutorSmoke.RunAsync(Harness, PerfProvider.Mssql);
    }
}
