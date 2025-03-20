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
        string? token = await configurationServiceTokenHandler.GetTokenAsync(
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

        string claimsEndpoint = "v2/claimSets";
        HttpResponseMessage claimSetsResponse = await configurationServiceApiClient.Client.GetAsync(
            claimsEndpoint
        );
        string responseJsonString = await claimSetsResponse.Content.ReadAsStringAsync();
        List<ClaimSet> claimSets =
            JsonSerializer.Deserialize<List<ClaimSet>>(responseJsonString, _jsonOptions) ?? [];

        List<ClaimSet> claimSetAuthorizationMetadata = [];

        foreach (ClaimSet claimSet in claimSets)
        {
            AuthorizationMetadataResponse? authorizationMetadata = await GetAuthorizationMetadataForClaimSet(
                claimSet.Name
            );
            if (authorizationMetadata != null)
            {
                ClaimSet enrichedClaimSet = EnrichClaimSetWithAuthorizationMetadata(
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
        string authorizationMetadataEndpoint = $"/authorizationMetadata?claimSetName={claimSetName}";
        HttpResponseMessage response = await configurationServiceApiClient.Client.GetAsync(
            authorizationMetadataEndpoint
        );
        string jsonString = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<AuthorizationMetadataResponse>(jsonString, _jsonOptions);
    }

    private static ClaimSet EnrichClaimSetWithAuthorizationMetadata(
        ClaimSet claimSet,
        AuthorizationMetadataResponse authorizationMetadata
    )
    {
        ClaimSet enrichedClaimSet = new(claimSet.Name, []);

        foreach (AuthorizationMetadataResponse.Claim claim in authorizationMetadata.Claims)
        {
            AuthorizationMetadataResponse.Authorization? authorization =
                authorizationMetadata.Authorizations.Find(a => a.Id == claim.AuthorizationId);
            if (authorization != null)
            {
                List<ResourceClaim> resourceClaims = authorization
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
