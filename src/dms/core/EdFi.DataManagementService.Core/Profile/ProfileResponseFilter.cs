// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.Profile;

/// <summary>
/// Filters JSON documents according to profile ReadContentType rules.
/// </summary>
internal class ProfileResponseFilter : IProfileResponseFilter
{
    private const string IdFieldName = "id";
    private const string ExtensionFieldName = "_ext";

    /// <inheritdoc />
    public JsonNode FilterDocument(
        JsonNode document,
        ContentTypeDefinition contentType,
        HashSet<string> identityPropertyNames
    )
    {
        if (document is not JsonObject sourceObject)
        {
            return document.DeepClone();
        }

        return FilterObject(sourceObject, contentType, identityPropertyNames);
    }

    /// <inheritdoc />
    public HashSet<string> ExtractIdentityPropertyNames(IEnumerable<JsonPath> identityJsonPaths)
    {
        return ExtractRootPropertyNames(identityJsonPaths);
    }

    /// <summary>
    /// Filters a JSON object according to content type rules.
    /// </summary>
    private static JsonObject FilterObject(
        JsonObject source,
        ContentTypeDefinition contentType,
        HashSet<string> identityPropertyNames
    )
    {
        var result = new JsonObject();

        foreach (var property in source)
        {
            string propertyName = property.Key;
            JsonNode? propertyValue = property.Value;

            // Always include identity fields and 'id' field
            if (propertyName == IdFieldName || identityPropertyNames.Contains(propertyName))
            {
                result[propertyName] = propertyValue?.DeepClone();
                continue;
            }

            // Handle extensions (_ext field)
            if (propertyName == ExtensionFieldName && propertyValue is JsonObject extObject)
            {
                JsonObject? filteredExt = FilterExtensions(
                    extObject,
                    contentType.ExtensionRulesByName,
                    contentType.MemberSelection
                );
                if (filteredExt != null && filteredExt.Count > 0)
                {
                    result[propertyName] = filteredExt;
                }
                continue;
            }

            // Handle nested objects with explicit rules
            if (contentType.ObjectRulesByName.TryGetValue(propertyName, out ObjectRule? objectRule))
            {
                if (propertyValue is JsonObject nestedObject)
                {
                    result[propertyName] = FilterNestedObject(nestedObject, objectRule);
                }
                continue;
            }

            // Handle collections with explicit rules
            if (
                contentType.CollectionRulesByName.TryGetValue(
                    propertyName,
                    out CollectionRule? collectionRule
                )
            )
            {
                if (propertyValue is JsonArray collectionArray)
                {
                    result[propertyName] = FilterCollection(collectionArray, collectionRule);
                }
                continue;
            }

            // Handle scalar properties based on member selection
            bool shouldInclude = contentType.MemberSelection switch
            {
                MemberSelection.IncludeAll => true,
                MemberSelection.IncludeOnly => contentType.PropertyNameSet.Contains(propertyName),
                MemberSelection.ExcludeOnly => !contentType.PropertyNameSet.Contains(propertyName),
                _ => true,
            };

            if (shouldInclude)
            {
                result[propertyName] = propertyValue?.DeepClone();
            }
        }

        return result;
    }

    /// <summary>
    /// Filters a nested object according to object rule.
    /// </summary>
    private static JsonObject FilterNestedObject(JsonObject source, ObjectRule objectRule)
    {
        var result = new JsonObject();

        foreach (var property in source)
        {
            string propertyName = property.Key;
            JsonNode? propertyValue = property.Value;

            // Handle extensions within nested object
            if (propertyName == ExtensionFieldName && propertyValue is JsonObject extObject)
            {
                JsonObject? filteredExt = FilterExtensions(
                    extObject,
                    objectRule.ExtensionRulesByName,
                    objectRule.MemberSelection
                );
                if (filteredExt != null && filteredExt.Count > 0)
                {
                    result[propertyName] = filteredExt;
                }
                continue;
            }

            // Handle nested objects with explicit rules
            if (objectRule.NestedObjectRulesByName.TryGetValue(propertyName, out ObjectRule? nestedRule))
            {
                if (propertyValue is JsonObject nestedObject)
                {
                    result[propertyName] = FilterNestedObject(nestedObject, nestedRule);
                }
                continue;
            }

            // Handle collections with explicit rules
            if (
                objectRule.CollectionRulesByName.TryGetValue(propertyName, out CollectionRule? collectionRule)
            )
            {
                if (propertyValue is JsonArray collectionArray)
                {
                    result[propertyName] = FilterCollection(collectionArray, collectionRule);
                }
                continue;
            }

            // Handle scalar properties based on member selection
            bool shouldInclude = objectRule.MemberSelection switch
            {
                MemberSelection.IncludeAll => true,
                MemberSelection.IncludeOnly => objectRule.PropertyNameSet.Contains(propertyName),
                MemberSelection.ExcludeOnly => !objectRule.PropertyNameSet.Contains(propertyName),
                _ => true,
            };

            if (shouldInclude)
            {
                result[propertyName] = propertyValue?.DeepClone();
            }
        }

        return result;
    }

