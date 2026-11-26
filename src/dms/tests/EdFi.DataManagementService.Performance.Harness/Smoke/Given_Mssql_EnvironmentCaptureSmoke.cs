// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Performance.Harness.Measurement;
using EdFi.DataManagementService.Performance.Harness.Results;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Mssql;
using FluentAssertions;
using Microsoft.Data.SqlClient;

namespace EdFi.DataManagementService.Performance.Harness.Smoke;

/// <summary>
/// The fingerprint's database settings carry the database options that change a write's
/// cost, so delayed durability on the same server and image reaches them.
/// </summary>
[TestFixture]
[Explicit("Environment capture against a live database; run manually")]
[Category("Performance")]
public class Given_Mssql_EnvironmentCaptureSmoke : MssqlApiIntegrationTestBase
{
    private string _leasedConnectionString = null!;

    protected override FixtureKey Fixture => FixtureKey.AuthoritativeDs52;

    protected override async Task<string> LeaseDatabaseAsync(FixtureContext fixture)
    {
        _leasedConnectionString = await base.LeaseDatabaseAsync(fixture);
        return _leasedConnectionString;
    }

    [Test]
    public async Task It_captures_a_database_level_durability_change()
    {
        IReadOnlyList<PerfSetting> untouched = await CaptureSettingsAsync();

        untouched.Should().Contain(new PerfSetting("delayed_durability_desc", "DISABLED"));
        untouched.Select(setting => setting.Name).Should().Contain("snapshot_isolation_state_desc");

        try
        {
            await SetDelayedDurabilityAsync("FORCED");

            (await CaptureSettingsAsync())
                .Should()
                .Contain(new PerfSetting("delayed_durability_desc", "FORCED"));
        }
        finally
        {
            // The lease may go back to a pool slot, so leave the option as it was.
            await SetDelayedDurabilityAsync("DISABLED");
        }
    }

    private async Task SetDelayedDurabilityAsync(string value)
    {
        string database = new SqlConnectionStringBuilder(_leasedConnectionString).InitialCatalog;
        await using SqlConnection connection = new(_leasedConnectionString);
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = $"ALTER DATABASE [{database}] SET DELAYED_DURABILITY = {value};";
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// A database option is read from sys.databases, not from the session, so the existing
    /// connection sees the change.
    /// </summary>
    private async Task<IReadOnlyList<PerfSetting>> CaptureSettingsAsync()
    {
        await using SqlConnection connection = new(_leasedConnectionString);
        await connection.OpenAsync();
        PerfEnvironmentIdentity identity = await PerfEnvironmentCapture.CaptureAsync(
            connection,
            PerfProvider.Mssql,
            imageTag: "mcr.microsoft.com/mssql/server:2025-latest",
            imageDigest: "sha256:86cc6144ef39bb0fbed2329e1ad79b13ee82e7b2e4739213a0db0800e668a74a",
            storageNote: "local docker volume, not tmpfs",
            _leasedConnectionString
        );
        return identity.Server.Settings;
    }
}
