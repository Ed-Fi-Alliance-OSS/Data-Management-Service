// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;
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
        TextWriter? standardError = null,
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
            )
        )
        {
            try
            {
                DocumentCacheStatusResponse statusResponse = await GetStatusResponseAsync(
                        invocationTarget.TargetKey,
                        serviceProvider,
                        cancellationToken
                    )
                    .ConfigureAwait(false);

                if (parseResult.GetValue<bool>(DocumentCacheAdminCommandSurface.JsonOptionName))
                {
                    await standardOutput
                        .WriteLineAsync(
                            DocumentCacheAdminJsonSerializer.SerializeContract(
                                statusResponse,
                                typeof(DocumentCacheStatusResponse)
                            )
                        )
                        .ConfigureAwait(false);
                }
                else
                {
                    await WriteHumanStatusAsync(statusResponse, standardOutput).ConfigureAwait(false);
                }

                return DocumentCacheAdminExitCodes.Success;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                if (standardError is not null)
                {
                    await standardError
                        .WriteLineAsync(
                            $"DocumentCache status failed before a complete status document could be produced: {exception.Message}"
                        )
                        .ConfigureAwait(false);
                }

                return DocumentCacheAdminExitCodes.FailedNoMutation;
            }
        }

        if (DocumentCacheAdminCommandSurface.IsMutatingCommand(parseResult.CommandResult.Command.Name))
        {
            if (
                !DocumentCacheAdminMutatingCommandRequestBuilder.TryBuild(
                    parseResult,
                    invocationTarget,
                    out DocumentCacheAdminMutatingCommandRequest? commandRequest,
                    out string? failure
                )
            )
            {
                if (standardError is not null)
                {
                    await standardError.WriteLineAsync(failure).ConfigureAwait(false);
                }

                return DocumentCacheAdminExitCodes.ArgumentError;
            }

            IDocumentCacheAdminMutatingCommandDispatcher? dispatcher =
                serviceProvider.GetService<IDocumentCacheAdminMutatingCommandDispatcher>();
            if (dispatcher is null)
            {
                if (standardError is not null)
                {
                    await standardError
                        .WriteLineAsync(
                            "DocumentCache administrative command runtime services are not configured for this invocation."
                        )
                        .ConfigureAwait(false);
                }

                return DocumentCacheAdminExitCodes.ConfigurationError;
            }

            try
            {
                DocumentCacheAdministrativeCommandResult result = await dispatcher
                    .ExecuteAsync(commandRequest!, cancellationToken)
                    .ConfigureAwait(false);

                if (parseResult.GetValue<bool>(DocumentCacheAdminCommandSurface.JsonOptionName))
                {
                    await standardOutput
                        .WriteLineAsync(
                            DocumentCacheAdminJsonSerializer.SerializeContract(
                                result,
                                typeof(DocumentCacheAdministrativeCommandResult)
                            )
                        )
                        .ConfigureAwait(false);
                }
                else
                {
                    await WriteHumanAdministrativeCommandResultAsync(result, standardOutput)
                        .ConfigureAwait(false);
                }

                return ExitCodeFor(result.Status);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                if (standardError is not null)
                {
                    await standardError
                        .WriteLineAsync(
                            $"DocumentCache administrative command failed before a shared result could be produced: {exception.Message}"
                        )
                        .ConfigureAwait(false);
                }

                return DocumentCacheAdminExitCodes.UnexpectedFailure;
            }
        }

        return await parseResult.InvokeAsync().ConfigureAwait(false);
    }

    private static async Task WriteHumanStatusAsync(
        DocumentCacheStatusResponse statusResponse,
        TextWriter standardOutput
    )
    {
        await standardOutput
            .WriteLineAsync(
                $"DocumentCache status observedAt={FormatTimestamp(statusResponse.ObservedAt)} targets={statusResponse.Targets.Length.ToString(CultureInfo.InvariantCulture)}"
            )
            .ConfigureAwait(false);

        foreach (DocumentCacheStatusTarget target in statusResponse.Targets)
        {
            await standardOutput
                .WriteLineAsync(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"target tenantKey=\"{target.TargetKey.TenantKey}\" dataStoreId={target.TargetKey.DataStoreId}"
                    )
                )
                .ConfigureAwait(false);
            await standardOutput
                .WriteLineAsync(
                    $"  resolution={target.Resolution.Status} reason={target.Resolution.Reason} generation={FormatNullableLong(target.TargetGeneration)}"
                )
                .ConfigureAwait(false);
            await standardOutput
                .WriteLineAsync(
                    $"  eligibility={target.Eligibility.Status} reason={target.Eligibility.Reason}"
                )
                .ConfigureAwait(false);
            await standardOutput
                .WriteLineAsync(
                    $"  lifecycle={target.Lifecycle.State} availability={target.Lifecycle.Availability} durableObservedAt={FormatNullableTimestamp(target.DurableObservedAt)}"
                )
                .ConfigureAwait(false);
            await standardOutput
                .WriteLineAsync(
                    $"  cacheAhead={target.CacheAhead.State} recoveryRequired={FormatNullableBoolean(target.CacheAhead.RecoveryRequired)}"
                )
                .ConfigureAwait(false);
            await standardOutput
                .WriteLineAsync(
                    $"  queue={target.QueueSummary.Presence} oldestWorkFirstEnqueuedAt={FormatNullableTimestamp(target.QueueSummary.OldestWorkFirstEnqueuedAt)} oldestWorkAgeSeconds={FormatNullableDouble(target.QueueSummary.OldestWorkAgeSeconds)}"
                )
                .ConfigureAwait(false);
            await standardOutput
                .WriteLineAsync(
                    $"  operationalHealth={target.OperationalHealth.Status} reason={target.OperationalHealth.Reason}"
                )
                .ConfigureAwait(false);
            await standardOutput
                .WriteLineAsync($"  caughtUp={target.CaughtUp.Status} reason={target.CaughtUp.Reason}")
                .ConfigureAwait(false);
            await standardOutput
                .WriteLineAsync($"  executionState={target.ExecutionState.Status}")
                .ConfigureAwait(false);
        }
    }

    private static async Task WriteHumanAdministrativeCommandResultAsync(
        DocumentCacheAdministrativeCommandResult result,
        TextWriter standardOutput
    )
    {
        await standardOutput
            .WriteLineAsync(
                $"DocumentCache command={result.Command} status={result.Status} classification={result.Classification} mutated={FormatNullableBoolean(result.Mutated)}"
            )
            .ConfigureAwait(false);
        await standardOutput
            .WriteLineAsync(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"target tenantKey=\"{result.TargetKey.TenantKey}\" dataStoreId={result.TargetKey.DataStoreId}"
                )
            )
            .ConfigureAwait(false);
        await standardOutput
            .WriteLineAsync(
                $"  generation={FormatNullableLong(result.TargetGeneration)} fingerprint={FormatNullableFingerprint(result.PhysicalSourceFingerprint)}"
            )
            .ConfigureAwait(false);
        await standardOutput
            .WriteLineAsync(
                $"  lifecycle={FormatNullableEnum(result.Lifecycle)} cacheAheadRecoveryRequired={FormatNullableBoolean(result.CacheAheadRecoveryRequired)} elapsedCommandTimeSeconds={FormatNullableDurationSeconds(result.ElapsedCommandTime)}"
            )
            .ConfigureAwait(false);

        foreach (DocumentCacheAdministrativePhaseDiagnostic diagnostic in result.PhaseDiagnostics)
        {
            await standardOutput
                .WriteLineAsync(
                    $"  diagnostic phase={diagnostic.CurrentPhase} category={diagnostic.DiagnosticCategory} retryable={FormatNullableBoolean(diagnostic.Retryable)} message=\"{diagnostic.Message}\""
                )
                .ConfigureAwait(false);
        }
    }

    private static int ExitCodeFor(DocumentCacheAdministrativeCommandStatus status) =>
        status switch
        {
            DocumentCacheAdministrativeCommandStatus.Completed => DocumentCacheAdminExitCodes.Success,
            DocumentCacheAdministrativeCommandStatus.RejectedNoMutation =>
                DocumentCacheAdminExitCodes.RejectedNoMutation,
            DocumentCacheAdministrativeCommandStatus.FailedNoMutation =>
                DocumentCacheAdminExitCodes.FailedNoMutation,
            DocumentCacheAdministrativeCommandStatus.IncompleteRetryable =>
                DocumentCacheAdminExitCodes.IncompleteRetryable,
            _ => DocumentCacheAdminExitCodes.UnexpectedFailure,
        };

    private static string FormatTimestamp(DateTimeOffset timestamp) =>
        timestamp.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static string FormatNullableTimestamp(DateTimeOffset? timestamp) =>
        timestamp is null ? "null" : FormatTimestamp(timestamp.Value);

    private static string FormatNullableLong(long? value) =>
        value is null ? "null" : value.Value.ToString(CultureInfo.InvariantCulture);

    private static string FormatNullableBoolean(bool? value) =>
        value is null ? "null" : value.Value.ToString(CultureInfo.InvariantCulture).ToLowerInvariant();

    private static string FormatNullableDouble(double? value) =>
        value is null ? "null" : value.Value.ToString("G17", CultureInfo.InvariantCulture);

    private static string FormatNullableFingerprint(DocumentCachePhysicalSourceFingerprint? fingerprint) =>
        fingerprint is null ? "null" : fingerprint.Value;

    private static string FormatNullableEnum<TEnum>(TEnum? value)
        where TEnum : struct, Enum => value is null ? "null" : value.Value.ToString();

    private static string FormatNullableDurationSeconds(TimeSpan? duration) =>
        duration is null ? "null" : duration.Value.TotalSeconds.ToString("G17", CultureInfo.InvariantCulture);

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

        return await statusService
            .GetStatusAsync(cancellationToken, DocumentCacheStatusEvaluationMode.StandaloneDirectObservation)
            .ConfigureAwait(false);
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
