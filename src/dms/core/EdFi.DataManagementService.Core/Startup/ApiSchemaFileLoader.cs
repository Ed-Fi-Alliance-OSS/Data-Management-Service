// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Startup;

/// <summary>
/// Loads and normalizes ApiSchema.json files from explicit file paths.
/// This is the library-first entry point for CLI and test harness usage.
/// </summary>
public class ApiSchemaFileLoader(
    IApiSchemaInputNormalizer inputNormalizer,
    ILogger<ApiSchemaFileLoader> logger
) : IApiSchemaFileLoader
{
    /// <inheritdoc />
    public ApiSchemaFileLoadResult Load(string coreSchemaPath, IReadOnlyList<string> extensionSchemaPaths)
    {
        logger.LogDebug(
            "Loading API schemas: core={CorePath}, extensions={ExtensionCount}",
            LoggingSanitizer.SanitizeForLogging(coreSchemaPath),
            extensionSchemaPaths.Count
        );

        // Load core schema
        var coreResult = LoadJsonFile(coreSchemaPath);
        if (coreResult.Error != null)
        {
            return coreResult.Error;
        }

        // Load extension schemas
        var extensionNodes = new List<JsonNode>();
        foreach (var extPath in extensionSchemaPaths)
        {
            var extResult = LoadJsonFile(extPath);
            if (extResult.Error != null)
            {
                return extResult.Error;
            }
            extensionNodes.Add(extResult.Node!);
        }

        // Create document nodes and normalize
        var rawNodes = new ApiSchemaDocumentNodes(coreResult.Node!, extensionNodes.ToArray());

        logger.LogDebug("Normalizing loaded schemas");
        var normalizationResult = inputNormalizer.Normalize(rawNodes);

        return normalizationResult switch
        {
            ApiSchemaNormalizationResult.SuccessResult success => new ApiSchemaFileLoadResult.SuccessResult(
                success.NormalizedNodes
            ),
            _ => new ApiSchemaFileLoadResult.NormalizationFailureResult(normalizationResult),
        };
    }

    /// <summary>
    /// Loads and parses a JSON file from the specified path.
    /// </summary>
    private (JsonNode? Node, ApiSchemaFileLoadResult? Error) LoadJsonFile(string filePath)
    {
        if (!File.Exists(filePath))
        {
            logger.LogError(
                "Schema file not found: {FilePath}",
                LoggingSanitizer.SanitizeForLogging(filePath)
            );
            return (null, new ApiSchemaFileLoadResult.FileNotFoundResult(filePath));
        }

        string jsonContent;
        try
        {
            jsonContent = File.ReadAllText(filePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(
                ex,
                "Failed to read schema file: {FilePath}",
                LoggingSanitizer.SanitizeForLogging(filePath)
            );
            return (null, new ApiSchemaFileLoadResult.FileReadErrorResult(filePath, ex.Message));
        }

        try
        {
            var node = JsonNode.Parse(jsonContent);
            if (node == null)
            {
                logger.LogError(
                    "Schema file contains null JSON: {FilePath}",
                    LoggingSanitizer.SanitizeForLogging(filePath)
                );
                return (null, new ApiSchemaFileLoadResult.InvalidJsonResult(filePath, "JSON parsed to null"));
            }
            return (node, null);
        }
        catch (JsonException ex)
        {
            logger.LogError(
                ex,
                "Schema file contains invalid JSON: {FilePath}",
                LoggingSanitizer.SanitizeForLogging(filePath)
            );
            return (null, new ApiSchemaFileLoadResult.InvalidJsonResult(filePath, ex.Message));
        }
    }
}
