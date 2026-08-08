// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Backend;

public enum DocumentCacheReadAccelerationResourceKind
{
    Resource,
    Descriptor,
}

public enum DocumentCacheReadAccelerationLookupReadiness
{
    RelationalFallbackOnly,
    AuthorizedCandidate,
}

public enum DocumentCacheReadAccelerationFallbackReason
{
    ReadAccelerationDisabled,
    NotExternalRead,
    InvalidTargetKey,
    SelectedDataStoreUnavailable,
    TargetRegistryUnavailable,
    UnresolvedTarget,
    TargetReadAccelerationDisabled,
    CandidateSelectionUnavailable,
    CacheLookupMiss,
    CacheLookupStale,
    CacheLookupSourceDrift,
    CacheLookupFenced,
    CacheLookupUnavailable,
    CacheLookupInvariantFailure,
    CacheHitResponseShapingUnavailable,
}

public sealed record DocumentCacheReadAccelerationFallbackContext(
    DocumentCacheReadAccelerationFallbackReason Reason,
    DocumentCacheTargetExecutionContext? TargetContext
);

public sealed record DocumentCacheReadAccelerationCandidate(
    long DocumentId,
    DocumentUuid DocumentUuid,
    short ResourceKeyId,
    long ContentVersion,
    DateTimeOffset ContentLastModifiedAt
);

public sealed record DocumentCacheReadAccelerationCandidatePage(
    IReadOnlyList<DocumentCacheReadAccelerationCandidate> Candidates,
    long? TotalCount,
    long? HighestSelectedDocumentId
)
{
    public bool IsEmpty => Candidates.Count == 0;
}

public abstract record DocumentCacheReadAccelerationGetByIdSelectionResult
{
    private DocumentCacheReadAccelerationGetByIdSelectionResult() { }

    public sealed record Complete(GetResult Result) : DocumentCacheReadAccelerationGetByIdSelectionResult;

    public sealed record Candidate(
        DocumentCacheReadAccelerationCandidate AuthorizedCandidate,
        Func<
            DocumentCacheReadAccelerationFallbackContext,
            CancellationToken,
            Task<GetResult>
        > RelationalFallback
    ) : DocumentCacheReadAccelerationGetByIdSelectionResult;
}

public abstract record DocumentCacheReadAccelerationQuerySelectionResult
{
    private DocumentCacheReadAccelerationQuerySelectionResult() { }

    public sealed record Complete(QueryResult Result) : DocumentCacheReadAccelerationQuerySelectionResult;

    public sealed record CandidatePage(
        DocumentCacheReadAccelerationCandidatePage AuthorizedCandidatePage,
        Func<
            DocumentCacheReadAccelerationFallbackContext,
            CancellationToken,
            Task<QueryResult>
        > RelationalFallback
    ) : DocumentCacheReadAccelerationQuerySelectionResult;
}

public sealed record DocumentCacheReadAccelerationGetByIdRequest(
    string TenantKey,
    MappingSet MappingSet,
    QualifiedResourceName Resource,
    DocumentUuid DocumentUuid,
    RelationalGetRequestReadMode ReadMode,
    DocumentCacheReadAccelerationResourceKind ResourceKind,
    DocumentCacheReadAccelerationLookupReadiness LookupReadiness,
    Func<DocumentCacheReadAccelerationFallbackContext, CancellationToken, Task<GetResult>> RelationalFallback,
    DocumentCacheReadAccelerationCandidate? AuthorizedCandidate = null,
    Func<
        CancellationToken,
        Task<DocumentCacheReadAccelerationGetByIdSelectionResult>
    >? SelectAuthorizedCandidate = null
)
{
    public ReadableProfileProjectionContext? ReadableProfileProjectionContext { get; init; }

    public ResponseContentCoding ResponseContentCoding { get; init; } = ResponseContentCoding.Identity;
}

