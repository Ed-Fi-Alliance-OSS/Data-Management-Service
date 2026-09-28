// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Utilities;
using Microsoft.IdentityModel.Protocols;

namespace EdFi.DataManagementService.Core.Security;

/// <summary>
/// Document retriever implementation for fetching OIDC metadata
/// </summary>
/// <param name="httpClient">The client used for every fetch</param>
/// <param name="allowedOrigin">
/// The origin (scheme, host and port) of JwtAuthentication:MetadataAddress. The same retriever
/// fetches the metadata document and then the jwks_uri that document names, so refusing any other
/// origin stops a tampered document from pointing DMS at signing keys served elsewhere, even when
/// its issuer is the configured one.
/// </param>
internal class HttpDocumentRetriever(HttpClient httpClient, Uri allowedOrigin) : IDocumentRetriever
{
    private const int MaxLoggedAddressLength = 256;

    /// <summary>
    /// Whether to require HTTPS for metadata endpoints
    /// </summary>
    public bool RequireHttps { get; init; } = true;

    /// <summary>
    /// Retrieves a document from the specified address
    /// </summary>
    public async Task<string> GetDocumentAsync(string address, CancellationToken cancel)
    {
        if (RequireHttps && !address.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"HTTPS is required but the address is not HTTPS: {address}");
        }

        // SECURITY CRITICAL: checked before any request is sent, so a rejected address is never
        // contacted and its document is never adopted.
        if (
            !Uri.TryCreate(address, UriKind.Absolute, out Uri? target)
            || Uri.Compare(
                target,
                allowedOrigin,
                UriComponents.SchemeAndServer,
                UriFormat.Unescaped,
                StringComparison.OrdinalIgnoreCase
            ) != 0
        )
        {
            throw new InvalidOperationException(
                $"OIDC document address '{LoggingSanitizer.SanitizeFreeTextForLogging(address, MaxLoggedAddressLength)}' is not on the JwtAuthentication:MetadataAddress origin '{allowedOrigin.GetLeftPart(UriPartial.Authority)}'"
            );
        }

        var response = await httpClient.GetAsync(target, cancel);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancel);
    }
}
