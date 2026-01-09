// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Xml;
using System.Xml.Schema;

namespace EdFi.DmsConfigurationService.DataModel.Model.Profile;

/// <summary>
/// Provides validation utilities for Ed-Fi API Profile XML documents.
/// </summary>
public static class ProfileValidationUtils
{
    /// <summary>
    /// Validates that the profile name matches the name attribute in the XML Profile element.
    /// </summary>
    /// <param name="profileName">The expected profile name.</param>
    /// <param name="xml">The XML string to validate.</param>
    /// <returns>True if the XML Profile element's name attribute matches the provided profile name; otherwise, false.</returns>
    public static bool XmlProfileNameMatches(string profileName, string xml)
    {
        try
        {
            var doc = new XmlDocument();
            doc.LoadXml(xml);
            var node = doc.DocumentElement;
            return node != null && node.Name == "Profile" && node.GetAttribute("name") == profileName;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Validates that the XML conforms to the Ed-Fi ODS API Profile XSD schema.
    /// </summary>
    /// <param name="xml">The XML string to validate.</param>
    /// <returns>True if the XML is valid according to the Ed-Fi-ODS-API-Profile.xsd schema; otherwise, false.</returns>
    public static bool IsValidProfileXml(string xml)
    {
        try
        {
            var doc = new XmlDocument();
            doc.LoadXml(xml);
            var path = new Uri(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!).LocalPath;
            var schemas = new XmlSchemaSet();
            schemas.Add("", Path.Combine(path, "Schema", "Ed-Fi-ODS-API-Profile.xsd"));
            bool valid = true;
            doc.Schemas.Add(schemas);
            doc.Validate(
                (o, e) =>
                {
                    valid = false;
                }
            );
            return valid;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Validates that the Profile XML contains at least one Resource element.
    /// </summary>
    /// <param name="xml">The XML string to validate.</param>
    /// <returns>True if the XML contains at least one Resource element; otherwise, false.</returns>
    public static bool HasAtLeastOneResource(string xml)
    {
        try
        {
            var doc = new XmlDocument();
            doc.LoadXml(xml);
            var resources = doc.DocumentElement?.GetElementsByTagName("Resource");
            return resources != null && resources.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Validates that all Resource elements in the Profile XML have a non-empty name attribute.
    /// </summary>
    /// <param name="xml">The XML string to validate.</param>
    /// <returns>True if all Resource elements have a valid name attribute; otherwise, false.</returns>
    public static bool AllResourcesHaveNameAttribute(string xml)
    {
        try
        {
            var doc = new XmlDocument();
            doc.LoadXml(xml);
            var resources = doc.DocumentElement?.GetElementsByTagName("Resource");
            if (resources == null)
            {
                return false;
            }
            foreach (XmlNode node in resources)
            {
                if (
                    node.Attributes?["name"] == null
                    || string.IsNullOrWhiteSpace(node?.Attributes?["name"]?.Value ?? string.Empty)
                )
                {
                    return false;
                }
            }
            return true;
        }
        catch
        {
            return false;
        }
    }
}
