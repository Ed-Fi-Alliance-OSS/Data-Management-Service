// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;

namespace EdFi.DataManagementService.Backend.Mssql;

internal sealed class MssqlDocumentCacheAdministrativePrimitives : IDocumentCacheAdministrativePrimitives
{
    private static readonly DocumentCacheAdministrativePrimitiveCommands Commands =
        DocumentCacheAdministrativePrimitivesSupport.GetCommands(SqlDialect.Mssql);

    public RelationalProviderToken ProviderToken => RelationalProviderToken.SqlServer;

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

    public Task<DocumentCacheAdministrativeClearBatchResult> ClearDocumentCacheBatchAsync(
        IRelationalWriteSession mutexSession,
        DocumentCacheAdministrativeClearBatchRequest request,
        CancellationToken cancellationToken = default
    ) =>
        DocumentCacheAdministrativePrimitivesSupport.ClearDocumentCacheBatchAsync(
            mutexSession,
            Commands,
            request,
            cancellationToken
        );

    public Task<DocumentCacheAdministrativeClearBatchResult> ClearDocumentProjectionWorkBatchAsync(
        IRelationalWriteSession mutexSession,
        DocumentCacheAdministrativeClearBatchRequest request,
        DocumentCacheAdministrativeWorkClearance clearance,
        CancellationToken cancellationToken = default
    ) =>
        DocumentCacheAdministrativePrimitivesSupport.ClearDocumentProjectionWorkBatchAsync(
            mutexSession,
            Commands,
            request,
            clearance,
            cancellationToken
        );

    public Task<DocumentCacheAdministrativeProjectedStateEmptinessResult> ReadProjectedStateEmptinessAsync(
        IRelationalWriteSession mutexSession,
        CancellationToken cancellationToken = default
    ) =>
        DocumentCacheAdministrativePrimitivesSupport.ReadProjectedStateEmptinessAsync(
            mutexSession,
            Commands,
            cancellationToken
        );

    public Task<DocumentCacheAdministrativeBaselineBoundaryResult> CaptureBaselineBoundaryAsync(
        IRelationalWriteSession mutexSession,
        CancellationToken cancellationToken = default
    ) =>
        DocumentCacheAdministrativePrimitivesSupport.CaptureBaselineBoundaryAsync(
            mutexSession,
            Commands,
            cancellationToken
        );

    public Task<DocumentCacheAdministrativeWorkHighWaterObservationResult> ObserveWorkHighWaterAsync(
        IRelationalWriteSession mutexSession,
        DocumentCacheAdministrativeWorkHighWaterObservationRequest request,
        CancellationToken cancellationToken = default
    ) =>
        DocumentCacheAdministrativePrimitivesSupport.ObserveWorkHighWaterAsync(
            mutexSession,
            Commands,
            request,
            cancellationToken
        );

    public Task<DocumentCacheAdministrativeBaselineSeedPageResult> SeedBaselinePageAsync(
        IRelationalWriteSession mutexSession,
        DocumentCacheAdministrativeBaselineSeedPageRequest request,
        CancellationToken cancellationToken = default
    ) =>
        DocumentCacheAdministrativePrimitivesSupport.SeedBaselinePageAsync(
            mutexSession,
            Commands,
            request,
            cancellationToken
        );

    public Task<DocumentCacheAdministrativeScrubPageResult> ScrubPageAsync(
        IRelationalWriteSession mutexSession,
        DocumentCacheAdministrativeScrubPageRequest request,
        CancellationToken cancellationToken = default
    ) =>
        DocumentCacheAdministrativePrimitivesSupport.ScrubPageAsync(
            mutexSession,
            Commands,
            request,
            cancellationToken
        );
}
