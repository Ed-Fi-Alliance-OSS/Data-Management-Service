// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace EdFi.DmsConfigurationService.SecurityMetadataTools;

/// <summary>
/// Maps the ODS SecurityMetadata/Claims export to the CMS Claims.json contract.
/// Inheritance is retained in the tree, not resolved into each child claim.
/// </summary>
internal static class OdsXmlToClaimsConverter
{
    public static JsonObject Convert(XDocument document)
    {
        if (
            document.Root is not XElement root
            || root.Name != "SecurityMetadata"
            || root.Element("Claims") is not XElement claims
        )
        {
            throw new InvalidOperationException("Expected a SecurityMetadata root with a Claims element.");
        }

        // One per-document mapping supplies both the catalog and hierarchy references.
        Dictionary<string, string> claimSetNames = new(StringComparer.Ordinal);
        JsonArray catalog = [];
        JsonArray hierarchy = ConvertClaims(claims, claimSetNames, catalog);

        return new JsonObject { ["claimSets"] = catalog, ["claimsHierarchy"] = hierarchy };
    }

    private static JsonArray ConvertClaims(
        XElement claims,
        Dictionary<string, string> claimSetNames,
        JsonArray catalog
    ) => new(claims.Elements("Claim").Select(claim => ConvertClaim(claim, claimSetNames, catalog)).ToArray());

    private static JsonObject ConvertClaim(
        XElement claim,
        Dictionary<string, string> claimSetNames,
        JsonArray catalog
    )
    {
        // claimId is export metadata, not part of the CMS document.
        JsonObject result = new() { ["name"] = ReadName(claim) };

        if (claim.Element("DefaultAuthorization") is XElement defaults && defaults.Elements("Action").Any())
        {
            result["defaultAuthorization"] = new JsonObject
            {
                ["actions"] = new JsonArray(
                    defaults.Elements("Action").Select(ConvertDefaultAction).ToArray()
                ),
            };
        }

        if (claim.Element("ClaimSets") is XElement claimSets)
        {
            result["claimSets"] = new JsonArray(
                claimSets
                    .Elements("ClaimSet")
                    .Select(claimSet => ConvertClaimSet(claimSet, claimSetNames, catalog))
                    .ToArray()
            );
        }

        if (claim.Element("Claims") is XElement children)
        {
            result["claims"] = ConvertClaims(children, claimSetNames, catalog);
        }

        return result;
    }

    private static JsonObject ConvertDefaultAction(XElement action) =>
        new()
        {
            ["name"] = ReadName(action),
            ["authorizationStrategies"] = ConvertStrategies(action, "AuthorizationStrategies"),
        };

    private static JsonObject ConvertClaimSet(
        XElement claimSet,
        Dictionary<string, string> claimSetNames,
        JsonArray catalog
    )
    {
        string sourceName = ReadName(claimSet);
        if (!claimSetNames.ContainsKey(sourceName))
        {
            claimSetNames.Add(sourceName, sourceName);
            catalog.Add(
                new JsonObject { ["claimSetName"] = claimSetNames[sourceName], ["isSystemReserved"] = false }
            );
        }

        return new JsonObject
        {
            ["name"] = claimSetNames[sourceName],
            ["actions"] = new JsonArray(
                (claimSet.Element("Actions")?.Elements("Action") ?? [])
                    .Select(ConvertClaimSetAction)
                    .ToArray()
            ),
        };
    }

    private static JsonObject ConvertClaimSetAction(XElement action)
    {
        JsonObject result = new() { ["name"] = ReadName(action) };

        // Absent and explicitly empty overrides are different source declarations.
        if (action.Element("AuthorizationStrategyOverrides") is not null)
        {
            result["authorizationStrategyOverrides"] = ConvertStrategies(
                action,
                "AuthorizationStrategyOverrides"
            );
        }

        return result;
    }

    private static JsonArray ConvertStrategies(XElement action, string containerName) =>
        new(
            (action.Element(containerName)?.Elements("AuthorizationStrategy") ?? [])
                .Select(strategy => new JsonObject { ["name"] = ReadName(strategy) })
                .ToArray()
        );

    private static string ReadName(XElement element) =>
        element.Attribute("name")?.Value
        ?? throw new InvalidOperationException($"Expected a name attribute on {element.Name}.");
}
