// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Text.Json;
using Dapper;
using EdFi.DmsConfigurationService.Backend.Repositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Repositories;

public class ClaimsHierarchyRepository(
    IOptions<DatabaseOptions> databaseOptions,
    ILogger<ClaimsHierarchyRepository> logger
) : IClaimsHierarchyRepository
{
    public async Task<ClaimsHierarchyGetResult> GetClaimsHierarchy()
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();

        try
        {
            // Initial implementation assumes a basic implementation with single record.
            string sql = "SELECT Hierarchy, LastModifiedDate FROM dmscs.ClaimsHierarchy";

            var hierarchyTuples = (
                await connection.QueryAsync<(string hierarchyJson, DateTime lastModifiedDate)>(sql)
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

    public async Task<ClaimsHierarchySaveResult> SaveClaimsHierarchy(
        List<Claim> claimsHierarchy,
        DateTime existingLastModifiedDate,
        DbTransaction? transaction = null
    )
    {
        (DbConnection connection, bool shouldDispose) = await GetConnectionAsync(transaction);

        try
        {
            string hierarchyJson = JsonSerializer.Serialize(claimsHierarchy);

            const string SelectSql = "SELECT id, lastmodifieddate FROM dmscs.ClaimsHierarchy";

            var existingRecords = (
                await connection.QueryAsync<(long Id, DateTime LastModifiedDate)>(SelectSql)
            ).ToList();

            if (existingRecords.Count > 1)
            {
                // Only one claims hierarchy is currently expected to be present, but multiple were found.
                return new ClaimsHierarchySaveResult.FailureMultipleHierarchiesFound();
            }

            if (existingRecords.Count == 0)
            {
                const string InsertSql =
                    @"
                    INSERT INTO dmscs.ClaimsHierarchy (hierarchy, lastmodifieddate)
                    VALUES (@Hierarchy::jsonb, now());";

                await connection.ExecuteAsync(InsertSql, new { Hierarchy = hierarchyJson });
            }
            else
            {
                var existingRecord = existingRecords.Single();

                // If the supplied change data doesn't match the one just retrieved, this indicates a multi-user conflict
                if (existingLastModifiedDate != existingRecord.LastModifiedDate)
                {
                    return new ClaimsHierarchySaveResult.FailureMultiUserConflict();
                }

                const string UpdateSql =
                    @"
                    UPDATE dmscs.ClaimsHierarchy
                    SET hierarchy = @Hierarchy::jsonb,
                        lastmodifieddate = now()
                    WHERE id = @Id AND lastmodifieddate = @LastModifiedDate;";

                int affectedRows = await connection.ExecuteAsync(
                    UpdateSql,
                    new
                    {
                        Hierarchy = hierarchyJson,
                        Id = existingRecord.Id,
                        LastModifiedDate = existingLastModifiedDate,
                    }
                );

                // In the remote chance a multi-user conflict happened since the last check, we need to respond with a failure.
                if (affectedRows == 0)
                {
                    return new ClaimsHierarchySaveResult.FailureMultiUserConflict();
                }
            }

            return new ClaimsHierarchySaveResult.Success();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unable to save claims hierarchy");
            return new ClaimsHierarchySaveResult.FailureUnknown(ex.Message);
        }
        finally
        {
            if (shouldDispose)
            {
                await connection.DisposeAsync();
            }
        }
    }

    private async Task<(DbConnection connection, bool shouldDispose)> GetConnectionAsync(
        DbTransaction? transaction
    )
    {
        bool shouldDispose = false;

        DbConnection? connection;
        DbConnection? suppliedConnection = transaction?.Connection;

        if (suppliedConnection == null)
        {
            connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
            await connection.OpenAsync();
            shouldDispose = true;
        }
        else
        {
            connection = suppliedConnection;
        }

        return (connection, shouldDispose);
    }
}
