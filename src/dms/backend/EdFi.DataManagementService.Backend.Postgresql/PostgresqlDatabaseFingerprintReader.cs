// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using EdFi.DataManagementService.Core.External.Backend;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace EdFi.DataManagementService.Backend.Postgresql;

// TODO(DMS-939): Add an integration test that reads from an actual provisioned PostgreSQL database
// to verify the reader's column names match the DDL-emitted schema.
public class PostgresqlDatabaseFingerprintReader(ILogger<PostgresqlDatabaseFingerprintReader> logger)
    : IDatabaseFingerprintReader
{
    public async Task<DatabaseFingerprint?> ReadFingerprintAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        // Check if the dms.effectiveschema table exists
        // Note: PostgreSQL folds unquoted DDL identifiers to lowercase,
        // so information_schema stores the table name as 'effectiveschema'.
        await using var existsCommand = connection.CreateCommand();
        existsCommand.CommandText =
            "SELECT 1 FROM information_schema.tables WHERE table_schema = 'dms' AND table_name = 'effectiveschema'";

        if (await existsCommand.ExecuteScalarAsync() is null)
        {
            logger.LogDebug("dms.effectiveschema table does not exist");
            return null;
        }

        // Read the singleton row
        await using var readCommand = connection.CreateCommand();
        readCommand.CommandText = """
            SELECT apischemaformatversion, effectiveschemahash, resourcekeycount, resourcekeyseedhash
            FROM dms.effectiveschema
            WHERE effectiveschemasingletonid = 1
            """;

        await using var reader = await readCommand.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            logger.LogDebug("dms.effectiveschema table exists but has no singleton row");
            return null;
        }

        return new DatabaseFingerprint(
            ApiSchemaFormatVersion: reader.GetString(reader.GetOrdinal("apischemaformatversion")),
            EffectiveSchemaHash: reader.GetString(reader.GetOrdinal("effectiveschemahash")),
            ResourceKeyCount: reader.GetInt16(reader.GetOrdinal("resourcekeycount")),
            ResourceKeySeedHash: ((byte[])reader[reader.GetOrdinal("resourcekeyseedhash")]).ToImmutableArray()
        );
    }
}
