// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Mssql;

namespace EdFi.DataManagementService.Performance.Harness.Smoke;

[TestFixture]
[Explicit("Loads and verifies a live 10,000-row fixture; run manually against a local database")]
[Category("Performance")]
public class Given_Mssql_PerfFixtureLoaderSmoke : MssqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.AuthoritativeDs52;

    [Test]
    public async Task It_loads_verifies_and_serves_the_smoke_fixture()
    {
        await PerfFixtureSmokeScenario.RunAsync(Harness, PerfProvider.Mssql);
    }
}
