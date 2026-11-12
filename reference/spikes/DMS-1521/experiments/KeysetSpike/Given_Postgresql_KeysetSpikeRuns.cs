// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Postgresql;

namespace KeysetSpike;

/// <summary>
/// PostgreSQL resource run: observer calibration on the empty lease, then the primary fixture, the
/// six-cell matrix through the harness scenario executor, spike plans, replays, counters, and parity.
/// </summary>
[TestFixture]
[Explicit("DMS-1521 evidence run; started by Invoke-KeysetSpike.ps1")]
[Category("DMS1521")]
public class Given_Postgresql_KeysetSpikeResourceRun : PostgresqlApiIntegrationTestBase
{
    private string _leased = null!;

    protected override FixtureKey Fixture => FixtureKey.AuthoritativeDs52;

    protected override bool CaptureQueryPlans => true;

    protected override async Task<string> LeaseDatabaseAsync(FixtureContext fixture) =>
        _leased = await base.LeaseDatabaseAsync(fixture);

    [Test]
    public async Task It_runs_the_resource_fixture() =>
        await TestContext.Out.WriteLineAsync(
            "Run directory: "
                + await KeysetSpikeRunner.RunResourceAsync(
                    new(
                        Harness,
                        PerfProvider.Postgresql,
                        _leased,
                        () => OpenAssertionConnectionAsync(_leased)
                    )
                )
        );
}

/// <summary>PostgreSQL descriptor run: the descriptor fixture on its own lease, limits 25 and 500.</summary>
[TestFixture]
[Explicit("DMS-1521 evidence run; started by Invoke-KeysetSpike.ps1")]
[Category("DMS1521")]
public class Given_Postgresql_KeysetSpikeDescriptorRun : PostgresqlApiIntegrationTestBase
{
    private string _leased = null!;

    protected override FixtureKey Fixture => FixtureKey.AuthoritativeDs52;

    protected override bool CaptureQueryPlans => true;

    protected override async Task<string> LeaseDatabaseAsync(FixtureContext fixture) =>
        _leased = await base.LeaseDatabaseAsync(fixture);

    [Test]
    public async Task It_runs_the_descriptor_fixture() =>
        await TestContext.Out.WriteLineAsync(
            "Run directory: "
                + await KeysetSpikeRunner.RunDescriptorAsync(
                    new(
                        Harness,
                        PerfProvider.Postgresql,
                        _leased,
                        () => OpenAssertionConnectionAsync(_leased)
                    )
                )
        );
}

/// <summary>
/// PostgreSQL read-acceleration run: the primary fixture with read acceleration on (configured as in
/// <c>Given_Postgresql_DocumentCacheReadAcceleration</c>), a cache-hit cell and a forced-miss cell.
/// </summary>
[TestFixture]
[Explicit("DMS-1521 evidence run; started by Invoke-KeysetSpike.ps1")]
[Category("DMS1521")]
public class Given_Postgresql_KeysetSpikeReadAccelerationRun : PostgresqlApiIntegrationTestBase
{
    private string _leased = null!;

    protected override FixtureKey Fixture => FixtureKey.AuthoritativeDs52;

    protected override bool CaptureQueryPlans => true;

    protected override bool EnableDocumentCacheReadAcceleration => true;

    protected override bool RecordDocumentCacheReadTelemetry => true;

    // As in the integration tests. The production default (250 ms) lets SQL Server direct-fill no
    // document on this machine, so the page could not be warmed.
    protected override string DocumentCacheReadAccelerationDirectFillTimeout => "00:00:05";

    protected override async Task<string> LeaseDatabaseAsync(FixtureContext fixture) =>
        _leased = await base.LeaseDatabaseAsync(fixture);

    [Test]
    public async Task It_runs_the_read_acceleration_fixture() =>
        await TestContext.Out.WriteLineAsync(
            "Run directory: "
                + await KeysetSpikeRunner.RunReadAccelerationAsync(
                    new(
                        Harness,
                        PerfProvider.Postgresql,
                        _leased,
                        () => OpenAssertionConnectionAsync(_leased)
                    )
                )
        );
}
