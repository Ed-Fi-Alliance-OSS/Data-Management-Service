// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Core.Profile;

/// <summary>
/// Builds the per-scope schema-required-members map consumed by the
/// <see cref="CreatabilityAnalyzer"/>. The analyzer evaluates required members
/// per <c>jsonScope</c> for non-collection scopes and collection items, so a
/// root-only entry (<c>{ "$": [...] }</c>) is insufficient: a hidden required
/// non-identity member at a nested or collection scope must still surface a
/// creatability violation.
/// </summary>
/// <remarks>
/// The builder navigates the resource's fully dereferenced/expanded
/// <c>jsonSchemaForInsert</c> for each scope in the catalog, parsing the
/// canonical <c>JsonScope</c> (e.g. <c>$.classPeriods[*]</c>,
/// <c>$._ext.sample</c>, <c>$.classPeriods[*]._ext.sample</c>) into a sequence
/// of property/items navigations and then reading the <c>"required"</c> array
/// at the resolved schema node. The builder fails closed when a catalog scope
/// cannot be resolved in the schema by throwing
/// <see cref="InvalidOperationException"/>: silently omitting such scopes
/// would let the analyzer fall through to "no schema-required members," which
/// can wrongly mark a new nested or extension-child scope as creatable when a
/// hidden required non-identity member would otherwise reject the insert.
/// </remarks>
internal static class EffectiveSchemaRequiredMembersBuilder
{
    /// <summary>
    /// Produces a map of scope <c>JsonScope</c> → schema-required member names
    /// for every scope in <paramref name="scopeCatalog"/> against the supplied
    /// insert schema.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Build(
        JsonNode jsonSchemaForInsert,
        IReadOnlyList<CompiledScopeDescriptor> scopeCatalog
    )
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

#pragma warning disable S3267 // Loop builds a dictionary and throws fail-closed on unresolved scopes; LINQ rewrite would obscure intent.
        foreach (CompiledScopeDescriptor scope in scopeCatalog)
        {
            JsonNode? schemaNode = NavigateToScopeSchemaNode(jsonSchemaForInsert, scope.JsonScope);
            if (schemaNode is null)
            {
                throw new InvalidOperationException(
                    $"Compiled scope '{scope.JsonScope}' is present in the scope catalog "
                        + "but could not be resolved in jsonSchemaForInsert. The scope catalog "
                        + "and schema must agree; missing entries would silently fail open on "
                        + "creatability decisions for hidden required non-identity members in "
                        + "nested/extension-child shapes."
                );
            }

            IReadOnlyList<string> required = ExtractRequiredMembers(schemaNode);
            result[scope.JsonScope] = required;
        }
#pragma warning restore S3267

        return result;
    }

    /// <summary>
    /// Walks <paramref name="root"/> to the schema node corresponding to a
    /// canonical <c>JsonScope</c>. Returns <c>null</c> when the scope cannot be
    /// resolved against the schema (missing property, missing items child, etc.).
    /// </summary>
    private static JsonNode? NavigateToScopeSchemaNode(JsonNode root, string jsonScope)
    {
        if (jsonScope == "$")
        {
            return root;
        }

        if (!jsonScope.StartsWith("$.", StringComparison.Ordinal))
        {
            return null;
        }

        string remainder = jsonScope[2..];
        string[] segments = remainder.Split('.');
        JsonNode current = root;

        foreach (string segment in segments)
        {
            bool isCollectionItem = segment.EndsWith("[*]", StringComparison.Ordinal);
            string memberName = isCollectionItem ? segment[..^3] : segment;

            JsonNode? properties = current["properties"];
            if (properties is null)
            {
                return null;
            }

            JsonNode? memberNode = properties[memberName];
            if (memberNode is null)
            {
                return null;
            }

            if (isCollectionItem)
            {
                JsonNode? items = memberNode["items"];
                if (items is null)
                {
                    return null;
                }
                current = items;
            }
            else
            {
                current = memberNode;
            }
        }

        return current;
    }

    /// <summary>
    /// Reads the <c>"required"</c> array (if any) from a schema node into a
    /// list of member names, ignoring non-string entries defensively.
    /// </summary>
    private static IReadOnlyList<string> ExtractRequiredMembers(JsonNode schemaNode)
    {
        if (schemaNode["required"] is not JsonArray requiredArray)
        {
            return [];
        }

        List<string> members = new(requiredArray.Count);
        foreach (JsonNode? entry in requiredArray)
        {
            if (entry is JsonValue value && value.TryGetValue(out string? name))
            {
                members.Add(name);
            }
        }

        return members;
    }
}
