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
[Explicit("Benchmark side against a live database at smoke scale; run manually")]
[Category("Performance")]
public class Given_Postgresql_BenchmarkSideSmoke : PostgresqlApiIntegrationTestBase
{
    private PerfBenchmarkSidePlan _plan = null!;
    private PerfSlowdownInjection _injection = null!;
    private string _leasedConnectionString = null!;

    protected override FixtureKey Fixture => FixtureKey.AuthoritativeDs52;

    protected override bool CaptureQueryPlans => true;

    protected override IReadOnlyDictionary<string, string> AdditionalHostSettings =>
        PerfBenchmarkSidePipeline.HostSettings;

    [OneTimeSetUp]
    public void CreatePlan()
    {
        _plan = BenchmarkSideSmoke.Plan(PerfProvider.Postgresql);
        _injection = PerfBenchmarkSidePipeline.InjectionFor(_plan)!;
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
    public async Task It_measures_a_valid_benchmark_side()
    {
        await BenchmarkSideSmoke.RunAsync(
            Harness,
            _plan,
            _injection,
            _leasedConnectionString,
            () => OpenAssertionConnectionAsync(_leasedConnectionString),
            imageTag: "postgres:18.6-alpine",
            imageDigest: "sha256:77f585114c32fbca283dc835b0596f4e52b51b4c6662d7810b2f4084f60a1873"
        );
    }
}
