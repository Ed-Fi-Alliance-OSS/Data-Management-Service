// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Xml;
using System.Xml.Linq;
using EdFi.DataManagementService.Core.Profiles.Model;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Profiles;

/// <summary>
/// Loads and parses XML profile documents into internal profile models.
/// </summary>
public class ProfileXmlLoader(ILogger<ProfileXmlLoader> _logger)
{
    /// <summary>
    /// Loads all XML profile documents from the specified directory.
    /// </summary>
    /// <param name="profilesPath">Directory path containing XML profile files</param>
    /// <returns>Array of parsed profiles</returns>
    public ApiProfile[] LoadProfilesFromDirectory(string profilesPath)
    {
        if (!Directory.Exists(profilesPath))
        {
            _logger.LogWarning("Profiles directory does not exist: {ProfilesPath}", profilesPath);
            return [];
        }

        var profiles = new List<ApiProfile>();
        var xmlFiles = Directory.GetFiles(profilesPath, "*.xml");

        _logger.LogInformation("Loading profiles from {ProfilesPath}, found {FileCount} XML files", profilesPath, xmlFiles.Length);

        foreach (var xmlFile in xmlFiles)
        {
            try
            {
                var profile = LoadProfileFromFile(xmlFile);
                if (profile != null)
                {
                    profiles.Add(profile);
                    _logger.LogInformation("Loaded profile '{ProfileName}' from {FileName}", profile.Name, Path.GetFileName(xmlFile));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load profile from {FileName}", Path.GetFileName(xmlFile));
            }
        }

        _logger.LogInformation("Successfully loaded {ProfileCount} profiles", profiles.Count);
        return profiles.ToArray();
    }

    /// <summary>
    /// Loads a single profile from an XML file.
    /// </summary>
    /// <param name="filePath">Path to the XML profile file</param>
    /// <returns>Parsed profile or null if invalid</returns>
    public ApiProfile? LoadProfileFromFile(string filePath)
    {
        // Use secure XML settings to prevent XXE attacks
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null
        };

        using var reader = XmlReader.Create(filePath, settings);
        var doc = XDocument.Load(reader, LoadOptions.None);
        return ParseProfile(doc);
    }

    /// <summary>
    /// Parses a profile from XML content.
    /// </summary>
    /// <param name="xmlContent">XML content as string</param>
    /// <returns>Parsed profile or null if invalid</returns>
    public ApiProfile? ParseProfileFromXml(string xmlContent)
    {
        // Use secure XML settings to prevent XXE attacks
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null
        };

        using var stringReader = new StringReader(xmlContent);
        using var reader = XmlReader.Create(stringReader, settings);
        var doc = XDocument.Load(reader, LoadOptions.None);
        return ParseProfile(doc);
    }

    private ApiProfile? ParseProfile(XDocument doc)
    {
        var profileElement = doc.Element("Profile");
        if (profileElement == null)
        {
            _logger.LogWarning("XML document does not contain a Profile root element");
            return null;
        }

        var profileName = profileElement.Attribute("name")?.Value;
        if (string.IsNullOrWhiteSpace(profileName))
        {
            _logger.LogWarning("Profile element missing 'name' attribute");
            return null;
        }

        var resources = new List<ProfileResource>();
        foreach (var resourceElement in profileElement.Elements("Resource"))
        {
            var resource = ParseResource(resourceElement);
            if (resource != null)
            {
                resources.Add(resource);
            }
        }

        if (resources.Count == 0)
        {
            _logger.LogWarning("Profile '{ProfileName}' contains no valid resources", profileName);
            return null;
        }

        return new ApiProfile(profileName, resources.ToArray());
    }

    private ProfileResource? ParseResource(XElement resourceElement)
    {
        var resourceName = resourceElement.Attribute("name")?.Value;
        if (string.IsNullOrWhiteSpace(resourceName))
        {
            _logger.LogWarning("Resource element missing 'name' attribute");
            return null;
        }

        var readContentType = resourceElement.Element("ReadContentType");
        var writeContentType = resourceElement.Element("WriteContentType");

        if (readContentType == null && writeContentType == null)
        {
            _logger.LogWarning("Resource '{ResourceName}' has neither ReadContentType nor WriteContentType", resourceName);
            return null;
        }

        return new ProfileResource(
            resourceName,
            readContentType != null ? ParseContentType(readContentType) : null,
            writeContentType != null ? ParseContentType(writeContentType) : null
        );
    }

    private ContentType? ParseContentType(XElement contentTypeElement)
    {
        var memberSelectionAttr = contentTypeElement.Attribute("memberSelection")?.Value;
        if (string.IsNullOrWhiteSpace(memberSelectionAttr))
        {
            _logger.LogWarning("ContentType element missing 'memberSelection' attribute");
            return null;
        }

        if (!Enum.TryParse<MemberSelection>(memberSelectionAttr, ignoreCase: true, out var memberSelection))
        {
            _logger.LogWarning("Invalid memberSelection value: {MemberSelection}", memberSelectionAttr);
            return null;
        }

        var properties = contentTypeElement
            .Elements("Property")
            .Select(ParseProperty)
            .Where(p => p != null)
            .Cast<ProfileProperty>()
            .ToArray();

        var collections = contentTypeElement
            .Elements("Collection")
            .Select(ParseCollection)
            .Where(c => c != null)
            .Cast<ProfileCollection>()
            .ToArray();

        return new ContentType(memberSelection, properties, collections);
    }

    private ProfileProperty? ParseProperty(XElement propertyElement)
    {
        var propertyName = propertyElement.Attribute("name")?.Value;
        if (string.IsNullOrWhiteSpace(propertyName))
        {
            _logger.LogWarning("Property element missing 'name' attribute");
            return null;
        }

        return new ProfileProperty(propertyName);
    }

    private ProfileCollection? ParseCollection(XElement collectionElement)
    {
        var collectionName = collectionElement.Attribute("name")?.Value;
        if (string.IsNullOrWhiteSpace(collectionName))
        {
            _logger.LogWarning("Collection element missing 'name' attribute");
            return null;
        }

        var memberSelectionAttr = collectionElement.Attribute("memberSelection")?.Value ?? "IncludeAll";
        if (!Enum.TryParse<MemberSelection>(memberSelectionAttr, ignoreCase: true, out var memberSelection))
        {
            _logger.LogWarning("Invalid memberSelection value for collection: {MemberSelection}", memberSelectionAttr);
            memberSelection = MemberSelection.IncludeAll;
        }

        return new ProfileCollection(collectionName, memberSelection);
    }
}
