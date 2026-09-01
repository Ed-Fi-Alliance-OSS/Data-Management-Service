// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Performance.Harness.Measurement;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Postgresql;
using Microsoft.Extensions.DependencyInjection;

namespace EdFi.DataManagementService.Performance.Harness.Smoke;

[TestFixture]
[Explicit("DocumentCache qualification pipeline against a live database at smoke scale; run manually")]
[NonParallelizable]
[Category("Performance")]
[Category("DocumentCacheQualificationSmoke")]
public class Given_Postgresql_DocumentCacheQualificationRunSmoke : PostgresqlApiIntegrationTestBase
{
    private string _leasedConnectionString = null!;

    protected override FixtureKey Fixture => FixtureKey.AuthoritativeDs52;

    protected override bool CaptureQueryPlans => true;

    protected override bool EnableDocumentCacheReadAcceleration => true;

    protected override string DocumentCacheReadAccelerationDirectFillTimeout => "00:00:05";

    protected override int? DocumentCacheProjectorPageSizeOverride => 1_000;

    protected override int? DocumentCacheProjectorMaxConcurrentTargetsOverride => 2;

    protected override long? DocumentCacheProjectorBaselineHighWaterMarkOverride =>
        PerfFixtureKind.Smoke10k.RowCount;

    protected override async Task<string> LeaseDatabaseAsync(FixtureContext fixture)
    {
        _leasedConnectionString = await base.LeaseDatabaseAsync(fixture);
        return _leasedConnectionString;
    }

    protected override void CustomizeServices(
        IServiceCollection services,
        FixtureContext fixture,
        string leasedConnectionString
    )
    {
        services.UseInternalOnlyDocumentCacheDownstreamHistory();
    }

    [Test]
    public async Task It_writes_every_phase_artifact_end_to_end()
    {
        await DocumentCacheQualificationRunSmoke.RunAsync(
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
