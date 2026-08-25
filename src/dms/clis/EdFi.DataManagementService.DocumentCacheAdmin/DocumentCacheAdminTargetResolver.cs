// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;

namespace EdFi.DataManagementService.DocumentCacheAdmin;

internal enum DocumentCacheAdminTargetResolutionOutcome
{
    Completed,
    UnexpectedTargetMembership,
}

internal sealed record DocumentCacheAdminTargetResolutionResult(
    DocumentCacheAdminTargetResolutionOutcome Outcome,
    DocumentCacheTargetKey TargetKey,
    DocumentCacheTargetRegistrySnapshot RegistrySnapshot,
    string? FailureMessage
)
{
    private const string UnexpectedTargetMembershipMessage =
        "DocumentCache target registry did not contain exactly the invocation target.";

    public static DocumentCacheAdminTargetResolutionResult FromSnapshot(
        DocumentCacheTargetKey targetKey,
        DocumentCacheTargetRegistrySnapshot registrySnapshot
    )
    {
        ArgumentNullException.ThrowIfNull(targetKey);
        ArgumentNullException.ThrowIfNull(registrySnapshot);

        if (registrySnapshot.Targets.Length != 1 || !registrySnapshot.Targets[0].TargetKey.Equals(targetKey))
        {
            return new DocumentCacheAdminTargetResolutionResult(
                DocumentCacheAdminTargetResolutionOutcome.UnexpectedTargetMembership,
                targetKey,
                registrySnapshot,
                UnexpectedTargetMembershipMessage
            );
        }

        return new DocumentCacheAdminTargetResolutionResult(
            DocumentCacheAdminTargetResolutionOutcome.Completed,
            targetKey,
            registrySnapshot,
            FailureMessage: null
        );
    }
}

internal interface IDocumentCacheAdminTargetResolver
{
    Task<DocumentCacheAdminTargetResolutionResult> ResolveAsync(
        DocumentCacheTargetKey targetKey,
        CancellationToken cancellationToken = default
    );
}

internal sealed class DocumentCacheAdminTargetResolver(IDocumentCacheTargetRegistry targetRegistry)
    : IDocumentCacheAdminTargetResolver
{
    public async Task<DocumentCacheAdminTargetResolutionResult> ResolveAsync(
        DocumentCacheTargetKey targetKey,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(targetKey);

        DocumentCacheTargetRegistrySnapshot registrySnapshot = await targetRegistry
            .RefreshAsync(DocumentCacheTargetRefreshReason.Startup, cancellationToken)
            .ConfigureAwait(false);

        return DocumentCacheAdminTargetResolutionResult.FromSnapshot(targetKey, registrySnapshot);
    }
}
