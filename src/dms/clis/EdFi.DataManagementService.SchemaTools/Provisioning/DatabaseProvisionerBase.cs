// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend.External;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.SchemaTools.Provisioning;

/// <summary>
/// Holds the dialect-specific SQL strings and formatting used by
/// <see cref="DatabaseProvisionerBase"/> to implement preflight checks.
/// </summary>
/// <param name="EffectiveSchemaTableExistsSql">
/// Query that returns a non-null scalar if the dms.EffectiveSchema table exists.
/// </param>
/// <param name="EffectiveSchemaHashSql">
/// Query that selects EffectiveSchemaHash from the singleton row.
/// </param>
/// <param name="SeedTableCheckSql">
/// Query that returns table_name rows for ResourceKey and SchemaComponent in the dms schema.
/// </param>
/// <param name="EffectiveSchemaCountAndHashSql">
/// Query that selects ResourceKeyCount and ResourceKeySeedHash from the singleton row.
/// </param>
/// <param name="ResourceKeySelectSql">
/// Query that selects all ResourceKey rows ordered by ResourceKeyId.
/// </param>
/// <param name="SchemaComponentSelectSql">
/// Parameterized query that selects SchemaComponent rows filtered by EffectiveSchemaHash (@hash),
/// ordered by ProjectEndpointName.
/// </param>
/// <param name="MissingTableResourceKey">
/// Dialect-specific formatted table name for ResourceKey in error messages.
/// </param>
/// <param name="MissingTableSchemaComponent">
/// Dialect-specific formatted table name for SchemaComponent in error messages.
/// </param>
public sealed record DialectSql(
    string EffectiveSchemaTableExistsSql,
    string EffectiveSchemaHashSql,
    string SeedTableCheckSql,
    string EffectiveSchemaCountAndHashSql,
    string ResourceKeySelectSql,
    string SchemaComponentSelectSql,
    string MissingTableResourceKey,
    string MissingTableSchemaComponent
);

/// <summary>
/// Abstract base class for database provisioners that extracts shared preflight logic
/// using ADO.NET's <see cref="DbConnection"/>/<see cref="DbCommand"/> abstractions.
/// Concrete classes provide dialect-specific SQL strings and connection creation;
/// the base class handles the common algorithm for <see cref="IDatabaseProvisioner.PreflightSchemaHashCheck"/>
/// and <see cref="IDatabaseProvisioner.PreflightSeedValidation"/>.
/// </summary>
public abstract class DatabaseProvisionerBase(ILogger logger) : IDatabaseProvisioner
{
    protected ILogger Logger => logger;

    /// <summary>
    /// Returns the dialect-specific SQL strings used by the shared preflight methods.
    /// </summary>
    protected abstract DialectSql Dialect { get; }

    /// <summary>
    /// Creates a new <see cref="DbConnection"/> for the given connection string.
    /// The caller is responsible for opening and disposing it.
    /// </summary>
    protected abstract DbConnection CreateConnection(string connectionString);

    public abstract string GetDatabaseName(string connectionString);

    public abstract bool CreateDatabaseIfNotExists(string connectionString);

    public abstract void ExecuteInTransaction(
        string connectionString,
        string sql,
        int commandTimeoutSeconds = 300
    );

    public abstract void CheckOrConfigureMvcc(string connectionString, bool databaseWasCreated);

    public void PreflightSchemaHashCheck(string connectionString, string expectedHash)
    {
        using var connection = CreateConnection(connectionString);
        connection.Open();

        // Check if the dms.EffectiveSchema table exists
        using var existsCommand = connection.CreateCommand();
        existsCommand.CommandText = Dialect.EffectiveSchemaTableExistsSql;
        if (existsCommand.ExecuteScalar() is null)
        {
            return; // New database — no table yet, proceed with provisioning
        }

        // Table exists — check the stored hash
        using var hashCommand = connection.CreateCommand();
        hashCommand.CommandText = Dialect.EffectiveSchemaHashSql;
        var storedHash = hashCommand.ExecuteScalar() as string;

        // Table exists but singleton row is missing — partial/corrupt state
        if (storedHash is null)
        {
            throw new InvalidOperationException(
                "The dms.EffectiveSchema table exists but contains no singleton row. "
                    + "This indicates a partial or corrupt provisioning state. "
                    + "Drop and recreate the database before re-provisioning."
            );
        }

        SchemaHashChecker.ValidateOrThrow(storedHash, expectedHash, logger);
    }

