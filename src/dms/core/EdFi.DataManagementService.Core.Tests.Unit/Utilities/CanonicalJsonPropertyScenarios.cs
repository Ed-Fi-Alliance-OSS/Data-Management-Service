// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using CsCheck;

namespace EdFi.DataManagementService.Core.Tests.Unit.Utilities;

internal static class CanonicalJsonPropertyScenarios
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly string[] Texts =
    [
        "",
        "ordinary",
        "雪é",
        "é",
        "e\u0301",
        "😀",
        "\"\\\n\r\t\0",
        "<>&",
        "Z",
        "a",
        "\u2028",
    ];

    internal static Gen<string> Text => Gen.Int[0, Texts.Length - 1].Select(i => Texts[i]);

    // Immutable JSON strings are the scenario descriptions. Every evaluation parses fresh nodes.
    // At most three children at each of three generated levels; no discarded inputs.
    internal static Gen<string> Generate(string shape) =>
        shape switch
        {
            "scalar" => Scalar,
            "object" => Container(2, true),
            "array" => Container(2, false),
            "nested" => Container(2, true).Select(json => $"[{{\"雪\":[{json},null]}},[{json}]]"),
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };

    private static Gen<string> Scalar =>
        from kind in Gen.Int[0, 3]
        from text in Text
        from number in Gen.Int
        select kind switch
        {
            0 => "null",
            1 => number % 2 == 0 ? "true" : "false",
            2 => JsonSerializer.Serialize(text, JsonOptions),
            _ => number.ToString(CultureInfo.InvariantCulture),
        };

    private static Gen<string> Tree(int depth) =>
        depth == 0
            ? Scalar
            : Gen.Int[0, 2].SelectMany(kind => kind == 0 ? Scalar : Container(depth - 1, kind == 1));

    private static Gen<string> Container(int depth, bool isObject) =>
        from names in Text.Array[3]
        from children in Tree(depth).Array[0, 3]
        select isObject
            ? "{"
                + string.Join(
                    ",",
                    children.Select(
                        (child, i) => JsonSerializer.Serialize($"{names[i]}-{i}", JsonOptions) + ":" + child
                    )
                )
                + "}"
            : "[" + string.Join(",", children) + "]";

    // Independent oracle uses JsonDocument and string composition, never the production serializer.
    // Numeric tokens are copied exactly: this is not arithmetic/value-based canonicalization.
    internal static string Expected(string json)
    {
        using var document = JsonDocument.Parse(json);
        return Render(document.RootElement, canonical: true, rotation: 0);
    }

    internal static string Permute(string json, int rotation)
    {
        using var document = JsonDocument.Parse(json);
        return Render(document.RootElement, canonical: false, rotation);
    }

    private static string Render(JsonElement element, bool canonical, int rotation) =>
        element.ValueKind switch
        {
            JsonValueKind.Object => "{"
                + string.Join(
                    ",",
                    Properties(element, canonical, rotation)
                        .Select(p =>
                            JsonSerializer.Serialize(p.Name, JsonOptions)
                            + ":"
                            + Render(p.Value, canonical, rotation + 1)
                        )
                )
                + "}",
            JsonValueKind.Array => "["
                + string.Join(",", element.EnumerateArray().Select(v => Render(v, canonical, rotation + 1)))
                + "]",
            JsonValueKind.String => JsonSerializer.Serialize(element.GetString(), JsonOptions),
            _ => element.GetRawText(),
        };

    private static IEnumerable<JsonProperty> Properties(JsonElement element, bool canonical, int rotation)
    {
        var properties = element.EnumerateObject().ToArray();
        if (canonical)
        {
            return properties.OrderBy(p => p.Name, StringComparer.Ordinal);
        }
        if (properties.Length == 0)
        {
            return properties;
        }
        var offset = rotation % properties.Length;
        return properties.Reverse().Skip(offset).Concat(properties.Reverse().Take(offset));
    }
}
