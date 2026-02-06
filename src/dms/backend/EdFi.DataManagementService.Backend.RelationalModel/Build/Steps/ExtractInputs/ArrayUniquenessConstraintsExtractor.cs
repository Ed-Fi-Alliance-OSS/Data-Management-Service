// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Backend.RelationalModel.Build.Steps.ExtractInputs;

internal static class ArrayUniquenessConstraintsExtractor
{
    /// <summary>
    /// Extracts array uniqueness constraints defined on the resource schema.
    /// </summary>
    internal static IReadOnlyList<ArrayUniquenessConstraintInput> ExtractArrayUniquenessConstraints(
        JsonObject resourceSchema,
        string projectName,
        string resourceName
    )
    {
        if (!resourceSchema.TryGetPropertyValue("arrayUniquenessConstraints", out var constraintsNode))
        {
            return Array.Empty<ArrayUniquenessConstraintInput>();
        }

        if (constraintsNode is null)
        {
            return Array.Empty<ArrayUniquenessConstraintInput>();
        }

        if (constraintsNode is not JsonArray constraintsArray)
        {
            throw new InvalidOperationException(
                "Expected arrayUniquenessConstraints to be an array, invalid ApiSchema."
            );
        }

        return ExtractArrayUniquenessConstraints(
            constraintsArray,
            projectName,
            resourceName,
            isNested: false
        );
    }

    /// <summary>
    /// Extracts and compiles array uniqueness constraints from a JSON array, recursively processing nested
    /// constraints.
    /// </summary>
    private static IReadOnlyList<ArrayUniquenessConstraintInput> ExtractArrayUniquenessConstraints(
        JsonArray constraintsArray,
        string projectName,
        string resourceName,
        bool isNested
    )
    {
        List<ArrayUniquenessConstraintInput> constraints = [];

        foreach (var constraint in constraintsArray)
        {
            if (constraint is null)
            {
                throw new InvalidOperationException(
                    "Expected arrayUniquenessConstraints to not contain null entries, invalid ApiSchema."
                );
            }

            if (constraint is not JsonObject constraintObject)
            {
                throw new InvalidOperationException(
                    "Expected arrayUniquenessConstraints entries to be objects, invalid ApiSchema."
                );
            }

            JsonPathExpression? basePath = null;

            if (constraintObject.TryGetPropertyValue("basePath", out var basePathNode))
            {
                if (basePathNode is null)
                {
                    throw new InvalidOperationException(
                        "Expected arrayUniquenessConstraints.basePath to be non-null, invalid ApiSchema."
                    );
                }

                if (basePathNode is not JsonValue basePathValue)
                {
                    throw new InvalidOperationException(
                        "Expected arrayUniquenessConstraints.basePath to be a string, invalid ApiSchema."
                    );
                }

                basePath = JsonPathExpressionCompiler.Compile(basePathValue.GetValue<string>());
            }
            else if (isNested)
            {
                throw new InvalidOperationException(
                    $"arrayUniquenessConstraints nestedConstraints entry is missing basePath on "
                        + $"resource '{projectName}:{resourceName}'."
                );
            }

            var pathsNode = constraintObject["paths"];

            if (pathsNode is not JsonArray pathsArray)
            {
                throw new InvalidOperationException(
                    "Expected arrayUniquenessConstraints.paths to be an array, invalid ApiSchema."
                );
            }

            if (pathsArray.Count == 0)
            {
                throw new InvalidOperationException(
                    "Expected arrayUniquenessConstraints.paths to contain entries, invalid ApiSchema."
                );
            }

            List<JsonPathExpression> paths = new(pathsArray.Count);

            foreach (var pathNode in pathsArray)
            {
                if (pathNode is null)
                {
                    throw new InvalidOperationException(
                        "Expected arrayUniquenessConstraints.paths to not contain null entries, "
                            + "invalid ApiSchema."
                    );
                }

                if (pathNode is not JsonValue pathValue)
                {
                    throw new InvalidOperationException(
                        "Expected arrayUniquenessConstraints.paths entries to be strings, invalid ApiSchema."
                    );
                }

                paths.Add(JsonPathExpressionCompiler.Compile(pathValue.GetValue<string>()));
            }

            IReadOnlyList<ArrayUniquenessConstraintInput> nestedConstraints =
                Array.Empty<ArrayUniquenessConstraintInput>();

            if (
                constraintObject.TryGetPropertyValue("nestedConstraints", out var nestedConstraintsNode)
                && nestedConstraintsNode is not null
            )
            {
                if (nestedConstraintsNode is not JsonArray nestedConstraintsArray)
                {
                    throw new InvalidOperationException(
                        "Expected arrayUniquenessConstraints.nestedConstraints to be an array, "
                            + "invalid ApiSchema."
                    );
                }

                nestedConstraints = ExtractArrayUniquenessConstraints(
                    nestedConstraintsArray,
                    projectName,
                    resourceName,
                    isNested: true
                );
            }

            constraints.Add(new ArrayUniquenessConstraintInput(basePath, paths.ToArray(), nestedConstraints));
        }

        return constraints.ToArray();
    }

