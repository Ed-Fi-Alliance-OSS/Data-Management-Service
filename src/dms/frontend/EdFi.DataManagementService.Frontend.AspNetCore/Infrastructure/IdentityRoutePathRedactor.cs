// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.RegularExpressions;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;

/// <summary>
/// Redacts the identifier segment from an identity route path before it reaches a log.
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
    private const string IdGroup = "id";
    private const string TokenGroup = "token";

    // The route shape with any number of leading tenant and route-qualifier segments, so redaction
    // never depends on configuration: a path whose prefix does not fit this host's configuration is
    // redacted as well.
    private static readonly Regex _anyPrefixPattern = BuildPattern("(?:/[^/]+)*");

    /// <summary>
    /// Redacts an identity get-by-id or results-poll path whatever its number of leading tenant and
    /// route-qualifier segments. Every other path is returned unchanged.
    /// </summary>
    public static string RedactWithAnyPrefix(string path) => Redact(path, _anyPrefixPattern);

    /// <summary>
    /// True when <paramref name="path"/> is an identity get-by-id or results-poll path, whatever its
    /// number of leading tenant and route-qualifier segments.
    /// </summary>
    public static bool IsIdentifierBearingPathWithAnyPrefix(string path) => _anyPrefixPattern.IsMatch(path);

    private static string Redact(string value, Regex pattern)
    {
        Match match = pattern.Match(value);
        if (!match.Success)
        {
            return value;
        }

        Group token = match.Groups[TokenGroup];
        return token.Success
            ? string.Concat(value.AsSpan(0, token.Index), TokenSegmentTemplate)
            : string.Concat(value.AsSpan(0, match.Groups[IdGroup].Index), IdSegmentTemplate);
    }

    // One route shape for every caller: a results-poll path (.../identity/v2/identities/results/{value})
    // or a get-by-id path (.../identity/v2/identities/{value}, excluding the literal find and search
    // operation names). The results alternative is tried first, so a results-poll path is never
    // mistaken for a get-by-id of "results". IgnoreCase/CultureInvariant because ASP.NET Core
    // routing matches these routes case-insensitively; the trailing /? before $ tolerates the
    // trailing slash routing also accepts, without pulling it into the captured identifier group.
    private static Regex BuildPattern(string prefixPattern) =>
        new(
            $"^{prefixPattern}/identity/v2/identities/(?:results/(?<{TokenGroup}>[^/]+)|(?!find/?$|search/?$)(?<{IdGroup}>[^/]+))/?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
        );
}
