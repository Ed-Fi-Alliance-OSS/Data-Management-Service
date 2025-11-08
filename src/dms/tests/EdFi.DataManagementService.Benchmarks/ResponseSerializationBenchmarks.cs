// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using BenchmarkDotNet.Attributes;

namespace EdFi.DataManagementService.Benchmarks;

[MemoryDiagnoser]
public class ResponseSerializationBenchmarks
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly JsonNode _node = JsonNode.Parse(
        """{"items":[{"id":1,"name":"alpha"},{"id":2,"name":"beta"}]}"""
    )!;

    [Benchmark]
    public string SerializeWithJsonSerializer() => JsonSerializer.Serialize(_node, SerializerOptions);

    [Benchmark]
    public byte[] SerializeWithUtf8JsonWriter()
    {
        using var buffer = new MemoryStream();
        using var writer = new Utf8JsonWriter(
            buffer,
            new JsonWriterOptions { Encoder = SerializerOptions.Encoder }
        );
        _node.WriteTo(writer);
        writer.Flush();
        return buffer.ToArray();
    }
}
