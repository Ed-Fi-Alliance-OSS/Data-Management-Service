// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Diagnostics;
using System.Globalization;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
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
        CancellationToken cancellationToken = default,
        TextReader? standardInput = null
    )
    {
        ArgumentNullException.ThrowIfNull(parseResult);
        ArgumentNullException.ThrowIfNull(invocationTarget);
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(standardOutput);

        // The cdc verb group has its own dispatch and shares the verb name `status` with the
        // DocumentCache status command, so every name comparison below is scoped away from it, and its
        // verbs report under their scoped label. A leaf name alone cannot identify a command once a verb
        // group exists.
        bool isCdcCommand = DocumentCacheAdminCommandSurface.IsCdcCommand(parseResult);
        string? cdcVerbName = DocumentCacheAdminCommandSurface.CdcVerbName(parseResult);
        string commandName = cdcVerbName is null
            ? parseResult.CommandResult.Command.Name
            : DocumentCacheAdminCommandSurface.CdcCommandLabel(cdcVerbName);

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

        if (isCdcCommand && cdcVerbName is not null)
        {
            if (
                !DocumentCacheAdminCdcCommandRequestBuilder.TryBuild(
                    parseResult,
                    cdcVerbName,
                    invocationTarget,
                    BindingJsonLoader(standardInput),
                    out DocumentCacheAdminCdcCommandRequest? cdcRequest,
                    out string? cdcFailure
                )
            )
            {
                await WriteErrorAsync(standardError, cdcFailure).ConfigureAwait(false);

                return CompleteCommand(
                    DocumentCacheAdminExitCodes.ArgumentError,
                    "argumentError",
                    "cdcRequestValidation"
                );
            }

            IDocumentCacheAdminCdcCommandDispatcher? cdcDispatcher =
                serviceProvider.GetService<IDocumentCacheAdminCdcCommandDispatcher>();
            if (cdcDispatcher is null)
            {
                await WriteErrorAsync(
                        standardError,
                        "CDC control plane services are not configured for this invocation."
                    )
                    .ConfigureAwait(false);

                return CompleteCommand(
                    DocumentCacheAdminExitCodes.ConfigurationError,
                    "configurationError",
                    "cdcServicesMissing"
                );
            }

            try
            {
                // No CLI-level timeout scope: the control plane owns per-step timeouts, and a step that
                // times out returns a fail-closed contract rather than an abandoned operation. A
                // wall-clock budget imposed here could only discard evidence the operation already has.
                DocumentCacheAdminCdcCommandResult cdcResult = await cdcDispatcher
                    .ExecuteAsync(cdcRequest!, cancellationToken)
                    .ConfigureAwait(false);

                await WriteCdcResultAsync(cdcResult, jsonOutput, standardOutput, standardError)
                    .ConfigureAwait(false);

                return CompleteCommand(cdcResult.ExitCode, cdcResult.Outcome, cdcResult.Category);
            }
            catch (OperationCanceledException)
            {
                await WriteErrorAsync(
                        standardError,
                        "CDC operation was cancelled before a shared contract could be produced."
                    )
                    .ConfigureAwait(false);

                return CompleteCommand(
                    DocumentCacheAdminExitCodes.IncompleteRetryable,
                    "incompleteRetryable",
                    "cdcCancelled"
                );
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await WriteErrorAsync(
                        standardError,
                        "CDC operation failed before a shared contract could be produced",
                        exception.Message
                    )
                    .ConfigureAwait(false);

                return CompleteCommand(
                    DocumentCacheAdminExitCodes.UnexpectedFailure,
                    "unexpectedFailure",
                    "cdcDispatcherFailure"
                );
            }
        }

        if (
            !isCdcCommand
            && string.Equals(
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
                        cancellationToken,
                        statusTimeout.RemainingTimeout
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
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
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

        if (!isCdcCommand && DocumentCacheAdminCommandSurface.IsMutatingCommand(commandName))
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

    /// <summary>
    /// Reads a <c>--binding-json</c> value from a file or from standard input. When no reader was
    /// supplied, stdin reads as empty rather than reaching the process console, so a caller that did not
    /// offer stdin cannot have a binding read out from under it.
    /// </summary>
    private static Func<string, string> BindingJsonLoader(TextReader? standardInput) =>
        path =>
            string.Equals(path, "-", StringComparison.Ordinal)
                ? (standardInput ?? TextReader.Null).ReadToEnd()
                : File.ReadAllText(path);

    private static async Task WriteCdcResultAsync(
        DocumentCacheAdminCdcCommandResult result,
        bool jsonOutput,
        TextWriter standardOutput,
        TextWriter? standardError
    )
    {
        if (result.Contract is { } contract && result.ContractType is { } contractType)
        {
            if (jsonOutput)
            {
                await standardOutput
                    .WriteLineAsync(
                        DocumentCacheAdminJsonSerializer.SerializeCdcContract(contract, contractType)
                    )
                    .ConfigureAwait(false);
                return;
            }

            await WriteHumanCdcResultAsync(result, standardOutput).ConfigureAwait(false);
            return;
        }

        // No contract was produced, so the diagnostics are the whole answer. They go to stderr in JSON
        // mode so stdout still contains exactly one shared contract document or nothing at all.
        foreach (CdcDiagnostic diagnostic in result.Diagnostics)
        {
            await WriteErrorAsync(
                    jsonOutput ? standardError : standardOutput,
                    $"{result.VerbName}: {diagnostic.Code}",
                    diagnostic.Message
                )
                .ConfigureAwait(false);
        }
    }

    private static async Task WriteHumanCdcResultAsync(
        DocumentCacheAdminCdcCommandResult result,
        TextWriter standardOutput
    )
    {
        await standardOutput
            .WriteLineAsync($"CDC {result.VerbName} outcome={result.Outcome}")
            .ConfigureAwait(false);

        if (result.GovernedNames is { } names)
        {
            await standardOutput
                .WriteLineAsync(
                    $"  connector={DocumentCacheAdminOutput.BoundedLabel(names.ConnectorName)} provider={names.Provider} dataStoreId={names.DataStoreId} instanceKey={DocumentCacheAdminOutput.BoundedLabel(names.InstanceKey)}"
                )
                .ConfigureAwait(false);
            await standardOutput
                .WriteLineAsync(
                    $"  topic={DocumentCacheAdminOutput.BoundedLabel(names.TopicName)} progressTopic={DocumentCacheAdminOutput.BoundedLabel(names.ProgressTopicName)} schemaHistoryTopic={FormatOptionalLabel(names.SchemaHistoryTopicName)}"
                )
                .ConfigureAwait(false);
        }

        switch (result.Contract)
        {
            case CdcAdmission admission:
                await standardOutput
                    .WriteLineAsync(
                        $"  admission={admission.AdmissionState} blocking={admission.PrimaryBlockingCategory} diagnostics={admission.Diagnostics.Count.ToString(CultureInfo.InvariantCulture)}"
                    )
                    .ConfigureAwait(false);
                break;
            case CdcStatus status:
                await standardOutput
                    .WriteLineAsync(
                        $"  readiness={status.Readiness} blocking={status.PrimaryBlockingCategory} targets={status.Targets.Count.ToString(CultureInfo.InvariantCulture)}"
                    )
                    .ConfigureAwait(false);
                break;
            case CdcAdoptionProof adoptionProof:
                await standardOutput
                    .WriteLineAsync(
                        $"  verifications={adoptionProof.VerificationResults.Count.ToString(CultureInfo.InvariantCulture)}"
                    )
                    .ConfigureAwait(false);
                break;
            case CdcCleanupProof cleanupProof:
                await standardOutput
                    .WriteLineAsync(
                        $"  cleanupMode={cleanupProof.CleanupMode} governedArtifacts={cleanupProof.GovernedArtifacts.Count.ToString(CultureInfo.InvariantCulture)}"
                    )
                    .ConfigureAwait(false);
                break;
            default:
                break;
        }
    }

    private static string FormatOptionalLabel(string? value) =>
        value is null ? "null" : DocumentCacheAdminOutput.BoundedLabel(value);

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
        CancellationToken cancellationToken,
        TimeSpan endpointTimeoutOverride
    )
    {
        IDocumentCacheStatusService statusService =
            serviceProvider.GetRequiredService<IDocumentCacheStatusService>();

        return await statusService
            .GetStatusAsync(
                cancellationToken,
                DocumentCacheStatusEvaluationMode.StandaloneDirectObservation,
                endpointTimeoutOverride
            )
            .ConfigureAwait(false);
    }

    private sealed class DocumentCacheAdminTimeoutScope : IDisposable
    {
        private readonly TimeSpan _timeout;
        private readonly long _startedAt;
        private readonly CancellationTokenSource _timeoutSource;
        private readonly CancellationTokenSource _linkedSource;

        private DocumentCacheAdminTimeoutScope(TimeSpan timeout, CancellationToken callerCancellationToken)
        {
            _timeout = timeout;
            _startedAt = Stopwatch.GetTimestamp();
            _timeoutSource = new CancellationTokenSource();
            _timeoutSource.CancelAfter(timeout);
            _linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
                callerCancellationToken,
                _timeoutSource.Token
            );
        }

        public CancellationToken Token => _linkedSource.Token;

        public bool IsTimeoutExpired => _timeoutSource.IsCancellationRequested;

        public TimeSpan RemainingTimeout
        {
            get
            {
                TimeSpan remaining = _timeout - Stopwatch.GetElapsedTime(_startedAt);
                return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            }
        }

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
