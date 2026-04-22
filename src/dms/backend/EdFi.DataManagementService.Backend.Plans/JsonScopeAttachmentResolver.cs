// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Plans;

internal static class JsonScopeAttachmentResolver
{
    public static IReadOnlyList<JsonPathSegment> ResolveExpectedImmediateParentScopeSegmentsOrThrow(
        JsonPathExpression childScope,
        DbTableKind childKind
    )
    {
        return childKind switch
        {
            DbTableKind.Collection or DbTableKind.ExtensionCollection =>
                ResolveImmediateAncestorScopeSegmentsOrThrow(childScope),
            DbTableKind.RootExtension => [],
            DbTableKind.CollectionExtensionScope =>
                ResolveBaseScopeSegmentsForCollectionExtensionScopeOrThrow(childScope),
            _ => throw new InvalidOperationException(
                $"Cannot resolve expected immediate parent scope for table kind '{childKind}' "
                    + $"at scope '{childScope.Canonical}'."
            ),
        };
    }

    public static JsonPathSegment[] ResolveRelativeAttachmentSegmentsOrThrow(
        JsonPathExpression parentScope,
        JsonPathExpression childScope,
        DbTableKind childKind
    )
    {
        if (TryResolveRelativeAttachmentSegments(parentScope, childScope, childKind, out var segments))
        {
            return segments;
        }

        throw new InvalidOperationException(
            $"Cannot resolve scope attachment from child scope '{childScope.Canonical}' "
                + $"relative to parent scope '{parentScope.Canonical}' for table kind '{childKind}'."
        );
    }

    public static bool TryResolveRelativeAttachmentSegments(
        JsonPathExpression parentScope,
        JsonPathExpression childScope,
        DbTableKind childKind,
        out JsonPathSegment[] relativeSegments
    )
    {
        var parentSegments = GetRestrictedSegments(parentScope);
        var childSegments = GetRestrictedSegments(childScope);

        if (IsScopePrefix(parentSegments, childSegments))
        {
            relativeSegments = [.. childSegments.Skip(parentSegments.Count)];
            return true;
        }

        if (childKind is DbTableKind.CollectionExtensionScope)
        {
            var baseScopeSegments = ResolveBaseScopeSegmentsForCollectionExtensionScopeOrThrow(childScope);

            if (AreSegmentsEqual(parentSegments, baseScopeSegments))
            {
                relativeSegments = [childSegments[^2], childSegments[^1]];
                return true;
            }
        }

        relativeSegments = [];
        return false;
    }

