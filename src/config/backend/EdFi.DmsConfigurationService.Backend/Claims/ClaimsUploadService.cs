// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend.Claims.Models;
using EdFi.DmsConfigurationService.Backend.ClaimsDataLoader;
using Microsoft.Extensions.Logging;

namespace EdFi.DmsConfigurationService.Backend.Claims;

/// <summary>
/// Service for handling claims uploads and validations
/// </summary>
public class ClaimsUploadService(
    ILogger<ClaimsUploadService> logger,
    IClaimsProvider claimsProvider,
    IClaimsDataLoader claimsDataLoader,
    IClaimsValidator claimsValidator
) : IClaimsUploadService
{
    /// <summary>
    /// Uploads and validates claims from JSON content, then persists them to the database.
    /// This operation first validates the claims structure, then performs an atomic update
    /// to replace existing non-system-reserved claim sets and the claims hierarchy.
    /// The provider's in-memory state is only updated after successful database persistence.
    /// </summary>
    /// <param name="claimsJson">The claims JSON to upload containing claimSets and claimsHierarchy</param>
    /// <returns>Status of the upload operation including any validation or database errors</returns>
    public async Task<ClaimsLoadStatus> UploadClaimsAsync(JsonNode claimsJson)
    {
        logger.LogInformation("Starting claims upload operation");

        try
        {
            // Validate that the JSON is not null
            if (claimsJson == null)
            {
                ClaimsFailure failure = new("Validation", "Claims JSON cannot be null");
                logger.LogWarning(failure.Message);
                return new(false, [failure]);
            }

            // Parse and validate the JSON structure
            logger.LogDebug("Parsing claims JSON structure");

            // Extract claim sets and hierarchy nodes
            JsonNode? claimSetsNode = claimsJson["claimSets"];
            JsonNode? claimsHierarchyNode = claimsJson["claimsHierarchy"];

            if (claimSetsNode == null || claimsHierarchyNode == null)
            {
                List<ClaimsFailure> failures = [];
                if (claimSetsNode == null)
                {
                    failures.Add(new("Structure", "Missing required 'claimSets' property"));
                }
                if (claimsHierarchyNode == null)
                {
                    failures.Add(new("Structure", "Missing required 'claimsHierarchy' property"));
                }
                return new(false, failures);
            }

            // Create ClaimsDocumentNodes
            ClaimsDocument claimsNodes = new(claimSetsNode, claimsHierarchyNode);

            // Validate using ClaimsValidator
            List<ClaimsValidationFailure> validationFailures = claimsValidator.Validate(claimsJson);
            if (validationFailures.Count > 0)
            {
                logger.LogError(
                    "Claims validation failed with {FailureCount} failures",
                    validationFailures.Count
                );

                List<ClaimsFailure> failures = validationFailures
                    .Select(vf => new ClaimsFailure(
                        "Validation",
                        string.Join(", ", vf.FailureMessages),
                        vf.FailurePath.Value
                    ))
                    .ToList();

                foreach (ClaimsFailure failure in failures)
                {
                    logger.LogWarning(
                        "Claims validation failure [{FailureType}]: {Message} at {Path}",
                        failure.FailureType,
                        failure.Message,
                        failure.Path ?? "N/A"
                    );
                }

                return new(false, failures);
            }

            // Update database with new claims
            ClaimsDataLoadResult updateResult = await claimsDataLoader.UpdateClaimsAsync(claimsNodes);

            switch (updateResult)
            {
                case ClaimsDataLoadResult.Success:
                    // Database update succeeded - update provider's in-memory state
                    Guid newReloadId = Guid.NewGuid();
                    claimsProvider.UpdateInMemoryState(claimsNodes, newReloadId);

                    logger.LogInformation(
                        "Claims uploaded and persisted successfully. Reload ID: {ReloadId}",
                        newReloadId
                    );
                    return new(true, []);

                case ClaimsDataLoadResult.ValidationFailure validationFailure:
                    List<ClaimsFailure> failures = validationFailure
                        .Errors.Select(e => new ClaimsFailure("Validation", e))
                        .ToList();
                    return new(false, failures);

                case ClaimsDataLoadResult.DatabaseFailure databaseFailure:
                    return new(false, [new ClaimsFailure("Database", databaseFailure.ErrorMessage)]);

                case ClaimsDataLoadResult.UnexpectedFailure unexpectedFailure:
                    return new(
                        false,
                        [new("Unexpected", unexpectedFailure.ErrorMessage, null, unexpectedFailure.Exception)]
                    );

                default:
                    return new(
                        false,
                        [new("Unknown", $"Unexpected result type: {updateResult.GetType().Name}")]
                    );
            }
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Invalid JSON format in uploaded claims");

            ClaimsFailure failure = new(
                "JsonError",
                "Invalid JSON format in uploaded claims document",
                null,
                ex
            );

            return new(false, [failure]);
        }
        catch (ArgumentException ex)
        {
            logger.LogError(ex, "Invalid arguments provided for claims upload");

            ClaimsFailure failure = new("ArgumentError", "Invalid claims data provided for upload", null, ex);

            return new(false, [failure]);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogError(ex, "Invalid operation during claims upload");

            ClaimsFailure failure = new(
                "OperationError",
                "Invalid operation occurred during claims upload",
                null,
                ex
            );

            return new(false, [failure]);
        }
    }

    /// <summary>
    /// Reloads claims from the configured source and persists them to the database.
    /// This operation is atomic - either all changes are saved or none are.
    /// The provider's in-memory state is only updated after successful database persistence.
    /// </summary>
    /// <returns>Status indicating success or failure of the reload operation</returns>
    public async Task<ClaimsLoadStatus> ReloadClaimsAsync()
    {
        logger.LogInformation("Reloading claims from configured source...");

        try
        {
            // Load claims from configured source
            ClaimsLoadResult loadResult = claimsProvider.LoadClaimsFromSource();

            if (loadResult.Failures.Count > 0)
            {
                logger.LogError(
                    "Failed to load claims from source with {FailureCount} failures",
                    loadResult.Failures.Count
                );
                return new(false, loadResult.Failures);
            }

            if (loadResult.Nodes == null)
            {
                ClaimsFailure failure = new("Configuration", "Claims loading returned null without failures");
                logger.LogError(failure.Message);
                return new(false, [failure]);
            }

            // Validate claims
            string claimSetsJson = loadResult.Nodes.ClaimSetsNode.ToJsonString();
            string hierarchyJson = loadResult.Nodes.ClaimsHierarchyNode.ToJsonString();

            JsonObject combinedClaims = new()
            {
                ["claimSets"] = JsonNode.Parse(claimSetsJson),
                ["claimsHierarchy"] = JsonNode.Parse(hierarchyJson),
            };

            List<ClaimsValidationFailure> validationFailures = claimsValidator.Validate(combinedClaims);
            if (validationFailures.Count > 0)
            {
                logger.LogError(
                    "Claims validation failed with {FailureCount} failures",
                    validationFailures.Count
                );

                List<ClaimsFailure> failures = validationFailures
                    .Select(vf => new ClaimsFailure(
                        "Validation",
                        string.Join(", ", vf.FailureMessages),
                        vf.FailurePath.Value
                    ))
                    .ToList();

                return new(false, failures);
            }

            // Update database with new claims
            ClaimsDataLoadResult updateResult = await claimsDataLoader.UpdateClaimsAsync(loadResult.Nodes);

            switch (updateResult)
            {
                case ClaimsDataLoadResult.Success:
                    // Database update succeeded - update provider's in-memory state
                    Guid newReloadId = Guid.NewGuid();
                    claimsProvider.UpdateInMemoryState(loadResult.Nodes, newReloadId);

                    logger.LogInformation(
                        "Claims reloaded successfully. New reload ID: {ReloadId}",
                        newReloadId
                    );
                    return new(true, []);

                case ClaimsDataLoadResult.ValidationFailure validationFailure:
                    List<ClaimsFailure> dbValidationFailures = validationFailure
                        .Errors.Select(e => new ClaimsFailure("Validation", e))
                        .ToList();
                    return new(false, dbValidationFailures);

                case ClaimsDataLoadResult.DatabaseFailure databaseFailure:
                    return new(false, [new("Database", databaseFailure.ErrorMessage)]);

                case ClaimsDataLoadResult.UnexpectedFailure unexpectedFailure:
                    return new(
                        false,
                        [new("Unexpected", unexpectedFailure.ErrorMessage, null, unexpectedFailure.Exception)]
                    );

                default:
                    return new(
                        false,
                        [new("Unknown", $"Unexpected result type: {updateResult.GetType().Name}")]
                    );
            }
        }
        catch (IOException ex)
        {
            logger.LogError(ex, "File I/O error during claims reload");

            ClaimsFailure failure = new("IOError", "Failed to read claims files during reload", null, ex);

            return new(false, [failure]);
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "JSON parsing error during claims reload");

            ClaimsFailure failure = new(
                "JsonError",
                "Invalid JSON format encountered during claims reload",
                null,
                ex
            );

            return new(false, [failure]);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogError(ex, "Invalid operation during claims reload");

            ClaimsFailure failure = new(
                "OperationError",
                "Invalid operation occurred during claims reload",
                null,
                ex
            );

            return new(false, [failure]);
        }
    }
}
