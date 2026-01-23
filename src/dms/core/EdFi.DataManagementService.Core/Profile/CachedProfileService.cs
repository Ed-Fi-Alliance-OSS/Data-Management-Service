// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.OpenApi;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Profile;

/// <summary>
/// Cached data for an application's profiles.
/// An empty instance (no profiles) is cached to distinguish "no profiles assigned"
/// from "not yet cached".
/// </summary>
internal record CachedApplicationProfiles(
    IReadOnlyDictionary<string, ProfileDefinition> ProfilesByName,
    IReadOnlyList<string> AssignedProfileNames
)
{
    /// <summary>
    /// Represents an application with no profiles assigned.
    /// </summary>
    public static CachedApplicationProfiles Empty { get; } =
        new(new Dictionary<string, ProfileDefinition>(), Array.Empty<string>());

    /// <summary>
    /// Returns true if no profiles are assigned to this application.
    /// </summary>
    public bool IsEmpty => AssignedProfileNames.Count == 0;
}

/// <summary>
/// Cached profile catalog containing all profiles for a tenant.
/// Provides O(1) lookup by profile name.
/// </summary>
internal record CachedProfileCatalog(
    IReadOnlyDictionary<string, ProfileDefinition> ProfilesByName,
    IReadOnlyDictionary<long, ProfileDefinition> ProfilesById,
    IReadOnlyList<string> ProfileNames
)
{
    /// <summary>
    /// Represents an empty catalog (no profiles defined).
    /// </summary>
    public static CachedProfileCatalog Empty { get; } =
        new(new Dictionary<string, ProfileDefinition>(), new Dictionary<long, ProfileDefinition>(), []);

    /// <summary>
    /// Returns true if the catalog is empty.
    /// </summary>
    public bool IsEmpty => ProfileNames.Count == 0;

    /// <summary>
    /// Attempts to get a profile by name (case-insensitive).
    /// </summary>
    public bool TryGetProfile(string profileName, out ProfileDefinition? definition) =>
        ProfilesByName.TryGetValue(profileName.ToLowerInvariant(), out definition);

    /// <summary>
    /// Attempts to get a profile by id.
    /// </summary>
    public bool TryGetProfile(long profileId, out ProfileDefinition? definition) =>
        ProfilesById.TryGetValue(profileId, out definition);
}

