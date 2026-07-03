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
        int separatorIndex = trimmed.IndexOf(' ');

        string scheme = separatorIndex < 0 ? trimmed : trimmed[..separatorIndex];
        string parameter = separatorIndex < 0 ? string.Empty : trimmed[(separatorIndex + 1)..].Trim();

        if (!string.Equals(scheme, BearerScheme, StringComparison.OrdinalIgnoreCase))
        {
            return AuthorizationHeaderResult.Error("Unknown Authorization header scheme.");
        }

        if (string.IsNullOrWhiteSpace(parameter))
        {
            return AuthorizationHeaderResult.Error("Missing Authorization header bearer token value.");
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
