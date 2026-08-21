// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Performance.Harness.Fixtures;
using EdFi.DataManagementService.Performance.Harness.Measurement;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Mssql;

namespace EdFi.DataManagementService.Performance.Harness.Runs;

/// <summary>
/// The SQL Server evidence-run entry point, configured entirely through PERF_* environment
/// variables. Normally invoked by eng/performance/invoke-traditional-baseline.ps1.
/// </summary>
[TestFixture]
[Explicit("Evidence run: loads the configured fixture and writes baseline artifacts")]
[Category("Performance")]
public class Given_Mssql_TraditionalBaselineRun : MssqlApiIntegrationTestBase
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
    public async Task It_captures_the_traditional_baseline()
    {
        PerfRunConfiguration configuration = PerfRunConfigurationLoader.FromEnvironment();
        PerfEvidenceRunSettings settings = PerfEvidenceRunSettings.FromEnvironment();

        string runDirectory = await PerfBaselineRunPipeline.RunAsync(
            Harness,
            PerfProvider.Mssql,
            () => OpenAssertionConnectionAsync(_leasedConnectionString),
            _leasedConnectionString,
            new PerfFixtureDefinition(configuration.Fixture),
            configuration.DeepOffset,
            configuration.WarmupIterations,
            configuration.MeasuredIterations,
            configuration.ResultsDirectory,
            configuration.RunnerCommit,
            settings
        );

        await TestContext.Out.WriteLineAsync($"Baseline artifacts: {runDirectory}");
    }
}
