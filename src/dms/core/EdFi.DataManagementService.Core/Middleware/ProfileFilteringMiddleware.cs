// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Profile;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Middleware that filters GET response bodies according to profile ReadContentType rules.
/// This middleware runs before the handler and uses post-processing to filter the response
/// after the handler has set it.
/// </summary>
internal class ProfileFilteringMiddleware(
    IProfileResponseFilter profileResponseFilter,
    ILogger<ProfileFilteringMiddleware> logger
) : IPipelineStep
{
    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        // Execute downstream handlers first
        await next();

        // Only filter successful GET responses
        if (requestInfo.FrontendResponse.StatusCode != 200)
        {
            return;
        }

        // Only filter if profile context with ReadContentType exists
        ContentTypeDefinition? readContentType = requestInfo.ProfileContext?.ResourceProfile.ReadContentType;

        if (readContentType == null)
        {
            return;
        }

        // Get the response body
        var responseBody = requestInfo.FrontendResponse.Body;
        if (responseBody == null)
        {
            return;
        }

        logger.LogDebug(
            "Applying profile response filter. Profile: {ProfileName} - {TraceId}",
            requestInfo.ProfileContext!.ProfileName,
            requestInfo.FrontendRequest.TraceId.Value
        );

        // Get identity paths for protection
        var identityPaths = requestInfo.ResourceSchema.IdentityJsonPaths;

        // Filter based on response type (single document vs array)
        JsonNode filteredBody = responseBody switch
        {
            JsonArray array => FilterArray(array, readContentType, identityPaths),
            JsonObject => profileResponseFilter.FilterDocument(responseBody, readContentType, identityPaths),
            _ => responseBody,
        };

        // Replace response with filtered version
        requestInfo.FrontendResponse = new FrontendResponse(
            StatusCode: requestInfo.FrontendResponse.StatusCode,
            Body: filteredBody,
            Headers: requestInfo.FrontendResponse.Headers.ToDictionary(h => h.Key, h => h.Value),
            LocationHeaderPath: requestInfo.FrontendResponse.LocationHeaderPath,
            ContentType: requestInfo.FrontendResponse.ContentType
        );

        logger.LogDebug(
            "Profile response filter applied successfully - {TraceId}",
            requestInfo.FrontendRequest.TraceId.Value
        );
    }

    /// <summary>
    /// Filters each document in a query result array.
    /// </summary>
    private JsonArray FilterArray(
        JsonArray array,
        ContentTypeDefinition contentType,
        IEnumerable<External.Model.JsonPath> identityPaths
    )
    {
        var filtered = new JsonArray();

        foreach (JsonNode? item in array)
        {
            if (item == null)
            {
                continue;
            }

            JsonNode filteredItem = profileResponseFilter.FilterDocument(item, contentType, identityPaths);
            filtered.Add(filteredItem);
        }

        return filtered;
    }
}
