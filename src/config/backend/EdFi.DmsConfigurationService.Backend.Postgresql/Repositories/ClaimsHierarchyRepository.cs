// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using Dapper;
using EdFi.DmsConfigurationService.Backend.Repositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Repositories;

public class ClaimsHierarchyRepository(
    IOptions<DatabaseOptions> databaseOptions,
    ILogger<ClaimsHierarchyRepository> logger) : IClaimsHierarchyRepository
{
    public async Task<ClaimsHierarchyResult> GetClaimsHierarchy()
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();

        try
        {
            // Initial implementation assumes a basic implementation with single record.
            string sql = "SELECT Hierarchy FROM dmscs.ClaimsHierarchy";

            string? hierarchyJson = await connection.QuerySingleAsync<string>(sql);

            if (hierarchyJson == null)
            {
                return new ClaimsHierarchyResult.FailureHierarchyNotFound();
            }

            var hierarchy = JsonSerializer.Deserialize<Claim[]>(hierarchyJson);

            if (hierarchy == null)
            {
                return new ClaimsHierarchyResult.FailureUnknown("Unable to deserialize claim set hierarchy");
            }

            return new ClaimsHierarchyResult.Success(hierarchy);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unable to get claims hierarchy");

            return new ClaimsHierarchyResult.FailureUnknown(ex.Message);
        }
    }
}
