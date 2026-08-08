// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.External.Backend;

namespace EdFi.DataManagementService.Backend;

internal enum DocumentCacheReadLookupOutcome
{
    FreshHit = 1,
    LifecycleDisabled = 2,
    LifecycleResetting = 3,
    LifecycleRebuilding = 4,
    CacheAheadRecoveryRequired = 5,
    MissingCacheRow = 6,
    MissingSourceRow = 7,
    SourceDrift = 8,
    StaleCacheRow = 9,
    MissingLifecycleState = 10,
    InvalidLifecycleState = 11,
    ProjectionTargetIneligible = 12,
    ProviderPrerequisiteIneligible = 13,
    CacheUnavailable = 14,
    DeterministicInvariantFailure = 15,
}

internal sealed record DocumentCacheReadDocumentLookupRequest(
    MappingSet MappingSet,
    DocumentCacheReadAccelerationCandidate Candidate
);

internal sealed record DocumentCacheReadBatchLookupRequest(
    MappingSet MappingSet,
    IReadOnlyList<DocumentCacheReadAccelerationCandidate> Candidates
)
{
    public bool IsEmpty => Candidates.Count == 0;
}

internal abstract record DocumentCacheReadDocumentLookupResult
{
    private DocumentCacheReadDocumentLookupResult(
        DocumentCacheReadLookupOutcome outcome,
        DocumentCacheReadAccelerationCandidate candidate
    )
    {
        Outcome = DocumentCacheMaterializerGuards.RequireDefined(
            outcome,
            nameof(outcome),
            "Unsupported DocumentCache read lookup outcome."
        );
        Candidate = candidate ?? throw new ArgumentNullException(nameof(candidate));
    }

    public DocumentCacheReadLookupOutcome Outcome { get; }

    public DocumentCacheReadAccelerationCandidate Candidate { get; }

    public bool IsFreshHit => Outcome == DocumentCacheReadLookupOutcome.FreshHit;

    public sealed record FreshHit : DocumentCacheReadDocumentLookupResult
    {
        public FreshHit(
            DocumentCacheReadAccelerationCandidate candidate,
            string documentJson,
            string streamEtag,
            DateTimeOffset cacheLastModifiedAt
        )
            : base(DocumentCacheReadLookupOutcome.FreshHit, candidate)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(documentJson);
            ArgumentException.ThrowIfNullOrWhiteSpace(streamEtag);

            DocumentJson = documentJson;
            StreamEtag = streamEtag;
            CacheLastModifiedAt = cacheLastModifiedAt;
        }

        public string DocumentJson { get; }

        public string StreamEtag { get; }

        public DateTimeOffset CacheLastModifiedAt { get; }
    }

    public sealed record Fallback : DocumentCacheReadDocumentLookupResult
    {
        public Fallback(
            DocumentCacheReadLookupOutcome outcome,
            DocumentCacheReadAccelerationCandidate candidate,
            string message
        )
            : base(outcome, candidate)
        {
            if (outcome == DocumentCacheReadLookupOutcome.FreshHit)
            {
                throw new ArgumentException(
                    "Fresh cache hits must use the FreshHit result.",
                    nameof(outcome)
                );
            }

            Message = DocumentCacheReadLookupDiagnosticText.Sanitize(message);
        }

        public string Message { get; }
    }
}

internal sealed record DocumentCacheReadBatchLookupResult
{
    public DocumentCacheReadBatchLookupResult(
        DocumentCacheReadLookupOutcome outcome,
        IReadOnlyList<DocumentCacheReadDocumentLookupResult> Documents,
        string message
    )
    {
        Outcome = DocumentCacheMaterializerGuards.RequireDefined(
            outcome,
            nameof(outcome),
            "Unsupported DocumentCache read lookup outcome."
        );
        this.Documents = Documents ?? throw new ArgumentNullException(nameof(Documents));
        Message = DocumentCacheReadLookupDiagnosticText.Sanitize(message);

        if (outcome == DocumentCacheReadLookupOutcome.FreshHit && !AllDocumentsFresh(this.Documents))
        {
            throw new ArgumentException("Fresh batch lookup results require all documents to be fresh.");
        }
    }

    public DocumentCacheReadLookupOutcome Outcome { get; }

    public IReadOnlyList<DocumentCacheReadDocumentLookupResult> Documents { get; }

    public string Message { get; }

