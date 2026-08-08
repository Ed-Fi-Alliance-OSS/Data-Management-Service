// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
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
    IDocumentCacheReadTelemetry? readTelemetry = null,
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
    private readonly IDocumentCacheReadTelemetry _readTelemetry =
        readTelemetry ?? NoOpDocumentCacheReadTelemetry.Instance;
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
            RecordFallback(
                DocumentCacheReadAccelerationOperation.GetById,
                request.ResourceKind,
                targetContext: null,
                DocumentCacheReadAccelerationFallbackReason.NotExternalRead
            );

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
            RecordFallback(
                DocumentCacheReadAccelerationOperation.GetById,
                request.ResourceKind,
                targetContext: null,
                fallbackReason
            );

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
                RecordFallback(
                    DocumentCacheReadAccelerationOperation.GetById,
                    request.ResourceKind,
                    targetContext,
                    DocumentCacheReadAccelerationFallbackReason.CandidateSelectionUnavailable
                );

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
            RecordFallback(
                DocumentCacheReadAccelerationOperation.GetById,
                request.ResourceKind,
                targetContext,
                DocumentCacheReadAccelerationFallbackReason.CandidateSelectionUnavailable
            );

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

        DocumentCacheReadLookupResult<GetResult> lookupResult = await LookupGetByIdAsync(
                request,
                targetContext,
                cancellationToken
            )
            .ConfigureAwait(false);

        if (lookupResult.CachedResult is not null)
        {
            RecordHit(
                DocumentCacheReadAccelerationOperation.GetById,
                request.ResourceKind,
                targetContext,
                pageHit: false
            );

            return lookupResult.CachedResult;
        }

        RecordLookupFallback(
            DocumentCacheReadAccelerationOperation.GetById,
            request.ResourceKind,
            targetContext,
            lookupResult.FallbackReason
        );

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
            RecordFallback(
                DocumentCacheReadAccelerationOperation.Query,
                request.ResourceKind,
                targetContext: null,
                fallbackReason
            );

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
                RecordFallback(
                    DocumentCacheReadAccelerationOperation.Query,
                    request.ResourceKind,
                    targetContext,
                    DocumentCacheReadAccelerationFallbackReason.CandidateSelectionUnavailable
                );

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
            RecordFallback(
                DocumentCacheReadAccelerationOperation.Query,
                request.ResourceKind,
                targetContext,
                DocumentCacheReadAccelerationFallbackReason.CandidateSelectionUnavailable
            );

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

        DocumentCacheReadLookupResult<QueryResult> lookupResult = await LookupQueryAsync(
                request,
                targetContext,
                cancellationToken
            )
            .ConfigureAwait(false);

        if (lookupResult.CachedResult is not null)
        {
            RecordHit(
                DocumentCacheReadAccelerationOperation.Query,
                request.ResourceKind,
                targetContext,
                pageHit: true
            );

            return lookupResult.CachedResult;
        }

        RecordLookupFallback(
            DocumentCacheReadAccelerationOperation.Query,
            request.ResourceKind,
            targetContext,
            lookupResult.FallbackReason
        );

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

    private async Task<DocumentCacheReadLookupResult<GetResult>> LookupGetByIdAsync(
        DocumentCacheReadAccelerationGetByIdRequest request,
        DocumentCacheTargetExecutionContext targetContext,
        CancellationToken cancellationToken
    )
    {
        RecordAttempt(DocumentCacheReadAccelerationOperation.GetById, request.ResourceKind, targetContext);
        long lookupStartTimestamp = Stopwatch.GetTimestamp();

        try
        {
            DocumentCacheReadLookupResult<GetResult> result = await _lookupAdapter
                .TryGetByIdAsync(request, targetContext, cancellationToken)
                .ConfigureAwait(false);

            _readTelemetry.RecordCacheLookupDuration(
                CreateReadTelemetryContext(
                    targetContext,
                    DocumentCacheReadAccelerationOperation.GetById,
                    request.ResourceKind,
                    result.CachedResult is not null
                        ? DocumentCacheReadTelemetryLabel.Hit
                        : result.FallbackReason.ToString()
                ),
                DocumentCacheReadTelemetry.GetElapsedTime(lookupStartTimestamp)
            );

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            RecordUnexpectedLookupException(
                DocumentCacheReadAccelerationOperation.GetById,
                request.ResourceKind,
                targetContext,
                lookupStartTimestamp,
                exception
            );
            throw;
        }
    }

    private async Task<DocumentCacheReadLookupResult<QueryResult>> LookupQueryAsync(
        DocumentCacheReadAccelerationQueryRequest request,
        DocumentCacheTargetExecutionContext targetContext,
        CancellationToken cancellationToken
    )
    {
        RecordAttempt(DocumentCacheReadAccelerationOperation.Query, request.ResourceKind, targetContext);
        long lookupStartTimestamp = Stopwatch.GetTimestamp();

        try
        {
            DocumentCacheReadLookupResult<QueryResult> result = await _lookupAdapter
                .TryQueryAsync(request, targetContext, cancellationToken)
                .ConfigureAwait(false);

            _readTelemetry.RecordCacheLookupDuration(
                CreateReadTelemetryContext(
                    targetContext,
                    DocumentCacheReadAccelerationOperation.Query,
                    request.ResourceKind,
                    result.CachedResult is not null
                        ? DocumentCacheReadTelemetryLabel.PageHit
                        : result.FallbackReason.ToString()
                ),
                DocumentCacheReadTelemetry.GetElapsedTime(lookupStartTimestamp)
            );

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            RecordUnexpectedLookupException(
                DocumentCacheReadAccelerationOperation.Query,
                request.ResourceKind,
                targetContext,
                lookupStartTimestamp,
                exception
            );
            throw;
        }
    }

    private void RecordAttempt(
        DocumentCacheReadAccelerationOperation operation,
        DocumentCacheReadAccelerationResourceKind resourceKind,
        DocumentCacheTargetExecutionContext targetContext
    ) =>
        _readTelemetry.RecordAttempt(
            CreateReadTelemetryContext(
                targetContext,
                operation,
                resourceKind,
                DocumentCacheReadTelemetryLabel.Attempted
            )
        );

    private void RecordHit(
        DocumentCacheReadAccelerationOperation operation,
        DocumentCacheReadAccelerationResourceKind resourceKind,
        DocumentCacheTargetExecutionContext targetContext,
        bool pageHit
    )
    {
        DocumentCacheReadTelemetryContext context = CreateReadTelemetryContext(
            targetContext,
            operation,
            resourceKind,
            pageHit ? DocumentCacheReadTelemetryLabel.PageHit : DocumentCacheReadTelemetryLabel.Hit
        );

        _readTelemetry.RecordHit(context);
        if (pageHit)
        {
            _readTelemetry.RecordPageHit(context);
        }
    }

    private void RecordLookupFallback(
        DocumentCacheReadAccelerationOperation operation,
        DocumentCacheReadAccelerationResourceKind resourceKind,
        DocumentCacheTargetExecutionContext targetContext,
        DocumentCacheReadAccelerationFallbackReason fallbackReason
    )
    {
        _readTelemetry.RecordMiss(
            CreateReadTelemetryContext(targetContext, operation, resourceKind, fallbackReason.ToString())
        );
        RecordFallback(operation, resourceKind, targetContext, fallbackReason);
    }

    private void RecordFallback(
        DocumentCacheReadAccelerationOperation operation,
        DocumentCacheReadAccelerationResourceKind resourceKind,
        DocumentCacheTargetExecutionContext? targetContext,
        DocumentCacheReadAccelerationFallbackReason fallbackReason
    )
    {
        DocumentCacheReadTelemetryContext context = CreateReadTelemetryContext(
            targetContext,
            operation,
            resourceKind,
            fallbackReason.ToString()
        );

        _readTelemetry.RecordFallback(context);
        if (fallbackReason == DocumentCacheReadAccelerationFallbackReason.CacheLookupUnavailable)
        {
            _readTelemetry.RecordCacheUnavailable(context);
            _readTelemetry.RecordAdapterAcquisitionFailure(context);
        }
    }

    private void RecordUnexpectedLookupException(
        DocumentCacheReadAccelerationOperation operation,
        DocumentCacheReadAccelerationResourceKind resourceKind,
        DocumentCacheTargetExecutionContext targetContext,
        long lookupStartTimestamp,
        Exception exception
    )
    {
        DocumentCacheReadTelemetryContext context = CreateReadTelemetryContext(
            targetContext,
            operation,
            resourceKind,
            DocumentCacheReadTelemetryLabel.UnexpectedException
        );

        _readTelemetry.RecordCacheLookupDuration(
            context,
            DocumentCacheReadTelemetry.GetElapsedTime(lookupStartTimestamp)
        );
        _readTelemetry.RecordUnexpectedException(context);
        _logger.LogWarning(
            exception,
            "DocumentCache read lookup failed unexpectedly for target {TargetKey}. ExceptionType: {ExceptionType}",
            targetContext.TargetKey,
            exception.GetType().Name
        );
    }

    private void RecordDirectFill(
        DocumentCacheTargetExecutionContext targetContext,
        DocumentCacheReadAccelerationOperation operation,
        DocumentCacheReadAccelerationResourceKind resourceKind,
        string outcome
    ) =>
        _readTelemetry.RecordDirectFill(
            CreateReadTelemetryContext(targetContext, operation, resourceKind, outcome)
        );

    private static DocumentCacheReadTelemetryContext CreateReadTelemetryContext(
        DocumentCacheTargetExecutionContext? targetContext,
        DocumentCacheReadAccelerationOperation operation,
        DocumentCacheReadAccelerationResourceKind resourceKind,
        string outcome
    ) =>
        targetContext is null
            ? DocumentCacheReadTelemetryContext.ForNoTarget(operation, resourceKind, outcome)
            : DocumentCacheReadTelemetryContext.ForTarget(targetContext, operation, resourceKind, outcome);

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
            RecordDirectFill(
                targetContext,
                DocumentCacheReadAccelerationOperation.GetById,
                request.ResourceKind,
                DocumentCacheReadTelemetryLabel.SkippedFallbackNotSuccessful
            );
            return;
        }

        IReadOnlyList<DocumentCacheReadAccelerationCandidate> survivingCandidates = candidates
            .Where(candidate => candidate.DocumentUuid == success.DocumentUuid)
            .ToArray();

        await TryDirectFillAsync(
                request.MappingSet,
                targetContext,
                survivingCandidates,
                DocumentCacheReadAccelerationOperation.GetById,
                request.ResourceKind,
                cancellationToken
            )
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
            RecordDirectFill(
                targetContext,
                DocumentCacheReadAccelerationOperation.Query,
                request.ResourceKind,
                DocumentCacheReadTelemetryLabel.SkippedFallbackNotSuccessful
            );
            return;
        }

        ISet<Guid> servedDocumentUuids = GetServedDocumentUuids(success.EdfiDocs);
        if (servedDocumentUuids.Count == 0)
        {
            RecordDirectFill(
                targetContext,
                DocumentCacheReadAccelerationOperation.Query,
                request.ResourceKind,
                DocumentCacheReadTelemetryLabel.SkippedNoServedCandidate
            );
            return;
        }

        IReadOnlyList<DocumentCacheReadAccelerationCandidate> survivingCandidates = candidates
            .Where(candidate => servedDocumentUuids.Contains(candidate.DocumentUuid.Value))
            .ToArray();

        await TryDirectFillAsync(
                request.MappingSet,
                targetContext,
                survivingCandidates,
                DocumentCacheReadAccelerationOperation.Query,
                request.ResourceKind,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    private async Task TryDirectFillAsync(
        MappingSet mappingSet,
        DocumentCacheTargetExecutionContext targetContext,
        IReadOnlyList<DocumentCacheReadAccelerationCandidate> candidates,
        DocumentCacheReadAccelerationOperation operation,
        DocumentCacheReadAccelerationResourceKind resourceKind,
        CancellationToken cancellationToken
    )
    {
        if (candidates.Count == 0)
        {
            RecordDirectFill(
                targetContext,
                operation,
                resourceKind,
                DocumentCacheReadTelemetryLabel.SkippedNoCandidates
            );
            return;
        }

        if (_materializer is null || _cacheWriter is null)
        {
            RecordDirectFill(
                targetContext,
                operation,
                resourceKind,
                DocumentCacheReadTelemetryLabel.SkippedServicesUnavailable
            );
            return;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            RecordDirectFill(
                targetContext,
                operation,
                resourceKind,
                DocumentCacheReadTelemetryLabel.SkippedRequestCanceled
            );
            return;
        }

        if (!IsDirectFillTargetEligible(targetContext))
        {
            RecordDirectFill(
                targetContext,
                operation,
                resourceKind,
                DocumentCacheReadTelemetryLabel.SkippedTargetIneligible
            );
            return;
        }

        TimeSpan timeout = targetContext.EffectiveSettings.DirectFillTimeout;
        if (timeout <= TimeSpan.Zero)
        {
            RecordDirectFill(
                targetContext,
                operation,
                resourceKind,
                DocumentCacheReadTelemetryLabel.SkippedTimeoutDisabled
            );
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

        long directFillStartTimestamp = Stopwatch.GetTimestamp();
        string directFillDurationOutcome = DocumentCacheReadTelemetryLabel.Completed;

        try
        {
            foreach (DocumentCacheReadAccelerationCandidate candidate in candidates)
            {
                if (directFillTimeout.IsCancellationRequested)
                {
                    directFillDurationOutcome = DocumentCacheReadTelemetryLabel.TimedOut;
                    RecordDirectFill(targetContext, operation, resourceKind, directFillDurationOutcome);
                    return;
                }

                RecordDirectFill(
                    targetContext,
                    operation,
                    resourceKind,
                    DocumentCacheReadTelemetryLabel.Attempted
                );

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
                        directFillDurationOutcome = DocumentCacheReadTelemetryLabel.Failed;
                        RecordDirectFill(targetContext, operation, resourceKind, directFillDurationOutcome);
                        continue;
                    }

                    DocumentCacheWriterResult writerResult = await _cacheWriter
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

                    directFillDurationOutcome = IsDirectFillWriterSuccess(writerResult)
                        ? DocumentCacheReadTelemetryLabel.Succeeded
                        : DocumentCacheReadTelemetryLabel.Failed;
                    RecordDirectFill(targetContext, operation, resourceKind, directFillDurationOutcome);
                }
                catch (OperationCanceledException)
                {
                    directFillDurationOutcome = SelectDirectFillCancellationReason(
                        cancellationToken,
                        directFillTimeout
                    );
                    RecordDirectFill(targetContext, operation, resourceKind, directFillDurationOutcome);
                    LogDirectFillSkipped(targetContext, directFillDurationOutcome);
                    return;
                }
                catch (Exception exception)
                {
                    directFillDurationOutcome = DocumentCacheReadTelemetryLabel.Failed;
                    RecordDirectFill(targetContext, operation, resourceKind, directFillDurationOutcome);
                    LogDirectFillFailure(targetContext, exception);
                }
            }
        }
        finally
        {
            _readTelemetry.RecordDirectFillDuration(
                CreateReadTelemetryContext(targetContext, operation, resourceKind, directFillDurationOutcome),
                DocumentCacheReadTelemetry.GetElapsedTime(directFillStartTimestamp)
            );
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

    private static bool IsDirectFillWriterSuccess(DocumentCacheWriterResult result) =>
        result
            is DocumentCacheWriterResult.CandidateWrittenAcknowledged
                or DocumentCacheWriterResult.AlreadyCurrentAcknowledged
                or DocumentCacheWriterResult.AlreadyCurrentNoWork;

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
