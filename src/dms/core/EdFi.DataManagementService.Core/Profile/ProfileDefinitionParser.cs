// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Xml.Linq;

namespace EdFi.DataManagementService.Core.Profile;

/// <summary>
/// Result of parsing a profile definition XML
/// </summary>
public record ProfileDefinitionParseResult(
    bool IsSuccess,
    ProfileDefinition? Definition,
    string? ErrorMessage
)
{
    public static ProfileDefinitionParseResult Success(ProfileDefinition definition) =>
        new(true, definition, null);

    public static ProfileDefinitionParseResult Failure(string errorMessage) => new(false, null, errorMessage);
}

/// <summary>
/// Parses profile definition XML into ProfileDefinition data structures
/// </summary>
public static class ProfileDefinitionParser
{
    /// <summary>
    /// Parses a profile definition XML string into a ProfileDefinition
    /// </summary>
    /// <param name="xmlDefinition">The XML profile definition string</param>
    /// <returns>A parse result containing the parsed definition or error information</returns>
    public static ProfileDefinitionParseResult Parse(string xmlDefinition)
    {
        if (string.IsNullOrWhiteSpace(xmlDefinition))
        {
            return ProfileDefinitionParseResult.Failure("Profile definition XML is empty or null.");
        }

        try
        {
            XDocument doc = XDocument.Parse(xmlDefinition);
            XElement? profileElement = doc.Root;

            if (profileElement == null || profileElement.Name.LocalName != "Profile")
            {
                return ProfileDefinitionParseResult.Failure(
                    "Profile definition XML must have a 'Profile' root element."
                );
            }

            string? profileName = profileElement.Attribute("name")?.Value;
            if (string.IsNullOrWhiteSpace(profileName))
            {
                return ProfileDefinitionParseResult.Failure("Profile element must have a 'name' attribute.");
            }

            var resources = new List<ResourceProfile>();
            foreach (XElement resourceElement in profileElement.Elements("Resource"))
            {
                ResourceProfile? resourceProfile = ParseResource(resourceElement);
                if (resourceProfile != null)
                {
                    resources.Add(resourceProfile);
                }
            }

            if (resources.Count == 0)
            {
                return ProfileDefinitionParseResult.Failure(
                    "Profile definition must contain at least one Resource element."
                );
            }

            return ProfileDefinitionParseResult.Success(new ProfileDefinition(profileName, resources));
        }
        catch (Exception ex)
        {
            return ProfileDefinitionParseResult.Failure(
                $"Failed to parse profile definition XML: {ex.Message}"
            );
        }
    }

    private static ResourceProfile? ParseResource(XElement resourceElement)
    {
        string? resourceName = resourceElement.Attribute("name")?.Value;
        if (string.IsNullOrWhiteSpace(resourceName))
        {
            return null;
        }

        string? logicalSchema = resourceElement.Attribute("logicalSchema")?.Value;

        XElement? readContentTypeElement = resourceElement.Element("ReadContentType");
        XElement? writeContentTypeElement = resourceElement.Element("WriteContentType");

        ContentTypeDefinition? readContentType =
            readContentTypeElement != null ? ParseContentType(readContentTypeElement) : null;

        ContentTypeDefinition? writeContentType =
            writeContentTypeElement != null ? ParseContentType(writeContentTypeElement) : null;

        return new ResourceProfile(resourceName, logicalSchema, readContentType, writeContentType);
    }

    private static ContentTypeDefinition ParseContentType(XElement contentTypeElement)
    {
        MemberSelection memberSelection = ParseMemberSelection(
            contentTypeElement.Attribute("memberSelection")?.Value
        );

        var properties = ParseProperties(contentTypeElement);
        var objects = ParseObjects(contentTypeElement);
        var collections = ParseCollections(contentTypeElement);
        var extensions = ParseExtensions(contentTypeElement);

        return new ContentTypeDefinition(memberSelection, properties, objects, collections, extensions);
    }

    private static MemberSelection ParseMemberSelection(string? value)
    {
        return value?.ToLowerInvariant() switch
        {
            "includeonly" => MemberSelection.IncludeOnly,
            "excludeonly" => MemberSelection.ExcludeOnly,
            "includeall" => MemberSelection.IncludeAll,
            _ => MemberSelection.IncludeAll, // Default to IncludeAll if not specified
        };
    }