    public bool IsFreshHit => Outcome == DocumentCacheReadLookupOutcome.FreshHit;

    public static DocumentCacheReadBatchLookupResult EmptyFresh() =>
        new(DocumentCacheReadLookupOutcome.FreshHit, [], "Empty cache lookup page.");

    public static DocumentCacheReadBatchLookupResult FromDocuments(
        IReadOnlyList<DocumentCacheReadDocumentLookupResult> documents
    )
    {
        ArgumentNullException.ThrowIfNull(documents);

        if (documents.Count == 0)
        {
            return EmptyFresh();
        }

        DocumentCacheReadDocumentLookupResult? firstFallback = documents.FirstOrDefault(document =>
            !document.IsFreshHit
        );

        return firstFallback is null
            ? new(DocumentCacheReadLookupOutcome.FreshHit, documents, "All cache rows are fresh.")
            : new(firstFallback.Outcome, documents, "One or more cache rows were not fresh.");
    }

    public static DocumentCacheReadBatchLookupResult PageFallback(
        DocumentCacheReadLookupOutcome outcome,
        IReadOnlyList<DocumentCacheReadAccelerationCandidate> candidates,
        string message
    )
    {
        ArgumentNullException.ThrowIfNull(candidates);

        return new(
            outcome,
            candidates
                .Select(candidate => new DocumentCacheReadDocumentLookupResult.Fallback(
                    outcome,
                    candidate,
                    message
                ))
                .ToArray(),
            message
        );
    }

    private static bool AllDocumentsFresh(IReadOnlyList<DocumentCacheReadDocumentLookupResult> documents) =>
        documents.All(static document => document.IsFreshHit);
}

internal interface IDocumentCacheReadFreshnessLookupAdapter
{
    Task<DocumentCacheReadDocumentLookupResult> LookupDocumentAsync(
        DocumentCacheReadDocumentLookupRequest request,
        DocumentCacheTargetExecutionContext targetContext,
        CancellationToken cancellationToken = default
    );

    Task<DocumentCacheReadBatchLookupResult> LookupBatchAsync(
        DocumentCacheReadBatchLookupRequest request,
        DocumentCacheTargetExecutionContext targetContext,
        CancellationToken cancellationToken = default
    );
}

