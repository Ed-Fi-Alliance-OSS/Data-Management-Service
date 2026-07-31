// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.DocumentCache;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Caller-local purpose included in bounded cache-writer diagnostics and metrics.
/// </summary>
public enum DocumentCacheWriterPurpose
{
    /// <summary>
    /// Cache write requested by the asynchronous durable-work projector.
    /// </summary>
    DurableWorkProjection = 1,

    /// <summary>
    /// Cache write requested opportunistically after a relational read fallback.
    /// </summary>
    DirectFill = 2,
}

/// <summary>
/// Request to conditionally write one materialized candidate and acknowledge matching durable
/// projection work in one provider transaction.
/// </summary>
public sealed record DocumentCacheWriterRequest
{
    public DocumentCacheWriterRequest(
        DocumentCacheMaterializationTargetContext targetContext,
        long documentId,
        long? selectedRequiredContentVersion,
        DocumentCacheWriterPurpose purpose,
        DocumentCacheMaterializationCandidate? candidate,
        CancellationToken cancellationToken
    )
    {
        TargetContext = targetContext ?? throw new ArgumentNullException(nameof(targetContext));
        DocumentId = DocumentCacheMaterializerGuards.RequirePositive(documentId, nameof(documentId));
        if (selectedRequiredContentVersion is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(selectedRequiredContentVersion),
                selectedRequiredContentVersion,
                "Selected durable-work RequiredContentVersion must be positive when supplied."
            );
        }

        if (candidate is not null && candidate.DocumentId != DocumentId)
        {
            throw new ArgumentException(
                "Candidate DocumentId must match the requested DocumentId.",
                nameof(candidate)
            );
        }

        SelectedRequiredContentVersion = selectedRequiredContentVersion;
        Purpose = DocumentCacheMaterializerGuards.RequireDefined(
            purpose,
            nameof(purpose),
            "Unsupported cache-writer purpose."
        );
        Candidate = candidate;
        CancellationToken = cancellationToken;
    }

    /// <summary>
    /// The resolved projection target and mapping set. The writer does not perform target resolution.
    /// </summary>
    public DocumentCacheMaterializationTargetContext TargetContext { get; }

    /// <summary>
    /// Internal canonical document identity that anchors all current source/cache/work observations.
    /// </summary>
    public long DocumentId { get; }

    /// <summary>
    /// Optional caller-selected durable-work version. It is diagnostic context only and never
    /// authorizes cache DML or work acknowledgement.
    /// </summary>
    public long? SelectedRequiredContentVersion { get; }

    /// <summary>
    /// Caller-local diagnostic purpose for this writer attempt.
    /// </summary>
    public DocumentCacheWriterPurpose Purpose { get; }

    /// <summary>
    /// Optional materialized cache-row candidate. Null means classify current durable state only.
    /// </summary>
    public DocumentCacheMaterializationCandidate? Candidate { get; }

    /// <summary>
    /// Cancellation for the writer attempt. Provider retry handling maps bounded aborts into writer outcomes.
    /// </summary>
    public CancellationToken CancellationToken { get; }
}

/// <summary>
/// Bounded cache-writer outcome names used by results, metrics, and later provider adapters.
/// </summary>
public enum DocumentCacheWriterOutcome
{
    AlreadyCurrentAcknowledged = 1,
    CandidateWrittenAcknowledged = 2,
    NeedsMaterialization = 3,
    LifecycleOrLatchFenced = 4,
    SourceMissingOrDeleted = 5,
    StaleCandidateSuppressed = 6,
    WorkAnomaly = 7,
    CacheAheadLatchSet = 8,
    CacheAheadDisappeared = 9,
    RacingWriterLost = 10,
    RetryBudgetExhausted = 11,
    CallerAbortedRetry = 12,
    DeleteRaceRetryExhausted = 13,
    CacheAheadUnconfirmedCallerAbort = 14,
    DeterministicInvariantOrTargetFailure = 15,
}

