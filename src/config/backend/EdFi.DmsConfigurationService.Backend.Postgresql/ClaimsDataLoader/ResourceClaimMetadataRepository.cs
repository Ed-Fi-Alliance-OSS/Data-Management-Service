// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using EdFi.DmsConfigurationService.Backend.ClaimsDataLoader;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.ClaimsDataLoader;

public class ResourceClaimMetadataRepository(
    IOptions<DatabaseOptions> databaseOptions,
    ILogger<ResourceClaimMetadataRepository> logger
) : IResourceClaimMetadataRepository
{
    public async Task<int> SeedResourceClaims(IReadOnlyList<ResourceClaimMetadataSeed> resourceClaims)
    {
        if (resourceClaims.Count == 0)
        {
            return 0;
        }

        try
        {
            await using NpgsqlConnection connection = new(databaseOptions.Value.DatabaseConnection);
            await connection.OpenAsync();

            var inserted = await connection.QueryAsync<int>(
                """
                WITH input AS (
                    SELECT *
                    FROM unnest(@ResourceNames::text[], @ClaimNames::text[]) AS x("ResourceName", "ClaimName")
                )
                INSERT INTO "dmscs"."ResourceClaim" ("ResourceName", "ClaimName")
                SELECT "ResourceName", "ClaimName"
                FROM input
                ON CONFLICT ON CONSTRAINT "UX_ResourceClaim_TenantId_ClaimName" DO NOTHING
                RETURNING 1;
                """,
                new
                {
                    ResourceNames = resourceClaims.Select(x => x.ResourceName).ToArray(),
                    ClaimNames = resourceClaims.Select(x => x.ClaimName).ToArray(),
                }
            );

            int insertedCount = inserted.Count();
            logger.LogInformation("Seeded {InsertedCount} resource claim metadata rows", insertedCount);
            return insertedCount;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to seed resource claim metadata");
            throw new DatabaseOperationException("Failed to seed resource claim metadata");
        }
    }
}
