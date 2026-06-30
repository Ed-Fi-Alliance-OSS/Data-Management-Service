// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Backend.External;

/// <summary>
/// Contract for repository requests that carry a resolved relational mapping set.
/// </summary>
public interface IRequestWithMappingSet
{
    /// <summary>
    /// The resolved runtime mapping set for the active request.
    /// Supported relational middleware-owned execution paths populate this before repository execution.
    /// </summary>
    MappingSet MappingSet { get; }
}

/// <summary>
/// Contract for write requests that carry a resolved relational mapping set
/// and optional profile write context.
/// </summary>
public interface IWriteRequest : IRequestWithMappingSet
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
/// Relational upsert request.
/// </summary>
public interface IUpsertRequest : IUpdateRequest;

/// <summary>
/// Relational update request.
/// </summary>
public interface IUpdateRequest : IWriteRequest
{
    /// <summary>
    /// The ResourceInfo of the document to update.
    /// </summary>
    ResourceInfo ResourceInfo { get; }

    /// <summary>
    /// The DocumentInfo of the document to update.
    /// </summary>
    DocumentInfo DocumentInfo { get; }

    /// <summary>
    /// The Ed-Fi document body.
    /// </summary>
    JsonNode EdfiDoc { get; }

    /// <summary>
    /// Request headers provided by the frontend service.
    /// </summary>
    Dictionary<string, string> Headers { get; }

    /// <summary>
    /// Typed write precondition derived once from the request headers by Core.
    /// </summary>
    WritePrecondition WritePrecondition { get; }

    /// <summary>
    /// The request TraceId.
    /// </summary>
    TraceId TraceId { get; }

    /// <summary>
    /// The DocumentUuid of the document.
    /// </summary>
    DocumentUuid DocumentUuid { get; }
}

/// <summary>
/// Relational delete request.
/// </summary>
public interface IDeleteRequest : IRequestWithMappingSet
{
    /// <summary>
    /// The document UUID to delete.
    /// </summary>
    DocumentUuid DocumentUuid { get; }

    /// <summary>
    /// The ResourceInfo for the resource being deleted.
    /// </summary>
    ResourceInfo ResourceInfo { get; }

    /// <summary>
    /// The request TraceId.
    /// </summary>
    TraceId TraceId { get; }

    /// <summary>
    /// Request headers provided by the frontend service.
    /// </summary>
    Dictionary<string, string> Headers { get; }

    /// <summary>
    /// Typed write precondition derived once from the request headers by Core.
    /// </summary>
    WritePrecondition WritePrecondition { get; }

    /// <summary>
    /// Typed request-scoped authorization inputs for relational single-record planning/execution.
    /// </summary>
    RelationalAuthorizationContext AuthorizationContext { get; }

    /// <summary>
    /// Effective authorization strategy evaluators for the current delete action.
    /// </summary>
    AuthorizationStrategyEvaluator[] AuthorizationStrategyEvaluators { get; }
}
