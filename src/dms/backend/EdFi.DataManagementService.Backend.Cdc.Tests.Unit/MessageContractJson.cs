// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Numerics;
using System.Text.Json;
using FluentAssertions;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Unit;

/// <summary>Semantic JSON equality without floating-point or decimal precision limits.</summary>
public static class MessageContractJson
{
    public static void ShouldEqual(JsonElement actual, JsonElement expected) =>
        Equivalent(actual, expected).Should().BeTrue("complete JSON values must match (payloads redacted)");

    public static bool Equivalent(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind)
        {
            return false;
        }

        switch (left.ValueKind)
        {
            case JsonValueKind.Object:
                JsonProperty[] leftProperties = left.EnumerateObject().ToArray();
                JsonProperty[] rightProperties = right.EnumerateObject().ToArray();
                return leftProperties.Length == rightProperties.Length
                    && leftProperties.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count()
                        == leftProperties.Length
                    && rightProperties.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count()
                        == rightProperties.Length
                    && Array.TrueForAll(
                        leftProperties,
                        p => right.TryGetProperty(p.Name, out JsonElement value) && Equivalent(p.Value, value)
                    );
            case JsonValueKind.Array:
                return left.GetArrayLength() == right.GetArrayLength()
                    && left.EnumerateArray()
                        .Zip(right.EnumerateArray())
                        .All(pair => Equivalent(pair.First, pair.Second));
            case JsonValueKind.Number:
                return NormalizeNumber(left.GetRawText()) == NormalizeNumber(right.GetRawText());
            case JsonValueKind.String:
                return left.GetString() == right.GetString();
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                return true;
            default:
                return false;
        }
    }

    // Keep significant digits as text and the decimal exponent as BigInteger. Even an
    // enormous exponent needs no expansion, rounding, or allocation of exponent-sized buffers.
    private static (string Digits, BigInteger Exponent) NormalizeNumber(string number)
    {
        int exponentIndex = number.IndexOfAny(['e', 'E']);
        string mantissa = exponentIndex < 0 ? number : number[..exponentIndex];
        BigInteger exponent =
            exponentIndex < 0
                ? BigInteger.Zero
                : BigInteger.Parse(number[(exponentIndex + 1)..], CultureInfo.InvariantCulture);
        int decimalIndex = mantissa.IndexOf('.');
        if (decimalIndex >= 0)
        {
            exponent -= mantissa.Length - decimalIndex - 1;
        }

        bool negative = mantissa.StartsWith('-');
        string digits = mantissa.TrimStart('-').Replace(".", "", StringComparison.Ordinal).TrimStart('0');
        if (digits.Length == 0)
        {
            return ("0", BigInteger.Zero);
        }

        string significant = digits.TrimEnd('0');
        exponent += digits.Length - significant.Length;
        return (negative ? "-" + significant : significant, exponent);
    }
}
