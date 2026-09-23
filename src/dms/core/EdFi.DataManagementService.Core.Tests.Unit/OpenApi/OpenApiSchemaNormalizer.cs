// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using Json.Schema;

namespace EdFi.DataManagementService.Core.Tests.Unit.OpenApi;

/// <summary>
/// Converts an OpenAPI 3.0 component schema into a JSON Schema draft <c>JsonSchema.Net</c> accepts
/// (story D4/D14): OpenAPI's <c>nullable: true</c> has no JSON Schema equivalent, so a property typed
/// <c>{"type": "string", "nullable": true}</c> becomes <c>{"type": ["string", "null"]}</c>, and a
/// nullable <c>$ref</c> wrapped for OpenAPI 3.0's syntax as
/// <c>{"allOf": [{"$ref": "..."}], "nullable": true}</c> becomes
/// <c>{"anyOf": [{"$ref": "..."}, {"type": "null"}]}</c>. Every <c>$ref</c> target
/// <c>#/components/schemas/X</c> is rewritten to <c>#/$defs/X</c>, and the full component schema map is
/// copied into the built document's <c>$defs</c> so every rewritten reference resolves.
/// </summary>
internal static class OpenApiSchemaNormalizer
{
    private const string ComponentSchemaRefPrefix = "#/components/schemas/";
    private const string DefsRefPrefix = "#/$defs/";

    /// <summary>
    /// Builds a compiled <see cref="JsonSchema" /> for <paramref name="rootSchema" /> (typically a
    /// path operation's response schema node, which may itself be a bare <c>$ref</c> or a
    /// <c>oneOf</c> of references), with every schema in <paramref name="componentSchemas" /> copied
    /// into <c>$defs</c> so references resolve.
    /// </summary>
    public static JsonSchema BuildJsonSchema(JsonObject componentSchemas, JsonNode rootSchema)
    {
        JsonObject defs = [];
        foreach ((string name, JsonNode? schemaNode) in componentSchemas)
        {
            defs[name] = Normalize(schemaNode!.DeepClone());
        }

        JsonObject root = (JsonObject)Normalize(rootSchema.DeepClone());
        root["$defs"] = defs;

        return JsonSchema.FromText(root.ToJsonString());
    }

    /// <summary>
    /// Recursively rewrites <c>$ref</c> targets and converts <c>nullable: true</c> into a JSON
    /// Schema-native shape. Exposed for the normalizer's own negative-control test.
    /// </summary>
    public static JsonNode Normalize(JsonNode node)
    {
        return node switch
        {
            JsonObject obj => NormalizeObject(obj),
            JsonArray array => NormalizeArray(array),
            _ => node,
        };
    }

    private static JsonArray NormalizeArray(JsonArray array)
    {
        JsonArray result = [];
        foreach (JsonNode? item in array)
        {
            result.Add(item is null ? null : Normalize(item.DeepClone()));
        }
        return result;
    }

    private static JsonNode NormalizeObject(JsonObject obj)
    {
        RewriteRefInPlace(obj);

        bool isNullable = obj["nullable"]?.GetValue<bool>() == true;
        obj.Remove("nullable");

        if (
            isNullable
            && obj["allOf"] is JsonArray allOf
            && allOf.Count == 1
            && allOf[0] is JsonObject soleMember
            && soleMember.ContainsKey("$ref")
        )
        {
            JsonObject refNode = (JsonObject)Normalize(soleMember.DeepClone());
            obj.Remove("allOf");
            obj["anyOf"] = new JsonArray(refNode, new JsonObject { ["type"] = "null" });
            isNullable = false;
        }

        NormalizeChildMap(obj, "properties");
        NormalizeChild(obj, "items");
        NormalizeChildArray(obj, "allOf");
        NormalizeChildArray(obj, "oneOf");
        NormalizeChildArray(obj, "anyOf");
        if (obj["additionalProperties"] is JsonObject)
        {
            NormalizeChild(obj, "additionalProperties");
        }

        if (isNullable)
        {
            ApplyNullable(obj);
        }

        return obj;
    }

    private static void RewriteRefInPlace(JsonObject obj)
    {
        if (
            obj["$ref"] is JsonValue refValue
            && refValue.TryGetValue(out string? reference)
            && reference.StartsWith(ComponentSchemaRefPrefix, StringComparison.Ordinal)
        )
        {
            obj["$ref"] = DefsRefPrefix + reference[ComponentSchemaRefPrefix.Length..];
        }
    }

    private static void ApplyNullable(JsonObject obj)
    {
        if (obj["type"] is JsonValue typeValue && typeValue.TryGetValue(out string? typeString))
        {
            obj["type"] = new JsonArray(typeString, "null");
            return;
        }

        if (obj["$ref"] is not null)
        {
            JsonObject refNode = new() { ["$ref"] = obj["$ref"]!.DeepClone() };
            obj.Remove("$ref");
            obj["anyOf"] = new JsonArray(refNode, new JsonObject { ["type"] = "null" });
            return;
        }

        obj["type"] = "null";
    }

    private static void NormalizeChildMap(JsonObject obj, string propertyName)
    {
        if (obj[propertyName] is not JsonObject map)
        {
            return;
        }

        foreach (string key in map.Select(pair => pair.Key).ToList())
        {
            map[key] = Normalize(map[key]!.DeepClone());
        }
    }

    private static void NormalizeChild(JsonObject obj, string propertyName)
    {
        if (obj[propertyName] is JsonNode child)
        {
            obj[propertyName] = Normalize(child.DeepClone());
        }
    }

    private static void NormalizeChildArray(JsonObject obj, string propertyName)
    {
        if (obj[propertyName] is JsonArray array)
        {
            obj[propertyName] = NormalizeArray(array);
        }
    }
}
