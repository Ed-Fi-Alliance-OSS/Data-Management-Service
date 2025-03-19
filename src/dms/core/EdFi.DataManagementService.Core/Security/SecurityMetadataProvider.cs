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
        var claimSets = JsonSerializer.Deserialize<List<ClaimSet>>(responseJsonString, _jsonOptions) ?? [];

        var claimSetAuthorizationMetadata = new List<ClaimSet>();

        foreach (var claimSet in claimSets)
        {
            var authorizationMetadata = await GetAuthorizationMetadataForClaimSet(claimSet.Name);
            if (authorizationMetadata != null)
            {
                var enrichedClaimSet = EnrichClaimSetWithAuthorizationMetadata(
                    claimSet,
                    authorizationMetadata
                );
                claimSetAuthorizationMetadata.Add(enrichedClaimSet);
            }
        }

        return claimSetAuthorizationMetadata;
    }

    private async Task<AuthorizationMetadataResponse?> GetAuthorizationMetadataForClaimSet(
        string claimSetName
    )
    {
        var authorizationMetadataEndpoint = $"/authorizationMetadata?claimSetName={claimSetName}";
        var response = await configurationServiceApiClient.Client.GetAsync(authorizationMetadataEndpoint);
        var jsonString = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<AuthorizationMetadataResponse>(jsonString, _jsonOptions);
    }

    private static ClaimSet EnrichClaimSetWithAuthorizationMetadata(
        ClaimSet claimSet,
        AuthorizationMetadataResponse authorizationMetadata
    )
    {
        var enrichedClaimSet = new ClaimSet(claimSet.Name, []);

        foreach (var claim in authorizationMetadata.Claims)
        {
            var authorization = authorizationMetadata.Authorizations.Find(a => a.Id == claim.AuthorizationId);
            if (authorization != null)
            {
                var resourceClaims = authorization
                    .Actions.Select(action => new ResourceClaim(
                        claim.Name,
                        action.Name,
                        action.AuthorizationStrategies
                    ))
                    .ToList();
                enrichedClaimSet.ResourceClaims.AddRange(resourceClaims);
            }
        }

        return enrichedClaimSet;
    }
}
