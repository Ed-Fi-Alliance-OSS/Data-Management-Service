// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using EdFi.DmsConfigurationService.Backend.AuthorizationMetadata;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Model.Token;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace EdFi.DmsConfigurationService.Backend.Introspection;

/// <summary>
/// Provides token introspection functionality for JWT tokens
/// </summary>
public class TokenInfoProvider(
    IEducationOrganizationRepository educationOrganizationRepository,
    IClaimsHierarchyRepository claimsHierarchyRepository,
    IAuthorizationMetadataResponseFactory authorizationMetadataResponseFactory,
    IApiClientRepository apiClientRepository,
    ILogger<TokenInfoProvider> logger
) : ITokenInfoProvider
{
    private readonly JwtSecurityTokenHandler _tokenHandler = new();

    public async Task<TokenInfoResponse?> GetTokenInfoAsync(string token)
    {
        try
        {
            // Decode JWT without validation (introspection endpoint doesn't validate, just inspects)
            if (!_tokenHandler.CanReadToken(token))
            {
                logger.LogWarning("Token cannot be read as a valid JWT");
                return null;
            }

            var jwtToken = _tokenHandler.ReadJwtToken(token);

            // Extract claims from token
            var claims = jwtToken.Claims.ToList();
            var clientId = GetClaimValue(claims, "client_id") ?? string.Empty;
            var claimSetName = GetClaimValue(claims, "scope") ?? string.Empty;
            var namespacePrefixes = GetClaimValues(claims, "namespacePrefixes");
            var educationOrganizationIds = GetEducationOrganizationIds(claims);

            // Check if token is expired
            var exp = jwtToken.ValidTo;
            bool isActive = exp > DateTime.UtcNow;

            if (!isActive)
            {
                logger.LogInformation(
                    "Token is expired. ClientId: {ClientId}, Expiration: {Expiration}",
                    clientId,
                    exp
                );
            }

            // Get ApiClient information to validate it exists
            var apiClientResult = await apiClientRepository.GetApiClientByClientId(clientId);
            if (apiClientResult is not Backend.Repositories.ApiClientGetResult.Success)
            {
                logger.LogWarning("Client not found for ClientId: {ClientId}", clientId);
                return null;
            }

            // Get education organizations
            var educationOrganizations = await educationOrganizationRepository.GetEducationOrganizationsAsync(
                educationOrganizationIds
            );

            // Get authorized resources from claims hierarchy
            var resources = await GetAuthorizedResourcesAsync(claimSetName);

            // Get authorized services from claims hierarchy
            var services = await GetAuthorizedServicesAsync(claimSetName);

            return new TokenInfoResponse
            {
                Active = isActive,
                ClientId = clientId,
                NamespacePrefixes = namespacePrefixes.ToList(),
                EducationOrganizations = educationOrganizations,
                AssignedProfiles = Array.Empty<string>(), // Profiles not currently supported in DMS
                ClaimSet = new TokenInfoClaimSet { Name = claimSetName },
                Resources = resources,
                Services = services,
            };
        }
        catch (SecurityTokenException ex)
        {
            logger.LogWarning(ex, "Security token exception while processing token");
            return null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error processing token info");
            return null;
        }
    }

    private static string? GetClaimValue(List<Claim> claims, string claimType)
    {
        return claims.FirstOrDefault(c => c.Type == claimType)?.Value;
    }

    private static IReadOnlyList<string> GetClaimValues(List<Claim> claims, string claimType)
    {
        var value = GetClaimValue(claims, claimType);
        if (string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<string>();
        }

        return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static IEnumerable<long> GetEducationOrganizationIds(List<Claim> claims)
    {
        var value = GetClaimValue(claims, "educationOrganizationIds");
        if (string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<long>();
        }

        return value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(id => long.TryParse(id, out var result) ? result : 0)
            .Where(id => id > 0);
    }

    private async Task<IReadOnlyList<TokenInfoResource>> GetAuthorizedResourcesAsync(string claimSetName)
    {
        try
        {
            // Get claims hierarchy
            var claimsHierarchyResult = await claimsHierarchyRepository.GetClaimsHierarchy();

            if (claimsHierarchyResult is not ClaimsHierarchyGetResult.Success success)
            {
                logger.LogWarning("Claims hierarchy not found");
                return Array.Empty<TokenInfoResource>();
            }

            // Get authorization metadata for the claim set
            var authorizationMetadata = await authorizationMetadataResponseFactory.Create(
                claimSetName,
                success.Claims
            );

            if (!authorizationMetadata.ClaimSets.Any())
            {
                logger.LogWarning("No claim sets found for: {ClaimSetName}", claimSetName);
                return Array.Empty<TokenInfoResource>();
            }

            var claimSet = authorizationMetadata.ClaimSets.First();
            var resources = new List<TokenInfoResource>();

            // Build resources from claims and authorizations
            foreach (var claim in claimSet.Claims)
            {
                var authorization = claimSet.Authorizations.FirstOrDefault(a => a.Id == claim.AuthorizationId);
                if (authorization == null)
                {
                    continue;
                }

                // Convert claim name to resource path format
                // Example: "http://ed-fi.org/ods/identity/claims/ed-fi/students" -> "/ed-fi/students"
                var resourcePath = ConvertClaimNameToResourcePath(claim.Name);

                var operations = authorization
                    .Actions.Select(a => a.Name)
                    .ToList();

                if (operations.Any())
                {
                    resources.Add(
                        new TokenInfoResource { Resource = resourcePath, Operations = operations }
                    );
                }
            }

            return resources;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving authorized resources for claim set: {ClaimSetName}", claimSetName);
            return Array.Empty<TokenInfoResource>();
        }
    }

    private async Task<IReadOnlyList<TokenInfoService>> GetAuthorizedServicesAsync(string claimSetName)
    {
        try
        {
            // Get claims hierarchy
            var claimsHierarchyResult = await claimsHierarchyRepository.GetClaimsHierarchy();

            if (claimsHierarchyResult is not ClaimsHierarchyGetResult.Success success)
            {
                logger.LogWarning("Claims hierarchy not found for services");
                return Array.Empty<TokenInfoService>();
            }

            // Get authorization metadata for the claim set
            var authorizationMetadata = await authorizationMetadataResponseFactory.Create(
                claimSetName,
                success.Claims
            );

            if (!authorizationMetadata.ClaimSets.Any())
            {
                logger.LogWarning("No claim sets found for services: {ClaimSetName}", claimSetName);
                return Array.Empty<TokenInfoService>();
            }

            var claimSet = authorizationMetadata.ClaimSets.First();
            var services = new List<TokenInfoService>();

            // Filter claims for services (following Ed-Fi ODS pattern)
            foreach (var claim in claimSet.Claims)
            {
                // Only process service claims
                if (!claim.Name.StartsWith(ClaimConstants.ServicesPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var authorization = claimSet.Authorizations.FirstOrDefault(a => a.Id == claim.AuthorizationId);
                if (authorization == null)
                {
                    continue;
                }

                // Extract service name by removing the prefix
                var serviceName = claim.Name.Substring(ClaimConstants.ServicesPrefix.Length);

                var operations = authorization
                    .Actions.Select(a => a.Name)
                    .ToList();

                if (operations.Any())
                {
                    services.Add(
                        new TokenInfoService { Service = serviceName, Operations = operations }
                    );
                }
            }

            return services;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving authorized services for claim set: {ClaimSetName}", claimSetName);
            return Array.Empty<TokenInfoService>();
        }
    }

    private static string ConvertClaimNameToResourcePath(string claimName)
    {
        // Extract resource name from claim URI
        // Example: "http://ed-fi.org/ods/identity/claims/ed-fi/students" -> "/ed-fi/students"
        // Example: "http://ed-fi.org/identity/claims/ed-fi/academicWeek" -> "/ed-fi/academicWeek"
        // Example: "http://ed-fi.org/ods/identity/claims/domains/edFiDescriptors" -> "/ed-fi/descriptors"

        // Try the standard ODS prefix first
        if (claimName.StartsWith(ClaimConstants.OdsIdentityClaimsPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var path = claimName.Substring(ClaimConstants.OdsIdentityClaimsPrefix.Length);

            // Handle special cases for domains
            if (path.StartsWith(ClaimConstants.DomainsPrefix, StringComparison.OrdinalIgnoreCase))
            {
                // Convert domains to their resource paths
                path = path.Replace(ClaimConstants.DomainsPrefix, "", StringComparison.OrdinalIgnoreCase);
                // Handle edFiDescriptors -> ed-fi/descriptors
                if (path.Equals("edFiDescriptors", StringComparison.OrdinalIgnoreCase))
                {
                    return "/ed-fi/descriptors";
                }
            }

            return "/" + path;
        }

        // Try the alternate identity claims prefix (without "ods")
        if (claimName.StartsWith(ClaimConstants.IdentityClaimsPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var path = claimName.Substring(ClaimConstants.IdentityClaimsPrefix.Length);
            return "/" + path;
        }

        // Fallback: return claim name as-is
        return claimName;
    }
}
