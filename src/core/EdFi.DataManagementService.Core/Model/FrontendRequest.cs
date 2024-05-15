// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Core.Model;

/// <summary>
/// An API request sent from the frontend to be processed
/// </summary>
public record FrontendRequest(
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
    Dictionary<string, string> QueryParameters,
    /// <summary>
    /// A request identifier provided by the frontend service, used for log tracing
    /// </summary>
    string TraceId
);
