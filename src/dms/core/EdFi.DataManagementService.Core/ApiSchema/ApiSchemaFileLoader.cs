// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

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
    private readonly Lazy<Dictionary<string, JsonNode>> _apiSchemaNodes = new(() =>
    {
        _logger.LogDebug("Entering ApiSchemaFileLoader._apiSchemaNodes");

        var schemaNodes = new Dictionary<string, JsonNode>();

        string basePath = Path.GetFullPath(appSettings.Value.ApiSchemaFolder);

        if (!Directory.Exists(basePath))
        {
            _logger.LogError("ApiSchema folder '{BasePath}' does not exist.", basePath);
            return schemaNodes;
        }

        string[] schemaFiles = ["ApiSchema.json", "ApiSchema.Extension.json"];

        foreach (string fileName in schemaFiles)
        {
            string filePath = Path.Combine(basePath, fileName);
            if (!File.Exists(filePath))
            {
                _logger.LogInformation(
                    "Schema file '{FileName}' not found in '{BasePath}'.",
                    fileName,
                    basePath
                );
                continue;
            }

            try
            {
                string jsonContent = File.ReadAllText(filePath);
                JsonNode? rootNode = JsonNode.Parse(jsonContent);
                if (rootNode == null)
                {
                    throw new InvalidOperationException($"Unable to parse '{fileName}'");
                }

                schemaNodes[fileName] = rootNode;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to load schema file '{FileName}' from '{BasePath}'",
                    fileName,
                    basePath
                );
            }
        }

        return schemaNodes;
    });

    public JsonNode CoreApiSchemaRootNode =>
        _apiSchemaNodes.Value.TryGetValue("ApiSchema.json", out var node) ? node : new JsonArray();

    public JsonNode[] ExtensionApiSchemaRootNodes =>
        _apiSchemaNodes.Value.TryGetValue("ApiSchema.Extension.json", out var node) ? [node] : [];
}