public enum DocumentCacheWriterFenceReason
{
    LifecycleNotEligible = 1,
    CacheAheadRecoveryRequired = 2,
    StateMissing = 3,
    StateInvalid = 4,
    StateUnreadable = 5,
}

public enum DocumentCacheWriterWorkAnomalyKind
{
    MissingWork = 1,
    WorkVersionMismatch = 2,
}

public enum DocumentCacheWriterInvariantFailureReason
{
    MatchingVersionDocumentUuidMismatch = 1,
    MatchingVersionResourceMetadataMismatch = 2,
    TargetMappingMismatch = 3,
}

/// <summary>
/// Result of a per-document cache-write/conditional-acknowledgement attempt. Provider faults that are
/// not modeled as bounded writer outcomes use ordinary exception flow.
/// </summary>
public abstract record DocumentCacheWriterResult
{
    private DocumentCacheWriterResult() { }

    public abstract DocumentCacheWriterOutcome Outcome { get; }

    public sealed record AlreadyCurrentAcknowledged : DocumentCacheWriterResult
    {
        public AlreadyCurrentAcknowledged(long acknowledgedContentVersion)
        {
            AcknowledgedContentVersion = RequirePositiveContentVersion(
                acknowledgedContentVersion,
                nameof(acknowledgedContentVersion)
            );
        }

        public override DocumentCacheWriterOutcome Outcome =>
            DocumentCacheWriterOutcome.AlreadyCurrentAcknowledged;

        public long AcknowledgedContentVersion { get; }
    }

    public sealed record CandidateWrittenAcknowledged : DocumentCacheWriterResult
    {
        public CandidateWrittenAcknowledged(
            DocumentCacheMaterializationCandidate candidate,
            long acknowledgedContentVersion
        )
        {
            Candidate = candidate ?? throw new ArgumentNullException(nameof(candidate));
            AcknowledgedContentVersion = RequirePositiveContentVersion(
                acknowledgedContentVersion,
                nameof(acknowledgedContentVersion)
            );
            if (Candidate.ContentVersion != AcknowledgedContentVersion)
            {
                throw new ArgumentException(
                    "Candidate write acknowledgement must use the candidate ContentVersion.",
                    nameof(acknowledgedContentVersion)
                );
            }
        }

        public override DocumentCacheWriterOutcome Outcome =>
            DocumentCacheWriterOutcome.CandidateWrittenAcknowledged;

        public DocumentCacheMaterializationCandidate Candidate { get; }

        public long AcknowledgedContentVersion { get; }
    }

    public sealed record NeedsMaterialization : DocumentCacheWriterResult
    {
        public NeedsMaterialization(long currentContentVersion)
        {
            CurrentContentVersion = RequirePositiveContentVersion(
                currentContentVersion,
                nameof(currentContentVersion)
            );
        }

        public override DocumentCacheWriterOutcome Outcome => DocumentCacheWriterOutcome.NeedsMaterialization;

        public long CurrentContentVersion { get; }
    }

    public sealed record LifecycleOrLatchFenced : DocumentCacheWriterResult
    {
        public LifecycleOrLatchFenced(
            DocumentCacheWriterFenceReason reason,
            DocumentCacheLifecycleState? lifecycleState,
            bool? cacheAheadRecoveryRequired
        )
        {
            Reason = RequireDefined(reason, nameof(reason), "Unsupported cache-writer fence reason.");
            if (lifecycleState is not null)
            {
                RequireDefined(
                    lifecycleState.Value,
                    nameof(lifecycleState),
                    "Unsupported DocumentCache lifecycle state."
                );
            }

            LifecycleState = lifecycleState;
            CacheAheadRecoveryRequired = cacheAheadRecoveryRequired;
        }

        public override DocumentCacheWriterOutcome Outcome =>
            DocumentCacheWriterOutcome.LifecycleOrLatchFenced;

        public DocumentCacheWriterFenceReason Reason { get; }

        public DocumentCacheLifecycleState? LifecycleState { get; }

        public bool? CacheAheadRecoveryRequired { get; }
    }

    public sealed record SourceMissingOrDeleted : DocumentCacheWriterResult
    {
        private SourceMissingOrDeleted() { }