    /// <summary>
    /// Filters a collection (array) according to collection rule.
    /// </summary>
    private static JsonArray FilterCollection(JsonArray source, CollectionRule collectionRule)
    {
        var result = new JsonArray();

        foreach (JsonNode? item in source)
        {
            if (item is not JsonObject itemObject)
            {
                // Non-object items are included as-is
                result.Add(item?.DeepClone());
                continue;
            }

            // Apply item filter if present
            if (
                collectionRule.ItemFilter != null
                && !ShouldIncludeCollectionItem(itemObject, collectionRule.ItemFilter)
            )
            {
                continue;
            }

            // Filter the item's properties
            var filteredItem = new JsonObject();

            foreach (var property in itemObject)
            {
                string propertyName = property.Key;
                JsonNode? propertyValue = property.Value;

                // Handle extensions within collection item
                if (propertyName == ExtensionFieldName && propertyValue is JsonObject extObject)
                {
                    JsonObject? filteredExt = FilterExtensions(
                        extObject,
                        collectionRule.ExtensionRulesByName,
                        collectionRule.MemberSelection
                    );
                    if (filteredExt != null && filteredExt.Count > 0)
                    {
                        filteredItem[propertyName] = filteredExt;
                    }
                    continue;
                }

                // Handle nested objects with explicit rules
                if (
                    collectionRule.NestedObjectRulesByName.TryGetValue(
                        propertyName,
                        out ObjectRule? nestedRule
                    )
                )
                {
                    if (propertyValue is JsonObject nestedObject)
                    {
                        filteredItem[propertyName] = FilterNestedObject(nestedObject, nestedRule);
                    }
                    continue;
                }

                // Handle nested collections with explicit rules
                if (
                    collectionRule.NestedCollectionRulesByName.TryGetValue(
                        propertyName,
                        out CollectionRule? nestedCollectionRule
                    )
                )
                {
                    if (propertyValue is JsonArray nestedArray)
                    {
                        filteredItem[propertyName] = FilterCollection(nestedArray, nestedCollectionRule);
                    }
                    continue;
                }

                // Handle scalar properties based on member selection
                bool shouldInclude = collectionRule.MemberSelection switch
                {
                    MemberSelection.IncludeAll => true,
                    MemberSelection.IncludeOnly => collectionRule.PropertyNameSet.Contains(propertyName),
                    MemberSelection.ExcludeOnly => !collectionRule.PropertyNameSet.Contains(propertyName),
                    _ => true,
                };

                if (shouldInclude)
                {
                    filteredItem[propertyName] = propertyValue?.DeepClone();
                }
            }

            result.Add(filteredItem);
        }

        return result;
    }

    /// <summary>
    /// Determines whether a collection item should be included based on item filter.
    /// </summary>
    private static bool ShouldIncludeCollectionItem(JsonObject item, CollectionItemFilter itemFilter)
    {
        // Get the descriptor value from the item
        if (!item.TryGetPropertyValue(itemFilter.PropertyName, out JsonNode? descriptorNode))
        {
            // If the filter property doesn't exist, include the item (conservative approach)
            return true;
        }

        string? descriptorValue = descriptorNode?.GetValue<string>();
        if (string.IsNullOrEmpty(descriptorValue))
        {
            return true;
        }

        // Normalize for case-insensitive comparison
        string normalizedValue = descriptorValue.ToLowerInvariant();
        bool matchesFilter = itemFilter.Values.Any(v =>
            v.Equals(normalizedValue, StringComparison.OrdinalIgnoreCase)
        );

        return itemFilter.FilterMode switch
        {
            FilterMode.IncludeOnly => matchesFilter,
            FilterMode.ExcludeOnly => !matchesFilter,
            _ => true,
        };
    }

