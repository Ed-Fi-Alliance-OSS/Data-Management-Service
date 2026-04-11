// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.Backend;

/// <summary>
/// A get request to a document repository
/// </summary>
/// <param name="DocumentUuid">The document UUID to get</param>
/// <param name="ResourceInfo">
/// The qualified resource identifier for the resource being retrieved.
/// </param>
/// <param name="MappingSet">
/// The resolved runtime mapping set for the active request when relational request
/// handling is enabled.
/// </param>
/// <param name="ResourceAuthorizationHandler">The handler to authorize the delete request for a resource in the database</param>
/// <param name="TraceId">The request TraceId</param>
/// <param name="ReadMode">The local relational read mode for response materialization.</param>
/// <param name="ReadableProfileProjectionContext">
/// Optional readable-profile projection inputs when a readable profile applies to the request.
/// </param>
internal record GetRequest(
    DocumentUuid DocumentUuid,
    BaseResourceInfo ResourceInfo,
    MappingSet? MappingSet,
    IResourceAuthorizationHandler ResourceAuthorizationHandler,
    TraceId TraceId,
    RelationalGetRequestReadMode ReadMode = RelationalGetRequestReadMode.ExternalResponse,
    ReadableProfileProjectionContext? ReadableProfileProjectionContext = null
) : IRelationalGetRequest
{
    public ResourceName ResourceName => ResourceInfo.ResourceName;
}
