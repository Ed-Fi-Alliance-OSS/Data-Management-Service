// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Frontend;

namespace EdFi.DataManagementService.Core.Model;

/// <summary>
/// The API response returned to the frontend
/// </summary>
/// <param name="StatusCode">The HTTP status code to return</param>
/// <param name="Body">The body to return as a JsonNode object, or null if there is no a body to return</param>
/// <param name="Headers">A dictionary of response headers to return</param>
/// <param name="LocationHeaderPath">The path portion of a Location header URL for the response,
///     or null if there is no Location header for the response. Always begins with a forward-slash.
///     There will never be a Location entry in the Headers dictionary if this is not null.</param>
internal record FrontendResponse(
    int StatusCode,
    JsonNode? Body,
    Dictionary<string, string> Headers,
    string? LocationHeaderPath = null
) : IFrontendResponse;
