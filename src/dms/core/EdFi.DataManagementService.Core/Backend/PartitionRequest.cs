// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.Backend;

/// <summary>
/// A relational partition boundary request to a partition handler.
/// </summary>
/// <param name="ResourceInfo">
/// The qualified resource identifier for the resource whose partitions are being calculated.
/// </param>
/// <param name="AuthorizationContext">
/// Typed request-scoped authorization inputs for relational planning/execution.
/// </param>
/// <param name="MappingSet">
/// The resolved runtime mapping set for the active relational request.
/// </param>
/// <param name="QueryElements">
/// The resource-property filter elements. This must not include pagination or partition parameters.
/// </param>
/// <param name="AuthorizationStrategyEvaluators">
/// Collection of authorization strategy filters, each specifying collection of filters and filter operator.
/// </param>
/// <param name="RequestedPartitionCount">
/// The desired partition count, already defaulted from configuration when the request omitted it.
/// </param>
/// <param name="MinimumPartitionSize">The smallest partition, in candidate rows.</param>
/// <param name="TraceId">The request TraceId.</param>
/// <param name="ChangeVersionRange">
/// Optional validated minChangeVersion / maxChangeVersion window. Null is normalized to
/// <see cref="External.Model.ChangeVersionRange.None"/> on the relational seam.
/// </param>
/// <param name="TenantKey">The normalized request tenant key.</param>
internal sealed record RelationalPartitionRequest(
    ResourceInfo ResourceInfo,
    RelationalAuthorizationContext AuthorizationContext,
    MappingSet MappingSet,
    QueryElement[] QueryElements,
    AuthorizationStrategyEvaluator[] AuthorizationStrategyEvaluators,
    int RequestedPartitionCount,
    long MinimumPartitionSize,
    TraceId TraceId,
    ChangeVersionRange? ChangeVersionRange = null,
    string TenantKey = ""
) : IPartitionRequest
{
    ChangeVersionRange IPartitionRequest.ChangeVersionRange =>
        ChangeVersionRange ?? External.Model.ChangeVersionRange.None;
}
