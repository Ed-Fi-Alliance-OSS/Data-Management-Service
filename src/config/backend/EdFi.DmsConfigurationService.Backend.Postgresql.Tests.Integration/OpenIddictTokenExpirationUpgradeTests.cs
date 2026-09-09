// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using EdFi.DmsConfigurationService.Backend.Deploy;
using FluentAssertions;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration;

/// <summary>
/// Exercises the DMS-1430 upgrade against a journaled pre-upgrade database, which is the state a
/// real deployment upgrades from. Each test reverts the isolated database to that state, seeds rows
/// with SQL so they carry exactly the wall clocks Npgsql would have written, and runs the deploy
/// again so only 0032 executes.
///
/// The migration reinterprets each stored wall clock through the session time zone the deploy runs
/// under. That is the best available inverse of how the rows were written, not a full repair,
/// because the old column type discarded information before the script ever runs. The three cases
/// below pin the three outcomes, including the one that lands early, so the limitation stays
/// documented rather than being mistaken for a safety guarantee.
/// </summary>
[TestFixture]
public class Given_a_pre_DMS_1430_OpenIddictToken_PostgreSQL_upgrade
{
    private const string JournalPattern = "%0032_Alter_OpenIddictToken_ExpirationDate_TimeZone%";
    private const string WriteTimeZone = "America/New_York";

    // A June instant, far from any DST transition, so the -04:00 offset is unambiguous.
    private static readonly DateTimeOffset UnambiguousInstant = new(2026, 6, 1, 18, 0, 0, TimeSpan.Zero);

    // America/New_York leaves DST at 2026-11-01 06:00Z. These two instants are an hour apart and
    // both land on local wall clock 01:30, so the old column stored them identically.
    private static readonly DateTimeOffset AmbiguousEarlierInstant = new(
        2026,
        11,
        1,
        5,
        30,
        0,
        TimeSpan.Zero
    );
    private static readonly DateTimeOffset AmbiguousLaterInstant = new(2026, 11, 1, 6, 30, 0, TimeSpan.Zero);

