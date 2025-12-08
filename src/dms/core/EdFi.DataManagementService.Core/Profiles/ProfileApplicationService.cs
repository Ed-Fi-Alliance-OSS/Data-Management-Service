// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Profiles.Model;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Profiles;

/// <summary>
/// Applies profile filtering rules to JSON documents.
/// </summary>
public class ProfileApplicationService
{
    private readonly ILogger<ProfileApplicationService> _logger;

    public ProfileApplicationService(ILogger<ProfileApplicationService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Applies content type filtering rules to a JSON document.
    /// </summary>
    /// <param name="document">The JSON document to filter</param>
    /// <param name="contentType">The content type rules to apply</param>
    /// <returns>A filtered copy of the document</returns>
    public JsonNode? ApplyFilter(JsonNode? document, ContentType contentType)
    {
        if (document == null)
        {
            return null;
        }

        // Clone the document to avoid modifying the original
        var jsonString = document.ToJsonString();
        var filtered = JsonNode.Parse(jsonString);

        if (filtered is not JsonObject jsonObject)
        {
            // If not an object, return as-is
            return filtered;
        }

        ApplyFilterToObject(jsonObject, contentType);
        return filtered;
    }

    private void ApplyFilterToObject(JsonObject jsonObject, ContentType contentType)
    {
        var propertyNames = contentType.Properties.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var collectionNames = contentType.Collections.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Get list of keys to process (avoid modifying collection while iterating)
        var keys = jsonObject.Select(kvp => kvp.Key).ToList();

        foreach (var key in keys)
        {
            var isProperty = !IsCollection(jsonObject[key]);
            var isInPropertyList = propertyNames.Contains(key);
            var isInCollectionList = collectionNames.Contains(key);

            bool shouldRemove = false;

            if (contentType.MemberSelection == MemberSelection.IncludeOnly)
            {
                // In IncludeOnly mode, remove if not explicitly listed
                if (isProperty)
                {
                    shouldRemove = !isInPropertyList;
                }
                else
                {
                    // It's a collection
                    shouldRemove = !isInCollectionList;
                }
            }
            else if (contentType.MemberSelection == MemberSelection.ExcludeOnly)
            {
                // In ExcludeOnly mode, remove if explicitly listed
                if (isProperty)
                {
                    shouldRemove = isInPropertyList;
                }
                else
                {
                    // It's a collection
                    shouldRemove = isInCollectionList;
                }
            }

            if (shouldRemove)
            {
                jsonObject.Remove(key);
                _logger.LogDebug("Removed {MemberType} '{Key}' based on {MemberSelection}",
                    isProperty ? "property" : "collection", key, contentType.MemberSelection);
            }
        }
    }

    /// <summary>
    /// Determines if a JSON value represents a collection (array).
    /// </summary>
    private static bool IsCollection(JsonNode? node)
    {
        return node is JsonArray;
    }
}
