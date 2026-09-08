// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend;

namespace EdFi.DataManagementService.Backend.Mssql;

internal sealed class MssqlReferenceResolverAdapter(IRelationalCommandExecutor commandExecutor)
    : IReferenceResolverAdapter
{
    private readonly MssqlReferenceLookupSmallListStrategy _smallListStrategy = new(
        commandExecutor ?? throw new ArgumentNullException(nameof(commandExecutor))
    );

    private readonly MssqlReferenceLookupBulkStrategy _bulkStrategy = new(commandExecutor);

    public Task<IReadOnlyList<ReferenceLookupResult>> ResolveAsync(
        ReferenceLookupRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        return MssqlReferenceLookupSmallListStrategy.CanResolve(request.ReferentialIds)
            ? _smallListStrategy.ResolveAsync(request, cancellationToken)
            : _bulkStrategy.ResolveAsync(request, cancellationToken);
    }
}
