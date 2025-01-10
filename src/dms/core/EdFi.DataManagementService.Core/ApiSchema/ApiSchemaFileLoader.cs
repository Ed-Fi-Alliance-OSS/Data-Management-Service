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
/// Loads and parses ApiSchema file.
/// </summary>
internal class ApiSchemaFileLoader(ILogger<ApiSchemaFileLoader> _logger, IOptions<AppSettings> appSettings)
    : IApiSchemaProvider
{
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
                    "ds-5.1-api-schema-authoritative.json"
                )
            );
        }
        else
        {
            var assembly =
                Assembly.GetAssembly(typeof(DataStandard51.ApiSchema.Marker))
                ?? throw new InvalidOperationException("Could not load the ApiSchema library");

            var resourceName = assembly
                .GetManifestResourceNames()
                .Single(str => str.EndsWith("ApiSchema.json"));

            using Stream stream =
                assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException("Could not load ApiSchema resource");
            using StreamReader reader = new(stream);

            jsonContent = reader.ReadToEnd();
        }

        JsonNode? rootNodeFromFile = JsonNode.Parse(jsonContent);
        if (rootNodeFromFile == null)
        {
            _logger.LogCritical("Unable to read and parse Api Schema file");
            throw new InvalidOperationException("Unable to read and parse Api Schema file.");
        }
        return rootNodeFromFile;
    });

    public JsonNode CoreApiSchemaRootNode => _coreApiSchemaRootNode.Value;

    private readonly Lazy<JsonNode[]> _extensionApiSchemaRootNodes = new(() =>
    {
        _logger.LogDebug("Entering ApiSchemaFileLoader._extensionApiSchemaRootNode");

        string jsonContent;

        if (appSettings.Value.UseLocalApiSchemaJson)
        {
            jsonContent = File.ReadAllText(
                Path.Combine(
                    Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "",
                    "ApiSchema",
                    "tpdm-api-schema-authoritative.json"
                )
            );
        }
        else
        {
            var assembly =
                Assembly.GetAssembly(typeof(DataStandard51.ApiSchema.Marker))
                ?? throw new InvalidOperationException("Could not load the ApiSchema library");

            var resourceName = assembly
                .GetManifestResourceNames()
                .Single(str => str.EndsWith("ApiSchema.json"));

            using Stream stream =
                assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException("Could not load ApiSchema resource");
            using StreamReader reader = new(stream);

            jsonContent = reader.ReadToEnd();
        }

        JsonNode? rootNodeFromFile = JsonNode.Parse(jsonContent);
        if (rootNodeFromFile == null)
        {
            _logger.LogCritical("Unable to read and parse Api Schema file");
            throw new InvalidOperationException("Unable to read and parse Api Schema file.");
        }
        return [rootNodeFromFile];
    });

    public JsonNode[] ExtensionApiSchemaRootNodes => _extensionApiSchemaRootNodes.Value;
}
