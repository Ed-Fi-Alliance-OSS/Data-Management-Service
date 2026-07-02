// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.DataStoreContext;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Repositories;

public class DataStoreContextRepository(
    IOptions<DatabaseOptions> databaseOptions,
    ILogger<DataStoreContextRepository> logger,
    IAuditContext auditContext,
    ITenantContextProvider tenantContextProvider
) : IDataStoreContextRepository
{
    private static readonly IReadOnlyDictionary<string, string> OrderByColumns = new Dictionary<
        string,
        string
    >(StringComparer.OrdinalIgnoreCase)
    {
        ["id"] = "\"Id\"",
        ["dataStoreId"] = "\"DataStoreId\"",
        ["contextKey"] = "\"ContextKey\"",
        ["contextValue"] = "\"ContextValue\"",
    };

    private static string BuildOrderByClause(PagingQuery query)
    {
        if (query.OrderBy is not null && OrderByColumns.TryGetValue(query.OrderBy, out var col))
        {
            return $"ORDER BY {col} {(query.IsDescending ? "DESC" : "ASC")}";
        }

        return "ORDER BY \"Id\"";
    }

    private TenantContext TenantContext => tenantContextProvider.Context;

    private long? TenantId => TenantContext is TenantContext.Multitenant mt ? mt.TenantId : null;

    public async Task<DataStoreContextInsertResult> InsertDataStoreContext(
        DataStoreContextInsertCommand command
    )
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();
        try
        {
            var sql = $"""
                INSERT INTO "dmscs"."DataStoreContext" ("DataStoreId", "ContextKey", "ContextValue", "CreatedBy")
                SELECT @DataStoreId, @ContextKey, @ContextValue, @CreatedBy
                WHERE EXISTS (
                    SELECT 1 FROM "dmscs"."DataStore"
                    WHERE "Id" = @DataStoreId AND {TenantContext.TenantWhereClause()}
                )
                RETURNING "Id";
                """;

            var id = await connection.ExecuteScalarAsync<long?>(
                sql,
                new
                {
                    command.DataStoreId,
                    command.ContextKey,
                    command.ContextValue,
                    CreatedBy = auditContext.GetCurrentUser(),
                    TenantId,
                }
            );

            if (id is null)
            {
                return new DataStoreContextInsertResult.FailureDataStoreNotFound();
            }

            return new DataStoreContextInsertResult.Success(id.Value);
        }
        catch (PostgresException ex)
            when (ex.SqlState == PostgresErrorCodes.UniqueViolation
                && ex.ConstraintName == "UX_DataStoreContext_DataStoreId_ContextKey"
            )
        {
            logger.LogWarning(
                ex,
                "Data store context already exists for DataStoreId '{DataStoreId}' and ContextKey '{ContextKey}'",
                command.DataStoreId,
                command.ContextKey
            );
            return new DataStoreContextInsertResult.FailureDuplicateDataStoreContext(
                command.DataStoreId,
                command.ContextKey
            );
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Insert data store context failure");
            return new DataStoreContextInsertResult.FailureUnknown(ex.Message);
        }
    }

    public async Task<DataStoreContextQueryResult> QueryDataStoreContext(PagingQuery query)
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();
        try
        {
            string orderByClause = BuildOrderByClause(query);
            var sql = $"""
                SELECT rc."Id", rc."DataStoreId", rc."ContextKey", rc."ContextValue"
                FROM "dmscs"."DataStoreContext" rc
                JOIN "dmscs"."DataStore" ds ON rc."DataStoreId" = ds."Id"
                WHERE {TenantContext.TenantWhereClause("ds")}
                {orderByClause}
                {query.BuildPagingClause()};
                """;
            var dataStoreContexts = await connection.QueryAsync<DataStoreContextResponse>(
                sql,
                new
                {
                    query.Limit,
                    query.Offset,
                    TenantId,
                }
            );

            return new DataStoreContextQueryResult.Success(dataStoreContexts);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Query data store context failure");
            return new DataStoreContextQueryResult.FailureUnknown(ex.Message);
        }
    }

    public async Task<DataStoreContextGetResult> GetDataStoreContext(long id)
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();
        try
        {
            var sql = $"""
                SELECT rc."Id", rc."DataStoreId", rc."ContextKey", rc."ContextValue"
                FROM "dmscs"."DataStoreContext" rc
                JOIN "dmscs"."DataStore" ds ON rc."DataStoreId" = ds."Id"
                WHERE rc."Id" = @Id AND {TenantContext.TenantWhereClause("ds")};
                """;

            var dataStoreContext = await connection.QuerySingleOrDefaultAsync<DataStoreContextResponse>(
                sql,
                new { Id = id, TenantId }
            );

            return dataStoreContext is not null
                ? new DataStoreContextGetResult.Success(dataStoreContext)
                : new DataStoreContextGetResult.FailureNotFound();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Get data store context failure");
            return new DataStoreContextGetResult.FailureUnknown(ex.Message);
        }
    }

    public async Task<DataStoreContextUpdateResult> UpdateDataStoreContext(
        DataStoreContextUpdateCommand command
    )
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();
        try
        {
            var sql = $"""
                UPDATE "dmscs"."DataStoreContext" rc
                SET "DataStoreId" = @DataStoreId, "ContextKey" = @ContextKey, "ContextValue" = @ContextValue,
                    "LastModifiedAt" = @LastModifiedAt, "ModifiedBy" = @ModifiedBy
                FROM "dmscs"."DataStore" ds
                WHERE rc."Id" = @Id
                  AND rc."DataStoreId" = ds."Id"
                  AND {TenantContext.TenantWhereClause("ds")}
                  AND EXISTS (
                      SELECT 1 FROM "dmscs"."DataStore"
                      WHERE "Id" = @DataStoreId AND {TenantContext.TenantWhereClause()}
                  );
                """;

            int affectedRows = await connection.ExecuteAsync(
                sql,
                new
                {
                    command.Id,
                    command.DataStoreId,
                    command.ContextKey,
                    command.ContextValue,
                    LastModifiedAt = auditContext.GetCurrentTimestamp(),
                    ModifiedBy = auditContext.GetCurrentUser(),
                    TenantId,
                }
            );

            if (affectedRows == 0)
            {
                var contextExists = await ContextExistsForTenant(connection, command.Id);
                if (!contextExists)
                {
                    return new DataStoreContextUpdateResult.FailureNotExists();
                }

                return new DataStoreContextUpdateResult.FailureDataStoreNotFound();
            }

            return new DataStoreContextUpdateResult.Success();
        }
        catch (PostgresException ex)
            when (ex.SqlState == PostgresErrorCodes.UniqueViolation
                && ex.ConstraintName == "UX_DataStoreContext_DataStoreId_ContextKey"
            )
        {
            logger.LogWarning(
                ex,
                "Data store context already exists for DataStoreId '{DataStoreId}' and ContextKey '{ContextKey}'",
                command.DataStoreId,
                command.ContextKey
            );
            return new DataStoreContextUpdateResult.FailureDuplicateDataStoreContext(
                command.DataStoreId,
                command.ContextKey
            );
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Update data store context failure");
            return new DataStoreContextUpdateResult.FailureUnknown(ex.Message);
        }
    }

    public async Task<DataStoreContextDeleteResult> DeleteDataStoreContext(long id)
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();
        try
        {
            var sql = $"""
                DELETE FROM "dmscs"."DataStoreContext"
                WHERE "Id" = @Id
                  AND "DataStoreId" IN (
                      SELECT "Id" FROM "dmscs"."DataStore" WHERE {TenantContext.TenantWhereClause()}
                  );
                """;

            int affectedRows = await connection.ExecuteAsync(sql, new { Id = id, TenantId });
            return affectedRows > 0
                ? new DataStoreContextDeleteResult.Success()
                : new DataStoreContextDeleteResult.FailureNotExists();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Delete data store context failure");
            return new DataStoreContextDeleteResult.FailureUnknown(ex.Message);
        }
    }

    public async Task<DataStoreContextQueryByDataStoreResult> GetDataStoreContextsByDataStore(
        long dataStoreId
    )
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();
        try
        {
            var sql = $"""
                SELECT rc."Id", rc."DataStoreId", rc."ContextKey", rc."ContextValue"
                FROM "dmscs"."DataStoreContext" rc
                JOIN "dmscs"."DataStore" ds ON rc."DataStoreId" = ds."Id"
                WHERE rc."DataStoreId" = @DataStoreId AND {TenantContext.TenantWhereClause("ds")}
                ORDER BY rc."ContextKey";
                """;
            var dataStoreContexts = await connection.QueryAsync<DataStoreContextResponse>(
                sql,
                new { DataStoreId = dataStoreId, TenantId }
            );

            return new DataStoreContextQueryByDataStoreResult.Success(dataStoreContexts);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Get data store contexts by data store failure");
            return new DataStoreContextQueryByDataStoreResult.FailureUnknown(ex.Message);
        }
    }

    public async Task<DataStoreContextQueryByDataStoreIdsResult> GetDataStoreContextsByDataStoreIds(
        List<long> dataStoreIds
    )
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();
        try
        {
            if (dataStoreIds is null || dataStoreIds.Count == 0)
            {
                return new DataStoreContextQueryByDataStoreIdsResult.Success([]);
            }

            var sql = $"""
                SELECT rc."Id", rc."DataStoreId", rc."ContextKey", rc."ContextValue"
                FROM "dmscs"."DataStoreContext" rc
                JOIN "dmscs"."DataStore" ds ON rc."DataStoreId" = ds."Id"
                WHERE rc."DataStoreId" = ANY(@DataStoreIds) AND {TenantContext.TenantWhereClause("ds")}
                ORDER BY rc."DataStoreId", rc."ContextKey";
                """;
            var dataStoreContexts = await connection.QueryAsync<DataStoreContextResponse>(
                sql,
                new { DataStoreIds = dataStoreIds, TenantId }
            );

            return new DataStoreContextQueryByDataStoreIdsResult.Success(dataStoreContexts);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Get data store contexts by data store IDs failure");
            return new DataStoreContextQueryByDataStoreIdsResult.FailureUnknown(ex.Message);
        }
    }

    private async Task<bool> ContextExistsForTenant(NpgsqlConnection connection, long id)
    {
        var sql = $"""
            SELECT COUNT(1) FROM "dmscs"."DataStoreContext" rc
            JOIN "dmscs"."DataStore" ds ON rc."DataStoreId" = ds."Id"
            WHERE rc."Id" = @Id AND {TenantContext.TenantWhereClause("ds")};
            """;

        return await connection.ExecuteScalarAsync<int>(sql, new { Id = id, TenantId }) > 0;
    }
}
