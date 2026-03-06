// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using EdFi.DataManagementService.Core.External.Backend;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Backend.Mssql;

public class MssqlDatabaseFingerprintReader(ILogger<MssqlDatabaseFingerprintReader> logger)
    : IDatabaseFingerprintReader
{
    public async Task<DatabaseFingerprint?> ReadFingerprintAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        // Check if the dms.EffectiveSchema table exists
        await using var existsCommand = connection.CreateCommand();
        existsCommand.CommandText =
            "SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = 'dms' AND TABLE_NAME = 'EffectiveSchema'";

        if (await existsCommand.ExecuteScalarAsync() is null)
        {
            logger.LogDebug("dms.EffectiveSchema table does not exist");
            return null;
        }

        // Read the singleton row
        await using var readCommand = connection.CreateCommand();
        readCommand.CommandText = """
            SELECT [ApiSchemaFormatVersion], [EffectiveSchemaHash], [ResourceKeyCount], [ResourceKeySeedHash]
            FROM [dms].[EffectiveSchema]
            WHERE [EffectiveSchemaSingletonId] = 1
            """;

        await using var reader = await readCommand.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            logger.LogDebug("dms.EffectiveSchema table exists but has no singleton row");
            return null;
        }

        return new DatabaseFingerprint(
            ApiSchemaFormatVersion: reader.GetString(reader.GetOrdinal("ApiSchemaFormatVersion")),
            EffectiveSchemaHash: reader.GetString(reader.GetOrdinal("EffectiveSchemaHash")),
            ResourceKeyCount: reader.GetInt16(reader.GetOrdinal("ResourceKeyCount")),
            ResourceKeySeedHash: ((byte[])reader[reader.GetOrdinal("ResourceKeySeedHash")]).ToImmutableArray()
        );
    }
}
