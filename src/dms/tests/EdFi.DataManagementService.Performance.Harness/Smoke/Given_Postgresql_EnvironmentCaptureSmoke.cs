// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Performance.Harness.Measurement;
using EdFi.DataManagementService.Performance.Harness.Results;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Postgresql;
using FluentAssertions;
using Npgsql;

namespace EdFi.DataManagementService.Performance.Harness.Smoke;

/// <summary>
/// The fingerprint's database settings come from the allowlist only, and a durability change
/// made on the same server and image reaches them.
/// </summary>
[TestFixture]
[Explicit("Environment capture against a live database; run manually")]
[Category("Performance")]
public class Given_Postgresql_EnvironmentCaptureSmoke : PostgresqlApiIntegrationTestBase
{
    private string _leasedConnectionString = null!;

    protected override FixtureKey Fixture => FixtureKey.AuthoritativeDs52;

    protected override async Task<string> LeaseDatabaseAsync(FixtureContext fixture)
    {
        _leasedConnectionString = await base.LeaseDatabaseAsync(fixture);
        return _leasedConnectionString;
    }

    [Test]
    public async Task It_captures_the_allowlisted_settings_and_a_database_level_durability_change()
    {
        IReadOnlyList<PerfSetting> untouched = await CaptureSettingsAsync();

        untouched.Should().Contain(new PerfSetting("synchronous_commit", "on"));
        untouched.Should().Contain(new PerfSetting("fsync", "on"));
        untouched
            .Select(setting => setting.Name)
            .Where(name => !name.StartsWith("npgsql_", StringComparison.Ordinal))
            .Should()
            .OnlyContain(name => PerfEnvironmentCapture.PostgresqlSettingNames.Contains(name));

        string database = new NpgsqlConnectionStringBuilder(_leasedConnectionString).Database!;
        await using (NpgsqlConnection admin = new(_leasedConnectionString))
        {
            await admin.OpenAsync();
            await using NpgsqlCommand command = admin.CreateCommand();
            command.CommandText = $"ALTER DATABASE \"{database}\" SET synchronous_commit = off;";
            await command.ExecuteNonQueryAsync();
        }

        (await CaptureSettingsAsync()).Should().Contain(new PerfSetting("synchronous_commit", "off"));
    }

    /// <summary>
    /// Captures over a new physical connection: a database default set by ALTER DATABASE applies
    /// only when a session starts, so a pooled session would still report the old value.
    /// </summary>
    private async Task<IReadOnlyList<PerfSetting>> CaptureSettingsAsync()
    {
        NpgsqlConnectionStringBuilder unpooled = new(_leasedConnectionString) { Pooling = false };
        await using NpgsqlConnection connection = new(unpooled.ConnectionString);
        await connection.OpenAsync();
        PerfEnvironmentIdentity identity = await PerfEnvironmentCapture.CaptureAsync(
            connection,
            PerfProvider.Postgresql,
            imageTag: "postgres:18.6-alpine",
            imageDigest: "sha256:77f585114c32fbca283dc835b0596f4e52b51b4c6662d7810b2f4084f60a1873",
            storageNote: "local docker volume, not tmpfs",
            _leasedConnectionString
        );
        return identity.Server.Settings;
    }
}
