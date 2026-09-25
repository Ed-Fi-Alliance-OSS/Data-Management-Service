// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;

/// <summary>
/// Redacts the identifier segment from an identity route path before it reaches a log (D11).
/// A get-by-id path (for example .../identity/v2/identities/605943412) becomes
/// .../identity/v2/identities/{id}, and a results-poll path (for example
/// .../identity/v2/identities/results/SECRET-TOKEN-XYZ) becomes
/// .../identity/v2/identities/results/{token}, while every tenant and route-qualifier segment ahead
/// of /identity/v2 keeps its real value. Matching is case-insensitive and tolerates a single
/// trailing slash on the identifier segment, mirroring how ASP.NET Core routing matches these paths;
/// the redacted output drops that trailing slash along with the identifier it followed. Every other
/// path - including identities/find and identities/search (and their trailing-slash forms), neither
/// of which carries an identifier, and every non-identity route - is returned unchanged.
/// </summary>
/// <remarks>
/// Called ahead of <c>LoggingSanitizer</c> in <see cref="LoggingMiddleware"/> so the scope
/// <c>Path</c> property and every rendered message template carry the redacted value. The frontend
/// <c>LoggingMiddleware</c> runs before routing (Program.cs), so this is a route-shape match against
/// the raw request path rather than an endpoint-metadata check.
/// </remarks>
internal static class IdentityRoutePathRedactor
{
    private const string IdSegmentTemplate = "{id}";
    private const string TokenSegmentTemplate = "{token}";

    // Keyed by the number of leading tenant/route-qualifier segments (0-N, effectively a handful of
    // distinct values per process), so the compiled pattern for a given configuration is built once.
    private static readonly ConcurrentDictionary<int, Regex> _byIdPatterns = new();
    private static readonly ConcurrentDictionary<int, Regex> _resultsPatterns = new();

    public static string? Redact(PathString path, string[] qualifierSegments, bool multiTenancy)
    {
        string? value = path.Value;
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        int prefixSegmentCount = (multiTenancy ? 1 : 0) + qualifierSegments.Length;

        Regex resultsPattern = _resultsPatterns.GetOrAdd(prefixSegmentCount, BuildResultsPattern);
        Match resultsMatch = resultsPattern.Match(value);
        if (resultsMatch.Success)
        {
            return string.Concat(value.AsSpan(0, resultsMatch.Groups[1].Index), TokenSegmentTemplate);
        }

        Regex byIdPattern = _byIdPatterns.GetOrAdd(prefixSegmentCount, BuildByIdPattern);
        Match byIdMatch = byIdPattern.Match(value);
        if (byIdMatch.Success)
        {
            return string.Concat(value.AsSpan(0, byIdMatch.Groups[1].Index), IdSegmentTemplate);
        }

        return value;
    }

    // IgnoreCase/CultureInvariant because ASP.NET Core routing matches these routes
    // case-insensitively; the trailing /? before $ tolerates the trailing slash routing also
    // accepts, without pulling it into the captured identifier group.
    private static Regex BuildByIdPattern(int prefixSegmentCount) =>
        new(
            $"^{BuildPrefixPattern(prefixSegmentCount)}/identity/v2/identities/(?!find/?$|search/?$)([^/]+)/?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
        );

    private static Regex BuildResultsPattern(int prefixSegmentCount) =>
        new(
            $"^{BuildPrefixPattern(prefixSegmentCount)}/identity/v2/identities/results/([^/]+)/?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
        );

    private static string BuildPrefixPattern(int prefixSegmentCount) =>
        string.Concat(Enumerable.Repeat("/[^/]+", prefixSegmentCount));
}
