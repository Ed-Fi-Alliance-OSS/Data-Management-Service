// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Read request context for descriptor GET-by-id operations served from the shared
/// <c>dms.Descriptor</c> table.
/// </summary>
public sealed record DescriptorGetByIdRequest
{
    public DescriptorGetByIdRequest(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        DocumentUuid documentUuid,
        RelationalGetRequestReadMode readMode,
        AuthorizationStrategyEvaluator[] authorizationStrategyEvaluators,
        ReadableProfileProjectionContext? readableProfileProjectionContext,
        TraceId traceId,
        RelationalAuthorizationContext? relationalAuthorizationContext = null,
        ResponseContentCoding responseContentCoding = ResponseContentCoding.Identity,
        string tenantKey = ""
    )
    {
        MappingSet = mappingSet ?? throw new ArgumentNullException(nameof(mappingSet));
        Resource = resource;
        DocumentUuid = documentUuid;
        ReadMode = readMode;
        AuthorizationStrategyEvaluators =
            authorizationStrategyEvaluators
            ?? throw new ArgumentNullException(nameof(authorizationStrategyEvaluators));
        ReadableProfileProjectionContext = readableProfileProjectionContext;
        TraceId = traceId;
        RelationalAuthorizationContext =
            relationalAuthorizationContext ?? new RelationalAuthorizationContext([]);
        ResponseContentCoding = responseContentCoding;
        TenantKey = tenantKey;
    }

    /// <summary>
    /// The resolved runtime mapping set for the active request.
    /// </summary>
    public MappingSet MappingSet { get; init; }

    /// <summary>
    /// The qualified descriptor resource being retrieved.
    /// </summary>
    public QualifiedResourceName Resource { get; init; }

    /// <summary>
    /// The external document UUID supplied on the GET-by-id request.
    /// </summary>
    public DocumentUuid DocumentUuid { get; init; }

    /// <summary>
    /// Controls whether the response should be materialized as an external response or stored document.
    /// </summary>
    public RelationalGetRequestReadMode ReadMode { get; init; }

    /// <summary>
    /// The effective GET authorization strategies already resolved by Core.
    /// </summary>
    public AuthorizationStrategyEvaluator[] AuthorizationStrategyEvaluators { get; init; }

    /// <summary>
    /// Optional readable-profile projection inputs for external-response reads.
    /// </summary>
    public ReadableProfileProjectionContext? ReadableProfileProjectionContext { get; init; }

    /// <summary>
    /// The request trace id for diagnostics.
    /// </summary>
    public TraceId TraceId { get; init; }

    /// <summary>
    /// Request-scoped authorization inputs (namespace prefixes and claim education organization ids)
    /// used by backend-planned namespace authorization. Carried alongside the evaluators because the
    /// evaluators preserve raw strategy names with empty filter providers in relational mode and do not
    /// carry the namespace prefixes the planner needs.
    /// </summary>
    public RelationalAuthorizationContext RelationalAuthorizationContext { get; init; }

    /// <summary>The content coding selected for the external response.</summary>
    public ResponseContentCoding ResponseContentCoding { get; init; }

    /// <summary>The normalized request tenant key used for target-scoped read acceleration.</summary>
    public string TenantKey { get; init; }
}

/// <summary>
/// Read request context for descriptor GET-many/query operations served from the shared
/// <c>dms.Descriptor</c> table.
/// </summary>
public sealed record DescriptorQueryRequest
{
    /// <remarks>
    /// <paramref name="pageOrderingMode" /> is required rather than defaulted, and sits ahead of the
    /// optional parameters to stay that way. This record is the descriptor path's only source for the
    /// anchor — descriptor reads do not travel on <c>IQueryRequest</c> — so a default would let every
    /// existing construction site keep compiling while silently anchoring a ContentVersion-ordered
    /// page on DocumentId, which is the defect this contract exists to carry the fix for.
    /// </remarks>
    public DescriptorQueryRequest(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        QueryElement[] queryElements,
        CollectionPaging paging,
        AuthorizationStrategyEvaluator[] authorizationStrategyEvaluators,
        ReadableProfileProjectionContext? readableProfileProjectionContext,
        TraceId traceId,
        PageOrderingMode pageOrderingMode,
        RelationalAuthorizationContext? relationalAuthorizationContext = null,
        ChangeVersionRange? changeVersionRange = null,
        ResponseContentCoding responseContentCoding = ResponseContentCoding.Identity,
        string tenantKey = ""
    )
    {
        MappingSet = mappingSet ?? throw new ArgumentNullException(nameof(mappingSet));
        Resource = resource;
        QueryElements = queryElements ?? throw new ArgumentNullException(nameof(queryElements));
        Paging = paging ?? throw new ArgumentNullException(nameof(paging));
        AuthorizationStrategyEvaluators =
            authorizationStrategyEvaluators
            ?? throw new ArgumentNullException(nameof(authorizationStrategyEvaluators));
        ReadableProfileProjectionContext = readableProfileProjectionContext;
        TraceId = traceId;
        PageOrderingMode = pageOrderingMode;
        RelationalAuthorizationContext =
            relationalAuthorizationContext ?? new RelationalAuthorizationContext([]);
        ChangeVersionRange = changeVersionRange ?? ChangeVersionRange.None;
        ResponseContentCoding = responseContentCoding;
        TenantKey = tenantKey;
    }

