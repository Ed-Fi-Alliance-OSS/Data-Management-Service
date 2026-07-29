// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
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
        EffectiveSchemaHashSql: """SELECT "EffectiveSchemaHash" FROM dms."EffectiveSchema" WHERE "EffectiveSchemaSingletonId" = 1""",
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
        ProviderPrerequisiteSql: """
        WITH owner_role AS (
            SELECT pg_catalog.to_regrole('edfi_dms_enqueue_owner') AS oid
        ),
        session_role AS (
            SELECT oid, rolsuper, rolcreaterole
            FROM pg_catalog.pg_roles
            WHERE rolname = SESSION_USER
        )
        SELECT 'PostgreSQL provisioning principal must be SUPERUSER or CREATEROLE to create edfi_dms_enqueue_owner before provisioning.'
        FROM owner_role, session_role
        WHERE owner_role.oid IS NULL
        AND NOT (session_role.rolsuper OR session_role.rolcreaterole)
        UNION ALL
        SELECT 'PostgreSQL role edfi_dms_enqueue_owner exists but is not locked down as NOLOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS. Drop or repair the role before provisioning.'
        FROM owner_role
        INNER JOIN pg_catalog.pg_roles owner_role_attributes ON owner_role_attributes.oid = owner_role.oid
        WHERE owner_role.oid IS NOT NULL
        AND (
            owner_role_attributes.rolcanlogin
            OR owner_role_attributes.rolinherit
            OR owner_role_attributes.rolsuper
            OR owner_role_attributes.rolcreatedb
            OR owner_role_attributes.rolcreaterole
            OR owner_role_attributes.rolreplication
            OR owner_role_attributes.rolbypassrls
        )
        UNION ALL
        SELECT 'PostgreSQL role edfi_dms_enqueue_owner must not hold outgoing privilege-bearing memberships before provisioning.'
        FROM owner_role
        WHERE owner_role.oid IS NOT NULL
        AND EXISTS (
            SELECT 1
            FROM pg_catalog.pg_auth_members membership
            WHERE membership.member = owner_role.oid
            AND (membership.admin_option OR membership.inherit_option OR membership.set_option)
        )
        UNION ALL
        SELECT 'PostgreSQL provisioning principal has an unsafe direct membership in edfi_dms_enqueue_owner; required options are SET TRUE, INHERIT FALSE, ADMIN FALSE.'
        FROM owner_role, session_role
        WHERE owner_role.oid IS NOT NULL
        AND EXISTS (
            SELECT 1
            FROM pg_catalog.pg_auth_members membership
            WHERE membership.roleid = owner_role.oid
            AND membership.member = session_role.oid
            AND NOT (
                membership.admin_option
                AND NOT membership.inherit_option
                AND NOT membership.set_option
                AND session_role.rolcreaterole
            )
            AND (membership.admin_option OR membership.inherit_option OR NOT membership.set_option)
        )
        UNION ALL
        SELECT 'PostgreSQL provisioning principal must have direct SET TRUE, INHERIT FALSE, ADMIN FALSE membership in existing edfi_dms_enqueue_owner before provisioning.'
        FROM owner_role, session_role
        WHERE owner_role.oid IS NOT NULL
        AND NOT session_role.rolsuper
        AND NOT EXISTS (
            SELECT 1
            FROM pg_catalog.pg_auth_members membership
            WHERE membership.roleid = owner_role.oid
            AND membership.member = session_role.oid
            AND NOT membership.admin_option
            AND NOT membership.inherit_option
            AND membership.set_option
        )
        """,
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

    public override bool CreateDatabaseIfNotExists(string connectionString)
    {
        var targetDatabase = GetDatabaseName(connectionString);

        var builder = new NpgsqlConnectionStringBuilder(connectionString);

        Logger.LogInformation(
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
            Logger.LogInformation(
                "Database already exists: {DatabaseName}",
                LoggingSanitizer.SanitizeForLogging(targetDatabase)
            );
            return false;
        }

        // CREATE DATABASE cannot run inside a transaction in PostgreSQL.
        // Without an explicit BeginTransaction(), Npgsql executes in autocommit mode.
        // Use a quoted identifier to safely handle the database name.
        Logger.LogInformation(
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
            Logger.LogInformation(
                ex,
                "Database was created concurrently by another process: {DatabaseName}",
                LoggingSanitizer.SanitizeForLogging(targetDatabase)
            );
            return false;
        }

        Logger.LogInformation(
            "Database created successfully: {DatabaseName}",
            LoggingSanitizer.SanitizeForLogging(targetDatabase)
        );

        return true;
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

            Logger.LogInformation(
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
                Logger.LogError(
                    rollbackEx,
                    "Failed to roll back transaction for database: {DatabaseName}",
                    LoggingSanitizer.SanitizeForLogging(targetDatabase)
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
