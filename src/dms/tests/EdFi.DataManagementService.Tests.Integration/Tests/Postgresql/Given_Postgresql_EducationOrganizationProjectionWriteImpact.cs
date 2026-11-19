// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Postgresql;
using EdFi.DataManagementService.Tests.Integration.Scenarios;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Postgresql;

/// <summary>
/// The projection's provider read against production writes on PostgreSQL. PostgreSQL keeps no
/// cumulative lock-wait statistic, so waits are sampled; deadlocks come from
/// <c>pg_stat_database</c>, and their details only from the server log.
/// </summary>
public sealed class Given_Postgresql_EducationOrganizationProjectionWriteImpact
    : PostgresqlApiIntegrationTestBase
{
    // Only the projection statement aliases a column this way.
    private const string ReaderMarker = "%\"StateEducationAgencyReference\"%";

    protected override FixtureKey Fixture => FixtureKey.AuthoritativeDs52;

    [Test]
    [Explicit(
        "Projection provider measurement under production writes; run on demand and record the output."
    )]
    [Category("ProjectionMeasurement")]
    public Task It_measures_provider_reads_against_production_writes() =>
        EducationOrganizationProjectionWriteImpactScenario.MeasureAsync(
            Harness,
            PrimaryConnectionString,
            new EducationOrganizationProjectionWriteImpactScenario.Engine(
                Name: "postgresql",
                Dialect: SqlDialect.Pgsql,
                EffectiveSchemaHashAsync: () =>
                    ScalarAsync<string>("""SELECT "EffectiveSchemaHash" FROM "dms"."EffectiveSchema";"""),
                DeadlocksAsync: async () =>
                {
                    // Statistics reach pg_stat_database when a backend reports them, at most about a
                    // second later.
                    await Task.Delay(TimeSpan.FromSeconds(2));
                    return await ScalarAsync<long>(
                        "SELECT deadlocks FROM pg_stat_database WHERE datname = current_database();"
                    );
                },
                LockWaitTotalsAsync: null,
                SampleLockWaitersAsync: SampleLockWaitersAsync,
                StartDeadlockGraphsAsync: null,
                FinishDeadlockGraphsAsync: null
            )
        );

    private async Task<(int Writers, int Reader)> SampleLockWaitersAsync()
    {
        await using var connection = new NpgsqlConnection(PrimaryConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT count(*) FILTER (WHERE query NOT LIKE @reader)::int,
                   count(*) FILTER (WHERE query LIKE @reader)::int
            FROM pg_stat_activity
            WHERE datname = current_database() AND wait_event_type = 'Lock';
            """,
            connection
        );
        command.Parameters.AddWithValue("reader", ReaderMarker);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetInt32(0), reader.GetInt32(1));
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(PrimaryConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)Convert.ChangeType((await command.ExecuteScalarAsync())!, typeof(T));
    }
}
