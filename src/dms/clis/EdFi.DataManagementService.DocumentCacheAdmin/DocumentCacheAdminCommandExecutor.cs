// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.CommandLine;
using System.CommandLine.Parsing;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using Microsoft.Extensions.DependencyInjection;

namespace EdFi.DataManagementService.DocumentCacheAdmin;

internal static class DocumentCacheAdminCommandExecutor
{
    public static async Task<int> ExecuteAsync(
        ParseResult parseResult,
        DocumentCacheAdminInvocationTarget invocationTarget,
        IServiceProvider serviceProvider,
        TextWriter standardOutput,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(parseResult);
        ArgumentNullException.ThrowIfNull(invocationTarget);
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(standardOutput);

        if (
            string.Equals(
                parseResult.CommandResult.Command.Name,
                DocumentCacheAdminCommandSurface.StatusCommandName,
                StringComparison.Ordinal
            ) && parseResult.GetValue<bool>(DocumentCacheAdminCommandSurface.JsonOptionName)
        )
        {
            DocumentCacheStatusResponse statusResponse = await GetStatusResponseAsync(
                    invocationTarget.TargetKey,
                    serviceProvider,
                    cancellationToken
                )
                .ConfigureAwait(false);
            await standardOutput
                .WriteLineAsync(
                    DocumentCacheAdminJsonSerializer.SerializeContract(
                        statusResponse,
                        typeof(DocumentCacheStatusResponse)
                    )
                )
                .ConfigureAwait(false);
            return DocumentCacheAdminExitCodes.Success;
        }

        return await parseResult.InvokeAsync().ConfigureAwait(false);
    }

    private static async Task<DocumentCacheStatusResponse> GetStatusResponseAsync(
        DocumentCacheTargetKey targetKey,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken
    )
    {
        IDocumentCacheStatusService? statusService =
            serviceProvider.GetService<IDocumentCacheStatusService>();
        if (statusService is null)
        {
            return CreateUnconfiguredStatusResponse(targetKey, DateTimeOffset.UtcNow);
        }

        IDocumentCacheAdminTargetResolver? targetResolver =
            serviceProvider.GetService<IDocumentCacheAdminTargetResolver>();
        if (targetResolver is not null)
        {
            await targetResolver.ResolveAsync(targetKey, cancellationToken).ConfigureAwait(false);
        }

        return await statusService.GetStatusAsync(cancellationToken).ConfigureAwait(false);
    }

    private static DocumentCacheStatusResponse CreateUnconfiguredStatusResponse(
        DocumentCacheTargetKey targetKey,
        DateTimeOffset observedAt
    )
    {
        const string message = "DocumentCache runtime services are not configured for this invocation.";
        DocumentCacheStatusInventoryComponent inventoryNotObserved = new(
            DocumentCacheStatusInventoryStatus.NotObserved,
            DocumentCacheStatusInventoryReason.None,
            message
        );
        DocumentCacheStatusEnqueueTriggerComponent enqueueTriggerNotObserved = new(
            DocumentCacheStatusEnqueueTriggerStatus.NotObserved,
            DocumentCacheStatusInventoryReason.None,
            message
        );
        DocumentCacheStatusProviderPrerequisiteComponent providerPrerequisiteUnknown = new(
            DocumentCacheStatusProviderPrerequisiteStatus.Unknown,
            DocumentCacheStatusProviderPrerequisiteReason.None,
            message
        );

        return new DocumentCacheStatusResponse(
            observedAt,
            [
                new DocumentCacheStatusTarget(
                    DocumentCacheStatusTargetKey.FromTargetKey(targetKey),
                    targetGeneration: null,
                    observedAt,
                    durableObservedAt: null,
                    provider: null,
                    physicalSourceFingerprint: null,
                    new DocumentCacheStatusResolutionComponent(
                        DocumentCacheStatusResolutionStatus.Unknown,
                        DocumentCacheStatusResolutionReason.TargetNotFound,
                        observedAt,
                        message
                    ),
                    new DocumentCacheStatusEligibilityComponent(
                        DocumentCacheStatusEligibilityStatus.Unknown,
                        DocumentCacheStatusReason.UnresolvedTarget,
                        message
                    ),
                    new DocumentCacheStatusInventoryComponentGroup(
                        observedAt: null,
                        inventoryNotObserved,
                        inventoryNotObserved,
                        inventoryNotObserved,
                        inventoryNotObserved,
                        enqueueTriggerNotObserved
                    ),
                    new DocumentCacheStatusProviderPrerequisitesComponent(
                        DocumentCacheStatusProviderPrerequisiteStatus.Unknown,
                        DocumentCacheStatusProviderPrerequisiteReason.None,
                        observedAt: null,
                        providerPrerequisiteUnknown,
                        providerPrerequisiteUnknown
                    ),
                    new DocumentCacheStatusLifecycleComponent(
                        DocumentCacheStatusLifecycleState.Unknown,
                        DocumentCacheStatusAvailability.Unknown,
                        message
                    ),
                    new DocumentCacheStatusCacheAheadComponent(
                        DocumentCacheStatusCacheAheadState.Unknown,
                        recoveryRequired: null,
                        message
                    ),
                    new DocumentCacheOperationalHealthComponent(
                        DocumentCacheOperationalHealthStatus.Unknown,
                        DocumentCacheStatusReason.RuntimeNotObserved,
                        message
                    ),
                    new DocumentCacheCaughtUpComponent(
                        DocumentCacheCaughtUpStatus.Unknown,
                        DocumentCacheStatusReason.RuntimeNotObserved,
                        message
                    ),
                    new DocumentCacheStatusQueueSummary(
                        DocumentCacheStatusQueuePresence.Unavailable,
                        oldestWorkFirstEnqueuedAt: null,
                        oldestWorkAgeSeconds: null,
                        DocumentCacheStatusBacklogEstimate.Unavailable
                    ),
                    new DocumentCacheStatusExecutionStateComponent(
                        DocumentCacheStatusExecutionState.NotObserved,
                        observedAt: null,
                        activeWorkers: null,
                        concurrencySlotsUsed: null,
                        targetBackoffUntil: null,
                        lastSuccessfulWorkAt: null,
                        lastFailureAt: null,
                        message
                    ),
                    activeCommand: null,
                    lastEndedDiagnostic: null,
                    new DocumentCacheStatusDiagnosticWindow<DocumentCacheStatusTargetDiagnosticEvent>(),
                    new DocumentCacheStatusDiagnosticWindow<DocumentCacheStatusDocumentDiagnosticEvent>(),
                    new DocumentCacheStatusDiagnosticWindow<DocumentCacheStatusPoisonTraversalDiagnosticEvent>(),
                    DocumentCacheStatusEffectiveSettings.FromEffectiveSettings(
                        DocumentCacheTargetEffectiveSettings.FromOptions(new DocumentCacheOptions())
                    ),
                    new DocumentCacheStatusEnqueueFailures()
                ),
            ]
        );
    }
}
