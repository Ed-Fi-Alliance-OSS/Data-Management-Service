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
/// <param name="EffectiveSchemaFingerprintSql">
/// Query that selects the runtime fingerprint projection from the singleton row.
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
    string EffectiveSchemaFingerprintSql,
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

        string currentHash;

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

        // --- Validate EffectiveSchema singleton contents ---
        using (var esCommand = connection.CreateCommand())
        {
            esCommand.CommandText = Dialect.EffectiveSchemaFingerprintSql;
            using var reader = esCommand.ExecuteReader();

            if (!reader.Read())
            {
                throw new InvalidOperationException(
                    "The dms.EffectiveSchema table exists but contains no singleton row. "
                        + "This indicates a partial or corrupt provisioning state. "
                        + "Drop and recreate the database before re-provisioning."
                );
            }

            var storedEffectiveSchemaSingletonId = ReadInt16OrDefault(reader, 0, 0);
            var storedApiSchemaFormatVersion = ReadStringOrEmpty(reader, 1);
            currentHash = ReadStringOrEmpty(reader, 2);
            var storedResourceKeyCount = ReadInt16OrDefault(reader, 3, short.MinValue);
            var storedResourceKeySeedHash = ReadBytesOrEmpty(reader, 4);

            SeedValidator.ValidateEffectiveSchemaOrThrow(
                storedEffectiveSchemaSingletonId,
                storedApiSchemaFormatVersion,
                currentHash,
                storedResourceKeyCount,
                storedResourceKeySeedHash,
                expectedSchema,
                logger
            );

            if (reader.Read())
            {
                throw new InvalidOperationException(
                    $"dms.EffectiveSchema validation failed: {EffectiveSchemaFingerprintContract.CreateMultipleRowsMessage()}"
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

    private static short ReadInt16OrDefault(DbDataReader reader, int ordinal, short defaultValue)
    {
        if (reader.IsDBNull(ordinal))
        {
            return defaultValue;
        }

        return reader.GetValue(ordinal) switch
        {
            short value => value,
            byte value => value,
            int value when value is >= short.MinValue and <= short.MaxValue => checked((short)value),
            long value when value is >= short.MinValue and <= short.MaxValue => checked((short)value),
            _ => defaultValue,
        };
    }

    private static string ReadStringOrEmpty(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return string.Empty;
        }

        return reader.GetValue(ordinal) switch
        {
            string value => value,
            _ => reader.GetValue(ordinal).ToString() ?? string.Empty,
        };
    }

    private static byte[] ReadBytesOrEmpty(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return [];
        }

        return reader.GetValue(ordinal) switch
        {
            byte[] value => value,
            ArraySegment<byte> value => value.ToArray(),
            ReadOnlyMemory<byte> value => value.ToArray(),
            _ => [],
        };
    }
}