    /// <summary>
    /// Filters the _ext object according to extension rules.
    /// </summary>
    private static JsonObject? FilterExtensions(
        JsonObject extObject,
        IReadOnlyDictionary<string, ExtensionRule> extensionRules,
        MemberSelection parentMemberSelection
    )
    {
        var result = new JsonObject();

        foreach (var extension in extObject)
        {
            string extensionName = extension.Key;
            JsonNode? extensionValue = extension.Value;

            if (extensionValue is not JsonObject extensionObject)
            {
                continue;
            }

            // Check if we have explicit rules for this extension
            if (extensionRules.TryGetValue(extensionName, out ExtensionRule? extensionRule))
            {
                JsonObject filteredExtension = FilterExtensionObject(extensionObject, extensionRule);
                if (filteredExtension.Count > 0)
                {
                    result[extensionName] = filteredExtension;
                }
            }
            else
            {
                // No explicit rule - apply parent member selection logic
                // For IncludeOnly at parent level with no extension rule, exclude the extension
                // For ExcludeOnly at parent level with no extension rule, include the extension
                // For IncludeAll at parent level, include the extension
                bool shouldInclude = parentMemberSelection switch
                {
                    MemberSelection.IncludeAll => true,
                    MemberSelection.IncludeOnly => false, // Not explicitly included
                    MemberSelection.ExcludeOnly => true, // Not explicitly excluded
                    _ => true,
                };

                if (shouldInclude)
                {
                    result[extensionName] = extensionObject.DeepClone();
                }
            }
        }

        return result.Count > 0 ? result : null;
    }

    /// <summary>
    /// Filters an extension object according to its extension rule.
    /// </summary>
    private static JsonObject FilterExtensionObject(JsonObject source, ExtensionRule extensionRule)
    {
        var result = new JsonObject();

        foreach (var property in source)
        {
            string propertyName = property.Key;
            JsonNode? propertyValue = property.Value;

            // Handle nested objects with explicit rules
            if (extensionRule.ObjectRulesByName.TryGetValue(propertyName, out ObjectRule? objectRule))
            {
                if (propertyValue is JsonObject nestedObject)
                {
                    result[propertyName] = FilterNestedObject(nestedObject, objectRule);
                }
                continue;
            }

            // Handle collections with explicit rules
            if (
                extensionRule.CollectionRulesByName.TryGetValue(
                    propertyName,
                    out CollectionRule? collectionRule
                )
            )
            {
                if (propertyValue is JsonArray collectionArray)
                {
                    result[propertyName] = FilterCollection(collectionArray, collectionRule);
                }
                continue;
            }

            // Handle scalar properties based on member selection
            bool shouldInclude = extensionRule.MemberSelection switch
            {
                MemberSelection.IncludeAll => true,
                MemberSelection.IncludeOnly => extensionRule.PropertyNameSet.Contains(propertyName),
                MemberSelection.ExcludeOnly => !extensionRule.PropertyNameSet.Contains(propertyName),
                _ => true,
            };

            if (shouldInclude)
            {
                result[propertyName] = propertyValue?.DeepClone();
            }
        }

        return result;
    }

    /// <summary>
    /// Extracts the root property name from each JSON path.
    /// For simple paths like "$.schoolId", returns "schoolId".
    /// For nested paths like "$.courseOfferingReference.localCourseCode", extracts the first segment
    /// "courseOfferingReference" so the entire reference object is preserved during filtering.
    /// </summary>
    private static HashSet<string> ExtractRootPropertyNames(IEnumerable<JsonPath> jsonPaths)
    {
        return jsonPaths
            .Select(path => path.Value)
            .Select(pathValue =>
            {
                // Remove leading "$." or "$" if present
                if (pathValue.StartsWith("$."))
                {
                    pathValue = pathValue[2..];
                }
                else if (pathValue.StartsWith('$'))
                {
                    pathValue = pathValue[1..];
                }

                // Extract first segment (parent object name for nested paths)
                // "courseOfferingReference.localCourseCode" -> "courseOfferingReference"
                // "schoolId" -> "schoolId"
                int dotIndex = pathValue.IndexOf('.');
                return dotIndex > 0 ? pathValue[..dotIndex] : pathValue;
            })
            .ToHashSet();
    }
}
