// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Core.Profile;

/// <summary>
/// Applies readable profile projection to a fully reconstituted JSON document.
/// Walks the document recursively, filtering members according to profile rules
/// while preserving identity properties. Produces a new document without altering
/// the input.
/// </summary>
internal sealed class ReadableProfileProjector : IReadableProfileProjector
{
    private const string IdFieldName = "id";
    private const string EtagFieldName = "_etag";
    private const string LastModifiedDateFieldName = "_lastModifiedDate";
    private const string ExtensionFieldName = "_ext";

    /// <inheritdoc />
    public JsonNode Project(
        JsonNode reconstitutedDocument,
        ContentTypeDefinition readContentType,
        IReadOnlySet<string> identityPropertyNames
    )
    {
        if (reconstitutedDocument is JsonArray sourceArray)
        {
            return ProjectArray(sourceArray, readContentType, identityPropertyNames);
        }

        if (reconstitutedDocument is not JsonObject sourceObject)
        {
            return reconstitutedDocument.DeepClone();
        }

        return ProjectRoot(sourceObject, readContentType, identityPropertyNames);
    }

    // -----------------------------------------------------------------------
    //  Array scope (query responses)
    // -----------------------------------------------------------------------

    private static JsonArray ProjectArray(
        JsonArray source,
        ContentTypeDefinition contentType,
        IReadOnlySet<string> identityPropertyNames
    )
    {
        var result = new JsonArray();

        foreach (JsonNode? item in source)
        {
            if (item is JsonObject itemObject)
            {
                result.Add(ProjectRoot(itemObject, contentType, identityPropertyNames));
            }
            else if (item is not null)
            {
                result.Add(item.DeepClone());
            }
        }

        return result;
    }

    // -----------------------------------------------------------------------
    //  Root scope
    // -----------------------------------------------------------------------

    private static JsonObject ProjectRoot(
        JsonObject source,
        ContentTypeDefinition contentType,
        IReadOnlySet<string> identityPropertyNames
    )
    {
        var result = new JsonObject();

        foreach (var property in source)
        {
            string name = property.Key;
            JsonNode? value = property.Value;

            // Always preserve metadata and identity fields at the document root.
            if (
                name is IdFieldName or EtagFieldName or LastModifiedDateFieldName
                || identityPropertyNames.Contains(name)
            )
            {
                result[name] = value?.DeepClone();
                continue;
            }

            // Extensions
            if (name == ExtensionFieldName && value is JsonObject extObject)
            {
                JsonObject? filtered = ProjectExtensions(
                    extObject,
                    contentType.ExtensionRulesByName,
                    contentType.MemberSelection
                );
                if (filtered is { Count: > 0 })
                {
                    result[name] = filtered;
                }
                continue;
            }

            // Nested objects with explicit rules
            if (contentType.ObjectRulesByName.TryGetValue(name, out ObjectRule? objectRule))
            {
                if (value is JsonObject nestedObject)
                {
                    JsonObject projected = ProjectNestedObject(nestedObject, objectRule);
                    if (projected.Count > 0)
                    {
                        result[name] = projected;
                    }
                }
                continue;
            }

            // Collections with explicit rules
            if (contentType.CollectionRulesByName.TryGetValue(name, out CollectionRule? collectionRule))
            {
                if (value is JsonArray collectionArray)
                {
                    JsonArray projected = ProjectCollection(collectionArray, collectionRule);
                    if (projected.Count > 0)
                    {
                        result[name] = projected;
                    }
                }
                continue;
            }

            // Scalar properties — apply member selection
            if (IsMemberIncluded(contentType.MemberSelection, contentType.PropertyNameSet, name))
            {
                result[name] = value?.DeepClone();
            }
        }

        return result;
    }

    // -----------------------------------------------------------------------
    //  Nested objects
    // -----------------------------------------------------------------------

    private static JsonObject ProjectNestedObject(JsonObject source, ObjectRule objectRule)
    {
        var result = new JsonObject();

        foreach (var property in source)
        {
            string name = property.Key;
            JsonNode? value = property.Value;

            // Extensions within nested object
            if (name == ExtensionFieldName && value is JsonObject extObject)
            {
                JsonObject? filtered = ProjectExtensions(
                    extObject,
                    objectRule.ExtensionRulesByName,
                    objectRule.MemberSelection
                );
                if (filtered is { Count: > 0 })
                {
                    result[name] = filtered;
                }
                continue;
            }

            // Nested objects with explicit rules
            if (objectRule.NestedObjectRulesByName.TryGetValue(name, out ObjectRule? nestedRule))
            {
                if (value is JsonObject nestedObject)
                {
                    JsonObject projected = ProjectNestedObject(nestedObject, nestedRule);
                    if (projected.Count > 0)
                    {
                        result[name] = projected;
                    }
                }
                continue;
            }

            // Collections with explicit rules
            if (objectRule.CollectionRulesByName.TryGetValue(name, out CollectionRule? collectionRule))
            {
                if (value is JsonArray collectionArray)
                {
                    JsonArray projected = ProjectCollection(collectionArray, collectionRule);
                    if (projected.Count > 0)
                    {
                        result[name] = projected;
                    }
                }
                continue;
            }

            // Scalar properties
            if (IsMemberIncluded(objectRule.MemberSelection, objectRule.PropertyNameSet, name))
            {
                result[name] = value?.DeepClone();
            }
        }

        return result;
    }

    // -----------------------------------------------------------------------
    //  Collections
    // -----------------------------------------------------------------------

