// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CsCheck;
using EdFi.DataManagementService.Core.Utilities;
using FluentAssertions;
using NUnit.Framework;
using static EdFi.DataManagementService.Core.Tests.Unit.Utilities.CanonicalJsonPropertyScenarios;

namespace EdFi.DataManagementService.Core.Tests.Unit.Utilities;

[TestFixture]
[Category("SchemaHashProperty")]
public class Given_Generated_Canonical_Json
{
    [Test]
    public void It_canonicalizes_recursive_permutations_without_changing_values_or_input(
        [Values("scalar", "object", "array", "nested")] string shape
    )
    {
        (
            from json in Generate(shape)
            from rotation in Gen.Int[0, 20]
            select (Json: json, Rotation: rotation)
        ).Sample(
            scenario =>
            {
                var source = JsonNode.Parse(scenario.Json)!;
                var before = source?.ToJsonString() ?? "null";
                var expected = Encoding.UTF8.GetBytes(Expected(scenario.Json));
                var permuted = JsonNode.Parse(Permute(scenario.Json, scenario.Rotation))!;
                CanonicalJsonSerializer.SerializeToUtf8Bytes(source).Should().Equal(expected);
                CanonicalJsonSerializer.SerializeToUtf8Bytes(permuted).Should().Equal(expected);
                CanonicalJsonSerializer
                    .SerializeToString(source)
                    .Should()
                    .Be(Encoding.UTF8.GetString(expected));
                var canonical = CanonicalJsonSerializer.Canonicalize(source)!;
                (canonical?.ToJsonString(JsonOptions) ?? "null")
                    .Should()
                    .Be(Encoding.UTF8.GetString(expected));
                var twice = CanonicalJsonSerializer.Canonicalize(canonical)!;
                (twice?.ToJsonString(JsonOptions) ?? "null").Should().Be(Encoding.UTF8.GetString(expected));
                AssertStreamAndHash(source!, expected);
                (source?.ToJsonString() ?? "null").Should().Be(before);
                // Prove canonical containers own their children, including deeply nested objects.
                MutateContainers(canonical!);
                (source?.ToJsonString() ?? "null").Should().Be(before);
            },
            iter: 200,
            threads: 1,
            print: s => $"rotation={s.Rotation}, json={s.Json}"
        );
        TestContext.Out.WriteLine($"CsCheck 5.0.1: {shape}: 200 samples");
    }

    [Test]
    public void It_preserves_numeric_spellings_and_array_order(
        [Values("integer", "fraction", "exponent", "negative-zero")] string spelling
    )
    {
        Gen.Int[1, 100000]
            .Sample(
                value =>
                {
                    var digits = value.ToString(CultureInfo.InvariantCulture);
                    var token = spelling switch
                    {
                        "fraction" => digits + ".00",
                        "exponent" => digits + "e+0",
                        "negative-zero" => "-0.00e+0",
                        _ => digits,
                    };
                    // Distinct neighbours make sorting, deduplication, or dropping null observable.
                    var expected = $"[{token},null,\"{digits}\",[{token},false]]";
                    var source = JsonNode.Parse(expected)!;
                    CanonicalJsonSerializer.SerializeToString(source).Should().Be(expected);
                    CanonicalJsonSerializer
                        .Canonicalize(source)!
                        .ToJsonString(JsonOptions)
                        .Should()
                        .Be(expected);
                    AssertStreamAndHash(source, Encoding.UTF8.GetBytes(expected));
                },
                iter: 100,
                threads: 1,
                print: v => $"{spelling}: {v}"
            );
        TestContext.Out.WriteLine($"CsCheck 5.0.1: {spelling}: 100 samples");
    }

    [Test]
    public void It_hashes_exact_canonical_byte_lengths_across_buffer_boundaries(
        [Values(16383, 16384, 16385, 32767, 32768, 32769)] int byteLength,
        [Values(false, true)] bool chunked
    )
    {
        Gen.Int[0, 25]
            .Array[16]
            .Select(letters => new string(letters.Select(i => (char)('a' + i)).ToArray()))
            .Sample(
                text =>
                {
                    // Many small tokens exercise incremental flushing; one large token exercises buffer growth.
                    var prefix = chunked ? "[" + string.Concat(Enumerable.Repeat($"\"{text}\",", 700)) : "[";
                    var expected =
                        prefix + "\"" + new string(text[0], byteLength - prefix.Length - 3) + "\"]";
                    var bytes = Encoding.UTF8.GetBytes(expected);
                    bytes.Should().HaveCount(byteLength);
                    var source = JsonNode.Parse(expected)!;
                    CanonicalJsonSerializer.SerializeToUtf8Bytes(source).Should().Equal(bytes);
                    AssertStreamAndHash(source, bytes);
                },
                iter: 25,
                threads: 1,
                print: text => JsonSerializer.Serialize(text)
            );
        TestContext.Out.WriteLine($"CsCheck 5.0.1: {byteLength} bytes, chunked={chunked}: 25 samples");
    }

    private static void AssertStreamAndHash(JsonNode source, byte[] expected)
    {
        using var stream = new MemoryStream();
        CanonicalJsonSerializer.SerializeToStream(stream, source);
        stream.ToArray().Should().Equal(expected);
        CanonicalJsonSerializer.ComputeSha256Hash(source).Should().Equal(SHA256.HashData(expected));
    }

    private static void MutateContainers(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var child in obj.ToArray())
                {
                    MutateContainers(child.Value!);
                }
                obj["mutated"] = true;
                break;
            case JsonArray array:
                foreach (var child in array)
                {
                    MutateContainers(child!);
                }
                array.Add("mutated");
                break;
        }
    }
}