public sealed record DocumentCacheReadAccelerationQueryRequest(
    string TenantKey,
    MappingSet MappingSet,
    QualifiedResourceName Resource,
    DocumentCacheReadAccelerationResourceKind ResourceKind,
    DocumentCacheReadAccelerationLookupReadiness LookupReadiness,
    Func<
        DocumentCacheReadAccelerationFallbackContext,
        CancellationToken,
        Task<QueryResult>
    > RelationalFallback,
    DocumentCacheReadAccelerationCandidatePage? AuthorizedCandidatePage = null,
    Func<
        CancellationToken,
        Task<DocumentCacheReadAccelerationQuerySelectionResult>
    >? SelectAuthorizedCandidatePage = null
)
{
    public ReadableProfileProjectionContext? ReadableProfileProjectionContext { get; init; }

    public ResponseContentCoding ResponseContentCoding { get; init; } = ResponseContentCoding.Identity;
}

internal sealed record DocumentCacheReadLookupResult<TResult>
    where TResult : class
{
    public DocumentCacheReadLookupResult(
        TResult? cachedResult,
        DocumentCacheReadAccelerationFallbackReason fallbackReason,
        IReadOnlyList<DocumentCacheReadAccelerationCandidate>? directFillCandidates = null
    )
    {
        CachedResult = cachedResult;
        FallbackReason = fallbackReason;
        DirectFillCandidates = directFillCandidates ?? [];
    }

    public TResult? CachedResult { get; }

    public DocumentCacheReadAccelerationFallbackReason FallbackReason { get; }

    public IReadOnlyList<DocumentCacheReadAccelerationCandidate> DirectFillCandidates { get; }

    public bool HasCachedResult => CachedResult is not null;

    public static DocumentCacheReadLookupResult<TResult> Hit(TResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new DocumentCacheReadLookupResult<TResult>(
            result,
            DocumentCacheReadAccelerationFallbackReason.CacheLookupMiss
        );
    }

    public static DocumentCacheReadLookupResult<TResult> Fallback(
        DocumentCacheReadAccelerationFallbackReason reason =
            DocumentCacheReadAccelerationFallbackReason.CacheLookupMiss,
        IReadOnlyList<DocumentCacheReadAccelerationCandidate>? directFillCandidates = null
    ) => new(null, reason, directFillCandidates);
}

internal interface IDocumentCacheReadLookupAdapter
{
    Task<DocumentCacheReadLookupResult<GetResult>> TryGetByIdAsync(
        DocumentCacheReadAccelerationGetByIdRequest request,
        DocumentCacheTargetExecutionContext targetContext,
        CancellationToken cancellationToken = default
    );

    Task<DocumentCacheReadLookupResult<QueryResult>> TryQueryAsync(
        DocumentCacheReadAccelerationQueryRequest request,
        DocumentCacheTargetExecutionContext targetContext,
        CancellationToken cancellationToken = default
    );
}

internal sealed class NoOpDocumentCacheReadLookupAdapter : IDocumentCacheReadLookupAdapter
{
    public static NoOpDocumentCacheReadLookupAdapter Instance { get; } = new();

    public NoOpDocumentCacheReadLookupAdapter() { }

    public Task<DocumentCacheReadLookupResult<GetResult>> TryGetByIdAsync(
        DocumentCacheReadAccelerationGetByIdRequest request,
        DocumentCacheTargetExecutionContext targetContext,
        CancellationToken cancellationToken = default
    ) => Task.FromResult(DocumentCacheReadLookupResult<GetResult>.Fallback());

    public Task<DocumentCacheReadLookupResult<QueryResult>> TryQueryAsync(
        DocumentCacheReadAccelerationQueryRequest request,
        DocumentCacheTargetExecutionContext targetContext,
        CancellationToken cancellationToken = default
    ) => Task.FromResult(DocumentCacheReadLookupResult<QueryResult>.Fallback());
}

public interface IDocumentCacheReadAccelerationCoordinator
{
    Task<GetResult> GetByIdAsync(
        DocumentCacheReadAccelerationGetByIdRequest request,
        CancellationToken cancellationToken = default
    );