internal abstract class DocumentCacheReadLookupAdapterBase
    : IDocumentCacheReadLookupAdapter,
        IDocumentCacheReadFreshnessLookupAdapter
{
    protected abstract SqlDialect Dialect { get; }

    protected abstract RelationalProviderToken ProviderToken { get; }

    public async Task<DocumentCacheReadLookupResult<GetResult>> TryGetByIdAsync(
        DocumentCacheReadAccelerationGetByIdRequest request,
        DocumentCacheTargetExecutionContext targetContext,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.AuthorizedCandidate is null)
        {
            return DocumentCacheReadLookupResult<GetResult>.Fallback(
                DocumentCacheReadAccelerationFallbackReason.CandidateSelectionUnavailable
            );
        }

        DocumentCacheReadDocumentLookupResult lookupResult = await LookupDocumentAsync(
                new DocumentCacheReadDocumentLookupRequest(request.MappingSet, request.AuthorizedCandidate),
                targetContext,
                cancellationToken
            )
            .ConfigureAwait(false);

        return DocumentCacheReadLookupResult<GetResult>.Fallback(MapFallbackReason(lookupResult.Outcome));
    }

    public async Task<DocumentCacheReadLookupResult<QueryResult>> TryQueryAsync(
        DocumentCacheReadAccelerationQueryRequest request,
        DocumentCacheTargetExecutionContext targetContext,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.AuthorizedCandidatePage is null)
        {
            return DocumentCacheReadLookupResult<QueryResult>.Fallback(
                DocumentCacheReadAccelerationFallbackReason.CandidateSelectionUnavailable
            );
        }

        DocumentCacheReadBatchLookupResult lookupResult = await LookupBatchAsync(
                new DocumentCacheReadBatchLookupRequest(
                    request.MappingSet,
                    request.AuthorizedCandidatePage.Candidates
                ),
                targetContext,
                cancellationToken
            )
            .ConfigureAwait(false);

        return DocumentCacheReadLookupResult<QueryResult>.Fallback(MapFallbackReason(lookupResult.Outcome));
    }

    public async Task<DocumentCacheReadDocumentLookupResult> LookupDocumentAsync(
        DocumentCacheReadDocumentLookupRequest request,
        DocumentCacheTargetExecutionContext targetContext,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        DocumentCacheReadBatchLookupResult batchResult = await LookupBatchAsync(
                new DocumentCacheReadBatchLookupRequest(request.MappingSet, [request.Candidate]),
                targetContext,
                cancellationToken
            )
            .ConfigureAwait(false);

        return batchResult.Documents.Count == 1
            ? batchResult.Documents[0]
            : new DocumentCacheReadDocumentLookupResult.Fallback(
                DocumentCacheReadLookupOutcome.DeterministicInvariantFailure,
                request.Candidate,
                "DocumentCache read lookup returned an invalid single-document result shape."
            );
    }

    public async Task<DocumentCacheReadBatchLookupResult> LookupBatchAsync(
        DocumentCacheReadBatchLookupRequest request,
        DocumentCacheTargetExecutionContext targetContext,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(targetContext);

        RequireTargetBinding(targetContext);

        if (request.IsEmpty)
        {
            return DocumentCacheReadBatchLookupResult.EmptyFresh();
        }

        DocumentCacheReadBatchLookupResult? targetEligibilityResult = TryClassifyTargetEligibility(
            targetContext,
            request.Candidates
        );

        if (targetEligibilityResult is not null)
        {
            return targetEligibilityResult;
        }

        try
        {
            RelationalCommand command = DocumentCacheReadLookupSql.BuildCommand(Dialect, request.Candidates);
            IReadOnlyList<DocumentCacheReadLookupObservation> observations = await ExecuteReaderAsync(
                    targetContext,
                    command,
                    DocumentCacheReadLookupSql.ReadObservationsAsync,
                    cancellationToken
                )
                .ConfigureAwait(false);

            return DocumentCacheReadLookupClassifier.Classify(request, observations);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ObjectDisposedException)
        {
            throw;
        }
        catch (DocumentCacheReadLookupInvariantException exception)
        {
            return DocumentCacheReadBatchLookupResult.PageFallback(
                DocumentCacheReadLookupOutcome.DeterministicInvariantFailure,
                request.Candidates,
                exception.Message
            );
        }
        catch (Exception exception) when (IsCacheUnavailable(exception))
        {
            return DocumentCacheReadBatchLookupResult.PageFallback(
                DocumentCacheReadLookupOutcome.CacheUnavailable,
                request.Candidates,
                "DocumentCache read lookup provider availability failure."
            );
        }
    }

    protected abstract Task<TResult> ExecuteReaderAsync<TResult>(
        DocumentCacheTargetExecutionContext targetContext,
        RelationalCommand command,
        Func<IRelationalCommandReader, CancellationToken, Task<TResult>> readAsync,
        CancellationToken cancellationToken = default
    );

    protected abstract bool IsCacheUnavailable(Exception exception);

    private void RequireTargetBinding(DocumentCacheTargetExecutionContext targetContext)
    {
        if (targetContext.ProviderToken != ProviderToken)
        {
            throw new InvalidOperationException(
                $"DocumentCache read lookup adapter provider '{ProviderToken}' cannot bind target provider '{targetContext.ProviderToken}'."
            );
        }
    }

    private static DocumentCacheReadBatchLookupResult? TryClassifyTargetEligibility(
        DocumentCacheTargetExecutionContext targetContext,
        IReadOnlyList<DocumentCacheReadAccelerationCandidate> candidates
    )
    {
        if (targetContext.Inventory.Status != DocumentCacheInventoryStatus.Satisfied)
        {
            return DocumentCacheReadBatchLookupResult.PageFallback(
                DocumentCacheReadLookupOutcome.ProjectionTargetIneligible,
                candidates,
                "DocumentCache target inventory is not eligible for cache reads."
            );
        }

        if (targetContext.SqlServerPrerequisites?.HasFailure == true)
        {
            return DocumentCacheReadBatchLookupResult.PageFallback(
                DocumentCacheReadLookupOutcome.ProviderPrerequisiteIneligible,
                candidates,
                "DocumentCache provider prerequisites are not eligible for cache reads."
            );
        }

        return null;
    }

    private static DocumentCacheReadAccelerationFallbackReason MapFallbackReason(
        DocumentCacheReadLookupOutcome outcome
    ) =>
        outcome switch
        {
            DocumentCacheReadLookupOutcome.FreshHit =>
                DocumentCacheReadAccelerationFallbackReason.CacheHitResponseShapingUnavailable,
            DocumentCacheReadLookupOutcome.CacheUnavailable =>
                DocumentCacheReadAccelerationFallbackReason.CacheLookupUnavailable,
            DocumentCacheReadLookupOutcome.DeterministicInvariantFailure =>
                DocumentCacheReadAccelerationFallbackReason.CacheLookupInvariantFailure,
            DocumentCacheReadLookupOutcome.LifecycleDisabled
            or DocumentCacheReadLookupOutcome.LifecycleResetting
            or DocumentCacheReadLookupOutcome.LifecycleRebuilding
            or DocumentCacheReadLookupOutcome.CacheAheadRecoveryRequired
            or DocumentCacheReadLookupOutcome.MissingLifecycleState
            or DocumentCacheReadLookupOutcome.InvalidLifecycleState
            or DocumentCacheReadLookupOutcome.ProjectionTargetIneligible
            or DocumentCacheReadLookupOutcome.ProviderPrerequisiteIneligible =>
                DocumentCacheReadAccelerationFallbackReason.CacheLookupFenced,
            DocumentCacheReadLookupOutcome.SourceDrift =>
                DocumentCacheReadAccelerationFallbackReason.CacheLookupSourceDrift,
            DocumentCacheReadLookupOutcome.StaleCacheRow =>
                DocumentCacheReadAccelerationFallbackReason.CacheLookupStale,
            _ => DocumentCacheReadAccelerationFallbackReason.CacheLookupMiss,
        };
}

