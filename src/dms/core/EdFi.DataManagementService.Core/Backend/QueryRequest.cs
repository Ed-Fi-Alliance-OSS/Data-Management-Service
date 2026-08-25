// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.Backend;

/// <summary>
/// A relational query request to a query handler.
/// </summary>
/// <param name="ResourceInfo">
/// The qualified resource identifier for the resource being retrieved.
/// </param>
/// <param name="AuthorizationContext">
/// Typed request-scoped authorization inputs for relational GET-many planning/execution.
/// </param>
/// <param name="MappingSet">
/// The resolved runtime mapping set for the active relational request.
/// </param>
/// <param name="QueryElements">The elements of this query. This must not include pagination parameters.</param>
/// <param name="AuthorizationStrategyEvaluators">
/// Collection of authorization strategy filters, each specifying collection of filters and filter operator.
/// </param>
/// <param name="Paging">The paging mode and its inputs for this query.</param>
/// <param name="TraceId">The request TraceId.</param>
/// <param name="TenantKey">The normalized request tenant key.</param>
/// <param name="ReadableProfileProjectionContext">
/// Optional readable-profile projection inputs when a readable profile applies to the request.
/// </param>
/// <param name="ChangeVersionRange">
/// Optional validated minChangeVersion / maxChangeVersion window. Null is normalized to
/// <see cref="External.Model.ChangeVersionRange.None"/> on the relational seam.
/// </param>
/// <param name="PageOrderingMode">
/// The page anchor Core resolved from the change-version window. Defaults to
/// <see cref="External.Model.PageOrderingMode.DocumentId" />, which is the anchor of every window
/// shape but a max-bearing one, so a fixture that omits it selects the ordering this contract has
/// always used rather than a new one.
/// </param>
internal sealed record RelationalQueryRequest(
    ResourceInfo ResourceInfo,
    RelationalAuthorizationContext AuthorizationContext,
    MappingSet MappingSet,
    QueryElement[] QueryElements,
    AuthorizationStrategyEvaluator[] AuthorizationStrategyEvaluators,
    CollectionPaging Paging,
    TraceId TraceId,
    ReadableProfileProjectionContext? ReadableProfileProjectionContext = null,
    ChangeVersionRange? ChangeVersionRange = null,
    ResponseContentCoding ResponseContentCoding = ResponseContentCoding.Identity,
    string TenantKey = "",
    PageOrderingMode PageOrderingMode = External.Model.PageOrderingMode.DocumentId
) : IQueryRequest
{
    ChangeVersionRange IQueryRequest.ChangeVersionRange =>
        ChangeVersionRange ?? External.Model.ChangeVersionRange.None;
}
