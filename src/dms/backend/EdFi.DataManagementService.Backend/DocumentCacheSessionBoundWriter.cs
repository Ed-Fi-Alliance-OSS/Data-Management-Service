// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.DocumentCache;

namespace EdFi.DataManagementService.Backend;

internal sealed record DocumentCacheSessionBoundWriterRequest
{
    public DocumentCacheSessionBoundWriterRequest(
        IDocumentCacheAdministrativeMutexLease mutexLease,
        DocumentCacheWriterRequest writerRequest,
        bool commandExecutionMutated,
        Action? markMutationBeforeCommit = null,
        Func<bool>? commandExecutionMutatedProvider = null
    )
    {
        MutexLease = mutexLease ?? throw new ArgumentNullException(nameof(mutexLease));
        WriterRequest = writerRequest ?? throw new ArgumentNullException(nameof(writerRequest));
        MarkMutationBeforeCommit = markMutationBeforeCommit;
        CommandExecutionMutatedProvider = commandExecutionMutatedProvider ?? (() => commandExecutionMutated);
    }

    public IDocumentCacheAdministrativeMutexLease MutexLease { get; }

    public DocumentCacheWriterRequest WriterRequest { get; }

    public bool CommandExecutionMutated => CommandExecutionMutatedProvider();

    public Action? MarkMutationBeforeCommit { get; }

    private Func<bool> CommandExecutionMutatedProvider { get; }
}

internal sealed record DocumentCacheSessionBoundWriterResult
{
    private DocumentCacheSessionBoundWriterResult(
        DocumentCacheWriterResult? writerResult,
        DocumentCacheAdministrativeCommandStatus status,
        DocumentCacheAdministrativeCommandClassification classification,
        DocumentCacheAdministrativeDiagnosticCategory? diagnosticCategory,
        bool mutated,
        string message
    )
    {
        if (
            writerResult is null
            && classification == DocumentCacheAdministrativeCommandClassification.Succeeded
        )
        {
            throw new ArgumentException("A succeeded session-bound writer result requires a writer result.");
        }

        WriterResult = writerResult;
        Status = status;
        Classification = classification;
        DiagnosticCategory = diagnosticCategory;
        Mutated = mutated;
        Message = Sanitize(message);
    }

    public DocumentCacheWriterResult? WriterResult { get; }

    public DocumentCacheAdministrativeCommandStatus Status { get; }

    public DocumentCacheAdministrativeCommandClassification Classification { get; }

    public DocumentCacheAdministrativeDiagnosticCategory? DiagnosticCategory { get; }

    public bool Mutated { get; }

    public string Message { get; }

    public bool HasWriterResult => WriterResult is not null;

    public static DocumentCacheSessionBoundWriterResult FromWriterResult(
        DocumentCacheWriterResult writerResult,
        bool commandExecutionMutated
    )
    {
        ArgumentNullException.ThrowIfNull(writerResult);

        return writerResult switch
        {
            DocumentCacheWriterResult.RetryBudgetExhausted result => WriterRetryBudgetExhausted(
                result,
                commandExecutionMutated
            ),
            DocumentCacheWriterResult.DeleteRaceRetryExhausted result => WriterRetryBudgetExhausted(
                result,
                commandExecutionMutated
            ),
            DocumentCacheWriterResult.CallerAbortedRetry result => Failed(
                result,
                commandExecutionMutated
                    ? DocumentCacheAdministrativeCommandClassification.CancellationAfterMutation
                    : DocumentCacheAdministrativeCommandClassification.CancellationBeforeMutation,
                DocumentCacheAdministrativeDiagnosticCategory.Cancellation,
                commandExecutionMutated,
                "Session-bound DocumentCache writer retry was cancelled by the caller."
            ),
            _ => new(
                writerResult,
                DocumentCacheAdministrativeCommandStatus.Completed,
                DocumentCacheAdministrativeCommandClassification.Succeeded,
                diagnosticCategory: null,
                commandExecutionMutated || WriterResultMutated(writerResult),
                "Session-bound DocumentCache writer returned a writer outcome."
            ),
        };
    }

    private static DocumentCacheSessionBoundWriterResult WriterRetryBudgetExhausted(
        DocumentCacheWriterResult writerResult,
        bool commandExecutionMutated
    ) =>
        Failed(
            writerResult,
            DocumentCacheAdministrativeCommandClassification.WriterRetryBudgetExhausted,
            DocumentCacheAdministrativeDiagnosticCategory.WriterRetryBudgetExhausted,
            commandExecutionMutated,
            "Session-bound DocumentCache writer retry budget was exhausted."
        );

    public static DocumentCacheSessionBoundWriterResult SessionLoss(
        bool commandExecutionMutated,
        string message
    ) =>
        Failed(
            writerResult: null,
            commandExecutionMutated
                ? DocumentCacheAdministrativeCommandClassification.SessionLossAfterMutation
                : DocumentCacheAdministrativeCommandClassification.SessionLossNoMutation,
            DocumentCacheAdministrativeDiagnosticCategory.SessionLoss,
            commandExecutionMutated,
            message
        );

    public static DocumentCacheSessionBoundWriterResult ProviderCommandTimeout(
        bool commandExecutionMutated,
        string message
    ) =>
        Failed(
            writerResult: null,
            DocumentCacheAdministrativeCommandClassification.ProviderCommandTimeout,
            DocumentCacheAdministrativeDiagnosticCategory.ProviderCommandTimeout,
            commandExecutionMutated,
            message
        );

    private static DocumentCacheSessionBoundWriterResult Failed(
        DocumentCacheWriterResult? writerResult,
        DocumentCacheAdministrativeCommandClassification classification,
        DocumentCacheAdministrativeDiagnosticCategory diagnosticCategory,
        bool commandExecutionMutated,
        string message
    ) =>
        new(
            writerResult,
            commandExecutionMutated
                ? DocumentCacheAdministrativeCommandStatus.IncompleteRetryable
                : DocumentCacheAdministrativeCommandStatus.FailedNoMutation,
            classification,
            diagnosticCategory,
            commandExecutionMutated,
            message
        );

    private static string Sanitize(string? message) =>
        string.IsNullOrWhiteSpace(message) ? "DocumentCache session-bound writer diagnostic." : message;

    public static bool WriterResultMutated(DocumentCacheWriterResult writerResult) =>
        writerResult
            is DocumentCacheWriterResult.AlreadyCurrentAcknowledged
                or DocumentCacheWriterResult.CandidateWrittenAcknowledged
                or DocumentCacheWriterResult.CacheAheadLatchSet;
}

internal interface IDocumentCacheSessionBoundWriter
{
    Task<DocumentCacheSessionBoundWriterResult> WriteAsync(DocumentCacheSessionBoundWriterRequest request);
}