public sealed class DocumentCacheReadLookupInvariantException : Exception
{
    public DocumentCacheReadLookupInvariantException(string message)
        : base(message) { }
}

internal static class DocumentCacheReadLookupDiagnosticText
{
    private const int MaximumLength = 512;

    public static string Sanitize(string? message)
    {
        string sanitized = LogSanitizer.SanitizeForLog(message);
        return sanitized.Length <= MaximumLength ? sanitized : sanitized[..MaximumLength];
    }
}

internal sealed record DocumentCacheReadLookupObservation(
    int Ordinal,
    long RequestedDocumentId,
    Guid ExpectedDocumentUuid,
    short ExpectedResourceKeyId,
    long ExpectedContentVersion,
    int LifecycleRowCount,
    string? LifecycleState,
    bool? CacheAheadRecoveryRequired,
    long? SourceDocumentId,
    Guid? SourceDocumentUuid,
    short? SourceResourceKeyId,
    long? SourceContentVersion,
    DateTimeOffset? SourceContentLastModifiedAt,
    long? CacheDocumentId,
    Guid? CacheDocumentUuid,
    string? CacheProjectName,
    string? CacheResourceName,
    string? CacheResourceVersion,
    long? CacheContentVersion,
    string? StreamEtag,
    DateTimeOffset? CacheLastModifiedAt,
    string? DocumentJson
);

internal static class DocumentCacheReadLookupClassifier
{
    public static DocumentCacheReadBatchLookupResult Classify(
        DocumentCacheReadBatchLookupRequest request,
        IReadOnlyList<DocumentCacheReadLookupObservation> observations
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(observations);

        if (observations.Count != request.Candidates.Count)
        {
            throw new DocumentCacheReadLookupInvariantException(
                "DocumentCache read lookup returned an unexpected row count."
            );
        }

        List<DocumentCacheReadDocumentLookupResult> results = new(observations.Count);

        for (var index = 0; index < observations.Count; index++)
        {
            DocumentCacheReadAccelerationCandidate candidate = request.Candidates[index];
            DocumentCacheReadLookupObservation observation = observations[index];

            if (
                observation.Ordinal != index
                || observation.RequestedDocumentId != candidate.DocumentId
                || observation.ExpectedDocumentUuid != candidate.DocumentUuid.Value
                || observation.ExpectedResourceKeyId != candidate.ResourceKeyId
                || observation.ExpectedContentVersion != candidate.ContentVersion
            )
            {
                throw new DocumentCacheReadLookupInvariantException(
                    "DocumentCache read lookup returned rows that do not match the requested candidates."
                );
            }

            results.Add(ClassifyDocument(request.MappingSet, candidate, observation));
        }

        return DocumentCacheReadBatchLookupResult.FromDocuments(results);
    }

