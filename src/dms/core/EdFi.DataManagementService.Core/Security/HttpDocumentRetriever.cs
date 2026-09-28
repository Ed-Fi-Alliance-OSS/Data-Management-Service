// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;

namespace EdFi.DataManagementService.Core.Security;

/// <summary>
/// Document retriever implementation for fetching OIDC metadata
/// </summary>
/// <param name="httpClient">
/// The client used for every fetch. It must not follow redirects (see <see cref="HttpClientName"/>),
/// or an on-origin address could hand the fetch to another origin after the check below.
/// </param>
/// <param name="metadataAddress">
/// The full JwtAuthentication:MetadataAddress. Its origin (scheme, host and port) is the only one
/// fetched from: the same retriever fetches the metadata document and then the jwks_uri that
/// document names, so refusing any other origin stops a tampered document from pointing DMS at
/// signing keys served elsewhere, even when its issuer is the configured one. A fetch of exactly
/// this address is the metadata GET, which does not end a refusal episode (see the logger
/// parameter), so pass the full address, not just its origin.
/// </param>
/// <param name="logger">
/// Logs each origin refusal. A refusal during a background refresh is swallowed by the
/// configuration manager, so this log entry is its only trace. Only the first refusal of an episode
/// is logged at Error: once the automatic refresh is due, a failed refresh is retried on every
/// request, so later refusals are logged at Debug until a signing-key fetch succeeds.
/// </param>
internal class HttpDocumentRetriever(
    HttpClient httpClient,
    Uri metadataAddress,
    ILogger<HttpDocumentRetriever> logger
) : IDocumentRetriever
{
    /// <summary>
    /// The named HttpClient registration for OIDC fetches, whose primary handler does not follow
    /// redirects.
    /// </summary>
    internal const string HttpClientName = "EdFi.DataManagementService.OidcMetadata";

    private const int MaxLoggedAddressLength = 256;

    // 1 while an episode of refusals has already been logged at Error; see the logger parameter.
    private int _refusalLogged;

    /// <summary>
    /// Retrieves a document from the specified address
    /// </summary>
    public async Task<string> GetDocumentAsync(string address, CancellationToken cancel)
    {
        // SECURITY CRITICAL: checked before any request is sent, and the client does not follow
        // redirects, so a rejected address is never contacted and its document is never adopted.
        // The scheme is part of the origin, so with an https MetadataAddress this also refuses
        // every http address; AddJwtAuthentication requires an https MetadataAddress when
        // RequireHttpsMetadata is set.
        if (
            !Uri.TryCreate(address, UriKind.Absolute, out Uri? target)
            || Uri.Compare(
                target,
                metadataAddress,
                UriComponents.SchemeAndServer,
                UriFormat.Unescaped,
                StringComparison.OrdinalIgnoreCase
            ) != 0
        )
        {
            string sanitizedAddress = LoggingSanitizer.SanitizeFreeTextForLogging(
                address,
                MaxLoggedAddressLength
            );
            string origin = metadataAddress.GetLeftPart(UriPartial.Authority);

            LogLevel level =
                Interlocked.Exchange(ref _refusalLogged, 1) == 0 ? LogLevel.Error : LogLevel.Debug;

            logger.Log(
                level,
                "Refused OIDC document address {DocumentAddress}: it is not on the JwtAuthentication:MetadataAddress origin {AllowedOrigin}",
                sanitizedAddress,
                origin
            );

            throw new InvalidOperationException(
                $"OIDC document address '{sanitizedAddress}' is not on the JwtAuthentication:MetadataAddress origin '{origin}'"
            );
        }

        var response = await httpClient.GetAsync(target, cancel);
        response.EnsureSuccessStatusCode();

        // Any address other than the MetadataAddress is a signing-key (jwks_uri) fetch. Its
        // success ends a refusal episode; the metadata GET cannot, because it succeeds on every
        // attempt, including the ones whose jwks_uri is then refused.
        if (target != metadataAddress)
        {
            Interlocked.Exchange(ref _refusalLogged, 0);
        }

        return await response.Content.ReadAsStringAsync(cancel);
    }
}
