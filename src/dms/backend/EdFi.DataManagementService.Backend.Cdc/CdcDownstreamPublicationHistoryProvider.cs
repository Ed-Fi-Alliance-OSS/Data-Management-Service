// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EdFi.DataManagementService.Backend.Cdc;

/// <summary>
/// Reads provenance only inside an administrative execution holding the controller lock. Observations
/// outside that scope are unknown: a successful standalone read must not become reusable permission.
/// </summary>
public sealed class CdcDownstreamPublicationHistoryProvider
    : IDocumentCacheDownstreamPublicationHistoryProvider
{
    private readonly LocalCdcWorkflowJournalStore _store;
    private readonly string _deploymentKey;
    private readonly CdcProvider _provider;
    private readonly TimeProvider _time;
    private readonly TimeSpan _lockTimeout;
    private readonly AsyncLocal<ExecutionScope> _execution = new();

    public CdcDownstreamPublicationHistoryProvider(
        LocalCdcWorkflowJournalStore store,
        string deploymentKey,
        CdcProvider provider,
        TimeProvider timeProvider,
        TimeSpan lockTimeout
    )
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (lockTimeout <= TimeSpan.Zero || lockTimeout > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentOutOfRangeException(nameof(lockTimeout));
        }
        _store = store;
        _deploymentKey = deploymentKey;
        _provider = provider;
        _time = timeProvider;
        _lockTimeout = lockTimeout;
        // Validate the deployment/provider without interpreting either as ownership evidence.
        _ = LookupTarget(DocumentCacheTargetKey.Create(string.Empty, 1));
    }

    public async Task<DocumentCacheDownstreamPublicationHistoryObservation> ObserveAsync(
        DocumentCacheTargetKey targetKey,
        DocumentCachePhysicalSourceFingerprint? currentPhysicalSourceFingerprint,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(targetKey);
        cancellationToken.ThrowIfCancellationRequested();
        if (currentPhysicalSourceFingerprint is not null && _execution.Value is { Active: true } scope)
        {
            try
            {
                CdcSourcePublicationHistory history = await scope
                    .Session.ReadSourcePublicationHistoryAsync(
                        LookupTarget(targetKey),
                        currentPhysicalSourceFingerprint.Value,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                return scope.Active
                    ? Observation(history.Transitions[^1].Status, history.WorkflowId.ToString("D"))
                    : Observation(DocumentCacheDownstreamPublicationStatus.Unknown, string.Empty);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // All missing, unreadable, invalid, contradictory or disposed evidence fails closed.
                // Never include filesystem paths, raw source identifiers or exception messages.
            }
        }
        return Observation(DocumentCacheDownstreamPublicationStatus.Unknown, string.Empty);

        DocumentCacheDownstreamPublicationHistoryObservation Observation(
            DocumentCacheDownstreamPublicationStatus status,
            string generation
        ) =>
            new(
                targetKey,
                currentPhysicalSourceFingerprint,
                status,
                "cdc-managed-source-history",
                generation,
                _time.GetUtcNow(),
                status == DocumentCacheDownstreamPublicationStatus.Unknown
                    ? "Trusted downstream publication history is unavailable for this execution."
                    : "Downstream publication history was read from trusted managed source provenance."
            );
    }

    internal async Task<DocumentCacheAdministrativeCommandResult> ExecuteAsync(
        DocumentCacheAdministrativeCommandRunnerRequest request,
        Func<Task<DocumentCacheAdministrativeCommandResult>> execute,
        CancellationToken cancellationToken
    )
    {
        LocalCdcWorkflowJournalStore.Session session;
        try
        {
            session = await _store
                .AcquireAsync(
                    _lockTimeout,
                    _lockTimeout < TimeSpan.FromMilliseconds(50)
                        ? _lockTimeout
                        : TimeSpan.FromMilliseconds(50),
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (CdcWorkflowStateException)
        {
            return new(
                request.Command,
                request.TargetKey,
                DocumentCacheAdministrativeCommandClassification.DownstreamHistoryPresentOrUnknown,
                downstreamPublicationStatus: DocumentCacheDownstreamPublicationStatus.Unknown,
                diagnostics:
                [
                    new(
                        DocumentCacheTargetDiagnosticCategory.DownstreamPublicationHistoryPresentOrUnknown,
                        "Trusted downstream publication history could not be locked; no command was executed."
                    ),
                ]
            );
        }
        await using (session.ConfigureAwait(false))
        {
            ExecutionScope scope = new(session);
            _execution.Value = scope;
            try
            {
                return await execute().ConfigureAwait(false);
            }
            finally
            {
                // Invalidate copies of the execution context inherited by child tasks as well.
                scope.Revoke();
                _execution.Value = null!;
            }
        }
    }

    private CdcTargetIdentity LookupTarget(DocumentCacheTargetKey targetKey)
    {
        // Source history is independent of binding instance/generation. These lookup-only fields
        // are not persisted; the store follows the history's original creation identity and receipt.
        // E18 compares tenant keys ordinal-ignore-case; CDC provenance uses lowercase safe tokens.
        var result = CdcTargetValidator.Validate(
            new(
                _deploymentKey,
                targetKey.TenantKey.ToLowerInvariant(),
                targetKey.DataStoreId.ToString(CultureInfo.InvariantCulture),
                "history",
                _provider,
                "history",
                1,
                1,
                CdcTargetValidator.KafkaMurmur2V1PartitionerAlgorithm
            )
        );
        return result.Target?.ToTargetIdentity()
            ?? throw new ArgumentException("Trusted publication history configuration is invalid.");
    }

    private sealed class ExecutionScope(LocalCdcWorkflowJournalStore.Session session)
    {
        public LocalCdcWorkflowJournalStore.Session Session { get; } = session;
        private bool _active = true;
        public bool Active => Volatile.Read(ref _active);

        public void Revoke() => Volatile.Write(ref _active, false);
    }
}

internal sealed class CdcHistoryGatedAdministrativeCommandRunner(
    IDocumentCacheAdministrativeCommandRunner inner,
    CdcDownstreamPublicationHistoryProvider history
) : IDocumentCacheAdministrativeCommandRunner
{
    public Task<DocumentCacheAdministrativeCommandResult> ExecuteAsync(
        DocumentCacheAdministrativeCommandRunnerRequest request,
        IDocumentCacheAdministrativeCommandWorkflow workflow,
        CancellationToken cancellationToken = default
    ) =>
        request.Command
            is DocumentCacheAdministrativeCommand.OfflineActivation
                or DocumentCacheAdministrativeCommand.OfflineDeactivation
                or DocumentCacheAdministrativeCommand.InternalOnlyCacheAheadRecovery
            ? history.ExecuteAsync(
                request,
                () => inner.ExecuteAsync(request, workflow, cancellationToken),
                cancellationToken
            )
            : inner.ExecuteAsync(request, workflow, cancellationToken);
}

public static class CdcDownstreamPublicationHistoryServiceCollectionExtensions
{
    /// <summary>
    /// Administrative hosts only. StatePath must identify the same exclusively managed root as
    /// provisioning and CDC reservation. DeploymentKey selects its namespace, never attests ownership.
    /// No configured backend leaves E18's default unknown provider and command runner unchanged.
    /// </summary>
    public static IServiceCollection AddCdcDownstreamPublicationHistory(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        IConfigurationSection section = configuration.GetSection("Cdc:PublicationHistory");
        if (!section.Exists())
        {
            return services;
        }
        string statePath = section["StatePath"] ?? string.Empty;
        string deploymentKey = section["DeploymentKey"] ?? string.Empty;
        if (string.IsNullOrWhiteSpace(statePath) || string.IsNullOrWhiteSpace(deploymentKey))
        {
            throw new ArgumentException("Trusted publication history requires StatePath and DeploymentKey.");
        }
        TimeSpan timeout = section.GetValue("LockTimeout", TimeSpan.FromSeconds(30));
        services.TryAddSingleton<CdcDownstreamPublicationHistoryProvider>(sp =>
            new(
                new LocalCdcWorkflowJournalStore(statePath),
                deploymentKey,
                Enum.GetValues<CdcProvider>()
                    .Single(provider =>
                        CdcProviderToken.TryToRelationalProviderToken(provider, out var token)
                        && token == sp.GetRequiredService<DocumentCacheProcessProviderToken>().ProviderToken
                    ),
                sp.GetRequiredService<TimeProvider>(),
                timeout
            )
        );
        services.Replace(
            ServiceDescriptor.Singleton<IDocumentCacheDownstreamPublicationHistoryProvider>(sp =>
                sp.GetRequiredService<CdcDownstreamPublicationHistoryProvider>()
            )
        );
        services.Replace(
            ServiceDescriptor.Singleton<IDocumentCacheAdministrativeCommandRunner>(
                sp => new CdcHistoryGatedAdministrativeCommandRunner(
                    ActivatorUtilities.CreateInstance<DocumentCacheAdministrativeCommandRunner>(sp),
                    sp.GetRequiredService<CdcDownstreamPublicationHistoryProvider>()
                )
            )
        );
        return services;
    }
}