        public static SourceMissingOrDeleted Instance { get; } = new();

        public override DocumentCacheWriterOutcome Outcome =>
            DocumentCacheWriterOutcome.SourceMissingOrDeleted;
    }

    public sealed record StaleCandidateSuppressed : DocumentCacheWriterResult
    {
        public StaleCandidateSuppressed(long currentContentVersion, long candidateContentVersion)
        {
            CurrentContentVersion = RequirePositiveContentVersion(
                currentContentVersion,
                nameof(currentContentVersion)
            );
            CandidateContentVersion = RequirePositiveContentVersion(
                candidateContentVersion,
                nameof(candidateContentVersion)
            );
            if (CurrentContentVersion == CandidateContentVersion)
            {
                throw new ArgumentException(
                    "Matching candidate ContentVersion must not be represented as stale suppression.",
                    nameof(candidateContentVersion)
                );
            }
        }

        public override DocumentCacheWriterOutcome Outcome =>
            DocumentCacheWriterOutcome.StaleCandidateSuppressed;

        public long CurrentContentVersion { get; }

        public long CandidateContentVersion { get; }
    }

    public sealed record WorkAnomaly : DocumentCacheWriterResult
    {
        public WorkAnomaly(
            DocumentCacheWriterWorkAnomalyKind kind,
            DocumentCacheLifecycleState lifecycleState,
            long? currentSourceContentVersion,
            long? workRequiredContentVersion
        )
        {
            Kind = RequireDefined(kind, nameof(kind), "Unsupported cache-writer work anomaly kind.");
            LifecycleState = lifecycleState switch
            {
                DocumentCacheLifecycleState.Tracking or DocumentCacheLifecycleState.Rebuilding =>
                    lifecycleState,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(lifecycleState),
                    lifecycleState,
                    "Work anomalies must distinguish Tracking from Rebuilding."
                ),
            };
            CurrentSourceContentVersion = RequirePositiveWhenSupplied(
                currentSourceContentVersion,
                nameof(currentSourceContentVersion)
            );
            WorkRequiredContentVersion = RequirePositiveWhenSupplied(
                workRequiredContentVersion,
                nameof(workRequiredContentVersion)
            );
        }

        public override DocumentCacheWriterOutcome Outcome => DocumentCacheWriterOutcome.WorkAnomaly;

        public DocumentCacheWriterWorkAnomalyKind Kind { get; }

        public DocumentCacheLifecycleState LifecycleState { get; }

        public long? CurrentSourceContentVersion { get; }

        public long? WorkRequiredContentVersion { get; }

        private static long? RequirePositiveWhenSupplied(long? value, string parameterName)
        {
            if (value is <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    parameterName,
                    value,
                    $"{parameterName} must be positive."
                );
            }

