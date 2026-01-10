// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Security;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.TokenInfo;

/// <summary>
/// Provides token introspection functionality for JWT tokens by querying Config Service and DMS databases
/// </summary>
public class TokenInfoProvider(
    ConfigurationServiceApiClient configClient,
    IConfigurationServiceTokenHandler tokenHandler,
    ConfigurationServiceContext configContext,
    IEducationOrganizationRepository educationOrganizationRepository,
    IDmsInstanceProvider dmsInstanceProvider,
    IDmsInstanceSelection dmsInstanceSelection,
    IApiSchemaProvider apiSchemaProvider,
    ILogger<TokenInfoProvider> logger
) : ITokenInfoProvider
{
    private const string ServicesClaimPrefix = "http://ed-fi.org/identity/claims/services/";
    private readonly JwtSecurityTokenHandler _tokenHandler = new();

    public async Task<TokenInfoResponse?> GetTokenInfoAsync(string token)
    {
        try
        {
            // 1. Decode JWT without validation (introspection endpoint doesn't validate, just inspects)
            if (!_tokenHandler.CanReadToken(token))
            {
                logger.LogWarning("Token cannot be read as a valid JWT");
                return null;
            }

            var jwtToken = _tokenHandler.ReadJwtToken(token);
            var claims = jwtToken.Claims.ToList();

            // Extract token data
            var clientId = GetClaimValue(claims, "client_id") ?? string.Empty;
            var claimSetName = GetClaimValue(claims, "scope") ?? string.Empty;
            var namespacePrefixes = GetClaimValues(claims, "namespacePrefixes");
            var educationOrganizationIds = GetEducationOrganizationIds(claims);
            var dmsInstanceIds = GetDmsInstanceIds(claims);

            // Check expiration
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

            // 2. Resolve DMS instance for education organization lookup
            // Note: If token has multiple DMS instance IDs, use the first one
            bool dmsInstanceSet = false;
            if (dmsInstanceIds.Any())
            {
                SetDmsInstance(dmsInstanceIds.First());
                dmsInstanceSet = true;
            }
            else
            {
                logger.LogWarning(
                    "Token does not contain dmsInstanceIds claim. Education organization details will not be available."
                );
            }

            // 3. Authenticate with Config Service
            await AuthenticateWithConfigServiceAsync();

            // 4. Get authorization metadata from Config Service
            var authMetadata = await GetAuthorizationMetadataAsync(claimSetName);
            if (authMetadata == null)
            {
                logger.LogWarning(
                    "Authorization metadata not found for claim set: {ClaimSetName}",
                    claimSetName
                );
                return null;
            }

            // 5. Get education organizations from DMS tenant database (only if instance is set)
            var educationOrganizations = dmsInstanceSet
                ? await GetEducationOrganizationsAsync(educationOrganizationIds)
                : Array.Empty<TokenInfoEducationOrganization>();

            // 5. Build response
            var (resources, services) = ExtractResourcesAndServices(authMetadata, claimSetName);

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
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error processing token info");
            return null;
        }
    }

    private async Task AuthenticateWithConfigServiceAsync()
    {
        // Get token for the Configuration Service API
        string? configurationServiceToken = await tokenHandler.GetTokenAsync(
            configContext.clientId,
            configContext.clientSecret,
            configContext.scope
        );

        configClient.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            configurationServiceToken
        );
    }

    private async Task<JsonNode?> GetAuthorizationMetadataAsync(string? claimSetName)
    {
        try
        {
            logger.LogDebug("Fetching authorization metadata for claim set: {ClaimSetName}", claimSetName);

            HttpResponseMessage response = await configClient.Client.GetAsync(
                $"/authorizationMetadata?claimSetName={Uri.EscapeDataString(claimSetName ?? "")}"
            );

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Configuration Service returned status code {StatusCode} for authorization metadata endpoint",
                    response.StatusCode
                );
            }

            response.EnsureSuccessStatusCode();

            string content = await response.Content.ReadAsStringAsync();

            logger.LogDebug(
                "Received authorization metadata response, deserializing {ByteCount} bytes",
                content.Length
            );

            return JsonNode.Parse(content);
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(
                ex,
                "HTTP request failed while fetching authorization metadata for claim set: {ClaimSetName}",
                claimSetName
            );
            return null;
        }
        catch (JsonException ex)
        {
            logger.LogError(
                ex,
                "Failed to parse authorization metadata response for claim set: {ClaimSetName}",
                claimSetName
            );
            return null;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Unexpected error while fetching authorization metadata for claim set: {ClaimSetName}",
                claimSetName
            );
            return null;
        }
    }

    private async Task<IReadOnlyList<TokenInfoEducationOrganization>> GetEducationOrganizationsAsync(
        IEnumerable<long> edOrgIds
    )
    {
        var ids = edOrgIds.ToList();
        if (!ids.Any())
        {
            return Array.Empty<TokenInfoEducationOrganization>();
        }

        try
        {
            logger.LogDebug(
                "Fetching education organizations for IDs: {EducationOrganizationIds}",
                string.Join(",", ids)
            );

            var result = await educationOrganizationRepository.GetEducationOrganizationsAsync(ids);

            logger.LogDebug(
                "Retrieved {Count} education organizations from DMS tenant database",
                result.Count
            );

            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Unexpected error while fetching education organizations for IDs: {EducationOrganizationIds}",
                string.Join(",", ids)
            );
            return Array.Empty<TokenInfoEducationOrganization>();
        }
    }

    private (IReadOnlyList<TokenInfoResource>, IReadOnlyList<TokenInfoService>) ExtractResourcesAndServices(
        JsonNode authMetadata,
        string claimSetName
    )
    {
        var resources = new List<TokenInfoResource>();
        var services = new List<TokenInfoService>();

        try
        {
            // Parse authorization metadata response
            // Structure: [{ claimSetName, claims: [...], authorizations: [...] }]
            if (authMetadata == null)
            {
                return (resources, services);
            }

            var claimSets = authMetadata.AsArray();
            var claimSet = claimSets.FirstOrDefault(cs =>
                cs?["claimSetName"]?.GetValue<string>() == claimSetName
            );

            if (claimSet == null)
            {
                logger.LogWarning(
                    "Claim set not found in authorization metadata: {ClaimSetName}",
                    claimSetName
                );
                return (resources, services);
            }

            var claimsArray = claimSet["claims"]?.AsArray() ?? new JsonArray();
            var authorizationsArray = claimSet["authorizations"]?.AsArray() ?? new JsonArray();

            foreach (var claim in claimsArray)
            {
                var claimName = claim?["name"]?.GetValue<string>();
                var authorizationId = claim?["authorizationId"]?.GetValue<int>();

                if (string.IsNullOrEmpty(claimName) || !authorizationId.HasValue)
                {
                    continue;
                }

                // Find matching authorization
                var authorization = authorizationsArray.FirstOrDefault(a =>
                    a?["id"]?.GetValue<int>() == authorizationId.Value
                );

                if (authorization == null)
                {
                    continue;
                }

                var actionsArray = authorization["actions"]?.AsArray() ?? new JsonArray();
                var operations = actionsArray
                    .Select(a => a?["name"]?.GetValue<string>())
                    .Where(n => !string.IsNullOrEmpty(n))
                    .Cast<string>()
                    .ToList();

                if (!operations.Any())
                {
                    continue;
                }

                // Check if service or resource claim
                if (claimName.StartsWith(ServicesClaimPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    var serviceName = claimName[ServicesClaimPrefix.Length..];
                    if (!string.IsNullOrEmpty(serviceName))
                    {
                        services.Add(new TokenInfoService { Service = serviceName, Operations = operations });
                    }
                }
                else
                {
                    // It's a resource claim - convert to resource path with pluralization
                    var resourcePath = ConvertClaimNameToResourcePath(claimName);
                    if (!string.IsNullOrEmpty(resourcePath))
                    {
                        resources.Add(
                            new TokenInfoResource { Resource = resourcePath, Operations = operations }
                        );
                    }
                }
            }

            return (resources, services);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error extracting resources and services from authorization metadata");
            return (Array.Empty<TokenInfoResource>(), Array.Empty<TokenInfoService>());
        }
    }

    private static string? GetClaimValue(List<Claim> claims, string claimType)
    {
        return claims.Find(c => c.Type == claimType)?.Value;
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
        var values = GetClaimValues(claims, "educationOrganizationIds");
        return values!.Select(id => long.TryParse(id, out var result) ? result : 0).Where(id => id > 0);
    }

    private static IEnumerable<long> GetDmsInstanceIds(List<Claim> claims)
    {
        var values = GetClaimValues(claims, "dmsInstanceIds");
        return values!.Select(id => long.TryParse(id, out var result) ? result : 0).Where(id => id > 0);
    }

    /// <summary>
    /// Sets the DMS instance for the current request context to enable database access
    /// </summary>
    private void SetDmsInstance(long instanceId)
    {
        try
        {
            logger.LogDebug("Resolving DMS instance {InstanceId} for token introspection", instanceId);

            var dmsInstance = dmsInstanceProvider.GetById(instanceId);
            if (dmsInstance == null)
            {
                logger.LogWarning("DMS instance {InstanceId} not found", instanceId);
                return;
            }

            dmsInstanceSelection.SetSelectedDmsInstance(dmsInstance);
            logger.LogDebug(
                "DMS instance {InstanceId} selected successfully for education organization lookup",
                instanceId
            );
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error setting DMS instance {InstanceId}", instanceId);
        }
    }

    /// <summary>
    /// Converts a claim name to a resource path using DMS's ApiSchema resource name mapping
    /// Example: "http://ed-fi.org/ods/identity/claims/ed-fi/student" -> "/ed-fi/students"
    /// </summary>
    private string ConvertClaimNameToResourcePath(string claimName)
    {
        const string OdsIdentityClaimsPrefix = "http://ed-fi.org/ods/identity/claims/";
        const string IdentityClaimsPrefix = "http://ed-fi.org/identity/claims/";
        const string DomainsPrefix = "domains/";

        string resourcePath;

        // Extract the resource name from the claim
        if (claimName.StartsWith(OdsIdentityClaimsPrefix, StringComparison.OrdinalIgnoreCase))
        {
            resourcePath = claimName[OdsIdentityClaimsPrefix.Length..];
        }
        else if (claimName.StartsWith(IdentityClaimsPrefix, StringComparison.OrdinalIgnoreCase))
        {
            resourcePath = claimName[IdentityClaimsPrefix.Length..];
        }
        else
        {
            // Fallback: return claim name as-is if it doesn't match expected prefixes
            return claimName;
        }

        // Handle special case for domains prefix (remove it)
        if (resourcePath.StartsWith(DomainsPrefix, StringComparison.OrdinalIgnoreCase))
        {
            resourcePath = resourcePath[DomainsPrefix.Length..];
        }

        // Convert resource path to ResourceName format expected by ApiSchema
        // The path might be "ed-fi/student" or just "student", we need to extract the last part
        var parts = resourcePath.Split('/');
        var resourceName = parts[^1]; // Get last part (e.g., "student" from "ed-fi/student")

        // Capitalize first letter to match ResourceName format (e.g., "student" -> "Student")
        // ApiSchema resourceNameMapping uses PascalCase resource names
        if (!string.IsNullOrEmpty(resourceName))
        {
            resourceName = char.ToUpper(resourceName[0]) + (resourceName.Length > 1 ? resourceName[1..] : "");
        }

        try
        {
            // Get the ApiSchema and ProjectSchema
            var apiSchemaNodes = apiSchemaProvider.GetApiSchemaNodes();
            var apiSchemaDocuments = new ApiSchemaDocuments(apiSchemaNodes, logger);
            var coreProjectSchema = apiSchemaDocuments.GetCoreProjectSchema();

            // Convert to ResourceName and get the endpoint name from ApiSchema
            var resourceNameTyped = new ResourceName(resourceName);
            var endpointName = coreProjectSchema.GetEndpointNameFromResourceName(resourceNameTyped);

            // Construct the full path with project endpoint prefix
            return $"/{coreProjectSchema.ProjectEndpointName.Value}/{endpointName.Value}";
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to convert claim name '{ClaimName}' to resource path using ApiSchema, using empty",
                claimName
            );
            // Fallback to original behavior if ApiSchema lookup fails
            return string.Empty;
        }
    }
}
