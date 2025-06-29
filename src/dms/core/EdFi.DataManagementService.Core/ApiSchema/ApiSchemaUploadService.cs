// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Core.ApiSchema;

/// <summary>
/// Service for handling API schema upload operations
/// </summary>
internal interface IUploadApiSchemaService
{
    /// <summary>
    /// Upload API schemas from the provided request
    /// </summary>
    Task<UploadSchemaResponse> UploadApiSchemaAsync(UploadSchemaRequest request);
}

/// <summary>
/// Implementation of API schema upload service
/// </summary>
internal class UploadApiSchemaService(
    IApiSchemaProvider apiSchemaProvider,
    ILogger<UploadApiSchemaService> logger,
    IOptions<AppSettings> appSettings
) : IUploadApiSchemaService
{
    /// <summary>
    /// Upload API schemas from the provided request
    /// </summary>
    public async Task<UploadSchemaResponse> UploadApiSchemaAsync(UploadSchemaRequest request)
    {
        // Check if management endpoints are enabled
        if (!appSettings.Value.EnableManagementEndpoints)
        {
            logger.LogWarning("ApiSchema upload requested but management endpoints are disabled");
            return new UploadSchemaResponse(
                Success: false,
                ErrorMessage: "Management endpoints are disabled",
                SchemasProcessed: 0
            );
        }

        try
        {
            logger.LogInformation("Processing ApiSchema upload request");

            // Validate request
            if (string.IsNullOrWhiteSpace(request.CoreSchema))
            {
                return new UploadSchemaResponse(
                    Success: false,
                    ErrorMessage: "Core ApiSchema is required",
                    SchemasProcessed: 0,
                    IsValidationError: true
                );
            }

            // Parse and validate schemas
            JsonNode coreSchemaNode;
            try
            {
                coreSchemaNode =
                    JsonNode.Parse(request.CoreSchema)
                    ?? throw new InvalidOperationException("Core ApiSchema parsed to null");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to parse core ApiSchema JSON");
                return new UploadSchemaResponse(
                    Success: false,
                    ErrorMessage: "Invalid core ApiSchema JSON",
                    SchemasProcessed: 0,
                    IsValidationError: true
                );
            }

            List<JsonNode> extensionSchemaNodes = [];
            if (request.ExtensionSchemas != null)
            {
                for (int i = 0; i < request.ExtensionSchemas.Length; i++)
                {
                    try
                    {
                        var node =
                            JsonNode.Parse(request.ExtensionSchemas[i])
                            ?? throw new InvalidOperationException($"Extension ApiSchema {i} parsed to null");
                        extensionSchemaNodes.Add(node);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Failed to parse extension ApiSchema {Index} JSON", i);
                        return new UploadSchemaResponse(
                            Success: false,
                            ErrorMessage: $"Invalid extension ApiSchema JSON at index {i}",
                            SchemasProcessed: 0,
                            IsValidationError: true
                        );
                    }
                }
            }

            // Upload to schema service
            var (success, failures) = await apiSchemaProvider.LoadApiSchemaFromAsync(
                coreSchemaNode,
                extensionSchemaNodes.ToArray()
            );

            if (success)
            {
                UploadSchemaResponse successResponse = new(
                    Success: true,
                    ReloadId: apiSchemaProvider.ReloadId,
                    SchemasProcessed: 1 + extensionSchemaNodes.Count
                );
                logger.LogInformation(
                    "ApiSchema upload completed successfully. Processed {Count} schemas",
                    successResponse.SchemasProcessed
                );
                return successResponse;
            }
            else
            {
                // Convert failures to appropriate error message
                var errorMessage = failures.FirstOrDefault()?.Message ?? "Failed to upload ApiSchema";

                // Check for specific failure types
                var isValidationError = failures.Exists(f => f.FailureType == "Validation");

                return new UploadSchemaResponse(
                    Success: false,
                    ErrorMessage: errorMessage,
                    SchemasProcessed: 0,
                    IsValidationError: isValidationError,
                    Failures: failures
                );
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to upload ApiSchema");
            return new UploadSchemaResponse(
                Success: false,
                ErrorMessage: "Internal error during ApiSchema upload",
                SchemasProcessed: 0
            );
        }
    }
}
