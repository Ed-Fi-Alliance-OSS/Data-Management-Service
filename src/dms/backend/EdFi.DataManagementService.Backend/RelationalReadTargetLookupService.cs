// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Backend;

public interface IRelationalReadTargetLookupService
{
    /// <summary>
    /// Resolves the GET-by-id target by probing <paramref name="rootTable"/>'s
    /// <c>UX_&lt;Root&gt;_DocumentUuid</c> unique index. The route names the resource, so the root
    /// table carries the resource scope: a uuid persisted for a different resource is absent here and
    /// resolves to <see cref="RelationalReadTargetLookupResult.NotFound"/>.
    /// </summary>
    Task<RelationalReadTargetLookupResult> ResolveForGetByIdAsync(
        DbTableName rootTable,
        DocumentUuid documentUuid,
        CancellationToken cancellationToken = default
    );
}

public abstract record RelationalReadTargetLookupResult
{
    private RelationalReadTargetLookupResult() { }

    public sealed record ExistingDocument(long DocumentId, DocumentUuid DocumentUuid, long ContentVersion)
        : RelationalReadTargetLookupResult;

    public sealed record NotFound() : RelationalReadTargetLookupResult;
}

internal sealed class RelationalReadTargetLookupService(IRelationalCommandExecutor commandExecutor)
    : IRelationalReadTargetLookupService
{
    private readonly IRelationalCommandExecutor _commandExecutor =
        commandExecutor ?? throw new ArgumentNullException(nameof(commandExecutor));

    public async Task<RelationalReadTargetLookupResult> ResolveForGetByIdAsync(
        DbTableName rootTable,
        DocumentUuid documentUuid,
        CancellationToken cancellationToken = default
    )
    {
        var resolvedTarget = await RelationalDocumentUuidLookupSupport
            .TryResolveGetTargetByRootTableAsync(_commandExecutor, rootTable, documentUuid, cancellationToken)
            .ConfigureAwait(false);

        if (resolvedTarget is null)
        {
            return new RelationalReadTargetLookupResult.NotFound();
        }

        return new RelationalReadTargetLookupResult.ExistingDocument(
            resolvedTarget.DocumentId,
            new DocumentUuid(resolvedTarget.DocumentUuid),
            resolvedTarget.ContentVersion
        );
    }
}