    private static IReadOnlyList<PropertyRule> ParseProperties(XElement parentElement)
    {
        return parentElement
            .Elements("Property")
            .Select(e => e.Attribute("name")?.Value)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => new PropertyRule(name!))
            .ToList();
    }

    private static IReadOnlyList<ObjectRule> ParseObjects(XElement parentElement)
    {
        return parentElement.Elements("Object").Select(ParseObject).Where(o => o != null).ToList()!;
    }

    private static ObjectRule? ParseObject(XElement objectElement)
    {
        string? name = objectElement.Attribute("name")?.Value;
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        MemberSelection memberSelection = ParseMemberSelection(
            objectElement.Attribute("memberSelection")?.Value
        );
        string? logicalSchema = objectElement.Attribute("logicalSchema")?.Value;

        var properties = ParseProperties(objectElement);
        var nestedObjects = ParseObjects(objectElement);
        var collections = ParseCollections(objectElement);
        var extensions = ParseExtensions(objectElement);

        return new ObjectRule(
            name,
            memberSelection,
            logicalSchema,
            properties.Count > 0 ? properties : null,
            nestedObjects.Count > 0 ? nestedObjects : null,
            collections.Count > 0 ? collections : null,
            extensions.Count > 0 ? extensions : null
        );
    }

    private static IReadOnlyList<CollectionRule> ParseCollections(XElement parentElement)
    {
        return parentElement.Elements("Collection").Select(ParseCollection).Where(c => c != null).ToList()!;
    }

    private static CollectionRule? ParseCollection(XElement collectionElement)
    {
        string? name = collectionElement.Attribute("name")?.Value;
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        MemberSelection memberSelection = ParseMemberSelection(
            collectionElement.Attribute("memberSelection")?.Value
        );
        string? logicalSchema = collectionElement.Attribute("logicalSchema")?.Value;

        var properties = ParseProperties(collectionElement);
        var nestedObjects = ParseObjects(collectionElement);
        var nestedCollections = ParseCollections(collectionElement);
        var extensions = ParseExtensions(collectionElement);

        CollectionItemFilter? itemFilter = ParseFilter(collectionElement.Element("Filter"));

        return new CollectionRule(
            name,
            memberSelection,
            logicalSchema,
            properties.Count > 0 ? properties : null,
            nestedObjects.Count > 0 ? nestedObjects : null,
            nestedCollections.Count > 0 ? nestedCollections : null,
            extensions.Count > 0 ? extensions : null,
            itemFilter
        );
    }

    private static IReadOnlyList<ExtensionRule> ParseExtensions(XElement parentElement)
    {
        return parentElement.Elements("Extension").Select(ParseExtension).Where(e => e != null).ToList()!;
    }

    private static ExtensionRule? ParseExtension(XElement extensionElement)
    {
        string? name = extensionElement.Attribute("name")?.Value;
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        MemberSelection memberSelection = ParseMemberSelection(
            extensionElement.Attribute("memberSelection")?.Value
        );
        string? logicalSchema = extensionElement.Attribute("logicalSchema")?.Value;

        var properties = ParseProperties(extensionElement);
        var objects = ParseObjects(extensionElement);
        var collections = ParseCollections(extensionElement);

        return new ExtensionRule(
            name,
            memberSelection,
            logicalSchema,
            properties.Count > 0 ? properties : null,
            objects.Count > 0 ? objects : null,
            collections.Count > 0 ? collections : null
        );
    }

    private static CollectionItemFilter? ParseFilter(XElement? filterElement)
    {
        if (filterElement == null)
        {
            return null;
        }

        string? propertyName = filterElement.Attribute("propertyName")?.Value;
        if (string.IsNullOrWhiteSpace(propertyName))
        {
            return null;
        }

        string? filterModeValue = filterElement.Attribute("filterMode")?.Value;
        FilterMode filterMode = filterModeValue?.ToLowerInvariant() switch
        {
            "excludeonly" => FilterMode.ExcludeOnly,
            _ => FilterMode.IncludeOnly, // Default to IncludeOnly
        };

        var values = filterElement
            .Elements("Value")
            .Select(e => e.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToList();

        if (values.Count == 0)
        {
            return null;
        }

        return new CollectionItemFilter(propertyName, filterMode, values);
    }
}
