// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
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

internal sealed record DocumentCacheReadLookupResult<TResult>(
    TResult? CachedResult,
    DocumentCacheReadAccelerationFallbackReason FallbackReason
)
    where TResult : class
{
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
            DocumentCacheReadAccelerationFallbackReason.CacheLookupMiss
    ) => new(null, reason);
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
    IDocumentCacheWriter? cacheWriter = null
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

        return await request
            .RelationalFallback(
                new DocumentCacheReadAccelerationFallbackContext(lookupResult.FallbackReason, targetContext),
                cancellationToken
            )
            .ConfigureAwait(false);
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

        return await request
            .RelationalFallback(
                new DocumentCacheReadAccelerationFallbackContext(lookupResult.FallbackReason, targetContext),
                cancellationToken
            )
            .ConfigureAwait(false);
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
        _ = _materializer;
        _ = _cacheWriter;
        return true;
    }
}