    public void PreflightSeedValidation(string connectionString, EffectiveSchemaInfo expectedSchema)
    {
        using var connection = CreateConnection(connectionString);
        connection.Open();

        // Check if the dms.EffectiveSchema table exists — if not, this is a fresh database
        using var existsCommand = connection.CreateCommand();
        existsCommand.CommandText = Dialect.EffectiveSchemaTableExistsSql;
        if (existsCommand.ExecuteScalar() is null)
        {
            return;
        }

        // Read the current EffectiveSchemaHash to scope the SchemaComponent query
        using var hashCommand = connection.CreateCommand();
        hashCommand.CommandText = Dialect.EffectiveSchemaHashSql;
        var currentHash = hashCommand.ExecuteScalar() as string;

        // Table exists but singleton row is missing — partial/corrupt state
        if (currentHash is null)
        {
            throw new InvalidOperationException(
                "The dms.EffectiveSchema table exists but contains no singleton row. "
                    + "This indicates a partial or corrupt provisioning state. "
                    + "Drop and recreate the database before re-provisioning."
            );
        }

        // Guard: ensure the stored hash matches the expected hash.
        // This is self-contained so PreflightSeedValidation works independently
        // of any prior checks (e.g., standalone PreflightSchemaHashCheck).
        SchemaHashChecker.ValidateOrThrow(currentHash, expectedSchema.EffectiveSchemaHash, logger);

        // --- Check that required seed tables exist ---
        var missingTables = new List<string>();
        using (var tableCheckCommand = connection.CreateCommand())
        {
            tableCheckCommand.CommandText = Dialect.SeedTableCheckSql;
            var foundTables = new HashSet<string>();
            using var tableReader = tableCheckCommand.ExecuteReader();
            while (tableReader.Read())
            {
                foundTables.Add(tableReader.GetString(0));
            }

            if (!foundTables.Contains("ResourceKey"))
                missingTables.Add(Dialect.MissingTableResourceKey);
            if (!foundTables.Contains("SchemaComponent"))
                missingTables.Add(Dialect.MissingTableSchemaComponent);
        }

        if (missingTables.Count > 0)
        {
            throw new InvalidOperationException(
                $"The dms.EffectiveSchema table exists but the following required seed table(s) are missing: "
                    + $"{string.Join(", ", missingTables)}. "
                    + "This indicates a partial or corrupt provisioning state. "
                    + "Drop and recreate the database before re-provisioning."
            );
        }

        // --- Validate EffectiveSchema ResourceKeyCount and ResourceKeySeedHash ---
        using (var esCommand = connection.CreateCommand())
        {
            esCommand.CommandText = Dialect.EffectiveSchemaCountAndHashSql;
            using var reader = esCommand.ExecuteReader();
            if (reader.Read())
            {
                var storedCount = reader.GetInt16(0);
                var storedHash = new byte[32];
                reader.GetBytes(1, 0, storedHash, 0, 32);

                SeedValidator.ValidateEffectiveSchemaOrThrow(
                    storedCount,
                    storedHash,
                    expectedSchema.ResourceKeyCount,
                    expectedSchema.ResourceKeySeedHash,
                    logger
                );
            }
        }

        // --- Validate ResourceKey rows ---
        var actualResourceKeys = new List<ResourceKeyRow>();
        using (var rkCommand = connection.CreateCommand())
        {
            rkCommand.CommandText = Dialect.ResourceKeySelectSql;
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
            scCommand.CommandText = Dialect.SchemaComponentSelectSql;
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
}
