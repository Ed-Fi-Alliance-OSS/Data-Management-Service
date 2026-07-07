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

        // A well-formed Bearer credential separates the scheme from the token with a single
        // space and carries a whitespace-free token (a JWT never contains whitespace). A
        // non-space separator (tab, newline) or a multi-token value is a malformed header,
        // not a credential to pass through to JWT validation.
        if (remainder[0] != ' ')
        {
            return AuthorizationHeaderResult.Error("Invalid Authorization header.");
        }

        string parameter = remainder.Trim();
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
