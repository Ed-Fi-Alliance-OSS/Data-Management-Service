// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Postgresql;

namespace EdFi.DataManagementService.Performance.Harness.Smoke;

[TestFixture]
[Explicit("Full artifact pipeline against a live database at smoke scale; run manually")]
[Category("Performance")]
public class Given_Postgresql_BaselineRunSmoke : PostgresqlApiIntegrationTestBase
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
        // The compose-pinned image identity; the capture wrapper resolves and validates these
        // dynamically for evidence runs.
        await BaselineRunSmoke.RunAsync(
            Harness,
            PerfProvider.Postgresql,
            () => OpenAssertionConnectionAsync(_leasedConnectionString),
            _leasedConnectionString,
            imageTag: "postgres:16.8-alpine",
            imageDigest: "sha256:951d0626662c85a25e1ba0a89e64f314a2b99abced2c85b4423506249c2d82b0",
            storageNote: "local docker volume, not tmpfs"
        );
    }
}
