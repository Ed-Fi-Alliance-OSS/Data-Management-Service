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

    private readonly record struct CandidateScope<TCandidate>(TCandidate Candidate, JsonPathExpression Scope);

    /// <summary>
    /// Resolves the owning table scope for a reference object path and emits consistent scope mismatch errors.
    /// </summary>
    public static T ResolveOwningTableScope<T>(
        JsonPathExpression referenceObjectPath,
        IReadOnlyList<T> scopeCandidates,
        Func<T, JsonPathExpression> scopeSelector,
        QualifiedResourceName resource,
        string? tableName = null
    )
    {
        ArgumentNullException.ThrowIfNull(referenceObjectPath.Canonical, nameof(referenceObjectPath));
        ArgumentNullException.ThrowIfNull(referenceObjectPath.Segments, nameof(referenceObjectPath));
        ArgumentNullException.ThrowIfNull(scopeCandidates);
        ArgumentNullException.ThrowIfNull(scopeSelector);

        var tableQualifier = tableName is null ? string.Empty : $" for '{tableName}'";
        var candidateScopeList = BuildScopeList(scopeCandidates, scopeSelector);

        return ResolveDeepestMatchingScope(
            referenceObjectPath,
            scopeCandidates,
            scopeSelector,
            _ => new InvalidOperationException(
                $"Reference object path '{referenceObjectPath.Canonical}' on resource "
                    + $"'{FormatResource(resource)}' did not match any table scope{tableQualifier}. "
                    + $"Candidates: {candidateScopeList}."
            ),
            candidateScopes => new InvalidOperationException(
                $"Reference object path '{referenceObjectPath.Canonical}' on resource "
                    + $"'{FormatResource(resource)}' matched multiple table scopes with the same depth"
                    + $"{tableQualifier}: "
                    + $"{FormatScopeList(candidateScopes)}."
            ),
            () =>
                new InvalidOperationException(
                    $"Reference object path '{referenceObjectPath.Canonical}' on resource "
                        + $"'{FormatResource(resource)}' requires an extension table scope, but none "
                        + $"was found{tableQualifier}. Candidates: {candidateScopeList}."
                )
        );
    }

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
        ArgumentNullException.ThrowIfNull(referenceObjectPath.Canonical, nameof(referenceObjectPath));
        ArgumentNullException.ThrowIfNull(referenceObjectPath.Segments, nameof(referenceObjectPath));
        ArgumentNullException.ThrowIfNull(scopeCandidates);
        ArgumentNullException.ThrowIfNull(scopeSelector);
        ArgumentNullException.ThrowIfNull(noMatchExceptionFactory);
        ArgumentNullException.ThrowIfNull(extensionScopeRequiredExceptionFactory);

        var orderedCandidates = scopeCandidates
            .Select(candidate => new CandidateScope<T>(candidate, scopeSelector(candidate)))
            .OrderBy(candidate => candidate.Scope.Canonical, StringComparer.Ordinal)
            .ToArray();
        List<CandidateScope<T>> bestMatches = [];
        var bestSegmentCount = -1;

        foreach (var candidate in orderedCandidates)
        {
            var scope = candidate.Scope;

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
                .Select(candidate => candidate.Scope.Canonical)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            throw noMatchExceptionFactory(candidateScopes);
        }

        var bestMatchScopes = bestMatches
            .Select(candidate => candidate.Scope.Canonical)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(scope => scope, StringComparer.Ordinal)
            .ToArray();

        if (bestMatchScopes.Length > 1 && ambiguousMatchExceptionFactory is not null)
        {
            throw ambiguousMatchExceptionFactory(bestMatchScopes);
        }

        var bestMatch = bestMatches[0];
        var bestScope = bestMatch.Scope;

        if (
            ContainsExtensionSegment(referenceObjectPath.Segments)
            && !ContainsExtensionSegment(bestScope.Segments)
        )
        {
            throw extensionScopeRequiredExceptionFactory();
        }

        return bestMatch.Candidate;
    }

    /// <summary>
    /// Returns true when the segment list contains an <c>_ext</c> property segment.
    /// </summary>
    private static bool ContainsExtensionSegment(IReadOnlyList<JsonPathSegment> segments)
    {
        return segments.Any(segment => segment is JsonPathSegment.Property { Name: ExtensionPropertyName });
    }

    /// <summary>
    /// Builds a formatted candidate-scope list string for diagnostics.
    /// </summary>
    private static string BuildScopeList<T>(
        IReadOnlyList<T> scopeCandidates,
        Func<T, JsonPathExpression> scopeSelector
    )
    {
        var scopes = scopeCandidates
            .Select(candidate => scopeSelector(candidate).Canonical)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(scope => scope, StringComparer.Ordinal)
            .ToArray();

        return FormatScopeList(scopes);
    }

    /// <summary>
    /// Formats a list of scope strings as a stable quoted comma-separated list.
    /// </summary>
    private static string FormatScopeList(IReadOnlyList<string> scopes)
    {
        if (scopes.Count == 0)
        {
            return "<none>";
        }

        return string.Join(", ", scopes.Select(scope => $"'{scope}'"));
    }
}
