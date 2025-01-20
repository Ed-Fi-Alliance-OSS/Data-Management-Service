// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Core.ApiSchema;

/// <summary>
/// Loads and parses ApiSchema files.
/// </summary>
internal class ApiSchemaFileLoader(ILogger<ApiSchemaFileLoader> _logger, IOptions<AppSettings> appSettings)
    : IApiSchemaProvider
{
    /// <summary>
    /// Loads the resource with the given resourceName from the assembly as a JsonNode
    /// </summary>
    private static JsonNode LoadFromAssembly(string resourceName, Assembly assembly)
    {
        using Stream stream =
            assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Could not load ApiSchema '{resourceName}'");

        using StreamReader reader = new(stream);

        string? jsonContent = reader.ReadToEnd();

        return JsonNode.Parse(jsonContent)
            ?? throw new InvalidOperationException($"Unable to parse ApiSchema file '{resourceName}'");
    }

    /// <summary>
    /// Loads the core ApiSchema file, either from the file system or an ApiSchema assembly
    /// </summary>
    private readonly Lazy<JsonNode> _coreApiSchemaRootNode = new(() =>
    {
        _logger.LogDebug("Entering ApiSchemaFileLoader._coreApiSchemaRootNode");

        string jsonContent;

        if (appSettings.Value.UseLocalApiSchemaJson)
        {
            jsonContent = File.ReadAllText(
                Path.Combine(
                    Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "",
                    "ApiSchema",
                    "ApiSchema.json"
                )
            );

            return JsonNode.Parse(jsonContent)
                ?? throw new InvalidOperationException($"Unable to parse ApiSchema file 'ApiSchema.json'");
        }
        else
        {
            Assembly assembly =
                Assembly.GetAssembly(typeof(DataStandard51.ApiSchema.Marker))
                ?? throw new InvalidOperationException("Could not load the core ApiSchema library");

            string? resourceName = assembly
                .GetManifestResourceNames()
                .Single(str => str.EndsWith("ApiSchema.json"));

            return LoadFromAssembly(resourceName, assembly);
        }
    });

    public JsonNode CoreApiSchemaRootNode => _coreApiSchemaRootNode.Value;

    /// <summary>
    /// Loads extension ApiSchema files if they exist, either from the file system or ApiSchema assemblies
    /// </summary>
    private readonly Lazy<JsonNode[]> _extensionApiSchemaRootNodes = new(() =>
    {
        _logger.LogDebug("Entering ApiSchemaFileLoader._extensionApiSchemaRootNode");

        if (appSettings.Value.UseLocalApiSchemaJson)
        {
            string? jsonContent;
            try
            {
                jsonContent = File.ReadAllText(
                    Path.Combine(
                        Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "",
                        "ApiSchema",
                        "ApiSchema.Extension.json"
                    )
                );

                // No extension file found
                if (jsonContent == null)
                {
                    return [];
                }
            }
            catch (Exception ex)
            {
                _logger.LogInformation(ex, "No ApiSchema.Extension.json file was found.");
                return [];
            }

            JsonNode rootNode =
                JsonNode.Parse(jsonContent)
                ?? throw new InvalidOperationException("Unable to parse ApiSchema extension file");
            return [rootNode];
        }
        else
        {
            // DMS-497 will fix: This assembly marker should instead be some indicator of extension assemblies
            Assembly assembly =
                Assembly.GetAssembly(typeof(DataStandard51.ApiSchema.Marker))
                ?? throw new InvalidOperationException("Could not load the ApiSchema extension library");

            IEnumerable<string> resourceNames = assembly
                .GetManifestResourceNames()
                .Where(str => str.EndsWith("ApiSchema.Extension.json"));

            return resourceNames.Select(resourceName => LoadFromAssembly(resourceName, assembly)).ToArray();
        }
    });

    public JsonNode[] ExtensionApiSchemaRootNodes => _extensionApiSchemaRootNodes.Value;
}
