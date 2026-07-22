// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace EdFi.DataManagementService.Tests.E2E.Management;

public abstract class ContainerSetupBase
{
    private const string PgAdminUser = "postgres";
    private const string PgAdminPassword = "abcdefgh1!";

    private const ushort DbPortExternal = 5435;

    // The three DMS metadata tables preserved (not truncated) across an E2E reset on both engines.
    private static readonly (string Schema, string Table)[] _mssqlExcludedTables =
    [
        ("dms", "EffectiveSchema"),
        ("dms", "ResourceKey"),
        ("dms", "SchemaComponent"),
    ];
    private static readonly string _resetSql = """
        DO $$
        DECLARE
            truncate_sql text;
            sequence_sql text;
        BEGIN
            SELECT
                CASE
                    WHEN COUNT(*) = 0 THEN NULL
                    ELSE
                        'TRUNCATE TABLE '
                        || string_agg(
                            format('%I.%I', schemaname, tablename),
                            ', '
                            ORDER BY schemaname, tablename
                        )
                        || ' RESTART IDENTITY CASCADE;'
                END
            INTO truncate_sql
            FROM pg_tables
            WHERE schemaname <> 'information_schema'
              AND schemaname !~ '^pg_'
              AND NOT (
                  schemaname = 'dms'
                  AND tablename = ANY (ARRAY['EffectiveSchema', 'ResourceKey', 'SchemaComponent'])
              );

            IF truncate_sql IS NOT NULL THEN
                EXECUTE truncate_sql;
            END IF;

            FOR sequence_sql IN
                SELECT format(
                    'ALTER SEQUENCE %I.%I RESTART WITH %s',
                    schemaname,
                    sequencename,
                    start_value
                )
                FROM pg_sequences
                WHERE schemaname <> 'information_schema'
                  AND schemaname !~ '^pg_'
                ORDER BY schemaname, sequencename
            LOOP
                EXECUTE sequence_sql;
            END LOOP;
        END
        $$;
        """;

    public abstract Task ResetData();

    public abstract string ApiUrl();

    public static async Task ResetDatabase()
    {
        // Resolve the host-side reset connection per engine: PostgreSQL preserves the existing
        // localhost Npgsql connection; MSSQL uses the opaque admin/reset connection string the build
        // orchestration resolved from the environment and set for the test process.
        string connectionString = IsMssql(AppSettings.DatabaseEngine)
            ? AppSettings.DataStoreAdminConnectionString
            : BuildHostConnectionString(AppSettings.DataStoreDatabaseName);

        await ResetDatabaseAsync(AppSettings.DatabaseEngine, connectionString, ExecuteResetAsync);
    }

    // Test seam: build the engine-specific reset plan and hand it to an executor. Kept internal so
    // unit tests can assert branch selection and that only the selected provider path is produced,
    // without opening a real database connection.
    internal static async Task ResetDatabaseAsync(
        string databaseEngine,
        string connectionString,
        Func<DatabaseResetPlan, Task> executeReset
    )
    {
        DatabaseResetPlan plan = BuildResetPlan(databaseEngine, connectionString);
        await executeReset(plan);
    }

    internal static DatabaseResetPlan BuildResetPlan(string databaseEngine, string connectionString)
    {
        if (IsMssql(databaseEngine))
        {
            // Reuse the tested SQL Server reset (disables triggers/constraints, deletes, reseeds
            // identities, restarts sequences, and re-enables constraints/triggers even on error),
            // preserving exactly the three DMS metadata tables. The SQL is not duplicated here.
            return new DatabaseResetPlan(
                DatabaseResetProvider.SqlServer,
                connectionString,
                MssqlDatabaseResetSql.Build(_mssqlExcludedTables)
            );
        }

        return new DatabaseResetPlan(DatabaseResetProvider.Postgres, connectionString, _resetSql);
    }

    private static async Task ExecuteResetAsync(DatabaseResetPlan plan)
    {
        if (plan.Provider == DatabaseResetProvider.SqlServer)
        {
            using var connection = new SqlConnection(plan.ConnectionString);
            await connection.OpenAsync();
            using var command = new SqlCommand(plan.Sql, connection);
            await command.ExecuteNonQueryAsync();
        }
        else
        {
            using var connection = new NpgsqlConnection(plan.ConnectionString);
            await connection.OpenAsync();
            using var command = new NpgsqlCommand(plan.Sql, connection);
            await command.ExecuteNonQueryAsync();
        }
    }

    private static bool IsMssql(string databaseEngine) =>
        string.Equals(databaseEngine, "mssql", StringComparison.OrdinalIgnoreCase);

    private static string BuildHostConnectionString(string databaseName)
    {
        return $"host=localhost;port={DbPortExternal};username={PgAdminUser};password={PgAdminPassword};database={databaseName};NoResetOnClose=true;";
    }
}

internal enum DatabaseResetProvider
{
    Postgres,
    SqlServer,
}

internal sealed record DatabaseResetPlan(DatabaseResetProvider Provider, string ConnectionString, string Sql);
