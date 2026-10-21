// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace EdFi.DmsConfigurationService.SecurityMetadataTools;

/// <summary>
/// Checks the pinned ODS export vocabulary before conversion can discard any content.
/// JSON contract validation remains the responsibility of the existing CMS validator.
/// </summary>
internal static class OdsExportValidator
{
    public static XElement Validate(XDocument document)
    {
        if (document.DocumentType is not null)
        {
            throw new InvalidOperationException(
                "Security metadata XML must not contain a document type declaration."
            );
        }

        if (document.Root is not XElement root || root.Name != "SecurityMetadata")
        {
            throw new InvalidOperationException("Expected a SecurityMetadata root with a Claims element.");
        }

        ValidateElement(root);
        XElement claims = RequireChild(root, "Claims");
        if (!claims.Elements("Claim").Any())
        {
            throw Error(claims, "The export contains no claims.");
        }

        XElement[] claimSets = claims.Descendants("ClaimSet").ToArray();
        if (claimSets.Length == 0)
        {
            throw Error(claims, "The export contains no referenced claim sets.");
        }

        if (!claimSets.Elements("Actions").Elements("Action").Any())
        {
            throw Error(claims, "The export contains no claim-set actions.");
        }

        return claims;
    }

    private static void ValidateElement(XElement element)
    {
        switch (element.Name.LocalName)
        {
            case "SecurityMetadata":
                CheckShape(element, [], [], ["Claims"]);
                break;
            case "Claims":
                CheckShape(element, [], ["Claim"], []);
                break;
            case "Claim":
                CheckShape(element, ["name", "claimId"], [], ["DefaultAuthorization", "ClaimSets", "Claims"]);
                CheckName(element);
                break;
            case "DefaultAuthorization":
            case "Actions":
                CheckShape(element, [], ["Action"], []);
                break;
            case "ClaimSets":
                CheckShape(element, [], ["ClaimSet"], []);
                break;
            case "ClaimSet":
                CheckShape(element, ["name"], [], ["Actions"]);
                // Empty and control-containing claim-set names get the normalization-specific diagnostic.
                if (element.Attribute("name") is null)
                {
                    throw Error(element, "Expected a name attribute.");
                }
                RequireChild(element, "Actions");
                break;
            case "Action":
                bool isDefault = element.Parent?.Name == "DefaultAuthorization";
                string validationAttribute = isDefault ? "validationRuleSet" : "validationRuleSetOverride";
                string strategies = isDefault ? "AuthorizationStrategies" : "AuthorizationStrategyOverrides";
                CheckShape(element, ["name", validationAttribute], [], [strategies]);
                CheckName(element);
                if (
                    element.Attribute(validationAttribute) is XAttribute attribute
                    && attribute.Value.Length > 0
                )
                {
                    throw Error(
                        element,
                        $"Populated {validationAttribute} is unsupported by the CMS claims document."
                    );
                }
                break;
            case "AuthorizationStrategies":
            case "AuthorizationStrategyOverrides":
                CheckShape(element, [], ["AuthorizationStrategy"], []);
                break;
            case "AuthorizationStrategy":
                CheckShape(element, ["name"], [], []);
                CheckName(element);
                break;
        }

        foreach (XElement child in element.Elements())
        {
            ValidateElement(child);
        }
    }

    private static void CheckShape(
        XElement element,
        string[] attributes,
        string[] repeatedChildren,
        string[] singleChildren
    )
    {
        foreach (XName attributeName in element.Attributes().Select(attribute => attribute.Name))
        {
            if (!Array.Exists(attributes, name => attributeName == name))
            {
                throw Error(
                    element,
                    $"Unsupported attribute {JsonSerializer.Serialize(attributeName.ToString())}."
                );
            }
        }

        foreach (XElement child in element.Elements())
        {
            if (!repeatedChildren.Concat(singleChildren).Any(name => child.Name == name))
            {
                throw Error(child, $"Unsupported element {JsonSerializer.Serialize(child.Name.ToString())}.");
            }
        }

        if (Array.Find(singleChildren, name => element.Elements(name).Skip(1).Any()) is string duplicate)
        {
            throw Error(element, $"Expected at most one {duplicate} element.");
        }

        if (
            element
                .Nodes()
                .Any(node =>
                    node is XText text && !string.IsNullOrWhiteSpace(text.Value)
                    || node is XProcessingInstruction
                )
        )
        {
            throw Error(
                element,
                "Unexpected text or processing instruction; policy must use the supported XML elements and attributes."
            );
        }
    }

    private static XElement RequireChild(XElement element, string name) =>
        element.Element(name) ?? throw Error(element, $"Missing required {name} element.");

    private static void CheckName(XElement element)
    {
        if (string.IsNullOrWhiteSpace(element.Attribute("name")?.Value))
        {
            throw Error(element, "Expected a nonempty name attribute.");
        }
    }

    private static InvalidOperationException Error(XElement element, string message)
    {
        string path = string.Join(
            "/",
            element
                .AncestorsAndSelf()
                .Reverse()
                .Select(ancestor =>
                    ancestor.Attribute("name") is XAttribute name
                        ? $"{ancestor.Name}[@name={JsonSerializer.Serialize(name.Value)}]"
                        : ancestor.Name.ToString()
                )
        );
        IXmlLineInfo lineInfo = element;
        string location = lineInfo.HasLineInfo()
            ? $" (line {lineInfo.LineNumber}, column {lineInfo.LinePosition})"
            : "";
        return new InvalidOperationException($"{message} At /{path}{location}.");
    }
}
