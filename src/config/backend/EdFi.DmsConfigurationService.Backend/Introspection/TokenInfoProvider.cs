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

            // Get authorized resources and services from claims hierarchy (single call)
            var (resources, services) = await GetAuthorizedResourcesAndServicesAsync(claimSetName);

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

    /// <summary>
    /// Retrieves both authorized resources and services in a single call to avoid duplicate
    /// database queries and processing for the claims hierarchy and authorization metadata.
    /// </summary>
    private async Task<(IReadOnlyList<TokenInfoResource> Resources, IReadOnlyList<TokenInfoService> Services)>
        GetAuthorizedResourcesAndServicesAsync(string claimSetName)
    {
        try
        {
            // Get claims hierarchy once
            var claimsHierarchyResult = await claimsHierarchyRepository.GetClaimsHierarchy();

            if (claimsHierarchyResult is not ClaimsHierarchyGetResult.Success success)
            {
                logger.LogWarning("Claims hierarchy not found");
                return (Array.Empty<TokenInfoResource>(), Array.Empty<TokenInfoService>());
            }

            // Get authorization metadata once
            var authorizationMetadata = await authorizationMetadataResponseFactory.Create(
                claimSetName,
                success.Claims
            );

            if (!authorizationMetadata.ClaimSets.Any())
            {
                logger.LogWarning("No claim sets found for: {ClaimSetName}", claimSetName);
                return (Array.Empty<TokenInfoResource>(), Array.Empty<TokenInfoService>());
            }

            var claimSet = authorizationMetadata.ClaimSets.First();
            var resources = new List<TokenInfoResource>();
            var services = new List<TokenInfoService>();

            // Build resources and services from claims and authorizations
            foreach (var claim in claimSet.Claims)
            {
                var authorization = claimSet.Authorizations.FirstOrDefault(a => a.Id == claim.AuthorizationId);
                if (authorization == null)
                {
                    continue;
                }

                var operations = authorization
                    .Actions.Select(a => a.Name)
                    .ToList();

                if (!operations.Any())
                {
                    continue;
                }

                // Check if this is a service claim
                if (claim.Name.StartsWith(ClaimConstants.ServicesPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    var serviceName = claim.Name[ClaimConstants.ServicesPrefix.Length..];
                    services.Add(new TokenInfoService { Service = serviceName, Operations = operations });
                }
                else
                {
                    // It's a resource claim
                    var resourcePath = ConvertClaimNameToResourcePath(claim.Name);
                    resources.Add(new TokenInfoResource { Resource = resourcePath, Operations = operations });
                }
            }

            return (resources, services);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving authorized resources and services for claim set: {ClaimSetName}", claimSetName);
            return (Array.Empty<TokenInfoResource>(), Array.Empty<TokenInfoService>());
        }
    }

    private static string ConvertClaimNameToResourcePath(string claimName)
    {
        // Extract resource name from claim URI
        // Example: "http://ed-fi.org/ods/identity/claims/ed-fi/student" -> "/ed-fi/students"
        // Example: "http://ed-fi.org/identity/claims/ed-fi/academicWeek" -> "/ed-fi/academicWeeks"
        // Example: "http://ed-fi.org/ods/identity/claims/domains/edFiDescriptors" -> "/ed-fi/descriptors"

        // Try the standard ODS prefix first
        if (claimName.StartsWith(ClaimConstants.OdsIdentityClaimsPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var path = claimName[ClaimConstants.OdsIdentityClaimsPrefix.Length..];

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

            return "/" + PluralizePath(path);
        }

        // Try the alternate identity claims prefix (without "ods")
        if (claimName.StartsWith(ClaimConstants.IdentityClaimsPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var path = claimName[ClaimConstants.IdentityClaimsPrefix.Length..];
            return "/" + PluralizePath(path);
        }

        // Fallback: return claim name as-is
        return claimName;
    }

    /// <summary>
    /// Pluralizes and converts the last segment of a resource path to camelCase
    /// Example: "ed-fi/Student" -> "ed-fi/students"
    /// Example: "ed-fi/AcademicWeek" -> "ed-fi/academicWeeks"
    /// Example: "ed-fi/StudentSchoolAssociation" -> "ed-fi/studentSchoolAssociations"
    /// Uses simple pluralization rules matching ClaimsFragmentComposer.PluralToSingular
    /// and camelCase convention per Ed-Fi OpenAPI/REST standards
    /// </summary>
    private static string PluralizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        var lastSlashIndex = path.LastIndexOf('/');
        if (lastSlashIndex == -1)
        {
            // No slash, pluralize and camelCase the whole path
            return ToCamelCasePlural(path);
        }

        // Split into prefix and last segment
        var prefix = path[..(lastSlashIndex + 1)];
        var lastSegment = path[(lastSlashIndex + 1)..];

        return prefix + ToCamelCasePlural(lastSegment);
    }

    /// <summary>
    /// Converts a resource name to camelCase and pluralizes it
    /// Example: "Student" -> "students"
    /// Example: "AcademicWeek" -> "academicWeeks"
    /// Example: "Category" -> "categories"
    /// Example: "students" -> "students" (already plural/camelCase, no change)
    /// Follows Ed-Fi REST API conventions: lowercase first letter + pluralization
    /// </summary>
    private static string ToCamelCasePlural(string word)
    {
        if (string.IsNullOrWhiteSpace(word))
        {
            return word;
        }

        // First, convert to camelCase (lowercase first letter, keep rest as-is)
        string camelCase = word.Length switch
        {
            0 => word,
            1 => word.ToLower(),
            _ => char.ToLower(word[0]) + word[1..],
        };

        // If already ends with 's', assume it's already plural (e.g., "students")
        // This handles claims that already have plural form in the claim URI
        if (camelCase.EndsWith("s", StringComparison.OrdinalIgnoreCase))
        {
            return camelCase;
        }

        // Then pluralize
        // Rule 1: Words ending in 'y' (not preceded by vowel) -> 'ies'
        // Example: "category" -> "categories"
        if (camelCase.EndsWith("y", StringComparison.OrdinalIgnoreCase) && camelCase.Length > 1)
        {
            char precedingChar = camelCase[^2];
            if (!"aeiouAEIOU".Contains(precedingChar))
            {
                return camelCase[..^1] + "ies";
            }
        }

        // Rule 2: All other words -> append 's'
        // Example: "student" -> "students", "academicWeek" -> "academicWeeks"
        return camelCase + "s";
    }
}
