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

/// <summary>
/// PostgreSQL implementation of IClaimsTableValidator
/// </summary>
public class ClaimsTableValidator(
    IOptions<DatabaseOptions> databaseOptions,
    ILogger<ClaimsTableValidator> logger
) : IClaimsTableValidator
{
    public async Task<bool> AreClaimsTablesEmptyAsync()
    {
        try
        {
            await using NpgsqlConnection connection = new NpgsqlConnection(
                databaseOptions.Value.DatabaseConnection
            );
            await connection.OpenAsync();

            // Check if ClaimSet table has any records
            int claimSetCount = await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM dmscs.ClaimSet"
            );

            if (claimSetCount > 0)
            {
                logger.LogDebug("ClaimSet table has {Count} records", claimSetCount);
                return false;
            }

            // Check if ClaimsHierarchy table has any records
            int hierarchyCount = await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM dmscs.ClaimsHierarchy"
            );

            if (hierarchyCount > 0)
            {
                logger.LogDebug("ClaimsHierarchy table has {Count} records", hierarchyCount);
                return false;
            }

            logger.LogInformation("Both ClaimSet and ClaimsHierarchy tables are empty");
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error checking if claims tables are empty");
            throw new InvalidOperationException("Failed to check claims table state", ex);
        }
    }
}
