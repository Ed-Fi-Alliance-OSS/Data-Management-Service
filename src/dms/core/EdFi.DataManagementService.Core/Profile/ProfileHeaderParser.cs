// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.RegularExpressions;

namespace EdFi.DataManagementService.Core.Profile;

/// <summary>
/// Result of parsing a profile header
/// </summary>
public record ProfileHeaderParseResult(
    bool IsSuccess,
    ParsedProfileHeader? ParsedHeader,
    string? ErrorMessage
)
{
    public static ProfileHeaderParseResult Success(ParsedProfileHeader header) => new(true, header, null);

    public static ProfileHeaderParseResult Failure(string errorMessage) => new(false, null, errorMessage);

    public static ProfileHeaderParseResult NoProfileHeader() => new(true, null, null);
}

/// <summary>
/// Parsed components of a profile header
/// </summary>
/// <param name="ResourceName">The resource name from the header (singular form, e.g., "student")</param>
/// <param name="ProfileName">The profile name from the header</param>
/// <param name="UsageType">The usage type (readable or writable)</param>
public record ParsedProfileHeader(string ResourceName, string ProfileName, ProfileUsageType UsageType);

/// <summary>
/// Usage type specified in a profile header
/// </summary>
public enum ProfileUsageType
{
    Readable,
    Writable,
}

/// <summary>
/// Parses profile information from Accept and Content-Type headers
/// </summary>
public static class ProfileHeaderParser
{
    // Pattern: application/vnd.ed-fi.{resource}.{profile}.{usage}+json
    // Captures: resource, profile, usage
    // Note: Resource names are camelCase (no hyphens), profile names can contain hyphens
    private static readonly Regex ProfileHeaderRegex = new(
        @"^application/vnd\.ed-fi\.(?<resource>[a-zA-Z][a-zA-Z0-9]*)\.(?<profile>[a-zA-Z][a-zA-Z0-9-]*)\.(?<usage>readable|writable)\+json$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    /// <summary>
    /// Parses a profile header value (from Accept or Content-Type header)
    /// </summary>
    /// <param name="headerValue">The header value to parse</param>
    /// <returns>A parse result containing the parsed header or error information</returns>
    public static ProfileHeaderParseResult Parse(string? headerValue)
    {
        if (string.IsNullOrWhiteSpace(headerValue))
        {
            return ProfileHeaderParseResult.NoProfileHeader();
        }

        // Check if this is a standard JSON content type (not a profile header)
        if (IsStandardJsonContentType(headerValue))
        {
            return ProfileHeaderParseResult.NoProfileHeader();
        }

        // Check if it looks like it should be a profile header but is malformed
        if (headerValue.StartsWith("application/vnd.ed-fi.", StringComparison.OrdinalIgnoreCase))
        {
            Match match = ProfileHeaderRegex.Match(headerValue.Trim());
            if (!match.Success)
            {
                return ProfileHeaderParseResult.Failure(
                    "The format of the profile-based content type header was invalid."
                );
            }

            string resourceName = match.Groups["resource"].Value;
            string profileName = match.Groups["profile"].Value;
            string usageString = match.Groups["usage"].Value.ToLowerInvariant();

            ProfileUsageType usageType =
                usageString == "readable" ? ProfileUsageType.Readable : ProfileUsageType.Writable;

            return ProfileHeaderParseResult.Success(
                new ParsedProfileHeader(resourceName, profileName, usageType)
            );
        }

        // Not a profile header - treat as regular content type
        return ProfileHeaderParseResult.NoProfileHeader();
    }

    /// <summary>
    /// Checks if the header value is a standard JSON content type
    /// </summary>
    private static bool IsStandardJsonContentType(string headerValue)
    {
        string trimmed = headerValue.Trim().ToLowerInvariant();

        // Handle content types with charset or other parameters
        if (trimmed.Contains(';'))
        {
            trimmed = trimmed.Split(';')[0].Trim();
        }

        return trimmed is "application/json" or "text/json";
    }

    /// <summary>
    /// Constructs a profile content type header value
    /// </summary>
    /// <param name="resourceName">The resource name (singular form)</param>
    /// <param name="profileName">The profile name</param>
    /// <param name="usageType">The usage type</param>
    /// <returns>The formatted content type header value</returns>
    public static string BuildProfileContentType(
        string resourceName,
        string profileName,
        ProfileUsageType usageType
    )
    {
        string usage = usageType == ProfileUsageType.Readable ? "readable" : "writable";
        return $"application/vnd.ed-fi.{resourceName.ToLowerInvariant()}.{profileName.ToLowerInvariant()}.{usage}+json";
    }
}
