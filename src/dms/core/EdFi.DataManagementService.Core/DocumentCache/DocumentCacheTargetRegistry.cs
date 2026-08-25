// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using EdFi.DataManagementService.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Core.DocumentCache;

public enum DocumentCacheTargetRefreshReason
{
    Startup,
    CmsRefreshNotification,
    SupervisorTriggered,
}

public sealed record DocumentCacheTargetRegistrySnapshot
{
    public DocumentCacheTargetRegistrySnapshot(
        IEnumerable<DocumentCacheTargetObservation> targets,
        DateTimeOffset observedAt
    )
    {
        Targets = targets.ToImmutableArray();
        ObservedAt = observedAt;
    }

    public ImmutableArray<DocumentCacheTargetObservation> Targets { get; }

    public DateTimeOffset ObservedAt { get; }

    public DocumentCacheTargetObservation? GetTarget(DocumentCacheTargetKey targetKey)
    {
        ArgumentNullException.ThrowIfNull(targetKey);

        return Targets.FirstOrDefault(target => target.TargetKey.Equals(targetKey));
    }
}

public sealed record DocumentCacheTargetRuntimeSnapshot
{
    public DocumentCacheTargetRuntimeSnapshot(
        IEnumerable<DocumentCacheTargetExecutionContext> executionContexts,
        DateTimeOffset observedAt
    )
    {
        ExecutionContexts = executionContexts.ToImmutableArray();
        ObservedAt = observedAt;
    }

    public ImmutableArray<DocumentCacheTargetExecutionContext> ExecutionContexts { get; }

    public DateTimeOffset ObservedAt { get; }

    public DocumentCacheTargetExecutionContext? GetExecutionContext(DocumentCacheTargetKey targetKey)
    {
        ArgumentNullException.ThrowIfNull(targetKey);

        return ExecutionContexts.FirstOrDefault(context => context.TargetKey.Equals(targetKey));
    }

    public DocumentCacheTargetExecutionContext? GetExecutionContext(
        DocumentCacheTargetKey targetKey,
        DocumentCacheTargetContextGeneration generation
    )
    {
        ArgumentNullException.ThrowIfNull(targetKey);
        ArgumentNullException.ThrowIfNull(generation);

        return ExecutionContexts.FirstOrDefault(context =>
            context.TargetKey.Equals(targetKey) && context.Generation == generation
        );
    }
}

public sealed record DocumentCacheTargetStatusSnapshot
{
    public DocumentCacheTargetStatusSnapshot(
        DocumentCacheTargetRegistrySnapshot registrySnapshot,
        DocumentCacheTargetRuntimeSnapshot runtimeSnapshot
    )
    {
        RegistrySnapshot = registrySnapshot ?? throw new ArgumentNullException(nameof(registrySnapshot));
        RuntimeSnapshot = runtimeSnapshot ?? throw new ArgumentNullException(nameof(runtimeSnapshot));
    }

    public DocumentCacheTargetRegistrySnapshot RegistrySnapshot { get; }

    public DocumentCacheTargetRuntimeSnapshot RuntimeSnapshot { get; }

    public ImmutableArray<DocumentCacheTargetObservation> Targets => RegistrySnapshot.Targets;

    public DateTimeOffset RegistryObservedAt => RegistrySnapshot.ObservedAt;

    public DocumentCacheTargetObservation? GetTarget(DocumentCacheTargetKey targetKey) =>
        RegistrySnapshot.GetTarget(targetKey);

    public DocumentCacheTargetExecutionContext? GetExecutionContext(DocumentCacheTargetKey targetKey) =>
        RuntimeSnapshot.GetExecutionContext(targetKey);

    public DocumentCacheTargetExecutionContext? GetExecutionContext(
        DocumentCacheTargetKey targetKey,
        DocumentCacheTargetContextGeneration generation
    ) => RuntimeSnapshot.GetExecutionContext(targetKey, generation);
}

public interface IDocumentCacheTargetRegistry
{
    DocumentCacheTargetRegistrySnapshot CurrentSnapshot { get; }

    DocumentCacheTargetRuntimeSnapshot CurrentRuntimeSnapshot { get; }

    DocumentCacheTargetStatusSnapshot CurrentStatusSnapshot => new(CurrentSnapshot, CurrentRuntimeSnapshot);

    Task<DocumentCacheTargetRegistrySnapshot> RefreshAsync(
        DocumentCacheTargetRefreshReason reason,
        CancellationToken cancellationToken = default
    );
}

