// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net.Http.Headers;
using System.Text.Json;
using EdFi.DataManagementService.Core.Security.Model;

namespace EdFi.DataManagementService.Core.Security;

public interface ISecurityMetadataProvider
{
    Task<IList<ClaimSet>> GetAllClaimSets();
}

public class SecurityMetadataProvider(
    ConfigurationServiceApiClient configurationServiceApiClient,
    IConfigurationServiceTokenHandler configurationServiceTokenHandler,
    ConfigurationServiceContext configurationServiceContext
) : ISecurityMetadataProvider
{
    private async Task SetAuthorizationHeader()
    {
        var token = await configurationServiceTokenHandler.GetTokenAsync(
            configurationServiceContext.clientId,
            configurationServiceContext.clientSecret,
            configurationServiceContext.scope
        );
        configurationServiceApiClient.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
    }

    public async Task<IList<ClaimSet>> GetAllClaimSets()
    {
        await SetAuthorizationHeader();
        var claimsEndpoint = "v2/claimSets";
        var response = await configurationServiceApiClient.Client.GetAsync(claimsEndpoint);
        var jsonString = await response.Content.ReadAsStringAsync();
        List<ClaimSet> claimSets = JsonSerializer.Deserialize<List<ClaimSet>>(jsonString) ?? [];
        return claimSets;
    }
}
