// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.External.Backend;

/// <summary>
/// A query request to a query handler
/// </summary>
public interface IQueryRequest
{
    /// <summary>
    /// The ResourceInfo for the resource being retrieved
    /// </summary>
    ResourceInfo ResourceInfo { get; }

    /// <summary>
    /// The elements of this query. This must not include pagination parameters.
    /// </summary>
    QueryElement[] QueryElements { get; }

    /// <summary>
    /// Collection of authorization securable info
    /// </summary>
    public AuthorizationSecurableInfo[] AuthorizationSecurableInfo { get; }

    /// Collection of authorization strategy filters, each specifying
    /// collection of filters and filter operator
    /// </summary>
    public AuthorizationStrategyEvaluator[] AuthorizationStrategyEvaluators { get; }

    /// <summary>
    /// The pagination parameters for this query
    /// </summary>
    PaginationParameters PaginationParameters { get; }

    /// <summary>
    /// The request TraceId
    /// </summary>
    TraceId TraceId { get; }
}
