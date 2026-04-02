// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Backend;

internal static class RelationalJsonPathSupport
{
    public static string CombineRestrictedCanonical(
        JsonPathExpression scopePath,
        JsonPathExpression relativePath
    )
    {
        JsonPathSegment[] combinedSegments =
        [
            .. GetRestrictedSegments(scopePath),
            .. GetRestrictedSegments(relativePath),
        ];

        return BuildCanonical(combinedSegments);
    }

    public static IReadOnlyList<JsonPathSegment> GetRestrictedSegments(JsonPathExpression path)
    {
        if (path.Canonical == "$")
        {
            return [];
        }

        return path.Segments.Count > 0 ? path.Segments : ParseRestrictedCanonical(path.Canonical);
    }

    public static string BuildCanonical(IReadOnlyList<JsonPathSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);

        return string.Create(
            CalculateCanonicalLength(segments),
            segments,
            static (buffer, state) =>
            {
                buffer[0] = '$';
                var index = 1;

                foreach (var segment in state)
                {
                    switch (segment)
                    {
                        case JsonPathSegment.Property property:
                            buffer[index++] = '.';
                            property.Name.AsSpan().CopyTo(buffer[index..]);
                            index += property.Name.Length;
                            break;
                        case JsonPathSegment.AnyArrayElement:
                            "[*]".AsSpan().CopyTo(buffer[index..]);
                            index += 3;
                            break;
                        default:
                            throw new InvalidOperationException(
                                $"Restricted JSONPath segment '{segment.GetType().Name}' is not supported."
                            );
                    }
                }
            }
        );
    }

    public static ParsedConcretePath ParseConcretePath(JsonPath concretePath)
    {
        var path = concretePath.Value;

        if (string.IsNullOrWhiteSpace(path) || path[0] != '$')
        {
            throw CreateConcretePathException(path);
        }

        StringBuilder wildcardPath = new(path.Length);
        List<int> ordinalPath = [];
        wildcardPath.Append('$');

        var index = 1;

        while (index < path.Length)
        {
            var current = path[index];

            if (current == '.')
            {
                wildcardPath.Append(current);
                index = AppendProperty(path, index, wildcardPath, CreateConcretePathException);
                continue;
            }

            if (current == '[')
            {
                index = AppendArrayOrdinal(path, index, wildcardPath, ordinalPath);
                continue;
            }

            throw CreateConcretePathException(path);
        }

        return new ParsedConcretePath(wildcardPath.ToString(), [.. ordinalPath]);
    }

    private static JsonPathSegment[] ParseRestrictedCanonical(string canonicalPath)
    {
        if (string.IsNullOrWhiteSpace(canonicalPath) || canonicalPath[0] != '$')
        {
            throw CreateRestrictedPathException(canonicalPath);
        }

        List<JsonPathSegment> segments = [];
        var index = 1;

        while (index < canonicalPath.Length)
        {
            switch (canonicalPath[index])
            {
                case '.':
                    index = AppendProperty(canonicalPath, index, segments, CreateRestrictedPathException);
                    break;
                case '[' when IsArrayWildcard(canonicalPath, index):
                    segments.Add(new JsonPathSegment.AnyArrayElement());
                    index += 3;
                    break;
                default:
                    throw CreateRestrictedPathException(canonicalPath);
            }
        }

        return [.. segments];
    }

    private static int AppendProperty(
        string path,
        int dotIndex,
        ICollection<JsonPathSegment> segments,
        Func<string, InvalidOperationException> exceptionFactory
    )
    {
        var startIndex = dotIndex + 1;
        var index = startIndex;

        while (index < path.Length && path[index] is not ('.' or '['))
        {
            index++;
        }

        if (index == startIndex)
        {
            throw exceptionFactory(path);
        }

        segments.Add(new JsonPathSegment.Property(path[startIndex..index]));

        return index;
    }

    private static int AppendProperty(
        string path,
        int dotIndex,
        StringBuilder wildcardPath,
        Func<string, InvalidOperationException> exceptionFactory
    )
    {
        var startIndex = dotIndex + 1;
        var index = startIndex;

        while (index < path.Length && path[index] is not ('.' or '['))
        {
            index++;
        }

        if (index == startIndex)
        {
            throw exceptionFactory(path);
        }

        wildcardPath.Append(path.AsSpan(startIndex, index - startIndex));

        return index;
    }

    private static int AppendArrayOrdinal(
        string path,
        int openBracketIndex,
        StringBuilder wildcardPath,
        List<int> ordinalPath
    )
    {
        var closeBracketIndex = path.IndexOf(']', openBracketIndex + 1);

        if (closeBracketIndex < 0)
        {
            throw CreateConcretePathException(path);
        }

        var token = path.AsSpan(openBracketIndex + 1, closeBracketIndex - openBracketIndex - 1);

        if (!int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var ordinal))
        {
            throw CreateConcretePathException(path);
        }

        ordinalPath.Add(ordinal);
        wildcardPath.Append("[*]");

        return closeBracketIndex + 1;
    }

    private static bool IsArrayWildcard(string canonicalPath, int openBracketIndex)
    {
        return openBracketIndex + 2 < canonicalPath.Length
            && canonicalPath[openBracketIndex + 1] == '*'
            && canonicalPath[openBracketIndex + 2] == ']';
    }

    private static int CalculateCanonicalLength(IReadOnlyList<JsonPathSegment> segments)
    {
        var length = 1;

        for (var index = 0; index < segments.Count; index++)
        {
            length += segments[index] switch
            {
                JsonPathSegment.Property property => property.Name.Length + 1,
                JsonPathSegment.AnyArrayElement => 3,
                _ => throw new InvalidOperationException(
                    $"Restricted JSONPath segment '{segments[index].GetType().Name}' is not supported."
                ),
            };
        }

        return length;
    }

    private static InvalidOperationException CreateRestrictedPathException(string canonicalPath)
    {
        return new InvalidOperationException($"Restricted JSONPath '{canonicalPath}' is not canonical.");
    }

    private static InvalidOperationException CreateConcretePathException(string path)
    {
        return new InvalidOperationException(
            $"Resolved reference path '{path}' is not a canonical JSONPath."
        );
    }

    internal readonly record struct ParsedConcretePath(string WildcardPath, int[] OrdinalPath);
}
