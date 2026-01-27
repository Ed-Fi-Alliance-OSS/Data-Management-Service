// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Model;

namespace EdFi.DataManagementService.Core.Profile;

/// <summary>
/// Result of profile resolution
/// </summary>
public record ProfileResolutionResult(
    bool IsSuccess,
    ProfileContext? ProfileContext,
    ProfileResolutionError? Error
)
{
    public static ProfileResolutionResult Success(ProfileContext context) => new(true, context, null);

    public static ProfileResolutionResult NoProfileApplies() => new(true, null, null);

    public static ProfileResolutionResult Failure(ProfileResolutionError error) => new(false, null, error);
}

/// <summary>
/// Error information for profile resolution failures
/// </summary>
/// <param name="StatusCode">HTTP status code to return</param>
/// <param name="ErrorType">Error type URN (e.g., "urn:ed-fi:api:profile:invalid-profile-usage")</param>
/// <param name="Title">Short error title</param>
/// <param name="Detail">Detailed error description</param>
/// <param name="Errors">Specific error messages</param>
public record ProfileResolutionError(
    int StatusCode,
    string ErrorType,
    string Title,
    string Detail,
    string[] Errors
);

/// <summary>
/// Service for resolving and validating API profiles for requests
/// </summary>
internal interface IProfileService
{
    /// <summary>
    /// Resolves the profile for a request based on headers and client authorization
    /// </summary>
    /// <param name="parsedHeader">The parsed profile header, or null if no profile header was provided</param>
    /// <param name="method">The HTTP request method</param>
    /// <param name="resourceName">The resource name being requested (singular form from path)</param>
    /// <param name="applicationId">The application ID from client authorization</param>
    /// <param name="tenantId">Optional tenant ID for multi-tenant deployments</param>
    /// <returns>Profile resolution result containing context or error information</returns>
    Task<ProfileResolutionResult> ResolveProfileAsync(
        ParsedProfileHeader? parsedHeader,
        RequestMethod method,
        string resourceName,
        long applicationId,
        string? tenantId
    );

    /// <summary>
    /// Retrieves application profile definitions from cache or fetches them from the CMS if not cached.
    /// </summary>
    /// <param name="applicationId">The unique identifier of the application.</param>
    /// <param name="tenantId">The tenant identifier, or <c>null</c> for single-tenant scenarios.</param>
    /// <returns>
    /// A <see cref="CachedApplicationProfiles"/> containing the profile definitions and assigned profile names.
    /// </returns>
    Task<CachedApplicationProfiles> GetOrFetchApplicationProfilesAsync(long applicationId, string? tenantId);
}
