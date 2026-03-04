// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.RegularExpressions;
using EdFi.DataManagementService.Backend.External;
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
        var targetDatabase = GetDatabaseName(connectionString);

        var builder = new SqlConnectionStringBuilder(connectionString);

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

        var exists = checkCommand.ExecuteScalar() is not null;

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

        try
        {
            createCommand.ExecuteNonQuery();
        }
        catch (SqlException ex) when (ex.Number == 1801)
        {
            // 1801 = "Database already exists" — a concurrent process created it
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

        // Split on GO batch separators. GO is not valid T-SQL; it is a sqlcmd/SSMS
        // directive. The emitted DDL uses GO to separate batches required by
        // CREATE OR ALTER statements for triggers and views.
        // NOTE: commandTimeoutSeconds applies to each batch individually, so total
        // execution time may exceed the timeout when the script contains many batches.
        var batches = SplitOnGoBatchSeparator(sql);

        using var connection = new SqlConnection(connectionString);
        connection.Open();

        var batchList = batches.ToList();
        var currentBatch = 0;

        using var transaction = connection.BeginTransaction();
        try
        {
            for (currentBatch = 0; currentBatch < batchList.Count; currentBatch++)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = batchList[currentBatch];
                command.CommandTimeout = commandTimeoutSeconds;
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }
        catch (SqlException ex)
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

            throw new InvalidOperationException(
                $"DDL batch {currentBatch + 1} of {batchList.Count} failed for database "
                    + $"'{LoggingSanitizer.SanitizeForLogging(targetDatabase)}'",
                ex
            );
        }

        // Clear the connection pool so pooled connections are not reused with
        // stale session state after DDL schema changes.
        SqlConnection.ClearPool(connection);

        logger.LogInformation(
            "DDL executed successfully against database: {DatabaseName}",
            LoggingSanitizer.SanitizeForLogging(targetDatabase)
        );
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

    public void PreflightSchemaHashCheck(string connectionString, string expectedHash)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();

        // Check if the dms.EffectiveSchema table exists
        using var existsCommand = connection.CreateCommand();
        existsCommand.CommandText =
            "SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = 'dms' AND TABLE_NAME = 'EffectiveSchema'";
        if (existsCommand.ExecuteScalar() is null)
        {
            return; // New database — no table yet, proceed with provisioning
        }

        // Table exists — check the stored hash
        using var hashCommand = connection.CreateCommand();
        hashCommand.CommandText =
            """SELECT [EffectiveSchemaHash] FROM [dms].[EffectiveSchema] WHERE [EffectiveSchemaSingletonId] = 1""";
        var storedHash = hashCommand.ExecuteScalar() as string;

        SchemaHashChecker.ValidateOrThrow(storedHash, expectedHash, logger);
    }

    public void PreflightSeedValidation(string connectionString, EffectiveSchemaInfo expectedSchema)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();

        // Check if the dms.EffectiveSchema table exists — if not, this is a fresh database
        using var existsCommand = connection.CreateCommand();
        existsCommand.CommandText =
            "SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = 'dms' AND TABLE_NAME = 'EffectiveSchema'";
        if (existsCommand.ExecuteScalar() is null)
        {
            return;
        }

        // Read the current EffectiveSchemaHash to scope the SchemaComponent query
        using var hashCommand = connection.CreateCommand();
        hashCommand.CommandText =
            """SELECT [EffectiveSchemaHash] FROM [dms].[EffectiveSchema] WHERE [EffectiveSchemaSingletonId] = 1""";
        var currentHash = hashCommand.ExecuteScalar() as string;

        // If no row exists in EffectiveSchema, treat as a fresh provisioning run
        if (currentHash is null)
        {
            return;
        }

        // --- Validate ResourceKey rows ---
        var actualResourceKeys = new List<ResourceKeyRow>();
        using (var rkCommand = connection.CreateCommand())
        {
            rkCommand.CommandText =
                @"SELECT [ResourceKeyId], [ProjectName], [ResourceName], [ResourceVersion] FROM [dms].[ResourceKey] ORDER BY [ResourceKeyId]";
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
                @"SELECT [ProjectEndpointName], [ProjectName], [ProjectVersion], [IsExtensionProject] FROM [dms].[SchemaComponent] WHERE [EffectiveSchemaHash] = @hash ORDER BY [ProjectEndpointName]";
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

    public void CheckOrConfigureMvcc(string connectionString, bool databaseWasCreated)
    {
        var targetDatabase = GetDatabaseName(connectionString);

        // MVCC commands must run on master, outside a transaction
        var builder = new SqlConnectionStringBuilder(connectionString);
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
            rcsiCommand.CommandText = $"ALTER DATABASE {quotedName} SET READ_COMMITTED_SNAPSHOT ON";
            rcsiCommand.ExecuteNonQuery();

            using var snapshotCommand = connection.CreateCommand();
            snapshotCommand.CommandText = $"ALTER DATABASE {quotedName} SET ALLOW_SNAPSHOT_ISOLATION ON";
            snapshotCommand.ExecuteNonQuery();

            logger.LogInformation(
                "MVCC isolation configured for database: {DatabaseName}",
                LoggingSanitizer.SanitizeForLogging(targetDatabase)
            );
        }
        else
        {
            // For existing databases, only READ_COMMITTED_SNAPSHOT is checked.
            // ALLOW_SNAPSHOT_ISOLATION is optional per the design doc — it enables
            // explicit snapshot transactions but is not required for DMS's default
            // read-committed MVCC behavior. The newly-created path enables both as
            // a convenience, but we only warn about RCSI for pre-existing databases.
            using var checkCommand = connection.CreateCommand();
            checkCommand.CommandText =
                "SELECT is_read_committed_snapshot_on FROM sys.databases WHERE name = @dbName";
            checkCommand.Parameters.AddWithValue("@dbName", targetDatabase);

            var result = checkCommand.ExecuteScalar();
            if (result is null)
            {
                throw new InvalidOperationException(
                    $"Database '{LoggingSanitizer.SanitizeForConsole(targetDatabase)}' does not exist. "
                        + "Use the --create-database flag to create it automatically."
                );
            }

            if (!Convert.ToBoolean(result))
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
