// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Xml.Linq;

namespace EdFi.DataManagementService.Core.Profile;

/// <summary>
/// Parses XML profile definitions into ProfileDefinition objects
/// </summary>
internal static class ProfileXmlParser
{
    /// <summary>
    /// Parses an XML profile definition string
    /// </summary>
    /// <param name="xmlContent">The XML content to parse</param>
    /// <param name="description">Optional description of the profile</param>
    /// <returns>A parsed ProfileDefinition</returns>
    /// <exception cref="InvalidOperationException">Thrown when the XML is invalid or required elements are missing</exception>
    public static ProfileDefinition Parse(string xmlContent, string? description)
    {
        try
        {
            var doc = XDocument.Parse(xmlContent);
            var profileElement = doc.Element("Profile")
                ?? throw new InvalidOperationException("Root Profile element not found");

            var profileName = profileElement.Attribute("name")?.Value
                ?? throw new InvalidOperationException("Profile name attribute is required");

            var resourcePolicies = new List<ResourcePolicy>();

            foreach (var resourceElement in profileElement.Elements("Resource"))
            {
                var resourceName = resourceElement.Attribute("name")?.Value
                    ?? throw new InvalidOperationException("Resource name attribute is required");

                var readContentType = ParseContentTypePolicy(
                    resourceElement.Element("ReadContentType")
                );
                var writeContentType = ParseContentTypePolicy(
                    resourceElement.Element("WriteContentType")
                );

                resourcePolicies.Add(new ResourcePolicy(
                    resourceName,
                    readContentType,
                    writeContentType
                ));
            }

            return new ProfileDefinition(
                profileName,
                description,
                [.. resourcePolicies]
            );
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException("Failed to parse profile XML", ex);
        }
    }

    private static ContentTypePolicy? ParseContentTypePolicy(XElement? element)
    {
        if (element == null)
        {
            return null;
        }

        var memberSelectionAttr = element.Attribute("memberSelection")?.Value;
        var memberSelection = memberSelectionAttr switch
        {
            "IncludeAll" => MemberSelection.IncludeAll,
            "IncludeOnly" => MemberSelection.IncludeOnly,
            "ExcludeOnly" => MemberSelection.ExcludeOnly,
            _ => MemberSelection.IncludeAll
        };

        var includedProperties = element.Elements("Property")
            .Where(p => p.Attribute("name") != null)
            .Select(p => p.Attribute("name")!.Value)
            .ToArray();

        // For now, we only support included properties
        // Future enhancement: support explicit exclusions
        var excludedProperties = Array.Empty<string>();

        return new ContentTypePolicy(
            memberSelection,
            includedProperties,
            excludedProperties
        );
    }
}
