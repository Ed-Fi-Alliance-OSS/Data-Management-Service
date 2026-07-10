// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.Backend;

/// <summary>
/// A relational get request to a document repository.
/// </summary>
/// <param name="DocumentUuid">The document UUID to get.</param>
/// <param name="ResourceInfo">
/// The qualified resource identifier for the resource being retrieved.
/// </param>
/// <param name="MappingSet">
/// The resolved runtime mapping set for the active relational request.
/// </param>
/// <param name="TraceId">The request TraceId.</param>
/// <param name="ReadMode">The local relational read mode for response materialization.</param>
/// <param name="ReadableProfileProjectionContext">
/// Optional readable-profile projection inputs when a readable profile applies to the request.
/// </param>
internal sealed record RelationalGetRequest(
    DocumentUuid DocumentUuid,
    BaseResourceInfo ResourceInfo,
    MappingSet MappingSet,
    RelationalAuthorizationContext AuthorizationContext,
    AuthorizationStrategyEvaluator[] AuthorizationStrategyEvaluators,
    TraceId TraceId,
    RelationalGetRequestReadMode ReadMode = RelationalGetRequestReadMode.ExternalResponse,
    ReadableProfileProjectionContext? ReadableProfileProjectionContext = null,
    ResponseContentCoding ResponseContentCoding = ResponseContentCoding.Identity
) : IGetRequest
{
    public ResourceName ResourceName => ResourceInfo.ResourceName;
}
