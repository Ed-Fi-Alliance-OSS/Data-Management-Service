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
    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

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
        var claimSetsResponse = await configurationServiceApiClient.Client.GetAsync(claimsEndpoint);
        var responseJsonString = await claimSetsResponse.Content.ReadAsStringAsync();
        List<ClaimSet> claimSets =
            JsonSerializer.Deserialize<List<ClaimSet>>(responseJsonString, _jsonOptions) ?? [];

        var claimSetAuthorizationMetadata = new List<ClaimSet>();
        foreach (var claimSetName in claimSets.Select(x => x.Name))
        {
            var authorizationMetadataEndpoint = $"/authorizationMetadata?claimSetName={claimSetName}";
            var response = await configurationServiceApiClient.Client.GetAsync(authorizationMetadataEndpoint);
            var jsonString = await response.Content.ReadAsStringAsync();
            var authorizationMetadataResponse = JsonSerializer.Deserialize<AuthorizationMetadataResponse>(
                jsonString,
                _jsonOptions
            );
            if (authorizationMetadataResponse != null)
            {
                var claimSet = new ClaimSet(claimSetName, []);
                foreach (var claim in authorizationMetadataResponse.Claims)
                {
                    var authorization = authorizationMetadataResponse.Authorizations.Find(a =>
                        a.Id == claim.AuthorizationId
                    );
                    if (authorization != null)
                    {
                        List<ResourceClaim> resourceClaims = [];
                        foreach (var action in authorization.Actions)
                        {
                            resourceClaims.Add(
                                new ResourceClaim(claim.Name, action.Name, action.AuthorizationStrategies)
                            );
                        }
                        claimSet.ResourceClaims.AddRange(resourceClaims);
                    }
                }
                claimSetAuthorizationMetadata.Add(claimSet);
            }
        }

        return claimSetAuthorizationMetadata;
    }
}