    private static DocumentCacheReadDocumentLookupResult ClassifyDocument(
        MappingSet mappingSet,
        DocumentCacheReadAccelerationCandidate candidate,
        DocumentCacheReadLookupObservation observation
    )
    {
        DocumentCacheReadDocumentLookupResult.Fallback Fallback(
            DocumentCacheReadLookupOutcome outcome,
            string message
        ) => new(outcome, candidate, message);

        if (observation.LifecycleRowCount == 0)
        {
            return Fallback(
                DocumentCacheReadLookupOutcome.MissingLifecycleState,
                "DocumentCache lifecycle state row is missing."
            );
        }

        if (
            observation.LifecycleRowCount != 1
            || string.IsNullOrWhiteSpace(observation.LifecycleState)
            || observation.CacheAheadRecoveryRequired is null
            || !DocumentCacheLifecycleTokenParser.TryParse(
                observation.LifecycleState,
                out DocumentCacheLifecycleState lifecycle
            )
        )
        {
            return Fallback(
                DocumentCacheReadLookupOutcome.InvalidLifecycleState,
                "DocumentCache lifecycle state row is invalid."
            );
        }

        if (lifecycle != DocumentCacheLifecycleState.Tracking)
        {
            return Fallback(
                lifecycle switch
                {
                    DocumentCacheLifecycleState.Disabled => DocumentCacheReadLookupOutcome.LifecycleDisabled,
                    DocumentCacheLifecycleState.Resetting =>
                        DocumentCacheReadLookupOutcome.LifecycleResetting,
                    DocumentCacheLifecycleState.Rebuilding =>
                        DocumentCacheReadLookupOutcome.LifecycleRebuilding,
                    _ => DocumentCacheReadLookupOutcome.InvalidLifecycleState,
                },
                "DocumentCache lifecycle state is not eligible for cache reads."
            );
        }

        if (observation.CacheAheadRecoveryRequired.Value)
        {
            return Fallback(
                DocumentCacheReadLookupOutcome.CacheAheadRecoveryRequired,
                "DocumentCache cache-ahead recovery latch is set."
            );
        }

        if (observation.SourceDocumentId is null)
        {
            return Fallback(
                DocumentCacheReadLookupOutcome.MissingSourceRow,
                "DocumentCache source row is missing after candidate selection."
            );
        }

        if (observation.SourceDocumentId != candidate.DocumentId)
        {
            return Fallback(
                DocumentCacheReadLookupOutcome.DeterministicInvariantFailure,
                "DocumentCache source row identity does not match the authorized candidate."
            );
        }

        if (
            observation.SourceDocumentUuid != candidate.DocumentUuid.Value
            || observation.SourceResourceKeyId != candidate.ResourceKeyId
            || observation.SourceContentVersion != candidate.ContentVersion
        )
        {
            return Fallback(
                DocumentCacheReadLookupOutcome.SourceDrift,
                "DocumentCache source row drifted after candidate selection."
            );
        }

        if (
            !mappingSet.ResourceKeyById.TryGetValue(
                candidate.ResourceKeyId,
                out ResourceKeyEntry? resourceKey
            )
        )
        {
            return Fallback(
                DocumentCacheReadLookupOutcome.DeterministicInvariantFailure,
                "DocumentCache read lookup candidate resource key is missing from the mapping set."
            );
        }

        if (observation.CacheDocumentId is null)
        {
            return Fallback(DocumentCacheReadLookupOutcome.MissingCacheRow, "DocumentCache row is missing.");
        }

        if (observation.CacheDocumentId != candidate.DocumentId)
        {
            return Fallback(
                DocumentCacheReadLookupOutcome.DeterministicInvariantFailure,
                "DocumentCache row document identity does not match the authorized candidate."
            );
        }

        if (
            observation.CacheDocumentUuid != candidate.DocumentUuid.Value
            || !string.Equals(
                observation.CacheProjectName,
                resourceKey.Resource.ProjectName,
                StringComparison.Ordinal
            )
            || !string.Equals(
                observation.CacheResourceName,
                resourceKey.Resource.ResourceName,
                StringComparison.Ordinal
            )
            || !string.Equals(
                observation.CacheResourceVersion,
                resourceKey.ResourceVersion,
                StringComparison.Ordinal
            )
        )
        {
            return Fallback(
                DocumentCacheReadLookupOutcome.DeterministicInvariantFailure,
                "DocumentCache row identity metadata does not match the authorized candidate."
            );
        }

        if (observation.CacheContentVersion < candidate.ContentVersion)
        {
            return Fallback(DocumentCacheReadLookupOutcome.StaleCacheRow, "DocumentCache row is stale.");
        }

        if (
            observation.CacheContentVersion != candidate.ContentVersion
            || observation.CacheLastModifiedAt != candidate.ContentLastModifiedAt
            || string.IsNullOrWhiteSpace(observation.StreamEtag)
            || string.IsNullOrWhiteSpace(observation.DocumentJson)
        )
        {
            return Fallback(
                DocumentCacheReadLookupOutcome.DeterministicInvariantFailure,
                "DocumentCache row has invalid matching-version metadata."
            );
        }

        return new DocumentCacheReadDocumentLookupResult.FreshHit(
            candidate,
            observation.DocumentJson,
            observation.StreamEtag,
            observation.CacheLastModifiedAt.Value
        );
    }
}

