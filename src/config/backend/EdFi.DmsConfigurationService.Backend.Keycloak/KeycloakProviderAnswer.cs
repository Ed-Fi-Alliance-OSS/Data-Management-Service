// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.Keycloak;

/// <summary>
/// Handling of Keycloak's answers shared by the delegated revocation and introspection requests:
/// a bounded body read, the longest client id Keycloak can hold, the loggable provider error
/// categories and the failure classification and logging helpers. Nothing here returns anything from a provider body or a dependency exception
/// except an allowlisted error category and exception type names.
/// </summary>
internal static class KeycloakProviderAnswer
{
    /// <summary>The largest answer body read; a larger one is treated as unparseable.</summary>
    internal const int MaxResponseBodyBytes = 64 * 1024;

    /// <summary>The longest client id Keycloak can hold; a longer one names no client.</summary>
    internal const int MaxClientIdLength = 255;

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

    /// <summary>
    /// Reads at most <see cref="MaxResponseBodyBytes"/>; null when the body is larger, so an
    /// oversized answer is unparseable without being buffered.
    /// </summary>
    internal static async Task<byte[]?> ReadBoundedAsync(
        HttpContent content,
        CancellationToken cancellationToken
    )
    {
        if (content.Headers.ContentLength > MaxResponseBodyBytes)
        {
            return null;
        }

        await using Stream stream = await content.ReadAsStreamAsync(cancellationToken);
        byte[] buffer = new byte[MaxResponseBodyBytes + 1];
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total > MaxResponseBodyBytes ? null : buffer[..total];
    }

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
        var names = new List<string>();
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            names.Add(current.GetType().FullName ?? current.GetType().Name);
        }

        return string.Join(" -> ", names);
    }
}
