// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.DmsInstanceRouteContext;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Repositories;

public class DmsInstanceRouteContextRepository(
    IOptions<DatabaseOptions> databaseOptions,
    ILogger<DmsInstanceRouteContextRepository> logger
) : IDmsInstanceRouteContextRepository
{
    public async Task<DmsInstanceRouteContextInsertResult> InsertDmsInstanceRouteContext(
        DmsInstanceRouteContextInsertCommand command
    )
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();
        try
        {
            string sql = """
                INSERT INTO dmscs.DmsInstanceRouteContext (InstanceId, ContextKey, ContextValue)
                VALUES (@InstanceId, @ContextKey, @ContextValue)
                RETURNING Id;
                """;

            long id = await connection.ExecuteScalarAsync<long>(sql, command);

            return new DmsInstanceRouteContextInsertResult.Success(id);
        }
        catch (PostgresException ex)
            when (ex.SqlState == "23503" && ex.Message.Contains("fk_dmsinstanceroutecontext_instance"))
        {
            logger.LogWarning(ex, "Instance not found");
            return new DmsInstanceRouteContextInsertResult.FailureInstanceNotFound();
        }
        catch (PostgresException ex)
            when (ex.SqlState == "23505" && ex.Message.Contains("idx_dms_instance_routecontext_unique"))
        {
            logger.LogWarning(
                ex,
                "Instance route context already exists for InstanceId '{InstanceId}' and ContextKey '{ContextKey}'",
                command.InstanceId,
                command.ContextKey
            );
            return new DmsInstanceRouteContextInsertResult.FailureDuplicateDmsInstanceRouteContext(
                command.InstanceId,
                command.ContextKey
            );
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Insert instance route context failure");
            return new DmsInstanceRouteContextInsertResult.FailureUnknown(ex.Message);
        }
    }

    public async Task<DmsInstanceRouteContextQueryResult> QueryInstanceRouteContext(PagingQuery query)
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();
        try
        {
            string sql = """
                SELECT Id, InstanceId, ContextKey, ContextValue
                FROM dmscs.DmsInstanceRouteContext
                ORDER BY Id
                LIMIT @Limit OFFSET @Offset;
                """;
            var instanceRouteContexts = await connection.QueryAsync<DmsInstanceRouteContextResponse>(
                sql,
                query
            );

            return new DmsInstanceRouteContextQueryResult.Success(instanceRouteContexts);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Query instance route context failure");
            return new DmsInstanceRouteContextQueryResult.FailureUnknown(ex.Message);
        }
    }

    public async Task<DmsInstanceRouteContextGetResult> GetInstanceRouteContext(long id)
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();
        try
        {
            string sql = """
                SELECT Id, InstanceId, ContextKey, ContextValue
                FROM dmscs.DmsInstanceRouteContext
                WHERE Id = @Id;
                """;

            var instanceRouteContext =
                await connection.QuerySingleOrDefaultAsync<DmsInstanceRouteContextResponse>(
                    sql,
                    new { Id = id }
                );

            return instanceRouteContext != null
                ? new DmsInstanceRouteContextGetResult.Success(instanceRouteContext)
                : new DmsInstanceRouteContextGetResult.FailureNotFound();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Get instance route context failure");
            return new DmsInstanceRouteContextGetResult.FailureUnknown(ex.Message);
        }
    }

    public async Task<DmsInstanceRouteContextUpdateResult> UpdateDmsInstanceRouteContext(
        DmsInstanceRouteContextUpdateCommand command
    )
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();
        try
        {
            string sql = """
                UPDATE dmscs.DmsInstanceRouteContext
                SET InstanceId = @InstanceId, ContextKey = @ContextKey, ContextValue = @ContextValue
                WHERE Id = @Id;
                """;

            int affectedRows = await connection.ExecuteAsync(sql, command);

            if (affectedRows == 0)
            {
                return new DmsInstanceRouteContextUpdateResult.FailureNotExists();
            }

            return new DmsInstanceRouteContextUpdateResult.Success();
        }
        catch (PostgresException ex)
            when (ex.SqlState == "23503" && ex.Message.Contains("fk_dmsinstanceroutecontext_instance"))
        {
            logger.LogWarning(ex, "Update instance route context failure: Instance not found");
            return new DmsInstanceRouteContextUpdateResult.FailureInstanceNotFound();
        }
        catch (PostgresException ex)
            when (ex.SqlState == "23505" && ex.Message.Contains("idx_dms_instance_routecontext_unique"))
        {
            logger.LogWarning(
                ex,
                "Instance route context already exists for InstanceId '{InstanceId}' and ContextKey '{ContextKey}'",
                command.InstanceId,
                command.ContextKey
            );
            return new DmsInstanceRouteContextUpdateResult.FailureDuplicateDmsInstanceRouteContext(
                command.InstanceId,
                command.ContextKey
            );
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Update instance route context failure");
            return new DmsInstanceRouteContextUpdateResult.FailureUnknown(ex.Message);
        }
    }

    public async Task<InstanceRouteContextDeleteResult> DeleteInstanceRouteContext(long id)
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();
        try
        {
            string sql = """
                DELETE FROM dmscs.DmsInstanceRouteContext WHERE Id = @Id;
                """;

            int affectedRows = await connection.ExecuteAsync(sql, new { Id = id });
            return affectedRows > 0
                ? new InstanceRouteContextDeleteResult.Success()
                : new InstanceRouteContextDeleteResult.FailureNotExists();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Delete instance route context failure");
            return new InstanceRouteContextDeleteResult.FailureUnknown(ex.Message);
        }
    }

    public async Task<InstanceRouteContextQueryByInstanceResult> GetInstanceRouteContextsByInstance(
        long instanceId
    )
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();
        try
        {
            string sql = """
                SELECT Id, InstanceId, ContextKey, ContextValue
                FROM dmscs.DmsInstanceRouteContext
                WHERE InstanceId = @InstanceId
                ORDER BY ContextKey;
                """;
            var instanceRouteContexts = await connection.QueryAsync<DmsInstanceRouteContextResponse>(
                sql,
                new { InstanceId = instanceId }
            );

            return new InstanceRouteContextQueryByInstanceResult.Success(instanceRouteContexts);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Get instance route contexts by instance failure");
            return new InstanceRouteContextQueryByInstanceResult.FailureUnknown(ex.Message);
        }
    }

    public async Task<InstanceRouteContextQueryByInstanceIdsResult> GetInstanceRouteContextsByInstanceIds(
        List<long> instanceIds
    )
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();
        try
        {
            if (instanceIds == null || !instanceIds.Any())
            {
                return new InstanceRouteContextQueryByInstanceIdsResult.Success([]);
            }

            string sql = """
                SELECT Id, InstanceId, ContextKey, ContextValue
                FROM dmscs.DmsInstanceRouteContext
                WHERE InstanceId = ANY(@InstanceIds)
                ORDER BY InstanceId, ContextKey;
                """;
            var instanceRouteContexts = await connection.QueryAsync<DmsInstanceRouteContextResponse>(
                sql,
                new { InstanceIds = instanceIds }
            );

            return new InstanceRouteContextQueryByInstanceIdsResult.Success(instanceRouteContexts);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Get instance route contexts by instance IDs failure");
            return new InstanceRouteContextQueryByInstanceIdsResult.FailureUnknown(ex.Message);
        }
    }
}
