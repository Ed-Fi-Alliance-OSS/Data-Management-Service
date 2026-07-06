// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using Dapper;
using EdFi.DmsConfigurationService.Backend.ClaimsDataLoader;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.Mssql.ClaimsDataLoader;

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
            await using SqlConnection connection = new(databaseOptions.Value.DatabaseConnection);
            await connection.OpenAsync();

            string payload = JsonSerializer.Serialize(
                resourceClaims.Select(x => new { x.ResourceName, x.ClaimName })
            );

            int insertedCount = await connection.ExecuteAsync(
                """
                INSERT INTO dmscs.ResourceClaim (ResourceName, ClaimName)
                SELECT j.ResourceName, j.ClaimName
                FROM OPENJSON(@Payload)
                    WITH (ResourceName NVARCHAR(255) '$.ResourceName', ClaimName NVARCHAR(255) '$.ClaimName') j
                WHERE NOT EXISTS (SELECT 1 FROM dmscs.ResourceClaim rc WHERE rc.ClaimName = j.ClaimName);
                """,
                new { Payload = payload }
            );

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
