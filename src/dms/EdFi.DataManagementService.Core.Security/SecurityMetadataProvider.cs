// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Core.Security;

public interface ISecurityMetadataProvider
{
    Task<IList<string>> GetAllClaimSets();
}

public class SecurityMetadataProvider(
    ConfigurationServiceApiClient configurationServiceApiClient,
    ConfigurationServiceTokenHandler configurationServiceTokenHandler,
    ConfigurationServiceContext configurationServiceContext
) : ISecurityMetadataProvider
{
    private async Task<HttpRequestMessage> ApiRequest(HttpMethod httpMethod, string url)
    {
        var token = await configurationServiceTokenHandler.GetTokenAsync(
            configurationServiceContext.key,
            configurationServiceContext.secret,
            configurationServiceContext.scope
        );
        var requestMessage = new HttpRequestMessage { Method = httpMethod, RequestUri = new Uri(url) };
        var authHeader = $"Bearer {token}";

        requestMessage.Headers.Authorization = AuthenticationHeaderValue.Parse(authHeader);
        return requestMessage;
    }

    public async Task<IList<string>> GetAllClaimSets()
    {
        List<string> claimSets = [];
        var request = await ApiRequest(HttpMethod.Get, "/v2/claimSets");
        var response = await configurationServiceApiClient.HttpClient!.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var jsonString = await response.Content.ReadAsStringAsync();
        var itemList = JsonArray.Parse(jsonString);
        if (itemList != null)
        {
            foreach (var item in itemList.AsArray())
            {
                var name = item?["name"]?.ToString();
                if (name != null)
                {
                    claimSets.Add(name);
                }
            }
        }
        return claimSets;
    }
}