    /// <summary>
    /// The resolved runtime mapping set for the active request.
    /// </summary>
    public MappingSet MappingSet { get; init; }

    /// <summary>
    /// The qualified descriptor resource being queried.
    /// </summary>
    public QualifiedResourceName Resource { get; init; }

    /// <summary>
    /// The client query elements after Core validation and parsing.
    /// </summary>
    public QueryElement[] QueryElements { get; init; }

    /// <summary>
    /// The paging choice for descriptor GET-many execution: traditional limit/offset, or cursor
    /// selection over an inclusive DocumentId range.
    /// </summary>
    public CollectionPaging Paging { get; init; }

    /// <summary>
    /// The effective GET-many authorization strategies already resolved by Core.
    /// </summary>
    public AuthorizationStrategyEvaluator[] AuthorizationStrategyEvaluators { get; init; }

    /// <summary>
    /// Optional readable-profile projection inputs for external-response reads.
    /// </summary>
    public ReadableProfileProjectionContext? ReadableProfileProjectionContext { get; init; }

    /// <summary>
    /// The request trace id for diagnostics.
    /// </summary>
    public TraceId TraceId { get; init; }

    /// <summary>
    /// Request-scoped authorization inputs (namespace prefixes and claim education organization ids)
    /// used by backend-planned namespace authorization. Carried alongside the evaluators because the
    /// evaluators preserve raw strategy names with empty filter providers in relational mode and do not
    /// carry the namespace prefixes the planner needs.
    /// </summary>
    public RelationalAuthorizationContext RelationalAuthorizationContext { get; init; }

    /// <summary>
    /// The validated minChangeVersion / maxChangeVersion window for this query.
    /// <see cref="ChangeVersionRange.None"/> when neither parameter was supplied.
    /// </summary>
    public ChangeVersionRange ChangeVersionRange { get; init; }

    /// <summary>
    /// The page anchor: the ordering key descriptor page selection walks, and therefore the units of
    /// this request's cursor bounds and of the continuation token Core issues for its response.
    /// Resolved by Core from <see cref="ChangeVersionRange" /> and carried here rather than re-derived,
    /// so descriptor pages and regular-resource pages of the same window anchor identically.
    /// </summary>
    public PageOrderingMode PageOrderingMode { get; init; }

    /// <summary>The content coding selected for the external response.</summary>
    public ResponseContentCoding ResponseContentCoding { get; init; }

    /// <summary>The normalized request tenant key used for target-scoped read acceleration.</summary>
    public string TenantKey { get; init; }
}

