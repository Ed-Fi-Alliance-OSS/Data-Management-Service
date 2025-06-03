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

        // Retrieve all claim sets with their authorization metadata in one call by omitting claimSetName
        string authorizationMetadataEndpoint = "/authorizationMetadata";
        HttpResponseMessage response = await configurationServiceApiClient.Client.GetAsync(
            authorizationMetadataEndpoint
        );
        string jsonString = await response.Content.ReadAsStringAsync();

        // Deserialize as a collection of authorization metadata responses (one per claim set)
        IList<ClaimSetMetadata> allClaimSets =
            JsonSerializer.Deserialize<IList<ClaimSetMetadata>>(jsonString, _jsonOptions)
            ?? new List<ClaimSetMetadata>();

        List<ClaimSet> claimSets = [];

        foreach (var claimSetMetadata in allClaimSets)
        {
            var claimSetName = claimSetMetadata.ClaimSetName;

            var claimSet = new ClaimSet(claimSetName, []);

            foreach (ClaimSetMetadata.Claim claim in claimSetMetadata.Claims)
            {
                ClaimSetMetadata.Authorization? authorization = claimSetMetadata.Authorizations.Find(a =>
                    a.Id == claim.AuthorizationId
                );

                if (authorization != null)
                {
                    List<ResourceClaim> resourceClaims = authorization
                        .Actions.Select(action => new ResourceClaim(
                            claim.Name,
                            action.Name,
                            action.AuthorizationStrategies
                        ))
                        .ToList();

                    claimSet.ResourceClaims.AddRange(resourceClaims);
                }
            }

            claimSets.Add(claimSet);
        }

        return claimSets;
    }
}
