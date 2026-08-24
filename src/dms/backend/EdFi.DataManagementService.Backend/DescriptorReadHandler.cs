// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.Etag;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.External.Security;
using EdFi.DataManagementService.Core.Paging;
using EdFi.DataManagementService.Core.Profile;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Backend;

internal sealed record DescriptorQueryRowsPage(long? TotalCount, IReadOnlyList<DescriptorReadRow> Rows);

internal sealed record DescriptorQueryCandidatePage(
    long? TotalCount,
    IReadOnlyList<DescriptorReadCandidateRow> Rows
);

internal sealed class DescriptorReadHandler(
    IRelationalCommandExecutor commandExecutor,
    IReadableProfileProjector readableProfileProjector,
    IServedEtagComposer servedEtagComposer,
    ILogger<DescriptorReadHandler> logger,
    IDocumentCacheReadAccelerationCoordinator readAccelerationCoordinator,
    ICustomViewAuthorizationExecutor customViewAuthorizationExecutor,
    ChangeQueryPageOrderingPolicy? orderingPolicy = null
) : IDescriptorReadHandler
{
    private const string DocumentUuidParameterName = "@documentUuid";
    private const string ResourceKeyIdParameterName = "@resourceKeyId";
    private const string SelectedDocumentIdParameterPrefix = "@selectedDocumentId";

    private enum DescriptorRowProjection
    {
        CandidateMetadata,
        FullRow,
    }

    // The descriptor page query binds a single ResourceKeyId discriminator parameter on top of the paging
    // parameters; see DescriptorQueryPageKeysetPlanner. Counted into the non-authorization parameter budget.
    private const int DescriptorQueryResourceKeyParameterCount = 1;

    // The boundary statement binds the unpaged candidate mode's own parameters where a page binds its
    // paging ones. Derived from the mode rather than written as a literal, so the budget cannot drift
    // from what the statement actually emits.
    private static readonly int _descriptorPartitionParameterCount = PageCandidateModeParameters
        .For(PageCandidateModePlanning.UnpagedCandidatesMode)
        .Count;
    private readonly IRelationalCommandExecutor _commandExecutor =
        commandExecutor ?? throw new ArgumentNullException(nameof(commandExecutor));
    private readonly IReadableProfileProjector _readableProfileProjector =
        readableProfileProjector ?? throw new ArgumentNullException(nameof(readableProfileProjector));
    private readonly IServedEtagComposer _servedEtagComposer =
        servedEtagComposer ?? throw new ArgumentNullException(nameof(servedEtagComposer));
    private readonly ILogger<DescriptorReadHandler> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly ICustomViewAuthorizationExecutor _customViewAuthorizationExecutor =
        customViewAuthorizationExecutor
        ?? throw new ArgumentNullException(nameof(customViewAuthorizationExecutor));
    private readonly ChangeQueryPageOrderingPolicy _orderingPolicy =
        orderingPolicy ?? ChangeQueryPageOrderingPolicy.Default;
    private readonly IDocumentCacheReadAccelerationCoordinator _readAccelerationCoordinator =
        readAccelerationCoordinator ?? throw new ArgumentNullException(nameof(readAccelerationCoordinator));

    private abstract record DescriptorGetByIdReadResult<TRow>
        where TRow : class, IDescriptorReadCandidateMetadata
    {
        private DescriptorGetByIdReadResult() { }

        public sealed record Complete(GetResult Result) : DescriptorGetByIdReadResult<TRow>;

        public sealed record AuthorizedRow(TRow Row) : DescriptorGetByIdReadResult<TRow>;
    }

    private abstract record DescriptorQueryNoCacheReadResult
    {
        private DescriptorQueryNoCacheReadResult() { }

        public sealed record Complete(QueryResult Result) : DescriptorQueryNoCacheReadResult;

        public sealed record RowsPage(DescriptorQueryRowsPage Page) : DescriptorQueryNoCacheReadResult;
    }

    private abstract record DescriptorQueryCandidateSelectionReadResult
    {
        private DescriptorQueryCandidateSelectionReadResult() { }

        public sealed record Complete(QueryResult Result) : DescriptorQueryCandidateSelectionReadResult;

        public sealed record CandidatePage(
            DescriptorQueryCandidatePage Page,
            IReadOnlyList<PageDocumentIdAuthorizationCustomViewCheck>? CustomViewChecks
        ) : DescriptorQueryCandidateSelectionReadResult;
    }

    private sealed record DescriptorQueryPreparation(
        PageDocumentIdAuthorizationSpec? AuthorizationSpec,
        PageKeysetSpec.Query PlannedQuery
    );

    private abstract record DescriptorQueryPreparationResult
    {
        private DescriptorQueryPreparationResult() { }

        public sealed record Complete(QueryResult Result) : DescriptorQueryPreparationResult;

        public sealed record Prepared(DescriptorQueryPreparation Preparation)
            : DescriptorQueryPreparationResult;
    }

    private sealed record DescriptorPartitionPreparation(
        PageDocumentIdAuthorizationSpec? AuthorizationSpec,
        PartitionWindowPlan PartitionPlan
    );

    private abstract record DescriptorPartitionPreparationResult
    {
        private DescriptorPartitionPreparationResult() { }

        public sealed record Complete(PartitionResult Result) : DescriptorPartitionPreparationResult;

        public sealed record Prepared(DescriptorPartitionPreparation Preparation)
            : DescriptorPartitionPreparationResult;
    }

    public async Task<GetResult> HandleGetByIdAsync(
        DescriptorGetByIdRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        _logger.LogDebug(
            "Descriptor GET-by-id routed to descriptor read handler for {Resource} - {TraceId}",
            RelationalWriteSupport.FormatResource(request.Resource),
            request.TraceId.Value
        );

        if (request.ReadMode != RelationalGetRequestReadMode.ExternalResponse)
        {
            return await HandleGetByIdNoCacheResultAsync(request, cancellationToken).ConfigureAwait(false);
        }

        return await _readAccelerationCoordinator
            .GetByIdAsync(
                new DocumentCacheReadAccelerationGetByIdRequest(
                    request.TenantKey,
                    request.MappingSet,
                    request.Resource,
                    request.DocumentUuid,
                    DocumentCacheReadAccelerationResourceKind.Descriptor,
                    fallbackCancellationToken =>
                        HandleGetByIdNoCacheResultAsync(request, fallbackCancellationToken),
                    selectionCancellationToken =>
                        SelectGetByIdReadAccelerationCandidateAsync(request, selectionCancellationToken)
                )
                {
                    ReadableProfileProjectionContext = request.ReadableProfileProjectionContext,
                    ResponseContentCoding = request.ResponseContentCoding,
                },
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    private async Task<GetResult> HandleGetByIdNoCacheResultAsync(
        DescriptorGetByIdRequest request,
        CancellationToken cancellationToken
    )
    {
        DescriptorGetByIdReadResult<DescriptorReadRow> noCacheReadResult = await ReadGetByIdAsync(
                request,
                DescriptorRowProjection.FullRow,
                DescriptorReadRowReader.ReadSingleOrDefaultAsync,
                cancellationToken
            )
            .ConfigureAwait(false);

        return noCacheReadResult switch
        {
            DescriptorGetByIdReadResult<DescriptorReadRow>.Complete complete => complete.Result,
            DescriptorGetByIdReadResult<DescriptorReadRow>.AuthorizedRow authorizedRow =>
                MaterializeDescriptorGetSuccess(request, authorizedRow.Row),
            _ => throw new InvalidOperationException(
                $"Unsupported descriptor GET no-cache read result '{noCacheReadResult.GetType().Name}'."
            ),
        };
    }

    private async Task<DocumentCacheReadAccelerationGetByIdSelectionResult> SelectGetByIdReadAccelerationCandidateAsync(
        DescriptorGetByIdRequest request,
        CancellationToken cancellationToken
    )
    {
        DescriptorGetByIdReadResult<DescriptorReadCandidateRow> candidateReadResult = await ReadGetByIdAsync(
                request,
                DescriptorRowProjection.CandidateMetadata,
                DescriptorReadRowReader.ReadSingleCandidateOrDefaultAsync,
                cancellationToken
            )
            .ConfigureAwait(false);

        if (candidateReadResult is DescriptorGetByIdReadResult<DescriptorReadCandidateRow>.Complete complete)
        {
            return new DocumentCacheReadAccelerationGetByIdSelectionResult.Complete(complete.Result);
        }

        var authorizedRow =
            (DescriptorGetByIdReadResult<DescriptorReadCandidateRow>.AuthorizedRow)candidateReadResult;

        return new DocumentCacheReadAccelerationGetByIdSelectionResult.Candidate(
            CreateDescriptorReadAccelerationCandidate(authorizedRow.Row),
            fallbackCancellationToken => HandleGetByIdNoCacheResultAsync(request, fallbackCancellationToken)
        );
    }

    private async Task<DescriptorGetByIdReadResult<TRow>> ReadGetByIdAsync<TRow>(
        DescriptorGetByIdRequest request,
        DescriptorRowProjection projection,
        Func<IRelationalCommandReader, CancellationToken, Task<TRow?>> readSingleOrDefaultAsync,
        CancellationToken cancellationToken
    )
        where TRow : class, IDescriptorReadCandidateMetadata
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(readSingleOrDefaultAsync);
        cancellationToken.ThrowIfCancellationRequested();

        var authorizationResult = await ResolveGetByIdAuthorizationPreflightAsync(request, cancellationToken)
            .ConfigureAwait(false);

        if (authorizationResult.CompleteResult is not null)
        {
            return new DescriptorGetByIdReadResult<TRow>.Complete(authorizationResult.CompleteResult);
        }

        RelationalCommand command;

        try
        {
            command = BuildGetByIdCommand(
                request.MappingSet.Key.Dialect,
                request.DocumentUuid,
                RelationalWriteSupport.GetResourceKeyIdOrThrow(request.MappingSet, request.Resource),
                projection
            );
        }
        catch (NotSupportedException ex)
        {
            return new DescriptorGetByIdReadResult<TRow>.Complete(new GetResult.UnknownFailure(ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            return new DescriptorGetByIdReadResult<TRow>.Complete(new GetResult.UnknownFailure(ex.Message));
        }
        catch (KeyNotFoundException ex)
        {
            return new DescriptorGetByIdReadResult<TRow>.Complete(new GetResult.UnknownFailure(ex.Message));
        }

        TRow? descriptorRow;

        try
        {
            descriptorRow = await _commandExecutor
                .ExecuteReaderAsync(command, readSingleOrDefaultAsync, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (DescriptorReadInvariantException ex)
        {
            return new DescriptorGetByIdReadResult<TRow>.Complete(new GetResult.UnknownFailure(ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            return new DescriptorGetByIdReadResult<TRow>.Complete(new GetResult.UnknownFailure(ex.Message));
        }

        if (descriptorRow is null)
        {
            return new DescriptorGetByIdReadResult<TRow>.Complete(new GetResult.GetFailureNotExists());
        }

        // Custom views and NamespaceBased are AND filters executing in CMS-configured order, and the first
        // failure is the one reported. The namespace check is in memory and the custom-view checks are their
        // own query, so the order is sequenced here rather than by statement position.
        var (customViewsBeforeNamespace, customViewsAfterNamespace) =
            CustomViewAuthorizationCheckSplitter.PartitionByConfiguredIndex(
                authorizationResult.CustomViewChecks,
                authorizationResult.Proceed?.NamespaceChecks.Count > 0
                    ? authorizationResult.Proceed.NamespaceChecks[0].RawConfiguredIndex
                    : int.MaxValue
            );

        if (
            await ExecuteGetByIdCustomViewsAsync(
                    request,
                    descriptorRow.DocumentId,
                    customViewsBeforeNamespace,
                    authorizationResult.CustomViewChecks,
                    cancellationToken
                )
                .ConfigureAwait(false) is
            { } beforeNamespaceDenial
        )
        {
            return new DescriptorGetByIdReadResult<TRow>.Complete(beforeNamespaceDenial);
        }

        if (
            ValidateGetByIdDescriptorCandidate(
                descriptorRow,
                authorizationResult.NamespacePrefixParameterization
            ) is
            { } terminal
        )
        {
            return new DescriptorGetByIdReadResult<TRow>.Complete(terminal);
        }

        if (
            await ExecuteGetByIdCustomViewsAsync(
                    request,
                    descriptorRow.DocumentId,
                    customViewsAfterNamespace,
                    authorizationResult.CustomViewChecks,
                    cancellationToken
                )
                .ConfigureAwait(false) is
            { } afterNamespaceDenial
        )
        {
            return new DescriptorGetByIdReadResult<TRow>.Complete(afterNamespaceDenial);
        }

        LogDiscriminatorMismatchIfPresent(request, descriptorRow);

        return new DescriptorGetByIdReadResult<TRow>.AuthorizedRow(descriptorRow);
    }

    private sealed record DescriptorGetByIdAuthorizationResult(
        GetResult? CompleteResult,
        NamespacePrefixParameterization? NamespacePrefixParameterization,
        DescriptorReadAuthorizationPreflightOutcome.Proceed? Proceed,
        IReadOnlyList<SingleRecordCustomViewAuthorizationCheckSpec> CustomViewChecks
    );

    private async Task<DescriptorGetByIdAuthorizationResult> ResolveGetByIdAuthorizationPreflightAsync(
        DescriptorGetByIdRequest request,
        CancellationToken cancellationToken
    )
    {
        // StoredDocument reads are internal read-modify-write fetches that bypass per-record
        // authorization exactly as the generic single-record path does: the caller was already
        // authorized for the operation that triggered the fetch. Only ExternalResponse reads run the
        // namespace authorization preflight and the in-memory stored-namespace check below.
        if (request.ReadMode == RelationalGetRequestReadMode.StoredDocument)
        {
            return new DescriptorGetByIdAuthorizationResult(null, null, null, []);
        }

        // Namespace planner terminals (no usable root column, no prefixes, MSSQL prefix cap) and
        // unsupported strategies resolve before any SQL roundtrip. The stored namespace check itself
        // runs in memory against the namespace value materialized by the existing single SELECT.
        var authorizationPreflight = ResolveDescriptorReadAuthorization(
            request.MappingSet,
            request.Resource,
            request.AuthorizationStrategyEvaluators,
            request.RelationalAuthorizationContext,
            NamespaceAuthorizationOperation.ReadSingle,
            "descriptor GET",
            "GET"
        );

        switch (authorizationPreflight)
        {
            case DescriptorReadAuthorizationPreflightOutcome.NotImplemented notImplemented:
                // Custom views configured ahead of this terminal execute first, so a missing or
                // non-conforming view keeps its own 500 rather than being hidden by the terminal.
                await ValidateGetByIdCustomViewsAsync(
                        request,
                        notImplemented.CustomViewChecks,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                return new DescriptorGetByIdAuthorizationResult(
                    new GetResult.GetFailureNotImplemented(notImplemented.FailureMessage),
                    null,
                    null,
                    []
                );
            case DescriptorReadAuthorizationPreflightOutcome.SecurityConfigurationError configError:
                await ValidateGetByIdCustomViewsAsync(
                        request,
                        configError.CustomViewChecks,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                return new DescriptorGetByIdAuthorizationResult(
                    new GetResult.GetFailureSecurityConfiguration(
                        configError.Errors,
                        configError.Diagnostics
                    ),
                    null,
                    null,
                    []
                );
            case DescriptorReadAuthorizationPreflightOutcome.NamespaceNotAuthorized namespaceNotAuthorized:
                await ValidateGetByIdCustomViewsAsync(
                        request,
                        namespaceNotAuthorized.CustomViewChecks,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                return new DescriptorGetByIdAuthorizationResult(
                    new GetResult.GetFailureNamespaceNotAuthorized(namespaceNotAuthorized.Failure),
                    null,
                    null,
                    []
                );
            case DescriptorReadAuthorizationPreflightOutcome.Proceed proceed:
                if (
                    !TryPlanGetByIdCustomViews(
                        request,
                        proceed.CustomViewStrategies,
                        out var customViewChecks,
                        out var customViewPlanFailure,
                        out var customViewChecksBeforePlanFailure
                    )
                )
                {
                    await ValidateSingleRecordGetByIdCustomViewsAsync(
                            request,
                            customViewChecksBeforePlanFailure,
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                    return new DescriptorGetByIdAuthorizationResult(customViewPlanFailure!, null, null, []);
                }

                return new DescriptorGetByIdAuthorizationResult(
                    null,
                    proceed.NamespacePrefixParameterization,
                    proceed,
                    customViewChecks
                );
            default:
                throw new InvalidOperationException(
                    $"Unsupported descriptor GET authorization preflight outcome '{authorizationPreflight.GetType().Name}'."
                );
        }
    }

    private static GetResult? ValidateGetByIdDescriptorCandidate(
        IDescriptorReadCandidateMetadata descriptorRow,
        NamespacePrefixParameterization? namespacePrefixParameterization
    )
    {
        if (namespacePrefixParameterization is not null)
        {
            var namespaceFailure = EvaluateStoredNamespace(
                descriptorRow.Namespace,
                namespacePrefixParameterization
            );

            return namespaceFailure is null
                ? null
                : new GetResult.GetFailureNamespaceNotAuthorized(namespaceFailure);
        }

        if (!string.IsNullOrEmpty(descriptorRow.Namespace))
        {
            return null;
        }

        // Without namespace authorization configured, the stored-namespace-uninitialized 403
        // path does not apply, so a null stored Namespace is genuine descriptor row corruption.
        // Surface it as an UnknownFailure with the same column-naming diagnostic the row
        // reader produces for the other required descriptor columns.
        return new GetResult.UnknownFailure(
            $"Descriptor read corruption detected for DocumentId {descriptorRow.DocumentId} "
                + $"(ResourceKeyId={descriptorRow.ResourceKeyId}): dms.Descriptor.Namespace must not be null."
        );
    }

    public async Task<QueryResult> HandleQueryAsync(
        DescriptorQueryRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        _logger.LogDebug(
            "Descriptor query routed to descriptor read handler for {Resource} - {TraceId}",
            RelationalWriteSupport.FormatResource(request.Resource),
            request.TraceId.Value
        );

        // Cursor pages take the uncached path rather than read acceleration. A cursor walk depends on
        // every page reporting the selected-keyset boundary its successor resumes from, and only
        // traditional paging is exercised against the read-acceleration path, so cursor selection keeps
        // to the path whose boundary reporting is covered end to end.
        if (request.Paging is CollectionPaging.Cursor)
        {
            return await HandleQueryNoCacheResultAsync(request, cancellationToken).ConfigureAwait(false);
        }

        return await _readAccelerationCoordinator
            .QueryAsync(
                new DocumentCacheReadAccelerationQueryRequest(
                    request.TenantKey,
                    request.MappingSet,
                    request.Resource,
                    DocumentCacheReadAccelerationResourceKind.Descriptor,
                    fallbackCancellationToken =>
                        HandleQueryNoCacheResultAsync(request, fallbackCancellationToken),
                    selectionCancellationToken =>
                        SelectQueryReadAccelerationCandidatePageAsync(request, selectionCancellationToken)
                )
                {
                    ReadableProfileProjectionContext = request.ReadableProfileProjectionContext,
                    ResponseContentCoding = request.ResponseContentCoding,
                },
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    private async Task<QueryResult> HandleQueryNoCacheResultAsync(
        DescriptorQueryRequest request,
        CancellationToken cancellationToken
    )
    {
        DescriptorQueryNoCacheReadResult noCacheReadResult = await ReadQueryNoCacheAsync(
                request,
                cancellationToken
            )
            .ConfigureAwait(false);

        return noCacheReadResult switch
        {
            DescriptorQueryNoCacheReadResult.Complete complete => complete.Result,
            DescriptorQueryNoCacheReadResult.RowsPage rowsPage => MaterializeDescriptorQuerySuccess(
                request,
                rowsPage.Page
            ),
            _ => throw new InvalidOperationException(
                $"Unsupported descriptor query no-cache read result '{noCacheReadResult.GetType().Name}'."
            ),
        };
    }

    private async Task<DocumentCacheReadAccelerationQuerySelectionResult> SelectQueryReadAccelerationCandidatePageAsync(
        DescriptorQueryRequest request,
        CancellationToken cancellationToken
    )
    {
        DescriptorQueryCandidateSelectionReadResult candidateReadResult = await ReadQueryCandidatePageAsync(
                request,
                cancellationToken
            )
            .ConfigureAwait(false);

        if (candidateReadResult is DescriptorQueryCandidateSelectionReadResult.Complete complete)
        {
            return new DocumentCacheReadAccelerationQuerySelectionResult.Complete(complete.Result);
        }

        var rowsPage = (DescriptorQueryCandidateSelectionReadResult.CandidatePage)candidateReadResult;

        // Resolved ahead of the empty check so both branches report the same continuation facts. A
        // selection that returned nothing is still a selection that ran under an ordering, and a
        // traditional page over a max-bearing change-version window cannot anchor a DocumentId
        // continuation whether or not it selected rows. Deriving it in only the non-empty branch would
        // leave the empty one on the permissive default and answer the same request differently from
        // the regular-resource path.
        var continuationBoundary = PageContinuationBoundary.For(
            request.Paging,
            _orderingPolicy.ResolveForLiveQuery(request.ChangeVersionRange),
            SelectedMaximumOf(rowsPage.Page.Rows)
        );

        if (rowsPage.Page.Rows.Count == 0)
        {
            QueryResult.QuerySuccess relationalSuccess = new(
                [],
                request.Paging.IncludesTotalCount
                    ? RelationalReadGuardrails.ConvertTotalCountOrThrow(
                        request.Resource,
                        rowsPage.Page.TotalCount,
                        "descriptor query"
                    )
                    : null,
                continuationBoundary.SelectedMaximum
            )
            {
                AllowsDocumentIdContinuation = continuationBoundary.AllowsDocumentIdContinuation,
            };

            return new DocumentCacheReadAccelerationQuerySelectionResult.Complete(relationalSuccess);
        }

        var candidatePage = CreateDescriptorReadAccelerationCandidatePage(
            rowsPage.Page,
            continuationBoundary,
            request.Paging.IncludesTotalCount
        );

        return new DocumentCacheReadAccelerationQuerySelectionResult.CandidatePage(
            candidatePage,
            fallbackCancellationToken =>
                HydrateSelectedDescriptorQueryCandidatePageAsync(
                    request,
                    rowsPage.Page,
                    candidatePage,
                    rowsPage.CustomViewChecks,
                    fallbackCancellationToken
                )
        );
    }

    private async Task<DescriptorQueryCandidateSelectionReadResult> ReadQueryCandidatePageAsync(
        DescriptorQueryRequest request,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        DescriptorQueryPreparationResult preparationResult = await PrepareDescriptorQueryAsync(
                request,
                cancellationToken
            )
            .ConfigureAwait(false);

        if (preparationResult is DescriptorQueryPreparationResult.Complete complete)
        {
            return new DescriptorQueryCandidateSelectionReadResult.Complete(complete.Result);
        }

        var preparation = ((DescriptorQueryPreparationResult.Prepared)preparationResult).Preparation;

        DescriptorQueryCandidatePage candidatePage;

        try
        {
            candidatePage = await ReadQueryCandidateRowsAsync(
                    request,
                    preparation.PlannedQuery,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (DbException ex) when (preparation.AuthorizationSpec?.CustomViewChecks is { Count: > 0 })
        {
            throw new CustomViewAuthorizationValidationException(ex);
        }
        catch (NotSupportedException ex)
        {
            return new DescriptorQueryCandidateSelectionReadResult.Complete(
                new QueryResult.UnknownFailure(ex.Message)
            );
        }
        catch (DescriptorReadInvariantException ex)
        {
            return new DescriptorQueryCandidateSelectionReadResult.Complete(
                new QueryResult.UnknownFailure(ex.Message)
            );
        }
        catch (InvalidOperationException ex)
        {
            return new DescriptorQueryCandidateSelectionReadResult.Complete(
                new QueryResult.UnknownFailure(ex.Message)
            );
        }
        catch (ArgumentException ex)
        {
            return new DescriptorQueryCandidateSelectionReadResult.Complete(
                new QueryResult.UnknownFailure(ex.Message)
            );
        }
        catch (KeyNotFoundException ex)
        {
            return new DescriptorQueryCandidateSelectionReadResult.Complete(
                new QueryResult.UnknownFailure(ex.Message)
            );
        }

        return new DescriptorQueryCandidateSelectionReadResult.CandidatePage(
            candidatePage,
            preparation.AuthorizationSpec?.CustomViewChecks
        );
    }

    private async Task<QueryResult> HydrateSelectedDescriptorQueryCandidatePageAsync(
        DescriptorQueryRequest request,
        DescriptorQueryCandidatePage selectedRowsPage,
        DocumentCacheReadAccelerationCandidatePage candidatePage,
        IReadOnlyList<PageDocumentIdAuthorizationCustomViewCheck>? customViewChecks,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(candidatePage);
        cancellationToken.ThrowIfCancellationRequested();

        var selectedDocumentIds = candidatePage
            .Candidates.Select(static candidate => candidate.DocumentId)
            .ToArray();

        if (selectedDocumentIds.Length == 0)
        {
            return MaterializeDescriptorQuerySuccess(
                request,
                new DescriptorQueryRowsPage(candidatePage.TotalCount, [])
            );
        }

        RelationalCommand command;

        try
        {
            command = BuildSelectedQueryRowsCommand(request.MappingSet.Key.Dialect, selectedDocumentIds);
        }
        catch (NotSupportedException ex)
        {
            return new QueryResult.UnknownFailure(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return new QueryResult.UnknownFailure(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return new QueryResult.UnknownFailure(ex.Message);
        }

        IReadOnlyList<DescriptorReadRow> descriptorRows;

        try
        {
            descriptorRows = await _commandExecutor
                .ExecuteReaderAsync(command, DescriptorReadRowReader.ReadAllAsync, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (DbException ex) when (customViewChecks is { Count: > 0 })
        {
            throw new CustomViewAuthorizationValidationException(ex);
        }
        catch (DescriptorReadInvariantException ex)
        {
            return new QueryResult.UnknownFailure(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return new QueryResult.UnknownFailure(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return new QueryResult.UnknownFailure(ex.Message);
        }

        Dictionary<long, DescriptorReadRow> descriptorRowsByDocumentId = [];

        foreach (DescriptorReadRow row in descriptorRows)
        {
            if (!descriptorRowsByDocumentId.TryAdd(row.DocumentId, row))
            {
                return await HandleQueryNoCacheResultAsync(request, cancellationToken).ConfigureAwait(false);
            }
        }

        List<DescriptorReadRow> orderedRows = new(selectedDocumentIds.Length);

        foreach (long documentId in selectedDocumentIds)
        {
            if (descriptorRowsByDocumentId.TryGetValue(documentId, out var row))
            {
                orderedRows.Add(row);
            }
        }

        if (!SelectedDescriptorQueryRowsStillMatch(selectedRowsPage.Rows, orderedRows))
        {
            return await HandleQueryNoCacheResultAsync(request, cancellationToken).ConfigureAwait(false);
        }

        return MaterializeDescriptorQuerySuccess(
            request,
            new DescriptorQueryRowsPage(candidatePage.TotalCount, orderedRows)
        );
    }

    private static bool SelectedDescriptorQueryRowsStillMatch(
        IReadOnlyList<DescriptorReadCandidateRow> selectedRows,
        IReadOnlyList<DescriptorReadRow> hydratedRows
    )
    {
        if (selectedRows.Count != hydratedRows.Count)
        {
            return false;
        }

        Dictionary<long, DescriptorReadRow> hydratedRowsByDocumentId = [];

        foreach (DescriptorReadRow row in hydratedRows)
        {
            if (!hydratedRowsByDocumentId.TryAdd(row.DocumentId, row))
            {
                return false;
            }
        }

        foreach (DescriptorReadCandidateRow selectedRow in selectedRows)
        {
            if (!hydratedRowsByDocumentId.TryGetValue(selectedRow.DocumentId, out var hydratedRow))
            {
                return false;
            }

            if (
                hydratedRow.DocumentUuid != selectedRow.DocumentUuid
                || hydratedRow.ResourceKeyId != selectedRow.ResourceKeyId
                || hydratedRow.ContentVersion != selectedRow.ContentVersion
                || hydratedRow.ContentLastModifiedAt != selectedRow.ContentLastModifiedAt
                || hydratedRow.Namespace != selectedRow.Namespace
                || hydratedRow.CodeValue != selectedRow.CodeValue
                || hydratedRow.Discriminator != selectedRow.Discriminator
            )
            {
                return false;
            }
        }

        return true;
    }

    private async Task<DescriptorQueryNoCacheReadResult> ReadQueryNoCacheAsync(
        DescriptorQueryRequest request,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        DescriptorQueryPreparationResult preparationResult = await PrepareDescriptorQueryAsync(
                request,
                cancellationToken
            )
            .ConfigureAwait(false);

        if (preparationResult is DescriptorQueryPreparationResult.Complete complete)
        {
            return new DescriptorQueryNoCacheReadResult.Complete(complete.Result);
        }

        var preparation = ((DescriptorQueryPreparationResult.Prepared)preparationResult).Preparation;

        DescriptorQueryRowsPage queryRowsPage;

        try
        {
            queryRowsPage = await ReadQueryRowsAsync(request, preparation.PlannedQuery, cancellationToken)
                .ConfigureAwait(false);
        }
        // Trade-off: a provider error raised while executing a custom-view page query is intentionally
        // relabeled as a custom-view validation failure, even though not every such error originates in
        // the view. Validation above already proved the views resolve, so the alternative is letting the
        // DbException escape into the non-ProblemDetails unhandled path and lose the public
        // urn:ed-fi:api:system contract this failure is documented to carry.
        catch (DbException ex) when (preparation.AuthorizationSpec?.CustomViewChecks is { Count: > 0 })
        {
            throw new CustomViewAuthorizationValidationException(ex);
        }
        catch (NotSupportedException ex)
        {
            return new DescriptorQueryNoCacheReadResult.Complete(new QueryResult.UnknownFailure(ex.Message));
        }
        catch (DescriptorReadInvariantException ex)
        {
            return new DescriptorQueryNoCacheReadResult.Complete(new QueryResult.UnknownFailure(ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            return new DescriptorQueryNoCacheReadResult.Complete(new QueryResult.UnknownFailure(ex.Message));
        }
        catch (ArgumentException ex)
        {
            return new DescriptorQueryNoCacheReadResult.Complete(new QueryResult.UnknownFailure(ex.Message));
        }
        catch (KeyNotFoundException ex)
        {
            return new DescriptorQueryNoCacheReadResult.Complete(new QueryResult.UnknownFailure(ex.Message));
        }

        return new DescriptorQueryNoCacheReadResult.RowsPage(queryRowsPage);
    }

    private async Task<DescriptorQueryPreparationResult> PrepareDescriptorQueryAsync(
        DescriptorQueryRequest request,
        CancellationToken cancellationToken
    )
    {
        var authorizationPreflight = ResolveDescriptorReadAuthorization(
            request.MappingSet,
            request.Resource,
            request.AuthorizationStrategyEvaluators,
            request.RelationalAuthorizationContext,
            NamespaceAuthorizationOperation.ReadMany,
            "descriptor query",
            "GET-many"
        );

        // Each terminal validates the custom views configured ahead of it. An empty list is a no-op.
        switch (authorizationPreflight)
        {
            case DescriptorReadAuthorizationPreflightOutcome.NotImplemented notImplemented:
                await ValidateCustomViewsAsync(request, notImplemented.CustomViewChecks, cancellationToken)
                    .ConfigureAwait(false);
                return new DescriptorQueryPreparationResult.Complete(
                    new QueryResult.QueryFailureNotImplemented(notImplemented.FailureMessage)
                );
            case DescriptorReadAuthorizationPreflightOutcome.SecurityConfigurationError configError:
                await ValidateCustomViewsAsync(request, configError.CustomViewChecks, cancellationToken)
                    .ConfigureAwait(false);
                return new DescriptorQueryPreparationResult.Complete(
                    new QueryResult.QueryFailureSecurityConfiguration(
                        configError.Errors,
                        configError.Diagnostics
                    )
                );
            case DescriptorReadAuthorizationPreflightOutcome.NamespaceNotAuthorized namespaceNotAuthorized:
                await ValidateCustomViewsAsync(
                        request,
                        namespaceNotAuthorized.CustomViewChecks,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                return new DescriptorQueryPreparationResult.Complete(
                    new QueryResult.QueryFailureNamespaceNotAuthorized(namespaceNotAuthorized.Failure)
                );
        }

        var proceed = (DescriptorReadAuthorizationPreflightOutcome.Proceed)authorizationPreflight;

        // The descriptor page subquery roots on dms.Descriptor, which carries both the DocumentId keyset
        // and the Namespace column, so the namespace and custom-view checks bind directly to the root
        // alias. The planner consumes the orchestrator's authorization checks through
        // PageDocumentIdAuthorizationSpec.
        var authorizationSpec = BuildDescriptorQueryAuthorizationSpec(proceed);

        DescriptorQueryPreprocessingResult preprocessingResult;

        try
        {
            preprocessingResult = DescriptorQueryRequestPreprocessor.Preprocess(
                request.MappingSet,
                request.Resource,
                request.QueryElements
            );
        }
        catch (NotSupportedException ex)
        {
            return new DescriptorQueryPreparationResult.Complete(
                new QueryResult.QueryFailureNotImplemented(ex.Message)
            );
        }
        catch (InvalidOperationException ex)
        {
            return new DescriptorQueryPreparationResult.Complete(new QueryResult.UnknownFailure(ex.Message));
        }
        catch (KeyNotFoundException ex)
        {
            return new DescriptorQueryPreparationResult.Complete(new QueryResult.UnknownFailure(ex.Message));
        }

        if (preprocessingResult.Outcome is RelationalQueryPreprocessingOutcome.EmptyPage)
        {
            await ValidateCustomViewsAsync(request, authorizationSpec?.CustomViewChecks, cancellationToken)
                .ConfigureAwait(false);

            return new DescriptorQueryPreparationResult.Complete(
                new QueryResult.QuerySuccess([], request.Paging.IncludesTotalCount ? 0 : null)
                {
                    SelectionSkipped = true,
                }
            );
        }

        // Descriptor queries still compose the namespace authorization state with the query filter,
        // paging, ResourceKeyId, and change-version parameters. Fail closed if that exceeds SQL Server's
        // per-command parameter ceiling rather than letting the query fail at execution.
        await ValidateCustomViewsAsync(request, authorizationSpec?.CustomViewChecks, cancellationToken)
            .ConfigureAwait(false);

        if (
            BuildDescriptorQueryParameterBudgetFailure(
                request.MappingSet.Key.Dialect,
                request.Resource,
                proceed.NamespacePrefixParameterization,
                preprocessingResult.QueryElementsInOrder.Count,
                CountPagingParameters(request),
                CountChangeVersionParameters(request.ChangeVersionRange)
            ) is
            { } parameterBudgetFailure
        )
        {
            return new DescriptorQueryPreparationResult.Complete(parameterBudgetFailure);
        }

        PageKeysetSpec.Query plannedQuery;

        try
        {
            plannedQuery = PlanDescriptorQuery(request, preprocessingResult, authorizationSpec);
        }
        catch (NotSupportedException ex)
        {
            return new DescriptorQueryPreparationResult.Complete(new QueryResult.UnknownFailure(ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            return new DescriptorQueryPreparationResult.Complete(new QueryResult.UnknownFailure(ex.Message));
        }
        catch (ArgumentException ex)
        {
            return new DescriptorQueryPreparationResult.Complete(new QueryResult.UnknownFailure(ex.Message));
        }
        catch (KeyNotFoundException ex)
        {
            return new DescriptorQueryPreparationResult.Complete(new QueryResult.UnknownFailure(ex.Message));
        }

        return new DescriptorQueryPreparationResult.Prepared(
            new DescriptorQueryPreparation(authorizationSpec, plannedQuery)
        );
    }

    public async Task<PartitionResult> HandlePartitionsAsync(
        DescriptorPartitionRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        _logger.LogDebug(
            "Descriptor partition boundaries routed to descriptor read handler for {Resource} - {TraceId}",
            RelationalWriteSupport.FormatResource(request.Resource),
            LoggingSanitizer.SanitizeForLogging(request.TraceId.Value)
        );

        // No read-acceleration path: the cache holds hydrated documents and the candidate pages that
        // selected them, and a boundary calculation ranges over the whole authorized candidate relation.
        DescriptorPartitionPreparationResult preparationResult = await PrepareDescriptorPartitionAsync(
                request,
                cancellationToken
            )
            .ConfigureAwait(false);

        if (preparationResult is DescriptorPartitionPreparationResult.Complete complete)
        {
            return complete.Result;
        }

        var preparation = ((DescriptorPartitionPreparationResult.Prepared)preparationResult).Preparation;

        IReadOnlyList<long> ascendingStarts;

        try
        {
            ascendingStarts = await PartitionBoundaryCommand
                .ExecuteAsync(
                    _commandExecutor,
                    preparation.PartitionPlan,
                    "Descriptor partition boundary",
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        // Trade-off: a provider error raised while executing a custom-view boundary statement is
        // intentionally relabeled as a custom-view validation failure, even though not every such error
        // originates in the view. This mirrors the page path so the two operations report the same public
        // urn:ed-fi:api:system contract for the same condition.
        catch (DbException ex) when (preparation.AuthorizationSpec?.CustomViewChecks is { Count: > 0 })
        {
            throw new CustomViewAuthorizationValidationException(ex);
        }
        // The same non-provider fault set the page path catches. A condition both operations can reach
        // has to leave the backend as the same kind of result, or one answers with the logged
        // problem+json unknown failure while the other escapes to the generic unhandled 500.
        catch (NotSupportedException ex)
        {
            return new PartitionResult.UnknownPartitionFailure(ex.Message);
        }
        catch (DescriptorReadInvariantException ex)
        {
            return new PartitionResult.UnknownPartitionFailure(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return new PartitionResult.UnknownPartitionFailure(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return new PartitionResult.UnknownPartitionFailure(ex.Message);
        }
        catch (KeyNotFoundException ex)
        {
            return new PartitionResult.UnknownPartitionFailure(ex.Message);
        }

        try
        {
            return new PartitionResult.PartitionSuccess(
                PartitionRangeAssembler.ToInclusiveRanges(ascendingStarts)
            );
        }
        // Non-ascending starts mean the compiled statement changed, not that a client sent something
        // unusual. Reporting it keeps a corrupted boundary set from reaching a client as a walkable one.
        catch (ArgumentException ex)
        {
            return new PartitionResult.UnknownPartitionFailure(ex.Message);
        }
    }

    /// <summary>
    /// Resolves authorization, preprocesses the filter, and compiles the descriptor boundary statement,
    /// or reports the outcome that stops the request.
    /// </summary>
    /// <remarks>
    /// Every seam here is the one <see cref="PrepareDescriptorQueryAsync" /> uses, in the same order, so
    /// a boundary set is calculated over exactly the rows the equivalent GET-many would page: the same
    /// authorization preflight and its custom-view ordering, the same preprocessor, the same
    /// <c>ResourceKeyId</c>-rooted candidate planner, and the same parameter budget.
    /// </remarks>
    private async Task<DescriptorPartitionPreparationResult> PrepareDescriptorPartitionAsync(
        DescriptorPartitionRequest request,
        CancellationToken cancellationToken
    )
    {
        var dialect = request.MappingSet.Key.Dialect;
        var authorizationPreflight = ResolveDescriptorReadAuthorization(
            request.MappingSet,
            request.Resource,
            request.AuthorizationStrategyEvaluators,
            request.RelationalAuthorizationContext,
            NamespaceAuthorizationOperation.ReadMany,
            "descriptor partitions",
            "GET-many"
        );

        // Each terminal validates the custom views configured ahead of it. An empty list is a no-op.
        switch (authorizationPreflight)
        {
            case DescriptorReadAuthorizationPreflightOutcome.NotImplemented notImplemented:
                await ValidateCustomViewsAsync(dialect, notImplemented.CustomViewChecks, cancellationToken)
                    .ConfigureAwait(false);
                return new DescriptorPartitionPreparationResult.Complete(
                    new PartitionResult.PartitionFailureNotImplemented(notImplemented.FailureMessage)
                );
            case DescriptorReadAuthorizationPreflightOutcome.SecurityConfigurationError configError:
                await ValidateCustomViewsAsync(dialect, configError.CustomViewChecks, cancellationToken)
                    .ConfigureAwait(false);
                return new DescriptorPartitionPreparationResult.Complete(
                    new PartitionResult.PartitionFailureSecurityConfiguration(
                        configError.Errors,
                        configError.Diagnostics
                    )
                );
            case DescriptorReadAuthorizationPreflightOutcome.NamespaceNotAuthorized namespaceNotAuthorized:
                await ValidateCustomViewsAsync(
                        dialect,
                        namespaceNotAuthorized.CustomViewChecks,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                return new DescriptorPartitionPreparationResult.Complete(
                    new PartitionResult.PartitionFailureNamespaceNotAuthorized(namespaceNotAuthorized.Failure)
                );
        }

        var proceed = (DescriptorReadAuthorizationPreflightOutcome.Proceed)authorizationPreflight;
        var authorizationSpec = BuildDescriptorQueryAuthorizationSpec(proceed);

        DescriptorQueryPreprocessingResult preprocessingResult;

        try
        {
            preprocessingResult = DescriptorQueryRequestPreprocessor.Preprocess(
                request.MappingSet,
                request.Resource,
                request.QueryElements
            );
        }
        catch (NotSupportedException ex)
        {
            return new DescriptorPartitionPreparationResult.Complete(
                new PartitionResult.PartitionFailureNotImplemented(ex.Message)
            );
        }
        catch (InvalidOperationException ex)
        {
            return new DescriptorPartitionPreparationResult.Complete(
                new PartitionResult.UnknownPartitionFailure(ex.Message)
            );
        }
        catch (KeyNotFoundException ex)
        {
            return new DescriptorPartitionPreparationResult.Complete(
                new PartitionResult.UnknownPartitionFailure(ex.Message)
            );
        }

        if (preprocessingResult.Outcome is RelationalQueryPreprocessingOutcome.EmptyPage)
        {
            await ValidateCustomViewsAsync(dialect, authorizationSpec?.CustomViewChecks, cancellationToken)
                .ConfigureAwait(false);

            return new DescriptorPartitionPreparationResult.Complete(
                new PartitionResult.PartitionSuccess([]) { SelectionSkipped = true }
            );
        }

        await ValidateCustomViewsAsync(dialect, authorizationSpec?.CustomViewChecks, cancellationToken)
            .ConfigureAwait(false);

        if (
            BuildDescriptorQueryParameterBudgetFailure(
                dialect,
                request.Resource,
                proceed.NamespacePrefixParameterization,
                preprocessingResult.QueryElementsInOrder.Count,
                _descriptorPartitionParameterCount,
                CountChangeVersionParameters(request.ChangeVersionRange)
            ) is
            { } parameterBudgetFailure
        )
        {
            return new DescriptorPartitionPreparationResult.Complete(
                RelationalPartitionResultMapping.FromQueryResult(parameterBudgetFailure)
            );
        }

        PartitionWindowPlan partitionPlan;

        try
        {
            var candidatePlan = new DescriptorQueryPageKeysetPlanner(dialect).PlanCandidates(
                request.MappingSet,
                request.Resource,
                preprocessingResult,
                authorizationSpec,
                request.ChangeVersionRange
            );

            partitionPlan = new PartitionWindowPlanner(dialect).Plan(
                candidatePlan,
                request.RequestedPartitionCount,
                request.MinimumPartitionSize
            );
        }
        catch (NotSupportedException ex)
        {
            return new DescriptorPartitionPreparationResult.Complete(
                new PartitionResult.UnknownPartitionFailure(ex.Message)
            );
        }
        catch (InvalidOperationException ex)
        {
            return new DescriptorPartitionPreparationResult.Complete(
                new PartitionResult.UnknownPartitionFailure(ex.Message)
            );
        }
        catch (ArgumentException ex)
        {
            return new DescriptorPartitionPreparationResult.Complete(
                new PartitionResult.UnknownPartitionFailure(ex.Message)
            );
        }
        catch (KeyNotFoundException ex)
        {
            return new DescriptorPartitionPreparationResult.Complete(
                new PartitionResult.UnknownPartitionFailure(ex.Message)
            );
        }

        return new DescriptorPartitionPreparationResult.Prepared(
            new DescriptorPartitionPreparation(authorizationSpec, partitionPlan)
        );
    }

    /// <summary>
    /// The maximum <c>DocumentId</c> among the selected descriptor rows, or <see langword="null"/> when
    /// the page selected none. Taken across every row rather than from the last one: the page query
    /// orders ascending today, but a boundary that depended on that could not survive an ordering
    /// change, and the maximum costs the same either way.
    /// </summary>
    private static long? SelectedMaximumOf(IReadOnlyList<IDescriptorReadCandidateMetadata> descriptorRows) =>
        descriptorRows.Count == 0
            ? null
            : descriptorRows.Max(static descriptorRow => descriptorRow.DocumentId);

    /// <summary>
    /// Plans the single-record custom-view checks descriptor GET-by-id executes, or reports the
    /// security-configuration failure that stops the read.
    /// </summary>
    private static bool TryPlanGetByIdCustomViews(
        DescriptorGetByIdRequest request,
        IReadOnlyList<SupportedCustomViewAuthorizationStrategy> customViewStrategies,
        out IReadOnlyList<SingleRecordCustomViewAuthorizationCheckSpec> customViewChecks,
        out GetResult? securityConfigurationFailure,
        out IReadOnlyList<SingleRecordCustomViewAuthorizationCheckSpec> checksToValidateBeforeFailure
    )
    {
        customViewChecks = [];
        securityConfigurationFailure = null;
        checksToValidateBeforeFailure = [];

        if (customViewStrategies.Count == 0)
        {
            return true;
        }

        var outcome = SingleRecordCustomViewAuthorizationPlanner.Plan(
            request.MappingSet,
            request.MappingSet.GetConcreteResourceModelOrThrow(request.Resource),
            customViewStrategies,
            NamespaceAuthorizationOperation.ReadSingle
        );

        if (
            outcome
            is SingleRecordCustomViewAuthorizationPlanOutcome.SecurityConfiguration configurationFailure
        )
        {
            var failure = BuildDescriptorCustomViewSecurityConfigurationFailure(
                request.Resource,
                configurationFailure.Failures
            );

            securityConfigurationFailure = new GetResult.GetFailureSecurityConfiguration(
                failure.Errors,
                failure.Diagnostics
            );
            // Views configured ahead of the earliest planning failure planned successfully and execute
            // first, so they are still validated before this failure is reported.
            checksToValidateBeforeFailure = SingleRecordChecksBeforeFailure(configurationFailure);
            return false;
        }

        customViewChecks = ((SingleRecordCustomViewAuthorizationPlanOutcome.Plan)outcome).Checks;

        return true;
    }

    /// <summary>
    /// The planned single-record checks configured strictly before the earliest planning failure. Those views
    /// planned successfully and execute first, so they are validated even though a later one cannot plan.
    /// </summary>
    private static IReadOnlyList<SingleRecordCustomViewAuthorizationCheckSpec> SingleRecordChecksBeforeFailure(
        SingleRecordCustomViewAuthorizationPlanOutcome.SecurityConfiguration configurationFailure
    )
    {
        var earliestFailureIndex = RelationalAuthorizationPlanner.EarliestSecurityConfigurationFailureIndex(
            configurationFailure.Failures
        );

        return
        [
            .. configurationFailure.PlannedChecks.Where(check =>
                check.ConfiguredStrategy.RawConfiguredIndex < earliestFailureIndex
            ),
        ];
    }

    /// <summary>
    /// Validates single-record checks that execute ahead of a GET-by-id planning failure. These are
    /// single-record specs, so they take the single-record validator rather than the page-query one.
    /// </summary>
    private Task ValidateSingleRecordGetByIdCustomViewsAsync(
        DescriptorGetByIdRequest request,
        IReadOnlyList<SingleRecordCustomViewAuthorizationCheckSpec> checks,
        CancellationToken cancellationToken
    ) =>
        CustomViewAuthorizationValidator.ValidateSingleRecordAsync(
            _commandExecutor,
            request.MappingSet.Key.Dialect,
            checks,
            cancellationToken
        );

    /// <summary>
    /// Validates the views that execute ahead of a GET-by-id terminal. A null or empty list is a no-op, so
    /// every terminal can call this unconditionally.
    /// </summary>
    private Task ValidateGetByIdCustomViewsAsync(
        DescriptorGetByIdRequest request,
        IReadOnlyList<PageDocumentIdAuthorizationCustomViewCheck>? customViewChecks,
        CancellationToken cancellationToken
    ) =>
        CustomViewAuthorizationValidator.ValidateAsync(
            _commandExecutor,
            request.MappingSet.Key.Dialect,
            customViewChecks,
            cancellationToken
        );

    /// <summary>
    /// Runs one ordered segment of custom-view membership checks against the fetched row, answering with the
    /// caller-visible failure or <see langword="null"/> when the segment authorizes.
    /// </summary>
    /// <remarks>
    /// The row has already been read into memory by the time this runs, which is what the in-memory namespace
    /// check requires too. Denying afterwards discloses nothing: no part of the row reaches the response.
    /// </remarks>
    private async Task<GetResult?> ExecuteGetByIdCustomViewsAsync(
        DescriptorGetByIdRequest request,
        long documentId,
        IReadOnlyList<SingleRecordCustomViewAuthorizationCheckSpec> runChecks,
        IReadOnlyList<SingleRecordCustomViewAuthorizationCheckSpec> plannedChecks,
        CancellationToken cancellationToken
    )
    {
        if (runChecks.Count == 0)
        {
            return null;
        }

        var result = await _customViewAuthorizationExecutor
            .ExecuteAsync(
                new CustomViewAuthorizationExecutionRequest(
                    request.MappingSet,
                    documentId,
                    runChecks,
                    plannedChecks
                ),
                cancellationToken
            )
            .ConfigureAwait(false);

        return result switch
        {
            CustomViewAuthorizationExecutionResult.Authorized => null,
            CustomViewAuthorizationExecutionResult.NotAuthorized notAuthorized =>
                new GetResult.GetFailureCustomViewNotAuthorized(notAuthorized.Failure),
            CustomViewAuthorizationExecutionResult.InvalidAuthorizationFailure invalid =>
                new GetResult.GetFailureSecurityConfiguration([invalid.FailureMessage], invalid.Diagnostics),
            // The row was deleted between this read's SELECT and the membership check, so there is no longer
            // a record to authorize or to report a denial for.
            CustomViewAuthorizationExecutionResult.StaleTarget => new GetResult.GetFailureNotExists(),
            _ => throw new InvalidOperationException(
                $"Unsupported custom view authorization execution result '{result.GetType().Name}'."
            ),
        };
    }

    /// <summary>
    /// Validates the custom views that execute ahead of the caller's outcome. A null or empty list is a
    /// no-op, so every GET-many terminal can call this unconditionally.
    /// </summary>
    private Task ValidateCustomViewsAsync(
        DescriptorQueryRequest request,
        IReadOnlyList<PageDocumentIdAuthorizationCustomViewCheck>? customViewChecks,
        CancellationToken cancellationToken
    ) => ValidateCustomViewsAsync(request.MappingSet.Key.Dialect, customViewChecks, cancellationToken);

    /// <summary>
    /// The dialect-keyed form the GET-many and partition terminals share. Neither operation needs
    /// anything from its request beyond the dialect, and one validator call site keeps the two from
    /// validating different check sets for the same authorization plan.
    /// </summary>
    private Task ValidateCustomViewsAsync(
        SqlDialect dialect,
        IReadOnlyList<PageDocumentIdAuthorizationCustomViewCheck>? customViewChecks,
        CancellationToken cancellationToken
    ) =>
        CustomViewAuthorizationValidator.ValidateAsync(
            _commandExecutor,
            dialect,
            customViewChecks,
            cancellationToken
        );

    /// <summary>
    /// Counts the change-version parameters the descriptor page query will bind: one per supplied
    /// bound (minChangeVersion / maxChangeVersion), zero when no window applies.
    /// </summary>
    private static int CountChangeVersionParameters(ChangeVersionRange changeVersionRange) =>
        (changeVersionRange.MinChangeVersion is null ? 0 : 1)
        + (changeVersionRange.MaxChangeVersion is null ? 0 : 1);

    /// <summary>
    /// Counts the paging parameters the descriptor page query will bind, taken from the candidate mode
    /// the planner will receive rather than assumed. The count is mode-dependent — traditional paging
    /// binds an offset and a limit, cursor selection binds two bounds and a page size — so a fixed count
    /// would undercount the request's real command and the budget would fail at execution instead of
    /// failing closed.
    /// </summary>
    private int CountPagingParameters(DescriptorQueryRequest request) =>
        PageCandidateModePlanning
            .ForPaging(request.Paging, _orderingPolicy.ResolveForLiveQuery(request.ChangeVersionRange))
            .ParameterValues.Count;

    /// <summary>
    /// Returns a security-configuration failure when the descriptor page query's namespace prefix
    /// parameters, plus its query filter, paging, ResourceKeyId, and change-version parameters, exceed
    /// SQL Server's per-command parameter ceiling; otherwise <see langword="null"/>. The dialect gate
    /// lives in <see cref="AuthorizationParameterBudget.ExceedsCommandParameterLimit"/>.
    /// </summary>
    private static QueryResult? BuildDescriptorQueryParameterBudgetFailure(
        SqlDialect dialect,
        QualifiedResourceName resource,
        NamespacePrefixParameterization? namespacePrefixParameterization,
        int queryFilterParameterCount,
        int pagingParameterCount,
        int changeVersionParameterCount
    )
    {
        var nonAuthorizationParameterCount =
            queryFilterParameterCount
            + pagingParameterCount
            + DescriptorQueryResourceKeyParameterCount
            + changeVersionParameterCount;

        if (
            !AuthorizationParameterBudget.ExceedsCommandParameterLimit(
                dialect,
                namespacePrefixParameterization,
                claimEducationOrganizationIdParameterization: null,
                nonAuthorizationParameterCount
            )
        )
        {
            return null;
        }

        return new QueryResult.QueryFailureSecurityConfiguration(
            [
                NamespaceAuthorizationSecurityConfigurationMessages.CommandParameterCapExceeded(
                    namespacePrefixParameterization?.ConfiguredPrefixesInOrder.Count ?? 0,
                    0,
                    nonAuthorizationParameterCount
                ),
            ],
            AuthorizationSecurityConfigurationDiagnostics.ForCommandParameterCapExceeded(resource)
        );
    }

    internal Task<DescriptorQueryRowsPage> ReadQueryRowsAsync(
        DescriptorQueryRequest request,
        DescriptorQueryPreprocessingResult preprocessingResult,
        PageDocumentIdAuthorizationSpec? authorizationSpec = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(preprocessingResult);

        if (preprocessingResult.Outcome is not RelationalQueryPreprocessingOutcome.Continue)
        {
            throw new ArgumentException(
                "Descriptor query row retrieval requires preprocessing results in the continue state.",
                nameof(preprocessingResult)
            );
        }

        var plannedQuery = PlanDescriptorQuery(request, preprocessingResult, authorizationSpec);

        return ReadQueryRowsAsync(request, plannedQuery, cancellationToken);
    }

    private PageKeysetSpec.Query PlanDescriptorQuery(
        DescriptorQueryRequest request,
        DescriptorQueryPreprocessingResult preprocessingResult,
        PageDocumentIdAuthorizationSpec? authorizationSpec
    ) =>
        new DescriptorQueryPageKeysetPlanner(request.MappingSet.Key.Dialect).Plan(
            request.MappingSet,
            request.Resource,
            preprocessingResult,
            request.Paging,
            authorizationSpec,
            request.ChangeVersionRange,
            orderingMode: _orderingPolicy.ResolveForLiveQuery(request.ChangeVersionRange)
        );

    private Task<DescriptorQueryRowsPage> ReadQueryRowsAsync(
        DescriptorQueryRequest request,
        PageKeysetSpec.Query plannedQuery,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(plannedQuery);

        var command = BuildQueryCommand(
            request.MappingSet.Key.Dialect,
            plannedQuery,
            DescriptorRowProjection.FullRow
        );

        return _commandExecutor.ExecuteReaderAsync(
            command,
            (reader, ct) => ReadQueryRowsPageAsync(reader, plannedQuery.Plan.TotalCountSql is not null, ct),
            cancellationToken
        );
    }

    private Task<DescriptorQueryCandidatePage> ReadQueryCandidateRowsAsync(
        DescriptorQueryRequest request,
        PageKeysetSpec.Query plannedQuery,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(plannedQuery);

        var command = BuildQueryCommand(
            request.MappingSet.Key.Dialect,
            plannedQuery,
            DescriptorRowProjection.CandidateMetadata
        );

        return _commandExecutor.ExecuteReaderAsync(
            command,
            (reader, ct) =>
                ReadQueryCandidateRowsPageAsync(reader, plannedQuery.Plan.TotalCountSql is not null, ct),
            cancellationToken
        );
    }

    private void LogDiscriminatorMismatchIfPresent(
        DescriptorGetByIdRequest request,
        IDescriptorReadCandidateMetadata descriptorRow
    )
    {
        if (
            string.IsNullOrWhiteSpace(descriptorRow.Discriminator)
            || string.Equals(
                descriptorRow.Discriminator,
                request.Resource.ResourceName,
                StringComparison.Ordinal
            )
        )
        {
            return;
        }

        _logger.LogWarning(
            "Descriptor GET-by-id read discriminator mismatch for {Resource}: document {DocumentUuid} "
                + "stored discriminator '{StoredDiscriminator}' did not match requested descriptor type "
                + "'{ExpectedDiscriminator}'. ResourceKeyId remained authoritative. - {TraceId}",
            RelationalWriteSupport.FormatResource(request.Resource),
            descriptorRow.DocumentUuid,
            descriptorRow.Discriminator,
            request.Resource.ResourceName,
            request.TraceId.Value
        );
    }

    private GetResult.GetSuccess MaterializeDescriptorGetSuccess(
        DescriptorGetByIdRequest request,
        DescriptorReadRow descriptorRow
    ) =>
        new(
            new DocumentUuid(descriptorRow.DocumentUuid),
            MaterializeDescriptorDocument(request, descriptorRow),
            descriptorRow.ContentLastModifiedAt.UtcDateTime,
            null
        );

    /// <summary>
    /// Builds the descriptor query response, including the selected-keyset boundary a cursor walk
    /// continues from.
    /// </summary>
    /// <remarks>
    /// The rows reaching here are the selected keyset, so their maximum is the boundary. That holds on
    /// both read paths. Uncached selection retrieves rows in the same statement that selects them. The
    /// cache-accelerated path selects candidates and retrieves rows separately, but it admits a page
    /// only when every selected candidate is still present and unchanged, and otherwise re-reads through
    /// the uncached path — so a page that lost rows between selection and retrieval never arrives here
    /// with a short row set that would understate the boundary and stall the walk. Whether the maximum
    /// may anchor a continuation is a separate fact, decided from the ordering the page was selected
    /// with through the same helper the regular-resource path uses.
    /// </remarks>
    private QueryResult.QuerySuccess MaterializeDescriptorQuerySuccess(
        DescriptorQueryRequest request,
        DescriptorQueryRowsPage queryRowsPage
    )
    {
        var continuationBoundary = PageContinuationBoundary.For(
            request.Paging,
            _orderingPolicy.ResolveForLiveQuery(request.ChangeVersionRange),
            SelectedMaximumOf(queryRowsPage.Rows)
        );

        return new QueryResult.QuerySuccess(
            MaterializeDescriptorQueryDocuments(request, queryRowsPage.Rows),
            request.Paging.IncludesTotalCount
                ? RelationalReadGuardrails.ConvertTotalCountOrThrow(
                    request.Resource,
                    queryRowsPage.TotalCount,
                    "descriptor query"
                )
                : null,
            continuationBoundary.SelectedMaximum
        )
        {
            AllowsDocumentIdContinuation = continuationBoundary.AllowsDocumentIdContinuation,
        };
    }

    private JsonArray MaterializeDescriptorQueryDocuments(
        DescriptorQueryRequest request,
        IReadOnlyList<DescriptorReadRow> descriptorRows
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(descriptorRows);

        JsonArray edfiDocs = [];

        foreach (var descriptorRow in descriptorRows)
        {
            edfiDocs.Add(
                MaterializeDescriptorDocument(
                    descriptorRow,
                    RelationalReadMaterializationMode.ExternalResponse,
                    request.ReadableProfileProjectionContext,
                    request.MappingSet.Key.EffectiveSchemaHash,
                    request.ResponseContentCoding
                )
            );
        }

        return edfiDocs;
    }

    private static DocumentCacheReadAccelerationCandidate CreateDescriptorReadAccelerationCandidate(
        IDescriptorReadCandidateMetadata descriptorRow
    ) =>
        new(
            descriptorRow.DocumentId,
            new DocumentUuid(descriptorRow.DocumentUuid),
            descriptorRow.ResourceKeyId,
            descriptorRow.ContentVersion,
            descriptorRow.ContentLastModifiedAt
        );

    private static DocumentCacheReadAccelerationCandidatePage CreateDescriptorReadAccelerationCandidatePage(
        DescriptorQueryCandidatePage queryRowsPage,
        PageContinuationBoundary continuationBoundary,
        bool includesTotalCount
    ) =>
        new(
            [.. queryRowsPage.Rows.Select(CreateDescriptorReadAccelerationCandidate)],
            queryRowsPage.TotalCount,
            continuationBoundary,
            IncludesTotalCount: includesTotalCount
        );

    // Descriptors carry no reference links and are always served as JSON, so the served etag's
    // linkFlag/format components are the fixed descriptor values ("n" / "j"). Profile varies only
    // for ExternalResponse reads that a readable profile actually projects; content coding varies
    // with response compression. CacheProjection is internal and intentionally skips etag injection
    // and readable-profile projection. This condition mirrors
    // RelationalDocumentStoreRepository.ShouldApplyReadableProfileProjection so the descriptor and
    // non-descriptor read paths stay in lockstep.
    private JsonNode MaterializeDescriptorDocument(
        DescriptorReadRow descriptorRow,
        RelationalReadMaterializationMode materializationMode,
        ReadableProfileProjectionContext? readableProfileProjectionContext,
        string effectiveSchemaHash,
        ResponseContentCoding responseContentCoding
    )
    {
        var appliesReadableProfileProjection =
            materializationMode == RelationalReadMaterializationMode.ExternalResponse
            && readableProfileProjectionContext is not null;

        string? composedEtag = null;

        if (materializationMode == RelationalReadMaterializationMode.ExternalResponse)
        {
            string? etagProfileName = appliesReadableProfileProjection
                ? readableProfileProjectionContext!.ProfileName
                : null;

            composedEtag = _servedEtagComposer.Compose(
                new ServedEtagContext(
                    effectiveSchemaHash,
                    ResponseFormat.Json,
                    etagProfileName,
                    LinksEnabled: false,
                    descriptorRow.ContentVersion,
                    responseContentCoding
                )
            );
        }

        var materializedDocument = DescriptorDocumentMaterializer.Materialize(
            descriptorRow,
            materializationMode,
            composedEtag
        );

        if (!appliesReadableProfileProjection)
        {
            return materializedDocument;
        }

        var projectedDocument = _readableProfileProjector.Project(
            materializedDocument,
            readableProfileProjectionContext!.ContentTypeDefinition,
            readableProfileProjectionContext.IdentityPropertyNames
        );

        return projectedDocument;
    }

    private JsonNode MaterializeDescriptorDocument(
        DescriptorGetByIdRequest request,
        DescriptorReadRow descriptorRow
    ) =>
        MaterializeDescriptorDocument(
            descriptorRow,
            request.ReadMode.ToMaterializationMode(),
            request.ReadableProfileProjectionContext,
            request.MappingSet.Key.EffectiveSchemaHash,
            request.ResponseContentCoding
        );

    private static RelationalCommand BuildQueryCommand(
        SqlDialect dialect,
        PageKeysetSpec.Query plannedQuery,
        DescriptorRowProjection projection
    )
    {
        ArgumentNullException.ThrowIfNull(plannedQuery);

        var pageRowsSql = BuildPageRowsSql(dialect, plannedQuery.Plan.PageDocumentIdSql, projection);
        var commandText = plannedQuery.Plan.TotalCountSql is null
            ? pageRowsSql
            : $"{PlanSqlStatementText.AsTerminatedStatement(plannedQuery.Plan.TotalCountSql)}{Environment.NewLine}{Environment.NewLine}{pageRowsSql}";

        return new RelationalCommand(commandText, BuildQueryParameters(plannedQuery));
    }

    private static IReadOnlyList<RelationalParameter> BuildQueryParameters(PageKeysetSpec.Query plannedQuery)
    {
        ArgumentNullException.ThrowIfNull(plannedQuery);

        return
        [
            .. PlannedQueryParameterBinder
                .BindParameters(
                    plannedQuery.Plan,
                    plannedQuery.ParameterValues,
                    "Descriptor query keyset",
                    "Descriptor query keyset parameter",
                    "Unsupported descriptor query parameter binding kind."
                )
                .Select(static binding => new RelationalParameter(
                    binding.Name,
                    binding.Value,
                    binding.ConfigureParameter
                )),
        ];
    }

    private static async Task<DescriptorQueryRowsPage> ReadQueryRowsPageAsync(
        IRelationalCommandReader reader,
        bool hasTotalCount,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(reader);

        long? totalCount = null;

        if (hasTotalCount)
        {
            totalCount = await ReadTotalCountAsync(reader, cancellationToken).ConfigureAwait(false);

            if (!await reader.NextResultAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    "Expected descriptor query row result set after total count but no more result sets were available."
                );
            }
        }

        var rows = await DescriptorReadRowReader
            .ReadAllAsync(reader, cancellationToken)
            .ConfigureAwait(false);

        return new DescriptorQueryRowsPage(totalCount, rows);
    }

    private static async Task<DescriptorQueryCandidatePage> ReadQueryCandidateRowsPageAsync(
        IRelationalCommandReader reader,
        bool hasTotalCount,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(reader);

        long? totalCount = null;

        if (hasTotalCount)
        {
            totalCount = await ReadTotalCountAsync(reader, cancellationToken).ConfigureAwait(false);

            if (!await reader.NextResultAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    "Expected descriptor query candidate result set after total count but no more result sets were available."
                );
            }
        }

        var rows = await DescriptorReadRowReader
            .ReadAllCandidatesAsync(reader, cancellationToken)
            .ConfigureAwait(false);

        return new DescriptorQueryCandidatePage(totalCount, rows);
    }

    private static async Task<long> ReadTotalCountAsync(
        IRelationalCommandReader reader,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(reader);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "Expected a descriptor query total count result row but none was returned."
            );
        }

        var totalCountValue = reader.GetFieldValue<object>(0);

        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "Descriptor query total count result set returned multiple rows."
            );
        }

        return Convert.ToInt64(totalCountValue, CultureInfo.InvariantCulture);
    }

    private static string BuildPageRowsSql(
        SqlDialect dialect,
        string pageDocumentIdSql,
        DescriptorRowProjection projection
    )
    {
        var pageDocumentIdSqlBody = PlanSqlStatementText.AsEmbeddableBody(pageDocumentIdSql);
        var projectionSql = BuildDescriptorProjectionSql(dialect, "page_document_ids", projection);

        // The shared page compiler intentionally returns only a DocumentId keyset. Descriptor queries
        // root on dms.Descriptor, so this performs a page-sized PK lookup instead of widening that contract.
        return dialect switch
        {
            SqlDialect.Pgsql => $$"""
                SELECT
                {{projectionSql}}
                FROM (
                {{pageDocumentIdSqlBody}}
                ) page_document_ids
                INNER JOIN dms."Document" document
                    ON document."DocumentId" = page_document_ids."DocumentId"
                LEFT JOIN dms."Descriptor" descriptor
                    ON descriptor."DocumentId" = page_document_ids."DocumentId"
                ORDER BY page_document_ids."DocumentId" ASC;
                """,
            SqlDialect.Mssql => $$"""
                SELECT
                {{projectionSql}}
                FROM (
                {{pageDocumentIdSqlBody}}
                ) page_document_ids
                INNER JOIN [dms].[Document] document
                    ON document.[DocumentId] = page_document_ids.[DocumentId]
                LEFT JOIN [dms].[Descriptor] descriptor
                    ON descriptor.[DocumentId] = page_document_ids.[DocumentId]
                ORDER BY page_document_ids.[DocumentId] ASC;
                """,
            _ => throw new NotSupportedException(
                $"Relational descriptor GET-many retrieval does not support SQL dialect '{dialect}'."
            ),
        };
    }

    private static string BuildDescriptorProjectionSql(
        SqlDialect dialect,
        string documentIdSourceAlias,
        DescriptorRowProjection projection
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentIdSourceAlias);

        List<(string SourceAlias, string ColumnName)> columns =
        [
            (documentIdSourceAlias, "DocumentId"),
            ("document", "DocumentUuid"),
            ("document", "ContentVersion"),
            ("document", "ContentLastModifiedAt"),
            ("document", "ResourceKeyId"),
            ("descriptor", "Namespace"),
            ("descriptor", "CodeValue"),
        ];

        columns.AddRange(
            projection switch
            {
                DescriptorRowProjection.CandidateMetadata => [],
                DescriptorRowProjection.FullRow =>
                [
                    ("descriptor", "ShortDescription"),
                    ("descriptor", "Description"),
                    ("descriptor", "EffectiveBeginDate"),
                    ("descriptor", "EffectiveEndDate"),
                ],
                _ => throw new ArgumentOutOfRangeException(
                    nameof(projection),
                    projection,
                    "Unsupported descriptor row projection."
                ),
            }
        );

        columns.Add(("descriptor", "Discriminator"));

        return string.Join(
            $",{Environment.NewLine}",
            columns.Select(column =>
                $"    {column.SourceAlias}.{QuoteIdentifier(dialect, column.ColumnName)} AS {QuoteIdentifier(dialect, column.ColumnName)}"
            )
        );
    }

    private static string QuoteIdentifier(SqlDialect dialect, string identifier) =>
        dialect switch
        {
            SqlDialect.Pgsql => $"\"{identifier}\"",
            SqlDialect.Mssql => $"[{identifier}]",
            _ => throw new NotSupportedException(
                $"Relational descriptor projection does not support SQL dialect '{dialect}'."
            ),
        };

    private static RelationalCommand BuildSelectedQueryRowsCommand(
        SqlDialect dialect,
        IReadOnlyList<long> selectedDocumentIds
    )
    {
        ArgumentNullException.ThrowIfNull(selectedDocumentIds);

        if (selectedDocumentIds.Count == 0)
        {
            throw new ArgumentException(
                "Descriptor selected-page fallback requires at least one selected DocumentId.",
                nameof(selectedDocumentIds)
            );
        }

        return dialect switch
        {
            SqlDialect.Pgsql => BuildPgsqlSelectedQueryRowsCommand(selectedDocumentIds),
            SqlDialect.Mssql => BuildMssqlSelectedQueryRowsCommand(selectedDocumentIds),
            _ => throw new NotSupportedException(
                $"Relational descriptor selected-page fallback does not support SQL dialect '{dialect}'."
            ),
        };
    }

    private static RelationalCommand BuildPgsqlSelectedQueryRowsCommand(
        IReadOnlyList<long> selectedDocumentIds
    )
    {
        IReadOnlyList<RelationalParameter> parameters =
        [
            .. selectedDocumentIds.Select(
                static (documentId, index) =>
                    new RelationalParameter($"{SelectedDocumentIdParameterPrefix}{index}", documentId)
            ),
        ];

        string selectedDocumentIdsSql = string.Join(
            $",{Environment.NewLine}",
            selectedDocumentIds.Select(
                static (_, index) => $"({SelectedDocumentIdParameterPrefix}{index}, {index})"
            )
        );

        return new RelationalCommand(
            $$"""
            SELECT
                selected_document_ids."DocumentId" AS "DocumentId",
                document."DocumentUuid" AS "DocumentUuid",
                document."ContentVersion" AS "ContentVersion",
                document."ContentLastModifiedAt" AS "ContentLastModifiedAt",
                document."ResourceKeyId" AS "ResourceKeyId",
                descriptor."Namespace" AS "Namespace",
                descriptor."CodeValue" AS "CodeValue",
                descriptor."ShortDescription" AS "ShortDescription",
                descriptor."Description" AS "Description",
                descriptor."EffectiveBeginDate" AS "EffectiveBeginDate",
                descriptor."EffectiveEndDate" AS "EffectiveEndDate",
                descriptor."Discriminator" AS "Discriminator"
            FROM (
                VALUES
                {{selectedDocumentIdsSql}}
            ) AS selected_document_ids("DocumentId", "Ordinal")
            INNER JOIN dms."Document" document
                ON document."DocumentId" = selected_document_ids."DocumentId"
            LEFT JOIN dms."Descriptor" descriptor
                ON descriptor."DocumentId" = selected_document_ids."DocumentId"
            ORDER BY selected_document_ids."Ordinal" ASC;
            """,
            parameters
        );
    }

    private static RelationalCommand BuildMssqlSelectedQueryRowsCommand(
        IReadOnlyList<long> selectedDocumentIds
    )
    {
        return new RelationalCommand(
            $$"""
            SELECT
                selected_document_ids.[DocumentId] AS [DocumentId],
                document.[DocumentUuid] AS [DocumentUuid],
                document.[ContentVersion] AS [ContentVersion],
                document.[ContentLastModifiedAt] AS [ContentLastModifiedAt],
                document.[ResourceKeyId] AS [ResourceKeyId],
                descriptor.[Namespace] AS [Namespace],
                descriptor.[CodeValue] AS [CodeValue],
                descriptor.[ShortDescription] AS [ShortDescription],
                descriptor.[Description] AS [Description],
                descriptor.[EffectiveBeginDate] AS [EffectiveBeginDate],
                descriptor.[EffectiveEndDate] AS [EffectiveEndDate],
                descriptor.[Discriminator] AS [Discriminator]
            FROM OPENJSON(@selectedDocumentIdsJson)
            WITH (
                [DocumentId] bigint '$.DocumentId',
                [Ordinal] int '$.Ordinal'
            ) AS selected_document_ids
            INNER JOIN [dms].[Document] document
                ON document.[DocumentId] = selected_document_ids.[DocumentId]
            LEFT JOIN [dms].[Descriptor] descriptor
                ON descriptor.[DocumentId] = selected_document_ids.[DocumentId]
            ORDER BY selected_document_ids.[Ordinal] ASC;
            """,
            [CreateSelectedDocumentIdsJsonParameter(selectedDocumentIds)]
        );
    }

    private static RelationalParameter CreateSelectedDocumentIdsJsonParameter(
        IReadOnlyList<long> selectedDocumentIds
    )
    {
        return new RelationalParameter(
            SelectedDocumentIdsJsonParameterName,
            HydrationSqlConventions.SerializeSelectedPageDocumentIds(selectedDocumentIds),
            static parameter =>
            {
                parameter.DbType = DbType.String;
                parameter.Size = -1;
            }
        );
    }

    private static string SelectedDocumentIdsJsonParameterName =>
        $"@{HydrationSqlConventions.SelectedPageDocumentIdsJsonParameterName}";

    /// <summary>
    /// Plans descriptor GET / query namespace authorization through the relational authorization
    /// orchestrator before any SQL is built. Strategies other than <c>NamespaceBased</c> /
    /// <c>NoFurtherAuthorizationRequired</c> fail closed; the namespace planner terminals
    /// (no configured prefixes, no usable root column, MSSQL prefix cap) short-circuit with no DB
    /// roundtrip; otherwise the configured namespace prefixes are surfaced for the in-memory
    /// stored-value check on GET-by-id or for SQL emission on query.
    /// </summary>
    private static DescriptorReadAuthorizationPreflightOutcome ResolveDescriptorReadAuthorization(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        IReadOnlyList<AuthorizationStrategyEvaluator> authorizationStrategyEvaluators,
        RelationalAuthorizationContext authorizationContext,
        NamespaceAuthorizationOperation operation,
        string operationLabel,
        string actionLabel
    )
    {
        var configuredAuthorizationStrategies = ConfiguredAuthorizationStrategyAdapter.Adapt(
            authorizationStrategyEvaluators
        );
        var orchestratorOutcome = RelationalAuthorizationPlanner.Plan(
            mappingSet,
            mappingSet.GetConcreteResourceModelOrThrow(resource),
            operation,
            configuredAuthorizationStrategies,
            authorizationContext
        );

        return orchestratorOutcome switch
        {
            RelationalAuthorizationPlanOutcome.NoUsableRootColumn noUsableRoot =>
                BuildDescriptorNoUsableRootPreflight(mappingSet, resource, noUsableRoot),
            RelationalAuthorizationPlanOutcome.NoPrefixesConfigured noPrefixes =>
                BuildDescriptorNoPrefixesPreflight(mappingSet, resource, noPrefixes),
            // Both read paths route through the same builder, so this terminal carries the custom views
            // configured ahead of it and its message excludes them from the unsupported list. Gating this on
            // GET-many left GET-by-id with a bare 501 that skipped validating those views and named the
            // resolved custom-view strategy as though it were an unsupported blocker.
            RelationalAuthorizationPlanOutcome.Plan plan
                when RelationalReadGuardrails.HasDescriptorUnsupportedNonNamespaceStrategies(
                    plan.NonNamespaceConfiguredStrategies
                ) => BuildDescriptorReadPlanPreflight(
                mappingSet,
                resource,
                authorizationContext,
                plan,
                RelationalReadGuardrails.BuildAuthorizationNotImplementedMessage(
                    resource,
                    authorizationStrategyEvaluators,
                    operationLabel,
                    actionLabel,
                    plan.CustomViewStrategies
                )
            ),
            RelationalAuthorizationPlanOutcome.Plan plan => BuildDescriptorReadPlanPreflight(
                mappingSet,
                resource,
                authorizationContext,
                plan
            ),
            RelationalAuthorizationPlanOutcome.StillUnsupported stillUnsupported =>
                BuildDescriptorReadNotImplemented(
                    mappingSet,
                    resource,
                    stillUnsupported,
                    RelationalReadGuardrails.BuildAuthorizationNotImplementedMessage(
                        resource,
                        authorizationStrategyEvaluators,
                        operationLabel,
                        actionLabel,
                        stillUnsupported.RelationshipClassification.SupportedCustomViewStrategies
                    )
                ),
            RelationalAuthorizationPlanOutcome.SecurityConfigurationError securityConfigurationError =>
                BuildDescriptorReadSecurityConfigurationError(
                    mappingSet,
                    resource,
                    securityConfigurationError
                ),
            _ => throw new InvalidOperationException(
                $"Unsupported relational authorization plan outcome '{orchestratorOutcome.GetType().Name}'."
            ),
        };
    }

    private static DescriptorReadAuthorizationPreflightOutcome BuildDescriptorNoUsableRootPreflight(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        RelationalAuthorizationPlanOutcome.NoUsableRootColumn noUsableRoot
    )
    {
        var errors = new[]
        {
            NamespaceAuthorizationSecurityConfigurationMessages.NoUsableRootColumn(
                RelationalWriteSupport.FormatResource(noUsableRoot.Resource)
            ),
        };
        var diagnostics = RelationalReadGuardrails.BuildNoUsableRootColumnDiagnostics(noUsableRoot.Resource);

        if (
            TryResolveTerminalCustomViewChecks(
                mappingSet,
                resource,
                noUsableRoot.CustomViewStrategies,
                noUsableRoot.RawConfiguredIndex,
                out var customViewChecks
            ) is
            { } customViewFailure
        )
        {
            return customViewFailure;
        }

        return new DescriptorReadAuthorizationPreflightOutcome.SecurityConfigurationError(
            errors,
            diagnostics,
            customViewChecks
        );
    }

    private static DescriptorReadAuthorizationPreflightOutcome BuildDescriptorNoPrefixesPreflight(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        RelationalAuthorizationPlanOutcome.NoPrefixesConfigured noPrefixes
    )
    {
        var namespaceFailure = NamespaceAuthorizationFactory.NoPrefixesConfiguredFailure(
            noPrefixes.StrategyName
        );

        if (
            TryResolveTerminalCustomViewChecks(
                mappingSet,
                resource,
                noPrefixes.CustomViewStrategies,
                noPrefixes.RawConfiguredIndex,
                out var customViewChecks
            ) is
            { } customViewFailure
        )
        {
            return customViewFailure;
        }

        return new DescriptorReadAuthorizationPreflightOutcome.NamespaceNotAuthorized(
            namespaceFailure,
            customViewChecks
        );
    }

    private static DescriptorReadAuthorizationPreflightOutcome BuildDescriptorReadSecurityConfigurationError(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        RelationalAuthorizationPlanOutcome.SecurityConfigurationError securityConfigurationError
    )
    {
        var failure = RelationalReadGuardrails.BuildSecurityConfigurationFailure(
            resource,
            securityConfigurationError.NonNamespaceConfiguredStrategies,
            securityConfigurationError.RelationshipClassification
        );

        // The terminal here is the classifier's earliest security-configuration failure, so only the custom
        // views configured ahead of that index are validated. Mirrors the regular-resource
        // classifier-failure path in RelationalDocumentStoreRepository.
        if (
            TryResolveTerminalCustomViewChecks(
                mappingSet,
                resource,
                securityConfigurationError.RelationshipClassification.SupportedCustomViewStrategies,
                RelationalAuthorizationPlanner.EarliestSecurityConfigurationFailureIndex(
                    securityConfigurationError.RelationshipClassification.SecurityConfigurationFailures
                ),
                out var customViewChecks
            ) is
            { } customViewFailure
        )
        {
            return customViewFailure;
        }

        return new DescriptorReadAuthorizationPreflightOutcome.SecurityConfigurationError(
            failure.Errors,
            failure.Diagnostics,
            customViewChecks
        );
    }

    /// <summary>
    /// The known-but-not-enabled 501 terminal. OwnershipBased — the only known-but-not-enabled strategy —
    /// executes last per auth.md "Execution order", regardless of its configured position, so for GET-many
    /// every resolved custom view is validated before the 501 is reported, mirroring the relational query
    /// path. That lets a missing or non-conforming view surface its own configuration failure. GET-by-id
    /// carries its resolved views the same way, so neither read path reports a bare 501 over them.
    /// </summary>
    private static DescriptorReadAuthorizationPreflightOutcome BuildDescriptorReadNotImplemented(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        RelationalAuthorizationPlanOutcome.StillUnsupported stillUnsupported,
        string failureMessage
    )
    {
        // A null terminal index: this terminal executes last whatever its configured position, so every
        // resolved custom view is validated ahead of it rather than only those configured before it.
        if (
            TryResolveTerminalCustomViewChecks(
                mappingSet,
                resource,
                stillUnsupported.RelationshipClassification.SupportedCustomViewStrategies,
                terminalRawConfiguredIndex: null,
                out var customViewChecks
            ) is
            { } customViewFailure
        )
        {
            return customViewFailure;
        }

        return new DescriptorReadAuthorizationPreflightOutcome.NotImplemented(
            failureMessage,
            customViewChecks
        );
    }

    /// <summary>
    /// Builds the descriptor GET-many security-configuration response for custom-view planning failures.
    /// <see cref="RelationshipAuthorizationFailureKind.NoCustomViewJoinPath"/> gets the same specific
    /// join-path message the regular-resource GET-many path reports; every other kind keeps the guardrail's
    /// existing unknown-strategy wording. Diagnostics come from the guardrail either way, so the
    /// <c>RelationshipAuthorization.{FailureKind}</c> discriminator stays specific.
    /// </summary>
    private static RelationalReadSecurityConfigurationFailure BuildDescriptorCustomViewSecurityConfigurationFailure(
        QualifiedResourceName resource,
        IReadOnlyList<RelationshipAuthorizationFailureMetadata> failures
    )
    {
        var guardrailFailure = RelationalReadGuardrails.BuildSecurityConfigurationFailure(
            resource,
            [],
            new RelationshipAuthorizationClassification(
                RelationshipAuthorizationClassificationOutcome.SecurityConfigurationError,
                [],
                [],
                [],
                [],
                failures
            )
        );

        string[] joinPathErrors =
        [
            .. failures
                .Where(static failure =>
                    failure.FailureKind is RelationshipAuthorizationFailureKind.NoCustomViewJoinPath
                )
                .Select(static failure =>
                    CustomViewAuthorizationFailureMessages.NoJoinPath(failure, "descriptor query")
                ),
        ];

        return joinPathErrors.Length == 0
            ? guardrailFailure
            : guardrailFailure with
            {
                Errors = joinPathErrors,
            };
    }

    /// <summary>
    /// Resolves the custom-view checks a preflight terminal must carry, shared by every terminal builder.
    /// Returns the planning failure when the selected views cannot be planned — that failure replaces the
    /// terminal — otherwise null with <paramref name="checks"/> set to the checks the terminal attaches.
    /// A null <paramref name="terminalRawConfiguredIndex"/> means the terminal executes last whatever its
    /// configured position (per auth.md <em>Execution order</em>), so every resolved custom view runs ahead
    /// of it and is validated; otherwise only the views configured strictly before that index are, since
    /// validating a later one first would let its missing or non-conforming auth view mask the terminal.
    /// </summary>
    private static DescriptorReadAuthorizationPreflightOutcome? TryResolveTerminalCustomViewChecks(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        IReadOnlyList<SupportedCustomViewAuthorizationStrategy> strategies,
        int? terminalRawConfiguredIndex,
        out IReadOnlyList<PageDocumentIdAuthorizationCustomViewCheck> checks
    )
    {
        checks = [];

        // Both read paths execute custom-view checks, so both owe this validation: they plan different check
        // shapes, but a view that is missing or does not meet the DocumentId contract is the same 500 either
        // way. The operation is no longer consulted.
        var strategiesToValidate = terminalRawConfiguredIndex is { } rawConfiguredIndex
            ? CustomViewAuthorizationTerminalOrdering.CustomViewsBeforeTerminal(
                strategies,
                rawConfiguredIndex
            )
            : strategies;

        return TryPlanDescriptorCustomViews(mappingSet, resource, strategiesToValidate, out checks);
    }

    private static DescriptorReadAuthorizationPreflightOutcome? TryPlanDescriptorCustomViews(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        IReadOnlyList<SupportedCustomViewAuthorizationStrategy> customViewStrategies,
        out IReadOnlyList<PageDocumentIdAuthorizationCustomViewCheck> customViewChecks
    )
    {
        customViewChecks = [];

        if (customViewStrategies.Count == 0)
        {
            return null;
        }

        CustomViewAuthorizationPlanOutcome customViewOutcome = CustomViewAuthorizationPlanner.Plan(
            mappingSet,
            mappingSet.GetConcreteResourceModelOrThrow(resource),
            customViewStrategies
        );

        if (customViewOutcome is CustomViewAuthorizationPlanOutcome.SecurityConfiguration customViewSecurity)
        {
            var failure = BuildDescriptorCustomViewSecurityConfigurationFailure(
                resource,
                customViewSecurity.Failures
            );

            // Custom views configured ahead of the earliest planning failure planned successfully and
            // execute first, so they are validated before this failure is reported; a later planning
            // failure must not hide an earlier missing or non-conforming auth view.
            var checksBeforeFailure = PageDocumentIdCustomViewAdapter.AdaptFromChecks(
                CustomViewAuthorizationTerminalOrdering.ChecksBeforeTerminal(
                    customViewSecurity.PlannedChecks,
                    RelationalAuthorizationPlanner.EarliestSecurityConfigurationFailureIndex(
                        customViewSecurity.Failures
                    )
                )
            );

            return new DescriptorReadAuthorizationPreflightOutcome.SecurityConfigurationError(
                failure.Errors,
                failure.Diagnostics,
                checksBeforeFailure
            );
        }

        // The custom-view planner already roots shared-descriptor-table resources on dms.Descriptor with a
        // DocumentId key, which is exactly what the descriptor page query joins against, so the planned
        // checks need no descriptor-specific rewrite here.
        customViewChecks = PageDocumentIdCustomViewAdapter.AdaptFromChecks(
            ((CustomViewAuthorizationPlanOutcome.Plan)customViewOutcome).Checks
        );
        return null;
    }

    private static DescriptorReadAuthorizationPreflightOutcome BuildDescriptorReadPlanPreflight(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        RelationalAuthorizationContext authorizationContext,
        RelationalAuthorizationPlanOutcome.Plan plan,
        string? relationshipNotImplementedFailureMessage = null
    )
    {
        NamespacePrefixParameterization? namespacePrefixParameterization = null;

        if (
            plan.NamespaceChecks.Count > 0
            && !NamespacePrefixParameterizationPreflight.TryCreate(
                mappingSet.Key.Dialect,
                authorizationContext.NamespacePrefixes,
                out namespacePrefixParameterization,
                out var securityConfigurationMessage,
                out var securityConfigurationDiagnostics
            )
        )
        {
            var customViewStrategiesToValidate =
                CustomViewAuthorizationTerminalOrdering.CustomViewsBeforeTerminal(
                    plan.CustomViewStrategies,
                    plan.NamespaceChecks[0].RawConfiguredIndex
                );
            if (
                TryPlanDescriptorCustomViews(
                    mappingSet,
                    resource,
                    customViewStrategiesToValidate,
                    out var customViewChecksBeforeTerminal
                ) is
                { } customViewFailure
            )
            {
                return customViewFailure;
            }

            return new DescriptorReadAuthorizationPreflightOutcome.SecurityConfigurationError(
                [securityConfigurationMessage],
                securityConfigurationDiagnostics,
                customViewChecksBeforeTerminal
            );
        }

        if (
            TryPlanDescriptorCustomViews(
                mappingSet,
                resource,
                plan.CustomViewStrategies,
                out var customViewChecks
            ) is
            { } customViewPlanFailure
        )
        {
            return customViewPlanFailure;
        }

        if (relationshipNotImplementedFailureMessage is not null)
        {
            return new DescriptorReadAuthorizationPreflightOutcome.NotImplemented(
                relationshipNotImplementedFailureMessage,
                customViewChecks
            );
        }

        if (plan.NamespaceChecks.Count == 0 && customViewChecks.Count == 0)
        {
            return DescriptorReadAuthorizationPreflightOutcome.Proceed.NoAuthorization;
        }

        return new DescriptorReadAuthorizationPreflightOutcome.Proceed(
            plan.NamespaceChecks,
            namespacePrefixParameterization,
            customViewChecks,
            plan.CustomViewStrategies
        );
    }

    private static PageDocumentIdAuthorizationSpec? BuildDescriptorQueryAuthorizationSpec(
        DescriptorReadAuthorizationPreflightOutcome.Proceed proceed
    )
    {
        if (proceed.NamespaceChecks.Count == 0 && proceed.CustomViewChecks.Count == 0)
        {
            return null;
        }

        // No relational relationship strategies participate in descriptor queries; pass an empty
        // strategy list so the compiler emits the descriptor namespace and custom-view checks.
        return new PageDocumentIdAuthorizationSpec(
            Strategies: [],
            NamespaceChecks: proceed.NamespaceChecks,
            NamespacePrefixParameterization: proceed.NamespacePrefixParameterization,
            CustomViewChecks: proceed.CustomViewChecks
        );
    }

    private static NamespaceAuthorizationFailure? EvaluateStoredNamespace(
        string? storedNamespace,
        NamespacePrefixParameterization namespacePrefixParameterization
    )
    {
        if (string.IsNullOrEmpty(storedNamespace))
        {
            return new NamespaceAuthorizationFailure(
                NamespaceAuthorizationFailureKind.StoredNamespaceUninitialized,
                NamespaceAuthorizationFailureValueSource.Stored,
                EmittedAuth1Index: 0,
                AuthorizationStrategyNameConstants.NamespaceBased,
                [.. namespacePrefixParameterization.ConfiguredPrefixesInOrder]
            );
        }

        // The single-record GET-by-id check mirrors the LIKE prefix filter the GET-many and write paths
        // emit so it accepts and rejects the same stored namespaces for the same caller. The match and
        // its dialect case sensitivity live on the shared parameterization, next to the SQL escaping it
        // mirrors, instead of being re-derived here.
        if (namespacePrefixParameterization.MatchesAnyPrefix(storedNamespace))
        {
            return null;
        }

        return new NamespaceAuthorizationFailure(
            NamespaceAuthorizationFailureKind.NamespaceMismatch,
            NamespaceAuthorizationFailureValueSource.Stored,
            EmittedAuth1Index: 0,
            AuthorizationStrategyNameConstants.NamespaceBased,
            [.. namespacePrefixParameterization.ConfiguredPrefixesInOrder]
        );
    }

    /// <summary>
    /// Descriptor read authorization preflight results. Each terminal carries the custom-view checks that
    /// must be validated before it is reported — custom views are AND filters executing in CMS-configured
    /// order, so those configured ahead of the terminal still run. The list is empty when no custom view is
    /// configured ahead of the terminal, which is the only case that needs no validation.
    /// </summary>
    private abstract record DescriptorReadAuthorizationPreflightOutcome
    {
        private DescriptorReadAuthorizationPreflightOutcome() { }

        public sealed record NotImplemented(
            string FailureMessage,
            IReadOnlyList<PageDocumentIdAuthorizationCustomViewCheck> CustomViewChecks
        ) : DescriptorReadAuthorizationPreflightOutcome
        {
            public NotImplemented(string failureMessage)
                : this(failureMessage, []) { }
        }

        public sealed record SecurityConfigurationError(
            string[] Errors,
            SecurityConfigurationFailureDiagnostic[]? Diagnostics,
            IReadOnlyList<PageDocumentIdAuthorizationCustomViewCheck> CustomViewChecks
        ) : DescriptorReadAuthorizationPreflightOutcome
        {
            public SecurityConfigurationError(
                string[] errors,
                SecurityConfigurationFailureDiagnostic[]? diagnostics = null
            )
                : this(errors, diagnostics, []) { }
        }

        public sealed record NamespaceNotAuthorized(
            NamespaceAuthorizationFailure Failure,
            IReadOnlyList<PageDocumentIdAuthorizationCustomViewCheck> CustomViewChecks
        ) : DescriptorReadAuthorizationPreflightOutcome
        {
            public NamespaceNotAuthorized(NamespaceAuthorizationFailure failure)
                : this(failure, []) { }
        }

        /// <param name="NamespaceChecks">
        /// Planner-emitted check specs (used by the GET-many SQL emission path).
        /// </param>
        /// <param name="NamespacePrefixParameterization">
        /// Dialect-specific prefix parameterization; non-null exactly when namespace authorization
        /// applies. Drives the GET-many SQL emission and the GET-by-id in-memory stored-value check.
        /// </param>
        /// <param name="CustomViewStrategies">
        /// The configured custom views, carried unplanned so each caller can plan the shape it executes:
        /// GET-many compiles them into its page query, GET-by-id into a single-record membership query.
        /// </param>
        public sealed record Proceed(
            IReadOnlyList<NamespaceAuthorizationCheckSpec> NamespaceChecks,
            NamespacePrefixParameterization? NamespacePrefixParameterization,
            IReadOnlyList<PageDocumentIdAuthorizationCustomViewCheck> CustomViewChecks,
            IReadOnlyList<SupportedCustomViewAuthorizationStrategy> CustomViewStrategies
        ) : DescriptorReadAuthorizationPreflightOutcome
        {
            public static Proceed NoAuthorization { get; } = new([], null, [], []);
        }
    }

    private static RelationalCommand BuildGetByIdCommand(
        SqlDialect dialect,
        DocumentUuid documentUuid,
        short resourceKeyId,
        DescriptorRowProjection projection
    )
    {
        IReadOnlyList<RelationalParameter> parameters =
        [
            new(DocumentUuidParameterName, documentUuid.Value),
            new(ResourceKeyIdParameterName, resourceKeyId),
        ];
        var projectionSql = BuildDescriptorProjectionSql(dialect, "document", projection);

        return dialect switch
        {
            SqlDialect.Pgsql => new(
                $$"""
                SELECT
                {{projectionSql}}
                FROM dms."Document" document
                LEFT JOIN dms."Descriptor" descriptor
                    ON descriptor."DocumentId" = document."DocumentId"
                WHERE document."DocumentUuid" = @documentUuid
                    AND document."ResourceKeyId" = @resourceKeyId;
                """,
                parameters
            ),
            SqlDialect.Mssql => new(
                $$"""
                SELECT
                {{projectionSql}}
                FROM [dms].[Document] document
                LEFT JOIN [dms].[Descriptor] descriptor
                    ON descriptor.[DocumentId] = document.[DocumentId]
                WHERE document.[DocumentUuid] = @documentUuid
                    AND document.[ResourceKeyId] = @resourceKeyId;
                """,
                parameters
            ),
            _ => throw new NotSupportedException(
                $"Relational descriptor GET by id does not support SQL dialect '{dialect}'."
            ),
        };
    }
}
