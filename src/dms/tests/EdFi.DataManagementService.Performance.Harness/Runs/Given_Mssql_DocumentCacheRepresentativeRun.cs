// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Performance.Harness.Measurement;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Mssql;

namespace EdFi.DataManagementService.Performance.Harness.Runs;

/// <summary>
/// SQL Server entry point for the DMS-1317 representative DocumentCache qualification run.
/// This fixture is intentionally explicit because it loads the large DS 5.2 performance
/// fixture and writes release-validation artifacts.
/// </summary>
[TestFixture]
[Explicit("Representative DocumentCache qualification evidence run")]
[NonParallelizable]
[Category("Performance")]
[Category("DocumentCacheRepresentativeQualification")]
[Category("MssqlIntegration")]
public class Given_Mssql_DocumentCacheRepresentativeRun : MssqlApiIntegrationTestBase
{
    private const int MinimumRepresentativeProductMajorVersion = 17;

    private string _leasedConnectionString = null!;

    protected override FixtureKey Fixture => FixtureKey.AuthoritativeDs52;

    protected override bool CaptureQueryPlans => true;

    protected override bool EnableDocumentCacheReadAcceleration => true;

    protected override string DocumentCacheReadAccelerationDirectFillTimeout => "00:00:05";

    protected override bool MatchProductionWriteIsolation => true;

    protected override async Task<string> LeaseDatabaseAsync(FixtureContext fixture)
    {
        _leasedConnectionString = await base.LeaseDatabaseAsync(fixture);
        return _leasedConnectionString;
    }

    [Test]
    public async Task It_runs_the_representative_document_cache_qualification()
    {
        DocumentCacheRepresentativeRunConfiguration configuration =
            DocumentCacheRepresentativeRunConfigurationLoader.FromEnvironment(PerfProvider.Mssql);

        await GuardSqlServer2025Async();

        string runDirectory = await DocumentCacheQualificationRunPipeline.RunAsync(
            Harness,
            PerfProvider.Mssql,
            () => OpenAssertionConnectionAsync(_leasedConnectionString),
            _leasedConnectionString,
            configuration
        );

        Assert.That(runDirectory, Is.Not.Empty);
        await TestContext.Out.WriteLineAsync(
            $"DocumentCache representative qualification artifacts: {runDirectory}"
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
            || productMajorVersion < MinimumRepresentativeProductMajorVersion
        )
        {
            Assert.Fail(
                "DocumentCache representative qualification requires SQL Server 2025+ "
                    + $"(SERVERPROPERTY('ProductMajorVersion') >= {MinimumRepresentativeProductMajorVersion}); "
                    + $"observed ProductMajorVersion='{rawProductMajorVersion ?? "<null>"}'."
            );
        }
    }
}
