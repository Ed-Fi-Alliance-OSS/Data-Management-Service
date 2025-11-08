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
public class QueryStreamingBenchmarks
{
    private readonly JsonArray _documents;

    public QueryStreamingBenchmarks()
    {
        _documents = new JsonArray();
        for (int i = 0; i < 100; i++)
        {
            _documents.Add(new JsonObject { ["id"] = i, ["payload"] = new string('x', 128) });
        }
    }

    [Benchmark]
    public byte[] MaterializeJsonArray()
    {
        return JsonSerializer.SerializeToUtf8Bytes(_documents);
    }

    [Benchmark]
    public byte[] StreamWithUtf8JsonWriter()
    {
        using var output = new MemoryStream();
        using var writer = new Utf8JsonWriter(
            output,
            new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }
        );

        writer.WriteStartArray();
        foreach (JsonNode? node in _documents)
        {
            node?.WriteTo(writer);
        }
        writer.WriteEndArray();
        writer.Flush();
        return output.ToArray();
    }
}
