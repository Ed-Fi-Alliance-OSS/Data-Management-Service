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
        ArgumentNullException.ThrowIfNull(target);

        _logger.LogDebug("Reading resource key rows from dms.ResourceKey");

        // The guard covers taking the lease as well as the open, because the connection string is
        // parsed twice before the open - once realizing the derivative's effective string, once in the
        // SqlConnection constructor - so a provider-invalid string fails at one of those rather than at
        // the open. Command execution below is deliberately outside it; a failure reading the rows is
        // not an unreachable database.
        await using MssqlLeasedConnection leased = await ConnectionAcquisition.GuardAsync(
            () => MssqlLeasedConnection.OpenAsync(_acquisition, target, cancellationToken),
            target.Kind,
            MssqlConnectionAcquisitionFailure.IsExpected,
            MssqlConnectionAcquisitionFailure.Describe,
            _logger,
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

        _logger.LogDebug("Read {Count} resource key rows", rows.Count);

        return rows;
    }
}