internal static class DocumentCacheReadLookupSql
{
    public static RelationalCommand BuildCommand(
        SqlDialect dialect,
        IReadOnlyList<DocumentCacheReadAccelerationCandidate> candidates
    )
    {
        ArgumentNullException.ThrowIfNull(candidates);

        if (candidates.Count == 0)
        {
            throw new ArgumentException("DocumentCache read lookup requires at least one candidate.");
        }

        return dialect switch
        {
            SqlDialect.Pgsql => BuildPostgresqlCommand(candidates),
            SqlDialect.Mssql => BuildMssqlCommand(candidates),
            _ => throw new NotSupportedException(
                $"DocumentCache read lookup does not support SQL dialect '{dialect}'."
            ),
        };
    }

    public static async Task<IReadOnlyList<DocumentCacheReadLookupObservation>> ReadObservationsAsync(
        IRelationalCommandReader reader,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(reader);

        try
        {
            List<DocumentCacheReadLookupObservation> rows = [];

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(ReadObservation(reader));
            }

            return rows;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsResultShapeFailure(exception))
        {
            throw new DocumentCacheReadLookupInvariantException(
                $"DocumentCache read lookup returned an invalid result shape: {exception.Message}"
            );
        }
    }

    private static RelationalCommand BuildPostgresqlCommand(
        IReadOnlyList<DocumentCacheReadAccelerationCandidate> candidates
    ) =>
        new(
            $$"""
            WITH requested ("Ordinal", "DocumentId", "ExpectedDocumentUuid", "ExpectedResourceKeyId", "ExpectedContentVersion") AS (
            {{BuildPostgresqlValues(candidates.Count)}}
            ),
            state_rows AS (
                SELECT
                    state."ProjectionLifecycleState",
                    state."CacheAheadRecoveryRequired"
                FROM "dms"."DocumentCacheState" state
                WHERE state."StateId" = 1
            ),
            state_count AS (
                SELECT COUNT(*)::integer AS "LifecycleRowCount"
                FROM state_rows
            ),
            state_row AS (
                SELECT
                    state_rows."ProjectionLifecycleState",
                    state_rows."CacheAheadRecoveryRequired"
                FROM state_rows
                LIMIT 1
            )
            SELECT
                requested."Ordinal" AS "Ordinal",
                requested."DocumentId" AS "RequestedDocumentId",
                requested."ExpectedDocumentUuid" AS "ExpectedDocumentUuid",
                requested."ExpectedResourceKeyId" AS "ExpectedResourceKeyId",
                requested."ExpectedContentVersion" AS "ExpectedContentVersion",
                state_count."LifecycleRowCount" AS "LifecycleRowCount",
                state_row."ProjectionLifecycleState" AS "LifecycleState",
                state_row."CacheAheadRecoveryRequired" AS "CacheAheadRecoveryRequired",
                document."DocumentId" AS "SourceDocumentId",
                document."DocumentUuid" AS "SourceDocumentUuid",
                document."ResourceKeyId" AS "SourceResourceKeyId",
                document."ContentVersion" AS "SourceContentVersion",
                document."ContentLastModifiedAt" AS "SourceContentLastModifiedAt",
                cache."DocumentId" AS "CacheDocumentId",
                cache."DocumentUuid" AS "CacheDocumentUuid",
                cache."ProjectName" AS "CacheProjectName",
                cache."ResourceName" AS "CacheResourceName",
                cache."ResourceVersion" AS "CacheResourceVersion",
                cache."ContentVersion" AS "CacheContentVersion",
                cache."StreamEtag" AS "StreamEtag",
                cache."LastModifiedAt" AS "CacheLastModifiedAt",
                cache."DocumentJson"::text AS "DocumentJson"
            FROM requested
            CROSS JOIN state_count
            LEFT JOIN state_row ON TRUE
            LEFT JOIN "dms"."Document" document
                ON document."DocumentId" = requested."DocumentId"
            LEFT JOIN "dms"."DocumentCache" cache
                ON cache."DocumentId" = requested."DocumentId"
            ORDER BY requested."Ordinal";
            """,
            BuildCandidateParameters(candidates)
        );

    private static RelationalCommand BuildMssqlCommand(
        IReadOnlyList<DocumentCacheReadAccelerationCandidate> candidates
    ) =>
        new(
            $$"""
            WITH [requested] ([Ordinal], [DocumentId], [ExpectedDocumentUuid], [ExpectedResourceKeyId], [ExpectedContentVersion]) AS (
            {{BuildMssqlValues(candidates.Count)}}
            ),
            [state_rows] AS (
                SELECT
                    [state].[ProjectionLifecycleState],
                    [state].[CacheAheadRecoveryRequired]
                FROM [dms].[DocumentCacheState] AS [state]
                WHERE [state].[StateId] = 1
            ),
            [state_count] AS (
                SELECT CAST(COUNT(*) AS int) AS [LifecycleRowCount]
                FROM [state_rows]
            ),
            [state_row] AS (
                SELECT TOP (1)
                    [state_rows].[ProjectionLifecycleState],
                    [state_rows].[CacheAheadRecoveryRequired]
                FROM [state_rows]
            )
            SELECT
                [requested].[Ordinal] AS [Ordinal],
                [requested].[DocumentId] AS [RequestedDocumentId],
                [requested].[ExpectedDocumentUuid] AS [ExpectedDocumentUuid],
                [requested].[ExpectedResourceKeyId] AS [ExpectedResourceKeyId],
                [requested].[ExpectedContentVersion] AS [ExpectedContentVersion],
                [state_count].[LifecycleRowCount] AS [LifecycleRowCount],
                [state_row].[ProjectionLifecycleState] AS [LifecycleState],
                [state_row].[CacheAheadRecoveryRequired] AS [CacheAheadRecoveryRequired],
                [document].[DocumentId] AS [SourceDocumentId],
                [document].[DocumentUuid] AS [SourceDocumentUuid],
                [document].[ResourceKeyId] AS [SourceResourceKeyId],
                [document].[ContentVersion] AS [SourceContentVersion],
                [document].[ContentLastModifiedAt] AS [SourceContentLastModifiedAt],
                [cache].[DocumentId] AS [CacheDocumentId],
                [cache].[DocumentUuid] AS [CacheDocumentUuid],
                [cache].[ProjectName] AS [CacheProjectName],
                [cache].[ResourceName] AS [CacheResourceName],
                [cache].[ResourceVersion] AS [CacheResourceVersion],
                [cache].[ContentVersion] AS [CacheContentVersion],
                [cache].[StreamEtag] AS [StreamEtag],
                [cache].[LastModifiedAt] AS [CacheLastModifiedAt],
                [cache].[DocumentJson] AS [DocumentJson]
            FROM [requested]
            CROSS JOIN [state_count]
            LEFT JOIN [state_row] ON 1 = 1
            LEFT JOIN [dms].[Document] AS [document]
                ON [document].[DocumentId] = [requested].[DocumentId]
            LEFT JOIN [dms].[DocumentCache] AS [cache]
                ON [cache].[DocumentId] = [requested].[DocumentId]
            ORDER BY [requested].[Ordinal];
            """,
            BuildCandidateParameters(candidates)
        );

    private static string BuildPostgresqlValues(int count) =>
        "VALUES\n"
        + string.Join(
            ",\n",
            Enumerable
                .Range(0, count)
                .Select(index =>
                    $"    (CAST(@ordinal{index} AS integer), CAST(@documentId{index} AS bigint), CAST(@documentUuid{index} AS uuid), CAST(@resourceKeyId{index} AS smallint), CAST(@contentVersion{index} AS bigint))"
                )
        );

    private static string BuildMssqlValues(int count) =>
        string.Join(
            "\nUNION ALL\n",
            Enumerable
                .Range(0, count)
                .Select(index =>
                    $"    SELECT CAST(@ordinal{index} AS int), CAST(@documentId{index} AS bigint), CAST(@documentUuid{index} AS uniqueidentifier), CAST(@resourceKeyId{index} AS smallint), CAST(@contentVersion{index} AS bigint)"
                )
        );

    private static IReadOnlyList<RelationalParameter> BuildCandidateParameters(
        IReadOnlyList<DocumentCacheReadAccelerationCandidate> candidates
    )
    {
        List<RelationalParameter> parameters = new(candidates.Count * 5);

        for (var index = 0; index < candidates.Count; index++)
        {
            DocumentCacheReadAccelerationCandidate candidate = candidates[index];
            parameters.Add(new RelationalParameter($"@ordinal{index}", index));
            parameters.Add(new RelationalParameter($"@documentId{index}", candidate.DocumentId));
            parameters.Add(new RelationalParameter($"@documentUuid{index}", candidate.DocumentUuid.Value));
            parameters.Add(new RelationalParameter($"@resourceKeyId{index}", candidate.ResourceKeyId));
            parameters.Add(new RelationalParameter($"@contentVersion{index}", candidate.ContentVersion));
        }

        return parameters;
    }

    private static DocumentCacheReadLookupObservation ReadObservation(IRelationalCommandReader reader) =>
        new(
            reader.GetRequiredFieldValue<int>("Ordinal"),
            reader.GetRequiredFieldValue<long>("RequestedDocumentId"),
            reader.GetRequiredFieldValue<Guid>("ExpectedDocumentUuid"),
            reader.GetRequiredFieldValue<short>("ExpectedResourceKeyId"),
            reader.GetRequiredFieldValue<long>("ExpectedContentVersion"),
            reader.GetRequiredFieldValue<int>("LifecycleRowCount"),
            GetNullableString(reader, "LifecycleState"),
            GetNullableStructValue<bool>(reader, "CacheAheadRecoveryRequired"),
            GetNullableStructValue<long>(reader, "SourceDocumentId"),
            GetNullableStructValue<Guid>(reader, "SourceDocumentUuid"),
            GetNullableStructValue<short>(reader, "SourceResourceKeyId"),
            GetNullableStructValue<long>(reader, "SourceContentVersion"),
            GetNullableDateTimeOffset(reader, "SourceContentLastModifiedAt"),
            GetNullableStructValue<long>(reader, "CacheDocumentId"),
            GetNullableStructValue<Guid>(reader, "CacheDocumentUuid"),
            GetNullableString(reader, "CacheProjectName"),
            GetNullableString(reader, "CacheResourceName"),
            GetNullableString(reader, "CacheResourceVersion"),
            GetNullableStructValue<long>(reader, "CacheContentVersion"),
            GetNullableString(reader, "StreamEtag"),
            GetNullableDateTimeOffset(reader, "CacheLastModifiedAt"),
            GetNullableString(reader, "DocumentJson")
        );

    private static T? GetNullableStructValue<T>(IRelationalCommandReader reader, string columnName)
        where T : struct
    {
        int ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<T>(ordinal);
    }

    private static string? GetNullableString(IRelationalCommandReader reader, string columnName)
    {
        int ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<string>(ordinal);
    }

    private static DateTimeOffset? GetNullableDateTimeOffset(
        IRelationalCommandReader reader,
        string columnName
    )
    {
        int ordinal = reader.GetOrdinal(columnName);
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        object value = reader.GetFieldValue<object>(ordinal);

        return value switch
        {
            DateTimeOffset dateTimeOffset => dateTimeOffset,
            DateTime dateTime => new DateTimeOffset(
                dateTime.Kind == DateTimeKind.Unspecified
                    ? DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)
                    : dateTime
            ),
            string text => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture),
            _ => throw new InvalidOperationException(
                $"DocumentCache read lookup expected a DateTimeOffset-compatible value for {columnName}, "
                    + $"but received '{value.GetType().Name}'."
            ),
        };
    }

    private static bool IsResultShapeFailure(Exception exception) =>
        exception
            is InvalidOperationException
                or InvalidCastException
                or IndexOutOfRangeException
                or ArgumentException
                or FormatException;
}
