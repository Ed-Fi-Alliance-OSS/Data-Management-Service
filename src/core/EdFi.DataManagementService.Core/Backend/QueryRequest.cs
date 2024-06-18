// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Model;

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
    /// The search parameters for this query. This must not include pagination parameters.
    /// </summary>
    IDictionary<string, string> SearchParameters,

    /// <summary>
    /// The pagination parameters for this query
    /// </summary>
    IPaginationParameters PaginationParameters,

    /// <summary>
    /// The request TraceId
    /// </summary>
    TraceId TraceId
) : IQueryRequest;
