// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;

namespace EdFi.DataManagementService.Backend.RelationalModel;

/// <summary>
/// Compiles and canonicalizes a constrained JSONPath syntax used throughout relational model derivation.
/// </summary>
/// <remarks>
/// <para>
/// Supported syntax:
/// </para>
/// <list type="bullet">
/// <item><description><c>$</c> as the root.</description></item>
/// <item><description>Property segments via <c>.propertyName</c>.</description></item>
/// <item><description>Array wildcard segments via <c>[*]</c> (must follow a property segment).</description></item>
/// </list>
/// <para>
/// Property names are restricted to letters/digits plus <c>_</c> and <c>-</c>. The output canonical form is
/// stable and is used for deterministic ordering and dictionary keys.
/// </para>
/// </remarks>
public static class JsonPathExpressionCompiler
{
    /// <summary>
    /// Parses a JSONPath string into a canonical <see cref="JsonPathExpression"/>.
    /// </summary>
    /// <param name="jsonPath">The JSONPath string to compile.</param>
    /// <returns>A compiled expression with a canonical string and structured segments.</returns>
    public static JsonPathExpression Compile(string jsonPath)
    {
        if (jsonPath is null)
        {
            throw new ArgumentNullException(nameof(jsonPath));
        }

        if (jsonPath.Length == 0)
        {
            throw new ArgumentException("JsonPath must not be empty.", nameof(jsonPath));
        }

        if (jsonPath[0] != '$')
        {
            throw new ArgumentException("JsonPath must start with '$'.", nameof(jsonPath));
        }

        if (jsonPath.Length == 1)
        {
            return new JsonPathExpression("$", Array.Empty<JsonPathSegment>());
        }

        List<JsonPathSegment> segments = [];
        var index = 1;

        while (index < jsonPath.Length)
        {
            var current = jsonPath[index];
            if (current == '.')
            {
                index++;
                var start = index;
                while (index < jsonPath.Length && jsonPath[index] is not '.' and not '[')
                {
                    var character = jsonPath[index];
                    if (!IsValidPropertyCharacter(character))
                    {
                        throw new ArgumentException(
                            $"JsonPath contains invalid property character '{character}'.",
                            nameof(jsonPath)
                        );
                    }

                    index++;
                }

                if (index == start)
                {
                    throw new ArgumentException(
                        "JsonPath property segments must be non-empty.",
                        nameof(jsonPath)
                    );
                }

                var propertyName = jsonPath[start..index];
                segments.Add(new JsonPathSegment.Property(propertyName));
                continue;
            }

            if (current == '[')
            {
                if (segments.Count == 0 || segments[^1] is not JsonPathSegment.Property)
                {
                    throw new ArgumentException(
                        "JsonPath array wildcards must follow a property segment.",
                        nameof(jsonPath)
                    );
                }

                if (index + 2 >= jsonPath.Length || jsonPath[index + 1] != '*' || jsonPath[index + 2] != ']')
                {
                    throw new ArgumentException(
                        "JsonPath array segments must use the wildcard [*].",
                        nameof(jsonPath)
                    );
                }

                segments.Add(new JsonPathSegment.AnyArrayElement());
                index += 3;
                continue;
            }

            throw new ArgumentException(
                $"JsonPath contains unexpected character '{current}'.",
                nameof(jsonPath)
            );
        }

        var canonical = BuildCanonical(segments);

        return new JsonPathExpression(canonical, segments.ToArray());
    }

    /// <summary>
    /// Creates a canonical <see cref="JsonPathExpression"/> from a sequence of validated segments.
    /// </summary>
    /// <param name="segments">The segments that make up the path.</param>
    public static JsonPathExpression FromSegments(IEnumerable<JsonPathSegment> segments)
    {
        if (segments is null)
        {
            throw new ArgumentNullException(nameof(segments));
        }

        var segmentArray = segments.ToArray();
        ValidateSegments(segmentArray);

        var canonical = BuildCanonical(segmentArray);

        return new JsonPathExpression(canonical, segmentArray);
    }

    /// <summary>
    /// Validates that segment sequences form a well-structured JSONPath (properties and <c>[*]</c> only).
    /// </summary>
    private static void ValidateSegments(IReadOnlyList<JsonPathSegment> segments)
    {
        if (segments.Count == 0)
        {
            return;
        }

        JsonPathSegment? previous = null;
        foreach (var segment in segments)
        {
            if (segment is null)
            {
                throw new ArgumentException(
                    "JsonPath segments cannot contain null values.",
                    nameof(segments)
                );
            }

            if (segment is JsonPathSegment.Property property)
            {
                if (!IsValidPropertyName(property.Name))
                {
                    throw new ArgumentException(
                        $"JsonPath property name '{property.Name}' is invalid.",
                        nameof(segments)
                    );
                }

                previous = segment;
                continue;
            }

            if (segment is JsonPathSegment.AnyArrayElement)
            {
                if (previous is null || previous is JsonPathSegment.AnyArrayElement)
                {
                    throw new ArgumentException(
                        "JsonPath array wildcards must follow a property segment.",
                        nameof(segments)
                    );
                }

                previous = segment;
                continue;
            }

            throw new ArgumentOutOfRangeException(
                nameof(segments),
                segment,
                "Unknown JSONPath segment type."
            );
        }
    }

    /// <summary>
    /// Builds the canonical JSONPath string for a validated segment sequence.
    /// </summary>
    private static string BuildCanonical(IReadOnlyList<JsonPathSegment> segments)
    {
        if (segments.Count == 0)
        {
            return "$";
        }

        var builder = new StringBuilder("$");
        foreach (var segment in segments)
        {
            switch (segment)
            {
                case JsonPathSegment.Property property:
                    builder.Append('.').Append(property.Name);
                    break;
                case JsonPathSegment.AnyArrayElement:
                    builder.Append("[*]");
                    break;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(segments),
                        segment,
                        "Unknown JSONPath segment type."
                    );
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Determines whether a property name contains only allowed characters.
    /// </summary>
    private static bool IsValidPropertyName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        foreach (var character in name)
        {
            if (!IsValidPropertyCharacter(character))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Determines whether an individual character is allowed in a property name.
    /// </summary>
    private static bool IsValidPropertyCharacter(char character)
    {
        return character is '_' or '-' || char.IsLetterOrDigit(character);
    }
}
