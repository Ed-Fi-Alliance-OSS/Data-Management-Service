// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Security.Cryptography;
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
public static class CanonicalJsonSerializer
{
    private const int MaxCachedPropertyNames = 8192;
    private static readonly JavaScriptEncoder JsonEncoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
    private static readonly ConcurrentDictionary<string, JsonEncodedText> EncodedPropertyNameCache = new(
        StringComparer.Ordinal
    );

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = false,
        Encoder = JsonEncoder,
    };

    /// <summary>
    /// Serializes a JsonNode to canonical UTF-8 bytes.
    /// </summary>
    /// <param name="node">The JSON node to serialize.</param>
    /// <returns>UTF-8 encoded bytes of the canonical JSON representation.</returns>
    public static byte[] SerializeToUtf8Bytes(JsonNode? node)
    {
        using var stream = new MemoryStream();

        SerializeToStream(stream, node);

        return stream.ToArray();
    }

    /// <summary>
    /// Serializes a JsonNode to a stream as canonical UTF-8 JSON.
    /// </summary>
    /// <param name="stream">The stream that receives canonical UTF-8 bytes.</param>
    /// <param name="node">The JSON node to serialize.</param>
    public static void SerializeToStream(Stream stream, JsonNode? node)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var writer = new Utf8JsonWriter(stream, WriterOptions);

        WriteCanonicalNode(writer, node);

        writer.Flush();
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
    /// Computes the SHA-256 hash of a JsonNode's canonical UTF-8 representation.
    /// </summary>
    /// <param name="node">The JSON node to hash.</param>
    /// <returns>The SHA-256 hash bytes.</returns>
    public static byte[] ComputeSha256Hash(JsonNode? node)
    {
        return ComputeSha256HashCore(node, propertyNamesToSkip: null);
    }

    internal static byte[] ComputeSha256HashExcludingPropertyNames(
        JsonNode? node,
        FrozenSet<string> propertyNamesToSkip
    )
    {
        ArgumentNullException.ThrowIfNull(propertyNamesToSkip);

        return ComputeSha256HashCore(node, propertyNamesToSkip);
    }

    private static byte[] ComputeSha256HashCore(JsonNode? node, FrozenSet<string>? propertyNamesToSkip)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var stream = new IncrementalHashWriteStream(hash);
        using var writer = new Utf8JsonWriter(stream, WriterOptions);

        WriteCanonicalNode(writer, node, propertyNamesToSkip);
        writer.Flush();

        return hash.GetHashAndReset();
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

    private static void WriteCanonicalNode(
        Utf8JsonWriter writer,
        JsonNode? node,
        FrozenSet<string>? propertyNamesToSkip = null
    )
    {
        switch (node)
        {
            case null:
                writer.WriteNullValue();
                break;
            case JsonObject jsonObject:
                WriteCanonicalObject(writer, jsonObject, propertyNamesToSkip);
                break;
            case JsonArray jsonArray:
                WriteCanonicalArray(writer, jsonArray, propertyNamesToSkip);
                break;
            case JsonValue jsonValue:
                jsonValue.WriteTo(writer);
                break;
            default:
                node.WriteTo(writer);
                break;
        }
    }

    private static void WriteCanonicalObject(
        Utf8JsonWriter writer,
        JsonObject jsonObject,
        FrozenSet<string>? propertyNamesToSkip
    )
    {
        writer.WriteStartObject();

        if (jsonObject.Count == 0)
        {
            writer.WriteEndObject();
            return;
        }

        var properties = RentSortedProperties(jsonObject, propertyNamesToSkip, out var propertyCount);

        try
        {
            for (var index = 0; index < propertyCount; index++)
            {
                var (propertyName, propertyValue) = properties[index];

                writer.WritePropertyName(GetEncodedPropertyName(propertyName));
                WriteCanonicalNode(writer, propertyValue, propertyNamesToSkip);
            }
        }
        finally
        {
            ReturnProperties(properties, propertyCount);
        }

        writer.WriteEndObject();
    }

    private static void WriteCanonicalArray(
        Utf8JsonWriter writer,
        JsonArray jsonArray,
        FrozenSet<string>? propertyNamesToSkip
    )
    {
        writer.WriteStartArray();

        // Preserve element order exactly
        foreach (var item in jsonArray)
        {
            WriteCanonicalNode(writer, item, propertyNamesToSkip);
        }

        writer.WriteEndArray();
    }

    private static JsonEncodedText GetEncodedPropertyName(string propertyName)
    {
        if (EncodedPropertyNameCache.TryGetValue(propertyName, out var encodedPropertyName))
        {
            return encodedPropertyName;
        }

        if (EncodedPropertyNameCache.Count >= MaxCachedPropertyNames)
        {
            return JsonEncodedText.Encode(propertyName, JsonEncoder);
        }

        return EncodedPropertyNameCache.GetOrAdd(
            propertyName,
            static name => JsonEncodedText.Encode(name, JsonEncoder)
        );
    }

    private static KeyValuePair<string, JsonNode?>[] RentSortedProperties(
        JsonObject jsonObject,
        FrozenSet<string>? propertyNamesToSkip,
        out int propertyCount
    )
    {
        var properties = ArrayPool<KeyValuePair<string, JsonNode?>>.Shared.Rent(jsonObject.Count);
        propertyCount = 0;

        if (propertyNamesToSkip is null)
        {
            foreach (var property in jsonObject)
            {
                properties[propertyCount++] = property;
            }
        }
        else
        {
            foreach (var property in jsonObject)
            {
                if (propertyNamesToSkip.Contains(property.Key))
                {
                    continue;
                }

                properties[propertyCount++] = property;
            }
        }

        Array.Sort(properties, 0, propertyCount, JsonObjectPropertyComparer.Instance);

        return properties;
    }

    private static void ReturnProperties(KeyValuePair<string, JsonNode?>[] properties, int propertyCount)
    {
        Array.Clear(properties, 0, propertyCount);
        ArrayPool<KeyValuePair<string, JsonNode?>>.Shared.Return(properties);
    }

    private sealed class JsonObjectPropertyComparer : IComparer<KeyValuePair<string, JsonNode?>>
    {
        public static readonly JsonObjectPropertyComparer Instance = new();

        public int Compare(KeyValuePair<string, JsonNode?> x, KeyValuePair<string, JsonNode?> y)
        {
            return string.CompareOrdinal(x.Key, y.Key);
        }
    }

    private sealed class IncrementalHashWriteStream(IncrementalHash hash) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            hash.AppendData(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            hash.AppendData(buffer);
        }
    }
}
