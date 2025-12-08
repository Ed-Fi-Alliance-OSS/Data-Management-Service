// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.RegularExpressions;
using EdFi.DataManagementService.Core.Profiles.Model;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Profiles;

/// <summary>
/// Resolves which profile should be applied to a request based on headers and resource.
/// </summary>
public partial class ProfileResolutionService
{
    private readonly IProfileProvider _profileProvider;
    private readonly ILogger<ProfileResolutionService> _logger;

    // Media type pattern: application/vnd.ed-fi.{resource}.{profile}.readable+json
    // or: application/vnd.ed-fi.{resource}.{profile}.writable+json
    [GeneratedRegex(@"^application/vnd\.ed-fi\.(?<resource>[^.]+)\.(?<profile>[^.]+)\.(?<operation>readable|writable)\+json$", RegexOptions.IgnoreCase)]
    private static partial Regex ProfileMediaTypeRegex();

    public ProfileResolutionService(
        IProfileProvider profileProvider,
        ILogger<ProfileResolutionService> logger)
    {
        _profileProvider = profileProvider;
        _logger = logger;
    }

    /// <summary>
    /// Resolves the profile for a GET request based on Accept header.
    /// </summary>
    /// <param name="acceptHeader">The Accept header value</param>
    /// <param name="resourceName">The requested resource name</param>
    /// <returns>The ContentType to apply for read operations, or null if no profile applies</returns>
    public ContentType? ResolveReadProfile(string? acceptHeader, string resourceName)
    {
        // Try explicit profile from header
        if (!string.IsNullOrWhiteSpace(acceptHeader))
        {
            var profileName = ParseProfileFromMediaType(acceptHeader, resourceName, "readable");
            if (profileName != null)
            {
                var profileResource = _profileProvider.GetProfileResource(profileName, resourceName);
                if (profileResource?.ReadContentType != null)
                {
                    _logger.LogDebug("Resolved read profile '{ProfileName}' for resource '{ResourceName}' from Accept header",
                        profileName, resourceName);
                    return profileResource.ReadContentType;
                }

                _logger.LogWarning("Profile '{ProfileName}' not found or does not define ReadContentType for resource '{ResourceName}'",
                    profileName, resourceName);
            }
        }

        // Try default profile if only one applies
        return ResolveDefaultProfile(resourceName, isRead: true);
    }

    /// <summary>
    /// Resolves the profile for a POST/PUT request based on Content-Type header.
    /// </summary>
    /// <param name="contentTypeHeader">The Content-Type header value</param>
    /// <param name="resourceName">The requested resource name</param>
    /// <returns>The ContentType to apply for write operations, or null if no profile applies</returns>
    public ContentType? ResolveWriteProfile(string? contentTypeHeader, string resourceName)
    {
        // Try explicit profile from header
        if (!string.IsNullOrWhiteSpace(contentTypeHeader))
        {
            var profileName = ParseProfileFromMediaType(contentTypeHeader, resourceName, "writable");
            if (profileName != null)
            {
                var profileResource = _profileProvider.GetProfileResource(profileName, resourceName);
                if (profileResource?.WriteContentType != null)
                {
                    _logger.LogDebug("Resolved write profile '{ProfileName}' for resource '{ResourceName}' from Content-Type header",
                        profileName, resourceName);
                    return profileResource.WriteContentType;
                }

                _logger.LogWarning("Profile '{ProfileName}' not found or does not define WriteContentType for resource '{ResourceName}'",
                    profileName, resourceName);
            }
        }

        // Try default profile if only one applies
        return ResolveDefaultProfile(resourceName, isRead: false);
    }

    /// <summary>
    /// Parses a profile name from a media type string.
    /// </summary>
    /// <param name="mediaType">The media type (Accept or Content-Type header value)</param>
    /// <param name="expectedResource">The expected resource name</param>
    /// <param name="expectedOperation">"readable" or "writable"</param>
    /// <returns>The profile name if valid, null otherwise</returns>
    private string? ParseProfileFromMediaType(string mediaType, string expectedResource, string expectedOperation)
    {
        var match = ProfileMediaTypeRegex().Match(mediaType);
        if (!match.Success)
        {
            return null;
        }

        var resource = match.Groups["resource"].Value;
        var profile = match.Groups["profile"].Value;
        var operation = match.Groups["operation"].Value;

        // Validate resource matches
        if (!string.Equals(resource, expectedResource, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Media type resource '{MediaResource}' does not match request resource '{ResourceName}'",
                resource, expectedResource);
            return null;
        }

        // Validate operation matches
        if (!string.Equals(operation, expectedOperation, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Media type operation '{Operation}' does not match expected operation '{ExpectedOperation}'",
                operation, expectedOperation);
            return null;
        }

        return profile;
    }

    /// <summary>
    /// Resolves default profile when no explicit profile is specified.
    /// Returns a profile only if exactly one profile applies to the resource.
    /// </summary>
    private ContentType? ResolveDefaultProfile(string resourceName, bool isRead)
    {
        var profiles = _profileProvider.GetProfilesForResource(resourceName);

        if (profiles.Length == 0)
        {
            // No profiles for this resource
            return null;
        }

        if (profiles.Length > 1)
        {
            // Multiple profiles exist - require explicit selection
            _logger.LogDebug("Multiple profiles exist for resource '{ResourceName}', explicit header required", resourceName);
            return null;
        }

        // Exactly one profile - use it as default
        var profile = profiles[0];
        var resource = Array.Find(profile.Resources, r =>
            string.Equals(r.Name, resourceName, StringComparison.OrdinalIgnoreCase));

        if (resource == null)
        {
            return null;
        }

        var contentType = isRead ? resource.ReadContentType : resource.WriteContentType;

        if (contentType != null)
        {
            _logger.LogDebug("Using default profile '{ProfileName}' for resource '{ResourceName}'",
                profile.Name, resourceName);
        }

        return contentType;
    }
}
