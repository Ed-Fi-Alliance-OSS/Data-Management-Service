// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Core.DocumentCache;

public sealed record DocumentCacheTargetConnectionInput
{
    public DocumentCacheTargetConnectionInput(RelationalProviderToken providerToken, string value)
    {
        ArgumentNullException.ThrowIfNull(providerToken);

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Target connection input must not be blank.", nameof(value));
        }

        ProviderToken = providerToken;
        Value = value;
    }

    public RelationalProviderToken ProviderToken { get; }

    public string Value { get; }
}

public sealed record DocumentCacheTargetDataStoreMetadata(long Id, string DataStoreType);

public sealed record DocumentCacheTargetExecutionContext
{
    public DocumentCacheTargetExecutionContext(
        DocumentCacheTargetKey targetKey,
        DocumentCacheTargetContextGeneration generation,
        DocumentCacheTargetEffectiveSettings effectiveSettings,
        DocumentCacheTargetDataStoreMetadata dataStore,
        DocumentCacheTargetConnectionInput connectionInput,
        DocumentCachePhysicalSourceFingerprint physicalSourceFingerprint,
        DocumentCacheLifecycleObservation lifecycle,
        DocumentCacheInventoryValidationResult inventory,
        DocumentCacheEnqueueTriggerValidationResult enqueueTrigger,
        DocumentCacheSqlServerPrerequisiteDetails? sqlServerPrerequisites
    )
    {
        TargetKey = targetKey;
        Generation = generation;
        EffectiveSettings = effectiveSettings;
        DataStore = dataStore;
        ConnectionInput = connectionInput;
        ProviderToken = connectionInput.ProviderToken;
        PhysicalSourceFingerprint = physicalSourceFingerprint;
        Lifecycle = lifecycle;
        Inventory = inventory;
        EnqueueTrigger = enqueueTrigger;
        SqlServerPrerequisites = sqlServerPrerequisites;
    }

    public DocumentCacheTargetKey TargetKey { get; }

    public DocumentCacheTargetContextGeneration Generation { get; }

    public DocumentCacheTargetEffectiveSettings EffectiveSettings { get; }

    public DocumentCacheTargetDataStoreMetadata DataStore { get; }

    public DocumentCacheTargetConnectionInput ConnectionInput { get; }

    public RelationalProviderToken ProviderToken { get; }

    public DocumentCachePhysicalSourceFingerprint PhysicalSourceFingerprint { get; }

    public DocumentCacheLifecycleObservation Lifecycle { get; }

    public DocumentCacheInventoryValidationResult Inventory { get; }

    public DocumentCacheEnqueueTriggerValidationResult EnqueueTrigger { get; }

    public DocumentCacheSqlServerPrerequisiteDetails? SqlServerPrerequisites { get; }
}

public sealed record DocumentCacheTargetContextBuildResult(
    DocumentCacheTargetObservation Observation,
    DocumentCacheTargetExecutionContext? ExecutionContext
)
{
    public bool HasExecutionContext => ExecutionContext is not null;
}

public interface IDocumentCacheTargetContextBuilder
{
    Task<DocumentCacheTargetContextBuildResult> BuildAsync(
        DocumentCacheTargetKey targetKey,
        DocumentCacheTargetContextGeneration generation,
        CancellationToken cancellationToken = default
    );
}

