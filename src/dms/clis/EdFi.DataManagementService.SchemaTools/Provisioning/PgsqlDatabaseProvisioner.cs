// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Globalization;
using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace EdFi.DataManagementService.SchemaTools.Provisioning;

/// <summary>
/// PostgreSQL implementation of <see cref="IDatabaseProvisioner"/>.
/// Uses Npgsql for all database connectivity.
/// </summary>
public class PgsqlDatabaseProvisioner(ILogger logger) : DatabaseProvisionerBase(logger)
{
    private static readonly DialectSql _dialect = new(
        EffectiveSchemaTableExistsSql: "SELECT 1 FROM information_schema.tables WHERE table_schema = 'dms' AND table_name = 'EffectiveSchema'",
        SeedTableCheckSql: "SELECT table_name FROM information_schema.tables WHERE table_schema = 'dms' AND table_name IN ('ResourceKey', 'SchemaComponent')",
        EffectiveSchemaFingerprintSql: EffectiveSchemaTableDefinition.RenderReadFingerprintCommandText(
            SqlDialect.Pgsql
        ),
        DataStoreIdentityTableExistsSql: "SELECT 1 FROM information_schema.tables WHERE table_schema = 'dms' AND table_name = 'DataStoreIdentity'",
        DataStoreIdentitySourceIdentitySql: """SELECT "SourceIdentity" FROM dms."DataStoreIdentity" WHERE "DataStoreIdentitySingletonId" = 1""",
        DocumentCacheStateTableExistsSql: "SELECT 1 FROM information_schema.tables WHERE table_schema = 'dms' AND table_name = 'DocumentCacheState'",
        DocumentCacheStateSingletonSql: """SELECT "ProjectionLifecycleState", "CacheAheadRecoveryRequired" FROM dms."DocumentCacheState" WHERE "StateId" = 1""",
        KnownLegacyDocumentCacheArtifactSql: """
        SELECT 'dms."DocumentCache"."Etag"'
        WHERE EXISTS (
            SELECT 1
            FROM information_schema.columns
            WHERE table_schema = 'dms'
            AND table_name = 'DocumentCache'
            AND column_name = 'Etag'
        )
        UNION ALL
        SELECT 'UX_DocumentCache_DocumentUuid'
        WHERE EXISTS (
            SELECT 1
            FROM pg_catalog.pg_constraint constraint_info
            WHERE constraint_info.conname = 'UX_DocumentCache_DocumentUuid'
            AND constraint_info.conrelid = to_regclass('"dms"."DocumentCache"')
        )
        OR to_regclass('"dms"."UX_DocumentCache_DocumentUuid"') IS NOT NULL
        UNION ALL
        SELECT 'IX_DocumentCache_ProjectName_ResourceName_LastModifiedAt'
        WHERE to_regclass('"dms"."IX_DocumentCache_ProjectName_ResourceName_LastModifiedAt"') IS NOT NULL
        """,
        ProviderPrerequisiteSql: PgsqlEnqueueOwnerPrerequisiteSql.ProviderPrerequisiteSql,
        ResourceKeySelectSql: @"SELECT ""ResourceKeyId"", ""ProjectName"", ""ResourceName"", ""ResourceVersion"" FROM dms.""ResourceKey"" ORDER BY ""ResourceKeyId""",
        SchemaComponentSelectSql: @"SELECT ""ProjectEndpointName"", ""ProjectName"", ""ProjectVersion"", ""IsExtensionProject"" FROM dms.""SchemaComponent"" WHERE ""EffectiveSchemaHash"" = @hash ORDER BY ""ProjectEndpointName""",
        MissingTableDataStoreIdentity: "dms.\"DataStoreIdentity\"",
        MissingTableDocumentCacheState: "dms.\"DocumentCacheState\"",
        MissingTableResourceKey: "dms.\"ResourceKey\"",
        MissingTableSchemaComponent: "dms.\"SchemaComponent\""
    );

    protected override DialectSql Dialect => _dialect;

    protected override DbConnection CreateConnection(string connectionString) =>
        new NpgsqlConnection(connectionString);

    public override string GetDatabaseName(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        return string.IsNullOrWhiteSpace(builder.Database)
            ? throw new InvalidOperationException("Connection string does not specify a database name.")
            : builder.Database;
    }

    /// <summary>
    /// The lowest <c>server_version_num</c> DMS supports: PostgreSQL 18.0.
    /// </summary>
    internal const int MinimumServerVersionNum = 180000;

    /// <summary>
    /// Maintenance-connection precondition: the server version, the existing target database's
    /// encoding (NULL when it does not exist yet), and template1's encoding, which a new database
    /// created with <c>ENCODING 'UTF8'</c> must match.
    /// </summary>
    internal const string PlatformPreconditionSql = """
        SELECT current_setting('server_version_num')::integer,
            (SELECT pg_encoding_to_char(encoding) FROM pg_database WHERE datname = @dbName),
            (SELECT pg_encoding_to_char(encoding) FROM pg_database WHERE datname = 'template1')
        """;

    /// <summary>
    /// Target-connection compatibility check run before any other preflight query.
    /// </summary>
    internal const string TargetPlatformSql = """
        SELECT current_setting('server_version_num')::integer, pg_encoding_to_char(encoding)
        FROM pg_database
        WHERE datname = current_database()
        """;

