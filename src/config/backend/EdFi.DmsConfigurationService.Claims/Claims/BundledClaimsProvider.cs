// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend.Claims.Models;

namespace EdFi.DmsConfigurationService.Claims;

/// <summary>
/// Selects and loads the bundled base claims without host or database dependencies.
/// </summary>
public static class BundledClaimsProvider
{
    public const string DefaultDataStandardVersion = "5.2";

    /// <summary>
    /// Resolves the configured version, preserving the CMS default for unset or whitespace values.
    /// </summary>
    public static string ResolveDataStandardVersion(string? version) =>
        string.IsNullOrWhiteSpace(version) ? DefaultDataStandardVersion : version.Trim();

    private static string EmbeddedClaimsResourceName(Assembly assembly, string dataStandardVersion)
    {
        string folder = "ds" + dataStandardVersion.Replace(".", "");
        return $"{assembly.GetName().Name}.Claims.Standards.{folder}.Claims.json";
    }

    /// <summary>
    /// Loads a fresh document for the selected version, or returns structured loading failures.
    /// </summary>
    public static ClaimsLoadResult Load(string? dataStandardVersion = null)
    {
        try
        {
            Assembly assembly = typeof(BundledClaimsProvider).Assembly;
            string version = ResolveDataStandardVersion(dataStandardVersion);
            string resourceName = EmbeddedClaimsResourceName(assembly, version);

            if (assembly.GetManifestResourceStream(resourceName) is not Stream stream)
            {
                ClaimsFailure failure = new(
                    "Configuration",
                    $"Could not load embedded Claims.json for data standard version '{version}' (resource '{resourceName}')"
                );
                return new ClaimsLoadResult(null, [failure]);
            }

            using StreamReader reader = new(stream);
            string jsonContent = reader.ReadToEnd();

            if (JsonNode.Parse(jsonContent) is not JsonNode claimsJson)
            {
                ClaimsFailure failure = new(
                    "ParseError",
                    "Unable to parse assembly-bundled Claims.json file"
                );
                return new ClaimsLoadResult(null, [failure]);
            }

            if (
                claimsJson["claimSets"] is not JsonNode claimSetsNode
                || claimsJson["claimsHierarchy"] is not JsonNode claimsHierarchyNode
            )
            {
                ClaimsFailure failure = new(
                    "Configuration",
                    "Assembly Claims.json missing required properties"
                );
                return new ClaimsLoadResult(null, [failure]);
            }

            return new ClaimsLoadResult(new ClaimsDocument(claimSetsNode, claimsHierarchyNode), []);
        }
        catch (JsonException ex)
        {
            ClaimsFailure failure = new("JsonError", "Invalid JSON format in embedded Claims.json", null, ex);
            return new ClaimsLoadResult(null, [failure]);
        }
        catch (IOException ex)
        {
            ClaimsFailure failure = new(
                "IOError",
                "Failed to read embedded Claims.json from assembly",
                null,
                ex
            );
            return new ClaimsLoadResult(null, [failure]);
        }
        catch (InvalidOperationException ex)
        {
            ClaimsFailure failure = new(
                "ResourceError",
                "Embedded Claims.json resource not found in assembly",
                null,
                ex
            );
            return new ClaimsLoadResult(null, [failure]);
        }
    }
}
