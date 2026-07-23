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
        // Both engines consume the opaque host-side admin/reset connection string the build
        // orchestration resolved once from the selected environment (custom credentials, published
        // port, and database, plus NoResetOnClose=true for PostgreSQL). PostgreSQL keeps Npgsql and the
        // existing DO $$ reset SQL; MSSQL uses SqlClient and the shared MssqlDatabaseResetSql. The C#
        // harness never re-derives ports or credentials. The reset TRUNCATEs every non-metadata table,
        // so the admin connection string is first proven to target the configured E2E database.
        await ResetDatabaseAsync(
            AppSettings.DatabaseEngine,
            AppSettings.DataStoreAdminConnectionString,
            AppSettings.DataStoreDatabaseName,
            ExecuteResetAsync
        );
    }

    // Test seam: build the engine-specific reset plan and hand it to an executor. Kept internal so
    // unit tests can assert branch selection and that only the selected provider path is produced,
    // without opening a real database connection.
    internal static async Task ResetDatabaseAsync(
        string databaseEngine,
        string connectionString,
        string expectedDatabaseName,
        Func<DatabaseResetPlan, Task> executeReset
    )
    {
        DatabaseResetPlan plan = BuildResetPlan(databaseEngine, connectionString, expectedDatabaseName);
        await executeReset(plan);
    }

    internal static DatabaseResetPlan BuildResetPlan(
        string databaseEngine,
        string connectionString,
        string expectedDatabaseName
    )
    {
        // Fail closed before producing any reset plan: the reset TRUNCATEs every non-metadata table in
        // the target database, so a misconfigured admin connection string (empty, malformed, or a
        // different database) must never reach a truncate against a primary DMS/CMS database.
        RequireResetTargetsExpectedDatabase(databaseEngine, connectionString, expectedDatabaseName);

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

    // Proves the admin/reset connection string targets exactly the configured E2E database. Parses the
    // database out of the provider-specific connection string (InitialCatalog for SQL Server, Database
    // for PostgreSQL) and refuses on an unconfigured expected name, an empty/malformed connection
    // string, or a database that differs from the configured E2E database. Error messages carry only
    // database names, never the connection string or any credential.
    internal static void RequireResetTargetsExpectedDatabase(
        string databaseEngine,
        string connectionString,
        string expectedDatabaseName
    )
    {
        if (string.IsNullOrWhiteSpace(expectedDatabaseName))
        {
            throw new InvalidOperationException(
                "E2E database reset refused: the configured E2E data-store database name is empty."
            );
        }

        string targetDatabase = IsMssql(databaseEngine)
            ? ParseSqlServerDatabase(connectionString)
            : ParsePostgresDatabase(connectionString);

        if (string.IsNullOrWhiteSpace(targetDatabase))
        {
            throw new InvalidOperationException(
                "E2E database reset refused: the admin connection string does not specify a database."
            );
        }

        if (!string.Equals(targetDatabase, expectedDatabaseName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"E2E database reset refused: the admin connection string targets database '{targetDatabase}', "
                    + $"not the configured E2E database '{expectedDatabaseName}'."
            );
        }
    }

    private static string ParseSqlServerDatabase(string connectionString)
    {
        try
        {
            return new SqlConnectionStringBuilder(connectionString).InitialCatalog;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            throw new InvalidOperationException(
                "E2E database reset refused: the SQL Server admin connection string is malformed."
            );
        }
    }

    private static string ParsePostgresDatabase(string connectionString)
    {
        try
        {
            return new NpgsqlConnectionStringBuilder(connectionString).Database ?? string.Empty;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            throw new InvalidOperationException(
                "E2E database reset refused: the PostgreSQL admin connection string is malformed."
            );
        }
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
}

internal enum DatabaseResetProvider
{
    Postgres,
    SqlServer,
}

internal sealed record DatabaseResetPlan(DatabaseResetProvider Provider, string ConnectionString, string Sql);
