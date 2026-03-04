// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace EdFi.DataManagementService.SchemaTools.Provisioning;

/// <summary>
/// PostgreSQL implementation of <see cref="IDatabaseProvisioner"/>.
/// Uses Npgsql for all database connectivity.
/// </summary>
public class PgsqlDatabaseProvisioner(ILogger logger) : IDatabaseProvisioner
{
    public string GetDatabaseName(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        return string.IsNullOrWhiteSpace(builder.Database)
            ? throw new InvalidOperationException("Connection string does not specify a database name.")
            : builder.Database;
    }

    public bool CreateDatabaseIfNotExists(string connectionString)
    {
        var targetDatabase = GetDatabaseName(connectionString);

        var builder = new NpgsqlConnectionStringBuilder(connectionString);

        logger.LogInformation(
            "Checking if database exists: {DatabaseName}",
            LoggingSanitizer.SanitizeForLogging(targetDatabase)
        );

        // Connect to the admin database to create the target database
        builder.Database = "postgres";
        var adminConnectionString = builder.ConnectionString;

        using var connection = new NpgsqlConnection(adminConnectionString);
        connection.Open();

        // Check if the database already exists
        using var checkCommand = connection.CreateCommand();
        checkCommand.CommandText = "SELECT 1 FROM pg_database WHERE datname = @dbName";
        checkCommand.Parameters.AddWithValue("@dbName", targetDatabase);

        var exists = checkCommand.ExecuteScalar() is not null;

        if (exists)
        {
            logger.LogInformation(
                "Database already exists: {DatabaseName}",
                LoggingSanitizer.SanitizeForLogging(targetDatabase)
            );
            return false;
        }

        // CREATE DATABASE cannot run inside a transaction in PostgreSQL.
        // Without an explicit BeginTransaction(), Npgsql executes in autocommit mode.
        // Use a quoted identifier to safely handle the database name.
        logger.LogInformation(
            "Creating database: {DatabaseName}",
            LoggingSanitizer.SanitizeForLogging(targetDatabase)
        );

        using var createCommand = connection.CreateCommand();
        var quotedName = $"\"{targetDatabase.Replace("\"", "\"\"")}\"";
        createCommand.CommandText = $"CREATE DATABASE {quotedName}";

        try
        {
            createCommand.ExecuteNonQuery();
        }
        catch (PostgresException ex) when (ex.SqlState == "42P04")
        {
            // 42P04 = "duplicate_database" — a concurrent process created it
            // between our check and our CREATE. Treat as "already existed".
            logger.LogInformation(
                ex,
                "Database was created concurrently by another process: {DatabaseName}",
                LoggingSanitizer.SanitizeForLogging(targetDatabase)
            );
            return false;
        }

        logger.LogInformation(
            "Database created successfully: {DatabaseName}",
            LoggingSanitizer.SanitizeForLogging(targetDatabase)
        );

        return true;
    }

    public void ExecuteInTransaction(string connectionString, string sql, int commandTimeoutSeconds = 300)
    {
        var targetDatabase = GetDatabaseName(connectionString);

        logger.LogInformation(
            "Executing DDL in transaction against database: {DatabaseName}",
            LoggingSanitizer.SanitizeForLogging(targetDatabase)
        );

        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();

        // The entire script is sent as a single command, so commandTimeoutSeconds
        // bounds the total execution time (unlike MSSQL which applies it per batch).
        using var transaction = connection.BeginTransaction();
        try
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.CommandTimeout = commandTimeoutSeconds;
            command.ExecuteNonQuery();

            transaction.Commit();

            logger.LogInformation(
                "DDL executed successfully against database: {DatabaseName}",
                LoggingSanitizer.SanitizeForLogging(targetDatabase)
            );
        }
        catch
        {
            try
            {
                transaction.Rollback();
            }
            catch (Exception rollbackEx)
            {
                logger.LogError(
                    rollbackEx,
                    "Failed to roll back transaction for database: {DatabaseName}",
                    LoggingSanitizer.SanitizeForLogging(targetDatabase)
                );
            }

            throw;
        }
    }

    public void PreflightSchemaHashCheck(string connectionString, string expectedHash)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();

        // Check if the dms.EffectiveSchema table exists
        using var existsCommand = connection.CreateCommand();
        existsCommand.CommandText =
            "SELECT 1 FROM information_schema.tables WHERE table_schema = 'dms' AND table_name = 'EffectiveSchema'";
        if (existsCommand.ExecuteScalar() is null)
        {
            return; // New database — no table yet, proceed with provisioning
        }

        // Table exists — check the stored hash
        using var hashCommand = connection.CreateCommand();
        hashCommand.CommandText =
            """SELECT "EffectiveSchemaHash" FROM dms."EffectiveSchema" WHERE "EffectiveSchemaSingletonId" = 1""";
        var storedHash = hashCommand.ExecuteScalar() as string;

        SchemaHashChecker.ValidateOrThrow(storedHash, expectedHash, logger);
    }

    public void PreflightSeedValidation(string connectionString, EffectiveSchemaInfo expectedSchema)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();

        // Check if the dms.EffectiveSchema table exists — if not, this is a fresh database
        using var existsCommand = connection.CreateCommand();
        existsCommand.CommandText =
            "SELECT 1 FROM information_schema.tables WHERE table_schema = 'dms' AND table_name = 'EffectiveSchema'";
        if (existsCommand.ExecuteScalar() is null)
        {
            return;
        }

        // Read the current EffectiveSchemaHash to scope the SchemaComponent query
        using var hashCommand = connection.CreateCommand();
        hashCommand.CommandText =
            """SELECT "EffectiveSchemaHash" FROM dms."EffectiveSchema" WHERE "EffectiveSchemaSingletonId" = 1""";
        var currentHash = hashCommand.ExecuteScalar() as string;

        // If no row exists in EffectiveSchema, treat as a fresh provisioning run
        if (currentHash is null)
        {
            return;
        }

        // Guard: ensure the stored hash matches the expected hash.
        // PreflightSchemaHashCheck should have already caught mismatches, but this
        // makes PreflightSeedValidation self-contained in case the call order changes.
        if (!string.Equals(currentHash, expectedSchema.EffectiveSchemaHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Schema hash mismatch in PreflightSeedValidation: stored hash does not match expected hash."
            );
        }

        // --- Validate ResourceKey rows ---
        var actualResourceKeys = new List<ResourceKeyRow>();
        using (var rkCommand = connection.CreateCommand())
        {
            rkCommand.CommandText =
                @"SELECT ""ResourceKeyId"", ""ProjectName"", ""ResourceName"", ""ResourceVersion"" FROM dms.""ResourceKey"" ORDER BY ""ResourceKeyId""";
            using var reader = rkCommand.ExecuteReader();
            while (reader.Read())
            {
                actualResourceKeys.Add(
                    new ResourceKeyRow(
                        reader.GetInt16(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetString(3)
                    )
                );
            }
        }

        SeedValidator.ValidateResourceKeysOrThrow(
            actualResourceKeys,
            expectedSchema.ResourceKeysInIdOrder,
            logger
        );

        // --- Validate SchemaComponent rows ---
        var actualSchemaComponents = new List<SchemaComponentRow>();
        using (var scCommand = connection.CreateCommand())
        {
            scCommand.CommandText =
                @"SELECT ""ProjectEndpointName"", ""ProjectName"", ""ProjectVersion"", ""IsExtensionProject"" FROM dms.""SchemaComponent"" WHERE ""EffectiveSchemaHash"" = @hash ORDER BY ""ProjectEndpointName""";
            var param = scCommand.CreateParameter();
            param.ParameterName = "@hash";
            param.Value = currentHash;
            scCommand.Parameters.Add(param);
            using var reader = scCommand.ExecuteReader();
            while (reader.Read())
            {
                actualSchemaComponents.Add(
                    new SchemaComponentRow(
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetBoolean(3)
                    )
                );
            }
        }

        SeedValidator.ValidateSchemaComponentsOrThrow(
            actualSchemaComponents,
            expectedSchema.SchemaComponentsInEndpointOrder,
            logger
        );
    }

    /// <summary>
    /// No-op for PostgreSQL. MVCC is the default isolation behavior.
    /// </summary>
    public void CheckOrConfigureMvcc(string connectionString, bool databaseWasCreated)
    {
        // PostgreSQL uses MVCC natively — no configuration needed.
    }
}
