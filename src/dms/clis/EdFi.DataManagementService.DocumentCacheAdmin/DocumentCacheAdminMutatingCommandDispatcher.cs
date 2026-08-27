// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Core.DocumentCache;

namespace EdFi.DataManagementService.DocumentCacheAdmin;

internal interface IDocumentCacheAdminMutatingCommandDispatcher
{
    Task<DocumentCacheAdministrativeCommandResult> ExecuteAsync(
        DocumentCacheAdminMutatingCommandRequest commandRequest,
        CancellationToken cancellationToken = default
    );
}

internal sealed class DocumentCacheAdminMutatingCommandDispatcher(
    IDocumentCacheGuardedNewEmptyActivationCommand guardedNewEmptyActivationCommand,
    IDocumentCacheOfflineActivationCommand offlineActivationCommand,
    IDocumentCacheOfflineDeactivationCommand offlineDeactivationCommand,
    IDocumentCacheOnlineCacheRebuildCommand onlineCacheRebuildCommand,
    IDocumentCacheExplicitIntegrityScrubCommand explicitIntegrityScrubCommand,
    IDocumentCacheInternalOnlyCacheAheadRecoveryCommand internalOnlyCacheAheadRecoveryCommand
) : IDocumentCacheAdminMutatingCommandDispatcher
{
    public Task<DocumentCacheAdministrativeCommandResult> ExecuteAsync(
        DocumentCacheAdminMutatingCommandRequest commandRequest,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(commandRequest);

        return commandRequest.Request switch
        {
            DocumentCacheGuardedNewEmptyActivationRequest request =>
                guardedNewEmptyActivationCommand.ExecuteAsync(request, cancellationToken),
            DocumentCacheOfflineActivationRequest request => offlineActivationCommand.ExecuteAsync(
                request,
                cancellationToken
            ),
            DocumentCacheOfflineDeactivationRequest request => offlineDeactivationCommand.ExecuteAsync(
                request,
                cancellationToken
            ),
            DocumentCacheOnlineCacheRebuildRequest request => onlineCacheRebuildCommand.ExecuteAsync(
                request,
                cancellationToken
            ),
            DocumentCacheExplicitIntegrityScrubRequest request => explicitIntegrityScrubCommand.ExecuteAsync(
                request,
                cancellationToken
            ),
            DocumentCacheInternalOnlyCacheAheadRecoveryRequest request =>
                internalOnlyCacheAheadRecoveryCommand.ExecuteAsync(request, cancellationToken),
            _ => throw new InvalidOperationException(
                $"Unsupported DocumentCache mutating request type '{commandRequest.Request.GetType().FullName}'."
            ),
        };
    }
}
