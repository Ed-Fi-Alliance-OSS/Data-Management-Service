// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;

namespace EdFi.DataManagementService.Backend.DocumentCacheRuntime;

public static class DocumentCacheRuntimeTargetResolver
{
    public static async Task<DocumentCacheTargetRegistrySnapshot> ResolveAsync(
        IDocumentCacheTargetRegistry targetRegistry,
        DocumentCacheTargetKey targetKey,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(targetRegistry);
        ArgumentNullException.ThrowIfNull(targetKey);
        cancellationToken.ThrowIfCancellationRequested();
        return await targetRegistry
            .RefreshAsync(DocumentCacheTargetRefreshReason.Startup, cancellationToken)
            .ConfigureAwait(false);
    }

    public static bool ContainsOnlyTarget(
        DocumentCacheTargetRegistrySnapshot snapshot,
        DocumentCacheTargetKey targetKey
    ) => snapshot.Targets.Length == 1 && snapshot.Targets[0].TargetKey.Equals(targetKey);
}
