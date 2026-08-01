// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Mssql;

/// <summary>
/// Executes a natural-key lookup batch as one SQL Server command.
/// </summary>
/// <remarks>
/// SQL Server's 2100-parameter ceiling is per command, but the command builder passes each group's whole
/// entry list as a single <c>nvarchar(max)</c> JSON parameter shredded by <c>OPENJSON</c>, so a batch binds
/// one parameter per target group however many references it carries. Reaching the ceiling would take a
/// request naming 2099 distinct targets, which is why this adapter no longer slices batches: like
/// PostgreSQL, a batch is always exactly one command and one round trip, and the builder keeps a cheap
/// guard for the impossible case.
/// </remarks>
internal sealed class MssqlNaturalKeyLookupAdapter(IRelationalCommandExecutor commandExecutor)
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
            MssqlNaturalKeyLookupCommandBuilder.Build(batch),
            (reader, token) => NaturalKeyLookupResultReader.ReadAsync(batch, reader, token),
            cancellationToken
        );
    }
}
