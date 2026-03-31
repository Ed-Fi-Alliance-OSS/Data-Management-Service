// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;

namespace EdFi.DataManagementService.Backend.External;

/// <summary>
/// Backend-local contract for write requests that carry a resolved relational mapping set.
/// This keeps MappingSet off the public Core.External repository request boundary.
/// </summary>
public interface IRelationalWriteRequest
{
    /// <summary>
    /// The resolved runtime mapping set for the active request.
    /// Supported relational middleware-owned execution paths populate this before repository
    /// execution; null remains possible only for direct-call or pipeline-bypass scenarios.
    /// </summary>
    MappingSet? MappingSet { get; }
}

/// <summary>
/// Backend-local relational upsert request.
/// </summary>
public interface IRelationalUpsertRequest : IUpsertRequest, IRelationalWriteRequest;

/// <summary>
/// Backend-local relational update request.
/// </summary>
public interface IRelationalUpdateRequest : IUpdateRequest, IRelationalWriteRequest;
