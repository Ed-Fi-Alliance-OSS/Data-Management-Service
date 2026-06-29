// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Backend.External;

/// <summary>
/// Backend-local relational tracked Change Query request.
/// The public request contract carries ODS-facing query values; this relational seam adds
/// compiled mapping metadata that should stay out of Core.External.
/// </summary>
public interface IRelationalTrackedChangeQueryRequest : ITrackedChangeQueryRequest
{
    /// <summary>
    /// Typed request-scoped authorization inputs for relational tracked Change Query planning/execution.
    /// </summary>
    RelationalAuthorizationContext AuthorizationContext { get; }

    /// <summary>
    /// The resolved ReadChanges authorization strategy evaluators for the request's resource/action,
    /// in configured order. The relational backend adapts these to ConfiguredAuthorizationStrategy.
    /// </summary>
    IReadOnlyList<AuthorizationStrategyEvaluator> AuthorizationStrategyEvaluators { get; }

    /// <summary>
    /// The resolved runtime mapping set for the active relational request.
    /// </summary>
    MappingSet MappingSet { get; }

    /// <summary>
    /// The resolved concrete resource model for the resource-scoped tracked Change Query request.
    /// </summary>
    ConcreteResourceModel ResourceModel { get; }

    /// <summary>
    /// The tracked-change table selected for the resource and operation.
    /// </summary>
    TrackedChangeTableInfo TrackedChangeTable { get; }
}
