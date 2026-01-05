// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net.Http.Json;
using EdFi.DataManagementService.Core.Configuration;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Security;

public interface IConfigurationServiceTokenHandler
{
    Task<string?> GetTokenAsync(string clientId, string clientSecret, string scope);
}

/// <summary>
/// Handles OAuth token retrieval and caching for Configuration Service API calls.
/// Uses HybridCache for stampede protection - only one request fetches token on cache miss.
/// </summary>
public class ConfigurationServiceTokenHandler(
    HybridCache hybridCache,
    CacheSettings cacheSettings,
    ConfigurationServiceApiClient configurationServiceApiClient,
    ILogger<ConfigurationServiceTokenHandler> logger
) : IConfigurationServiceTokenHandler
{
    private const string TokenCacheKey = "ConfigServiceToken";

    public async Task<string?> GetTokenAsync(string clientId, string clientSecret, string scope)
    {
        // HybridCache.GetOrCreateAsync provides stampede protection:
        // Only one concurrent caller executes the factory; others wait for the result
        return await hybridCache.GetOrCreateAsync(
            TokenCacheKey,
            async cancel => await FetchTokenAsync(clientId, clientSecret, scope, cancel),
            new HybridCacheEntryOptions
            {
                Expiration = TimeSpan.FromMinutes(cacheSettings.TokenCacheExpirationMinutes),
                LocalCacheExpiration = TimeSpan.FromMinutes(cacheSettings.TokenCacheExpirationMinutes),
            }
        );
    }

    private async Task<string?> FetchTokenAsync(
        string clientId,
        string clientSecret,
        string scope,
        CancellationToken cancellationToken
    )
    {
        logger.LogDebug("Cache miss - fetching new token from Configuration service");

        var urlEncodedData = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("client_id", clientId),
                new KeyValuePair<string, string>("client_secret", clientSecret),
                new KeyValuePair<string, string>("grant_type", "client_credentials"),
                new KeyValuePair<string, string>("scope", scope),
            ]
        );

        var response = await configurationServiceApiClient.Client.PostAsync(
            "connect/token",
            urlEncodedData,
            cancellationToken
        );
        var tokenResponse = await response.Content.ReadFromJsonAsync<BearerToken>(cancellationToken);
        var token = tokenResponse?.Access_token;

        if (string.IsNullOrEmpty(token))
        {
            logger.LogWarning("Received empty or null token from Configuration service");
        }

        return token;
    }
}

public class BearerToken
{
    public string? Access_token { get; set; }

    public string? Token_type { get; set; }

    public double Expires_in { get; set; }
}
