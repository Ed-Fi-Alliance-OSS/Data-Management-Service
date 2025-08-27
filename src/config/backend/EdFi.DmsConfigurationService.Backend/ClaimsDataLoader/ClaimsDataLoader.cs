// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend.Claims;
using EdFi.DmsConfigurationService.Backend.Claims.Models;
using EdFi.DmsConfigurationService.Backend.Models.ClaimsHierarchy;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Model.ClaimSets;
using Microsoft.Extensions.Logging;

namespace EdFi.DmsConfigurationService.Backend.ClaimsDataLoader;

internal class DatabaseOperationException(string message) : Exception(message);

/// <summary>
/// Service responsible for loading and updating claims data in the database from JSON sources.
/// Handles both initial claims loading when tables are empty and atomic updates of existing claims data.
/// </summary>
public class ClaimsDataLoader(
    IClaimsProvider claimsProvider,
    IClaimSetRepository claimSetRepository,
    IClaimsHierarchyRepository claimsHierarchyRepository,
    IClaimsTableValidator claimsTableValidator,
    IClaimsDocumentRepository claimsDocumentRepository,
    ILogger<ClaimsDataLoader> logger
) : IClaimsDataLoader
{
    private readonly JsonSerializerOptions _jsonSerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Checks if both ClaimSet and ClaimsHierarchy tables are empty.
    /// </summary>
    /// <returns>True if both tables contain no records, false otherwise.</returns>
    public async Task<bool> AreClaimsTablesEmptyAsync()
    {
        return await claimsTableValidator.AreClaimsTablesEmptyAsync();
    }

    /// <summary>
    /// Loads initial claims data into empty database tables from the configured claims provider.
    /// Only performs the load if tables are completely empty to avoid overwriting existing data.
    /// </summary>
    /// <returns>Result indicating success with loaded counts or failure with error details.</returns>
    public async Task<ClaimsDataLoadResult> LoadInitialClaimsAsync()
    {
        try
        {
            // Check if claims tables are already populated
            bool areTablesEmpty = await claimsTableValidator.AreClaimsTablesEmptyAsync();
            if (!areTablesEmpty)
            {
                logger.LogInformation("Claims tables are already populated, returning AlreadyLoaded");
                return new ClaimsDataLoadResult.AlreadyLoaded();
            }

            logger.LogInformation("Loading initial claims data from Claims.json");

            ClaimsDocument claimsNodes;
            try
            {
                // Get claims data from provider
                claimsNodes = claimsProvider.GetClaimsDocumentNodes();
                if (claimsNodes == null)
                {
                    return new ClaimsDataLoadResult.ValidationFailure(
                        ["Failed to load claims document from provider"]
                    );
                }
            }
            catch (InvalidOperationException ex)
                when (ex.Message.Contains("Claims loading failed")
                    || ex.Message.Contains("Claims validation failed")
                )
            {
                // Check if it's a validation failure
                if (!claimsProvider.IsClaimsValid)
                {
                    List<string> errors = claimsProvider
                        .ClaimsFailures.Select(f => $"{f.FailureType}: {f.Message}")
                        .ToList();
                    return new ClaimsDataLoadResult.ValidationFailure(errors);
                }

                // Otherwise, it's some other failure
                return new ClaimsDataLoadResult.ValidationFailure([ex.Message]);
            }

            // Load claim sets
            int claimSetsLoaded = await LoadClaimSetsAsync(claimsNodes.ClaimSetsNode);

            // Load claims hierarchy
            bool hierarchyLoaded = await LoadClaimsHierarchyAsync(claimsNodes.ClaimsHierarchyNode);

            logger.LogInformation(
                "Successfully loaded {ClaimSetCount} claim sets and hierarchy data",
                claimSetsLoaded
            );

            return new ClaimsDataLoadResult.Success(claimSetsLoaded, hierarchyLoaded);
        }
        catch (DatabaseOperationException ex)
        {
            logger.LogError(ex, "Database error during claims data loading");
            return new ClaimsDataLoadResult.DatabaseFailure(ex.Message);
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "JSON format error during claims data loading");
            return new ClaimsDataLoadResult.UnexpectedFailure("Invalid JSON format in claims data", ex);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogError(ex, "Invalid operation during claims data loading");
            return new ClaimsDataLoadResult.UnexpectedFailure(
                "Invalid operation during claims data loading",
                ex
            );
        }
    }

    /// <summary>
    /// Loads claim sets from JSON data into the ClaimSet table.
    /// </summary>
    /// <param name="claimSetsNode">JSON array containing claim set definitions.</param>
    /// <returns>The number of claim sets successfully loaded.</returns>
    private async Task<int> LoadClaimSetsAsync(JsonNode? claimSetsNode)
    {
        if (claimSetsNode is not JsonArray claimSetsArray)
        {
            logger.LogWarning("No claim sets found in Claims.json or invalid format");
            return 0;
        }

        int count = 0;
        foreach (JsonNode? claimSetNode in claimSetsArray)
        {
            if (claimSetNode == null)
            {
                continue;
            }

            string? claimSetName = claimSetNode["claimSetName"]?.GetValue<string>();
            bool isSystemReserved = claimSetNode["isSystemReserved"]?.GetValue<bool>() ?? false;

            if (string.IsNullOrEmpty(claimSetName))
            {
                logger.LogWarning("Skipping claim set with no name");
                continue;
            }

            ClaimSetInsertCommand command = new()
            {
                Name = claimSetName,
                IsSystemReserved = isSystemReserved,
            };

            ClaimSetInsertResult result = await claimSetRepository.InsertClaimSet(command);

            switch (result)
            {
                case ClaimSetInsertResult.Success:
                    count++;
                    logger.LogDebug("Loaded claim set: {ClaimSetName}", claimSetName);
                    break;
                case ClaimSetInsertResult.FailureDuplicateClaimSetName:
                    logger.LogWarning("Claim set already exists: {ClaimSetName}", claimSetName);
                    break;
                case ClaimSetInsertResult.FailureUnknown failure:
                    logger.LogError(
                        "Failed to load claim set {ClaimSetName}: {Error}",
                        claimSetName,
                        failure.FailureMessage
                    );
                    throw new DatabaseOperationException(
                        $"Failed to load claim set {claimSetName}: {failure.FailureMessage}"
                    );
            }
        }

        return count;
    }

    /// <summary>
    /// Loads the claims hierarchy from JSON data into the ClaimsHierarchy table.
    /// </summary>
    /// <param name="hierarchyNode">JSON array containing the hierarchical claims structure.</param>
    /// <returns>True if hierarchy was successfully loaded, false otherwise.</returns>
    private async Task<bool> LoadClaimsHierarchyAsync(JsonNode? hierarchyNode)
    {
        if (hierarchyNode is not JsonArray hierarchyArray)
        {
            logger.LogWarning("No claims hierarchy found in Claims.json or invalid format");
            return false;
        }

        try
        {
            // Convert JsonNode to List<Claim> as expected by repository
            string hierarchyJson = hierarchyArray.ToJsonString();
            List<Claim>? claims = JsonSerializer.Deserialize<List<Claim>>(
                hierarchyJson,
                _jsonSerializerOptions
            );

            if (claims == null)
            {
                logger.LogWarning("Failed to deserialize claims hierarchy");
                return false;
            }

            // Use a default date for initial load
            ClaimsHierarchySaveResult result = await claimsHierarchyRepository.SaveClaimsHierarchy(
                claims,
                DateTime.MinValue
            );

            switch (result)
            {
                case ClaimsHierarchySaveResult.Success:
                    logger.LogDebug(
                        "Successfully loaded claims hierarchy with {Count} root claims",
                        claims.Count
                    );
                    return true;
                case ClaimsHierarchySaveResult.FailureUnknown failure:
                    logger.LogError("Failed to load claims hierarchy: {Error}", failure.FailureMessage);
                    throw new DatabaseOperationException(
                        $"Failed to load claims hierarchy: {failure.FailureMessage}"
                    );
                default:
                    logger.LogError(
                        "Unexpected result when loading claims hierarchy: {Result}",
                        result.GetType().Name
                    );
                    throw new InvalidOperationException(
                        $"Unexpected result when loading claims hierarchy: {result.GetType().Name}"
                    );
            }
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Failed to parse claims hierarchy JSON");
            throw new InvalidOperationException("Failed to parse claims hierarchy JSON", ex);
        }
    }

    /// <summary>
    /// Updates existing claims data in the database by replacing all non-system-reserved claim sets
    /// and the entire claims hierarchy in a single atomic transaction.
    /// </summary>
    /// <param name="claimsNodes">The new claims document containing claim sets and hierarchy to load</param>
    /// <returns>Result indicating success or failure of the update operation</returns>
    public async Task<ClaimsDataLoadResult> UpdateClaimsAsync(ClaimsDocument claimsNodes)
    {
        try
        {
            logger.LogInformation("Updating claims data in database");

            ClaimsDocumentUpdateResult result = await claimsDocumentRepository.ReplaceClaimsDocument(
                claimsNodes
            );

            return result switch
            {
                ClaimsDocumentUpdateResult.Success success => new ClaimsDataLoadResult.Success(
                    success.ClaimSetsInserted,
                    success.HierarchyUpdated
                ),

                ClaimsDocumentUpdateResult.ValidationFailure validation =>
                    new ClaimsDataLoadResult.ValidationFailure(validation.Errors),

                ClaimsDocumentUpdateResult.DatabaseFailure database =>
                    new ClaimsDataLoadResult.DatabaseFailure(database.ErrorMessage),

                ClaimsDocumentUpdateResult.MultiUserConflict => new ClaimsDataLoadResult.DatabaseFailure(
                    "Multi-user conflict detected while updating claims"
                ),

                ClaimsDocumentUpdateResult.UnexpectedFailure unexpected =>
                    new ClaimsDataLoadResult.UnexpectedFailure(unexpected.ErrorMessage, unexpected.Exception),

                _ => new ClaimsDataLoadResult.UnexpectedFailure(
                    $"Unexpected result type: {result.GetType().Name}"
                ),
            };
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "JSON format error during claims update");
            return new ClaimsDataLoadResult.UnexpectedFailure("Invalid JSON format during claims update", ex);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogError(ex, "Invalid operation during claims update");
            return new ClaimsDataLoadResult.UnexpectedFailure("Invalid operation during claims update", ex);
        }
    }
}
