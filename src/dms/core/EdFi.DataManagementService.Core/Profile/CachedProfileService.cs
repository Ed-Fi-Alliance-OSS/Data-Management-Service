// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
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
internal sealed record CachedApplicationProfiles(IReadOnlyDictionary<long, string> ProfilesById)
{
    /// <summary>
    /// Represents an application with no profiles assigned.
    /// </summary>
    public static CachedApplicationProfiles Empty { get; } = new(new Dictionary<long, string>());

    /// <summary>
    /// Returns true if no profiles are assigned to this application.
    /// </summary>
    public bool IsEmpty => ProfilesById.Count == 0;

    public bool Contains(long profileId) => ProfilesById.ContainsKey(profileId);

    public IEnumerable<long> AssignedProfileIds => ProfilesById.Keys;

    public IEnumerable<string> AssignedProfileNames => ProfilesById.Values;
}

/// <summary>
/// Cached profile store containing all profiles for a tenant.
/// Provides O(1) lookup by profile name and by id-to-name mapping.
/// </summary>
internal sealed record CachedProfileStore(
    IReadOnlyDictionary<string, ProfileDefinition> DefinitionsByName,
    IReadOnlyDictionary<long, string> NameById
)
{
    public IEnumerable<string> ProfileNames => DefinitionsByName.Keys;

    public bool TryGetByName(string name, out ProfileDefinition? definition)
    {
        if (DefinitionsByName.TryGetValue(name, out definition))
        {
            return true;
        }

        ProfileDefinition? matchedDefinition = DefinitionsByName
            .Where(entry => entry.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Value)
            .FirstOrDefault();

        if (matchedDefinition is not null)
        {
            definition = matchedDefinition;
            return true;
        }

        definition = null;
        return false;
    }

    public bool TryGetById(long id, out ProfileDefinition? definition)
    {
        if (NameById.TryGetValue(id, out string? name))
        {
            return DefinitionsByName.TryGetValue(name, out definition);
        }

        definition = null;
        return false;
    }
}

