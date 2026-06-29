// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Backend.External;

/// <summary>
/// Backend-local contract for requests that carry a resolved relational mapping set.
/// This keeps MappingSet off the public Core.External repository request boundary.
/// </summary>
public interface IRelationalRequestWithMappingSet
{
    /// <summary>
    /// The resolved runtime mapping set for the active request.
    /// Supported relational middleware-owned execution paths populate this before repository execution.
    /// </summary>
    MappingSet MappingSet { get; }
}

/// <summary>
/// Backend-local contract for write requests that carry a resolved relational mapping set
/// and optional profile write context.
/// </summary>
public interface IRelationalWriteRequest : IRelationalRequestWithMappingSet
{
    /// <summary>
    /// Optional profile write context when a writable profile applies to the request.
    /// Null when no profile applies or the request is not a write operation.
    /// </summary>
    BackendProfileWriteContext? BackendProfileWriteContext { get; }

    /// <summary>
    /// Effective authorization strategy evaluators for the current write action.
    /// </summary>
    AuthorizationStrategyEvaluator[] AuthorizationStrategyEvaluators { get; }

    /// <summary>
    /// Typed request-scoped authorization inputs for relational write planning/execution.
    /// </summary>
    RelationalAuthorizationContext AuthorizationContext { get; }
}

/// <summary>
/// Backend-local relational upsert request.
/// </summary>
public interface IRelationalUpsertRequest : IUpsertRequest, IRelationalWriteRequest;

/// <summary>
/// Backend-local relational update request.
/// </summary>
public interface IRelationalUpdateRequest : IUpdateRequest, IRelationalWriteRequest;

/// <summary>
/// Backend-local relational delete request.
/// </summary>
public interface IRelationalDeleteRequest : IDeleteRequest, IRelationalRequestWithMappingSet
{
    /// <summary>
    /// Typed request-scoped authorization inputs for relational single-record planning/execution.
    /// </summary>
    RelationalAuthorizationContext AuthorizationContext { get; }

    /// <summary>
    /// Effective authorization strategy evaluators for the current delete action.
    /// </summary>
    AuthorizationStrategyEvaluator[] AuthorizationStrategyEvaluators { get; }
}
