// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Backend.Postgresql;

/// <summary>
/// Reads the resource-key seed rows from whichever database the request selected.
/// </summary>
/// <remarks>
/// The connection comes from the leased data-source cache for the same reason the fingerprint reader's
/// does: this is the second database touch of a request, and both must share the data source whose
/// lifetime the ownership rules govern rather than opening a pool the cache does not know about.
/// </remarks>
public class PostgresqlResourceKeyRowReader(
    NpgsqlDataSourceCache dataSourceCache,
    ILogger<PostgresqlResourceKeyRowReader> logger
) : IResourceKeyRowReader
{
    private const string ResourceKeySelectSql = """
        SELECT "ResourceKeyId", "ProjectName", "ResourceName", "ResourceVersion"
        FROM dms."ResourceKey"
        ORDER BY "ResourceKeyId"
        """;

    public async Task<IReadOnlyList<ResourceKeyRow>> ReadResourceKeyRowsAsync(
        EffectiveDataStoreTarget target,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(target);

        logger.LogDebug("Reading resource key rows from dms.ResourceKey");

        await using LeasedNpgsqlConnection leased = await dataSourceCache.OpenLeasedConnectionAsync(
            target.ConnectionString,
            cancellationToken
        );

        await using var command = leased.Connection.CreateCommand();
        command.CommandText = ResourceKeySelectSql;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var rows = new List<ResourceKeyRow>();

        while (await reader.ReadAsync(cancellationToken))
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