public sealed class DocumentCacheTargetContextBuilder(
    IDataStoreProvider dataStoreProvider,
    IOptions<DocumentCacheOptions> options,
    DocumentCacheProcessProviderToken processProviderToken,
    IDocumentCachePhysicalSourceFingerprintReader fingerprintReader,
    IDocumentCacheLifecycleReader lifecycleReader,
    IDocumentCacheInventoryValidator inventoryValidator,
    IDocumentCacheProviderPrerequisiteValidator prerequisiteValidator,
    ILogger<DocumentCacheTargetContextBuilder> logger
) : IDocumentCacheTargetContextBuilder
{
    public async Task<DocumentCacheTargetContextBuildResult> BuildAsync(
        DocumentCacheTargetKey targetKey,
        DocumentCacheTargetContextGeneration generation,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(targetKey);
        ArgumentNullException.ThrowIfNull(generation);

        DocumentCacheTargetEffectiveSettings effectiveSettings =
            DocumentCacheTargetEffectiveSettings.FromOptions(options.Value);

        DataStore? dataStore = dataStoreProvider.GetById(
            targetKey.DataStoreId,
            targetKey.TenantKey.Length == 0 ? null : targetKey.TenantKey
        );

        if (dataStore is null)
        {
            DocumentCacheResolutionRetryState retryState = new(
                attemptCount: 0,
                lastAttemptedAt: null,
                nextRetryAt: null,
                DocumentCacheTargetDiagnosticCategory.TargetUnresolved,
                "Configured target is not loaded from CMS."
            );
            DocumentCacheTargetDiagnostic diagnostic = CreateDiagnostic(
                targetKey,
                DocumentCacheTargetResolutionState.Unresolved,
                providerToken: null,
                generation: null,
                physicalSourceFingerprint: null,
                lifecycle: null,
                inventory: null,
                enqueueTrigger: null,
                sqlServerPrerequisites: null,
                retryState,
                DocumentCacheTargetDiagnosticCategory.TargetUnresolved,
                "Configured target is not loaded from CMS."
            );

            return new DocumentCacheTargetContextBuildResult(
                DocumentCacheTargetObservation.Unresolved(
                    targetKey,
                    effectiveSettings,
                    retryState,
                    [diagnostic]
                ),
                ExecutionContext: null
            );
        }

        RelationalProviderToken? providerToken = dataStore.RelationalProviderToken;
        DocumentCacheTargetDiagnosticCategory? providerMetadataFailureCategory =
            GetProviderMetadataFailureCategory(dataStore);

        if (providerMetadataFailureCategory is not null)
        {
            DocumentCacheTargetDiagnostic diagnostic = CreateDiagnostic(
                targetKey,
                DocumentCacheTargetResolutionState.Resolved,
                providerToken,
                generation,
                physicalSourceFingerprint: null,
                lifecycle: null,
                inventory: null,
                enqueueTrigger: null,
                sqlServerPrerequisites: null,
                retryState: null,
                providerMetadataFailureCategory.Value,
                providerMetadataFailureCategory
                == DocumentCacheTargetDiagnosticCategory.ProviderMetadataMissing
                    ? "Resolved target is missing relational provider metadata."
                    : "Resolved target has unknown relational provider metadata."
            );

            return Ineligible(targetKey, effectiveSettings, generation, providerToken, [diagnostic]);
        }

        providerToken = dataStore.RelationalProviderToken!;
        if (providerToken != processProviderToken.ProviderToken)
        {
            DocumentCacheTargetDiagnostic diagnostic = CreateDiagnostic(
                targetKey,
                DocumentCacheTargetResolutionState.Resolved,
                providerToken,
                generation,
                physicalSourceFingerprint: null,
                lifecycle: null,
                inventory: null,
                enqueueTrigger: null,
                sqlServerPrerequisites: null,
                retryState: null,
                DocumentCacheTargetDiagnosticCategory.ProviderMismatch,
                "Resolved target provider does not match this DMS process provider."
            );

            return Ineligible(targetKey, effectiveSettings, generation, providerToken, [diagnostic]);
        }

        if (string.IsNullOrWhiteSpace(dataStore.ConnectionString))
        {
            DocumentCacheTargetDiagnostic diagnostic = CreateDiagnostic(
                targetKey,
                DocumentCacheTargetResolutionState.Resolved,
                providerToken,
                generation,
                physicalSourceFingerprint: null,
                lifecycle: null,
                inventory: null,
                enqueueTrigger: null,
                sqlServerPrerequisites: null,
                retryState: null,
                DocumentCacheTargetDiagnosticCategory.ConnectionInputMissing,
                "Resolved target has no usable connection input."
            );

            return Ineligible(targetKey, effectiveSettings, generation, providerToken, [diagnostic]);
        }

        if (!AdaptersMatch(providerToken))
        {
            DocumentCacheTargetDiagnostic diagnostic = CreateDiagnostic(
                targetKey,
                DocumentCacheTargetResolutionState.Resolved,
                providerToken,
                generation,
                physicalSourceFingerprint: null,
                lifecycle: null,
                inventory: null,
                enqueueTrigger: null,
                sqlServerPrerequisites: null,
                retryState: null,
                DocumentCacheTargetDiagnosticCategory.UnexpectedProviderFailure,
                "DocumentCache provider services do not match this DMS process provider."
            );

            return Ineligible(targetKey, effectiveSettings, generation, providerToken, [diagnostic]);
        }

        string connectionValue = dataStore.ConnectionString;

        DocumentCachePhysicalSourceFingerprintReadResult fingerprintResult = await ReadFingerprintAsync(
                connectionValue,
                cancellationToken
            )
            .ConfigureAwait(false);
        DocumentCacheLifecycleReadResult lifecycleResult = await ReadLifecycleAsync(
                connectionValue,
                cancellationToken
            )
            .ConfigureAwait(false);
        DocumentCacheProviderInventoryValidationResult inventoryResult = await ValidateInventoryAsync(
                connectionValue,
                cancellationToken
            )
            .ConfigureAwait(false);

        DocumentCacheProviderPrerequisiteValidationResult? prerequisiteResult = null;
        if (lifecycleResult.Lifecycle is not null)
        {
            prerequisiteResult = await ValidatePrerequisitesAsync(
                    connectionValue,
                    lifecycleResult.Lifecycle,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }

        List<DocumentCacheTargetDiagnostic> diagnostics = BuildDiagnostics(
            targetKey,
            generation,
            providerToken,
            fingerprintResult,
            lifecycleResult,
            inventoryResult,
            prerequisiteResult
        );

        bool eligible =
            fingerprintResult.Fingerprint is not null
            && lifecycleResult.Lifecycle is not null
            && inventoryResult.IsSatisfied
            && prerequisiteResult?.IsSatisfied == true;

        if (!eligible)
        {
            return new DocumentCacheTargetContextBuildResult(
                DocumentCacheTargetObservation.ResolvedIneligible(
                    targetKey,
                    effectiveSettings,
                    generation,
                    providerToken,
                    fingerprintResult.Fingerprint,
                    lifecycleResult.Lifecycle,
                    inventoryResult.Inventory,
                    inventoryResult.EnqueueTrigger,
                    prerequisiteResult?.SqlServerPrerequisites,
                    retryState: null,
                    diagnostics
                ),
                ExecutionContext: null
            );
        }

        DocumentCacheTargetConnectionInput connectionInput = new(providerToken, connectionValue);
        DocumentCacheTargetExecutionContext executionContext = new(
            targetKey,
            generation,
            effectiveSettings,
            new DocumentCacheTargetDataStoreMetadata(dataStore.Id, dataStore.DataStoreType),
            connectionInput,
            fingerprintResult.Fingerprint!,
            lifecycleResult.Lifecycle!,
            inventoryResult.Inventory,
            inventoryResult.EnqueueTrigger,
            prerequisiteResult!.SqlServerPrerequisites
        );

        return new DocumentCacheTargetContextBuildResult(
            DocumentCacheTargetObservation.ResolvedEligible(
                targetKey,
                effectiveSettings,
                generation,
                providerToken,
                fingerprintResult.Fingerprint!,
                lifecycleResult.Lifecycle!,
                inventoryResult.Inventory,
                inventoryResult.EnqueueTrigger,
                prerequisiteResult.SqlServerPrerequisites
            ),
            executionContext
        );
    }

    private bool AdaptersMatch(RelationalProviderToken providerToken) =>
        fingerprintReader.ProviderToken == providerToken
        && lifecycleReader.ProviderToken == providerToken
        && inventoryValidator.ProviderToken == providerToken
        && prerequisiteValidator.ProviderToken == providerToken;

    private async Task<DocumentCachePhysicalSourceFingerprintReadResult> ReadFingerprintAsync(
        string connectionValue,
        CancellationToken cancellationToken
    )
    {
        try
        {
            return await fingerprintReader
                .ReadFingerprintAsync(connectionValue, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "DocumentCache target fingerprint read failed");
            return DocumentCachePhysicalSourceFingerprintReadResult.Failure(
                DocumentCachePhysicalSourceFingerprintReadStatus.SourceIdentityUnreadable,
                "Physical source fingerprint is unreadable."
            );
        }
    }

    private async Task<DocumentCacheLifecycleReadResult> ReadLifecycleAsync(
        string connectionValue,
        CancellationToken cancellationToken
    )
    {
        try
        {
            return await lifecycleReader.ReadLifecycleAsync(connectionValue, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "DocumentCache target lifecycle read failed");
            return DocumentCacheLifecycleReadResult.Failure(
                DocumentCacheLifecycleReadStatus.Unreadable,
                "DocumentCache lifecycle is unreadable."
            );
        }
    }

    private async Task<DocumentCacheProviderInventoryValidationResult> ValidateInventoryAsync(
        string connectionValue,
        CancellationToken cancellationToken
    )
    {
        try
        {
            return await inventoryValidator.ValidateInventoryAsync(connectionValue, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "DocumentCache target inventory validation failed");
            return new DocumentCacheProviderInventoryValidationResult(
                new DocumentCacheInventoryValidationResult(
                    DocumentCacheInventoryStatus.Unreadable,
                    "DocumentCache inventory is unreadable."
                ),
                new DocumentCacheEnqueueTriggerValidationResult(
                    DocumentCacheEnqueueTriggerStatus.Unreadable,
                    "DocumentCache enqueue inventory is unreadable."
                )
            );
        }
    }

    private async Task<DocumentCacheProviderPrerequisiteValidationResult> ValidatePrerequisitesAsync(
        string connectionValue,
        DocumentCacheLifecycleObservation lifecycle,
        CancellationToken cancellationToken
    )
    {
        try
        {
            return await prerequisiteValidator
                .ValidateInitializationAsync(connectionValue, lifecycle, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "DocumentCache target provider prerequisite validation failed");
            return DocumentCacheProviderPrerequisiteValidationResult.Initialization(
                new DocumentCacheSqlServerPrerequisiteDetails(
                    new DocumentCacheProviderPrerequisiteResult(
                        DocumentCacheProviderPrerequisiteName.ReadCommittedSnapshot,
                        DocumentCacheProviderPrerequisiteStatus.Unreadable,
                        "Provider prerequisite is unreadable."
                    ),
                    new DocumentCacheProviderPrerequisiteResult(
                        DocumentCacheProviderPrerequisiteName.NestedTriggers,
                        DocumentCacheProviderPrerequisiteStatus.Unreadable,
                        "Provider prerequisite is unreadable."
                    )
                ),
                lifecycle
            );
        }
    }

    private static List<DocumentCacheTargetDiagnostic> BuildDiagnostics(
        DocumentCacheTargetKey targetKey,
        DocumentCacheTargetContextGeneration generation,
        RelationalProviderToken providerToken,
        DocumentCachePhysicalSourceFingerprintReadResult fingerprintResult,
        DocumentCacheLifecycleReadResult lifecycleResult,
        DocumentCacheProviderInventoryValidationResult inventoryResult,
        DocumentCacheProviderPrerequisiteValidationResult? prerequisiteResult
    )
    {
        List<DocumentCacheTargetDiagnostic> diagnostics = [];

        if (!fingerprintResult.Succeeded)
        {
            diagnostics.Add(
                CreateDiagnostic(
                    targetKey,
                    DocumentCacheTargetResolutionState.Resolved,
                    providerToken,
                    generation,
                    physicalSourceFingerprint: null,
                    lifecycleResult.Lifecycle,
                    inventoryResult.Inventory,
                    inventoryResult.EnqueueTrigger,
                    prerequisiteResult?.SqlServerPrerequisites,
                    retryState: null,
                    DocumentCacheTargetDiagnosticCategory.PhysicalSourceFingerprintFailure,
                    fingerprintResult.Message
                )
            );
        }

        if (!lifecycleResult.Succeeded)
        {
            diagnostics.Add(
                CreateDiagnostic(
                    targetKey,
                    DocumentCacheTargetResolutionState.Resolved,
                    providerToken,
                    generation,
                    fingerprintResult.Fingerprint,
                    lifecycle: null,
                    inventoryResult.Inventory,
                    inventoryResult.EnqueueTrigger,
                    prerequisiteResult?.SqlServerPrerequisites,
                    retryState: null,
                    DocumentCacheTargetDiagnosticCategory.LifecycleObservationFailure,
                    lifecycleResult.Message
                )
            );
        }

        if (inventoryResult.Inventory.Status != DocumentCacheInventoryStatus.Satisfied)
        {
            diagnostics.Add(
                CreateDiagnostic(
                    targetKey,
                    DocumentCacheTargetResolutionState.Resolved,
                    providerToken,
                    generation,
                    fingerprintResult.Fingerprint,
                    lifecycleResult.Lifecycle,
                    inventoryResult.Inventory,
                    inventoryResult.EnqueueTrigger,
                    prerequisiteResult?.SqlServerPrerequisites,
                    retryState: null,
                    DocumentCacheTargetDiagnosticCategory.InventoryFailure,
                    inventoryResult.Inventory.Message
                )
            );
        }

        if (inventoryResult.EnqueueTrigger.Status != DocumentCacheEnqueueTriggerStatus.Satisfied)
        {
            diagnostics.Add(
                CreateDiagnostic(
                    targetKey,
                    DocumentCacheTargetResolutionState.Resolved,
                    providerToken,
                    generation,
                    fingerprintResult.Fingerprint,
                    lifecycleResult.Lifecycle,
                    inventoryResult.Inventory,
                    inventoryResult.EnqueueTrigger,
                    prerequisiteResult?.SqlServerPrerequisites,
                    retryState: null,
                    DocumentCacheTargetDiagnosticCategory.EnqueueTriggerFailure,
                    inventoryResult.EnqueueTrigger.Message
                )
            );
        }

        if (prerequisiteResult?.FailureCategory is not null)
        {
            diagnostics.Add(
                CreateDiagnostic(
                    targetKey,
                    DocumentCacheTargetResolutionState.Resolved,
                    providerToken,
                    generation,
                    fingerprintResult.Fingerprint,
                    lifecycleResult.Lifecycle,
                    inventoryResult.Inventory,
                    inventoryResult.EnqueueTrigger,
                    prerequisiteResult.SqlServerPrerequisites,
                    retryState: null,
                    prerequisiteResult.FailureCategory.Value,
                    prerequisiteResult.Message
                )
            );
        }

        return diagnostics;
    }

    private static DocumentCacheTargetDiagnosticCategory? GetProviderMetadataFailureCategory(
        DataStore dataStore
    ) =>
        dataStore.RelationalProviderMetadataStatus switch
        {
            RelationalProviderMetadataStatus.Missing =>
                DocumentCacheTargetDiagnosticCategory.ProviderMetadataMissing,
            RelationalProviderMetadataStatus.Unknown =>
                DocumentCacheTargetDiagnosticCategory.ProviderMetadataUnknown,
            RelationalProviderMetadataStatus.Supported when dataStore.RelationalProviderToken is null =>
                DocumentCacheTargetDiagnosticCategory.ProviderMetadataMissing,
            RelationalProviderMetadataStatus.Supported => null,
            _ => DocumentCacheTargetDiagnosticCategory.ProviderMetadataUnknown,
        };

    private static DocumentCacheTargetContextBuildResult Ineligible(
        DocumentCacheTargetKey targetKey,
        DocumentCacheTargetEffectiveSettings effectiveSettings,
        DocumentCacheTargetContextGeneration generation,
        RelationalProviderToken? providerToken,
        IEnumerable<DocumentCacheTargetDiagnostic> diagnostics
    ) =>
        new(
            DocumentCacheTargetObservation.ResolvedIneligible(
                targetKey,
                effectiveSettings,
                generation,
                providerToken,
                physicalSourceFingerprint: null,
                lifecycle: null,
                inventory: null,
                enqueueTrigger: null,
                sqlServerPrerequisites: null,
                retryState: null,
                diagnostics
            ),
            ExecutionContext: null
        );

    private static DocumentCacheTargetDiagnostic CreateDiagnostic(
        DocumentCacheTargetKey targetKey,
        DocumentCacheTargetResolutionState resolutionState,
        RelationalProviderToken? providerToken,
        DocumentCacheTargetContextGeneration? generation,
        DocumentCachePhysicalSourceFingerprint? physicalSourceFingerprint,
        DocumentCacheLifecycleObservation? lifecycle,
        DocumentCacheInventoryValidationResult? inventory,
        DocumentCacheEnqueueTriggerValidationResult? enqueueTrigger,
        DocumentCacheSqlServerPrerequisiteDetails? sqlServerPrerequisites,
        DocumentCacheResolutionRetryState? retryState,
        DocumentCacheTargetDiagnosticCategory category,
        string message
    ) =>
        new(
            targetKey,
            resolutionState,
            providerToken,
            generation,
            physicalSourceFingerprint,
            lifecycle,
            inventory,
            enqueueTrigger,
            sqlServerPrerequisites,
            retryState,
            category,
            message
        );
}
