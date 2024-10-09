// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Core.External.Frontend;

/// <summary>
/// The API response returned to the frontend
/// </summary>
/// <param name="StatusCode">T</param>
/// <param name="Body">The body to return as a JsonNode object, or null if there is no a body to return</param>
/// <param name="Headers">A dictionary of response headers to return</param>
/// <param name="LocationHeaderPath">The path portion of a Location header URL for the response,
///     or null if there is no Location header for the response. Always begins with a forward-slash.
///     There will never be a Location entry in the Headers dictionary if this is not null.</param>
public interface IFrontendResponse
{
    /// <summary>
    /// The HTTP status code to return
    /// </summary>
    int StatusCode { get; }

    /// <summary>
    /// The body to return as a JsonNode object, or null if there is no a body to return
    /// </summary>
    JsonNode? Body { get; }

    /// <summary>
    /// A dictionary of response headers to return
    /// </summary>
    Dictionary<string, string> Headers { get; }

    /// <summary>
    /// The path portion of a Location header URL for the response,
    /// or null if there is no Location header for the response. Always begins with a forward-slash.
    /// There will never be a Location entry in the Headers dictionary if this is not null
    /// </summary>
    string? LocationHeaderPath { get; }
}
