// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;
using EdFi.DataManagementService.Backend;
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
            using DocumentCacheAdminTimeoutScope statusTimeout = DocumentCacheAdminTimeoutScope.Start(
                parseResult,
                DocumentCacheAdminCommandSurface.StatusTimeoutSecondsOptionName,
                cancellationToken
            );

            try
            {
                DocumentCacheStatusResponse statusResponse = await GetStatusResponseAsync(
                        invocationTarget.TargetKey,
                        serviceProvider,
                        statusTimeout.Token
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
            catch (OperationCanceledException) when (statusTimeout.IsTimeoutExpired)
            {
                await WriteErrorAsync(
                        standardError,
                        "DocumentCache status timed out before a complete status document could be produced."
                    )
                    .ConfigureAwait(false);

                return CompleteCommand(
                    DocumentCacheAdminExitCodes.FailedNoMutation,
                    "failedNoMutation",
                    "statusTimeout"
                );
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

            DocumentCacheAdminMutatingCommandRequest mutatingCommandRequest =
                commandRequest
                ?? throw new InvalidOperationException(
                    "DocumentCache mutating command request builder succeeded without a request."
                );

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

            using DocumentCacheAdminTimeoutScope commandTimeout = DocumentCacheAdminTimeoutScope.Start(
                parseResult,
                DocumentCacheAdminCommandSurface.CommandTimeoutSecondsOptionName,
                cancellationToken
            );

            DocumentCacheAdministrativeCommandResult? targetResolutionResult;
            try
            {
                targetResolutionResult = await ResolveMutatingTargetAsync(
                        mutatingCommandRequest,
                        serviceProvider,
                        commandTimeout.Token
                    )
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (commandTimeout.IsTimeoutExpired)
            {
                DocumentCacheAdministrativeCommandResult timeoutResult =
                    CreateWorkflowTimeoutBeforeSharedResult(mutatingCommandRequest);
                await WriteAdministrativeCommandResultAsync(timeoutResult, jsonOutput, standardOutput)
                    .ConfigureAwait(false);

                return CompleteCommand(
                    DocumentCacheAdminExitCodeMapper.ForAdministrativeCommandResult(timeoutResult),
                    timeoutResult.Status.ToString(),
                    timeoutResult.Classification.ToString()
                );
            }
            catch (OperationCanceledException)
            {
                DocumentCacheAdministrativeCommandResult cancellationResult = CreatePreDispatchFailureResult(
                    mutatingCommandRequest,
                    DocumentCacheAdministrativeCommandClassification.CancellationBeforeMutation,
                    DocumentCacheAdministrativeDiagnosticCategory.Cancellation,
                    "Administrative command was cancelled during target preparation before dispatch."
                );
                await WriteAdministrativeCommandResultAsync(cancellationResult, jsonOutput, standardOutput)
                    .ConfigureAwait(false);

                return CompleteCommand(
                    DocumentCacheAdminExitCodeMapper.ForAdministrativeCommandResult(cancellationResult),
                    cancellationResult.Status.ToString(),
                    cancellationResult.Classification.ToString()
                );
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                DocumentCacheAdministrativeCommandResult failureResult = CreatePreDispatchFailureResult(
                    mutatingCommandRequest,
                    DocumentCacheAdministrativeCommandClassification.UnexpectedProviderFailure,
                    DocumentCacheAdministrativeDiagnosticCategory.UnexpectedProviderFailure,
                    CreatePreDispatchFailureMessage(
                        "Administrative command failed during target preparation before dispatch.",
                        exception.Message
                    )
                );
                await WriteAdministrativeCommandResultAsync(failureResult, jsonOutput, standardOutput)
                    .ConfigureAwait(false);

                return CompleteCommand(
                    DocumentCacheAdminExitCodeMapper.ForAdministrativeCommandResult(failureResult),
                    failureResult.Status.ToString(),
                    failureResult.Classification.ToString()
                );
            }

            if (targetResolutionResult is not null)
            {
                await WriteAdministrativeCommandResultAsync(
                        targetResolutionResult,
                        jsonOutput,
                        standardOutput
                    )
                    .ConfigureAwait(false);

                return CompleteCommand(
                    DocumentCacheAdminExitCodeMapper.ForAdministrativeCommandResult(targetResolutionResult),
                    targetResolutionResult.Status.ToString(),
                    targetResolutionResult.Classification.ToString()
                );
            }

            try
            {
                DocumentCacheAdministrativeCommandResult result = await dispatcher
                    .ExecuteAsync(mutatingCommandRequest, commandTimeout.Token)
                    .ConfigureAwait(false);
                if (commandTimeout.IsTimeoutExpired)
                {
                    result = ConvertCancellationResultToWorkflowTimeout(result);
                }

                await WriteAdministrativeCommandResultAsync(result, jsonOutput, standardOutput)
                    .ConfigureAwait(false);

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
                    DocumentCacheAdminExitCodes.UnexpectedFailure,
                    "unexpectedFailure",
                    commandTimeout.IsTimeoutExpired
                        ? "dispatcherTimeoutWithoutResult"
                        : "dispatcherCancellationWithoutResult"
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

    private static async Task<DocumentCacheAdministrativeCommandResult?> ResolveMutatingTargetAsync(
        DocumentCacheAdminMutatingCommandRequest commandRequest,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken
    )
    {
        IDocumentCacheAdminTargetResolver? targetResolver =
            serviceProvider.GetService<IDocumentCacheAdminTargetResolver>();
        if (targetResolver is not null)
        {
            DocumentCacheAdminTargetResolutionResult resolution = await targetResolver
                .ResolveAsync(commandRequest.TargetKey, cancellationToken)
                .ConfigureAwait(false);
            if (resolution.Outcome != DocumentCacheAdminTargetResolutionOutcome.Completed)
            {
                return CreateTargetResolutionRejectedResult(commandRequest, resolution);
            }
        }

        IDocumentCacheProjectionSupervisor? projectionSupervisor =
            serviceProvider.GetService<IDocumentCacheProjectionSupervisor>();
        if (projectionSupervisor is not null)
        {
            await projectionSupervisor
                .RefreshAsync(DocumentCacheTargetRefreshReason.Startup, cancellationToken)
                .ConfigureAwait(false);
        }

        return null;
    }

    private static async Task WriteAdministrativeCommandResultAsync(
        DocumentCacheAdministrativeCommandResult result,
        bool jsonOutput,
        TextWriter standardOutput
    )
    {
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
            return;
        }

        await WriteHumanAdministrativeCommandResultAsync(result, standardOutput).ConfigureAwait(false);
    }

    private static DocumentCacheAdministrativeCommandResult CreateWorkflowTimeoutBeforeSharedResult(
        DocumentCacheAdminMutatingCommandRequest commandRequest
    ) =>
        new(
            ToAdministrativeCommand(commandRequest.CommandName),
            DocumentCacheAdministrativeTargetKey.FromTargetKey(commandRequest.TargetKey),
            DocumentCacheAdministrativeCommandStatus.FailedNoMutation,
            DocumentCacheAdministrativeCommandClassification.WorkflowTimeout,
            mutated: false,
            phaseDiagnostics:
            [
                new DocumentCacheAdministrativePhaseDiagnostic(
                    DocumentCacheAdministrativeCommandPhase.ResolveTarget,
                    lastCompletedPhase: null,
                    retryable: false,
                    DocumentCacheAdministrativeDiagnosticCategory.WorkflowTimeout,
                    affectedDocumentIds: [],
                    "Administrative workflow timeout expired before a shared command result could be produced."
                ),
            ]
        );

    private static DocumentCacheAdministrativeCommandResult CreateTargetResolutionRejectedResult(
        DocumentCacheAdminMutatingCommandRequest commandRequest,
        DocumentCacheAdminTargetResolutionResult resolution
    ) =>
        new(
            ToAdministrativeCommand(commandRequest.CommandName),
            DocumentCacheAdministrativeTargetKey.FromTargetKey(commandRequest.TargetKey),
            DocumentCacheAdministrativeCommandStatus.RejectedNoMutation,
            DocumentCacheAdministrativeCommandClassification.TargetNotConfigured,
            mutated: false,
            phaseDiagnostics:
            [
                new DocumentCacheAdministrativePhaseDiagnostic(
                    DocumentCacheAdministrativeCommandPhase.ResolveTarget,
                    lastCompletedPhase: null,
                    retryable: false,
                    DocumentCacheAdministrativeDiagnosticCategory.TargetNotConfigured,
                    affectedDocumentIds: [],
                    resolution.FailureMessage
                        ?? "DocumentCache target registry did not contain exactly the invocation target."
                ),
            ]
        );

    private static DocumentCacheAdministrativeCommandResult CreatePreDispatchFailureResult(
        DocumentCacheAdminMutatingCommandRequest commandRequest,
        DocumentCacheAdministrativeCommandClassification classification,
        DocumentCacheAdministrativeDiagnosticCategory diagnosticCategory,
        string message
    ) =>
        new(
            ToAdministrativeCommand(commandRequest.CommandName),
            DocumentCacheAdministrativeTargetKey.FromTargetKey(commandRequest.TargetKey),
            DocumentCacheAdministrativeCommandStatus.FailedNoMutation,
            classification,
            mutated: false,
            phaseDiagnostics:
            [
                new DocumentCacheAdministrativePhaseDiagnostic(
                    DocumentCacheAdministrativeCommandPhase.ResolveTarget,
                    lastCompletedPhase: null,
                    retryable: false,
                    diagnosticCategory,
                    affectedDocumentIds: [],
                    message
                ),
            ]
        );

    private static string CreatePreDispatchFailureMessage(string messagePrefix, string? diagnostic)
    {
        string sanitizedDiagnostic = DocumentCacheAdminOutput.SanitizeDiagnostic(diagnostic);
        return string.IsNullOrWhiteSpace(sanitizedDiagnostic)
            ? messagePrefix
            : $"{messagePrefix}: {sanitizedDiagnostic}";
    }

    private static DocumentCacheAdministrativeCommandResult ConvertCancellationResultToWorkflowTimeout(
        DocumentCacheAdministrativeCommandResult result
    )
    {
        if (
            result.Classification
            is not DocumentCacheAdministrativeCommandClassification.CancellationBeforeMutation
                and not DocumentCacheAdministrativeCommandClassification.CancellationAfterMutation
        )
        {
            return result;
        }

        bool mutated = result.Mutated;
        ImmutableArray<DocumentCacheAdministrativePhaseDiagnostic> existingDiagnostics = result
            .PhaseDiagnostics
            .IsDefault
            ? []
            : result.PhaseDiagnostics;
        DocumentCacheAdministrativePhaseDiagnostic? lastDiagnostic = existingDiagnostics.LastOrDefault();

        ImmutableArray<DocumentCacheAdministrativePhaseDiagnostic> timeoutDiagnostics =
            existingDiagnostics.Add(
                new DocumentCacheAdministrativePhaseDiagnostic(
                    lastDiagnostic?.CurrentPhase ?? DocumentCacheAdministrativeCommandPhase.ResolveTarget,
                    lastDiagnostic?.LastCompletedPhase,
                    retryable: mutated,
                    DocumentCacheAdministrativeDiagnosticCategory.WorkflowTimeout,
                    affectedDocumentIds: [],
                    mutated
                        ? "Administrative workflow timeout expired after durable mutation; reissue the same explicit command."
                        : "Administrative workflow timeout expired before durable mutation."
                )
            );

        return new(
            result.Command,
            result.TargetKey,
            mutated
                ? DocumentCacheAdministrativeCommandStatus.IncompleteRetryable
                : DocumentCacheAdministrativeCommandStatus.FailedNoMutation,
            DocumentCacheAdministrativeCommandClassification.WorkflowTimeout,
            mutated,
            result.TargetGeneration,
            result.PhysicalSourceFingerprint,
            result.Lifecycle,
            result.CacheAheadRecoveryRequired,
            timeoutDiagnostics,
            result.OfflineWriterAdmission,
            result.ElapsedCommandTime
        );
    }

    private static DocumentCacheAdministrativeCommand ToAdministrativeCommand(string commandName)
    {
        if (
            DocumentCacheAdminMutatingCommandContracts.TryGet(
                commandName,
                out DocumentCacheAdminMutatingCommandContract? contract
            )
        )
        {
            return contract.AdministrativeCommand;
        }

        throw new ArgumentException(
            $"Unsupported DocumentCache mutating command '{commandName}'.",
            nameof(commandName)
        );
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
        IDocumentCacheAdminTargetResolver? targetResolver =
            serviceProvider.GetService<IDocumentCacheAdminTargetResolver>();
        if (targetResolver is not null)
        {
            DocumentCacheAdminTargetResolutionResult resolution = await targetResolver
                .ResolveAsync(targetKey, cancellationToken)
                .ConfigureAwait(false);
            if (resolution.Outcome != DocumentCacheAdminTargetResolutionOutcome.Completed)
            {
                return CreateTargetResolutionFailureStatusResponse(resolution);
            }
        }

        IDocumentCacheStatusService statusService =
            serviceProvider.GetRequiredService<IDocumentCacheStatusService>();

        return await statusService
            .GetStatusAsync(cancellationToken, DocumentCacheStatusEvaluationMode.StandaloneDirectObservation)
            .ConfigureAwait(false);
    }

    private static DocumentCacheStatusResponse CreateTargetResolutionFailureStatusResponse(
        DocumentCacheAdminTargetResolutionResult resolution
    )
    {
        string message =
            resolution.FailureMessage
            ?? "DocumentCache target registry did not contain exactly the invocation target.";
        DateTimeOffset observedAt = resolution.RegistrySnapshot.ObservedAt;
        var notObservedInventory = new DocumentCacheStatusInventoryComponent(
            DocumentCacheStatusInventoryStatus.NotObserved,
            DocumentCacheStatusInventoryReason.None,
            message: null
        );
        var unknownProviderPrerequisite = new DocumentCacheStatusProviderPrerequisiteComponent(
            DocumentCacheStatusProviderPrerequisiteStatus.Unknown,
            DocumentCacheStatusProviderPrerequisiteReason.None,
            message: null
        );

        return new(
            observedAt,
            [
                new DocumentCacheStatusTarget(
                    DocumentCacheStatusTargetKey.FromTargetKey(resolution.TargetKey),
                    targetGeneration: null,
                    observedAt,
                    durableObservedAt: null,
                    provider: null,
                    physicalSourceFingerprint: null,
                    new DocumentCacheStatusResolutionComponent(
                        DocumentCacheStatusResolutionStatus.Unresolved,
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
                        notObservedInventory,
                        notObservedInventory,
                        notObservedInventory,
                        notObservedInventory,
                        new DocumentCacheStatusEnqueueTriggerComponent(
                            DocumentCacheStatusEnqueueTriggerStatus.NotObserved,
                            DocumentCacheStatusInventoryReason.None,
                            message: null
                        )
                    ),
                    new DocumentCacheStatusProviderPrerequisitesComponent(
                        DocumentCacheStatusProviderPrerequisiteStatus.Unknown,
                        DocumentCacheStatusProviderPrerequisiteReason.None,
                        observedAt: null,
                        unknownProviderPrerequisite,
                        unknownProviderPrerequisite
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
                        DocumentCacheStatusReason.UnresolvedTarget,
                        message
                    ),
                    new DocumentCacheCaughtUpComponent(
                        DocumentCacheCaughtUpStatus.Unknown,
                        DocumentCacheStatusReason.UnresolvedTarget,
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
                    new DocumentCacheStatusDiagnosticWindow<DocumentCacheStatusTargetDiagnosticEvent>([
                        new DocumentCacheStatusTargetDiagnosticEvent(
                            observedAt,
                            DocumentCacheStatusTargetDiagnosticCategory.TargetResolution,
                            message
                        ),
                    ]),
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

    private sealed class DocumentCacheAdminTimeoutScope : IDisposable
    {
        private readonly CancellationTokenSource _timeoutSource;
        private readonly CancellationTokenSource _linkedSource;

        private DocumentCacheAdminTimeoutScope(TimeSpan timeout, CancellationToken callerCancellationToken)
        {
            _timeoutSource = new CancellationTokenSource();
            _timeoutSource.CancelAfter(timeout);
            _linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
                callerCancellationToken,
                _timeoutSource.Token
            );
        }

        public CancellationToken Token => _linkedSource.Token;

        public bool IsTimeoutExpired => _timeoutSource.IsCancellationRequested;

        public static DocumentCacheAdminTimeoutScope Start(
            ParseResult parseResult,
            string timeoutOptionName,
            CancellationToken callerCancellationToken
        )
        {
            string timeoutSeconds = parseResult.GetRequiredValue<string>(timeoutOptionName);
            if (
                !DocumentCacheAdminCommandSurface.TryParsePositiveSeconds(
                    timeoutSeconds,
                    out TimeSpan timeout
                )
            )
            {
                throw new InvalidOperationException(
                    $"Validated timeout option '{timeoutOptionName}' could not be converted."
                );
            }

            return new(timeout, callerCancellationToken);
        }

        public void Dispose()
        {
            _linkedSource.Dispose();
            _timeoutSource.Dispose();
        }
    }
}
