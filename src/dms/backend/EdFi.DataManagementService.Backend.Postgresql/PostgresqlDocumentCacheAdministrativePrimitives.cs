// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;

namespace EdFi.DataManagementService.Backend.Postgresql;

internal sealed class PostgresqlDocumentCacheAdministrativePrimitives : IDocumentCacheAdministrativePrimitives
{
    private static readonly DocumentCacheAdministrativePrimitiveCommands Commands =
        DocumentCacheAdministrativePrimitivesSupport.GetCommands(SqlDialect.Pgsql);

    public RelationalProviderToken ProviderToken => RelationalProviderToken.Postgresql;

    public Task<DocumentCacheLifecycleReadResult> ReadLifecycleAsync(
        IRelationalWriteSession mutexSession,
        DocumentCacheAdministrativeStateLockMode lockMode = DocumentCacheAdministrativeStateLockMode.Shared,
        CancellationToken cancellationToken = default
    ) =>
        DocumentCacheAdministrativePrimitivesSupport.ReadLifecycleAsync(
            mutexSession,
            Commands,
            lockMode,
            cancellationToken
        );

    public Task LockCanonicalDocumentsForGuardedActivationAsync(
        IRelationalWriteSession mutexSession,
        CancellationToken cancellationToken = default
    ) =>
        DocumentCacheAdministrativePrimitivesSupport.LockCanonicalDocumentsForGuardedActivationAsync(
            mutexSession,
            Commands,
            cancellationToken
        );

    public Task<DocumentCacheGuardedNewEmptyActivationState> ReadGuardedNewEmptyActivationStateAsync(
        IRelationalWriteSession mutexSession,
        CancellationToken cancellationToken = default
    ) =>
        DocumentCacheAdministrativePrimitivesSupport.ReadGuardedNewEmptyActivationStateAsync(
            mutexSession,
            Commands,
            cancellationToken
        );

    public Task<DocumentCacheProviderPrerequisiteValidationResult> ValidateActivationPrerequisitesAsync(
        IRelationalWriteSession mutexSession,
        CancellationToken cancellationToken = default
    ) =>
        DocumentCacheAdministrativePrimitivesSupport.ValidateActivationPrerequisitesAsync(
            mutexSession,
            Commands,
            cancellationToken
        );

    public Task<DocumentCacheAdministrativeLifecycleTransitionResult> TryTransitionLifecycleAsync(
        IRelationalWriteSession mutexSession,
        DocumentCacheAdministrativeLifecycleTransitionRequest request,
        CancellationToken cancellationToken = default
    ) =>
        DocumentCacheAdministrativePrimitivesSupport.TryTransitionLifecycleAsync(
            mutexSession,
            Commands,
            request,
            cancellationToken
        );

    public Task<DocumentCacheAdministrativeActivationTransitionResult> TryTransitionLifecycleAfterActivationPrerequisitesAsync(
        IRelationalWriteSession mutexSession,
        DocumentCacheAdministrativeLifecycleTransitionRequest request,
        CancellationToken cancellationToken = default
    ) =>
        DocumentCacheAdministrativePrimitivesSupport.TryTransitionLifecycleAfterActivationPrerequisitesAsync(
            mutexSession,
            Commands,
            request,
            cancellationToken
        );
}
