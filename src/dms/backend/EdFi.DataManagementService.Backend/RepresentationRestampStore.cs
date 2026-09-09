// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.DocumentCache;

namespace EdFi.DataManagementService.Backend;

public interface IRepresentationRestampStore
{
    Task<long> GetMaxChangeVersionAsync(IRelationalWriteSession session, CancellationToken cancellationToken);

    Task<RepresentationRestampSelection> ResolveSelectionAsync(
        IRelationalWriteSession session,
        DocumentCacheRepresentationRestampScope scope,
        int pageSize,
        CancellationToken cancellationToken
    );

    Task CreateDraftAsync(
        IRelationalWriteSession session,
        DocumentCacheRepresentationRestampOperation operation,
        CancellationToken cancellationToken
    );

    Task<DocumentCacheRepresentationRestampOperation?> LoadAsync(
        IRelationalWriteSession session,
        Guid operationId,
        CancellationToken cancellationToken
    );

    Task<RepresentationRestampPage> SelectNextPageAsync(
        IRelationalWriteSession session,
        DocumentCacheRepresentationRestampOperation operation,
        int pageSize,
        CancellationToken cancellationToken
    );

    Task<RepresentationRestampPageCommit> StampPageAsync(
        IRelationalWriteSession session,
        RepresentationRestampPage page,
        CancellationToken cancellationToken
    );

    Task UpdateProgressAsync(
        IRelationalWriteSession session,
        Guid operationId,
        long committedCount,
        DocumentCacheRepresentationRestampOperationState state,
        CancellationToken cancellationToken
    );

    Task<long> CountRemainingEligibleAsync(
        IRelationalWriteSession session,
        DocumentCacheRepresentationRestampOperation operation,
        CancellationToken cancellationToken
    );

    Task MarkCompletedAsync(
        IRelationalWriteSession session,
        Guid operationId,
        CancellationToken cancellationToken
    );
}
