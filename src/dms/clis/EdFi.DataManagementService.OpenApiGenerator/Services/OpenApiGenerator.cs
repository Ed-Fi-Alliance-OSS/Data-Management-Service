// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.OpenApi;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.OpenApiGenerator.Services;

public class OpenApiGenerator(ILogger<OpenApiGenerator> logger)
{
    private readonly ILogger<OpenApiGenerator> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public void Generate(string coreSchemaPath, string? extensionSchemaPath, string outputPath)
    {
        _logger.LogInformation("Starting OpenAPI generation...");

        if (
            string.IsNullOrWhiteSpace(coreSchemaPath)
            || string.IsNullOrWhiteSpace(extensionSchemaPath)
            || string.IsNullOrWhiteSpace(outputPath)
        )
        {
            _logger.LogError("Invalid input paths. Ensure all paths are provided.");
            throw new ArgumentException("Core schema, extension schema, and output paths are required.");
        }

        _logger.LogDebug("Loading core schema from: {CoreSchemaPath}", coreSchemaPath);
        var coreSchema =
            JsonNode.Parse(File.ReadAllText(coreSchemaPath))
            ?? throw new InvalidOperationException("Invalid core schema file.");

        _logger.LogDebug("Loading extension schema from: {ExtensionSchemaPath}", extensionSchemaPath);

        string content = File.ReadAllText(extensionSchemaPath);
        var parsedNode =
            JsonNode.Parse(content) ?? throw new InvalidOperationException("Invalid extension schema file.");

        var extensionSchema = new[] { parsedNode };

        _logger.LogDebug("Combining core and extension schemas.");
        OpenApiDocument openApiDocument = new(_logger);

        var combinedSchema = openApiDocument.CreateDocument(coreSchema, extensionSchema);

        _logger.LogDebug("Writing combined schema to: {OutputPath}", outputPath);
        File.WriteAllText(outputPath, combinedSchema.ToJsonString());

        _logger.LogInformation("OpenAPI generation completed successfully.");
    }
}
