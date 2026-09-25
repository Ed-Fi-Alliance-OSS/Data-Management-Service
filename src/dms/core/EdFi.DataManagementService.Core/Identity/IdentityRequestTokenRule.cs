// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Identity;

/// <summary>
/// Why <see cref="IdentityRequestTokenRule.Evaluate" /> found a request token unusable. Also reported
/// for the single usable outcome as <see cref="None" /> so tests do not need a separate success flag.
/// </summary>
internal enum IdentityRequestTokenRejectionReason
{
    /// <summary>The token is usable; no rejection applies.</summary>
    None,

    /// <summary>The token is null, empty, or entirely whitespace.</summary>
    Blank,

    /// <summary>The token contains a path separator (<c>/</c> or <c>\</c>).</summary>
    ContainsPathSeparator,

    /// <summary>The token contains a Unicode control character.</summary>
    ContainsControlCharacter,

    /// <summary>The token is exactly <c>.</c> or <c>..</c>, a path traversal segment.</summary>
    DotSegment,

    /// <summary>
    /// <c>Uri.UnescapeDataString(Uri.EscapeDataString(token))</c> does not reproduce the token
    /// (ordinal comparison), for example a lone UTF-16 surrogate.
    /// </summary>
    NotRoundTrippable,

    /// <summary>The percent-escaped token is longer than <see cref="IdentityRequestTokenRule.MaxEscapedTokenLength" />.</summary>
    EscapedTokenTooLong,

    /// <summary>
    /// The composed poll path (route prefix, a separating <c>/</c>, and the escaped token), plus the
    /// HTTP method and version overhead of the request line, would exceed the server's configured
    /// maximum request line size.
    /// </summary>
    ComposedPathTooLong,
}

/// <summary>
/// The outcome of <see cref="IdentityRequestTokenRule.Evaluate" />.
/// </summary>
/// <param name="IsUsable">Whether the token may be escaped into a <c>Location</c> poll path.</param>
/// <param name="EscapedToken">
/// The result of <c>Uri.EscapeDataString(token)</c> when escaping was attempted, or an empty string
/// when the token was rejected before escaping (null or blank). Present on both usable and unusable
/// outcomes so a caller can log the value that was tried without recomputing it.
/// </param>
/// <param name="Reason"><see cref="IdentityRequestTokenRejectionReason.None" /> when usable.</param>
internal readonly record struct IdentityRequestTokenEvaluation(
    bool IsUsable,
    string EscapedToken,
    IdentityRequestTokenRejectionReason Reason
);

/// <summary>
/// Decides whether a provider-supplied request token can be safely round-tripped through a
/// <c>Location</c> poll path (design.md:722-768). The token is opaque to DMS: no provider structure or
/// meaning is assumed, so the rule only rejects shapes that would break path parsing, path traversal
/// safety, unescaping fidelity, or the server's own request-line budget.
/// </summary>
internal static class IdentityRequestTokenRule
{
    /// <summary>
    /// The escaped-token length ceiling. Chosen independently of any single server's request-line
    /// limit so the rule rejects pathologically long tokens even against a generously configured host.
    /// </summary>
    internal const int MaxEscapedTokenLength = 1024;

    /// <summary>
    /// Evaluates whether <paramref name="token" /> can be composed into a poll path under
    /// <paramref name="composedPathPrefix" /> (the route-qualified <c>.../identities/results</c> path,
    /// with no trailing slash and no token) without exceeding <paramref name="maxRequestLineSize" />,
    /// the server's configured <c>KestrelServerLimits.MaxRequestLineSize</c> (or the frontend's
    /// equivalent), read at request time rather than hard-coded.
    /// </summary>
    public static IdentityRequestTokenEvaluation Evaluate(
        string? token,
        string composedPathPrefix,
        int maxRequestLineSize
    )
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return Reject(string.Empty, IdentityRequestTokenRejectionReason.Blank);
        }

        if (token.Contains('/') || token.Contains('\\'))
        {
            return Reject(token, IdentityRequestTokenRejectionReason.ContainsPathSeparator);
        }

        if (token.Any(char.IsControl))
        {
            return Reject(token, IdentityRequestTokenRejectionReason.ContainsControlCharacter);
        }

        if (token is "." or "..")
        {
            return Reject(token, IdentityRequestTokenRejectionReason.DotSegment);
        }

        string escaped = Uri.EscapeDataString(token);

        if (!string.Equals(Uri.UnescapeDataString(escaped), token, StringComparison.Ordinal))
        {
            return Reject(escaped, IdentityRequestTokenRejectionReason.NotRoundTrippable);
        }

        if (escaped.Length > MaxEscapedTokenLength)
        {
            return Reject(escaped, IdentityRequestTokenRejectionReason.EscapedTokenTooLong);
        }

        // "GET " and " HTTP/1.1" bound the request-line overhead around the composed path: method,
        // a separating space, the path itself, another space, and the HTTP version token.
        int composedRequestLineLength =
            composedPathPrefix.Length + 1 + escaped.Length + "GET ".Length + " HTTP/1.1".Length;

        if (composedRequestLineLength > maxRequestLineSize)
        {
            return Reject(escaped, IdentityRequestTokenRejectionReason.ComposedPathTooLong);
        }

        return new IdentityRequestTokenEvaluation(true, escaped, IdentityRequestTokenRejectionReason.None);
    }

    private static IdentityRequestTokenEvaluation Reject(
        string escapedToken,
        IdentityRequestTokenRejectionReason reason
    ) => new(false, escapedToken, reason);
}
