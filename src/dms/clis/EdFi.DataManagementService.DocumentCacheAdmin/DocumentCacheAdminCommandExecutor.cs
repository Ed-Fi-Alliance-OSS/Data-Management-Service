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
                await ResolveStatusTargetAsync(
                        invocationTarget.TargetKey,
                        serviceProvider,
                        statusTimeout.Token
                    )
                    .ConfigureAwait(false);

                DocumentCacheStatusResponse statusResponse = await GetStatusResponseAsync(
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

            bool dispatchStarted = false;
            try
            {
                DocumentCacheAdministrativeCommandResult? preDispatchResolutionResult =
                    await TryRefreshMutatingTargetAsync(
                            commandName,
                            mutatingCommandRequest,
                            serviceProvider,
                            commandTimeout.Token
                        )
                        .ConfigureAwait(false);
                if (preDispatchResolutionResult is not null)
                {
                    if (commandTimeout.IsTimeoutExpired)
                    {
                        preDispatchResolutionResult = ConvertCancellationResultToWorkflowTimeout(
                            preDispatchResolutionResult
                        );
                    }

                    await WriteAdministrativeCommandResultAsync(
                            preDispatchResolutionResult,
                            jsonOutput,
                            standardOutput
                        )
                        .ConfigureAwait(false);

                    return CompleteCommand(
                        DocumentCacheAdminExitCodeMapper.ForAdministrativeCommandResult(
                            preDispatchResolutionResult
                        ),
                        preDispatchResolutionResult.Status.ToString(),
                        preDispatchResolutionResult.Classification.ToString()
                    );
                }

                dispatchStarted = true;
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
            catch (OperationCanceledException) when (!dispatchStarted && commandTimeout.IsTimeoutExpired)
            {
                DocumentCacheAdministrativeCommandResult result = CreatePreDispatchWorkflowTimeoutResult(
                    commandName,
                    mutatingCommandRequest
                );

                await WriteAdministrativeCommandResultAsync(result, jsonOutput, standardOutput)
                    .ConfigureAwait(false);

                return CompleteCommand(
                    DocumentCacheAdminExitCodeMapper.ForAdministrativeCommandResult(result),
                    result.Status.ToString(),
                    result.Classification.ToString()
                );
            }
            catch (OperationCanceledException)
                when (!dispatchStarted && cancellationToken.IsCancellationRequested)
            {
                DocumentCacheAdministrativeCommandResult result = CreatePreDispatchCancellationResult(
                    commandName,
                    mutatingCommandRequest
                );

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
                DocumentCacheAdministrativeCommandResult result = CreateDispatchCancellationResult(
                    commandName,
                    mutatingCommandRequest
                );

                await WriteAdministrativeCommandResultAsync(result, jsonOutput, standardOutput)
                    .ConfigureAwait(false);

                return CompleteCommand(
                    DocumentCacheAdminExitCodeMapper.ForAdministrativeCommandResult(result),
                    result.Status.ToString(),
                    result.Classification.ToString()
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

    private static async Task<DocumentCacheAdministrativeCommandResult?> TryRefreshMutatingTargetAsync(
        string commandName,
        DocumentCacheAdminMutatingCommandRequest commandRequest,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken
    )
    {
        IDocumentCacheProjectionSupervisor? projectionSupervisor =
            serviceProvider.GetService<IDocumentCacheProjectionSupervisor>();
        if (projectionSupervisor is null)
        {
            return null;
        }

        DocumentCacheTargetRegistrySnapshot registrySnapshot = await projectionSupervisor
            .RefreshAsync(DocumentCacheTargetRefreshReason.Startup, cancellationToken)
            .ConfigureAwait(false);

        DocumentCacheAdminTargetResolutionResult resolution =
            DocumentCacheAdminTargetResolutionResult.FromSnapshot(commandRequest.TargetKey, registrySnapshot);
        return resolution.Outcome == DocumentCacheAdminTargetResolutionOutcome.Completed
            ? null
            : CreatePreDispatchTargetResolutionFailureResult(
                commandName,
                commandRequest,
                resolution.FailureMessage
                    ?? "DocumentCache target registry did not contain exactly the invocation target."
            );
    }

    private static DocumentCacheAdministrativeCommandResult CreateDispatchCancellationResult(
        string commandName,
        DocumentCacheAdminMutatingCommandRequest commandRequest
    )
    {
        if (
            !DocumentCacheAdminMutatingCommandContracts.TryGet(
                commandName,
                out DocumentCacheAdminMutatingCommandContract? contract
            )
        )
        {
            throw new InvalidOperationException($"Command '{commandName}' is not a mutating command.");
        }

        DocumentCacheOfflineWriterAdmissionConfirmation? offlineWriterAdmission = commandRequest.Request
            is IDocumentCacheOfflineWriterAdmissionRequest request
            ? request.OfflineWriterAdmission?.Confirmation
            : null;

        return new(
            contract.AdministrativeCommand,
            DocumentCacheAdministrativeTargetKey.FromTargetKey(commandRequest.TargetKey),
            DocumentCacheAdministrativeCommandStatus.IncompleteRetryable,
            DocumentCacheAdministrativeCommandClassification.CancellationAfterMutation,
            mutated: true,
            phaseDiagnostics:
            [
                new DocumentCacheAdministrativePhaseDiagnostic(
                    DocumentCacheAdministrativeCommandPhase.Complete,
                    lastCompletedPhase: null,
                    retryable: true,
                    DocumentCacheAdministrativeDiagnosticCategory.Cancellation,
                    affectedDocumentIds: [],
                    "Administrative command cancellation escaped after mutating dispatch began; reissue the same explicit command."
                ),
            ],
            offlineWriterAdmission: offlineWriterAdmission
        );
    }

    private static DocumentCacheAdministrativeCommandResult CreatePreDispatchTargetResolutionFailureResult(
        string commandName,
        DocumentCacheAdminMutatingCommandRequest commandRequest,
        string message
    ) =>
        CreatePreDispatchResult(
            commandName,
            commandRequest,
            DocumentCacheAdministrativeCommandStatus.FailedNoMutation,
            DocumentCacheAdministrativeCommandClassification.UnexpectedProviderFailure,
            DocumentCacheAdministrativeDiagnosticCategory.DeterministicInvariantFailure,
            message,
            retryable: false
        );

    private static DocumentCacheAdministrativeCommandResult CreatePreDispatchWorkflowTimeoutResult(
        string commandName,
        DocumentCacheAdminMutatingCommandRequest commandRequest
    ) =>
        CreatePreDispatchResult(
            commandName,
            commandRequest,
            DocumentCacheAdministrativeCommandStatus.FailedNoMutation,
            DocumentCacheAdministrativeCommandClassification.WorkflowTimeout,
            DocumentCacheAdministrativeDiagnosticCategory.WorkflowTimeout,
            "Administrative workflow timeout expired before the target could be refreshed.",
            retryable: false
        );

    private static DocumentCacheAdministrativeCommandResult CreatePreDispatchCancellationResult(
        string commandName,
        DocumentCacheAdminMutatingCommandRequest commandRequest
    ) =>
        CreatePreDispatchResult(
            commandName,
            commandRequest,
            DocumentCacheAdministrativeCommandStatus.FailedNoMutation,
            DocumentCacheAdministrativeCommandClassification.CancellationBeforeMutation,
            DocumentCacheAdministrativeDiagnosticCategory.Cancellation,
            "Administrative command was cancelled before the target could be refreshed.",
            retryable: false
        );

    private static DocumentCacheAdministrativeCommandResult CreatePreDispatchResult(
        string commandName,
        DocumentCacheAdminMutatingCommandRequest commandRequest,
        DocumentCacheAdministrativeCommandStatus status,
        DocumentCacheAdministrativeCommandClassification classification,
        DocumentCacheAdministrativeDiagnosticCategory diagnosticCategory,
        string message,
        bool retryable
    )
    {
        if (
            !DocumentCacheAdminMutatingCommandContracts.TryGet(
                commandName,
                out DocumentCacheAdminMutatingCommandContract? contract
            )
        )
        {
            throw new InvalidOperationException($"Command '{commandName}' is not a mutating command.");
        }

        DocumentCacheOfflineWriterAdmissionConfirmation? offlineWriterAdmission = commandRequest.Request
            is IDocumentCacheOfflineWriterAdmissionRequest request
            ? request.OfflineWriterAdmission?.Confirmation
            : null;

        return new(
            contract.AdministrativeCommand,
            DocumentCacheAdministrativeTargetKey.FromTargetKey(commandRequest.TargetKey),
            status,
            classification,
            mutated: false,
            phaseDiagnostics:
            [
                new DocumentCacheAdministrativePhaseDiagnostic(
                    DocumentCacheAdministrativeCommandPhase.ResolveTarget,
                    lastCompletedPhase: null,
                    retryable,
                    diagnosticCategory,
                    affectedDocumentIds: [],
                    message
                ),
            ],
            offlineWriterAdmission: offlineWriterAdmission
        );
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

    private static async Task ResolveStatusTargetAsync(
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
                throw new InvalidOperationException(
                    resolution.FailureMessage
                        ?? "DocumentCache target registry did not contain exactly the invocation target."
                );
            }
        }
    }

    private static async Task<DocumentCacheStatusResponse> GetStatusResponseAsync(
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken
    )
    {
        IDocumentCacheStatusService statusService =
            serviceProvider.GetRequiredService<IDocumentCacheStatusService>();

        return await statusService
            .GetStatusAsync(cancellationToken, DocumentCacheStatusEvaluationMode.StandaloneDirectObservation)
            .ConfigureAwait(false);
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
