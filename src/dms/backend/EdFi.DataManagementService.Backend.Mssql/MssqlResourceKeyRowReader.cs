// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Backend.Mssql;

public class MssqlResourceKeyRowReader(ILogger<MssqlResourceKeyRowReader> logger) : IResourceKeyRowReader
{
    private const string ResourceKeySelectSql = """
        SELECT [ResourceKeyId], [ProjectName], [ResourceName], [ResourceVersion]
        FROM [dms].[ResourceKey]
        ORDER BY [ResourceKeyId]
        """;

    public async Task<IReadOnlyList<ResourceKeyRow>> ReadResourceKeyRowsAsync(string connectionString)
    {
        logger.LogDebug("Reading resource key rows from dms.ResourceKey");

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = ResourceKeySelectSql;

        await using var reader = await command.ExecuteReaderAsync();

        var rows = new List<ResourceKeyRow>();

        while (await reader.ReadAsync())
        {
            rows.Add(
                new ResourceKeyRow(
                    reader.GetInt16(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3)
                )
            );
        }

        logger.LogDebug("Read {Count} resource key rows", rows.Count);

        return rows;
    }
}
