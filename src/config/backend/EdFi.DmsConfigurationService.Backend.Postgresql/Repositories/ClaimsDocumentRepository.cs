// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using EdFi.DmsConfigurationService.Backend.Claims.Models;
using EdFi.DmsConfigurationService.Backend.Models.ClaimsHierarchy;
using EdFi.DmsConfigurationService.Backend.Repositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Repositories;

/// <summary>
/// Repository for managing claims document data across two database tables: ClaimSet and ClaimsHierarchy.
/// Handles transactional operations to ensure consistency between claim sets definitions and their hierarchical relationships.
/// </summary>
public class ClaimsDocumentRepository(
    IOptions<DatabaseOptions> databaseOptions,
    ILogger<ClaimsDocumentRepository> logger
) : IClaimsDocumentRepository
{
    private static readonly JsonSerializerOptions _jsonSerializerOptions = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Replaces the entire claims document including claim sets and claims hierarchy in a transactional manner.
    /// </summary>
    /// <param name="claimsNodes">The claims document containing claim sets and hierarchy to replace existing data with.</param>
    /// <returns>Result indicating success with counts of deleted/inserted items or failure with error details.</returns>
    public async Task<ClaimsDocumentUpdateResult> ReplaceClaimsDocument(ClaimsDocument claimsNodes)
    {
        await using NpgsqlConnection connection = new NpgsqlConnection(
            databaseOptions.Value.DatabaseConnection
        );
        await connection.OpenAsync();
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();

        try
        {
            // Step 1: Get current claims hierarchy to maintain lastModifiedDate tracking
            ClaimsHierarchyGetResult hierarchyResult = await GetClaimsHierarchy(connection, transaction);
            if (hierarchyResult is not ClaimsHierarchyGetResult.Success hierarchySuccess)
            {
                return new ClaimsDocumentUpdateResult.DatabaseFailure(
                    "Failed to retrieve current claims hierarchy"
                );
            }

            DateTime existingLastModifiedDate = hierarchySuccess.LastModifiedDate;
            long existingHierarchyId = hierarchySuccess.Id;

            // Step 2: Delete all non-system-reserved claim sets
            int deletedCount = await DeleteNonSystemReservedClaimSets(connection, transaction);

            // Step 3: Insert new claim sets
            int insertedCount = await InsertClaimSets(claimsNodes.ClaimSetsNode, connection, transaction);

            // Step 4: Update claims hierarchy
            bool hierarchyUpdated = await UpdateClaimsHierarchy(
                claimsNodes.ClaimsHierarchyNode,
                existingHierarchyId,
                existingLastModifiedDate,
                connection,
                transaction
            );

            if (!hierarchyUpdated)
            {
                await transaction.RollbackAsync();
                return new ClaimsDocumentUpdateResult.DatabaseFailure("Failed to update claims hierarchy");
            }

            // Step 5: Commit transaction
            await transaction.CommitAsync();

            logger.LogInformation(
                "Successfully updated claims document. Deleted {DeletedCount} claim sets, inserted {InsertedCount} new claim sets",
                deletedCount,
                insertedCount
            );

            return new ClaimsDocumentUpdateResult.Success(deletedCount, insertedCount, true);
        }
        catch (PostgresException ex) when (ex.SqlState == "23503") // Foreign key violation
        {
            await transaction.RollbackAsync();
            logger.LogError(ex, "Foreign key constraint violation during claims update");
            return new ClaimsDocumentUpdateResult.DatabaseFailure(
                "Cannot delete claim sets that are in use by applications. Remove application associations first."
            );
        }
        catch (JsonException ex)
        {
            await transaction.RollbackAsync();
            logger.LogError(ex, "JSON format error during claims document update");
            return new ClaimsDocumentUpdateResult.UnexpectedFailure(
                "Invalid JSON format in claims document",
                ex
            );
        }
        catch (NpgsqlException ex)
        {
            await transaction.RollbackAsync();
            logger.LogError(ex, "Database connection error during claims document update");
            return new ClaimsDocumentUpdateResult.DatabaseFailure("Database connection error: " + ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            await transaction.RollbackAsync();
            logger.LogError(ex, "Invalid operation during claims document update");
            return new ClaimsDocumentUpdateResult.UnexpectedFailure(
                "Invalid operation during claims update",
                ex
            );
        }
    }

    /// <summary>
    /// Retrieves the current claims hierarchy from the database for tracking modifications.
    /// </summary>
    /// <param name="connection">The database connection to use.</param>
    /// <param name="transaction">The active transaction.</param>
    /// <returns>Result containing the hierarchy data including ID and last modified date, or failure details.</returns>
    private async Task<ClaimsHierarchyGetResult> GetClaimsHierarchy(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction
    )
    {
        try
        {
            const string Sql = "SELECT Id, Hierarchy, LastModifiedDate FROM dmscs.ClaimsHierarchy";

            List<(long id, string hierarchyJson, DateTime lastModifiedDate)> hierarchyTuples = (
                await connection.QueryAsync<(long id, string hierarchyJson, DateTime lastModifiedDate)>(
                    Sql,
                    transaction: transaction
                )
            ).ToList();

            if (hierarchyTuples.Count == 0)
            {
                return new ClaimsHierarchyGetResult.FailureHierarchyNotFound();
            }

            if (hierarchyTuples.Count > 1)
            {
                return new ClaimsHierarchyGetResult.FailureMultipleHierarchiesFound();
            }

            List<Claim>? hierarchy = JsonSerializer.Deserialize<List<Claim>>(
                hierarchyTuples[0].hierarchyJson
            );

            if (hierarchy == null)
            {
                return new ClaimsHierarchyGetResult.FailureUnknown(
                    "Unable to deserialize claim set hierarchy"
                );
            }

            return new ClaimsHierarchyGetResult.Success(
                hierarchy,
                hierarchyTuples[0].lastModifiedDate,
                hierarchyTuples[0].id
            );
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "JSON deserialization error in claims hierarchy");
            return new ClaimsHierarchyGetResult.FailureUnknown(
                "Invalid JSON format in claims hierarchy: " + ex.Message
            );
        }
        catch (NpgsqlException ex)
        {
            logger.LogError(ex, "Database error getting claims hierarchy");
            return new ClaimsHierarchyGetResult.FailureUnknown("Database error: " + ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogError(ex, "Invalid operation getting claims hierarchy");
            return new ClaimsHierarchyGetResult.FailureUnknown("Invalid operation: " + ex.Message);
        }
    }

    /// <summary>
    /// Deletes all claim sets that are not marked as system-reserved from the database.
    /// </summary>
    /// <param name="connection">The database connection to use.</param>
    /// <param name="transaction">The active transaction.</param>
    /// <returns>The count of deleted claim sets.</returns>
    private async Task<int> DeleteNonSystemReservedClaimSets(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction
    )
    {
        const string Sql =
            @"
            DELETE FROM dmscs.ClaimSet
            WHERE IsSystemReserved = false
            RETURNING Id";

        IEnumerable<long> deletedIds = await connection.QueryAsync<long>(Sql, transaction: transaction);
        int deletedCount = deletedIds.Count();

        logger.LogDebug("Deleted {Count} non-system-reserved claim sets", deletedCount);
        return deletedCount;
    }

    /// <summary>
    /// Inserts new claim sets from JSON data into the database, skipping existing ones.
    /// </summary>
    /// <param name="claimSetsNode">JSON node containing the array of claim sets to insert.</param>
    /// <param name="connection">The database connection to use.</param>
    /// <param name="transaction">The active transaction.</param>
    /// <returns>The count of successfully inserted claim sets.</returns>
    private async Task<int> InsertClaimSets(
        JsonNode? claimSetsNode,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction
    )
    {
        if (claimSetsNode is not JsonArray claimSetsArray)
        {
            logger.LogWarning("No claim sets found or invalid format");
            return 0;
        }

        const string InsertSql =
            @"
            INSERT INTO dmscs.ClaimSet (ClaimSetName, IsSystemReserved)
            VALUES (@ClaimSetName, @IsSystemReserved)
            ON CONFLICT (ClaimSetName) DO NOTHING
            RETURNING Id";

        int insertedCount = 0;

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

            long? insertedId = await connection.QuerySingleOrDefaultAsync<long?>(
                InsertSql,
                new { ClaimSetName = claimSetName, IsSystemReserved = isSystemReserved },
                transaction: transaction
            );

            if (insertedId.HasValue)
            {
                insertedCount++;
                logger.LogDebug("Inserted claim set: {ClaimSetName}", claimSetName);
            }
            else
            {
                logger.LogDebug("Claim set already exists: {ClaimSetName}", claimSetName);
            }
        }

        return insertedCount;
    }

    /// <summary>
    /// Updates the claims hierarchy with new data using optimistic concurrency control.
    /// </summary>
    /// <param name="hierarchyNode">JSON node containing the new hierarchy structure.</param>
    /// <param name="existingHierarchyId">The ID of the existing hierarchy record to update.</param>
    /// <param name="existingLastModifiedDate">The last modified date for optimistic concurrency check.</param>
    /// <param name="connection">The database connection to use.</param>
    /// <param name="transaction">The active transaction.</param>
    /// <returns>True if update succeeded, false if concurrency conflict or validation error occurred.</returns>
    private async Task<bool> UpdateClaimsHierarchy(
        JsonNode? hierarchyNode,
        long existingHierarchyId,
        DateTime existingLastModifiedDate,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction
    )
    {
        if (hierarchyNode is not JsonArray hierarchyArray)
        {
            logger.LogWarning("No claims hierarchy found or invalid format");
            return false;
        }

        try
        {
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

            string newHierarchyJson = JsonSerializer.Serialize(claims);

            // Since we already retrieved the hierarchy at the beginning of the transaction,
            // we know it exists. Use optimistic concurrency control with ID and timestamp.
            const string UpdateSql =
                @"
                UPDATE dmscs.ClaimsHierarchy
                SET hierarchy = @Hierarchy::jsonb,
                    lastmodifieddate = now()
                WHERE id = @Id AND lastmodifieddate = @LastModifiedDate";

            int affectedRows = await connection.ExecuteAsync(
                UpdateSql,
                new
                {
                    Hierarchy = newHierarchyJson,
                    Id = existingHierarchyId,
                    LastModifiedDate = existingLastModifiedDate,
                },
                transaction
            );

            if (affectedRows == 0)
            {
                logger.LogError(
                    "Optimistic concurrency conflict detected: ClaimsHierarchy was modified by another transaction. "
                        + "Expected LastModifiedDate: {ExpectedDate}, HierarchyId: {HierarchyId}",
                    existingLastModifiedDate,
                    existingHierarchyId
                );
                return false;
            }

            logger.LogDebug("Successfully updated claims hierarchy with {Count} root claims", claims.Count);
            return true;
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "JSON serialization error updating claims hierarchy");
            throw new InvalidOperationException("Invalid JSON format in claims hierarchy update", ex);
        }
        catch (NpgsqlException ex)
        {
            logger.LogError(ex, "Database error updating claims hierarchy");
            throw new InvalidOperationException("Database error updating claims hierarchy", ex);
        }
    }
}
