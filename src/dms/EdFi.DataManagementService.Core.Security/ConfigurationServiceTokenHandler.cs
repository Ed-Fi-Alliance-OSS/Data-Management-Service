// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net.Http.Json;
using Microsoft.Extensions.Caching.Memory;

namespace EdFi.DataManagementService.Core.Security;

public interface IConfigurationServiceTokenHandler
{
    Task<string?> GetTokenAsync(string clientId, string clientSecret, string scope);
}

public class ConfigurationServiceTokenHandler(
    IMemoryCache configServiceTokenCache,
    ConfigurationServiceApiClient configurationServiceApiClient
) : IConfigurationServiceTokenHandler
{
    private readonly IMemoryCache _configServiceTokenCache = configServiceTokenCache;
    private readonly ConfigurationServiceApiClient _configurationServiceApiClient =
        configurationServiceApiClient;
    private const string TokenCacheKey = "ConfigServiceToken";

    public async Task<string?> GetTokenAsync(string clientId, string clientSecret, string scope)
    {
        if (_configServiceTokenCache.TryGetValue(TokenCacheKey, out string? token))
        {
            return token;
        }
        var urlEncodedData = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("client_id", clientId),
                new KeyValuePair<string, string>("client_secret", clientSecret),
                new KeyValuePair<string, string>("grant_type", "client_credentials"),
                new KeyValuePair<string, string>("scope", scope),
            ]
        );
        var response = await _configurationServiceApiClient.Client.PostAsync("connect/token", urlEncodedData);
        response.EnsureSuccessStatusCode();

        var tokenResponse = await response.Content.ReadFromJsonAsync<BearerToken>();

        token = tokenResponse?.Access_token;

        if (!string.IsNullOrEmpty(token))
        {
            var expires_in = tokenResponse != null ? tokenResponse.Expires_in : 1800;
            _configServiceTokenCache.Set(TokenCacheKey, token, TimeSpan.FromSeconds(expires_in));
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