public sealed class DocumentCacheTargetRegistry(
    IDataStoreProvider dataStoreProvider,
    IDocumentCacheTargetContextBuilder targetContextBuilder,
    IOptions<DocumentCacheOptions> options,
    TimeProvider timeProvider,
    ILogger<DocumentCacheTargetRegistry> logger
) : IDocumentCacheTargetRegistry
{
    private readonly ImmutableArray<DocumentCacheTargetKey> _configuredTargetKeys = options
        .Value.GetTargetKeys()
        .ToImmutableArray();

    private readonly DocumentCacheTargetEffectiveSettings _effectiveSettings =
        DocumentCacheTargetEffectiveSettings.FromOptions(options.Value);

    private readonly object _snapshotLock = new();

    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private ImmutableDictionary<DocumentCacheTargetKey, TargetState> _targetStates = options
        .Value.GetTargetKeys()
        .ToImmutableDictionary(
            targetKey => targetKey,
            targetKey =>
            {
                DocumentCacheTargetObservation observation = DocumentCacheTargetObservation.Configured(
                    targetKey,
                    DocumentCacheTargetEffectiveSettings.FromOptions(options.Value)
                );

                return new TargetState(
                    StableObservation: observation,
                    SnapshotObservation: observation,
                    ExecutionContext: null,
                    Signature: null,
                    ProviderMetadataStatus: null,
                    RetryAttemptCount: 0,
                    LastGenerationValue: 0
                );
            }
        );

    private DocumentCacheTargetRegistrySnapshot _currentSnapshot = new(
        options
            .Value.GetTargetKeys()
            .Select(targetKey =>
                DocumentCacheTargetObservation.Configured(
                    targetKey,
                    DocumentCacheTargetEffectiveSettings.FromOptions(options.Value)
                )
            ),
        timeProvider.GetUtcNow()
    );

    public DocumentCacheTargetRegistrySnapshot CurrentSnapshot
    {
        get
        {
            lock (_snapshotLock)
            {
                return _currentSnapshot;
            }
        }
    }

    public DocumentCacheTargetRuntimeSnapshot CurrentRuntimeSnapshot
    {
        get
        {
            lock (_snapshotLock)
            {
                return CreateRuntimeSnapshot(_targetStates, timeProvider.GetUtcNow());
            }
        }
    }

    public DocumentCacheTargetStatusSnapshot CurrentStatusSnapshot
    {
        get
        {
            lock (_snapshotLock)
            {
                return CreateStatusSnapshot(_currentSnapshot, _targetStates);
            }
        }
    }

    public async Task<DocumentCacheTargetRegistrySnapshot> RefreshAsync(
        DocumentCacheTargetRefreshReason reason,
        CancellationToken cancellationToken = default
    )
    {
        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_configuredTargetKeys.IsEmpty)
            {
                DocumentCacheTargetRegistrySnapshot emptySnapshot = new([], timeProvider.GetUtcNow());
                lock (_snapshotLock)
                {
                    _currentSnapshot = emptySnapshot;
                }

                return emptySnapshot;
            }

            logger.LogDebug("Refreshing DocumentCache target registry for reason {RefreshReason}", reason);

            ImmutableDictionary<string, TenantRefreshResult> tenantRefreshResults =
                await RefreshConfiguredTenantsAsync(reason, cancellationToken).ConfigureAwait(false);

            ImmutableDictionary<DocumentCacheTargetKey, TargetState>.Builder nextStates =
                ImmutableDictionary.CreateBuilder<DocumentCacheTargetKey, TargetState>();

            foreach (DocumentCacheTargetKey targetKey in _configuredTargetKeys)
            {
                TargetState previousState = _targetStates[targetKey];
                string tenantKey = GetProviderTenantKey(targetKey);
                TenantRefreshResult tenantRefreshResult = tenantRefreshResults[tenantKey];

                TargetState nextState = tenantRefreshResult.Succeeded
                    ? await ResolveAfterSuccessfulTenantRefreshAsync(
                            targetKey,
                            previousState,
                            reason,
                            cancellationToken
                        )
                        .ConfigureAwait(false)
                    : ApplyTenantRefreshFailure(targetKey, previousState);

                nextStates.Add(targetKey, nextState);
            }

            ImmutableDictionary<DocumentCacheTargetKey, TargetState> nextTargetStates =
                nextStates.ToImmutable();
            DocumentCacheTargetRegistrySnapshot nextSnapshot = CreateSnapshot(nextTargetStates);
            lock (_snapshotLock)
            {
                _targetStates = nextTargetStates;
                _currentSnapshot = nextSnapshot;
            }

            return nextSnapshot;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<ImmutableDictionary<string, TenantRefreshResult>> RefreshConfiguredTenantsAsync(
        DocumentCacheTargetRefreshReason reason,
        CancellationToken cancellationToken
    )
    {
        ImmutableDictionary<string, TenantRefreshResult>.Builder refreshResults =
            ImmutableDictionary.CreateBuilder<string, TenantRefreshResult>(StringComparer.OrdinalIgnoreCase);

        foreach (
            string tenantKey in _configuredTargetKeys
                .Select(GetProviderTenantKey)
                .Distinct(StringComparer.OrdinalIgnoreCase)
        )
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                string? providerTenant = tenantKey.Length == 0 ? null : tenantKey;
                if (ShouldForceLoadTenant(reason, tenantKey))
                {
                    await dataStoreProvider
                        .LoadDataStores(providerTenant, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    await dataStoreProvider
                        .RefreshInstancesIfExpiredAsync(providerTenant, cancellationToken)
                        .ConfigureAwait(false);
                }

                refreshResults.Add(tenantKey, TenantRefreshResult.Success());
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                LogTenantRefreshFailure(exception);
                refreshResults.Add(tenantKey, TenantRefreshResult.Failure());
            }
        }

        return refreshResults.ToImmutable();
    }

    private bool ShouldForceLoadTenant(DocumentCacheTargetRefreshReason reason, string tenantKey)
    {
        if (reason != DocumentCacheTargetRefreshReason.SupervisorTriggered)
        {
            return true;
        }

        string? providerTenant = tenantKey.Length == 0 ? null : tenantKey;
        if (!dataStoreProvider.IsLoaded(providerTenant))
        {
            return true;
        }

        return _configuredTargetKeys
            .Where(targetKey =>
                string.Equals(GetProviderTenantKey(targetKey), tenantKey, StringComparison.OrdinalIgnoreCase)
            )
            .Any(targetKey =>
                _targetStates[targetKey].StableObservation.ResolutionState
                    != DocumentCacheTargetResolutionState.Resolved
                || CanForceLoadRecoverableReusableGeneration(_targetStates[targetKey].StableObservation)
            );
    }

    private void LogTenantRefreshFailure(Exception exception)
    {
        logger.LogDebug(
            "DocumentCache target registry refresh failed for category {FailureCategory}; exception type {ExceptionType}",
            DocumentCacheTargetDiagnosticCategory.TransientCmsRefreshFailure,
            exception.GetType().Name
        );
    }

    private async Task<TargetState> ResolveAfterSuccessfulTenantRefreshAsync(
        DocumentCacheTargetKey targetKey,
        TargetState previousState,
        DocumentCacheTargetRefreshReason reason,
        CancellationToken cancellationToken
    )
    {
        string? providerTenant = targetKey.TenantKey.Length == 0 ? null : targetKey.TenantKey;
        DataStore? dataStore = dataStoreProvider.GetById(targetKey.DataStoreId, providerTenant);

        if (dataStore is null)
        {
            return ApplyTargetUnresolved(targetKey, previousState);
        }

        DocumentCacheResolvedTargetDataStore resolvedDataStore = DocumentCacheResolvedTargetDataStore.From(
            dataStore
        );
        ResolvedTargetExecutionSignature signature = ResolvedTargetExecutionSignature.From(resolvedDataStore);
        bool hasReusableGeneration =
            previousState.Signature == signature
            && previousState.StableObservation.ResolutionState == DocumentCacheTargetResolutionState.Resolved
            && previousState.StableObservation.Generation is not null;

        if (hasReusableGeneration)
        {
            DocumentCacheTargetContextGeneration reusableGeneration = previousState
                .StableObservation
                .Generation!;
            if (
                previousState.ProviderMetadataStatus != resolvedDataStore.RelationalProviderMetadataStatus
                || ShouldRetryReusableGeneration(reason, previousState.StableObservation)
            )
            {
                DocumentCacheTargetContextBuildResult refreshedBuildResult = await targetContextBuilder
                    .BuildAsync(targetKey, resolvedDataStore, reusableGeneration, cancellationToken)
                    .ConfigureAwait(false);

                return new TargetState(
                    refreshedBuildResult.Observation,
                    refreshedBuildResult.Observation,
                    refreshedBuildResult.ExecutionContext,
                    signature,
                    resolvedDataStore.RelationalProviderMetadataStatus,
                    RetryAttemptCount: 0,
                    previousState.LastGenerationValue
                );
            }

            return previousState with
            {
                SnapshotObservation = previousState.StableObservation,
                RetryAttemptCount = 0,
            };
        }

        DocumentCacheTargetContextGeneration generation = new(previousState.LastGenerationValue + 1);
        DocumentCacheTargetContextBuildResult buildResult = await targetContextBuilder
            .BuildAsync(targetKey, resolvedDataStore, generation, cancellationToken)
            .ConfigureAwait(false);

        DocumentCacheTargetObservation stableObservation = buildResult.Observation;
        if (
            previousState.StableObservation.ResolutionState == DocumentCacheTargetResolutionState.Resolved
            && previousState.LastGenerationValue > 0
        )
        {
            stableObservation = stableObservation.WithAdditionalDiagnostic(
                CreateDiagnostic(
                    stableObservation,
                    DocumentCacheTargetDiagnosticCategory.TargetReplaced,
                    "Resolved target execution metadata changed; target context generation replaced.",
                    retryState: null
                )
            );
        }

        return new TargetState(
            stableObservation,
            stableObservation,
            buildResult.ExecutionContext,
            signature,
            resolvedDataStore.RelationalProviderMetadataStatus,
            RetryAttemptCount: 0,
            LastGenerationValue: generation.Value
        );
    }

    private TargetState ApplyTenantRefreshFailure(DocumentCacheTargetKey targetKey, TargetState previousState)
    {
        DocumentCacheResolutionRetryState retryState = CreateRetryState(
            previousState,
            DocumentCacheTargetDiagnosticCategory.TransientCmsRefreshFailure,
            "CMS refresh failed for configured target."
        );

        if (
            previousState.StableObservation.ResolutionState == DocumentCacheTargetResolutionState.Resolved
            && previousState.StableObservation.Generation is not null
        )
        {
            DocumentCacheTargetDiagnostic diagnostic = CreateDiagnostic(
                previousState.StableObservation,
                DocumentCacheTargetDiagnosticCategory.TransientCmsRefreshFailure,
                "CMS refresh failed for configured target; retaining current target context generation.",
                retryState
            );

            return previousState with
            {
                SnapshotObservation = previousState.StableObservation.WithRetryDiagnostic(
                    retryState,
                    diagnostic
                ),
                RetryAttemptCount = retryState.AttemptCount,
            };
        }

        DocumentCacheTargetDiagnostic unresolvedDiagnostic = CreateUnresolvedDiagnostic(
            targetKey,
            retryState,
            DocumentCacheTargetDiagnosticCategory.TransientCmsRefreshFailure,
            "CMS refresh failed for configured target."
        );

        DocumentCacheTargetObservation unresolvedObservation = DocumentCacheTargetObservation.Unresolved(
            targetKey,
            _effectiveSettings,
            retryState,
            [unresolvedDiagnostic]
        );

        return new TargetState(
            unresolvedObservation,
            unresolvedObservation,
            ExecutionContext: null,
            Signature: null,
            ProviderMetadataStatus: null,
            retryState.AttemptCount,
            previousState.LastGenerationValue
        );
    }

    private TargetState ApplyTargetUnresolved(DocumentCacheTargetKey targetKey, TargetState previousState)
    {
        DocumentCacheResolutionRetryState retryState = CreateRetryState(
            previousState,
            DocumentCacheTargetDiagnosticCategory.TargetUnresolved,
            "Configured target is not present in CMS refresh result."
        );
        DocumentCacheTargetDiagnostic diagnostic = CreateUnresolvedDiagnostic(
            targetKey,
            retryState,
            DocumentCacheTargetDiagnosticCategory.TargetUnresolved,
            "Configured target is not present in CMS refresh result."
        );

        DocumentCacheTargetObservation unresolvedObservation = DocumentCacheTargetObservation.Unresolved(
            targetKey,
            _effectiveSettings,
            retryState,
            [diagnostic]
        );

        return new TargetState(
            unresolvedObservation,
            unresolvedObservation,
            ExecutionContext: null,
            Signature: null,
            ProviderMetadataStatus: null,
            retryState.AttemptCount,
            previousState.LastGenerationValue
        );
    }

    private DocumentCacheResolutionRetryState CreateRetryState(
        TargetState previousState,
        DocumentCacheTargetDiagnosticCategory category,
        string message
    )
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        return new DocumentCacheResolutionRetryState(
            previousState.RetryAttemptCount + 1,
            now,
            now + _effectiveSettings.ProjectorFailureBackoff,
            category,
            message
        );
    }

    private static DocumentCacheTargetDiagnostic CreateUnresolvedDiagnostic(
        DocumentCacheTargetKey targetKey,
        DocumentCacheResolutionRetryState retryState,
        DocumentCacheTargetDiagnosticCategory category,
        string message
    ) =>
        new(
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
            category,
            message
        );

    private static DocumentCacheTargetDiagnostic CreateDiagnostic(
        DocumentCacheTargetObservation observation,
        DocumentCacheTargetDiagnosticCategory category,
        string message,
        DocumentCacheResolutionRetryState? retryState
    ) =>
        new(
            observation.TargetKey,
            observation.ResolutionState,
            observation.ProviderToken,
            observation.Generation,
            observation.PhysicalSourceFingerprint,
            observation.Lifecycle,
            observation.Inventory,
            observation.EnqueueTrigger,
            observation.SqlServerPrerequisites,
            retryState,
            category,
            message
        );

    private DocumentCacheTargetRegistrySnapshot CreateSnapshot(
        ImmutableDictionary<DocumentCacheTargetKey, TargetState> targetStates
    ) =>
        new(
            _configuredTargetKeys.Select(targetKey => targetStates[targetKey].SnapshotObservation),
            timeProvider.GetUtcNow()
        );

    private DocumentCacheTargetRuntimeSnapshot CreateRuntimeSnapshot(
        ImmutableDictionary<DocumentCacheTargetKey, TargetState> targetStates,
        DateTimeOffset observedAt
    ) =>
        new(
            _configuredTargetKeys
                .Select(targetKey => targetStates[targetKey].ExecutionContext)
                .OfType<DocumentCacheTargetExecutionContext>(),
            observedAt
        );

    private DocumentCacheTargetStatusSnapshot CreateStatusSnapshot(
        DocumentCacheTargetRegistrySnapshot registrySnapshot,
        ImmutableDictionary<DocumentCacheTargetKey, TargetState> targetStates
    ) => new(registrySnapshot, CreateRuntimeSnapshot(targetStates, timeProvider.GetUtcNow()));

    private static string GetProviderTenantKey(DocumentCacheTargetKey targetKey) => targetKey.TenantKey;

    private static bool ShouldRetryReusableGeneration(
        DocumentCacheTargetRefreshReason reason,
        DocumentCacheTargetObservation observation
    )
    {
        if (reason != DocumentCacheTargetRefreshReason.SupervisorTriggered)
        {
            return false;
        }

        return HasRecoverableReusableGenerationFailure(observation);
    }

    private static bool CanForceLoadRecoverableReusableGeneration(
        DocumentCacheTargetObservation observation
    ) => HasRecoverableReusableGenerationFailure(observation);

    private static bool HasRecoverableReusableGenerationFailure(DocumentCacheTargetObservation observation)
    {
        if (
            observation.ResolutionState != DocumentCacheTargetResolutionState.Resolved
            || observation.EligibilityState != DocumentCacheTargetEligibilityState.Ineligible
        )
        {
            return false;
        }

        ImmutableHashSet<DocumentCacheTargetDiagnosticCategory> categories = observation
            .Diagnostics.Select(diagnostic => diagnostic.Category)
            .ToImmutableHashSet();

        return categories.Contains(DocumentCacheTargetDiagnosticCategory.ProviderPrerequisiteFailed)
            && !categories.Contains(DocumentCacheTargetDiagnosticCategory.UnsupportedPrerequisiteIncident);
    }

    private sealed record TenantRefreshResult(bool Succeeded)
    {
        public static TenantRefreshResult Success() => new(Succeeded: true);

        public static TenantRefreshResult Failure() => new(Succeeded: false);
    }

    private sealed record TargetState(
        DocumentCacheTargetObservation StableObservation,
        DocumentCacheTargetObservation SnapshotObservation,
        DocumentCacheTargetExecutionContext? ExecutionContext,
        ResolvedTargetExecutionSignature? Signature,
        RelationalProviderMetadataStatus? ProviderMetadataStatus,
        int RetryAttemptCount,
        long LastGenerationValue
    );

    private sealed record ResolvedTargetExecutionSignature(
        RelationalProviderToken? ProviderToken,
        string? ConnectionFactoryInput
    )
    {
        public static ResolvedTargetExecutionSignature From(
            DocumentCacheResolvedTargetDataStore resolvedDataStore
        )
        {
            ArgumentNullException.ThrowIfNull(resolvedDataStore);

            return new(
                resolvedDataStore.RelationalProviderToken,
                string.IsNullOrWhiteSpace(resolvedDataStore.ConnectionFactoryInput)
                    ? null
                    : resolvedDataStore.ConnectionFactoryInput
            );
        }
    }
}