    public override void CheckPlatformPreconditions(string connectionString)
    {
        var targetDatabase = GetDatabaseName(connectionString);

        using var connection = CreateConnection(BuildMaintenanceConnectionString(connectionString));
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = PlatformPreconditionSql;
        AddDatabaseNameParameter(command, targetDatabase);

        using var reader = command.ExecuteReader();
        reader.Read();
        int serverVersionNum = Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture);

        // A new database inherits template1's encoding: CREATE DATABASE ... ENCODING 'UTF8' fails on a
        // non-UTF-8 template1 with a raw 22023, so check template1 when the target does not exist yet.
        if (reader.IsDBNull(1))
        {
            RequirePlatform(serverVersionNum, "template1 encoding", ReadEncoding(reader, 2));
        }
        else
        {
            RequirePlatform(serverVersionNum, "target database encoding", ReadEncoding(reader, 1));
        }
    }

    protected override void ValidatePlatformCompatibility(DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = TargetPlatformSql;

        using var reader = command.ExecuteReader();
        reader.Read();
        RequirePlatform(
            Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture),
            "target database encoding",
            ReadEncoding(reader, 1)
        );
    }

    private static string ReadEncoding(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? "unavailable" : reader.GetString(ordinal);

    private static void RequirePlatform(int serverVersionNum, string encodingSource, string encoding)
    {
        if (serverVersionNum < MinimumServerVersionNum)
        {
            throw new PostgresqlPlatformCompatibilityException(
                $"server_version_num {serverVersionNum.ToString(CultureInfo.InvariantCulture)}"
            );
        }

        if (encoding != "UTF8")
        {
            throw new PostgresqlPlatformCompatibilityException($"{encodingSource} {encoding}");
        }
    }

    public override bool CreateDatabaseIfNotExists(string connectionString)
    {
        var targetDatabase = GetDatabaseName(connectionString);

        Logger.LogInformation(
            "Checking if database exists: {DatabaseName}",
            LoggingSanitizer.SanitizeInternalValueForLogging(targetDatabase)
        );

        // Connect to the admin database to create the target database
        using var connection = CreateConnection(BuildMaintenanceConnectionString(connectionString));
        connection.Open();

        // Check if the database already exists
        using var checkCommand = connection.CreateCommand();
        checkCommand.CommandText = "SELECT 1 FROM pg_database WHERE datname = @dbName";
        AddDatabaseNameParameter(checkCommand, targetDatabase);

        var exists = checkCommand.ExecuteScalar() is not null;

        if (exists)
        {
            Logger.LogInformation(
                "Database already exists: {DatabaseName}",
                LoggingSanitizer.SanitizeInternalValueForLogging(targetDatabase)
            );
            return false;
        }

        // CREATE DATABASE cannot run inside a transaction in PostgreSQL.
        // Without an explicit BeginTransaction(), Npgsql executes in autocommit mode.
        // Use a quoted identifier to safely handle the database name.
        Logger.LogInformation(
            "Creating database: {DatabaseName}",
            LoggingSanitizer.SanitizeInternalValueForLogging(targetDatabase)
        );

        using var createCommand = connection.CreateCommand();
        var quotedName = $"\"{targetDatabase.Replace("\"", "\"\"")}\"";
        // The descriptor index's pg_c_utf8 collation requires a UTF-8 database. No TEMPLATE or locale
        // override: a cluster whose template1 is not UTF-8 is an operator precondition, checked first.
        createCommand.CommandText = $"CREATE DATABASE {quotedName} ENCODING 'UTF8'";

        try
        {
            createCommand.ExecuteNonQuery();
        }
        catch (PostgresException ex) when (ex.SqlState == "42P04")
        {
            // 42P04 = "duplicate_database" — a concurrent process created it
            // between our check and our CREATE. Treat as "already existed".
            Logger.LogInformation(
                ex,
                "Database was created concurrently by another process: {DatabaseName}",
                LoggingSanitizer.SanitizeInternalValueForLogging(targetDatabase)
            );
            return false;
        }

        Logger.LogInformation(
            "Database created successfully: {DatabaseName}",
            LoggingSanitizer.SanitizeInternalValueForLogging(targetDatabase)
        );

        return true;
    }

    private static string BuildMaintenanceConnectionString(string connectionString) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres" }.ConnectionString;

    private static void AddDatabaseNameParameter(DbCommand command, string databaseName)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@dbName";
        parameter.Value = databaseName;
        command.Parameters.Add(parameter);
    }

    public override void ExecuteInTransaction(
        string connectionString,
        string sql,
        int commandTimeoutSeconds = 300
    )
    {
        var targetDatabase = GetDatabaseName(connectionString);

        Logger.LogInformation(
            "Executing DDL in transaction against database: {DatabaseName}",
            LoggingSanitizer.SanitizeInternalValueForLogging(targetDatabase)
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

            Logger.LogInformation(
                "DDL executed successfully against database: {DatabaseName}",
                LoggingSanitizer.SanitizeInternalValueForLogging(targetDatabase)
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
                Logger.LogError(
                    rollbackEx,
                    "Failed to roll back transaction for database: {DatabaseName}",
                    LoggingSanitizer.SanitizeInternalValueForLogging(targetDatabase)
                );
            }

            throw;
        }
    }

    /// <summary>
    /// No-op for PostgreSQL. MVCC is the default isolation behavior.
    /// </summary>
    public override void CheckOrConfigureMvcc(string connectionString, bool databaseWasCreated)
    {
        // PostgreSQL uses MVCC natively — no configuration needed.
    }
}
