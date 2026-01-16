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
/// Provides access to profile data from the Configuration Management Service
/// </summary>
public interface IProfileCmsProvider
{
    /// <summary>
    /// Gets the profile IDs assigned to an application
    /// </summary>
    /// <param name="applicationId">The application ID</param>
    /// <param name="tenantId">Optional tenant ID for multi-tenant deployments</param>
    /// <returns>Application profile info or null if not found</returns>
    Task<ApplicationProfileInfo?> GetApplicationProfileInfoAsync(long applicationId, string? tenantId);

    /// <summary>
    /// Gets a profile by its ID
    /// </summary>
    /// <param name="profileId">The profile ID</param>
    /// <param name="tenantId">Optional tenant ID for multi-tenant deployments</param>
    /// <returns>Profile response or null if not found</returns>
    Task<CmsProfileResponse?> GetProfileAsync(long profileId, string? tenantId);
}
