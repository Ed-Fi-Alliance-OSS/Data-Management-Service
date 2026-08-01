// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Postgresql;

/// <summary>
/// Executes a natural-key lookup batch as one PostgreSQL command.
/// </summary>
/// <remarks>
/// PostgreSQL binds one array parameter per probe column regardless of entry count, so there is no
/// parameter ceiling to respect and a batch is always exactly one round trip.
/// </remarks>
internal sealed class PostgresqlNaturalKeyLookupAdapter(IRelationalCommandExecutor commandExecutor)
    : INaturalKeyLookupAdapter
{
    private readonly IRelationalCommandExecutor _commandExecutor =
        commandExecutor ?? throw new ArgumentNullException(nameof(commandExecutor));

    public Task<IReadOnlyList<NaturalKeyLookupRow>> ResolveAsync(
        NaturalKeyLookupBatch batch,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(batch);

        return _commandExecutor.ExecuteReaderAsync(
            PostgresqlNaturalKeyLookupCommandBuilder.Build(batch),
            (reader, token) => NaturalKeyLookupResultReader.ReadAsync(batch, reader, token),
            cancellationToken
        );
    }
}
