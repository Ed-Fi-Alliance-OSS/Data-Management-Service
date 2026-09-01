// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Backend.Mssql;

public class MssqlResourceKeyRowReader : IResourceKeyRowReader
{
    private readonly IMssqlConnectionAcquisition _acquisition;
    private readonly ILogger<MssqlResourceKeyRowReader> _logger;

    public MssqlResourceKeyRowReader(
        IMssqlConnectionAcquisition acquisition,
        ILogger<MssqlResourceKeyRowReader> logger
    )
    {
        _acquisition = acquisition ?? throw new ArgumentNullException(nameof(acquisition));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    private const string ResourceKeySelectSql = """
        SELECT [ResourceKeyId], [ProjectName], [ResourceName], [ResourceVersion]
        FROM [dms].[ResourceKey]
        ORDER BY [ResourceKeyId]
        """;

    public async Task<IReadOnlyList<ResourceKeyRow>> ReadResourceKeyRowsAsync(
        EffectiveDataStoreTarget target,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug("Reading resource key rows from dms.ResourceKey");

        await using MssqlConnectionLease lease = await _acquisition.AcquireLeaseAsync(
            target,
            cancellationToken
        );
        await using var connection = await lease.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
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

        _logger.LogDebug("Read {Count} resource key rows", rows.Count);

        return rows;
    }
}
