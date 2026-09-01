// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Performance.Harness.Measurement;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Mssql;
using Microsoft.Extensions.DependencyInjection;

namespace EdFi.DataManagementService.Performance.Harness.Smoke;

[TestFixture]
[Explicit("DocumentCache qualification pipeline against a live database at smoke scale; run manually")]
[NonParallelizable]
[Category("Performance")]
[Category("DocumentCacheQualificationSmoke")]
public class Given_Mssql_DocumentCacheQualificationRunSmoke : MssqlApiIntegrationTestBase
{
    private const int MinimumSmokeProductMajorVersion = 17;

    private string _leasedConnectionString = null!;

    protected override FixtureKey Fixture => FixtureKey.AuthoritativeDs52;

    protected override bool CaptureQueryPlans => true;

    protected override bool EnableDocumentCacheReadAcceleration => true;

    protected override string DocumentCacheReadAccelerationDirectFillTimeout => "00:00:05";

    protected override bool MatchProductionWriteIsolation => true;

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
        await GuardSqlServer2025Async();

        await DocumentCacheQualificationRunSmoke.RunAsync(
            Harness,
            PerfProvider.Mssql,
            () => OpenAssertionConnectionAsync(_leasedConnectionString),
            _leasedConnectionString,
            imageTag: "mcr.microsoft.com/mssql/server:2025-latest",
            imageDigest: "sha256:86cc6144ef39bb0fbed2329e1ad79b13ee82e7b2e4739213a0db0800e668a74a",
            storageNote: "local docker volume, not tmpfs"
        );
    }

    private async Task GuardSqlServer2025Async()
    {
        await using DbCommand command = Harness.DbConnection.CreateCommand();
        command.CommandText = "SELECT CAST(SERVERPROPERTY('ProductMajorVersion') AS nvarchar(128));";
        object? value = await command.ExecuteScalarAsync();
        string? rawProductMajorVersion = value is null or DBNull ? null : Convert.ToString(value);

        if (
            !int.TryParse(rawProductMajorVersion, out int productMajorVersion)
            || productMajorVersion < MinimumSmokeProductMajorVersion
        )
        {
            Assert.Ignore(
                $"DocumentCache qualification smoke requires SQL Server 2025 or newer; got '{rawProductMajorVersion ?? "<null>"}'."
            );
        }
    }
}
