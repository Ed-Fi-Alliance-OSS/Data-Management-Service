// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.RegularExpressions;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.SchemaTools.Provisioning;

/// <summary>
/// SQL Server implementation of <see cref="IDatabaseProvisioner"/>.
/// Uses Microsoft.Data.SqlClient for all database connectivity.
/// </summary>
public partial class MssqlDatabaseProvisioner(ILogger logger) : IDatabaseProvisioner
{
    public string GetDatabaseName(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        return string.IsNullOrWhiteSpace(builder.InitialCatalog)
            ? throw new InvalidOperationException(
                "Connection string does not specify a database name (Initial Catalog)."
            )
            : builder.InitialCatalog;
    }

    public bool CreateDatabaseIfNotExists(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        var targetDatabase = string.IsNullOrWhiteSpace(builder.InitialCatalog)
            ? throw new InvalidOperationException(
                "Connection string does not specify a database name (Initial Catalog)."
            )
            : builder.InitialCatalog;

        logger.LogInformation(
            "Checking if database exists: {DatabaseName}",
            LoggingSanitizer.SanitizeForLogging(targetDatabase)
        );

        // Connect to the master database to create the target database
        builder.InitialCatalog = "master";
        var adminConnectionString = builder.ConnectionString;

        using var connection = new SqlConnection(adminConnectionString);
        connection.Open();

        // Check if the database already exists
        using var checkCommand = connection.CreateCommand();
        checkCommand.CommandText = "SELECT 1 FROM sys.databases WHERE name = @dbName";
        checkCommand.Parameters.AddWithValue("@dbName", targetDatabase);

        var exists = checkCommand.ExecuteScalar() != null;

        if (exists)
        {
            logger.LogInformation(
                "Database already exists: {DatabaseName}",
                LoggingSanitizer.SanitizeForLogging(targetDatabase)
            );
            return false;
        }

        logger.LogInformation(
            "Creating database: {DatabaseName}",
            LoggingSanitizer.SanitizeForLogging(targetDatabase)
        );

        // Use a quoted identifier to safely handle the database name
        using var createCommand = connection.CreateCommand();
        var quotedName = $"[{targetDatabase.Replace("]", "]]")}]";
        createCommand.CommandText = $"CREATE DATABASE {quotedName}";
        createCommand.ExecuteNonQuery();

        logger.LogInformation(
            "Database created successfully: {DatabaseName}",
            LoggingSanitizer.SanitizeForLogging(targetDatabase)
        );

        return true;
    }

    public void ExecuteInTransaction(string connectionString, string sql)
    {
        var targetDatabase = GetDatabaseName(connectionString);

        logger.LogInformation(
            "Executing DDL in transaction against database: {DatabaseName}",
            LoggingSanitizer.SanitizeForLogging(targetDatabase)
        );

        // Split on GO batch separators. GO is not valid T-SQL; it is a sqlcmd/SSMS
        // directive. The emitted DDL uses GO to separate batches required by
        // CREATE OR ALTER statements for triggers and views.
        var batches = SplitOnGoBatchSeparator(sql);

        using var connection = new SqlConnection(connectionString);
        connection.Open();

        using var transaction = connection.BeginTransaction();
        try
        {
            foreach (var batch in batches)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = batch;
                command.CommandTimeout = 300; // 5 minutes for large DDL scripts
                command.ExecuteNonQuery();
            }

            transaction.Commit();

            // Clear the connection pool so pooled connections to the target database
            // do not block subsequent ALTER DATABASE statements (e.g., MVCC configuration).
            SqlConnection.ClearPool(connection);

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

    /// <summary>
    /// Splits a SQL script on standalone GO batch separators, filtering out empty batches.
    /// GO is not valid T-SQL; it is a sqlcmd/SSMS directive that separates batches.
    /// </summary>
    public static IEnumerable<string> SplitOnGoBatchSeparator(string sql)
    {
        return GoBatchSeparatorPattern()
            .Split(sql)
            .Select(batch => batch.Trim())
            .Where(batch => batch.Length > 0);
    }

    [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex GoBatchSeparatorPattern();

    public void CheckOrConfigureMvcc(string connectionString, bool databaseWasCreated)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        var targetDatabase = string.IsNullOrWhiteSpace(builder.InitialCatalog)
            ? throw new InvalidOperationException(
                "Connection string does not specify a database name (Initial Catalog)."
            )
            : builder.InitialCatalog;

        // MVCC commands must run on master, outside a transaction
        builder.InitialCatalog = "master";
        var adminConnectionString = builder.ConnectionString;

        using var connection = new SqlConnection(adminConnectionString);
        connection.Open();

        if (databaseWasCreated)
        {
            var quotedName = $"[{targetDatabase.Replace("]", "]]")}]";

            // Enable MVCC settings on the newly created database
            logger.LogInformation(
                "Configuring MVCC isolation for new database: {DatabaseName}",
                LoggingSanitizer.SanitizeForLogging(targetDatabase)
            );

            using var rcsiCommand = connection.CreateCommand();
            rcsiCommand.CommandText =
                $"ALTER DATABASE {quotedName} SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE";
            rcsiCommand.ExecuteNonQuery();

            using var snapshotCommand = connection.CreateCommand();
            snapshotCommand.CommandText =
                $"ALTER DATABASE {quotedName} SET ALLOW_SNAPSHOT_ISOLATION ON WITH ROLLBACK IMMEDIATE";
            snapshotCommand.ExecuteNonQuery();

            logger.LogInformation(
                "MVCC isolation configured for database: {DatabaseName}",
                LoggingSanitizer.SanitizeForLogging(targetDatabase)
            );
        }
        else
        {
            // Check current MVCC settings and warn if not enabled
            using var checkCommand = connection.CreateCommand();
            checkCommand.CommandText =
                "SELECT is_read_committed_snapshot_on FROM sys.databases WHERE name = @dbName";
            checkCommand.Parameters.AddWithValue("@dbName", targetDatabase);

            var result = checkCommand.ExecuteScalar();
            if (result is null)
            {
                logger.LogError(
                    "Database not found in sys.databases: {DatabaseName}",
                    LoggingSanitizer.SanitizeForLogging(targetDatabase)
                );
            }
            else if (result is bool rcsiEnabled && !rcsiEnabled)
            {
                var warning =
                    $"READ_COMMITTED_SNAPSHOT is OFF for database '{LoggingSanitizer.SanitizeForConsole(targetDatabase)}'. "
                    + "DMS strongly recommends enabling MVCC reads for correct concurrency behavior. "
                    + "Run: ALTER DATABASE [dbname] SET READ_COMMITTED_SNAPSHOT ON";

                logger.LogWarning(
                    "READ_COMMITTED_SNAPSHOT is OFF for database: {DatabaseName}",
                    LoggingSanitizer.SanitizeForLogging(targetDatabase)
                );
                Console.Error.WriteLine($"Warning: {warning}");
            }
        }
    }
}