    public static bool AreSegmentsEqual(
        IReadOnlyList<JsonPathSegment> left,
        IReadOnlyList<JsonPathSegment> right
    )
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (!AreSegmentsEqual(left[index], right[index]))
            {
                return false;
            }
        }

        return true;
    }

    public static string FormatScope(IReadOnlyList<JsonPathSegment> segments)
    {
        if (segments.Count == 0)
        {
            return "$";
        }

        var canonical = "$";

        foreach (var segment in segments)
        {
            canonical += segment switch
            {
                JsonPathSegment.Property property => $".{property.Name}",
                JsonPathSegment.AnyArrayElement => "[*]",
                _ => throw new InvalidOperationException("Unsupported restricted JsonPath segment."),
            };
        }

        return canonical;
    }

    public static IReadOnlyList<JsonPathSegment> GetRestrictedSegments(JsonPathExpression path)
    {
        if (path.Canonical == "$")
        {
            return [];
        }

        if (path.Segments.Count > 0)
        {
            return path.Segments;
        }

        return ParseRestrictedCanonical(path.Canonical);
    }

    private static IReadOnlyList<JsonPathSegment> ResolveImmediateAncestorScopeSegmentsOrThrow(
        JsonPathExpression jsonScope
    )
    {
        var childSegments = GetRestrictedSegments(jsonScope);

        for (var length = childSegments.Count - 1; length >= 0; length--)
        {
            var candidateSegments = childSegments.Take(length).ToArray();

            if (IsPotentialTableScope(candidateSegments))
            {
                return candidateSegments;
            }
        }

        throw new InvalidOperationException(
            $"Cannot resolve immediate parent scope: scope '{jsonScope.Canonical}' "
                + "does not have a valid ancestor table scope."
        );
    }

    private static IReadOnlyList<JsonPathSegment> ResolveBaseScopeSegmentsForCollectionExtensionScopeOrThrow(
        JsonPathExpression jsonScope
    )
    {
        var segments = GetRestrictedSegments(jsonScope);

        if (
            segments.Count < 2
            || segments[^2] is not JsonPathSegment.Property { Name: "_ext" }
            || segments[^1] is not JsonPathSegment.Property trailingProject
        )
        {
            throw new InvalidOperationException(
                $"Cannot resolve collection extension attachment for scope '{jsonScope.Canonical}': "
                    + "scope does not end in an '_ext.{project}' attachment segment."
            );
        }

        var baseScopeSegments = segments.Take(segments.Count - 2).ToArray();

        if (
            baseScopeSegments.Length >= 2
            && baseScopeSegments[0] is JsonPathSegment.Property { Name: "_ext" }
            && baseScopeSegments[1] is JsonPathSegment.Property leadingProject
        )
        {
            if (!string.Equals(leadingProject.Name, trailingProject.Name, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Cannot resolve collection extension attachment for scope '{jsonScope.Canonical}': "
                        + "scope uses mismatched leading and trailing extension project segments."
                );
            }

            baseScopeSegments = baseScopeSegments.Skip(2).ToArray();
        }

        if (baseScopeSegments.Length == 0 || baseScopeSegments[^1] is not JsonPathSegment.AnyArrayElement)
        {
            throw new InvalidOperationException(
                $"Cannot resolve collection extension attachment for scope '{jsonScope.Canonical}': "
                    + "scope does not map to a collection base scope."
            );
        }

        return baseScopeSegments;
    }

    private static bool IsPotentialTableScope(IReadOnlyList<JsonPathSegment> segments)
    {
        if (segments.Count == 0)
        {
            return true;
        }

        return segments[^1] is JsonPathSegment.AnyArrayElement || EndsWithExtensionScope(segments);
    }

    private static bool EndsWithExtensionScope(IReadOnlyList<JsonPathSegment> segments)
    {
        return segments.Count >= 2
            && segments[^2] is JsonPathSegment.Property { Name: "_ext" }
            && segments[^1] is JsonPathSegment.Property { Name.Length: > 0 };
    }

    private static bool AreSegmentsEqual(JsonPathSegment left, JsonPathSegment right)
    {
        if (left.GetType() != right.GetType())
        {
            return false;
        }

        return left switch
        {
            JsonPathSegment.Property leftProperty when right is JsonPathSegment.Property rightProperty =>
                string.Equals(leftProperty.Name, rightProperty.Name, StringComparison.Ordinal),
            JsonPathSegment.AnyArrayElement when right is JsonPathSegment.AnyArrayElement => true,
            _ => false,
        };
    }

    private static bool IsScopePrefix(
        IReadOnlyList<JsonPathSegment> parentSegments,
        IReadOnlyList<JsonPathSegment> childSegments
    )
    {
        if (parentSegments.Count > childSegments.Count)
        {
            return false;
        }

        for (var index = 0; index < parentSegments.Count; index++)
        {
            if (!AreSegmentsEqual(parentSegments[index], childSegments[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static JsonPathSegment[] ParseRestrictedCanonical(string canonicalPath)
    {
        if (string.IsNullOrWhiteSpace(canonicalPath) || canonicalPath[0] != '$')
        {
            throw new InvalidOperationException(
                $"Restricted JSONPath '{canonicalPath}' must start with '$'."
            );
        }

        List<JsonPathSegment> segments = [];
        var index = 1;

        while (index < canonicalPath.Length)
        {
            var current = canonicalPath[index];

            if (current == '.')
            {
                index = ParsePropertySegment(canonicalPath, index + 1, segments);
                continue;
            }

            if (current == '[' && IsArrayWildcard(canonicalPath, index))
            {
                segments.Add(new JsonPathSegment.AnyArrayElement());
                index += 3;
                continue;
            }

            throw new InvalidOperationException(
                $"Restricted JSONPath '{canonicalPath}' contains unsupported token at position {index}."
            );
        }

        return [.. segments];
    }

    private static int ParsePropertySegment(string path, int index, ICollection<JsonPathSegment> segments)
    {
        var startIndex = index;

        while (index < path.Length)
        {
            var current = path[index];

            if (current == '.' || current == '[')
            {
                break;
            }

            index++;
        }

        if (index == startIndex)
        {
            throw new InvalidOperationException(
                $"Restricted JSONPath '{path}' contains an empty property segment."
            );
        }

        segments.Add(new JsonPathSegment.Property(path[startIndex..index]));

        return index;
    }

    private static bool IsArrayWildcard(string path, int openBracketIndex) =>
        openBracketIndex + 2 < path.Length
        && path[openBracketIndex] == '['
        && path[openBracketIndex + 1] == '*'
        && path[openBracketIndex + 2] == ']';
}
