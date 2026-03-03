// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.SchemaTools.Provisioning;

/// <summary>
/// Encapsulates all database connectivity for the ddl provision command.
/// Each dialect (PostgreSQL, SQL Server) has its own implementation.
/// </summary>
public interface IDatabaseProvisioner
{
    /// <summary>
    /// Connects to the admin database (postgres/master) and creates the target
    /// database if it does not already exist. This is a pre-step outside any transaction.
    /// </summary>
    /// <returns>True if the database was newly created; false if it already existed.</returns>
    bool CreateDatabaseIfNotExists(string connectionString);

    /// <summary>
    /// Opens a connection to the target database, begins a transaction, executes
    /// the full DDL SQL string, and commits on success. Rolls back on failure.
    /// </summary>
    void ExecuteInTransaction(string connectionString, string sql, int commandTimeoutSeconds = 300);

    /// <summary>
    /// SQL Server only: configures or checks MVCC isolation settings.
    /// If <paramref name="databaseWasCreated"/> is true, enables READ_COMMITTED_SNAPSHOT
    /// and ALLOW_SNAPSHOT_ISOLATION. If false, checks current settings and warns if
    /// READ_COMMITTED_SNAPSHOT is OFF. PostgreSQL implementation is a no-op.
    /// </summary>
    void CheckOrConfigureMvcc(string connectionString, bool databaseWasCreated);

    /// <summary>
    /// Performs a lightweight preflight check: if dms.EffectiveSchema exists and
    /// its hash differs from <paramref name="expectedHash"/>, throws an
    /// InvalidOperationException. If the table does not exist or the hash matches,
    /// returns normally.
    /// </summary>
    void PreflightSchemaHashCheck(string connectionString, string expectedHash);

    /// <summary>
    /// Extracts the target database name from the connection string.
    /// </summary>
    string GetDatabaseName(string connectionString);
}