    Task<QueryResult> QueryAsync(
        DocumentCacheReadAccelerationQueryRequest request,
        CancellationToken cancellationToken = default
    );
}

internal sealed class PassthroughDocumentCacheReadAccelerationCoordinator
    : IDocumentCacheReadAccelerationCoordinator
{
    public static PassthroughDocumentCacheReadAccelerationCoordinator Instance { get; } = new();

    private static readonly DocumentCacheReadAccelerationFallbackContext FallbackContext = new(
        DocumentCacheReadAccelerationFallbackReason.TargetRegistryUnavailable,
        TargetContext: null
    );

    private PassthroughDocumentCacheReadAccelerationCoordinator() { }

    public Task<GetResult> GetByIdAsync(
        DocumentCacheReadAccelerationGetByIdRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.RelationalFallback(FallbackContext, cancellationToken);
    }

    public Task<QueryResult> QueryAsync(
        DocumentCacheReadAccelerationQueryRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.RelationalFallback(FallbackContext, cancellationToken);
    }
}

internal sealed class DocumentCacheReadAccelerationCoordinator(
    IOptions<DocumentCacheOptions>? options = null,
    IDataStoreSelection? dataStoreSelection = null,
    IDocumentCacheTargetRegistry? targetRegistry = null,
    IDocumentCacheReadLookupAdapter? lookupAdapter = null,
    IDocumentCacheMaterializer? materializer = null,
    IDocumentCacheWriter? cacheWriter = null,
    ILogger<DocumentCacheReadAccelerationCoordinator>? logger = null
) : IDocumentCacheReadAccelerationCoordinator
{
    private readonly IOptions<DocumentCacheOptions> _options =
        options ?? Options.Create(new DocumentCacheOptions());
    private readonly IDataStoreSelection? _dataStoreSelection = dataStoreSelection;
    private readonly IDocumentCacheTargetRegistry? _targetRegistry = targetRegistry;
    private readonly IDocumentCacheReadLookupAdapter _lookupAdapter =
        lookupAdapter ?? NoOpDocumentCacheReadLookupAdapter.Instance;
    private readonly IDocumentCacheMaterializer? _materializer = materializer;
    private readonly IDocumentCacheWriter? _cacheWriter = cacheWriter;
    private readonly ILogger<DocumentCacheReadAccelerationCoordinator> _logger =
        logger ?? NullLogger<DocumentCacheReadAccelerationCoordinator>.Instance;

    public async Task<GetResult> GetByIdAsync(
        DocumentCacheReadAccelerationGetByIdRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.ReadMode != RelationalGetRequestReadMode.ExternalResponse)
        {
            return await request
                .RelationalFallback(
                    new DocumentCacheReadAccelerationFallbackContext(
                        DocumentCacheReadAccelerationFallbackReason.NotExternalRead,
                        TargetContext: null
                    ),
                    cancellationToken
                )
                .ConfigureAwait(false);
        }

        if (!TryResolveTarget(request.TenantKey, out var targetContext, out var fallbackReason))
        {
            return await request
                .RelationalFallback(
                    new DocumentCacheReadAccelerationFallbackContext(fallbackReason, TargetContext: null),
                    cancellationToken
                )
                .ConfigureAwait(false);
        }

        if (request.LookupReadiness != DocumentCacheReadAccelerationLookupReadiness.AuthorizedCandidate)
        {
            if (request.SelectAuthorizedCandidate is null)
            {
                return await request
                    .RelationalFallback(
                        new DocumentCacheReadAccelerationFallbackContext(
                            DocumentCacheReadAccelerationFallbackReason.CandidateSelectionUnavailable,
                            targetContext
                        ),
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }

            var selectionResult = await request
                .SelectAuthorizedCandidate(cancellationToken)
                .ConfigureAwait(false);

            switch (selectionResult)
            {
                case DocumentCacheReadAccelerationGetByIdSelectionResult.Complete complete:
                    return complete.Result;

                case DocumentCacheReadAccelerationGetByIdSelectionResult.Candidate candidate:
                    request = request with
                    {
                        LookupReadiness = DocumentCacheReadAccelerationLookupReadiness.AuthorizedCandidate,
                        AuthorizedCandidate = candidate.AuthorizedCandidate,
                        RelationalFallback = candidate.RelationalFallback,
                    };
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Unsupported GET-by-id candidate selection result '{selectionResult.GetType().Name}'."
                    );
            }
        }

        if (request.AuthorizedCandidate is null)
        {
            return await request
                .RelationalFallback(
                    new DocumentCacheReadAccelerationFallbackContext(
                        DocumentCacheReadAccelerationFallbackReason.CandidateSelectionUnavailable,
                        targetContext
                    ),
                    cancellationToken
                )
                .ConfigureAwait(false);
        }

        DocumentCacheReadLookupResult<GetResult> lookupResult = await _lookupAdapter
            .TryGetByIdAsync(request, targetContext, cancellationToken)
            .ConfigureAwait(false);

        if (lookupResult.CachedResult is not null)
        {
            return lookupResult.CachedResult;
        }

        GetResult fallbackResult = await request
            .RelationalFallback(
                new DocumentCacheReadAccelerationFallbackContext(lookupResult.FallbackReason, targetContext),
                cancellationToken
            )
            .ConfigureAwait(false);

        await TryDirectFillAsync(
                request,
                targetContext,
                SelectGetByIdDirectFillCandidates(request, targetContext, lookupResult),
                fallbackResult,
                cancellationToken
            )
            .ConfigureAwait(false);

        return fallbackResult;
    }

    public async Task<QueryResult> QueryAsync(
        DocumentCacheReadAccelerationQueryRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!TryResolveTarget(request.TenantKey, out var targetContext, out var fallbackReason))
        {
            return await request
                .RelationalFallback(
                    new DocumentCacheReadAccelerationFallbackContext(fallbackReason, TargetContext: null),
                    cancellationToken
                )
                .ConfigureAwait(false);
        }

        if (request.LookupReadiness != DocumentCacheReadAccelerationLookupReadiness.AuthorizedCandidate)
        {
            if (request.SelectAuthorizedCandidatePage is null)
            {
                return await request
                    .RelationalFallback(
                        new DocumentCacheReadAccelerationFallbackContext(
                            DocumentCacheReadAccelerationFallbackReason.CandidateSelectionUnavailable,
                            targetContext
                        ),
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }

            var selectionResult = await request
                .SelectAuthorizedCandidatePage(cancellationToken)
                .ConfigureAwait(false);

            switch (selectionResult)
            {
                case DocumentCacheReadAccelerationQuerySelectionResult.Complete complete:
                    return complete.Result;

                case DocumentCacheReadAccelerationQuerySelectionResult.CandidatePage candidatePage:
                    request = request with
                    {
                        LookupReadiness = DocumentCacheReadAccelerationLookupReadiness.AuthorizedCandidate,
                        AuthorizedCandidatePage = candidatePage.AuthorizedCandidatePage,
                        RelationalFallback = candidatePage.RelationalFallback,
                    };
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Unsupported query candidate selection result '{selectionResult.GetType().Name}'."
                    );
            }
        }

        if (request.AuthorizedCandidatePage is null)
        {
            return await request
                .RelationalFallback(
                    new DocumentCacheReadAccelerationFallbackContext(
                        DocumentCacheReadAccelerationFallbackReason.CandidateSelectionUnavailable,
                        targetContext
                    ),
                    cancellationToken
                )
                .ConfigureAwait(false);
        }

        if (request.AuthorizedCandidatePage.IsEmpty)
        {
            return new QueryResult.QuerySuccess(
                [],
                request.AuthorizedCandidatePage.TotalCount is null
                    ? null
                    : RelationalReadGuardrails.ConvertTotalCountOrThrow(
                        request.Resource,
                        request.AuthorizedCandidatePage.TotalCount,
                        "cache query empty-page response"
                    ),
                request.AuthorizedCandidatePage.HighestSelectedDocumentId
            );
        }

        DocumentCacheReadLookupResult<QueryResult> lookupResult = await _lookupAdapter
            .TryQueryAsync(request, targetContext, cancellationToken)
            .ConfigureAwait(false);

        if (lookupResult.CachedResult is not null)
        {
            return lookupResult.CachedResult;
        }

        QueryResult fallbackResult = await request
            .RelationalFallback(
                new DocumentCacheReadAccelerationFallbackContext(lookupResult.FallbackReason, targetContext),
                cancellationToken
            )
            .ConfigureAwait(false);

        await TryDirectFillAsync(
                request,
                targetContext,
                SelectQueryDirectFillCandidates(request, targetContext, lookupResult, fallbackResult),
                fallbackResult,
                cancellationToken
            )
            .ConfigureAwait(false);

        return fallbackResult;
    }

    private bool TryResolveTarget(
        string tenantKey,
        out DocumentCacheTargetExecutionContext targetContext,
        out DocumentCacheReadAccelerationFallbackReason fallbackReason
    )
    {
        targetContext = null!;

        if (!_options.Value.ReadAcceleration.Enabled)
        {
            fallbackReason = DocumentCacheReadAccelerationFallbackReason.ReadAccelerationDisabled;
            return false;
        }

        if (_dataStoreSelection is null || !_dataStoreSelection.IsSet)
        {
            fallbackReason = DocumentCacheReadAccelerationFallbackReason.SelectedDataStoreUnavailable;
            return false;
        }

        DataStore selectedDataStore;
        try
        {
            selectedDataStore = _dataStoreSelection.GetSelectedDataStore();
        }
        catch (InvalidOperationException)
        {
            fallbackReason = DocumentCacheReadAccelerationFallbackReason.SelectedDataStoreUnavailable;
            return false;
        }

        if (
            !DocumentCacheTargetKey.TryCreate(
                tenantKey,
                selectedDataStore.Id,
                out DocumentCacheTargetKey? targetKey,
                out _
            )
        )
        {
            fallbackReason = DocumentCacheReadAccelerationFallbackReason.InvalidTargetKey;
            return false;
        }

        if (_targetRegistry is null)
        {
            fallbackReason = DocumentCacheReadAccelerationFallbackReason.TargetRegistryUnavailable;
            return false;
        }

        DocumentCacheTargetExecutionContext? resolvedTargetContext =
            _targetRegistry.CurrentRuntimeSnapshot.GetExecutionContext(targetKey);

        if (resolvedTargetContext is null)
        {
            fallbackReason = DocumentCacheReadAccelerationFallbackReason.UnresolvedTarget;
            return false;
        }

        if (!resolvedTargetContext.EffectiveSettings.ReadAccelerationEnabled)
        {
            fallbackReason = DocumentCacheReadAccelerationFallbackReason.TargetReadAccelerationDisabled;
            return false;
        }

        targetContext = resolvedTargetContext;
        fallbackReason = DocumentCacheReadAccelerationFallbackReason.CacheLookupMiss;
        return true;
    }

    private async Task TryDirectFillAsync(
        DocumentCacheReadAccelerationGetByIdRequest request,
        DocumentCacheTargetExecutionContext targetContext,
        IReadOnlyList<DocumentCacheReadAccelerationCandidate> candidates,
        GetResult fallbackResult,
        CancellationToken cancellationToken
    )
    {
        if (fallbackResult is not GetResult.GetSuccess success)
        {
            return;
        }

        IReadOnlyList<DocumentCacheReadAccelerationCandidate> survivingCandidates = candidates
            .Where(candidate => candidate.DocumentUuid == success.DocumentUuid)
            .ToArray();

        await TryDirectFillAsync(request.MappingSet, targetContext, survivingCandidates, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task TryDirectFillAsync(
        DocumentCacheReadAccelerationQueryRequest request,
        DocumentCacheTargetExecutionContext targetContext,
        IReadOnlyList<DocumentCacheReadAccelerationCandidate> candidates,
        QueryResult fallbackResult,
        CancellationToken cancellationToken
    )
    {
        if (fallbackResult is not QueryResult.QuerySuccess success)
        {
            return;
        }

        ISet<Guid> servedDocumentUuids = GetServedDocumentUuids(success.EdfiDocs);
        if (servedDocumentUuids.Count == 0)
        {
            return;
        }

        IReadOnlyList<DocumentCacheReadAccelerationCandidate> survivingCandidates = candidates
            .Where(candidate => servedDocumentUuids.Contains(candidate.DocumentUuid.Value))
            .ToArray();

        await TryDirectFillAsync(request.MappingSet, targetContext, survivingCandidates, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task TryDirectFillAsync(
        MappingSet mappingSet,
        DocumentCacheTargetExecutionContext targetContext,
        IReadOnlyList<DocumentCacheReadAccelerationCandidate> candidates,
        CancellationToken cancellationToken
    )
    {
        if (
            candidates.Count == 0
            || _materializer is null
            || _cacheWriter is null
            || cancellationToken.IsCancellationRequested
            || !IsDirectFillTargetEligible(targetContext)
        )
        {
            return;
        }

        TimeSpan timeout = targetContext.EffectiveSettings.DirectFillTimeout;
        if (timeout <= TimeSpan.Zero)
        {
            return;
        }

        using CancellationTokenSource directFillTimeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        directFillTimeout.CancelAfter(timeout);

        var materializationTargetContext = new DocumentCacheMaterializationTargetContext(
            new DocumentCacheProjectionTargetKey(
                targetContext.TargetKey.TenantKey,
                new DataStoreId(targetContext.TargetKey.DataStoreId)
            ),
            mappingSet,
            DocumentCacheMaterializationTargetValidation.EffectiveSchemaAndResourceKeySeedValidated,
            targetContext.ConnectionInput.Value
        );

        foreach (DocumentCacheReadAccelerationCandidate candidate in candidates)
        {
            if (directFillTimeout.IsCancellationRequested)
            {
                return;
            }

            try
            {
                DocumentCacheMaterializationResult materializationResult = await _materializer
                    .MaterializeAsync(
                        new DocumentCacheMaterializationRequest(
                            materializationTargetContext,
                            candidate.DocumentId,
                            selectedRequiredContentVersion: candidate.ContentVersion,
                            DocumentCacheMaterializationPurpose.DirectFill,
                            directFillTimeout.Token
                        )
                    )
                    .ConfigureAwait(false);

                if (materializationResult is not DocumentCacheMaterializationResult.Success success)
                {
                    continue;
                }

                await _cacheWriter
                    .WriteAsync(
                        new DocumentCacheWriterRequest(
                            materializationTargetContext,
                            candidate.DocumentId,
                            selectedRequiredContentVersion: success.Candidate.ContentVersion,
                            DocumentCacheWriterPurpose.DirectFill,
                            success.Candidate,
                            directFillTimeout.Token
                        )
                    )
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                LogDirectFillSkipped(
                    targetContext,
                    SelectDirectFillCancellationReason(cancellationToken, directFillTimeout)
                );
                return;
            }
            catch (Exception exception)
            {
                LogDirectFillFailure(targetContext, exception);
            }
        }
    }

    private static IReadOnlyList<DocumentCacheReadAccelerationCandidate> SelectGetByIdDirectFillCandidates(
        DocumentCacheReadAccelerationGetByIdRequest request,
        DocumentCacheTargetExecutionContext targetContext,
        DocumentCacheReadLookupResult<GetResult> lookupResult
    )
    {
        if (!IsDirectFillTargetEligible(targetContext) || request.AuthorizedCandidate is null)
        {
            return [];
        }

        if (
            targetContext.Lifecycle.State == DocumentCacheLifecycleState.Rebuilding
            && lookupResult.FallbackReason == DocumentCacheReadAccelerationFallbackReason.CacheLookupFenced
        )
        {
            return [request.AuthorizedCandidate];
        }

        return IsDocumentLevelDirectFillReason(lookupResult.FallbackReason)
            ? [request.AuthorizedCandidate]
            : [];
    }

    private static IReadOnlyList<DocumentCacheReadAccelerationCandidate> SelectQueryDirectFillCandidates(
        DocumentCacheReadAccelerationQueryRequest request,
        DocumentCacheTargetExecutionContext targetContext,
        DocumentCacheReadLookupResult<QueryResult> lookupResult,
        QueryResult fallbackResult
    )
    {
        if (
            !IsDirectFillTargetEligible(targetContext)
            || request.AuthorizedCandidatePage is null
            || fallbackResult is not QueryResult.QuerySuccess
        )
        {
            return [];
        }

        if (
            targetContext.Lifecycle.State == DocumentCacheLifecycleState.Rebuilding
            && lookupResult.FallbackReason == DocumentCacheReadAccelerationFallbackReason.CacheLookupFenced
        )
        {
            return request.AuthorizedCandidatePage.Candidates;
        }

        return IsDocumentLevelDirectFillReason(lookupResult.FallbackReason)
            ? lookupResult.DirectFillCandidates
            : [];
    }

    private static bool IsDirectFillTargetEligible(DocumentCacheTargetExecutionContext targetContext) =>
        targetContext.Lifecycle
            is { CacheAheadRecoveryRequired: false }
                and { State: DocumentCacheLifecycleState.Tracking or DocumentCacheLifecycleState.Rebuilding }
        && targetContext.Inventory.Status == DocumentCacheInventoryStatus.Satisfied
        && targetContext.SqlServerPrerequisites?.HasFailure != true;

    private static bool IsDocumentLevelDirectFillReason(
        DocumentCacheReadAccelerationFallbackReason fallbackReason
    ) =>
        fallbackReason
            is DocumentCacheReadAccelerationFallbackReason.CacheLookupMiss
                or DocumentCacheReadAccelerationFallbackReason.CacheLookupStale
                or DocumentCacheReadAccelerationFallbackReason.CacheLookupSourceDrift;

    private static ISet<Guid> GetServedDocumentUuids(JsonArray edfiDocs)
    {
        HashSet<Guid> documentUuids = [];

        foreach (JsonNode? edfiDoc in edfiDocs)
        {
            if (
                edfiDoc is JsonObject jsonObject
                && jsonObject.TryGetPropertyValue("id", out JsonNode? idNode)
                && idNode is JsonValue idValue
                && idValue.TryGetValue(out string? id)
                && Guid.TryParse(id, out Guid documentUuid)
            )
            {
                documentUuids.Add(documentUuid);
            }
        }

        return documentUuids;
    }

    private static string SelectDirectFillCancellationReason(
        CancellationToken requestCancellationToken,
        CancellationTokenSource directFillTimeout
    )
    {
        if (requestCancellationToken.IsCancellationRequested)
        {
            return "CallerCanceled";
        }

        return directFillTimeout.IsCancellationRequested ? "TimedOut" : "Canceled";
    }

    private void LogDirectFillSkipped(DocumentCacheTargetExecutionContext targetContext, string reason) =>
        _logger.LogDebug(
            "DocumentCache direct fill stopped for target {TargetKey}. Reason: {Reason}",
            targetContext.TargetKey,
            reason
        );

    private void LogDirectFillFailure(
        DocumentCacheTargetExecutionContext targetContext,
        Exception exception
    ) =>
        _logger.LogWarning(
            "DocumentCache direct fill failed for target {TargetKey}. ExceptionType: {ExceptionType}",
            targetContext.TargetKey,
            exception.GetType().Name
        );
}