    private static JsonArray ProjectCollection(JsonArray source, CollectionRule collectionRule)
    {
        var result = new JsonArray();

        foreach (JsonNode? item in source)
        {
            if (item is not JsonObject itemObject)
            {
                if (item is not null)
                {
                    result.Add(item.DeepClone());
                }
                continue;
            }

            // Apply collection item value filter
            if (
                collectionRule.ItemFilter is not null
                && !PassesItemFilter(itemObject, collectionRule.ItemFilter)
            )
            {
                continue;
            }

            JsonObject projectedItem = ProjectCollectionItem(itemObject, collectionRule);
            if (projectedItem.Count > 0)
            {
                result.Add(projectedItem);
            }
        }

        return result;
    }

    private static JsonObject ProjectCollectionItem(JsonObject source, CollectionRule collectionRule)
    {
        var result = new JsonObject();

        foreach (var property in source)
        {
            string name = property.Key;
            JsonNode? value = property.Value;

            // Extensions within collection item
            if (name == ExtensionFieldName && value is JsonObject extObject)
            {
                JsonObject? filtered = ProjectExtensions(
                    extObject,
                    collectionRule.ExtensionRulesByName,
                    collectionRule.MemberSelection
                );
                if (filtered is { Count: > 0 })
                {
                    result[name] = filtered;
                }
                continue;
            }

            // Nested objects with explicit rules
            if (collectionRule.NestedObjectRulesByName.TryGetValue(name, out ObjectRule? nestedRule))
            {
                if (value is JsonObject nestedObject)
                {
                    JsonObject projected = ProjectNestedObject(nestedObject, nestedRule);
                    if (projected.Count > 0)
                    {
                        result[name] = projected;
                    }
                }
                continue;
            }

            // Nested collections with explicit rules
            if (
                collectionRule.NestedCollectionRulesByName.TryGetValue(
                    name,
                    out CollectionRule? nestedCollectionRule
                )
            )
            {
                if (value is JsonArray nestedArray)
                {
                    JsonArray projected = ProjectCollection(nestedArray, nestedCollectionRule);
                    if (projected.Count > 0)
                    {
                        result[name] = projected;
                    }
                }
                continue;
            }

            // Scalar properties
            if (IsMemberIncluded(collectionRule.MemberSelection, collectionRule.PropertyNameSet, name))
            {
                result[name] = value?.DeepClone();
            }
        }

        return result;
    }

    // -----------------------------------------------------------------------
    //  Extensions
    // -----------------------------------------------------------------------

    private static JsonObject? ProjectExtensions(
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

            if (extensionRules.TryGetValue(extensionName, out ExtensionRule? extensionRule))
            {
                JsonObject filtered = ProjectExtensionObject(extensionObject, extensionRule);
                if (filtered.Count > 0)
                {
                    result[extensionName] = filtered;
                }
            }
            else
            {
                // No explicit rule — apply parent member selection logic
                bool shouldInclude = parentMemberSelection switch
                {
                    MemberSelection.IncludeAll => true,
                    MemberSelection.IncludeOnly => false,
                    MemberSelection.ExcludeOnly => true,
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

    private static JsonObject ProjectExtensionObject(JsonObject source, ExtensionRule extensionRule)
    {
        var result = new JsonObject();

        foreach (var property in source)
        {
            string name = property.Key;
            JsonNode? value = property.Value;

            // Nested objects with explicit rules
            if (extensionRule.ObjectRulesByName.TryGetValue(name, out ObjectRule? objectRule))
            {
                if (value is JsonObject nestedObject)
                {
                    JsonObject projected = ProjectNestedObject(nestedObject, objectRule);
                    if (projected.Count > 0)
                    {
                        result[name] = projected;
                    }
                }
                continue;
            }

            // Collections with explicit rules
            if (extensionRule.CollectionRulesByName.TryGetValue(name, out CollectionRule? collectionRule))
            {
                if (value is JsonArray collectionArray)
                {
                    JsonArray projected = ProjectCollection(collectionArray, collectionRule);
                    if (projected.Count > 0)
                    {
                        result[name] = projected;
                    }
                }
                continue;
            }

            // Scalar properties
            if (IsMemberIncluded(extensionRule.MemberSelection, extensionRule.PropertyNameSet, name))
            {
                result[name] = value?.DeepClone();
            }
        }

        return result;
    }

    // -----------------------------------------------------------------------
    //  Collection item value filter
    // -----------------------------------------------------------------------

    private static bool PassesItemFilter(JsonObject item, CollectionItemFilter itemFilter)
    {
        if (!item.TryGetPropertyValue(itemFilter.PropertyName, out JsonNode? descriptorNode))
        {
            return true;
        }

        string? descriptorValue = descriptorNode?.GetValue<string>();
        if (string.IsNullOrEmpty(descriptorValue))
        {
            return true;
        }

        bool matchesFilter = itemFilter.Values.Any(v =>
            v.Equals(descriptorValue, StringComparison.OrdinalIgnoreCase)
        );

        return itemFilter.FilterMode switch
        {
            FilterMode.IncludeOnly => matchesFilter,
            FilterMode.ExcludeOnly => !matchesFilter,
            _ => true,
        };
    }

    // -----------------------------------------------------------------------
    //  Helpers
    // -----------------------------------------------------------------------

    private static bool IsMemberIncluded(
        MemberSelection memberSelection,
        HashSet<string> propertyNameSet,
        string name
    )
    {
        return memberSelection switch
        {
            MemberSelection.IncludeAll => true,
            MemberSelection.IncludeOnly => propertyNameSet.Contains(name),
            MemberSelection.ExcludeOnly => !propertyNameSet.Contains(name),
            _ => true,
        };
    }
}
