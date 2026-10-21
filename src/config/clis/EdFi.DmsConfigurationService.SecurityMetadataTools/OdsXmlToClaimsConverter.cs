// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
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
        NameMapping names = new();
        JsonArray hierarchy = ConvertClaims(claims, names);

        return new JsonObject { ["claimSets"] = names.Catalog, ["claimsHierarchy"] = hierarchy };
    }

    private static JsonArray ConvertClaims(XElement claims, NameMapping names) =>
        new(claims.Elements("Claim").Select(claim => ConvertClaim(claim, names)).ToArray());

    private static JsonObject ConvertClaim(XElement claim, NameMapping names)
    {
        // claimId is export metadata, not part of the CMS document.
        JsonObject result = new() { ["name"] = names.MapClaimName(ReadName(claim)) };

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
                claimSets.Elements("ClaimSet").Select(claimSet => ConvertClaimSet(claimSet, names)).ToArray()
            );
        }

        if (claim.Element("Claims") is XElement children)
        {
            result["claims"] = ConvertClaims(children, names);
        }

        return result;
    }

    private static JsonObject ConvertDefaultAction(XElement action) =>
        new()
        {
            ["name"] = ReadName(action),
            ["authorizationStrategies"] = ConvertStrategies(action, "AuthorizationStrategies"),
        };

    private static JsonObject ConvertClaimSet(XElement claimSet, NameMapping names)
    {
        return new JsonObject
        {
            ["name"] = names.MapClaimSetName(ReadName(claimSet)),
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

    /// <summary>
    /// Tracks source identities across the entire tree, separately for claim sets and claims.
    /// A fresh mapping per conversion prevents one export from affecting the next.
    /// </summary>
    private sealed class NameMapping
    {
        private const string LegacyClaimPrefix = "http://ed-fi.org/ods/identity/claims/";
        private const string CurrentClaimPrefix = "http://ed-fi.org/identity/claims/";

        private readonly Dictionary<string, string> _claimSetNames = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _claimSetSources = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _claimSources = new(StringComparer.Ordinal);

        public JsonArray Catalog { get; } = [];

        public string MapClaimSetName(string sourceName)
        {
            if (_claimSetNames.TryGetValue(sourceName, out var existingName))
            {
                return existingName;
            }

            string mappedName = sourceName
                .Replace(" ", "", StringComparison.Ordinal)
                .Replace("-", "", StringComparison.Ordinal);

            if (mappedName.Length is 0 or > 256 || mappedName.Any(char.IsControl))
            {
                throw new InvalidOperationException(
                    $"Claim-set name {JsonSerializer.Serialize(sourceName)} must contain 1 to 256 characters after removing literal spaces and hyphens, and must not contain control characters."
                );
            }

            if (_claimSetSources.TryGetValue(mappedName, out var previousSource))
            {
                throw new InvalidOperationException(
                    $"Claim-set names {JsonSerializer.Serialize(previousSource)} and {JsonSerializer.Serialize(sourceName)} collide after normalization to {JsonSerializer.Serialize(mappedName)} (case-insensitive comparison)."
                );
            }

            _claimSetNames.Add(sourceName, mappedName);
            _claimSetSources.Add(mappedName, sourceName);
            Catalog.Add(new JsonObject { ["claimSetName"] = mappedName, ["isSystemReserved"] = false });
            return mappedName;
        }

        public string MapClaimName(string sourceName)
        {
            string mappedName = sourceName.StartsWith(LegacyClaimPrefix, StringComparison.Ordinal)
                ? CurrentClaimPrefix + sourceName[LegacyClaimPrefix.Length..]
                : sourceName;

            if (_claimSources.TryGetValue(mappedName, out var previousSource) && previousSource != sourceName)
            {
                throw new InvalidOperationException(
                    $"Claim URIs {JsonSerializer.Serialize(previousSource)} and {JsonSerializer.Serialize(sourceName)} collide after legacy prefix migration to {JsonSerializer.Serialize(mappedName)}."
                );
            }

            _claimSources[mappedName] = sourceName;
            return mappedName;
        }
    }

    private static string ReadName(XElement element) =>
        element.Attribute("name")?.Value
        ?? throw new InvalidOperationException($"Expected a name attribute on {element.Name}.");
}
