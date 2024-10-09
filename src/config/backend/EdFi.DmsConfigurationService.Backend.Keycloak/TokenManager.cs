// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.Keycloak;

public class TokenManager(KeycloakContext keycloakContext) : ITokenManager
{
    public async Task<string> GetAccessTokenAsync(IEnumerable<KeyValuePair<string, string>> credentials)
    {
        using var client = new HttpClient();

        var contentList = credentials.ToList();
        contentList.AddRange(
            [new KeyValuePair<string, string>("grant_type", "client_credentials")]);

        var content = new FormUrlEncodedContent(contentList);
        var path = $"{keycloakContext.Url}/realms/{keycloakContext.Realm}/protocol/openid-connect/token";
        var response = await client.PostAsync(path, content);
        var responseString = await response.Content.ReadAsStringAsync();

        if (response.IsSuccessStatusCode)
        {
            return responseString;
        }
        else
        {
            throw new Exception(responseString);
        }
    }
}
