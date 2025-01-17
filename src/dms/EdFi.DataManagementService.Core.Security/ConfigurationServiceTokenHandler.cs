// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net.Http.Json;
using Microsoft.Extensions.Caching.Memory;

namespace EdFi.DataManagementService.Core.Security;

public interface IConfigurationServiceTokenHandler
{
    Task<string?> GetTokenAsync(string key, string secret, string scope);
}

public class ConfigurationServiceTokenHandler : IConfigurationServiceTokenHandler
{
    private readonly IMemoryCache _configServiceTokenCache;
    private readonly ConfigurationServiceApiClient _httpClient;
    private const string TokenCacheKey = "ConfigServiceToken";

    public ConfigurationServiceTokenHandler(
        IMemoryCache configServiceTokenCache,
        ConfigurationServiceApiClient httpClient
    )
    {
        _configServiceTokenCache = configServiceTokenCache;
        _httpClient = httpClient;
    }

    public async Task<string?> GetTokenAsync(string key, string secret, string scope)
    {
        if (_configServiceTokenCache.TryGetValue(TokenCacheKey, out string? token))
        {
            return token;
        }
        var urlEncodedData = new Dictionary<string, string>
        {
            { "client_id", key },
            { "client_secret", secret },
            { "grant_type", "client_credentials" },
            { "scope", scope },
        };
        var content = new FormUrlEncodedContent(urlEncodedData);

        var requestMessage = new HttpRequestMessage
        {
            Method = HttpMethod.Post,
            Content = content,
            RequestUri = new Uri("/connect/token"),
        };

        var response = await _httpClient.HttpClient!.SendAsync(requestMessage);
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
