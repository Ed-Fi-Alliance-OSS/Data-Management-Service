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
using Microsoft.Extensions.Logging;

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

        string commandName = parseResult.CommandResult.Command.Name;
        bool jsonOutput = parseResult.GetValue<bool>(DocumentCacheAdminCommandSurface.JsonOptionName);
        IDocumentCacheAdminCliTelemetry telemetry =
            serviceProvider.GetService<IDocumentCacheAdminCliTelemetry>()
            ?? new DocumentCacheAdminCliTelemetry(
                serviceProvider.GetService<ILogger<DocumentCacheAdminCliTelemetry>>()
            );
        long commandStartTimestamp = telemetry.RecordCommandAttempt(
            commandName,
            invocationTarget.TargetKey,
            jsonOutput
        );

        int CompleteCommand(int exitCode, string outcome, string category)
        {
            telemetry.RecordCommandCompletion(
                commandName,
                invocationTarget.TargetKey,
                jsonOutput,
                exitCode,
                outcome,
                category,
                commandStartTimestamp
            );
            return exitCode;
        }

        if (
            string.Equals(
                commandName,
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

                if (jsonOutput)
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

                return CompleteCommand(DocumentCacheAdminExitCodes.Success, "completed", "status");
            }
            catch (OperationCanceledException)
            {
                await WriteErrorAsync(
                        standardError,
                        "DocumentCache status was cancelled before a complete status document could be produced."
                    )
                    .ConfigureAwait(false);

                return CompleteCommand(
                    DocumentCacheAdminExitCodes.FailedNoMutation,
                    "failedNoMutation",
                    "cancelled"
                );
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await WriteErrorAsync(
                        standardError,
                        "DocumentCache status failed before a complete status document could be produced",
                        exception.Message
                    )
                    .ConfigureAwait(false);

                return CompleteCommand(
                    DocumentCacheAdminExitCodes.FailedNoMutation,
                    "failedNoMutation",
                    "statusPipelineFailure"
                );
            }
        }

        if (DocumentCacheAdminCommandSurface.IsMutatingCommand(commandName))
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
                await WriteErrorAsync(standardError, failure).ConfigureAwait(false);

                return CompleteCommand(
                    DocumentCacheAdminExitCodes.ArgumentError,
                    "argumentError",
                    "requestValidation"
                );
            }

            IDocumentCacheAdminMutatingCommandDispatcher? dispatcher =
                serviceProvider.GetService<IDocumentCacheAdminMutatingCommandDispatcher>();
            if (dispatcher is null)
            {
                await WriteErrorAsync(
                        standardError,
                        "DocumentCache administrative command runtime services are not configured for this invocation."
                    )
                    .ConfigureAwait(false);

                return CompleteCommand(
                    DocumentCacheAdminExitCodes.ConfigurationError,
                    "configurationError",
                    "runtimeServicesMissing"
                );
            }

            try
            {
                DocumentCacheAdministrativeCommandResult result = await dispatcher
                    .ExecuteAsync(commandRequest!, cancellationToken)
                    .ConfigureAwait(false);

                if (jsonOutput)
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

                return CompleteCommand(
                    DocumentCacheAdminExitCodeMapper.ForAdministrativeCommandResult(result),
                    result.Status.ToString(),
                    result.Classification.ToString()
                );
            }
            catch (OperationCanceledException)
            {
                await WriteErrorAsync(
                        standardError,
                        "DocumentCache administrative command was cancelled before a shared result could be produced."
                    )
                    .ConfigureAwait(false);

                return CompleteCommand(
                    DocumentCacheAdminExitCodes.FailedNoMutation,
                    "failedNoMutation",
                    "cancelled"
                );
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await WriteErrorAsync(
                        standardError,
                        "DocumentCache administrative command failed before a shared result could be produced",
                        exception.Message
                    )
                    .ConfigureAwait(false);

                return CompleteCommand(
                    DocumentCacheAdminExitCodes.UnexpectedFailure,
                    "unexpectedFailure",
                    "dispatcherFailure"
                );
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
                        $"target={DocumentCacheAdminOutput.TargetSurrogate(target.TargetKey)} dataStoreId={target.TargetKey.DataStoreId}"
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
                    $"target={DocumentCacheAdminOutput.TargetSurrogate(result.TargetKey)} dataStoreId={result.TargetKey.DataStoreId}"
                )
            )
            .ConfigureAwait(false);
        await standardOutput
            .WriteLineAsync(
                $"  generation={FormatNullableLong(result.TargetGeneration)} physicalSourceFingerprint={DocumentCacheAdminOutput.FingerprintPresence(result.PhysicalSourceFingerprint)}"
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
                    $"  diagnostic phase={diagnostic.CurrentPhase} category={diagnostic.DiagnosticCategory} retryable={FormatNullableBoolean(diagnostic.Retryable)} message=\"{DocumentCacheAdminOutput.SanitizeDiagnostic(diagnostic.Message)}\""
                )
                .ConfigureAwait(false);
        }
    }

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

    private static string FormatNullableEnum<TEnum>(TEnum? value)
        where TEnum : struct, Enum => value is null ? "null" : value.Value.ToString();

    private static string FormatNullableDurationSeconds(TimeSpan? duration) =>
        duration is null ? "null" : duration.Value.TotalSeconds.ToString("G17", CultureInfo.InvariantCulture);

    private static Task WriteErrorAsync(TextWriter? standardError, string? message)
    {
        if (standardError is null)
        {
            return Task.CompletedTask;
        }

        return standardError.WriteLineAsync(DocumentCacheAdminOutput.SanitizeDiagnostic(message));
    }

    private static Task WriteErrorAsync(TextWriter? standardError, string messagePrefix, string? diagnostic)
    {
        if (standardError is null)
        {
            return Task.CompletedTask;
        }

        string sanitizedDiagnostic = DocumentCacheAdminOutput.SanitizeDiagnostic(diagnostic);
        string message = string.IsNullOrWhiteSpace(sanitizedDiagnostic)
            ? messagePrefix
            : $"{messagePrefix}: {sanitizedDiagnostic}";
        return standardError.WriteLineAsync(message);
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
