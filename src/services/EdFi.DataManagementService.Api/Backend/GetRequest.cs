// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Api.Core.Model;

namespace EdFi.DataManagementService.Api.Backend;

/// <summary>
/// A get request to a document repository
/// </summary>
/// <param name="DocumentUuid">The document UUID to get</param>
/// <param name="ResourceInfo">The ResourceInfo for the resource being retrieved</param>
/// <param name="TraceId">The request TraceId</param>
public record GetRequest(
    DocumentUuid DocumentUuid,
    ResourceInfo ResourceInfo,
    TraceId TraceId
);
