// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Text.Json;
using Dapper;
using EdFi.DmsConfigurationService.Backend.Models.ClaimsHierarchy;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Repositories;

public class ClaimsHierarchyRepository(
    IOptions<DatabaseOptions> databaseOptions,
    ILogger<ClaimsHierarchyRepository> logger,
    IAuditContext auditContext
) : IClaimsHierarchyRepository
{
    public async Task<ClaimsHierarchyGetResult> GetClaimsHierarchy(DbTransaction? transaction = null)
    {
        (DbConnection connection, bool shouldDispose) = await GetConnectionAsync(transaction);

        try
        {
            // Initial implementation assumes a basic implementation with single record.
            string sql = "SELECT Id, Hierarchy, LastModifiedDate FROM dmscs.ClaimsHierarchy";

            var hierarchyTuples = (
                await connection.QueryAsync<(long id, string hierarchyJson, DateTime lastModifiedDate)>(sql)
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
        finally
        {
            if (shouldDispose)
            {
                await connection.DisposeAsync();
            }
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
                    INSERT INTO dmscs.ClaimsHierarchy (hierarchy, lastmodifieddate, CreatedBy)
                    VALUES (@Hierarchy::jsonb, now(), @CreatedBy);";

                await connection.ExecuteAsync(
                    InsertSql,
                    new
                    {
                        Hierarchy = hierarchyJson,
                        CreatedBy = auditContext.GetCurrentUser()
                    });
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
                        lastmodifieddate = now(),
                        LastModifiedAt = @LastModifiedAt,
                        ModifiedBy = @ModifiedBy
                    WHERE id = @Id AND lastmodifieddate = @LastModifiedDate;";

                int affectedRows = await connection.ExecuteAsync(
                    UpdateSql,
                    new
                    {
                        Hierarchy = hierarchyJson,
                        Id = existingRecord.Id,
                        LastModifiedDate = existingLastModifiedDate,
                        LastModifiedAt = auditContext.GetCurrentTimestamp(),
                        ModifiedBy = auditContext.GetCurrentUser()
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
        catch (JsonException ex)
        {
            logger.LogError(ex, "JSON serialization error saving claims hierarchy");
            return new ClaimsHierarchySaveResult.FailureUnknown(
                "Invalid JSON format in claims hierarchy: " + ex.Message
            );
        }
        catch (NpgsqlException ex)
        {
            logger.LogError(ex, "Database error saving claims hierarchy");
            return new ClaimsHierarchySaveResult.FailureUnknown("Database error: " + ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogError(ex, "Invalid operation saving claims hierarchy");
            return new ClaimsHierarchySaveResult.FailureUnknown("Invalid operation: " + ex.Message);
        }
        finally
        {
            if (shouldDispose)
            {
                await connection.DisposeAsync();
            }
        }
    }

    private async Task<(DbConnection conn, bool shouldDispose)> GetConnectionAsync(DbTransaction? transaction)
    {
        if (transaction?.Connection != null)
        {
            return (transaction.Connection, shouldDispose: false);
        }

        var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();

        return (connection, shouldDispose: true);
    }
}
