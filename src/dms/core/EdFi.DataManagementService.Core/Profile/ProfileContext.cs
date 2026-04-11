// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Profile;

/// <summary>
/// The content type usage for a profile (read or write operations)
/// </summary>
public enum ProfileContentType
{
    Read,
    Write,
}

/// <summary>
/// Profile context for the current request, containing the resolved profile information
/// </summary>
/// <param name="ProfileName">The name of the profile being applied</param>
/// <param name="ContentType">Whether this is for read or write operations</param>
/// <param name="ResourceProfile">The profile rules for the requested resource</param>
/// <param name="WasExplicitlySpecified">True if the profile was specified via header, false if implicitly selected</param>
public record ProfileContext(
    string ProfileName,
    ProfileContentType ContentType,
    ResourceProfile ResourceProfile,
    bool WasExplicitlySpecified
);

/// <summary>
/// A complete parsed profile definition containing rules for one or more resources
/// </summary>
/// <param name="ProfileName">The name of the profile</param>
/// <param name="Resources">The list of resources covered by this profile</param>
public record ProfileDefinition(string ProfileName, IReadOnlyList<ResourceProfile> Resources);

/// <summary>
/// Profile rules for a single resource
/// </summary>
/// <param name="ResourceName">The resource name (e.g., "Student", "School")</param>
/// <param name="LogicalSchema">Optional schema for extension resources</param>
/// <param name="ReadContentType">Rules for read operations, or null if not readable</param>
/// <param name="WriteContentType">Rules for write operations, or null if not writable</param>
public record ResourceProfile(
    string ResourceName,
    string? LogicalSchema,
    ContentTypeDefinition? ReadContentType,
    ContentTypeDefinition? WriteContentType
);
