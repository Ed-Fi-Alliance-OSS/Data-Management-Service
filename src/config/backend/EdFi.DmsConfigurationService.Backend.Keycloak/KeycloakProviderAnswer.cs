// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.Keycloak;

/// <summary>
/// Handling of Keycloak's answers shared by the delegated revocation and introspection requests:
/// the client id length Keycloak can hold, the loggable provider error categories and the failure
/// classification and logging helpers. Nothing here returns anything from a provider body or a
/// dependency exception except an allowlisted error category and exception type names.
/// </summary>
internal static class KeycloakProviderAnswer
{
    /// <summary>
    /// The longest client id Keycloak can hold, in code points: Keycloak keeps it in a 255-character
    /// column, and every database counts a code point as at least one character (PostgreSQL counts code
    /// points, H2 UTF-16 code units).
    /// </summary>
    internal const int MaxClientIdCodePoints = 255;

    /// <summary>The provider <c>error</c> values that may be logged verbatim; every other value is <c>unrecognized</c>.</summary>
    private static readonly HashSet<string> _loggableProviderErrors = new(StringComparer.Ordinal)
    {
        "invalid_request",
        "invalid_client",
        "unauthorized_client",
        "unsupported_token_type",
        "invalid_token",
        "server_error",
        "temporarily_unavailable",
    };

    /// <summary>True when the client id has more code points than any Keycloak client id can hold.</summary>
    internal static bool IsLongerThanAnyClientId(string clientId) =>
        clientId.EnumerateRunes().Skip(MaxClientIdCodePoints).Any();

    /// <summary>The provider <c>error</c> when it is allowlisted for logging, otherwise <c>unrecognized</c>.</summary>
    internal static string ProviderErrorCategory(string? error) =>
        error is not null && _loggableProviderErrors.Contains(error) ? error : "unrecognized";

    /// <summary>Connection, name resolution, TLS or proxy failures: the request never reached Keycloak.</summary>
    internal static bool IsUndelivered(HttpRequestError error) =>
        error
            is HttpRequestError.ConnectionError
                or HttpRequestError.NameResolutionError
                or HttpRequestError.SecureConnectionError
                or HttpRequestError.ProxyTunnelError;

    /// <summary>The type names of an exception and its inner exceptions, outermost first.</summary>
    internal static string ExceptionTypeChain(Exception exception)
    {
        string name = exception.GetType().FullName ?? exception.GetType().Name;
        return exception.InnerException is { } inner ? $"{name} -> {ExceptionTypeChain(inner)}" : name;
    }
}
