// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net.Http.Headers;
using System.Text.Json;
using EdFi.DataManagementService.Core.Security.Model;

namespace EdFi.DataManagementService.Core.Security;

/// <summary>
/// Retrieves claim set metadata from the Configuration Service API
/// and transforms into claim sets
/// </summary>
public class ConfigurationServiceClaimSetProvider(
    ConfigurationServiceApiClient configurationServiceApiClient,
    IConfigurationServiceTokenHandler configurationServiceTokenHandler,
    ConfigurationServiceContext configurationServiceContext
) : IClaimSetProvider
{
    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Retrieves claim set metadata from the Configuration Service API, transforming
    /// into the internal ClaimSet model used by the DMS authorization pipeline.
    /// </summary>
    public async Task<IList<ClaimSet>> GetAllClaimSets()
    {
        /// Get token for the Configuration Service API
        string? configurationServiceToken = await configurationServiceTokenHandler.GetTokenAsync(
            configurationServiceContext.clientId,
            configurationServiceContext.clientSecret,
            configurationServiceContext.scope
        );
        configurationServiceApiClient.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", configurationServiceToken);

        return (await FetchAuthorizationMetadata()).Select(CreateClaimSet).ToList();
    }

    /// <summary>
    /// Fetches claim set metadata from the Configuration Service API.
    /// </summary>
    private async Task<IList<ClaimSetMetadata>> FetchAuthorizationMetadata()
    {
        // Retrieve all claim sets with their authorization metadata in one call by omitting claimSetName
        string authorizationMetadataEndpoint = "authorizationMetadata";
        HttpResponseMessage response = await configurationServiceApiClient.Client.GetAsync(
            authorizationMetadataEndpoint
        );

        string claimSetMetadataJson = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<IList<ClaimSetMetadata>>(claimSetMetadataJson, _jsonOptions) ?? [];
    }

    /// <summary>
    /// Creates a single ClaimSet from ClaimSetMetadata by mapping CMS.Claims to their CSM.Authorizations.
    /// </summary>
    private static ClaimSet CreateClaimSet(ClaimSetMetadata claimSetMetadata)
    {
        List<ResourceClaim> resourceClaims = claimSetMetadata
            .Claims.SelectMany(claim =>
                GetResourceClaimsForClaimSetMetadataClaim(claimSetMetadata.Authorizations, claim)
            )
            .ToList();

        return new(claimSetMetadata.ClaimSetName, resourceClaims);
    }

    /// <summary>
    /// Gets the ResourceClaims for a CMS.Claim by finding its associated CSM.Authorization.
    /// </summary>
    private static IEnumerable<ResourceClaim> GetResourceClaimsForClaimSetMetadataClaim(
        List<ClaimSetMetadata.Authorization> authorizations,
        ClaimSetMetadata.Claim claim
    )
    {
        ClaimSetMetadata.Authorization? authorization = authorizations.Find(auth =>
            auth.Id == claim.AuthorizationId
        );
        return authorization == null ? [] : CreateResourceClaims(claim, authorization);
    }

    /// <summary>
    /// Creates ResourceClaims from a CSM.Claim and its associated CSM.Authorization.
    /// </summary>
    private static IEnumerable<ResourceClaim> CreateResourceClaims(
        ClaimSetMetadata.Claim claim,
        ClaimSetMetadata.Authorization authorization
    )
    {
        return authorization.Actions.Select(action => new ResourceClaim(
            claim.Name,
            action.Name,
            action.AuthorizationStrategies
        ));
    }
}