/// <summary>
/// Profile service with caching and stampede protection.
/// Provides profile resolution for requests, catalog-level access to profiles,
/// and cached profile-filtered OpenAPI specifications.
/// </summary>
internal class CachedProfileService(
    IProfileCmsProvider profileCmsProvider,
    IProfileDataValidator profileDataValidator,
    IEffectiveApiSchemaProvider effectiveApiSchemaProvider,
    HybridCache hybridCache,
    CacheSettings cacheSettings,
    ILogger<CachedProfileService> logger
) : IProfileService
{
    private const string ApplicationProfilesCacheKeyPrefix = "ApplicationProfiles";
    private const string ProfileCatalogCacheKeyPrefix = "ProfileCatalog";
    private const string ProfileOpenApiCacheKeyPrefix = "ProfileOpenApi";
    private readonly ProfileOpenApiSpecificationFilter profileFilter = new(logger);

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
                CachedProfileStore unassignedProfileStore = await GetOrFetchProfileStoreAsync(tenantId);
                // An app without profile assignments can still use a valid explicit profile.
                // Passing null skips assignment enforcement while preserving normal profile
                // validation for existence, resource matching, and method/content-type rules.
                return ValidateExplicitProfile(
                    parsedHeader,
                    method,
                    resourceName,
                    cachedProfiles: null,
                    unassignedProfileStore
                );
            }

            // No profiles assigned and no header - no profile applies
            return ProfileResolutionResult.NoProfileApplies();
        }

        CachedProfileStore profileStore = await GetOrFetchProfileStoreAsync(tenantId);

        // Profiles are assigned to this application
        if (parsedHeader != null)
        {
            // Explicit profile header provided - validate and resolve
            return ValidateExplicitProfile(parsedHeader, method, resourceName, cachedProfiles, profileStore);
        }

        // No header provided - check for implicit profile selection
        return HandleImplicitProfileSelection(method, resourceName, cachedProfiles, profileStore);
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

                    // Fetch profile store to get names for the IDs
                    CachedProfileStore profileStore = await GetOrFetchProfileStoreAsync(tenantId);

                    var profilesById = new Dictionary<long, string>();
                    foreach (long profileId in appInfo.ProfileIds)
                    {
                        if (profileStore.NameById.TryGetValue(profileId, out string? profileName))
                        {
                            profilesById[profileId] = profileName;
                        }
                        else
                        {
                            logger.LogWarning(
                                "Profile ID {ProfileId} not found in profile store for application {ApplicationId}",
                                profileId,
                                applicationId
                            );
                        }
                    }

                    logger.LogDebug(
                        "Cached {Count} profile assignments for application. ApplicationId: {ApplicationId}",
                        profilesById.Count,
                        applicationId
                    );

                    return new CachedApplicationProfiles(profilesById);
                },
                new HybridCacheEntryOptions
                {
                    Expiration = TimeSpan.FromSeconds(cacheSettings.ProfileCacheExpirationSeconds),
                    LocalCacheExpiration = TimeSpan.FromSeconds(cacheSettings.ProfileCacheExpirationSeconds),
                }
            ) ?? CachedApplicationProfiles.Empty;
    }

    private static ProfileResolutionResult ValidateExplicitProfile(
        ParsedProfileHeader parsedHeader,
        RequestMethod method,
        string resourceName,
        CachedApplicationProfiles? cachedProfiles,
        CachedProfileStore profileStore
    )
    {
        IReadOnlyList<string> availableProfiles = cachedProfiles is not null
            ? GetAvailableProfileContentTypes(cachedProfiles, profileStore, resourceName, method)
            : [];
        // Assignment enforcement applies only when at least one assigned profile covers the
        // requested resource and verb. If none apply, the profiles design treats the request
        // as standard behavior rather than blocking an otherwise valid explicit profile.
        bool enforceAssignment = cachedProfiles is not null && availableProfiles.Count > 0;

        if (
            !profileStore.TryGetByName(parsedHeader.ProfileName, out ProfileDefinition? profileDefinition)
            || profileDefinition is null
        )
        {
            return CreateProfileNotFoundError(parsedHeader.ProfileName, method);
        }

        if (
            enforceAssignment
            && !cachedProfiles!.AssignedProfileNames.Contains(
                parsedHeader.ProfileName,
                StringComparer.OrdinalIgnoreCase
            )
        )
        {
            return CreateIncorrectUsageFailure(availableProfiles);
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
            if (enforceAssignment)
            {
                return CreateIncorrectUsageFailure(availableProfiles);
            }

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
        CachedApplicationProfiles cachedProfiles,
        CachedProfileStore profileStore
    )
    {
        IReadOnlyList<string> availableProfiles = GetAvailableProfileContentTypes(
            cachedProfiles,
            profileStore,
            resourceName,
            method
        );
        bool isReadOperation = method == RequestMethod.GET;
        List<(ProfileDefinition Definition, ResourceProfile ResourceProfile)> applicableProfiles = cachedProfiles
            .AssignedProfileNames.Select(profileName =>
                profileStore.TryGetByName(profileName, out ProfileDefinition? definition) ? definition : null
            )
            .Where(definition => definition is not null)
            .Select(definition => new
            {
                Definition = definition!,
                ResourceProfile = definition!.Resources.FirstOrDefault(r =>
                    r.ResourceName.Equals(resourceName, StringComparison.OrdinalIgnoreCase)
                ),
            })
            .Where(x => x.ResourceProfile is not null)
            .Where(x => isReadOperation ? x.ResourceProfile!.ReadContentType is not null : x.ResourceProfile!.WriteContentType is not null)
            .Select(x => (x.Definition, x.ResourceProfile!))
            .ToList();

        if (availableProfiles.Count == 0)
        {
            // No profiles cover this resource - no profile applies
            return ProfileResolutionResult.NoProfileApplies();
        }

        if (availableProfiles.Count == 1)
        {
            // Aligns with the profiles design in reference/design/profiles-DMS-877:
            // if exactly one assigned profile applies to the requested resource and verb,
            // the service implicitly selects it even when other assigned profiles exist.
            (ProfileDefinition profileDefinition, ResourceProfile resourceProfile) = applicableProfiles[0];

            ProfileResolutionResult? contentTypeValidation = ValidateResourceContentType(
                resourceProfile,
                method,
                profileDefinition.ProfileName
            );

            if (contentTypeValidation is not null)
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
                    WasExplicitlySpecified: false
                )
            );
        }

        // More than one profile covers this resource - client must specify which one
        return CreateIncorrectUsageFailure(availableProfiles);
    }

    /// <summary>
    /// Determines the appropriate ProfileUsageType based on the HTTP method.
    /// GET requests use Readable, all other methods use Writable.
    /// </summary>
    private static ProfileUsageType GetUsageTypeForMethod(RequestMethod method) =>
        method == RequestMethod.GET ? ProfileUsageType.Readable : ProfileUsageType.Writable;

    private static IReadOnlyList<string> GetAvailableProfileContentTypes(
        CachedApplicationProfiles cachedProfiles,
        CachedProfileStore profileStore,
        string resourceName,
        RequestMethod method
    )
    {
        ProfileUsageType usageType = GetUsageTypeForMethod(method);
        bool isReadOperation = method == RequestMethod.GET;

        return cachedProfiles
            .AssignedProfileNames.Select(profileName =>
                profileStore.TryGetByName(profileName, out ProfileDefinition? definition) ? definition : null
            )
            .Where(definition => definition is not null)
            .Select(definition => new
            {
                definition!.ProfileName,
                ResourceProfile = definition.Resources.FirstOrDefault(r =>
                    r.ResourceName.Equals(resourceName, StringComparison.OrdinalIgnoreCase)
                ),
            })
            .Where(x => x.ResourceProfile is not null)
            .Where(x => isReadOperation ? x.ResourceProfile!.ReadContentType is not null : x.ResourceProfile!.WriteContentType is not null)
            .Select(x =>
                $"'{ProfileHeaderParser.BuildProfileContentType(resourceName.ToLowerInvariant(), x.ProfileName, usageType)}'"
            )
            .ToList();
    }

    private static ProfileResolutionResult CreateIncorrectUsageFailure(
        IReadOnlyList<string> availableProfiles
    )
    {
        string errorMessage = availableProfiles.Count > 0
            ? $"Based on profile assignments, one of the following profile-specific content types is required when requesting this resource: {string.Join(", ", availableProfiles)}"
            : "None of the profiles assigned to the caller provide a profile-specific content type for this resource and method.";

        return ProfileResolutionResult.Failure(
            new ProfileResolutionError(
                StatusCode: 403,
                ErrorType: "urn:ed-fi:api:security:data-policy:incorrect-usage",
                Title: "Data Policy Failure Due to Incorrect Usage",
                Detail: "A data policy failure was encountered. The request was not constructed correctly for the data policy that has been applied to this data for the caller.",
                Errors: [errorMessage]
            )
        );
    }

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
        CachedProfileStore profileStore = await GetOrFetchProfileStoreAsync(tenantId);
        return profileStore.ProfileNames.ToList();
    }

    /// <inheritdoc />
    public async Task<ProfileDefinition?> GetProfileDefinitionAsync(string profileName, string? tenantId)
    {
        CachedProfileStore profileStore = await GetOrFetchProfileStoreAsync(tenantId);
        return profileStore.TryGetByName(profileName, out ProfileDefinition? definition) ? definition : null;
    }

    /// <summary>
    /// Gets the full profile store for a tenant, using cache with stampede protection.
    /// </summary>
    private async Task<CachedProfileStore> GetOrFetchProfileStoreAsync(string? tenantId)
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
                        return new CachedProfileStore(
                            new Dictionary<string, ProfileDefinition>(StringComparer.OrdinalIgnoreCase),
                            new Dictionary<long, string>()
                        );
                    }

                    // Fetch all profile definitions in parallel
                    var fetchTasks = profiles.Select(async profile =>
                    {
                        CmsProfileResponse? profileResponse = await profileCmsProvider.GetProfileAsync(
                            profile.Id,
                            tenantId
                        );
                        return (ProfileId: profile.Id, Response: profileResponse);
                    });

                    var fetchResults = await Task.WhenAll(fetchTasks);

                    // Parse all profiles and build the store
                    var profilesByName = new Dictionary<string, ProfileDefinition>(
                        StringComparer.OrdinalIgnoreCase
                    );
                    var nameById = new Dictionary<long, string>();

                    foreach (var (profileId, profileResponse) in fetchResults)
                    {
                        if (profileResponse is null)
                        {
                            logger.LogWarning(
                                "Profile fetch returned null. ProfileId: {ProfileId}, Tenant: {Tenant}",
                                profileId,
                                LoggingSanitizer.SanitizeForLogging(tenantId)
                            );
                            continue;
                        }

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

                        // Validate the parsed profile against the API schema
                        ProfileValidationResult validationResult = profileDataValidator.Validate(
                            parseResult.Definition,
                            effectiveApiSchemaProvider
                        );

                        // Skip profiles with validation errors
                        if (validationResult.HasErrors)
                        {
                            logger.LogError(
                                "Profile validation failed with errors. ProfileId: {ProfileId}, Name: {Name}, Errors: {Errors}",
                                profileResponse.Id,
                                LoggingSanitizer.SanitizeForLogging(profileResponse.Name),
                                string.Join(
                                    "; ",
                                    validationResult
                                        .Failures.Where(f => f.Severity == ValidationSeverity.Error)
                                        .Select(f => LoggingSanitizer.SanitizeForLogging(f.Message))
                                )
                            );
                            continue;
                        }

                        // Log warnings but still include the profile
                        if (validationResult.HasWarnings)
                        {
                            logger.LogWarning(
                                "Profile validation succeeded with warnings. ProfileId: {ProfileId}, Name: {Name}, Warnings: {Warnings}",
                                profileResponse.Id,
                                LoggingSanitizer.SanitizeForLogging(profileResponse.Name),
                                string.Join(
                                    "; ",
                                    validationResult
                                        .Failures.Where(f => f.Severity == ValidationSeverity.Warning)
                                        .Select(f => LoggingSanitizer.SanitizeForLogging(f.Message))
                                )
                            );
                        }

                        profilesByName[parseResult.Definition.ProfileName] = parseResult.Definition;
                        nameById[profileResponse.Id] = parseResult.Definition.ProfileName;
                    }

                    logger.LogDebug(
                        "Cached {Count} profiles for tenant: {Tenant}",
                        profilesByName.Count,
                        LoggingSanitizer.SanitizeForLogging(tenantId)
                    );

                    return new CachedProfileStore(profilesByName, nameById);
                },
                new HybridCacheEntryOptions
                {
                    Expiration = TimeSpan.FromSeconds(cacheSettings.ProfileCacheExpirationSeconds),
                    LocalCacheExpiration = TimeSpan.FromSeconds(cacheSettings.ProfileCacheExpirationSeconds),
                }
            )
            ?? new CachedProfileStore(
                new Dictionary<string, ProfileDefinition>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<long, string>()
            );
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
        CachedProfileStore profileStore = await GetOrFetchProfileStoreAsync(tenantId);

        if (!profileStore.TryGetByName(profileName, out ProfileDefinition? profileDefinition))
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
