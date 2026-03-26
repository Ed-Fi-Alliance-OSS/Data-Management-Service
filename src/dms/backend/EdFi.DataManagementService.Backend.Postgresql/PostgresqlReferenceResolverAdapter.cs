// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend;

namespace EdFi.DataManagementService.Backend.Postgresql;

internal sealed class PostgresqlReferenceResolverAdapter(IRelationalCommandExecutor commandExecutor)
    : IReferenceResolverAdapter
{
    private readonly IRelationalCommandExecutor _commandExecutor =
        commandExecutor ?? throw new ArgumentNullException(nameof(commandExecutor));

    public Task<IReadOnlyList<ReferenceLookupResult>> ResolveAsync(
        ReferenceLookupRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        return _commandExecutor.ExecuteReaderAsync(
            PostgresqlReferenceLookupCommandBuilder.Build(request),
            ReferenceLookupResultReader.ReadAsync,
            cancellationToken
        );
    }
}
