// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.ApiClient;
using EdFi.DmsConfigurationService.DataModel.Model.Application;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Repositories;

public class ApiClientRepository(
    IOptions<DatabaseOptions> databaseOptions,
    ILogger<ApiClientRepository> logger,
    IAuditContext auditContext,
    ITenantContextProvider tenantContextProvider
) : IApiClientRepository
{
    private TenantContext TenantContext => tenantContextProvider.Context;

    private long? TenantId => TenantContext is TenantContext.Multitenant mt ? mt.TenantId : null;

    /// <summary>
    /// SQL condition constraining an ApiClient row to the current tenant through its
    /// owning Application's Vendor.
    /// </summary>
    private string TenantScopedApplicationCondition(string? tableAlias = null)
    {
        var column = string.IsNullOrEmpty(tableAlias)
            ? "\"ApplicationId\""
            : $"{tableAlias}.\"ApplicationId\"";
        return $"""
            {column} IN (
                SELECT a."Id" FROM "dmscs"."Application" a
                JOIN "dmscs"."Vendor" v ON a."VendorId" = v."Id"
                WHERE {TenantContext.TenantWhereClause("v")}
            )
            """;
    }

    private string ApplicationInTenantExistsCondition =>
        $"""
            EXISTS (
                SELECT 1 FROM "dmscs"."Application" a
                JOIN "dmscs"."Vendor" v ON a."VendorId" = v."Id"
                WHERE a."Id" = @ApplicationId AND {TenantContext.TenantWhereClause("v")}
            )
            """;

    private async Task<bool> AllDataStoresInTenant(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long[] dataStoreIds
    )
    {
        string sql = $"""
            SELECT COUNT(1) FROM "dmscs"."DataStore"
            WHERE "Id" = ANY(@DataStoreIds) AND {TenantContext.TenantWhereClause()};
            """;
        int count = await connection.ExecuteScalarAsync<int>(
            sql,
            new { DataStoreIds = dataStoreIds, TenantId },
            transaction
        );
        return count == dataStoreIds.Distinct().Count();
    }

    public async Task<ApiClientInsertResult> InsertApiClient(
        ApiClientInsertCommand command,
        ApiClientCommand clientCommand
    )
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            string sql = $"""
                INSERT INTO "dmscs"."ApiClient" ("ApplicationId", "ClientId", "ClientUuid", "Name", "IsApproved", "CreatedBy")
                SELECT @ApplicationId, @ClientId, @ClientUuid, @Name, @IsApproved, @CreatedBy
                WHERE {ApplicationInTenantExistsCondition}
                RETURNING "Id";
                """;

            long? apiClientId = await connection.ExecuteScalarAsync<long?>(
                sql,
                new
                {
                    command.ApplicationId,
                    clientCommand.ClientId,
                    clientCommand.ClientUuid,
                    command.Name,
                    command.IsApproved,
                    CreatedBy = auditContext.GetCurrentUser(),
                    TenantId,
                },
                transaction
            );

            if (apiClientId is null)
            {
                logger.LogWarning("Application not found");
                await transaction.RollbackAsync();
                return new ApiClientInsertResult.FailureApplicationNotFound();
            }

            if (command.DataStoreIds.Length > 0)
            {
                if (!await AllDataStoresInTenant(connection, transaction, command.DataStoreIds))
                {
                    logger.LogWarning("Data store not found");
                    await transaction.RollbackAsync();
                    return new ApiClientInsertResult.FailureDataStoreNotFound();
                }

                sql = """
                    INSERT INTO "dmscs"."ApiClientDataStore" ("ApiClientId", "DataStoreId", "CreatedBy")
                    VALUES (@ApiClientId, @DataStoreId, @CreatedBy);
                    """;

                var currentUser = auditContext.GetCurrentUser();
                var dataStoreMappings = command.DataStoreIds.Select(dataStoreId => new
                {
                    ApiClientId = apiClientId,
                    DataStoreId = dataStoreId,
                    CreatedBy = currentUser,
                });

                await connection.ExecuteAsync(sql, dataStoreMappings, transaction);
            }

            await transaction.CommitAsync();
            return new ApiClientInsertResult.Success(apiClientId.Value);
        }
        catch (PostgresException ex)
            when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation
                && ex.ConstraintName == "FK_ApiClient_Application"
            )
        {
            logger.LogWarning(ex, "Application not found");
            await transaction.RollbackAsync();
            return new ApiClientInsertResult.FailureApplicationNotFound();
        }
        catch (PostgresException ex)
            when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation
                && ex.ConstraintName == "FK_ApiClientDataStore_DataStore"
            )
        {
            logger.LogWarning(ex, "Data store not found");
            await transaction.RollbackAsync();
            return new ApiClientInsertResult.FailureDataStoreNotFound();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Insert ApiClient failure");
            await transaction.RollbackAsync();
            return new ApiClientInsertResult.FailureUnknown(ex.Message);
        }
    }

    private static readonly IReadOnlyDictionary<string, string> OrderByColumns = new Dictionary<
        string,
        string
    >(StringComparer.OrdinalIgnoreCase)
    {
        ["id"] = "Id",
        ["applicationId"] = "ApplicationId",
        ["name"] = "Name",
    };

    private static string ResolveOrderByColumnName(ApiClientQuery query) =>
        query.OrderBy is not null && OrderByColumns.TryGetValue(query.OrderBy, out var col) ? col : "Id";

    private static string BuildOrderByClause(ApiClientQuery query, string? tableAlias = null) =>
        PostgresqlIdentifier.OrderBy(ResolveOrderByColumnName(query), query.IsDescending, tableAlias);

    private string BuildFilterClause(ApiClientQuery query)
    {
        var conditions = new List<string> { TenantScopedApplicationCondition() };
        if (query.ApplicationId.HasValue)
        {
            conditions.Add("\"ApplicationId\" = @ApplicationId");
        }
        return "WHERE " + string.Join(" AND ", conditions);
    }

    public async Task<ApiClientQueryResult> QueryApiClient(ApiClientQuery query)
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();
        try
        {
            string orderByClause = BuildOrderByClause(query);
            string filterClause = BuildFilterClause(query);
            string outerOrderByClause = BuildOrderByClause(query, "ac");
            string sql = $"""
                SELECT ac."Id", ac."ApplicationId", ac."ClientId", ac."ClientUuid", ac."Name", ac."IsApproved",
                       acd."DataStoreId"
                FROM (SELECT * FROM "dmscs"."ApiClient" {filterClause} {orderByClause} {query.BuildPagingClause()}) AS ac
                LEFT OUTER JOIN "dmscs"."ApiClientDataStore" acd ON ac."Id" = acd."ApiClientId"
                {outerOrderByClause};
                """;

            var apiClients = await connection.QueryAsync<ApiClientResponse, long?, ApiClientResponse>(
                sql,
                (apiClient, dataStoreId) =>
                {
                    if (dataStoreId is not null)
                    {
                        apiClient.DataStoreIds.Add(dataStoreId.Value);
                    }
                    return apiClient;
                },
                param: new
                {
                    query.Limit,
                    query.Offset,
                    query.ApplicationId,
                    TenantId,
                },
                splitOn: "DataStoreId"
            );

            var returnApiClients = apiClients
                .GroupBy(ac => ac.Id)
                .Select(g =>
                {
                    var grouped = g.First();
                    grouped.DataStoreIds = g.SelectMany(ac => ac.DataStoreIds).Distinct().ToList();
                    return grouped;
                })
                .ToList();

            return new ApiClientQueryResult.Success(returnApiClients);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Query ApiClient failure");
            return new ApiClientQueryResult.FailureUnknown(ex.Message);
        }
    }

    public async Task<ApiClientGetResult> GetApiClientByClientId(string clientId)
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();
        try
        {
            string sql = $"""
                SELECT ac."Id", ac."ApplicationId", ac."ClientId", ac."ClientUuid", ac."Name", ac."IsApproved",
                       acd."DataStoreId"
                FROM "dmscs"."ApiClient" ac
                LEFT OUTER JOIN "dmscs"."ApiClientDataStore" acd ON ac."Id" = acd."ApiClientId"
                WHERE ac."ClientId" = @ClientId AND {TenantScopedApplicationCondition("ac")};
                """;

            var apiClients = await connection.QueryAsync<ApiClientResponse, long?, ApiClientResponse>(
                sql,
                (apiClient, dataStoreId) =>
                {
                    if (dataStoreId is not null)
                    {
                        apiClient.DataStoreIds.Add(dataStoreId.Value);
                    }
                    return apiClient;
                },
                param: new { ClientId = clientId, TenantId },
                splitOn: "DataStoreId"
            );

            ApiClientResponse? returnApiClient = apiClients
                .GroupBy(ac => ac.Id)
                .Select(g =>
                {
                    var grouped = g.First();
                    grouped.DataStoreIds = g.SelectMany(ac => ac.DataStoreIds).Distinct().ToList();
                    return grouped;
                })
                .SingleOrDefault();

            return returnApiClient is not null
                ? new ApiClientGetResult.Success(returnApiClient)
                : new ApiClientGetResult.FailureNotFound();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Get ApiClient by ClientId failure");
            return new ApiClientGetResult.FailureUnknown(ex.Message);
        }
    }

    public async Task<ApiClientGetResult> GetApiClientById(long id)
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();
        try
        {
            string sql = $"""
                SELECT ac."Id", ac."ApplicationId", ac."ClientId", ac."ClientUuid", ac."Name", ac."IsApproved",
                       acd."DataStoreId"
                FROM "dmscs"."ApiClient" ac
                LEFT OUTER JOIN "dmscs"."ApiClientDataStore" acd ON ac."Id" = acd."ApiClientId"
                WHERE ac."Id" = @Id AND {TenantScopedApplicationCondition("ac")};
                """;

            var apiClients = await connection.QueryAsync<ApiClientResponse, long?, ApiClientResponse>(
                sql,
                (apiClient, dataStoreId) =>
                {
                    if (dataStoreId is not null)
                    {
                        apiClient.DataStoreIds.Add(dataStoreId.Value);
                    }
                    return apiClient;
                },
                param: new { Id = id, TenantId },
                splitOn: "DataStoreId"
            );

            ApiClientResponse? returnApiClient = apiClients
                .GroupBy(ac => ac.Id)
                .Select(g =>
                {
                    var grouped = g.First();
                    grouped.DataStoreIds = g.SelectMany(ac => ac.DataStoreIds).Distinct().ToList();
                    return grouped;
                })
                .SingleOrDefault();

            return returnApiClient is not null
                ? new ApiClientGetResult.Success(returnApiClient)
                : new ApiClientGetResult.FailureNotFound();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Get ApiClient by Id failure");
            return new ApiClientGetResult.FailureUnknown(ex.Message);
        }
    }

    public async Task<ApiClientUpdateResult> UpdateApiClient(ApiClientUpdateCommand command)
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            // Check if ApiClient exists within the current tenant
            string checkSql = $"""
                SELECT COUNT(1) FROM "dmscs"."ApiClient"
                WHERE "Id" = @Id AND {TenantScopedApplicationCondition()};
                """;
            int exists = await connection.ExecuteScalarAsync<int>(
                checkSql,
                new { command.Id, TenantId },
                transaction
            );
            if (exists == 0)
            {
                await transaction.RollbackAsync();
                return new ApiClientUpdateResult.FailureNotFound();
            }

            // Update ApiClient record; the target application must belong to the current tenant
            string updateSql = $"""
                UPDATE "dmscs"."ApiClient"
                SET "ApplicationId" = @ApplicationId, "Name" = @Name, "IsApproved" = @IsApproved,
                    "ClientUuid" = COALESCE(@ClientUuid, "ClientUuid"),
                    "LastModifiedAt" = @LastModifiedAt, "ModifiedBy" = @ModifiedBy
                WHERE "Id" = @Id AND {ApplicationInTenantExistsCondition};
                """;

            int updatedRows = await connection.ExecuteAsync(
                updateSql,
                new
                {
                    command.Id,
                    command.ApplicationId,
                    command.Name,
                    command.IsApproved,
                    ClientUuid = command.ClientUuid,
                    LastModifiedAt = auditContext.GetCurrentTimestamp(),
                    ModifiedBy = auditContext.GetCurrentUser(),
                    TenantId,
                },
                transaction
            );

            if (updatedRows == 0)
            {
                logger.LogWarning("Application not found");
                await transaction.RollbackAsync();
                return new ApiClientUpdateResult.FailureApplicationNotFound();
            }

            if (
                command.DataStoreIds.Length > 0
                && !await AllDataStoresInTenant(connection, transaction, command.DataStoreIds)
            )
            {
                logger.LogWarning("Data store not found");
                await transaction.RollbackAsync();
                return new ApiClientUpdateResult.FailureDataStoreNotFound();
            }

            // Delete existing data store mappings
            string deleteSql = """DELETE FROM "dmscs"."ApiClientDataStore" WHERE "ApiClientId" = @Id;""";
            await connection.ExecuteAsync(deleteSql, new { command.Id }, transaction);

            // Insert new data store mappings
            if (command.DataStoreIds.Length > 0)
            {
                string insertSql = """
                    INSERT INTO "dmscs"."ApiClientDataStore" ("ApiClientId", "DataStoreId", "CreatedBy")
                    VALUES (@ApiClientId, @DataStoreId, @CreatedBy);
                    """;

                var currentUser = auditContext.GetCurrentUser();
                var dataStoreMappings = command.DataStoreIds.Select(dataStoreId => new
                {
                    ApiClientId = command.Id,
                    DataStoreId = dataStoreId,
                    CreatedBy = currentUser,
                });

                await connection.ExecuteAsync(insertSql, dataStoreMappings, transaction);
            }

            await transaction.CommitAsync();
            return new ApiClientUpdateResult.Success();
        }
        catch (PostgresException ex)
            when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation
                && ex.ConstraintName == "FK_ApiClient_Application"
            )
        {
            logger.LogWarning(ex, "Application not found");
            await transaction.RollbackAsync();
            return new ApiClientUpdateResult.FailureApplicationNotFound();
        }
        catch (PostgresException ex)
            when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation
                && ex.ConstraintName == "FK_ApiClientDataStore_DataStore"
            )
        {
            logger.LogWarning(ex, "Data store not found");
            await transaction.RollbackAsync();
            return new ApiClientUpdateResult.FailureDataStoreNotFound();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Update ApiClient failure");
            await transaction.RollbackAsync();
            return new ApiClientUpdateResult.FailureUnknown(ex.Message);
        }
    }

    public async Task<ApiClientDeleteResult> DeleteApiClient(long id)
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            // Check if ApiClient exists within the current tenant
            string checkSql = $"""
                SELECT COUNT(1) FROM "dmscs"."ApiClient"
                WHERE "Id" = @Id AND {TenantScopedApplicationCondition()};
                """;
            int exists = await connection.ExecuteScalarAsync<int>(
                checkSql,
                new { Id = id, TenantId },
                transaction
            );
            if (exists == 0)
            {
                await transaction.RollbackAsync();
                return new ApiClientDeleteResult.FailureNotFound();
            }

            // Delete data store mappings first (due to foreign key constraint)
            string deleteMappingsSql =
                """DELETE FROM "dmscs"."ApiClientDataStore" WHERE "ApiClientId" = @Id;""";
            await connection.ExecuteAsync(deleteMappingsSql, new { Id = id }, transaction);

            // Delete ApiClient record
            string deleteSql = """DELETE FROM "dmscs"."ApiClient" WHERE "Id" = @Id;""";
            await connection.ExecuteAsync(deleteSql, new { Id = id }, transaction);

            await transaction.CommitAsync();
            return new ApiClientDeleteResult.Success();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Delete ApiClient failure");
            await transaction.RollbackAsync();
            return new ApiClientDeleteResult.FailureUnknown(ex.Message);
        }
    }
}
