// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

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
using EdFi.DataManagementService.Core.Profile;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Backend;

internal sealed record DescriptorQueryRowsPage(long? TotalCount, IReadOnlyList<DescriptorReadRow> Rows);

internal sealed class DescriptorReadHandler(
    IRelationalCommandExecutor commandExecutor,
    IReadableProfileProjector readableProfileProjector,
    IServedEtagComposer servedEtagComposer,
    ILogger<DescriptorReadHandler> logger,
    ChangeQueryPageOrderingPolicy? orderingPolicy = null,
    IDocumentCacheReadAccelerationCoordinator? readAccelerationCoordinator = null
) : IDescriptorReadHandler
{
    private const string DocumentUuidParameterName = "@documentUuid";
    private const string ResourceKeyIdParameterName = "@resourceKeyId";
    private const string SelectedDocumentIdParameterPrefix = "@selectedDocumentId";

    // The descriptor page query binds a single ResourceKeyId discriminator parameter on top of the paging
    // parameters; see DescriptorQueryPageKeysetPlanner. Counted into the non-authorization parameter budget.
    private const int DescriptorQueryResourceKeyParameterCount = 1;
    private readonly IRelationalCommandExecutor _commandExecutor =
        commandExecutor ?? throw new ArgumentNullException(nameof(commandExecutor));
    private readonly IReadableProfileProjector _readableProfileProjector =
        readableProfileProjector ?? throw new ArgumentNullException(nameof(readableProfileProjector));
    private readonly IServedEtagComposer _servedEtagComposer =
        servedEtagComposer ?? throw new ArgumentNullException(nameof(servedEtagComposer));
    private readonly ILogger<DescriptorReadHandler> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly ChangeQueryPageOrderingPolicy _orderingPolicy =
        orderingPolicy ?? ChangeQueryPageOrderingPolicy.Default;
    private readonly IDocumentCacheReadAccelerationCoordinator _readAccelerationCoordinator =
        readAccelerationCoordinator ?? PassthroughDocumentCacheReadAccelerationCoordinator.Instance;

    private abstract record DescriptorGetByIdNoCacheReadResult
    {
        private DescriptorGetByIdNoCacheReadResult() { }

        public sealed record Complete(GetResult Result) : DescriptorGetByIdNoCacheReadResult;

        public sealed record AuthorizedRow(DescriptorReadRow Row) : DescriptorGetByIdNoCacheReadResult;
    }

    private abstract record DescriptorQueryNoCacheReadResult
    {
        private DescriptorQueryNoCacheReadResult() { }

        public sealed record Complete(QueryResult Result) : DescriptorQueryNoCacheReadResult;

        public sealed record RowsPage(DescriptorQueryRowsPage Page) : DescriptorQueryNoCacheReadResult;
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
                    request.ReadMode,
                    DocumentCacheReadAccelerationResourceKind.Descriptor,
                    DocumentCacheReadAccelerationLookupReadiness.RelationalFallbackOnly,
                    (_, fallbackCancellationToken) =>
                        HandleGetByIdNoCacheResultAsync(request, fallbackCancellationToken),
                    SelectAuthorizedCandidate: selectionCancellationToken =>
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
        DescriptorGetByIdNoCacheReadResult noCacheReadResult = await ReadGetByIdNoCacheAsync(
                request,
                cancellationToken
            )
            .ConfigureAwait(false);

        return noCacheReadResult switch
        {
            DescriptorGetByIdNoCacheReadResult.Complete complete => complete.Result,
            DescriptorGetByIdNoCacheReadResult.AuthorizedRow authorizedRow => MaterializeDescriptorGetSuccess(
                request,
                authorizedRow.Row
            ),
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
        DescriptorGetByIdNoCacheReadResult noCacheReadResult = await ReadGetByIdNoCacheAsync(
                request,
                cancellationToken
            )
            .ConfigureAwait(false);

        if (noCacheReadResult is DescriptorGetByIdNoCacheReadResult.Complete complete)
        {
            return new DocumentCacheReadAccelerationGetByIdSelectionResult.Complete(complete.Result);
        }

        var authorizedRow = (DescriptorGetByIdNoCacheReadResult.AuthorizedRow)noCacheReadResult;

        return new DocumentCacheReadAccelerationGetByIdSelectionResult.Candidate(
            CreateDescriptorReadAccelerationCandidate(authorizedRow.Row),
            (_, fallbackCancellationToken) =>
                HandleGetByIdNoCacheResultAsync(request, fallbackCancellationToken)
        );
    }

    private async Task<DescriptorGetByIdNoCacheReadResult> ReadGetByIdNoCacheAsync(
        DescriptorGetByIdRequest request,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        // StoredDocument reads are internal read-modify-write fetches that bypass per-record
        // authorization exactly as the generic single-record path does: the caller was already
        // authorized for the operation that triggered the fetch. Only ExternalResponse reads run the
        // namespace authorization preflight and the in-memory stored-namespace check below.
        NamespacePrefixParameterization? namespacePrefixParameterization = null;

        if (request.ReadMode != RelationalGetRequestReadMode.StoredDocument)
        {
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

            // Custom views are implemented for GET-many only, so no GET-by-id terminal may carry checks.
            // Guard every terminal uniformly rather than one shape, so a future planner change that starts
            // emitting them here fails loudly instead of silently skipping validation.
            ThrowIfGetByIdCarriesCustomViewChecks(authorizationPreflight);

            switch (authorizationPreflight)
            {
                case DescriptorReadAuthorizationPreflightOutcome.NotImplemented notImplemented:
                    return new DescriptorGetByIdNoCacheReadResult.Complete(
                        new GetResult.GetFailureNotImplemented(notImplemented.FailureMessage)
                    );
                case DescriptorReadAuthorizationPreflightOutcome.SecurityConfigurationError configError:
                    return new DescriptorGetByIdNoCacheReadResult.Complete(
                        new GetResult.GetFailureSecurityConfiguration(
                            configError.Errors,
                            configError.Diagnostics
                        )
                    );
                case DescriptorReadAuthorizationPreflightOutcome.NamespaceNotAuthorized namespaceNotAuthorized:
                    return new DescriptorGetByIdNoCacheReadResult.Complete(
                        new GetResult.GetFailureNamespaceNotAuthorized(namespaceNotAuthorized.Failure)
                    );
            }

            namespacePrefixParameterization = (
                (DescriptorReadAuthorizationPreflightOutcome.Proceed)authorizationPreflight
            ).NamespacePrefixParameterization;
        }

        RelationalCommand command;

        try
        {
            command = BuildGetByIdCommand(
                request.MappingSet.Key.Dialect,
                request.DocumentUuid,
                RelationalWriteSupport.GetResourceKeyIdOrThrow(request.MappingSet, request.Resource)
            );
        }
        catch (NotSupportedException ex)
        {
            return new DescriptorGetByIdNoCacheReadResult.Complete(new GetResult.UnknownFailure(ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            return new DescriptorGetByIdNoCacheReadResult.Complete(new GetResult.UnknownFailure(ex.Message));
        }
        catch (KeyNotFoundException ex)
        {
            return new DescriptorGetByIdNoCacheReadResult.Complete(new GetResult.UnknownFailure(ex.Message));
        }

        DescriptorReadRow? descriptorRow;

        try
        {
            descriptorRow = await _commandExecutor
                .ExecuteReaderAsync(
                    command,
                    DescriptorReadRowReader.ReadSingleOrDefaultAsync,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (DescriptorReadInvariantException ex)
        {
            return new DescriptorGetByIdNoCacheReadResult.Complete(new GetResult.UnknownFailure(ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            return new DescriptorGetByIdNoCacheReadResult.Complete(new GetResult.UnknownFailure(ex.Message));
        }

        if (descriptorRow is null)
        {
            return new DescriptorGetByIdNoCacheReadResult.Complete(new GetResult.GetFailureNotExists());
        }

        // The descriptor row reader emits the same Namespace column the orchestrator resolved as
        // the stored authorization source, so the namespace check runs against that materialized
        // value without a second SQL roundtrip. The stored-namespace mismatch and uninitialized
        // failure kinds are constructed directly here because no AUTH1 codec mediates the
        // single-record path.
        if (namespacePrefixParameterization is not null)
        {
            var namespaceFailure = EvaluateStoredNamespace(
                descriptorRow.Namespace,
                namespacePrefixParameterization
            );

            if (namespaceFailure is not null)
            {
                return new DescriptorGetByIdNoCacheReadResult.Complete(
                    new GetResult.GetFailureNamespaceNotAuthorized(namespaceFailure)
                );
            }
        }
        else if (string.IsNullOrEmpty(descriptorRow.Namespace))
        {
            // Without namespace authorization configured, the stored-namespace-uninitialized 403
            // path does not apply, so a null stored Namespace is genuine descriptor row corruption.
            // Surface it as an UnknownFailure with the same column-naming diagnostic the row
            // reader produces for the other required descriptor columns.
            return new DescriptorGetByIdNoCacheReadResult.Complete(
                new GetResult.UnknownFailure(
                    $"Descriptor read corruption detected for DocumentId {descriptorRow.DocumentId} "
                        + $"(ResourceKeyId={descriptorRow.ResourceKeyId}): dms.Descriptor.Namespace must not be null."
                )
            );
        }

        LogDiscriminatorMismatchIfPresent(request, descriptorRow);

        return new DescriptorGetByIdNoCacheReadResult.AuthorizedRow(descriptorRow);
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

        return await _readAccelerationCoordinator
            .QueryAsync(
                new DocumentCacheReadAccelerationQueryRequest(
                    request.TenantKey,
                    request.MappingSet,
                    request.Resource,
                    DocumentCacheReadAccelerationResourceKind.Descriptor,
                    DocumentCacheReadAccelerationLookupReadiness.RelationalFallbackOnly,
                    (_, fallbackCancellationToken) =>
                        HandleQueryNoCacheResultAsync(request, fallbackCancellationToken),
                    SelectAuthorizedCandidatePage: selectionCancellationToken =>
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
        DescriptorQueryNoCacheReadResult noCacheReadResult = await ReadQueryNoCacheAsync(
                request,
                cancellationToken
            )
            .ConfigureAwait(false);

        if (noCacheReadResult is DescriptorQueryNoCacheReadResult.Complete complete)
        {
            return new DocumentCacheReadAccelerationQuerySelectionResult.Complete(complete.Result);
        }

        var rowsPage = (DescriptorQueryNoCacheReadResult.RowsPage)noCacheReadResult;

        if (rowsPage.Page.Rows.Count == 0)
        {
            QueryResult.QuerySuccess relationalSuccess = MaterializeDescriptorQuerySuccess(
                request,
                rowsPage.Page
            );

            return new DocumentCacheReadAccelerationQuerySelectionResult.Complete(relationalSuccess);
        }

        var candidatePage = CreateDescriptorReadAccelerationCandidatePage(rowsPage.Page);

        return new DocumentCacheReadAccelerationQuerySelectionResult.CandidatePage(
            candidatePage,
            (_, fallbackCancellationToken) =>
                HydrateSelectedDescriptorQueryCandidatePageAsync(
                    request,
                    candidatePage,
                    fallbackCancellationToken
                )
        );
    }

    private async Task<QueryResult> HydrateSelectedDescriptorQueryCandidatePageAsync(
        DescriptorQueryRequest request,
        DocumentCacheReadAccelerationCandidatePage candidatePage,
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

        var descriptorRowsByDocumentId = descriptorRows.ToDictionary(
            static row => row.DocumentId,
            static row => row
        );
        DescriptorReadRow[] orderedRows =
        [
            .. selectedDocumentIds
                .Where(descriptorRowsByDocumentId.ContainsKey)
                .Select(documentId => descriptorRowsByDocumentId[documentId]),
        ];

        return MaterializeDescriptorQuerySuccess(
            request,
            new DescriptorQueryRowsPage(candidatePage.TotalCount, orderedRows)
        );
    }

    private async Task<DescriptorQueryNoCacheReadResult> ReadQueryNoCacheAsync(
        DescriptorQueryRequest request,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var authorizationPreflight = ResolveDescriptorReadAuthorization(
            request.MappingSet,
            request.Resource,
            request.AuthorizationStrategyEvaluators,
            request.RelationalAuthorizationContext,
            NamespaceAuthorizationOperation.ReadMany,
            "descriptor query",
            "GET-many"
        );

        // Each terminal validates the custom views configured ahead of it — an empty list is a no-op — and
        // then reports the same result it would have reported without any custom view configured.
        switch (authorizationPreflight)
        {
            case DescriptorReadAuthorizationPreflightOutcome.NotImplemented notImplemented:
                await ValidateCustomViewsAsync(request, notImplemented.CustomViewChecks, cancellationToken)
                    .ConfigureAwait(false);
                return new DescriptorQueryNoCacheReadResult.Complete(
                    new QueryResult.QueryFailureNotImplemented(notImplemented.FailureMessage)
                );
            case DescriptorReadAuthorizationPreflightOutcome.SecurityConfigurationError configError:
                await ValidateCustomViewsAsync(request, configError.CustomViewChecks, cancellationToken)
                    .ConfigureAwait(false);
                return new DescriptorQueryNoCacheReadResult.Complete(
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
                return new DescriptorQueryNoCacheReadResult.Complete(
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
            return new DescriptorQueryNoCacheReadResult.Complete(
                new QueryResult.QueryFailureNotImplemented(ex.Message)
            );
        }
        catch (InvalidOperationException ex)
        {
            return new DescriptorQueryNoCacheReadResult.Complete(new QueryResult.UnknownFailure(ex.Message));
        }
        catch (KeyNotFoundException ex)
        {
            return new DescriptorQueryNoCacheReadResult.Complete(new QueryResult.UnknownFailure(ex.Message));
        }

        if (preprocessingResult.Outcome is RelationalQueryPreprocessingOutcome.EmptyPage)
        {
            await ValidateCustomViewsAsync(request, authorizationSpec?.CustomViewChecks, cancellationToken)
                .ConfigureAwait(false);

            return new DescriptorQueryNoCacheReadResult.Complete(
                new QueryResult.QuerySuccess([], request.PaginationParameters.TotalCount ? 0 : null)
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
            return new DescriptorQueryNoCacheReadResult.Complete(parameterBudgetFailure);
        }

        DescriptorQueryRowsPage queryRowsPage;

        try
        {
            queryRowsPage = await ReadQueryRowsAsync(
                    request,
                    preprocessingResult,
                    authorizationSpec,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        // Trade-off: a provider error raised while executing a custom-view page query is intentionally
        // relabeled as a custom-view validation failure, even though not every such error originates in
        // the view. Validation above already proved the views resolve, so the alternative is letting the
        // DbException escape into the non-ProblemDetails unhandled path and lose the public
        // urn:ed-fi:api:system contract this failure is documented to carry.
        catch (DbException ex) when (authorizationSpec?.CustomViewChecks is { Count: > 0 })
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

    /// <summary>
    /// Fails closed when a GET-by-id preflight terminal carries custom-view checks. GET-by-id has no
    /// validation step, so silently dropping them would skip a configured authorization filter.
    /// </summary>
    private static void ThrowIfGetByIdCarriesCustomViewChecks(
        DescriptorReadAuthorizationPreflightOutcome outcome
    )
    {
        var customViewChecks = outcome switch
        {
            DescriptorReadAuthorizationPreflightOutcome.NotImplemented notImplemented =>
                notImplemented.CustomViewChecks,
            DescriptorReadAuthorizationPreflightOutcome.SecurityConfigurationError configError =>
                configError.CustomViewChecks,
            DescriptorReadAuthorizationPreflightOutcome.NamespaceNotAuthorized namespaceNotAuthorized =>
                namespaceNotAuthorized.CustomViewChecks,
            DescriptorReadAuthorizationPreflightOutcome.Proceed proceed => proceed.CustomViewChecks,
            _ => [],
        };

        if (customViewChecks.Count > 0)
        {
            throw new InvalidOperationException(
                $"Descriptor GET-by-id does not support custom view-based authorization, but the preflight "
                    + $"outcome '{outcome.GetType().Name}' carried {customViewChecks.Count} custom-view check(s)."
            );
        }
    }

    /// <summary>
    /// Validates the custom views that execute ahead of the caller's outcome. A null or empty list is a
    /// no-op, so every GET-many terminal can call this unconditionally.
    /// </summary>
    private Task ValidateCustomViewsAsync(
        DescriptorQueryRequest request,
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
    /// Counts the change-version parameters the descriptor page query will bind: one per supplied
    /// bound (minChangeVersion / maxChangeVersion), zero when no window applies.
    /// </summary>
    private static int CountChangeVersionParameters(ChangeVersionRange changeVersionRange) =>
        (changeVersionRange.MinChangeVersion is null ? 0 : 1)
        + (changeVersionRange.MaxChangeVersion is null ? 0 : 1);

    /// <summary>
    /// Builds the paging choice for a descriptor query request. Descriptor query requests carry only
    /// traditional pagination parameters; cursor page selection reaches the descriptor handler with
    /// cursor execution. This is the single construction site, so the parameter budget below and the
    /// planner always describe the same candidate mode.
    /// </summary>
    private static CollectionPaging BuildPaging(DescriptorQueryRequest request) =>
        new CollectionPaging.Traditional(request.PaginationParameters);

    /// <summary>
    /// Counts the paging parameters the descriptor page query will bind, taken from the candidate mode
    /// the planner will receive rather than assumed. The count is mode-dependent — traditional paging
    /// binds an offset and a limit, cursor selection binds two bounds and a page size — so a fixed count
    /// would undercount as soon as a non-traditional mode reaches this handler, and the budget would
    /// fail at execution instead of failing closed.
    /// </summary>
    private int CountPagingParameters(DescriptorQueryRequest request) =>
        PageCandidateModePlanning
            .ForPaging(BuildPaging(request), _orderingPolicy.ResolveForLiveQuery(request.ChangeVersionRange))
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

        var plannedQuery = new DescriptorQueryPageKeysetPlanner(request.MappingSet.Key.Dialect).Plan(
            request.MappingSet,
            request.Resource,
            preprocessingResult,
            BuildPaging(request),
            authorizationSpec,
            request.ChangeVersionRange,
            orderingMode: _orderingPolicy.ResolveForLiveQuery(request.ChangeVersionRange)
        );
        var command = BuildQueryCommand(request.MappingSet.Key.Dialect, plannedQuery);

        return _commandExecutor.ExecuteReaderAsync(
            command,
            (reader, ct) => ReadQueryRowsPageAsync(reader, plannedQuery.Plan.TotalCountSql is not null, ct),
            cancellationToken
        );
    }

    private void LogDiscriminatorMismatchIfPresent(
        DescriptorGetByIdRequest request,
        DescriptorReadRow descriptorRow
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

    private QueryResult.QuerySuccess MaterializeDescriptorQuerySuccess(
        DescriptorQueryRequest request,
        DescriptorQueryRowsPage queryRowsPage
    ) =>
        new(
            MaterializeDescriptorQueryDocuments(request, queryRowsPage.Rows),
            request.PaginationParameters.TotalCount
                ? RelationalReadGuardrails.ConvertTotalCountOrThrow(
                    request.Resource,
                    queryRowsPage.TotalCount,
                    "descriptor query"
                )
                : null
        );

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
        DescriptorReadRow descriptorRow
    ) =>
        new(
            descriptorRow.DocumentId,
            new DocumentUuid(descriptorRow.DocumentUuid),
            descriptorRow.ResourceKeyId,
            descriptorRow.ContentVersion,
            descriptorRow.ContentLastModifiedAt
        );

    private static DocumentCacheReadAccelerationCandidatePage CreateDescriptorReadAccelerationCandidatePage(
        DescriptorQueryRowsPage queryRowsPage
    ) =>
        new(
            [.. queryRowsPage.Rows.Select(CreateDescriptorReadAccelerationCandidate)],
            queryRowsPage.TotalCount,
            HighestSelectedDocumentId: null
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

    private static RelationalCommand BuildQueryCommand(SqlDialect dialect, PageKeysetSpec.Query plannedQuery)
    {
        ArgumentNullException.ThrowIfNull(plannedQuery);

        var pageRowsSql = BuildPageRowsSql(dialect, plannedQuery.Plan.PageDocumentIdSql);
        var commandText = plannedQuery.Plan.TotalCountSql is null
            ? pageRowsSql
            : $"{PlanSqlStatementText.AsTerminatedStatement(plannedQuery.Plan.TotalCountSql)}{Environment.NewLine}{Environment.NewLine}{pageRowsSql}";

        return new RelationalCommand(commandText, BuildQueryParameters(plannedQuery));
    }

    private static IReadOnlyList<RelationalParameter> BuildQueryParameters(PageKeysetSpec.Query plannedQuery)
    {
        ArgumentNullException.ThrowIfNull(plannedQuery);

        List<QuerySqlParameter> requiredParameters = [];
        HashSet<string> seenParameterNames = new(StringComparer.OrdinalIgnoreCase);

        AddParameters(plannedQuery.Plan.TotalCountParametersInOrder, requiredParameters, seenParameterNames);
        AddParameters(plannedQuery.Plan.PageParametersInOrder, requiredParameters, seenParameterNames);

        List<string> missingParameterNames = [];
        List<RelationalParameter> parameters = [];

        foreach (var queryParameter in requiredParameters)
        {
            if (
                !plannedQuery.ParameterValues.TryGetValue(
                    queryParameter.ParameterName,
                    out var parameterValue
                )
            )
            {
                missingParameterNames.Add(queryParameter.ParameterName);
                continue;
            }

            parameters.Add(
                NamespaceAuthorizationCommandParameterBuilder.BuildParameter(queryParameter, parameterValue)
            );
        }

        if (missingParameterNames.Count > 0)
        {
            throw new InvalidOperationException(
                "Descriptor query keyset is missing required parameter values for "
                    + $"[{string.Join(", ", missingParameterNames.Select(parameterName => $"'{parameterName}'"))}]."
            );
        }

        return parameters;
    }

    private static void AddParameters(
        IReadOnlyList<QuerySqlParameter>? parameterInventory,
        ICollection<QuerySqlParameter> requiredParameters,
        ISet<string> seenParameterNames
    )
    {
        if (parameterInventory is null)
        {
            return;
        }

        foreach (var parameter in parameterInventory)
        {
            if (!seenParameterNames.Add(parameter.ParameterName))
            {
                continue;
            }

            requiredParameters.Add(parameter);
        }
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

    private static string BuildPageRowsSql(SqlDialect dialect, string pageDocumentIdSql)
    {
        var pageDocumentIdSqlBody = PlanSqlStatementText.AsEmbeddableBody(pageDocumentIdSql);

        // The shared page compiler intentionally returns only a DocumentId keyset. Descriptor queries
        // root on dms.Descriptor, so this performs a page-sized PK lookup instead of widening that contract.
        return dialect switch
        {
            SqlDialect.Pgsql => $$"""
                SELECT
                    page_document_ids."DocumentId" AS "DocumentId",
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
                    page_document_ids.[DocumentId] AS [DocumentId],
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
                $"Relational descriptor GET-many row retrieval does not support SQL dialect '{dialect}'."
            ),
        };
    }

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

        return dialect switch
        {
            SqlDialect.Pgsql => new RelationalCommand(
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
            ),
            SqlDialect.Mssql => new RelationalCommand(
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
                FROM (
                    VALUES
                    {{selectedDocumentIdsSql}}
                ) AS selected_document_ids([DocumentId], [Ordinal])
                INNER JOIN [dms].[Document] document
                    ON document.[DocumentId] = selected_document_ids.[DocumentId]
                LEFT JOIN [dms].[Descriptor] descriptor
                    ON descriptor.[DocumentId] = selected_document_ids.[DocumentId]
                ORDER BY selected_document_ids.[Ordinal] ASC;
                """,
                parameters
            ),
            _ => throw new NotSupportedException(
                $"Relational descriptor selected-page fallback does not support SQL dialect '{dialect}'."
            ),
        };
    }

    private static string EnsureTrailingSemicolon(string sql)
    {
        var trimmed = sql.AsSpan().TrimEnd();
        return trimmed.Length > 0 && trimmed[^1] == ';' ? sql : $"{trimmed};";
    }

    private static string StripTrailingSemicolon(string sql)
    {
        var trimmed = sql.AsSpan().TrimEnd();

        if (trimmed.Length > 0 && trimmed[^1] == ';')
        {
            trimmed = trimmed[..^1].TrimEnd();
        }

        return trimmed.ToString();
    }

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
                BuildDescriptorNoUsableRootPreflight(mappingSet, resource, operation, noUsableRoot),
            RelationalAuthorizationPlanOutcome.NoPrefixesConfigured noPrefixes =>
                BuildDescriptorNoPrefixesPreflight(mappingSet, resource, operation, noPrefixes),
            RelationalAuthorizationPlanOutcome.Plan plan
                when operation is NamespaceAuthorizationOperation.ReadMany
                    && RelationalReadGuardrails.HasDescriptorUnsupportedNonNamespaceStrategies(
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
            RelationalAuthorizationPlanOutcome.Plan plan
                when RelationalReadGuardrails.HasDescriptorUnsupportedNonNamespaceStrategies(
                    plan.NonNamespaceConfiguredStrategies
                ) => new DescriptorReadAuthorizationPreflightOutcome.NotImplemented(
                RelationalReadGuardrails.BuildAuthorizationNotImplementedMessage(
                    resource,
                    authorizationStrategyEvaluators,
                    operationLabel,
                    actionLabel
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
                    operation,
                    stillUnsupported,
                    RelationalReadGuardrails.BuildAuthorizationNotImplementedMessage(
                        resource,
                        authorizationStrategyEvaluators,
                        operationLabel,
                        actionLabel,
                        operation is NamespaceAuthorizationOperation.ReadMany
                            ? stillUnsupported.RelationshipClassification.SupportedCustomViewStrategies
                            : null
                    )
                ),
            RelationalAuthorizationPlanOutcome.SecurityConfigurationError securityConfigurationError =>
                BuildDescriptorReadSecurityConfigurationError(
                    mappingSet,
                    resource,
                    operation,
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
        NamespaceAuthorizationOperation operation,
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
                operation,
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
        NamespaceAuthorizationOperation operation,
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
                operation,
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
        NamespaceAuthorizationOperation operation,
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
                operation,
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
    /// path. That lets a missing or non-conforming view surface its own configuration failure.
    /// Custom views are implemented for GET-many only, so other operations keep the bare 501.
    /// </summary>
    private static DescriptorReadAuthorizationPreflightOutcome BuildDescriptorReadNotImplemented(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        NamespaceAuthorizationOperation operation,
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
                operation,
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
        NamespaceAuthorizationOperation operation,
        IReadOnlyList<SupportedCustomViewAuthorizationStrategy> strategies,
        int? terminalRawConfiguredIndex,
        out IReadOnlyList<PageDocumentIdAuthorizationCustomViewCheck> checks
    )
    {
        checks = [];

        // Custom views are implemented for GET-many only, so every other operation keeps its bare terminal.
        if (operation is not NamespaceAuthorizationOperation.ReadMany)
        {
            return null;
        }

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
            customViewChecks
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
    /// order, so those configured ahead of the terminal still run. The list is empty when nothing needs
    /// validating, which is always the case outside GET-many since custom views are GET-many only.
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
        public sealed record Proceed(
            IReadOnlyList<NamespaceAuthorizationCheckSpec> NamespaceChecks,
            NamespacePrefixParameterization? NamespacePrefixParameterization,
            IReadOnlyList<PageDocumentIdAuthorizationCustomViewCheck> CustomViewChecks
        ) : DescriptorReadAuthorizationPreflightOutcome
        {
            public static Proceed NoAuthorization { get; } = new([], null, []);
        }
    }

    private static RelationalCommand BuildGetByIdCommand(
        SqlDialect dialect,
        DocumentUuid documentUuid,
        short resourceKeyId
    )
    {
        IReadOnlyList<RelationalParameter> parameters =
        [
            new(DocumentUuidParameterName, documentUuid.Value),
            new(ResourceKeyIdParameterName, resourceKeyId),
        ];

        return dialect switch
        {
            SqlDialect.Pgsql => new RelationalCommand(
                """
                SELECT
                    document."DocumentId" AS "DocumentId",
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
                FROM dms."Document" document
                LEFT JOIN dms."Descriptor" descriptor
                    ON descriptor."DocumentId" = document."DocumentId"
                WHERE document."DocumentUuid" = @documentUuid
                    AND document."ResourceKeyId" = @resourceKeyId;
                """,
                parameters
            ),
            SqlDialect.Mssql => new RelationalCommand(
                """
                SELECT
                    document.[DocumentId] AS [DocumentId],
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