            return value;
        }
    }

    public sealed record CacheAheadLatchSet : DocumentCacheWriterResult
    {
        public CacheAheadLatchSet(long sourceContentVersion, long cacheContentVersion)
        {
            SourceContentVersion = RequirePositiveContentVersion(
                sourceContentVersion,
                nameof(sourceContentVersion)
            );
            CacheContentVersion = RequirePositiveContentVersion(
                cacheContentVersion,
                nameof(cacheContentVersion)
            );
            if (CacheContentVersion <= SourceContentVersion)
            {
                throw new ArgumentException(
                    "Cache-ahead latch outcomes require cache ContentVersion to be greater than source ContentVersion.",
                    nameof(cacheContentVersion)
                );
            }
        }

        public override DocumentCacheWriterOutcome Outcome => DocumentCacheWriterOutcome.CacheAheadLatchSet;

        public long SourceContentVersion { get; }

        public long CacheContentVersion { get; }
    }

    public sealed record CacheAheadDisappeared : DocumentCacheWriterResult
    {
        private CacheAheadDisappeared() { }

        public static CacheAheadDisappeared Instance { get; } = new();

        public override DocumentCacheWriterOutcome Outcome =>
            DocumentCacheWriterOutcome.CacheAheadDisappeared;
    }

    public sealed record RacingWriterLost : DocumentCacheWriterResult
    {
        private RacingWriterLost() { }

        public static RacingWriterLost Instance { get; } = new();

        public override DocumentCacheWriterOutcome Outcome => DocumentCacheWriterOutcome.RacingWriterLost;
    }

    public sealed record RetryBudgetExhausted : DocumentCacheWriterResult
    {
        public RetryBudgetExhausted(int attemptCount)
        {
            AttemptCount = RequirePositiveAttemptCount(attemptCount, nameof(attemptCount));
        }

        public override DocumentCacheWriterOutcome Outcome => DocumentCacheWriterOutcome.RetryBudgetExhausted;

        public int AttemptCount { get; }
    }

    public sealed record CallerAbortedRetry : DocumentCacheWriterResult
    {
        public CallerAbortedRetry(int attemptCount)
        {
            AttemptCount = RequirePositiveAttemptCount(attemptCount, nameof(attemptCount));
        }

        public override DocumentCacheWriterOutcome Outcome => DocumentCacheWriterOutcome.CallerAbortedRetry;

        public int AttemptCount { get; }
    }

    public sealed record DeleteRaceRetryExhausted : DocumentCacheWriterResult
    {
        public DeleteRaceRetryExhausted(int attemptCount)
        {
            AttemptCount = RequirePositiveAttemptCount(attemptCount, nameof(attemptCount));
        }

        public override DocumentCacheWriterOutcome Outcome =>
            DocumentCacheWriterOutcome.DeleteRaceRetryExhausted;

        public int AttemptCount { get; }
    }

    public sealed record CacheAheadUnconfirmedCallerAbort : DocumentCacheWriterResult
    {
        private CacheAheadUnconfirmedCallerAbort() { }

        public static CacheAheadUnconfirmedCallerAbort Instance { get; } = new();

        public override DocumentCacheWriterOutcome Outcome =>
            DocumentCacheWriterOutcome.CacheAheadUnconfirmedCallerAbort;
    }

    public sealed record DeterministicInvariantOrTargetFailure : DocumentCacheWriterResult
    {
        public DeterministicInvariantOrTargetFailure(
            DocumentCacheWriterInvariantFailureReason reason,
            long currentContentVersion,
            long candidateContentVersion
        )
        {
            Reason = RequireDefined(
                reason,
                nameof(reason),
                "Unsupported cache-writer invariant failure reason."
            );
            CurrentContentVersion = RequirePositiveContentVersion(
                currentContentVersion,
                nameof(currentContentVersion)
            );
            CandidateContentVersion = RequirePositiveContentVersion(
                candidateContentVersion,
                nameof(candidateContentVersion)
            );
            if (CurrentContentVersion != CandidateContentVersion)
            {
                throw new ArgumentException(
                    "Candidate ContentVersion mismatch must be represented as stale suppression.",
                    nameof(candidateContentVersion)
                );
            }
        }

        public override DocumentCacheWriterOutcome Outcome =>
            DocumentCacheWriterOutcome.DeterministicInvariantOrTargetFailure;

        public DocumentCacheWriterInvariantFailureReason Reason { get; }

        public long CurrentContentVersion { get; }

        public long CandidateContentVersion { get; }
    }

    private static long RequirePositiveContentVersion(long value, string parameterName) =>
        DocumentCacheMaterializerGuards.RequirePositive(value, parameterName);

    private static int RequirePositiveAttemptCount(int value, string parameterName)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, $"{parameterName} must be positive.");
        }

        return value;
    }

    private static TEnum RequireDefined<TEnum>(TEnum value, string parameterName, string message)
        where TEnum : struct, Enum =>
        DocumentCacheMaterializerGuards.RequireDefined(value, parameterName, message);
}

/// <summary>
/// Shared cache writer for later durable-work projector and direct-fill integrations.
/// </summary>
public interface IDocumentCacheWriter
{
    Task<DocumentCacheWriterResult> WriteAsync(DocumentCacheWriterRequest request);
}
