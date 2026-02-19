// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace EdFi.DataManagementService.Tests.E2E.Profiles;

/// <summary>
/// Loads profile definitions from XML files for E2E testing.
/// </summary>
public static class ProfileXmlFileLoader
{
    public static IReadOnlyList<(string Name, string Xml)> LoadProfiles(string relativePath)
    {
        string fullPath = ResolvePath(relativePath);
        XDocument doc = XDocument.Load(fullPath);

        XElement root =
            doc.Root ?? throw new InvalidOperationException("Profile XML file has no root element.");
        IEnumerable<XElement> profileElements =
            root.Name.LocalName == "Profile" ? new[] { root } : root.Elements("Profile");

        var profiles = new List<(string Name, string Xml)>();
        foreach (XElement element in profileElements)
        {
            string? name = element.Attribute("name")?.Value;
            if (string.IsNullOrEmpty(name))
            {
                throw new InvalidOperationException(
                    $"Profile element missing required 'name' attribute in file '{relativePath}'."
                );
            }

            profiles.Add((name, element.ToString(SaveOptions.DisableFormatting)));
        }

        return profiles;
    }

    public static string LoadProfileXml(string relativePath, string profileName)
    {
        IReadOnlyList<(string Name, string Xml)> profiles = LoadProfiles(relativePath);
        (string Name, string Xml)? match = profiles.FirstOrDefault(profile =>
            string.Equals(profile.Name, profileName, StringComparison.Ordinal)
        );

        if (string.IsNullOrEmpty(match.Value.Name))
        {
            throw new InvalidOperationException(
                $"Profile '{profileName}' not found in XML file '{relativePath}'."
            );
        }

        return match.Value.Xml;
    }

    private static string ResolvePath(string relativePath)
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string normalizedRelativePath = relativePath.Replace("/", Path.DirectorySeparatorChar.ToString());
        string fullPath = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", normalizedRelativePath));

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Profile XML file not found: {fullPath}");
        }

        return fullPath;
    }
}
