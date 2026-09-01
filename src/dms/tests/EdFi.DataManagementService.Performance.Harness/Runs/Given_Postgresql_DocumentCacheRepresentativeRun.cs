// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Performance.Harness.Measurement;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Postgresql;

namespace EdFi.DataManagementService.Performance.Harness.Runs;

/// <summary>
/// PostgreSQL entry point for the DMS-1317 representative DocumentCache qualification run.
/// This fixture is intentionally explicit because it loads the large DS 5.2 performance
/// fixture and writes release-validation artifacts.
/// </summary>
[TestFixture]
[Explicit("Representative DocumentCache qualification evidence run")]
[NonParallelizable]
[Category("Performance")]
[Category("DocumentCacheRepresentativeQualification")]
[Category("PostgresqlIntegration")]
public class Given_Postgresql_DocumentCacheRepresentativeRun : PostgresqlApiIntegrationTestBase
{
    private string _leasedConnectionString = null!;

    protected override FixtureKey Fixture => FixtureKey.AuthoritativeDs52;

    protected override bool CaptureQueryPlans => true;

    protected override bool EnableDocumentCacheReadAcceleration => true;

    protected override string DocumentCacheReadAccelerationDirectFillTimeout => "00:00:05";

    protected override async Task<string> LeaseDatabaseAsync(FixtureContext fixture)
    {
        _leasedConnectionString = await base.LeaseDatabaseAsync(fixture);
        return _leasedConnectionString;
    }

    [Test]
    public async Task It_runs_the_representative_document_cache_qualification()
    {
        DocumentCacheRepresentativeRunConfiguration configuration =
            DocumentCacheRepresentativeRunConfigurationLoader.FromEnvironment(PerfProvider.Postgresql);

        string runDirectory = await DocumentCacheQualificationRunPipeline.RunAsync(
            Harness,
            PerfProvider.Postgresql,
            () => OpenAssertionConnectionAsync(_leasedConnectionString),
            _leasedConnectionString,
            configuration
        );

        Assert.That(runDirectory, Is.Not.Empty);
        await TestContext.Out.WriteLineAsync(
            $"DocumentCache representative qualification artifacts: {runDirectory}"
        );
    }
}
