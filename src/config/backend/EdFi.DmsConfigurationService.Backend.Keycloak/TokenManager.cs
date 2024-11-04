// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;

namespace EdFi.DmsConfigurationService.Backend.Keycloak;

public class TokenManager(KeycloakContext keycloakContext) : ITokenManager
{
    public async Task<TokenResult> GetAccessTokenAsync(IEnumerable<KeyValuePair<string, string>> credentials)
    {
        try
        {
            using var client = new HttpClient();

            var contentList = credentials.ToList();
            contentList.AddRange([new KeyValuePair<string, string>("grant_type", "client_credentials")]);

            var content = new FormUrlEncodedContent(contentList);
            string path =
                $"{keycloakContext.Url}/realms/{keycloakContext.Realm}/protocol/openid-connect/token";
            var response = await client.PostAsync(path, content);
            string responseString = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                var resultMap = new Dictionary<HttpStatusCode, Func<string, TokenResult>>
                {
                    { HttpStatusCode.Unauthorized, msg => new TokenResult.BadCredentials(msg) },
                    { HttpStatusCode.Forbidden, msg => new TokenResult.InsufficientPermissions(msg) },
                    { HttpStatusCode.NotFound, msg => new TokenResult.InvalidRealm(msg) }
                };

                if (resultMap.TryGetValue(response.StatusCode, out var resultFunc))
                {
                    return resultFunc(responseString);
                }

                return new TokenResult.FailureKeycloak(responseString);
            }

            return new TokenResult.Success(responseString);
        }
        catch (HttpRequestException ex)
        {
            return ex.StatusCode == null
                ? new TokenResult.KeycloakUnreachable(ex.Message)
                : new TokenResult.FailureKeycloak(ex.Message);
        }
        catch (Exception ex)
        {
            return new TokenResult.FailureUnknown(ex.Message);
        }
    }
}
