// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.Backend;

/// <summary>
/// A query request to a query handler
/// </summary>
internal record QueryRequest(
    /// <summary>
    /// The ResourceInfo for the resource being retrieved
    /// </summary>
    ResourceInfo ResourceInfo,
    /// <summary>
    /// The elements of this query. This must not include pagination parameters.
    /// </summary>
    QueryElement[] QueryElements,
    /// <summary>
    /// Collection of authorization securable info
    /// </summary>
    AuthorizationSecurableInfo[] AuthorizationSecurableInfo,
    /// Collection of authorization strategy filters, each specifying
    /// collection of filters and filter operator
    /// </summary>
    AuthorizationStrategyEvaluator[] AuthorizationStrategyEvaluators,
    /// <summary>
    /// The pagination parameters for this query
    /// </summary>
    PaginationParameters PaginationParameters,
    /// <summary>
    /// The request TraceId
    /// </summary>
    TraceId TraceId
) : IQueryRequest;

/// <summary>
/// A relational query request to a query handler.
/// </summary>
/// <param name="ResourceInfo">
/// The qualified resource identifier for the resource being retrieved.
/// </param>
/// <param name="MappingSet">
/// The resolved runtime mapping set for the active relational request.
/// </param>
/// <param name="QueryElements">The elements of this query. This must not include pagination parameters.</param>
/// <param name="AuthorizationSecurableInfo">Collection of authorization securable info.</param>
/// <param name="AuthorizationStrategyEvaluators">
/// Collection of authorization strategy filters, each specifying collection of filters and filter operator.
/// </param>
/// <param name="PaginationParameters">The pagination parameters for this query.</param>
/// <param name="TraceId">The request TraceId.</param>
/// <param name="ReadableProfileProjectionContext">
/// Optional readable-profile projection inputs when a readable profile applies to the request.
/// </param>
internal sealed record RelationalQueryRequest(
    ResourceInfo ResourceInfo,
    MappingSet MappingSet,
    QueryElement[] QueryElements,
    AuthorizationSecurableInfo[] AuthorizationSecurableInfo,
    AuthorizationStrategyEvaluator[] AuthorizationStrategyEvaluators,
    PaginationParameters PaginationParameters,
    TraceId TraceId,
    ReadableProfileProjectionContext? ReadableProfileProjectionContext = null
)
    : QueryRequest(
        ResourceInfo: ResourceInfo,
        QueryElements: QueryElements,
        AuthorizationSecurableInfo: AuthorizationSecurableInfo,
        AuthorizationStrategyEvaluators: AuthorizationStrategyEvaluators,
        PaginationParameters: PaginationParameters,
        TraceId: TraceId
    ),
        IRelationalQueryRequest;
