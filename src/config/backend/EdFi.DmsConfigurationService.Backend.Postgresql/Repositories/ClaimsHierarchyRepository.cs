// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
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
            string sql = "SELECT Hierarchy FROM dmscs.ClaimsHierarchy";

            string? hierarchyJson = await connection.QuerySingleOrDefaultAsync<string>(sql);

            if (hierarchyJson == null)
            {
                return new ClaimsHierarchyGetResult.FailureHierarchyNotFound();
            }

            var hierarchy = JsonSerializer.Deserialize<Claim[]>(hierarchyJson);

            if (hierarchy == null)
            {
                return new ClaimsHierarchyGetResult.FailureUnknown(
                    "Unable to deserialize claim set hierarchy"
                );
            }

            return new ClaimsHierarchyGetResult.Success(hierarchy);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unable to get claims hierarchy");

            return new ClaimsHierarchyGetResult.FailureUnknown(ex.Message);
        }
    }

    public async Task<ClaimsHierarchySaveResult> SaveClaimsHierarchy(
        Claim[] claimsHierarchy,
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
                return new ClaimsHierarchySaveResult.MultipleHierarchiesFound();
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
                        LastModifiedDate = existingRecord.LastModifiedDate,
                    }
                );

                if (affectedRows == 0)
                {
                    return new ClaimsHierarchySaveResult.MultiUserConflict();
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
