// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Npgsql;

namespace EdFi.DataManagementService.Tests.E2E.Management;

public abstract class ContainerSetupBase
{
    private const string PgAdminUser = "postgres";
    private const string PgAdminPassword = "abcdefgh1!";

    private const ushort DbPortExternal = 5435;
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
        var hostConnectionString = BuildHostConnectionString(AppSettings.DataStoreDatabaseName);
        using var conn = new NpgsqlConnection(hostConnectionString);
        await conn.OpenAsync();

        var resetCommand = new NpgsqlCommand(_resetSql, conn);
        await resetCommand.ExecuteNonQueryAsync();
    }

    private static string BuildHostConnectionString(string databaseName)
    {
        return $"host=localhost;port={DbPortExternal};username={PgAdminUser};password={PgAdminPassword};database={databaseName};NoResetOnClose=true;";
    }
}