    /// <summary>
    /// Validates that array uniqueness constraints referencing any identity path from a reference object include
    /// all of that reference object's identity paths.
    /// </summary>
    internal static void ValidateArrayUniquenessReferenceIdentityCompleteness(
        IReadOnlyList<ArrayUniquenessConstraintInput> constraints,
        IReadOnlyList<DocumentReferenceMapping> referenceMappings,
        string projectName,
        string resourceName
    )
    {
        if (constraints.Count == 0 || referenceMappings.Count == 0)
        {
            return;
        }

        var referenceGroups = referenceMappings
            .Select(mapping => new ReferenceIdentityGroup(
                mapping.ReferenceObjectPath,
                mapping
                    .ReferenceJsonPaths.Select(binding => binding.ReferenceJsonPath.Canonical)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(path => path, StringComparer.Ordinal)
                    .ToArray()
            ))
            .ToArray();
        var resourceKey = $"{projectName}:{resourceName}";

        foreach (var constraint in constraints)
        {
            ValidateArrayUniquenessReferenceIdentityCompleteness(constraint, referenceGroups, resourceKey);
        }
    }

    /// <summary>
    /// Validates a single array uniqueness constraint (and nested constraints) for reference identity coverage
    /// within each array scope.
    /// </summary>
    private static void ValidateArrayUniquenessReferenceIdentityCompleteness(
        ArrayUniquenessConstraintInput constraint,
        IReadOnlyList<ReferenceIdentityGroup> referenceGroups,
        string resourceKey
    )
    {
        var resolvedPaths = constraint
            .Paths.Select(path => ResolveConstraintPath(constraint.BasePath, path))
            .ToArray();
        var pathsByScope = GroupPathsByArrayScope(resolvedPaths, resourceKey);
        var basePath = constraint.BasePath?.Canonical;

        foreach (var scopeGroup in pathsByScope.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            var scope = scopeGroup.Key;
            var scopePaths = scopeGroup.Value;
            var scopePath = GetArrayScope(scopePaths[0], resourceKey);
            var matched = ValidateArrayUniquenessReferenceIdentityCoverage(
                scopePaths,
                referenceGroups,
                resourceKey,
                scope,
                basePath,
                alignedScope: null,
                alignedBasePath: null
            );

            if (!matched)
            {
                if (
                    TryStripExtensionRootPrefix(scopePath, out var alignedScope)
                    && TryStripExtensionRootPrefix(scopePaths, out var alignedPaths)
                )
                {
                    var alignedBasePath = TryStripExtensionRootPrefix(
                        constraint.BasePath,
                        out var alignedBase
                    )
                        ? alignedBase.Canonical
                        : null;

                    _ = ValidateArrayUniquenessReferenceIdentityCoverage(
                        alignedPaths,
                        referenceGroups,
                        resourceKey,
                        scope,
                        basePath,
                        alignedScope.Canonical,
                        alignedBasePath
                    );
                }
            }
        }

        foreach (var nested in constraint.NestedConstraints)
        {
            ValidateArrayUniquenessReferenceIdentityCompleteness(nested, referenceGroups, resourceKey);
        }
    }

    /// <summary>
    /// Validates that when any reference identity path is included in a constraint scope, all identity paths for
    /// that reference object are included.
    /// </summary>
    private static bool ValidateArrayUniquenessReferenceIdentityCoverage(
        IReadOnlyList<JsonPathExpression> constraintPaths,
        IReadOnlyList<ReferenceIdentityGroup> referenceGroups,
        string resourceKey,
        string scope,
        string? basePath,
        string? alignedScope,
        string? alignedBasePath
    )
    {
        HashSet<string> constraintPathSet = new(
            constraintPaths.Select(path => path.Canonical),
            StringComparer.Ordinal
        );
        var matchedAny = false;

        foreach (var referenceGroup in referenceGroups)
        {
            if (!referenceGroup.ReferenceIdentityPaths.Any(constraintPathSet.Contains))
            {
                continue;
            }

            matchedAny = true;
            var missing = referenceGroup
                .ReferenceIdentityPaths.Where(path => !constraintPathSet.Contains(path))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();

            if (missing.Length == 0)
            {
                continue;
            }

            var basePathMessage = basePath is null ? string.Empty : $" basePath '{basePath}'";
            var alignedMessage =
                alignedScope is null ? string.Empty
                : alignedBasePath is null ? $" alignedScope '{alignedScope}'"
                : $" alignedScope '{alignedScope}' basePath '{alignedBasePath}'";

            throw new InvalidOperationException(
                $"arrayUniquenessConstraints scope '{scope}' on resource '{resourceKey}'"
                    + basePathMessage
                    + alignedMessage
                    + $" includes reference identity path(s) under '{referenceGroup.ReferenceObjectPath.Canonical}' "
                    + "but is missing reference identity path(s): "
                    + string.Join(", ", missing)
            );
        }

        return matchedAny;
    }

