// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Performance.Harness.Measurement;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Postgresql;
using Microsoft.Extensions.DependencyInjection;

namespace EdFi.DataManagementService.Performance.Harness.Runs;

/// <summary>
/// One benchmark side in a fresh process, configured by the comparison wrapper through
/// <c>PERF_*</c> variables. The slowdown is registered only when PERF_INJECT_SLOWDOWN is set.
/// </summary>
[TestFixture]
[Explicit("Benchmark side: loads the profile's fixture and writes side artifacts")]
[Category("Performance")]
public class Given_Postgresql_BenchmarkSideRun : PostgresqlApiIntegrationTestBase
{
    private PerfBenchmarkSidePlan _plan = null!;
    private PerfSlowdownInjection? _injection;
    private string _leasedConnectionString = null!;

    protected override FixtureKey Fixture => FixtureKey.AuthoritativeDs52;

    protected override bool CaptureQueryPlans => true;

    protected override IReadOnlyDictionary<string, string> AdditionalHostSettings =>
        PerfBenchmarkSidePipeline.HostSettings;

    [OneTimeSetUp]
    public void LoadPlan()
    {
        _plan = PerfBenchmarkSidePlan.FromEnvironment(
            PerfProvider.Postgresql,
            Environment.GetEnvironmentVariable
        );
        _injection = PerfBenchmarkSidePipeline.InjectionFor(_plan);
    }

    protected override void ConfigureAdditionalServices(IServiceCollection services) =>
        PerfBenchmarkSidePipeline.RegisterInjection(services, _injection);

    protected override async Task<string> LeaseDatabaseAsync(FixtureContext fixture)
    {
        _leasedConnectionString = await base.LeaseDatabaseAsync(fixture);
        await PerfBenchmarkSidePipeline.CommitWithoutFlushWaitAsync(_leasedConnectionString);
        return _leasedConnectionString;
    }

    [Test]
    public async Task It_measures_one_benchmark_side()
    {
        PerfBenchmarkSideOutcome outcome = await PerfBenchmarkSidePipeline.RunAsync(
            _plan,
            PerfBenchmarkSidePipeline.LiveSteps(
                Harness,
                _plan,
                _leasedConnectionString,
                () => OpenAssertionConnectionAsync(_leasedConnectionString),
                PerfEvidenceRunSettings.FromEnvironment(),
                _injection
            )
        );

        await TestContext.Out.WriteLineAsync($"Benchmark side artifacts: {_plan.SideDirectory}");
        await TestContext.Out.WriteLineAsync($"Read run: {outcome.ReadRunDirectory}");
    }
}
