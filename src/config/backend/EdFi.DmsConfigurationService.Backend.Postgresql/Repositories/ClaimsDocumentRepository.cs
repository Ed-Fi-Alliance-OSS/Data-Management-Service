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

public class ClaimsDocumentRepository(
    IOptions<DatabaseOptions> databaseOptions,
    ILogger<ClaimsDocumentRepository> logger
) : IClaimsDocumentRepository
{
    public async Task<ClaimsDocumentUpdateResult> ReplaceClaimsDocument(ClaimsDocument claimsNodes)
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        try
        {
            // Step 1: Get current claims hierarchy to maintain lastModifiedDate tracking
            var hierarchyResult = await GetClaimsHierarchy(connection, transaction);
            if (hierarchyResult is not ClaimsHierarchyGetResult.Success hierarchySuccess)
            {
                return new ClaimsDocumentUpdateResult.DatabaseFailure(
                    "Failed to retrieve current claims hierarchy"
                );
            }

            var existingLastModifiedDate = hierarchySuccess.LastModifiedDate;

            // Step 2: Delete all non-system-reserved claim sets
            var deletedCount = await DeleteNonSystemReservedClaimSets(connection, transaction);

            // Step 3: Insert new claim sets
            var insertedCount = await InsertClaimSets(claimsNodes.ClaimSetsNode, connection, transaction);

            // Step 4: Update claims hierarchy
            var hierarchyUpdated = await UpdateClaimsHierarchy(
                claimsNodes.ClaimsHierarchyNode,
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
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            logger.LogError(ex, "Unexpected error during claims document update");
            return new ClaimsDocumentUpdateResult.UnexpectedFailure(ex.Message, ex);
        }
    }

    private async Task<ClaimsHierarchyGetResult> GetClaimsHierarchy(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction
    )
    {
        try
        {
            const string Sql = "SELECT Hierarchy, LastModifiedDate FROM dmscs.ClaimsHierarchy";

            var hierarchyTuples = (
                await connection.QueryAsync<(string hierarchyJson, DateTime lastModifiedDate)>(
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

            var hierarchy = JsonSerializer.Deserialize<List<Claim>>(hierarchyTuples[0].hierarchyJson);

            if (hierarchy == null)
            {
                return new ClaimsHierarchyGetResult.FailureUnknown(
                    "Unable to deserialize claim set hierarchy"
                );
            }

            return new ClaimsHierarchyGetResult.Success(hierarchy, hierarchyTuples[0].lastModifiedDate);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unable to get claims hierarchy");
            return new ClaimsHierarchyGetResult.FailureUnknown(ex.Message);
        }
    }

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

        var deletedIds = await connection.QueryAsync<long>(Sql, transaction: transaction);
        var deletedCount = deletedIds.Count();

        logger.LogDebug("Deleted {Count} non-system-reserved claim sets", deletedCount);
        return deletedCount;
    }

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

        var insertedCount = 0;

        foreach (var claimSetNode in claimSetsArray)
        {
            if (claimSetNode == null)
            {
                continue;
            }

            var claimSetName = claimSetNode["claimSetName"]?.GetValue<string>();
            var isSystemReserved = claimSetNode["isSystemReserved"]?.GetValue<bool>() ?? false;

            if (string.IsNullOrEmpty(claimSetName))
            {
                logger.LogWarning("Skipping claim set with no name");
                continue;
            }

            var insertedId = await connection.QuerySingleOrDefaultAsync<long?>(
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

    private async Task<bool> UpdateClaimsHierarchy(
        JsonNode? hierarchyNode,
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
            var hierarchyJson = hierarchyArray.ToJsonString();
            var claims = JsonSerializer.Deserialize<List<Claim>>(
                hierarchyJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            );

            if (claims == null)
            {
                logger.LogWarning("Failed to deserialize claims hierarchy");
                return false;
            }

            // Check if hierarchy exists
            const string SelectSql = "SELECT id, lastmodifieddate FROM dmscs.ClaimsHierarchy";
            var existingRecords = (
                await connection.QueryAsync<(long Id, DateTime LastModifiedDate)>(
                    SelectSql,
                    transaction: transaction
                )
            ).ToList();

            if (existingRecords.Count > 1)
            {
                return false; // Multiple hierarchies not supported
            }

            var newHierarchyJson = JsonSerializer.Serialize(claims);

            if (existingRecords.Count == 0)
            {
                // Insert new hierarchy
                const string InsertSql =
                    @"
                    INSERT INTO dmscs.ClaimsHierarchy (hierarchy, lastmodifieddate)
                    VALUES (@Hierarchy::jsonb, now())";

                await connection.ExecuteAsync(InsertSql, new { Hierarchy = newHierarchyJson }, transaction);
            }
            else
            {
                // Update existing hierarchy
                var (id, lastModifiedDate) = existingRecords.Single();

                // Check for multi-user conflict
                if (existingLastModifiedDate != lastModifiedDate)
                {
                    logger.LogError("Multi-user conflict detected while updating claims hierarchy");
                    return false;
                }

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
                        Id = id,
                        LastModifiedDate = existingLastModifiedDate,
                    },
                    transaction
                );

                if (affectedRows == 0)
                {
                    logger.LogError("Multi-user conflict detected during update");
                    return false;
                }
            }

            logger.LogDebug("Successfully updated claims hierarchy with {Count} root claims", claims.Count);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update claims hierarchy");
            throw new InvalidOperationException("Failed to update claims hierarchy", ex);
        }
    }
}