/// <summary>
/// Profile service with caching and stampede protection.
/// Provides profile resolution for requests, catalog-level access to profiles,
/// and cached profile-filtered OpenAPI specifications.
/// </summary>
internal class CachedProfileService(
    IProfileCmsProvider profileCmsProvider,
    HybridCache hybridCache,
    CacheSettings cacheSettings,
    ILogger<CachedProfileService> logger
) : IProfileService
{
    private const string ApplicationProfilesCacheKeyPrefix = "ApplicationProfiles";
    private const string ProfileCatalogCacheKeyPrefix = "ProfileCatalog";
    private const string ProfileOpenApiCacheKeyPrefix = "ProfileOpenApi";

    private static string GetApplicationCacheKey(string? tenantId, long applicationId)
    {
        return string.IsNullOrEmpty(tenantId)
            ? $"{ApplicationProfilesCacheKeyPrefix}:{applicationId}"
            : $"{ApplicationProfilesCacheKeyPrefix}:{tenantId}:{applicationId}";
    }

    private static string GetCatalogCacheKey(string? tenantId) =>
        string.IsNullOrEmpty(tenantId)
            ? ProfileCatalogCacheKeyPrefix
            : $"{ProfileCatalogCacheKeyPrefix}:{tenantId}";

    private static string GetOpenApiCacheKey(string? tenantId, string profileName, Guid apiSchemaReloadId)
    {
        string normalizedProfileName = profileName.ToLowerInvariant();
        return string.IsNullOrEmpty(tenantId)
            ? $"{ProfileOpenApiCacheKeyPrefix}:{normalizedProfileName}:{apiSchemaReloadId}"
            : $"{ProfileOpenApiCacheKeyPrefix}:{tenantId}:{normalizedProfileName}:{apiSchemaReloadId}";
    }

    /// <inheritdoc />
    public async Task<ProfileResolutionResult> ResolveProfileAsync(
        ParsedProfileHeader? parsedHeader,
        RequestMethod method,
        string resourceName,
        long applicationId,
        string? tenantId
    )
    {
        // Get cached profiles for this application
        CachedApplicationProfiles cachedProfiles = await GetOrFetchApplicationProfilesAsync(
            applicationId,
            tenantId
        );

        // If no profiles are assigned to the application
        if (cachedProfiles.IsEmpty)
        {
            // No profiles assigned - if header was provided, validate the profile exists
            if (parsedHeader != null)
            {
                return await ValidateExplicitProfileWithoutAssignment(parsedHeader, method);
            }

            // No profiles assigned and no header - no profile applies
            return ProfileResolutionResult.NoProfileApplies();
        }

        // Profiles are assigned to this application
        if (parsedHeader != null)
        {
            // Explicit profile header provided - validate and resolve
            return ValidateExplicitProfile(parsedHeader, method, resourceName, cachedProfiles);
        }

        // No header provided - check for implicit profile selection
        return HandleImplicitProfileSelection(method, resourceName, cachedProfiles);
    }

    /// <inheritdoc />
    public async Task<CachedApplicationProfiles> GetOrFetchApplicationProfilesAsync(
        long applicationId,
        string? tenantId
    )
    {
        string cacheKey = GetApplicationCacheKey(tenantId, applicationId);

        // HybridCache.GetOrCreateAsync returns the cached value or the result of the factory.
        // We always return a non-null value from the factory, but use null-coalescing for safety.
        return await hybridCache.GetOrCreateAsync(
                cacheKey,
                async cancel =>
                {
                    logger.LogDebug(
                        "Cache miss for application profiles, fetching from CMS. ApplicationId: {ApplicationId}",
                        applicationId
                    );

                    ApplicationProfileInfo? appInfo = await profileCmsProvider.GetApplicationProfileInfoAsync(
                        applicationId,
                        tenantId
                    );

                    if (appInfo == null || appInfo.ProfileIds.Count == 0)
                    {
                        logger.LogDebug(
                            "No profiles assigned to application. ApplicationId: {ApplicationId}",
                            applicationId
                        );
                        // Return empty instead of null so we cache "no profiles" as a valid state
                        return CachedApplicationProfiles.Empty;
                    }

                    // Use the catalog cache as the single source of profile definitions
                    CachedProfileCatalog catalog = await GetOrFetchCatalogAsync(tenantId);

                    var profilesByName = new Dictionary<string, ProfileDefinition>();
                    var assignedProfileNames = new List<string>();

                    foreach (long profileId in appInfo.ProfileIds)
                    {
                        if (
                            !catalog.TryGetProfile(profileId, out ProfileDefinition? definition)
                            || definition is null
                        )
                        {
                            logger.LogWarning(
                                "Assigned profile not found in catalog. ProfileId: {ProfileId}, ApplicationId: {ApplicationId}",
                                profileId,
                                applicationId
                            );
                            continue;
                        }

                        profilesByName[definition.ProfileName.ToLowerInvariant()] = definition;
                        assignedProfileNames.Add(definition.ProfileName);
                    }

                    logger.LogDebug(
                        "Cached {Count} profiles for application. ApplicationId: {ApplicationId}",
                        profilesByName.Count,
                        applicationId
                    );

                    return new CachedApplicationProfiles(profilesByName, assignedProfileNames);
                },
                new HybridCacheEntryOptions
                {
                    Expiration = TimeSpan.FromSeconds(cacheSettings.ProfileCacheExpirationSeconds),
                    LocalCacheExpiration = TimeSpan.FromSeconds(cacheSettings.ProfileCacheExpirationSeconds),
                }
            ) ?? CachedApplicationProfiles.Empty;
    }

    private Task<ProfileResolutionResult> ValidateExplicitProfileWithoutAssignment(
        ParsedProfileHeader parsedHeader,
        RequestMethod method
    )
    {
        // Client specified a profile header but has no profiles assigned
        // We need to check if the profile exists at all and if it covers the resource
        // For now, we'll just honor the profile if it exists (no assignment enforcement)

        // This is a temporary implementation - in the future we might want to
        // fetch the profile from CMS to validate it exists
        // For now, return an error since we can't validate without profile data

        logger.LogDebug(
            "Profile header specified but no profiles assigned to application. Profile: {ProfileName}",
            LoggingSanitizer.SanitizeForLogging(parsedHeader.ProfileName)
        );

        // According to the design: "When a client's application has no profiles assigned,
        // no implicit profile selection or assignment enforcement is applied.
        // If a profile header is provided, it is honored after normal header validation."
        // This means we need to fetch the profile directly

        // For the N+1 approach, we don't have an efficient way to look up a profile by name
        // So we'll return an error for now - this should be refined when we add the dedicated endpoint
        return Task.FromResult(CreateProfileNotFoundError(parsedHeader.ProfileName, method));
    }

    private static ProfileResolutionResult ValidateExplicitProfile(
        ParsedProfileHeader parsedHeader,
        RequestMethod method,
        string resourceName,
        CachedApplicationProfiles cachedProfiles
    )
    {
        // Check if the profile is assigned to this application
        // Use lowercase for lookup since dictionary keys are normalized to lowercase
        if (
            !cachedProfiles.ProfilesByName.TryGetValue(
                parsedHeader.ProfileName.ToLowerInvariant(),
                out ProfileDefinition? profileDefinition
            )
        )
        {
            // Profile not assigned to this application
            string availableProfiles = string.Join(
                ", ",
                cachedProfiles.AssignedProfileNames.Select(name =>
                    $"'{ProfileHeaderParser.BuildProfileContentType(resourceName.ToLowerInvariant(), name, GetUsageTypeForMethod(method))}'"
                )
            );

            return ProfileResolutionResult.Failure(
                new ProfileResolutionError(
                    StatusCode: 403,
                    ErrorType: "urn:ed-fi:api:security:data-policy:incorrect-usage",
                    Title: "Data Policy Failure Due to Incorrect Usage",
                    Detail: "A data policy failure was encountered. The request was not constructed correctly for the data policy that has been applied to this data for the caller.",
                    Errors:
                    [
                        $"Based on profile assignments, one of the following profile-specific content types is required when requesting this resource: {availableProfiles}",
                    ]
                )
            );
        }

        // Validate resource name in header matches the requested resource
        if (!resourceName.Equals(parsedHeader.ResourceName, StringComparison.OrdinalIgnoreCase))
        {
            return ProfileResolutionResult.Failure(
                new ProfileResolutionError(
                    StatusCode: 400,
                    ErrorType: "urn:ed-fi:api:profile:invalid-profile-usage",
                    Title: "Invalid Profile Usage",
                    Detail: "The request construction was invalid with respect to usage of a data policy.",
                    Errors:
                    [
                        $"The resource specified by the profile-based content type ('{parsedHeader.ResourceName}') does not match the requested resource ('{resourceName}').",
                    ]
                )
            );
        }

        // Validate usage type matches HTTP method
        ProfileResolutionResult? usageValidation = ValidateUsageType(parsedHeader.UsageType, method);
        if (usageValidation != null)
        {
            return usageValidation;
        }

        // Find the resource profile for the requested resource
        ResourceProfile? resourceProfile = profileDefinition.Resources.FirstOrDefault(r =>
            r.ResourceName.Equals(resourceName, StringComparison.OrdinalIgnoreCase)
        );

        if (resourceProfile == null)
        {
            return ProfileResolutionResult.Failure(
                new ProfileResolutionError(
                    StatusCode: 400,
                    ErrorType: "urn:ed-fi:api:profile:invalid-profile-usage",
                    Title: "Invalid Profile Usage",
                    Detail: "The request construction was invalid with respect to usage of a data policy. The resource is not contained by the profile used by (or applied to) the request.",
                    Errors:
                    [
                        $"Resource '{resourceName}' is not accessible through the '{parsedHeader.ProfileName}' profile specified by the content type.",
                    ]
                )
            );
        }

        // Validate the resource has the appropriate content type for the operation
        ProfileResolutionResult? contentTypeValidation = ValidateResourceContentType(
            resourceProfile,
            method,
            parsedHeader.ProfileName
        );
        if (contentTypeValidation != null)
        {
            return contentTypeValidation;
        }

        ProfileContentType contentType =
            method == RequestMethod.GET ? ProfileContentType.Read : ProfileContentType.Write;

        return ProfileResolutionResult.Success(
            new ProfileContext(
                ProfileName: profileDefinition.ProfileName,
                ContentType: contentType,
                ResourceProfile: resourceProfile,
                WasExplicitlySpecified: true
            )
        );
    }

    private static ProfileResolutionResult HandleImplicitProfileSelection(
        RequestMethod method,
        string resourceName,
        CachedApplicationProfiles cachedProfiles
    )
    {
        // Find profiles that cover the requested resource
        var applicableProfiles = cachedProfiles
            .ProfilesByName.Values.Where(p =>
                p.Resources.Any(r => r.ResourceName.Equals(resourceName, StringComparison.OrdinalIgnoreCase))
            )
            .ToList();

        if (applicableProfiles.Count == 0)
        {
            // No profiles cover this resource - no profile applies
            return ProfileResolutionResult.NoProfileApplies();
        }

        if (applicableProfiles.Count == 1)
        {
            // Exactly one profile covers this resource - auto-select it
            ProfileDefinition profile = applicableProfiles[0];
            ResourceProfile resourceProfile = profile.Resources.First(r =>
                r.ResourceName.Equals(resourceName, StringComparison.OrdinalIgnoreCase)
            );

            // Validate the resource has the appropriate content type
            ProfileResolutionResult? contentTypeValidation = ValidateResourceContentType(
                resourceProfile,
                method,
                profile.ProfileName
            );
            if (contentTypeValidation != null)
            {
                return contentTypeValidation;
            }

            ProfileContentType contentType =
                method == RequestMethod.GET ? ProfileContentType.Read : ProfileContentType.Write;

            return ProfileResolutionResult.Success(
                new ProfileContext(
                    ProfileName: profile.ProfileName,
                    ContentType: contentType,
                    ResourceProfile: resourceProfile,
                    WasExplicitlySpecified: false
                )
            );
        }

        // Multiple profiles cover this resource - client must specify which one
        string availableProfiles = string.Join(
            ", ",
            applicableProfiles.Select(p =>
                $"'{ProfileHeaderParser.BuildProfileContentType(resourceName.ToLowerInvariant(), p.ProfileName, GetUsageTypeForMethod(method))}'"
            )
        );

        return ProfileResolutionResult.Failure(
            new ProfileResolutionError(
                StatusCode: 403,
                ErrorType: "urn:ed-fi:api:security:data-policy:incorrect-usage",
                Title: "Data Policy Failure Due to Incorrect Usage",
                Detail: "A data policy failure was encountered. The request was not constructed correctly for the data policy that has been applied to this data for the caller.",
                Errors:
                [
                    $"Based on profile assignments, one of the following profile-specific content types is required when requesting this resource: {availableProfiles}",
                ]
            )
        );
    }

    /// <summary>
    /// Determines the appropriate ProfileUsageType based on the HTTP method.
    /// GET requests use Readable, all other methods use Writable.
    /// </summary>
    private static ProfileUsageType GetUsageTypeForMethod(RequestMethod method) =>
        method == RequestMethod.GET ? ProfileUsageType.Readable : ProfileUsageType.Writable;

    private static ProfileResolutionResult? ValidateUsageType(
        ProfileUsageType usageType,
        RequestMethod method
    )
    {
        bool isReadOperation = method == RequestMethod.GET;

        if (usageType == ProfileUsageType.Writable && isReadOperation)
        {
            return ProfileResolutionResult.Failure(
                new ProfileResolutionError(
                    StatusCode: 400,
                    ErrorType: "urn:ed-fi:api:profile:invalid-profile-usage",
                    Title: "Invalid Profile Usage",
                    Detail: "The request construction was invalid with respect to usage of a data policy.",
                    Errors:
                    [
                        "A profile-based content type that is writable cannot be used with GET requests.",
                    ]
                )
            );
        }

        if (usageType == ProfileUsageType.Readable && !isReadOperation)
        {
            string methodName = method.ToString().ToUpperInvariant();
            return ProfileResolutionResult.Failure(
                new ProfileResolutionError(
                    StatusCode: 400,
                    ErrorType: "urn:ed-fi:api:profile:invalid-profile-usage",
                    Title: "Invalid Profile Usage",
                    Detail: "The request construction was invalid with respect to usage of a data policy.",
                    Errors:
                    [
                        $"A profile-based content type that is readable cannot be used with {methodName} requests.",
                    ]
                )
            );
        }

        return null;
    }

    private static ProfileResolutionResult? ValidateResourceContentType(
        ResourceProfile resourceProfile,
        RequestMethod method,
        string profileName
    )
    {
        bool isReadOperation = method == RequestMethod.GET;

        if (isReadOperation && resourceProfile.ReadContentType == null)
        {
            return ProfileResolutionResult.Failure(
                new ProfileResolutionError(
                    StatusCode: 405,
                    ErrorType: "urn:ed-fi:api:profile:method-usage",
                    Title: "Method Not Allowed with Profile",
                    Detail: "The request construction was invalid with respect to usage of a data policy. An attempt was made to access a resource that is not readable using the profile.",
                    Errors:
                    [
                        $"Resource class '{resourceProfile.ResourceName}' is not readable using API profile '{profileName}'.",
                    ]
                )
            );
        }

        if (!isReadOperation && resourceProfile.WriteContentType == null)
        {
            return ProfileResolutionResult.Failure(
                new ProfileResolutionError(
                    StatusCode: 405,
                    ErrorType: "urn:ed-fi:api:profile:method-usage",
                    Title: "Method Not Allowed with Profile",
                    Detail: "The request construction was invalid with respect to usage of a data policy. An attempt was made to access a resource that is not writable using the profile.",
                    Errors:
                    [
                        $"Resource class '{resourceProfile.ResourceName}' is not writable using API profile '{profileName}'.",
                    ]
                )
            );
        }

        return null;
    }

    private static ProfileResolutionResult CreateProfileNotFoundError(
        string profileName,
        RequestMethod method
    )
    {
        bool isReadOperation = method == RequestMethod.GET;
        int statusCode = isReadOperation ? 406 : 415;
        string headerType = isReadOperation ? "Accept" : "Content-Type";

        return ProfileResolutionResult.Failure(
            new ProfileResolutionError(
                StatusCode: statusCode,
                ErrorType: "urn:ed-fi:api:profile:invalid-profile-usage",
                Title: "Invalid Profile Usage",
                Detail: "The request construction was invalid with respect to usage of a data policy.",
                Errors:
                [
                    $"The profile '{profileName}' specified by the content type in the '{headerType}' header is not supported by this host.",
                ]
            )
        );
    }

    #region Catalog Access Methods

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetProfileNamesAsync(string? tenantId)
    {
        CachedProfileCatalog catalog = await GetOrFetchCatalogAsync(tenantId);
        return catalog.ProfileNames;
    }

    /// <inheritdoc />
    public async Task<ProfileDefinition?> GetProfileDefinitionAsync(string profileName, string? tenantId)
    {
        CachedProfileCatalog catalog = await GetOrFetchCatalogAsync(tenantId);
        return catalog.TryGetProfile(profileName, out ProfileDefinition? definition) ? definition : null;
    }

    /// <summary>
    /// Gets the full profile catalog for a tenant, using cache with stampede protection.
    /// </summary>
    private async Task<CachedProfileCatalog> GetOrFetchCatalogAsync(string? tenantId)
    {
        string cacheKey = GetCatalogCacheKey(tenantId);

        // HybridCache.GetOrCreateAsync provides stampede protection:
        // Only one concurrent caller executes the factory; others wait for the result
        return await hybridCache.GetOrCreateAsync(
                cacheKey,
                async cancel =>
                {
                    logger.LogDebug(
                        "Cache miss for profile catalog, fetching from CMS for tenant: {Tenant}",
                        LoggingSanitizer.SanitizeForLogging(tenantId)
                    );

                    IReadOnlyList<CmsProfileResponse> profiles = await profileCmsProvider.GetProfilesAsync(
                        tenantId
                    );

                    if (profiles.Count == 0)
                    {
                        logger.LogDebug(
                            "No profiles found for tenant: {Tenant}",
                            LoggingSanitizer.SanitizeForLogging(tenantId)
                        );
                        return CachedProfileCatalog.Empty;
                    }

                    // Parse all profiles and build the catalog
                    var profilesByName = new Dictionary<string, ProfileDefinition>();
                    var profilesById = new Dictionary<long, ProfileDefinition>();
                    var profileNames = new List<string>();

                    foreach (CmsProfileResponse profileResponse in profiles)
                    {
                        ProfileDefinitionParseResult parseResult = ProfileDefinitionParser.Parse(
                            profileResponse.Definition
                        );

                        if (!parseResult.IsSuccess || parseResult.Definition is null)
                        {
                            logger.LogWarning(
                                "Failed to parse profile definition. ProfileId: {ProfileId}, Name: {Name}, Error: {Error}",
                                profileResponse.Id,
                                LoggingSanitizer.SanitizeForLogging(profileResponse.Name),
                                LoggingSanitizer.SanitizeForLogging(
                                    parseResult.ErrorMessage ?? "Unknown error"
                                )
                            );
                            continue;
                        }

                        // Store with lowercase key for case-insensitive lookup after deserialization
                        profilesByName[parseResult.Definition.ProfileName.ToLowerInvariant()] =
                            parseResult.Definition;
                        profilesById[profileResponse.Id] = parseResult.Definition;
                        // Keep original case for display
                        profileNames.Add(parseResult.Definition.ProfileName);
                    }

                    logger.LogDebug(
                        "Cached {Count} profiles for tenant: {Tenant}",
                        profilesByName.Count,
                        LoggingSanitizer.SanitizeForLogging(tenantId)
                    );

                    return new CachedProfileCatalog(profilesByName, profilesById, profileNames);
                },
                new HybridCacheEntryOptions
                {
                    Expiration = TimeSpan.FromSeconds(cacheSettings.ProfileCacheExpirationSeconds),
                    LocalCacheExpiration = TimeSpan.FromSeconds(cacheSettings.ProfileCacheExpirationSeconds),
                }
            ) ?? CachedProfileCatalog.Empty;
    }

    #endregion

    #region Profile OpenAPI Methods

    /// <inheritdoc />
    public async Task<JsonNode?> GetProfileOpenApiSpecAsync(
        string profileName,
        string? tenantId,
        Func<JsonNode> baseSpecificationProvider,
        Guid apiSchemaReloadId
    )
    {
        // First verify the profile exists in the catalog
        CachedProfileCatalog catalog = await GetOrFetchCatalogAsync(tenantId);

        if (!catalog.TryGetProfile(profileName, out ProfileDefinition? profileDefinition))
        {
            logger.LogWarning(
                "Profile not found in catalog. ProfileName: {ProfileName}, TenantId: {TenantId}",
                LoggingSanitizer.SanitizeForLogging(profileName),
                LoggingSanitizer.SanitizeForLogging(tenantId)
            );
            return null;
        }

        // Get or create the cached OpenAPI spec
        // The cache key includes apiSchemaReloadId so specs are regenerated when the schema changes
        string cacheKey = GetOpenApiCacheKey(tenantId, profileName, apiSchemaReloadId);

        // Since we cache JsonNode as string (HybridCache serialization), we need to parse it back
        string? cachedSpec = await hybridCache.GetOrCreateAsync(
            cacheKey,
            async cancel =>
            {
                logger.LogDebug(
                    "Cache miss for profile OpenAPI spec, generating. ProfileName: {ProfileName}, TenantId: {TenantId}, SchemaReloadId: {ReloadId}",
                    LoggingSanitizer.SanitizeForLogging(profileName),
                    LoggingSanitizer.SanitizeForLogging(tenantId),
                    apiSchemaReloadId
                );

                // Get the base OpenAPI spec with servers from the provider
                JsonNode baseSpecification = baseSpecificationProvider();

                // Apply profile filtering to the OpenAPI spec
                var profileFilter = new ProfileOpenApiSpecificationFilter(logger);
                JsonNode filteredSpecification = profileFilter.CreateProfileSpecification(
                    baseSpecification,
                    profileDefinition!
                );

                logger.LogDebug(
                    "Cached profile OpenAPI spec. ProfileName: {ProfileName}, TenantId: {TenantId}",
                    LoggingSanitizer.SanitizeForLogging(profileName),
                    LoggingSanitizer.SanitizeForLogging(tenantId)
                );

                // Return as string for serialization
                return filteredSpecification.ToJsonString();
            },
            new HybridCacheEntryOptions
            {
                Expiration = TimeSpan.FromSeconds(cacheSettings.ProfileCacheExpirationSeconds),
                LocalCacheExpiration = TimeSpan.FromSeconds(cacheSettings.ProfileCacheExpirationSeconds),
            }
        );

        // Parse the cached string back to JsonNode
        return cachedSpec is not null ? JsonNode.Parse(cachedSpec) : null;
    }

    #endregion
}
