// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Core.Utilities;

/// <summary>
/// Provides deterministic canonical JSON serialization.
///
/// Canonical form ensures:
/// - Object properties are recursively sorted by name using ordinal string comparison
/// - Arrays preserve element order exactly
/// - Output is minified (no insignificant whitespace)
/// - Output is UTF-8 encoded (no BOM)
///
/// This guarantees byte-for-byte identical output for semantically equivalent JSON,
/// enabling deterministic hashing.
/// </summary>
internal static class CanonicalJsonSerializer
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Serializes a JsonNode to canonical UTF-8 bytes.
    /// </summary>
    /// <param name="node">The JSON node to serialize.</param>
    /// <returns>UTF-8 encoded bytes of the canonical JSON representation.</returns>
    public static byte[] SerializeToUtf8Bytes(JsonNode? node)
    {
        if (node == null)
        {
            return "null"u8.ToArray();
        }

        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, WriterOptions);

        WriteCanonicalNode(writer, node);

        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>
    /// Serializes a JsonNode to a canonical JSON string.
    /// </summary>
    /// <param name="node">The JSON node to serialize.</param>
    /// <returns>The canonical JSON string representation.</returns>
    public static string SerializeToString(JsonNode? node)
    {
        return Encoding.UTF8.GetString(SerializeToUtf8Bytes(node));
    }

    /// <summary>
    /// Creates a deep clone of a JsonNode with all object properties sorted canonically.
    /// Useful when you need to compare or inspect the canonical structure.
    /// </summary>
    /// <param name="node">The JSON node to canonicalize.</param>
    /// <returns>A new JsonNode with sorted properties.</returns>
    public static JsonNode? Canonicalize(JsonNode? node)
    {
        return node switch
        {
            null => null,
            JsonObject jsonObject => CanonicalizeObject(jsonObject),
            JsonArray jsonArray => CanonicalizeArray(jsonArray),
            JsonValue jsonValue => jsonValue.DeepClone(),
            _ => node.DeepClone(),
        };
    }

    private static JsonObject CanonicalizeObject(JsonObject jsonObject)
    {
        var result = new JsonObject();

        // Sort properties by key using ordinal comparison (ASCII/Unicode code point order)
        foreach (var kvp in jsonObject.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            result[kvp.Key] = Canonicalize(kvp.Value);
        }

        return result;
    }

    private static JsonArray CanonicalizeArray(JsonArray jsonArray)
    {
        var result = new JsonArray();

        // Preserve element order exactly
        foreach (var item in jsonArray)
        {
            result.Add(Canonicalize(item));
        }

        return result;
    }

    private static void WriteCanonicalNode(Utf8JsonWriter writer, JsonNode node)
    {
        switch (node)
        {
            case JsonObject jsonObject:
                WriteCanonicalObject(writer, jsonObject);
                break;
            case JsonArray jsonArray:
                WriteCanonicalArray(writer, jsonArray);
                break;
            case JsonValue jsonValue:
                jsonValue.WriteTo(writer);
                break;
            default:
                node.WriteTo(writer);
                break;
        }
    }

    private static void WriteCanonicalObject(Utf8JsonWriter writer, JsonObject jsonObject)
    {
        writer.WriteStartObject();

        // Sort properties by key using ordinal comparison
        foreach (var kvp in jsonObject.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            writer.WritePropertyName(kvp.Key);
            if (kvp.Value == null)
            {
                writer.WriteNullValue();
            }
            else
            {
                WriteCanonicalNode(writer, kvp.Value);
            }
        }

        writer.WriteEndObject();
    }

    private static void WriteCanonicalArray(Utf8JsonWriter writer, JsonArray jsonArray)
    {
        writer.WriteStartArray();

        // Preserve element order exactly
        foreach (var item in jsonArray)
        {
            if (item == null)
            {
                writer.WriteNullValue();
            }
            else
            {
                WriteCanonicalNode(writer, item);
            }
        }

        writer.WriteEndArray();
    }
}