    /// <summary>
    /// Groups constraint paths by the canonical JSONPath of their owning array scope.
    /// </summary>
    private static IReadOnlyDictionary<string, IReadOnlyList<JsonPathExpression>> GroupPathsByArrayScope(
        IReadOnlyList<JsonPathExpression> paths,
        string resourceKey
    )
    {
        if (paths.Count == 0)
        {
            throw new InvalidOperationException("arrayUniquenessConstraints must include at least one path.");
        }

        Dictionary<string, List<JsonPathExpression>> grouped = new(StringComparer.Ordinal);

        foreach (var path in paths)
        {
            var arrayScope = GetArrayScope(path, resourceKey);
            var scope = arrayScope.Canonical;

            if (!grouped.TryGetValue(scope, out var scopePaths))
            {
                scopePaths = [];
                grouped.Add(scope, scopePaths);
            }

            scopePaths.Add(path);
        }

        return grouped.ToDictionary(
            entry => entry.Key,
            entry => (IReadOnlyList<JsonPathExpression>)entry.Value,
            StringComparer.Ordinal
        );
    }

    /// <summary>
    /// Returns the owning array scope for a path by taking the prefix through its last wildcard array segment.
    /// </summary>
    private static JsonPathExpression GetArrayScope(JsonPathExpression path, string resourceKey)
    {
        var lastArrayIndex = -1;

        for (var index = 0; index < path.Segments.Count; index++)
        {
            if (path.Segments[index] is JsonPathSegment.AnyArrayElement)
            {
                lastArrayIndex = index;
            }
        }

        if (lastArrayIndex < 0)
        {
            throw new InvalidOperationException(
                $"arrayUniquenessConstraints path '{path.Canonical}' on resource '{resourceKey}' "
                    + "must include an array wildcard segment."
            );
        }

        var scopeSegments = path.Segments.Take(lastArrayIndex + 1).ToArray();
        return JsonPathExpressionCompiler.FromSegments(scopeSegments);
    }

    /// <summary>
    /// Resolves a constraint path relative to its optional base path.
    /// </summary>
    private static JsonPathExpression ResolveConstraintPath(
        JsonPathExpression? basePath,
        JsonPathExpression path
    )
    {
        return basePath is null ? path : ResolveRelativePath(basePath.Value, path);
    }

    /// <summary>
    /// Resolves a path relative to a base array path by concatenating JSONPath segments.
    /// </summary>
    private static JsonPathExpression ResolveRelativePath(
        JsonPathExpression basePath,
        JsonPathExpression relativePath
    )
    {
        if (relativePath.Segments.Count == 0)
        {
            return basePath;
        }

        var combinedSegments = basePath.Segments.Concat(relativePath.Segments).ToArray();
        return JsonPathExpressionCompiler.FromSegments(combinedSegments);
    }

    /// <summary>
    /// Attempts to strip a leading <c>._ext.{project}</c> prefix from an optional path.
    /// </summary>
    private static bool TryStripExtensionRootPrefix(JsonPathExpression? path, out JsonPathExpression stripped)
    {
        if (path is null)
        {
            stripped = default;
            return false;
        }

        return TryStripExtensionRootPrefix(path.Value, out stripped);
    }

    /// <summary>
    /// Attempts to strip a leading <c>._ext.{project}</c> prefix from a path.
    /// </summary>
    private static bool TryStripExtensionRootPrefix(JsonPathExpression path, out JsonPathExpression stripped)
    {
        if (
            path.Segments.Count >= 2
            && path.Segments[0] is JsonPathSegment.Property { Name: "_ext" }
            && path.Segments[1] is JsonPathSegment.Property
        )
        {
            var remainingSegments = path.Segments.Skip(2).ToArray();
            stripped = JsonPathExpressionCompiler.FromSegments(remainingSegments);
            return true;
        }

        stripped = default;
        return false;
    }

    /// <summary>
    /// Attempts to strip a leading <c>._ext.{project}</c> prefix from all paths in the list.
    /// </summary>
    private static bool TryStripExtensionRootPrefix(
        IReadOnlyList<JsonPathExpression> paths,
        out IReadOnlyList<JsonPathExpression> stripped
    )
    {
        List<JsonPathExpression> strippedPaths = new(paths.Count);

        foreach (var path in paths)
        {
            if (!TryStripExtensionRootPrefix(path, out var strippedPath))
            {
                stripped = Array.Empty<JsonPathExpression>();
                return false;
            }

            strippedPaths.Add(strippedPath);
        }

        stripped = strippedPaths.ToArray();
        return true;
    }

    /// <summary>
    /// Captures a reference object path and its ordered set of identity path canonical strings.
    /// </summary>
    private sealed record ReferenceIdentityGroup(
        JsonPathExpression ReferenceObjectPath,
        IReadOnlyList<string> ReferenceIdentityPaths
    );
}
