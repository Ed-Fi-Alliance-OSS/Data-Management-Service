// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.Startup;
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

    public override string ToString() =>
        $"{nameof(DocumentCacheTargetConnectionInput)} {{ ProviderToken = {ProviderToken}, Value = <redacted> }}";
}

public sealed record DocumentCacheTargetDataStoreMetadata(long Id, string DataStoreType);

public sealed record DocumentCacheResolvedTargetDataStore(
    long Id,
    string DataStoreType,
    RelationalProviderMetadataStatus RelationalProviderMetadataStatus,
    RelationalProviderToken? RelationalProviderToken,
    string? ConnectionFactoryInput
)
{
    public static DocumentCacheResolvedTargetDataStore From(DataStore dataStore)
    {
        ArgumentNullException.ThrowIfNull(dataStore);

        return new(
            dataStore.Id,
            dataStore.DataStoreType,
            dataStore.RelationalProviderMetadataStatus,
            dataStore.RelationalProviderToken,
            dataStore.ConnectionString
        );
    }
}

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
        DocumentCacheResolvedTargetDataStore resolvedDataStore,
        DocumentCacheTargetContextGeneration generation,
        CancellationToken cancellationToken = default
    );
}

public sealed class DocumentCacheTargetContextBuilder(
    IOptions<DocumentCacheOptions> options,
    DocumentCacheProcessProviderToken processProviderToken,
    IEffectiveSchemaSetProvider effectiveSchemaSetProvider,
    IDatabaseFingerprintReader databaseFingerprintReader,
    IResourceKeyValidator resourceKeyValidator,
    IDocumentCachePhysicalSourceFingerprintReader fingerprintReader,
    IDocumentCacheLifecycleReader lifecycleReader,
    IDocumentCacheInventoryValidator inventoryValidator,
    IDocumentCacheProviderPrerequisiteValidator prerequisiteValidator,
    ILogger<DocumentCacheTargetContextBuilder> logger
) : IDocumentCacheTargetContextBuilder
{
    public async Task<DocumentCacheTargetContextBuildResult> BuildAsync(
        DocumentCacheTargetKey targetKey,
        DocumentCacheResolvedTargetDataStore resolvedDataStore,
        DocumentCacheTargetContextGeneration generation,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(targetKey);
        ArgumentNullException.ThrowIfNull(resolvedDataStore);
        ArgumentNullException.ThrowIfNull(generation);

        DocumentCacheTargetEffectiveSettings effectiveSettings =
            DocumentCacheTargetEffectiveSettings.FromOptions(options.Value);

        RelationalProviderToken? providerToken = resolvedDataStore.RelationalProviderToken;
        DocumentCacheTargetDiagnosticCategory? providerMetadataFailureCategory =
            GetProviderMetadataFailureCategory(resolvedDataStore);

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

        providerToken = resolvedDataStore.RelationalProviderToken!;
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

        if (string.IsNullOrWhiteSpace(resolvedDataStore.ConnectionFactoryInput))
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

        string connectionValue = resolvedDataStore.ConnectionFactoryInput!;

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
        DocumentCacheProviderInventoryValidationResult combinedInventoryResult =
            CombineInventoryValidationResults(
                inventoryResult,
                fingerprintResult.ToInventoryValidationResult()
            );
        DocumentCacheTargetSchemaValidationResult schemaValidationResult = await ValidateTargetSchemaAsync(
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
            combinedInventoryResult,
            schemaValidationResult,
            prerequisiteResult
        );

        bool eligible =
            fingerprintResult.Fingerprint is not null
            && lifecycleResult.Lifecycle is not null
            && combinedInventoryResult.IsSatisfied
            && schemaValidationResult.IsSatisfied
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
                    combinedInventoryResult.Inventory,
                    combinedInventoryResult.EnqueueTrigger,
                    prerequisiteResult?.SqlServerPrerequisites,
                    retryState: null,
                    diagnostics,
                    combinedInventoryResult.InventoryComponents,
                    lifecycleResult.Status
                ),
                ExecutionContext: null
            );
        }

        DocumentCacheTargetConnectionInput connectionInput = new(providerToken, connectionValue);
        DocumentCacheTargetExecutionContext executionContext = new(
            targetKey,
            generation,
            effectiveSettings,
            new DocumentCacheTargetDataStoreMetadata(resolvedDataStore.Id, resolvedDataStore.DataStoreType),
            connectionInput,
            fingerprintResult.Fingerprint!,
            lifecycleResult.Lifecycle!,
            combinedInventoryResult.Inventory,
            combinedInventoryResult.EnqueueTrigger,
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
                combinedInventoryResult.Inventory,
                combinedInventoryResult.EnqueueTrigger,
                prerequisiteResult.SqlServerPrerequisites,
                inventoryComponents: combinedInventoryResult.InventoryComponents
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
            LogProviderFailure(
                DocumentCacheTargetDiagnosticCategory.PhysicalSourceFingerprintFailure,
                "physical source fingerprint read",
                exception
            );
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
            LogProviderFailure(
                DocumentCacheTargetDiagnosticCategory.LifecycleObservationFailure,
                "lifecycle read",
                exception
            );
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
            LogProviderFailure(
                DocumentCacheTargetDiagnosticCategory.InventoryFailure,
                "inventory validation",
                exception
            );
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

    private static DocumentCacheProviderInventoryValidationResult CombineInventoryValidationResults(
        DocumentCacheProviderInventoryValidationResult providerInventoryResult,
        DocumentCacheInventoryValidationResult sourceIdentityInventory
    )
    {
        if (sourceIdentityInventory.Status == DocumentCacheInventoryStatus.Satisfied)
        {
            return providerInventoryResult;
        }

        if (providerInventoryResult.Inventory.Status == DocumentCacheInventoryStatus.Satisfied)
        {
            return new DocumentCacheProviderInventoryValidationResult(
                sourceIdentityInventory,
                providerInventoryResult.InventoryComponents with
                {
                    DataStoreIdentity = sourceIdentityInventory,
                },
                providerInventoryResult.EnqueueTrigger
            );
        }

        DocumentCacheInventoryValidationComponents inventoryComponents =
            providerInventoryResult.InventoryComponents with
            {
                DataStoreIdentity = CombineInventoryResult(
                    providerInventoryResult.InventoryComponents.DataStoreIdentity,
                    sourceIdentityInventory
                ),
            };

        return new DocumentCacheProviderInventoryValidationResult(
            CombineInventoryResult(providerInventoryResult.Inventory, sourceIdentityInventory),
            inventoryComponents,
            providerInventoryResult.EnqueueTrigger
        );
    }

    private static DocumentCacheInventoryValidationResult CombineInventoryResult(
        DocumentCacheInventoryValidationResult providerResult,
        DocumentCacheInventoryValidationResult sourceIdentityInventory
    ) =>
        new(
            CombineInventoryStatus(providerResult.Status, sourceIdentityInventory.Status),
            CombineInventoryMessages(providerResult.Message, sourceIdentityInventory.Message)
        );

    private static DocumentCacheInventoryStatus CombineInventoryStatus(
        DocumentCacheInventoryStatus providerStatus,
        DocumentCacheInventoryStatus sourceIdentityStatus
    )
    {
        if (
            providerStatus == DocumentCacheInventoryStatus.Unreadable
            || sourceIdentityStatus == DocumentCacheInventoryStatus.Unreadable
        )
        {
            return DocumentCacheInventoryStatus.Unreadable;
        }

        if (
            providerStatus == DocumentCacheInventoryStatus.Invalid
            || sourceIdentityStatus == DocumentCacheInventoryStatus.Invalid
        )
        {
            return DocumentCacheInventoryStatus.Invalid;
        }

        if (
            providerStatus == DocumentCacheInventoryStatus.Missing
            || sourceIdentityStatus == DocumentCacheInventoryStatus.Missing
        )
        {
            return DocumentCacheInventoryStatus.Missing;
        }

        return sourceIdentityStatus == DocumentCacheInventoryStatus.NotEvaluated
            ? providerStatus
            : sourceIdentityStatus;
    }

    private static string CombineInventoryMessages(string providerMessage, string sourceIdentityMessage) =>
        providerMessage == sourceIdentityMessage
            ? providerMessage
            : $"{providerMessage} {sourceIdentityMessage}";

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
            DocumentCacheTargetDiagnosticCategory failureCategory =
                lifecycle.State == DocumentCacheLifecycleState.Disabled
                    ? DocumentCacheTargetDiagnosticCategory.ProviderPrerequisiteFailed
                    : DocumentCacheTargetDiagnosticCategory.UnsupportedPrerequisiteIncident;
            LogProviderFailure(failureCategory, "provider prerequisite validation", exception);
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

    private async Task<DocumentCacheTargetSchemaValidationResult> ValidateTargetSchemaAsync(
        string connectionValue,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        EffectiveSchemaInfo effectiveSchema;
        try
        {
            effectiveSchema = effectiveSchemaSetProvider.EffectiveSchemaSet.EffectiveSchema;
        }
        catch (Exception exception)
        {
            LogProviderFailure(
                DocumentCacheTargetDiagnosticCategory.EffectiveSchemaCompatibilityFailure,
                "runtime effective schema access",
                exception
            );
            return DocumentCacheTargetSchemaValidationResult.Failure(
                DocumentCacheTargetDiagnosticCategory.EffectiveSchemaCompatibilityFailure,
                "Runtime effective schema is unavailable for DocumentCache target compatibility validation."
            );
        }

        DatabaseFingerprint? databaseFingerprint;
        try
        {
            databaseFingerprint = await databaseFingerprintReader
                .ReadFingerprintAsync(connectionValue)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DatabaseFingerprintValidationException exception)
        {
            LogProviderFailure(
                DocumentCacheTargetDiagnosticCategory.EffectiveSchemaCompatibilityFailure,
                "target effective schema fingerprint read",
                exception
            );
            return DocumentCacheTargetSchemaValidationResult.Failure(
                DocumentCacheTargetDiagnosticCategory.EffectiveSchemaCompatibilityFailure,
                "Target EffectiveSchema fingerprint is invalid."
            );
        }
        catch (Exception exception)
        {
            LogProviderFailure(
                DocumentCacheTargetDiagnosticCategory.EffectiveSchemaCompatibilityFailure,
                "target effective schema fingerprint read",
                exception
            );
            return DocumentCacheTargetSchemaValidationResult.Failure(
                DocumentCacheTargetDiagnosticCategory.EffectiveSchemaCompatibilityFailure,
                "Target EffectiveSchema fingerprint is unreadable."
            );
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (databaseFingerprint is null)
        {
            return DocumentCacheTargetSchemaValidationResult.Failure(
                DocumentCacheTargetDiagnosticCategory.EffectiveSchemaCompatibilityFailure,
                "Target EffectiveSchema fingerprint is missing."
            );
        }

        if (
            !string.Equals(
                databaseFingerprint.EffectiveSchemaHash,
                effectiveSchema.EffectiveSchemaHash,
                StringComparison.Ordinal
            )
        )
        {
            return DocumentCacheTargetSchemaValidationResult.Failure(
                DocumentCacheTargetDiagnosticCategory.EffectiveSchemaCompatibilityFailure,
                "Target EffectiveSchema fingerprint does not match the selected runtime mapping set."
            );
        }

        bool resourceKeySeedFingerprintMatches =
            databaseFingerprint.ResourceKeyCount == effectiveSchema.ResourceKeyCount
            && databaseFingerprint
                .ResourceKeySeedHash.AsSpan()
                .SequenceEqual(effectiveSchema.ResourceKeySeedHash.AsSpan());

        ResourceKeyValidationResult resourceKeyValidationResult;
        try
        {
            resourceKeyValidationResult = await resourceKeyValidator
                .ValidateAsync(
                    databaseFingerprint,
                    effectiveSchema.ResourceKeyCount,
                    [.. effectiveSchema.ResourceKeySeedHash],
                    effectiveSchema.ResourceKeysInIdOrder.ToResourceKeyRows(),
                    connectionValue,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogProviderFailure(
                DocumentCacheTargetDiagnosticCategory.ResourceKeyCompatibilityFailure,
                "target resource key seed validation",
                exception
            );
            return DocumentCacheTargetSchemaValidationResult.Failure(
                DocumentCacheTargetDiagnosticCategory.ResourceKeyCompatibilityFailure,
                "Target ResourceKey seed validation is unreadable."
            );
        }

        if (!resourceKeySeedFingerprintMatches)
        {
            return DocumentCacheTargetSchemaValidationResult.Failure(
                DocumentCacheTargetDiagnosticCategory.ResourceKeyCompatibilityFailure,
                "Target ResourceKey seed fingerprint does not match the selected runtime mapping set."
            );
        }

        return resourceKeyValidationResult switch
        {
            ResourceKeyValidationResult.ValidationSuccess =>
                DocumentCacheTargetSchemaValidationResult.Success(),
            ResourceKeyValidationResult.ValidationFailure =>
                DocumentCacheTargetSchemaValidationResult.Failure(
                    DocumentCacheTargetDiagnosticCategory.ResourceKeyCompatibilityFailure,
                    "Target dms.ResourceKey seed does not match the selected runtime mapping set."
                ),
            _ => DocumentCacheTargetSchemaValidationResult.Failure(
                DocumentCacheTargetDiagnosticCategory.ResourceKeyCompatibilityFailure,
                "Target ResourceKey seed validation returned an unsupported result."
            ),
        };
    }

    private void LogProviderFailure(
        DocumentCacheTargetDiagnosticCategory failureCategory,
        string operation,
        Exception exception
    )
    {
        logger.LogDebug(
            "DocumentCache target provider operation failed for category {FailureCategory} during {Operation}; exception type {ExceptionType}",
            failureCategory,
            operation,
            exception.GetType().Name
        );
    }

    private static List<DocumentCacheTargetDiagnostic> BuildDiagnostics(
        DocumentCacheTargetKey targetKey,
        DocumentCacheTargetContextGeneration generation,
        RelationalProviderToken providerToken,
        DocumentCachePhysicalSourceFingerprintReadResult fingerprintResult,
        DocumentCacheLifecycleReadResult lifecycleResult,
        DocumentCacheProviderInventoryValidationResult inventoryResult,
        DocumentCacheTargetSchemaValidationResult schemaValidationResult,
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

        if (!schemaValidationResult.IsSatisfied)
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
                    schemaValidationResult.FailureCategory!.Value,
                    schemaValidationResult.Message
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
        DocumentCacheResolvedTargetDataStore dataStore
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

internal sealed record DocumentCacheTargetSchemaValidationResult
{
    private DocumentCacheTargetSchemaValidationResult(
        bool isSatisfied,
        DocumentCacheTargetDiagnosticCategory? failureCategory,
        string message
    )
    {
        if (isSatisfied && failureCategory is not null)
        {
            throw new ArgumentException(
                "Satisfied schema validation results must not carry a failure category."
            );
        }

        if (!isSatisfied && failureCategory is null)
        {
            throw new ArgumentException("Failed schema validation results require a failure category.");
        }

        IsSatisfied = isSatisfied;
        FailureCategory = failureCategory;
        Message = DocumentCacheDiagnosticText.Sanitize(message);
    }

    public bool IsSatisfied { get; }

    public DocumentCacheTargetDiagnosticCategory? FailureCategory { get; }

    public string Message { get; }

    public static DocumentCacheTargetSchemaValidationResult Success() =>
        new(isSatisfied: true, failureCategory: null, "Target schema compatibility validated.");

    public static DocumentCacheTargetSchemaValidationResult Failure(
        DocumentCacheTargetDiagnosticCategory failureCategory,
        string message
    ) => new(isSatisfied: false, failureCategory, message);
}
