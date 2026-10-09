// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.External.Frontend;

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
    /// Request body provided by the frontend service as a raw string, or null if there is no body
    /// </summary>
    string? Body,
    /// <summary>
    /// Request form provided by the frontend service as a dictionary, or null if the Content-Type header isn't "x-www-form-urlencoded"
    /// </summary>
    Dictionary<string, string>? Form,
    /// <summary>
    /// Request Headers provided by the frontend service as a **case-insensitive** dictionary
    /// </summary>
    Dictionary<string, string> Headers,
    /// <summary>
    /// Query parameters provided by the frontend service as a dictionary.
    /// </summary>
    Dictionary<string, string> QueryParameters,
    /// <summary>
    /// A request identifier provided by the frontend service, used for log tracing
    /// </summary>
    TraceId TraceId,
    /// <summary>
    /// Route qualifiers extracted from the URL path (e.g., district ID, school year)
    /// that determine which data store to route the request to.
    /// Empty if no route qualifiers are configured.
    /// </summary>
    Dictionary<RouteQualifierName, RouteQualifierValue> RouteQualifiers,
    /// <summary>
    /// The tenant identifier extracted from the URL path when multitenancy is enabled.
    /// Null when multitenancy is disabled.
    /// </summary>
    string? Tenant = null,
    /// <summary>
    /// Request body provided by the frontend service as a parsed JSON body, or null if there is no pre-parsed body
    /// </summary>
    JsonNode? ParsedBody = null,
    /// <summary>
    /// Error message from a failed frontend JSON parse, or null if parsing succeeded or was not attempted
    /// </summary>
    string? BodyParseErrorMessage = null,
    /// <summary>
    /// JSON path for the first duplicate property found by the frontend, or null if none was found or scanning was not attempted
    /// </summary>
    string? DuplicatePropertyPath = null,
    /// <summary>
    /// The content coding negotiated by the frontend for a successful resource response.
    /// </summary>
    ResponseContentCoding ResponseContentCoding = ResponseContentCoding.Identity,
    /// <summary>
    /// The request-line budget left for a path Core composes: the running server's configured
    /// maximum request-line size (for example Kestrel's <c>KestrelServerLimits.MaxRequestLineSize</c>)
    /// minus the length of any prefix the frontend places ahead of that path, such as an ASP.NET Core
    /// <c>PathBase</c>. Carried per request so Core can bound a composed poll path against the real
    /// deployment budget without knowing about such prefixes. Null when the frontend did not supply
    /// one, in which case a Core-side default applies.
    /// </summary>
    int? MaxRequestLineSize = null
)
{
    /// <summary>
    /// Query parameter names the client supplied more than once, counting letter-case variants of one
    /// name as the same name, because <see cref="QueryParameters"/> keeps only one value per name and
    /// cannot show a repeat. Compare entries with <see cref="StringComparer.OrdinalIgnoreCase"/>.
    /// Populated only by frontend handlers whose endpoint rejects repeated parameters; empty
    /// everywhere else.
    /// </summary>
    public IReadOnlyCollection<string> RepeatedQueryParameterNames { get; init; } = [];
}