/// <summary>
/// Request context for descriptor partition-boundary operations served from the shared
/// <c>dms.Descriptor</c> table.
/// </summary>
/// <remarks>
/// Separate from <see cref="DescriptorQueryRequest" /> rather than a paging variant of it: a boundary
/// calculation selects identifiers only, so it has no page, no readable-profile projection, and no
/// response content coding to carry, and the count and minimum size it does carry have no meaning for a
/// page.
/// </remarks>
public sealed record DescriptorPartitionRequest
{
    /// <remarks>
    /// <paramref name="pageOrderingMode" /> is required rather than defaulted, for the reason given on
    /// <see cref="DescriptorQueryRequest" />: this record is the descriptor path's only source for the
    /// anchor, and boundaries cut on the wrong ordering overlap.
    /// </remarks>
    public DescriptorPartitionRequest(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        QueryElement[] queryElements,
        AuthorizationStrategyEvaluator[] authorizationStrategyEvaluators,
        int requestedPartitionCount,
        long minimumPartitionSize,
        TraceId traceId,
        PageOrderingMode pageOrderingMode,
        RelationalAuthorizationContext? relationalAuthorizationContext = null,
        ChangeVersionRange? changeVersionRange = null,
        string tenantKey = ""
    )
    {
        MappingSet = mappingSet ?? throw new ArgumentNullException(nameof(mappingSet));
        Resource = resource;
        QueryElements = queryElements ?? throw new ArgumentNullException(nameof(queryElements));
        AuthorizationStrategyEvaluators =
            authorizationStrategyEvaluators
            ?? throw new ArgumentNullException(nameof(authorizationStrategyEvaluators));
        RequestedPartitionCount = requestedPartitionCount;
        MinimumPartitionSize = minimumPartitionSize;
        TraceId = traceId;
        PageOrderingMode = pageOrderingMode;
        RelationalAuthorizationContext =
            relationalAuthorizationContext ?? new RelationalAuthorizationContext([]);
        ChangeVersionRange = changeVersionRange ?? ChangeVersionRange.None;
        TenantKey = tenantKey;
    }

    /// <summary>
    /// The resolved runtime mapping set for the active request.
    /// </summary>
    public MappingSet MappingSet { get; init; }

    /// <summary>
    /// The qualified descriptor resource whose partitions are being calculated.
    /// </summary>
    public QualifiedResourceName Resource { get; init; }

    /// <summary>
    /// The client query elements after Core validation and parsing. Boundaries are calculated over the
    /// filtered candidate set, so these are the elements the equivalent GET-many would supply.
    /// </summary>
    public QueryElement[] QueryElements { get; init; }

    /// <summary>
    /// The effective GET-many authorization strategies already resolved by Core.
    /// </summary>
    public AuthorizationStrategyEvaluator[] AuthorizationStrategyEvaluators { get; init; }

    /// <summary>
    /// The desired partition count, already defaulted from configuration when the request omitted it.
    /// </summary>
    public int RequestedPartitionCount { get; init; }

    /// <summary>
    /// The smallest partition, in candidate rows.
    /// </summary>
    public long MinimumPartitionSize { get; init; }

    /// <summary>
    /// The request trace id for diagnostics.
    /// </summary>
    public TraceId TraceId { get; init; }

    /// <summary>
    /// Request-scoped authorization inputs (namespace prefixes and claim education organization ids)
    /// used by backend-planned namespace authorization. Carried alongside the evaluators because the
    /// evaluators preserve raw strategy names with empty filter providers in relational mode and do not
    /// carry the namespace prefixes the planner needs.
    /// </summary>
    public RelationalAuthorizationContext RelationalAuthorizationContext { get; init; }

    /// <summary>
    /// The validated minChangeVersion / maxChangeVersion window for this request.
    /// <see cref="Core.External.Model.ChangeVersionRange.None"/> when neither parameter was supplied.
    /// </summary>
    public ChangeVersionRange ChangeVersionRange { get; init; }

    /// <summary>
    /// The boundary anchor: the ordering key descriptor partitions are ranked, sized, and cut on, and
    /// therefore the units of every range this request returns. Resolved by Core from
    /// <see cref="ChangeVersionRange" /> by the same rule a page of the same window resolves, so a
    /// returned range is always replayable as a page.
    /// </summary>
    public PageOrderingMode PageOrderingMode { get; init; }

    /// <summary>The normalized request tenant key. Empty string identifies the default target.</summary>
    public string TenantKey { get; init; }
}

/// <summary>
/// Handles descriptor resource reads from the shared <c>dms.Descriptor</c> table,
/// bypassing the generic project-schema read path.
/// </summary>
public interface IDescriptorReadHandler
{
    /// <summary>
    /// Executes a descriptor GET-by-id read.
    /// </summary>
    Task<GetResult> HandleGetByIdAsync(
        DescriptorGetByIdRequest request,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Executes a descriptor GET-many/query read.
    /// </summary>
    Task<QueryResult> HandleQueryAsync(
        DescriptorQueryRequest request,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Calculates descriptor partition boundaries over the same authorized candidate relation
    /// <see cref="HandleQueryAsync" /> pages.
    /// </summary>
    Task<PartitionResult> HandlePartitionsAsync(
        DescriptorPartitionRequest request,
        CancellationToken cancellationToken = default
    );
}
