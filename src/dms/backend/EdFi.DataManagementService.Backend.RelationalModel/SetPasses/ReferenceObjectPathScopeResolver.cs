// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using static EdFi.DataManagementService.Backend.RelationalModel.Schema.RelationalModelSetSchemaHelpers;

namespace EdFi.DataManagementService.Backend.RelationalModel.SetPasses;

/// <summary>
/// Resolves reference object paths to the deepest matching table scope while enforcing extension scope
/// requirements.
/// </summary>
internal static class ReferenceObjectPathScopeResolver
{
    private const string ExtensionPropertyName = "_ext";

    /// <summary>
    /// Selects the deepest candidate whose scope is a prefix of the supplied reference object path.
    /// </summary>
    public static T ResolveDeepestMatchingScope<T>(
        JsonPathExpression referenceObjectPath,
        IReadOnlyList<T> scopeCandidates,
        Func<T, JsonPathExpression> scopeSelector,
        Func<IReadOnlyList<string>, Exception> noMatchExceptionFactory,
        Func<IReadOnlyList<string>, Exception>? ambiguousMatchExceptionFactory,
        Func<Exception> extensionScopeRequiredExceptionFactory
    )
    {
        ArgumentNullException.ThrowIfNull(scopeCandidates);
        ArgumentNullException.ThrowIfNull(scopeSelector);
        ArgumentNullException.ThrowIfNull(noMatchExceptionFactory);
        ArgumentNullException.ThrowIfNull(extensionScopeRequiredExceptionFactory);

        var orderedCandidates = scopeCandidates
            .OrderBy(candidate => scopeSelector(candidate).Canonical, StringComparer.Ordinal)
            .ToArray();
        List<T> bestMatches = [];
        var bestSegmentCount = -1;

        foreach (var candidate in orderedCandidates)
        {
            var scope = scopeSelector(candidate);

            if (!IsPrefixOf(scope.Segments, referenceObjectPath.Segments))
            {
                continue;
            }

            var segmentCount = scope.Segments.Count;

            if (segmentCount > bestSegmentCount)
            {
                bestSegmentCount = segmentCount;
                bestMatches.Clear();
                bestMatches.Add(candidate);
                continue;
            }

            if (segmentCount == bestSegmentCount)
            {
                bestMatches.Add(candidate);
            }
        }

        if (bestMatches.Count == 0)
        {
            var candidateScopes = orderedCandidates
                .Select(candidate => scopeSelector(candidate).Canonical)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            throw noMatchExceptionFactory(candidateScopes);
        }

        var bestMatchScopes = bestMatches
            .Select(candidate => scopeSelector(candidate).Canonical)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(scope => scope, StringComparer.Ordinal)
            .ToArray();

        if (bestMatchScopes.Length > 1 && ambiguousMatchExceptionFactory is not null)
        {
            throw ambiguousMatchExceptionFactory(bestMatchScopes);
        }

        var bestMatch = bestMatches[0];
        var bestScope = scopeSelector(bestMatch);

        if (
            ContainsExtensionSegment(referenceObjectPath.Segments)
            && !ContainsExtensionSegment(bestScope.Segments)
        )
        {
            throw extensionScopeRequiredExceptionFactory();
        }

        return bestMatch;
    }

    private static bool ContainsExtensionSegment(IReadOnlyList<JsonPathSegment> segments)
    {
        return segments.Any(segment => segment is JsonPathSegment.Property { Name: ExtensionPropertyName });
    }
}
