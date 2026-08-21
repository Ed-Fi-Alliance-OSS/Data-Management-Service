// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Mssql;

namespace EdFi.DataManagementService.Performance.Harness.Smoke;

[TestFixture]
[Explicit("Full artifact pipeline against a live database at smoke scale; run manually")]
[Category("Performance")]
public class Given_Mssql_BaselineRunSmoke : MssqlApiIntegrationTestBase
{
    private string _leasedConnectionString = null!;

    protected override FixtureKey Fixture => FixtureKey.AuthoritativeDs52;

    protected override bool CaptureQueryPlans => true;

    protected override async Task<string> LeaseDatabaseAsync(FixtureContext fixture)
    {
        _leasedConnectionString = await base.LeaseDatabaseAsync(fixture);
        return _leasedConnectionString;
    }

    [Test]
    public async Task It_writes_validated_artifacts_end_to_end()
    {
        // The digest resolved from the pinned local image; the capture wrapper resolves and
        // validates these dynamically for evidence runs.
        await BaselineRunSmoke.RunAsync(
            Harness,
            PerfProvider.Mssql,
            () => OpenAssertionConnectionAsync(_leasedConnectionString),
            _leasedConnectionString,
            imageTag: "mcr.microsoft.com/mssql/server:2025-latest",
            imageDigest: "sha256:86cc6144ef39bb0fbed2329e1ad79b13ee82e7b2e4739213a0db0800e668a74a",
            storageNote: "local docker volume, not tmpfs"
        );
    }
}
