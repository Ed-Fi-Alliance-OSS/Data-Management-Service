// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.Backend;

/// <summary>
/// A delete request to a document repository
/// </summary>
/// <param name="DocumentUuid">The document UUID to delete</param>
/// <param name="ResourceInfo">The ResourceInfo for the resource being deleted</param>
/// <param name="ResourceAuthorizationHandler">The handler to authorize the delete request for a resource in the database</param>
/// <param name="ResourceAuthorizationPathways">The AuthorizationPathways the resource is part of.</param>
/// <param name="TraceId">The request TraceId</param>
/// <param name="DeleteInEdOrgHierarchy">The request IsEdOrgHierarchy</param>
/// <param name="Headers">Request headers provided by the frontend service as a dictionary</param>
/// <param name="MappingSet">
/// The resolved runtime mapping set for relational execution. Null for non-relational or
/// pipeline-bypass scenarios.
/// </param>
internal record DeleteRequest(
    DocumentUuid DocumentUuid,
    ResourceInfo ResourceInfo,
    IResourceAuthorizationHandler ResourceAuthorizationHandler,
    IReadOnlyList<AuthorizationPathway> ResourceAuthorizationPathways,
    TraceId TraceId,
    bool DeleteInEdOrgHierarchy,
    Dictionary<string, string> Headers,
    MappingSet? MappingSet
) : IRelationalDeleteRequest
{
    public WritePrecondition WritePrecondition { get; init; } = WritePreconditionFactory.Create(Headers);
}
