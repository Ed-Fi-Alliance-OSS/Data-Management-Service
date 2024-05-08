// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Api.Core.Middleware;
using Microsoft.Extensions.Primitives;

namespace EdFi.DataManagementService.Api.Core.Model;

/// <summary>
/// An API request sent from the frontend to be processed
/// </summary>
public record FrontendRequest(
    /// <summary>
    /// The request method from a Tanager frontend - GET, POST, PUT, DELETE
    /// </summary>
    RequestMethod Method,
    /// <summary>
    /// The URL path in the form /namespace/resource and optionally /resourceId
    /// The path must not include query parameters
    /// </summary>
    string Path,
    /// <summary>
    /// Request body provided by the frontend service as a JsonNode, or null if there is no body
    /// </summary>
    JsonNode? Body,
    /// <summary>
    /// Query parameters provided by the frontend service as a dictionary.
    /// </summary>
    Dictionary<string, StringValues> QueryParameters,
    /// <summary>
    /// A request identifier provided by the frontend service, used for log tracing
    /// </summary>
    TraceId TraceId
);
