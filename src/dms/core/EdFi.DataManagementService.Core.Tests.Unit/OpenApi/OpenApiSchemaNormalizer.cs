// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using Json.Schema;

namespace EdFi.DataManagementService.Core.Tests.Unit.OpenApi;

/// <summary>
/// Converts an OpenAPI 3.0 component schema into a JSON Schema draft <c>JsonSchema.Net</c> accepts.
/// OpenAPI's <c>nullable: true</c> has no JSON Schema equivalent, so a schema typed
/// <c>{"type": "string", "nullable": true}</c> becomes <c>{"type": ["string", "null"]}</c>. OpenAPI 3.0.3
/// gives <c>nullable</c> effect only beside an explicit <c>type</c>, so a <c>nullable</c> with no
/// <c>type</c>, such as one beside an <c>allOf</c> or a <c>$ref</c>, is dropped and admits no
/// <c>null</c>, exactly as a strict OpenAPI 3.0.3 validator treats it. Every <c>$ref</c> target
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
    /// Recursively rewrites <c>$ref</c> targets and converts a typed <c>nullable: true</c> into a JSON
    /// Schema-native shape. Exposed for the normalizer's own negative-control tests.
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
        }
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
