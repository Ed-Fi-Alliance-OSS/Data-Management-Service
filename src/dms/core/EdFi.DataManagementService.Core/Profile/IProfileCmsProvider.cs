// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Profile;

/// <summary>
/// Information about an application's profile assignments from CMS
/// </summary>
/// <param name="ApplicationId">The application ID</param>
/// <param name="ProfileIds">The list of profile IDs assigned to this application</param>
public record ApplicationProfileInfo(long ApplicationId, IReadOnlyList<long> ProfileIds);

/// <summary>
/// Profile response from CMS
/// </summary>
/// <param name="Id">The profile ID</param>
/// <param name="Name">The profile name</param>
/// <param name="Definition">The XML profile definition</param>
public record CmsProfileResponse(long Id, string Name, string Definition);

/// <summary>
/// Thrown when profile data could not be fetched from the Configuration Management Service for any
/// reason other than CMS reporting the item as not found: a non-404 status, a timeout, a transport
/// failure, a token failure, or a malformed or null response body. It must never be read as "no
/// profile applies"; the core pipeline answers it with a retriable 503.
/// </summary>
/// <param name="message">A log-safe description of the failure</param>
/// <param name="innerException">The underlying cause</param>
public sealed class ProfileDataUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>
/// Provides access to profile data from the Configuration Management Service
/// </summary>
public interface IProfileCmsProvider
{
    /// <summary>
    /// Gets the profile IDs assigned to an application
    /// </summary>
    /// <param name="applicationId">The application ID</param>
    /// <param name="tenantId">Optional tenant ID for multi-tenant deployments</param>
    /// <returns>Application profile info, or null only when CMS answered 404</returns>
    /// <exception cref="ProfileDataUnavailableException">Any failure other than a CMS 404</exception>
    Task<ApplicationProfileInfo?> GetApplicationProfileInfoAsync(long applicationId, string? tenantId);

    /// <summary>
    /// Gets a profile by its ID
    /// </summary>
    /// <param name="profileId">The profile ID</param>
    /// <param name="tenantId">Optional tenant ID for multi-tenant deployments</param>
    /// <returns>Profile response, or null only when CMS answered 404</returns>
    /// <exception cref="ProfileDataUnavailableException">Any failure other than a CMS 404</exception>
    Task<CmsProfileResponse?> GetProfileAsync(long profileId, string? tenantId);

    /// <summary>
    /// Gets the full profile catalog (id, name, definition)
    /// </summary>
    /// <param name="tenantId">Optional tenant ID for multi-tenant deployments</param>
    /// <returns>List of profiles; empty only when CMS returned an empty list</returns>
    /// <exception cref="ProfileDataUnavailableException">
    /// Any failure, including a 404 on the list; a failure never yields an empty list
    /// </exception>
    Task<IReadOnlyList<CmsProfileResponse>> GetProfilesAsync(string? tenantId);
}
