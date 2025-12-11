// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.DmsInstanceRouteContext;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Repositories;

public class DmsInstanceRouteContextRepository(
    IOptions<DatabaseOptions> databaseOptions,
    ILogger<DmsInstanceRouteContextRepository> logger,
    IAuditContext auditContext,
    ITenantContextProvider tenantContextProvider
) : IDmsInstanceRouteContextRepository
{
    private TenantContext TenantContext => tenantContextProvider.Context;

    private long? TenantId => TenantContext is TenantContext.Multitenant mt ? mt.TenantId : null;

    public async Task<DmsInstanceRouteContextInsertResult> InsertDmsInstanceRouteContext(
        DmsInstanceRouteContextInsertCommand command
    )
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();
        try
        {
            // Insert only if the instance belongs to the current tenant
            var sql = $"""
                INSERT INTO dmscs.DmsInstanceRouteContext (InstanceId, ContextKey, ContextValue, CreatedBy)
                SELECT @InstanceId, @ContextKey, @ContextValue, @CreatedBy
                WHERE EXISTS (
                    SELECT 1 FROM dmscs.DmsInstance
                    WHERE Id = @InstanceId AND {TenantContext.TenantWhereClause()}
                )
                RETURNING Id;
                """;

            var id = await connection.ExecuteScalarAsync<long?>(
                sql,
                new
                {
                    command.InstanceId,
                    command.ContextKey,
                    command.ContextValue,
                    CreatedBy = auditContext.GetCurrentUser(),
                    TenantId,
                }
            );

            if (id == null)
            {
                return new DmsInstanceRouteContextInsertResult.FailureInstanceNotFound();
            }

            return new DmsInstanceRouteContextInsertResult.Success(id.Value);
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
            var sql = $"""
                SELECT rc.Id, rc.InstanceId, rc.ContextKey, rc.ContextValue,
                       rc.CreatedAt, rc.CreatedBy, rc.LastModifiedAt, rc.ModifiedBy
                FROM dmscs.DmsInstanceRouteContext rc
                JOIN dmscs.DmsInstance i ON rc.InstanceId = i.Id
                WHERE {TenantContext.TenantWhereClause("i")}
                ORDER BY rc.Id
                LIMIT @Limit OFFSET @Offset;
                """;
            var instanceRouteContexts = await connection.QueryAsync<DmsInstanceRouteContextResponse>(
                sql,
                new
                {
                    query.Limit,
                    query.Offset,
                    TenantId,
                }
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
            var sql = $"""
                SELECT rc.Id, rc.InstanceId, rc.ContextKey, rc.ContextValue,
                       rc.CreatedAt, rc.CreatedBy, rc.LastModifiedAt, rc.ModifiedBy
                FROM dmscs.DmsInstanceRouteContext rc
                JOIN dmscs.DmsInstance i ON rc.InstanceId = i.Id
                WHERE rc.Id = @Id AND {TenantContext.TenantWhereClause("i")};
                """;

            var instanceRouteContext =
                await connection.QuerySingleOrDefaultAsync<DmsInstanceRouteContextResponse>(
                    sql,
                    new { Id = id, TenantId }
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
            // Update only if both the route context and target instance belong to the current tenant
            var sql = $"""
                UPDATE dmscs.DmsInstanceRouteContext rc
                SET InstanceId = @InstanceId, ContextKey = @ContextKey, ContextValue = @ContextValue,
                    LastModifiedAt = @LastModifiedAt, ModifiedBy = @ModifiedBy
                FROM dmscs.DmsInstance i
                WHERE rc.Id = @Id
                  AND rc.InstanceId = i.Id
                  AND {TenantContext.TenantWhereClause("i")}
                  AND EXISTS (
                      SELECT 1 FROM dmscs.DmsInstance
                      WHERE Id = @InstanceId AND {TenantContext.TenantWhereClause()}
                  );
                """;

            int affectedRows = await connection.ExecuteAsync(
                sql,
                new
                {
                    command.Id,
                    command.InstanceId,
                    command.ContextKey,
                    command.ContextValue,
                    LastModifiedAt = auditContext.GetCurrentTimestamp(),
                    ModifiedBy = auditContext.GetCurrentUser(),
                    TenantId,
                }
            );

            if (affectedRows == 0)
            {
                // Determine why: route context doesn't exist/not owned, or target instance not owned
                var routeContextExists = await RouteContextExistsForTenant(connection, command.Id);
                if (!routeContextExists)
                {
                    return new DmsInstanceRouteContextUpdateResult.FailureNotExists();
                }

                return new DmsInstanceRouteContextUpdateResult.FailureInstanceNotFound();
            }

            return new DmsInstanceRouteContextUpdateResult.Success();
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
            var sql = $"""
                DELETE FROM dmscs.DmsInstanceRouteContext
                WHERE Id = @Id
                  AND InstanceId IN (
                      SELECT Id FROM dmscs.DmsInstance WHERE {TenantContext.TenantWhereClause()}
                  );
                """;

            int affectedRows = await connection.ExecuteAsync(sql, new { Id = id, TenantId });
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
            var sql = $"""
                SELECT rc.Id, rc.InstanceId, rc.ContextKey, rc.ContextValue,
                       rc.CreatedAt, rc.CreatedBy, rc.LastModifiedAt, rc.ModifiedBy
                FROM dmscs.DmsInstanceRouteContext rc
                JOIN dmscs.DmsInstance i ON rc.InstanceId = i.Id
                WHERE rc.InstanceId = @InstanceId AND {TenantContext.TenantWhereClause("i")}
                ORDER BY rc.ContextKey;
                """;
            var instanceRouteContexts = await connection.QueryAsync<DmsInstanceRouteContextResponse>(
                sql,
                new { InstanceId = instanceId, TenantId }
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
            if (instanceIds == null || instanceIds.Count == 0)
            {
                return new InstanceRouteContextQueryByInstanceIdsResult.Success([]);
            }

            var sql = $"""
                SELECT rc.Id, rc.InstanceId, rc.ContextKey, rc.ContextValue,
                       rc.CreatedAt, rc.CreatedBy, rc.LastModifiedAt, rc.ModifiedBy
                FROM dmscs.DmsInstanceRouteContext rc
                JOIN dmscs.DmsInstance i ON rc.InstanceId = i.Id
                WHERE rc.InstanceId = ANY(@InstanceIds) AND {TenantContext.TenantWhereClause("i")}
                ORDER BY rc.InstanceId, rc.ContextKey;
                """;
            var instanceRouteContexts = await connection.QueryAsync<DmsInstanceRouteContextResponse>(
                sql,
                new { InstanceIds = instanceIds, TenantId }
            );

            return new InstanceRouteContextQueryByInstanceIdsResult.Success(instanceRouteContexts);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Get instance route contexts by instance IDs failure");
            return new InstanceRouteContextQueryByInstanceIdsResult.FailureUnknown(ex.Message);
        }
    }

    private async Task<bool> RouteContextExistsForTenant(NpgsqlConnection connection, long id)
    {
        var sql = $"""
            SELECT COUNT(1) FROM dmscs.DmsInstanceRouteContext rc
            JOIN dmscs.DmsInstance i ON rc.InstanceId = i.Id
            WHERE rc.Id = @Id AND {TenantContext.TenantWhereClause("i")};
            """;

        return await connection.ExecuteScalarAsync<int>(sql, new { Id = id, TenantId }) > 0;
    }
}
