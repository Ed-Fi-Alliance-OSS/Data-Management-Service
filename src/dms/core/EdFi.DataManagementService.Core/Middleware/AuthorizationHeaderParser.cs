// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Classifies a present Authorization header value against the authentication error
/// contract for 401 authentication failures. The JWT authentication and JWT
/// role-authentication middleware share this parsing so their error details stay
/// consistent with the design contract.
/// </summary>
internal static class AuthorizationHeaderParser
{
    private const string BearerScheme = "Bearer";

    /// <summary>
    /// Parses a present Authorization header value. Returns the extracted Bearer token
    /// on success, or the error detail describing why classification failed.
    /// </summary>
    public static AuthorizationHeaderResult Parse(string authHeader)
    {
        // A present-but-blank header value has no parseable scheme.
        if (string.IsNullOrWhiteSpace(authHeader))
        {
            return AuthorizationHeaderResult.Error("Invalid Authorization header.");
        }

        string trimmed = authHeader.Trim();

        // The scheme is the leading run of non-whitespace characters. Reading up to the first
        // whitespace of any kind (not only a literal space) means a header whose scheme and
        // token are separated by a tab or other whitespace (e.g. "Bearer\tabc") is recognized
        // as the Bearer scheme with a malformed separator rather than as an unknown scheme.
        int schemeEnd = 0;
        while (schemeEnd < trimmed.Length && !char.IsWhiteSpace(trimmed[schemeEnd]))
        {
            schemeEnd++;
        }

        string scheme = trimmed[..schemeEnd];
        if (!string.Equals(scheme, BearerScheme, StringComparison.OrdinalIgnoreCase))
        {
            return AuthorizationHeaderResult.Error("Unknown Authorization header scheme.");
        }

        string remainder = trimmed[schemeEnd..];
        if (remainder.Length == 0)
        {
            return AuthorizationHeaderResult.Error("Missing Authorization header bearer token value.");
        }

        // A well-formed Bearer credential separates the scheme from the token with one or more
        // spaces and carries a whitespace-free token (a JWT never contains whitespace). Only the
        // separating space(s) are stripped — the token is NOT Trim()'d — so a non-space separator
        // (e.g. "Bearer \t<token>") or any whitespace inside the token survives into the value and
        // is rejected below as malformed, rather than being silently discarded and passed through
        // to JWT validation as if the header were well formed.
        string parameter = remainder.TrimStart(' ');
        if (parameter.Any(char.IsWhiteSpace))
        {
            return AuthorizationHeaderResult.Error("Invalid Authorization header.");
        }

        return AuthorizationHeaderResult.Success(parameter);
    }
}

/// <summary>
/// Result of parsing an Authorization header: either a valid Bearer token or an
/// authentication error detail (never both).
/// </summary>
internal readonly record struct AuthorizationHeaderResult(string? Token, string? ErrorDetail)
{
    public bool IsValid => ErrorDetail is null;

    public static AuthorizationHeaderResult Success(string token) => new(token, null);

    public static AuthorizationHeaderResult Error(string errorDetail) => new(null, errorDetail);
}
