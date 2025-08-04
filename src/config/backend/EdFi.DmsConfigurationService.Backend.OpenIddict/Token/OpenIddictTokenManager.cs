// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using System.Text;
using System.Text.Json;

namespace EdFi.DmsConfigurationService.Backend.OpenIddict.Token
{
    public class OpenIddictTokenManager : ITokenManager
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly string _tokenEndpoint;

        public OpenIddictTokenManager(IHttpClientFactory httpClientFactory, string tokenEndpoint)
        {
            _httpClientFactory = httpClientFactory;
            _tokenEndpoint = tokenEndpoint;
        }

        public OpenIddictTokenManager(IHttpClientFactory httpClientFactory)
        {
            _httpClientFactory = httpClientFactory;
            //TODO UPDATE VALUE
            _tokenEndpoint = "tokenEndpoint";
        }

        public async Task<TokenResult> GetAccessTokenAsync(IEnumerable<KeyValuePair<string, string>> credentials)
        {
            using var client = _httpClientFactory.CreateClient();
            using var content = new FormUrlEncodedContent(credentials);

            var response = await client.PostAsync(_tokenEndpoint, content);

            if (!response.IsSuccessStatusCode)
            {
                return new TokenResult.FailureUnknown($"Token endpoint returned {response.StatusCode}");
            }

            var json = await response.Content.ReadAsStringAsync();
            var tokenResponse = JsonSerializer.Deserialize<TokenResult>(json);
            return new TokenResult.Success(tokenResponse?.ToString() ?? String.Empty);
        }
    }
}
