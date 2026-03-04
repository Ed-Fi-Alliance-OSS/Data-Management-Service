// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

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

    /// <summary>
    /// No-op for PostgreSQL. MVCC is the default isolation behavior.
    /// </summary>
    public void CheckOrConfigureMvcc(string connectionString, bool databaseWasCreated)
    {
        // PostgreSQL uses MVCC natively — no configuration needed.
    }
}
