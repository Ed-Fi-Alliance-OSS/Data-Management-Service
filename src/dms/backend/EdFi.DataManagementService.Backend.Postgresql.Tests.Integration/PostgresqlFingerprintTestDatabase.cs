// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Npgsql;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

public sealed class PostgresqlFingerprintTestDatabase : IAsyncDisposable
{
    private const string ProvisioningSql = """
        CREATE SCHEMA IF NOT EXISTS "dms";

        CREATE TABLE IF NOT EXISTS "dms"."EffectiveSchema"
        (
            "EffectiveSchemaSingletonId" smallint NOT NULL,
            "ApiSchemaFormatVersion" varchar(64) NOT NULL,
            "EffectiveSchemaHash" varchar(64) NOT NULL,
            "ResourceKeyCount" smallint NOT NULL,
            "ResourceKeySeedHash" bytea NOT NULL,
            "AppliedAt" timestamp with time zone NOT NULL DEFAULT now(),
            CONSTRAINT "PK_EffectiveSchema" PRIMARY KEY ("EffectiveSchemaSingletonId")
        );
        """;

    private PostgresqlFingerprintTestDatabase(string databaseName, string connectionString)
    {
        DatabaseName = databaseName;
        ConnectionString = connectionString;
    }

    public string DatabaseName { get; }

    public string ConnectionString { get; }

    public static async Task<PostgresqlFingerprintTestDatabase> CreateProvisionedAsync()
    {
        var databaseName = PostgresqlTestDatabaseHelper.GenerateUniqueDatabaseName();
        var connectionString = PostgresqlTestDatabaseHelper.BuildConnectionString(databaseName);

        PostgresqlTestDatabaseHelper.CreateDatabase(databaseName);

        try
        {
            await ExecuteNonQueryAsync(connectionString, ProvisioningSql);
            return new(databaseName, connectionString);
        }
        catch
        {
            PostgresqlTestDatabaseHelper.DropDatabaseIfExists(databaseName);
            throw;
        }
    }

    public async Task ResetAsync()
    {
        await ExecuteNonQueryAsync("""DELETE FROM "dms"."EffectiveSchema";""");
    }

    public ValueTask DisposeAsync()
    {
        PostgresqlTestDatabaseHelper.DropDatabaseIfExists(DatabaseName);
        return ValueTask.CompletedTask;
    }

    private Task ExecuteNonQueryAsync(string sql)
    {
        return ExecuteNonQueryAsync(ConnectionString, sql);
    }

    private static async Task ExecuteNonQueryAsync(string connectionString, string sql)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