    private string _databaseName = string.Empty;
    private string _connectionString = string.Empty;

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        _connectionString = CreateIsolatedConnectionString(WriteTimeZone);
        DeploySuccessfully(_connectionString);
    }

    [OneTimeTearDown]
    public async Task OneTimeTeardown()
    {
        if (string.IsNullOrEmpty(_databaseName))
        {
            return;
        }

        await using NpgsqlConnection connection = new(CreateMaintenanceConnectionString());
        await connection.OpenAsync();
        await connection.ExecuteAsync($"""DROP DATABASE IF EXISTS "{_databaseName}" WITH (FORCE);""");
    }

    [SetUp]
    public async Task Setup()
    {
        await RevertToPreUpgradeStateAsync();
    }

    [Test]
    public async Task It_recovers_the_exact_instant_when_the_session_zone_is_unchanged()
    {
        await SeedStoredExpirationAsync("unambiguous", UnambiguousInstant);

        DeploySuccessfully(_connectionString);

        (await UpgradedColumnTypeAsync()).Should().Be("timestamp with time zone");
        (await UpgradedExpirationAsync("unambiguous")).Should().Be(UnambiguousInstant.UtcDateTime);
    }

    [Test]
    public async Task It_preserves_a_null_expiration()
    {
        await SeedStoredExpirationAsync("null-expiration", expiration: null);

        DeploySuccessfully(_connectionString);

        (await UpgradedExpirationAsync("null-expiration")).Should().BeNull();
    }

    [Test]
    public async Task It_leaves_the_value_untouched_when_the_script_is_replayed()
    {
        await SeedStoredExpirationAsync("replayed", UnambiguousInstant);

        DeploySuccessfully(_connectionString);
        await ReplayUpgradeScriptAsync();

        (await UpgradedExpirationAsync("replayed")).Should().Be(UnambiguousInstant.UtcDateTime);
    }

    /// <summary>
    /// Two instants an hour apart were already stored identically by the old column type, so the
    /// migration cannot separate them again: both recover as the later of the two. This is
    /// information the write destroyed, not something the migration loses. Recovering late is the
    /// safe direction here, because such a token lingers rather than being swept early.
    /// </summary>
    [Test]
    public async Task It_recovers_a_DST_ambiguous_wall_clock_as_the_later_instant()
    {
        await SeedStoredExpirationAsync("ambiguous-earlier", AmbiguousEarlierInstant);
        await SeedStoredExpirationAsync("ambiguous-later", AmbiguousLaterInstant);

        DeploySuccessfully(_connectionString);

        (await UpgradedExpirationAsync("ambiguous-earlier")).Should().Be(AmbiguousLaterInstant.UtcDateTime);
        (await UpgradedExpirationAsync("ambiguous-later")).Should().Be(AmbiguousLaterInstant.UtcDateTime);
    }

    /// <summary>
    /// The irrecoverable case, pinned so nobody mistakes the ambiguity result above for a general
    /// guarantee. A row written under America/New_York and migrated under UTC reconstructs against
    /// the migration-time zone, which places it EARLIER than the true instant by the offset
    /// difference. The zone a row was written under was never recorded, so the migration cannot
    /// detect this. The mitigation is operational: upgrade under the zone the rows were written
    /// under.
    /// </summary>
    [Test]
    public async Task It_shifts_the_instant_when_the_session_zone_changed_since_the_write()
    {
        await SeedStoredExpirationAsync("zone-changed", UnambiguousInstant);

        DeploySuccessfully(CreateConnectionString(_databaseName, "UTC"));

        DateTime? recovered = await UpgradedExpirationAsync("zone-changed");

        recovered.Should().NotBe(UnambiguousInstant.UtcDateTime);
        recovered.Should().Be(UnambiguousInstant.UtcDateTime.AddHours(-4));
    }

    /// <summary>
    /// Removes the 0032 journal entry and puts the column back to its pre-upgrade type, which is the
    /// state a deployment upgrading from an earlier release starts in. Reverting only this script
    /// keeps the outcome attributable to it, rather than to some earlier script replaying.
    /// </summary>
    private async Task RevertToPreUpgradeStateAsync()
    {
        await using NpgsqlConnection connection = new(_connectionString);
        await connection.OpenAsync();

        await connection.ExecuteAsync("""DELETE FROM "dmscs"."OpenIddictToken";""");
        await connection.ExecuteAsync(
            """
            ALTER TABLE "dmscs"."OpenIddictToken"
                ALTER COLUMN "ExpirationDate" TYPE timestamp without time zone;
            """
        );
        await connection.ExecuteAsync(
            """DELETE FROM public."dmscs_SchemaVersions" WHERE scriptname LIKE @JournalPattern;""",
            new { JournalPattern }
        );
    }

    /// <summary>
    /// Writes the row the way the pre-upgrade code did: an instant bound as timestamptz, which
    /// PostgreSQL casts down to a wall clock using the session time zone of this connection.
    /// </summary>
    private async Task SeedStoredExpirationAsync(string subject, DateTimeOffset? expiration)
    {
        await using NpgsqlConnection connection = new(_connectionString);
        await connection.OpenAsync();

        await connection.ExecuteAsync(
            """
            INSERT INTO "dmscs"."OpenIddictToken" ("Id", "Subject", "ExpirationDate")
            VALUES (@Id, @Subject, @Expiration)
            """,
            new
            {
                Id = Guid.NewGuid(),
                Subject = subject,
                Expiration = expiration,
            }
        );
    }

    private async Task<DateTime?> UpgradedExpirationAsync(string subject)
    {
        await using NpgsqlConnection connection = new(_connectionString);
        await connection.OpenAsync();

        return await connection.QuerySingleAsync<DateTime?>(
            """SELECT "ExpirationDate" AT TIME ZONE 'UTC' FROM "dmscs"."OpenIddictToken" WHERE "Subject" = @Subject;""",
            new { Subject = subject }
        );
    }

    private async Task<string> UpgradedColumnTypeAsync()
    {
        await using NpgsqlConnection connection = new(_connectionString);
        await connection.OpenAsync();

        return await connection.QuerySingleAsync<string>(
            """
            SELECT data_type
            FROM information_schema.columns
            WHERE table_schema = 'dmscs'
              AND table_name = 'OpenIddictToken'
              AND column_name = 'ExpirationDate';
            """
        );
    }

    /// <summary>
    /// Runs the shipped script body a second time against the already-upgraded database, which is
    /// what the database shape test's script replay does. The guard should make this a no-op.
    /// </summary>
    private async Task ReplayUpgradeScriptAsync()
    {
        string scriptName = typeof(Deploy.DatabaseDeploy)
            .Assembly.GetManifestResourceNames()
            .Single(name =>
                name.Contains("0032_Alter_OpenIddictToken_ExpirationDate_TimeZone", StringComparison.Ordinal)
            );

        await using Stream stream =
            typeof(Deploy.DatabaseDeploy).Assembly.GetManifestResourceStream(scriptName)
            ?? throw new InvalidOperationException($"Embedded deploy script not found: {scriptName}");
        using StreamReader reader = new(stream);
        string script = await reader.ReadToEndAsync();

        await using NpgsqlConnection connection = new(_connectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync(script);
    }

    private string CreateIsolatedConnectionString(string timeZone)
    {
        _databaseName = $"dms1430_upgrade_{Guid.NewGuid():N}";
        return CreateConnectionString(_databaseName, timeZone);
    }

    private static string CreateConnectionString(string databaseName, string timeZone) =>
        new NpgsqlConnectionStringBuilder(Configuration.DatabaseOptions.Value.DatabaseConnection)
        {
            Database = databaseName,
            Timezone = timeZone,
            Pooling = false,
        }.ConnectionString;

    private static string CreateMaintenanceConnectionString() =>
        new NpgsqlConnectionStringBuilder(Configuration.DatabaseOptions.Value.DatabaseConnection)
        {
            Database = "postgres",
            Pooling = false,
        }.ConnectionString;

    private static void DeploySuccessfully(string connectionString)
    {
        DatabaseDeployResult result = new Deploy.DatabaseDeploy().DeployDatabase(connectionString);

        if (result is DatabaseDeployResult.DatabaseDeployFailure failure)
        {
            Assert.Fail($"Database deploy failed: {failure.Error}");
        }
    }
}
