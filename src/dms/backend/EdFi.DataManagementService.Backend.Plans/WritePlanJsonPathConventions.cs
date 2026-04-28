// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.RelationalModel.Schema;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Deterministic JSON-path conventions used by write-plan compilation.
/// </summary>
public static class WritePlanJsonPathConventions
{
    /// <summary>
    /// Derives a table-scope-relative JSON path by stripping the table scope prefix from an absolute source path.
    /// </summary>
    /// <param name="jsonScope">Absolute table scope path.</param>
    /// <param name="sourceJsonPath">Absolute source path for a column.</param>
    /// <returns>A canonical path relative to <paramref name="jsonScope"/>.</returns>
    public static JsonPathExpression DeriveScopeRelativePath(
        JsonPathExpression jsonScope,
        JsonPathExpression sourceJsonPath
    )
    {
        if (!IsPrefixOf(jsonScope.Segments, sourceJsonPath.Segments))
        {
            throw new InvalidOperationException(
                $"Cannot derive scope-relative path for source '{sourceJsonPath.Canonical}': "
                    + $"scope '{jsonScope.Canonical}' is not a prefix."
            );
        }

        var relativeSegments = sourceJsonPath.Segments.Skip(jsonScope.Segments.Count).ToArray();

        if (Array.Exists(relativeSegments, segment => segment is JsonPathSegment.AnyArrayElement))
        {
            throw new InvalidOperationException(
                $"Cannot derive scope-relative path for source '{sourceJsonPath.Canonical}' under "
                    + $"scope '{jsonScope.Canonical}': stripped path contains '[*]'."
            );
        }

        return JsonPathExpressionCompiler.FromSegments(relativeSegments);
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="prefix"/> matches the left-most path segments.
    /// </summary>
    private static bool IsPrefixOf(IReadOnlyList<JsonPathSegment> prefix, IReadOnlyList<JsonPathSegment> path)
    {
        if (prefix.Count > path.Count)
        {
            return false;
        }

        for (var index = 0; index < prefix.Count; index++)
        {
            var prefixSegment = prefix[index];
            var pathSegment = path[index];

            if (prefixSegment.GetType() != pathSegment.GetType())
            {
                return false;
            }

            if (
                prefixSegment is JsonPathSegment.Property prefixProperty
                && pathSegment is JsonPathSegment.Property pathProperty
                && !string.Equals(prefixProperty.Name, pathProperty.Name, StringComparison.Ordinal)
            )
            {
                return false;
            }
        }

        return true;
    }
}
