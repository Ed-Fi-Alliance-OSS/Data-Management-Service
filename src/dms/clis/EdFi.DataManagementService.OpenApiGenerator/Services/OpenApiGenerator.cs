// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.OpenApi;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.OpenApiGenerator.Services;

public class OpenApiGenerator(ILogger<OpenApiGenerator> logger)
{
    private readonly ILogger<OpenApiGenerator> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    public string Generate(string coreSchemaPath, string? extensionSchemaPath)
    {
        _logger.LogInformation("Starting OpenAPI generation...");

        if (string.IsNullOrWhiteSpace(coreSchemaPath))
        {
            _logger.LogError("Invalid core schema path.");
            throw new ArgumentException("Core schema path is required.");
        }

        _logger.LogDebug("Loading core schema from: {CoreSchemaPath}", coreSchemaPath);
        var coreSchema =
            JsonNode.Parse(File.ReadAllText(coreSchemaPath))
            ?? throw new InvalidOperationException("Invalid core schema file.");

        JsonNode[] extensionSchema = Array.Empty<JsonNode>();
        if (!string.IsNullOrWhiteSpace(extensionSchemaPath))
        {
            _logger.LogDebug("Loading extension schema from: {ExtensionSchemaPath}", extensionSchemaPath);
            string content = File.ReadAllText(extensionSchemaPath);
            var parsedNode =
                JsonNode.Parse(content)
                ?? throw new InvalidOperationException("Invalid extension schema file.");
            extensionSchema = new[] { parsedNode };
        }

        _logger.LogDebug("Combining core and extension schemas.");
        OpenApiDocument openApiDocument = new(_logger);
        var combinedSchema = openApiDocument.CreateDocument(coreSchema, extensionSchema);

        _logger.LogInformation("OpenAPI generation completed successfully.");
        return combinedSchema.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
