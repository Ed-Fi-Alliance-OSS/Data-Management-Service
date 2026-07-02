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
    public ApiClientRepository(
        IOptions<DatabaseOptions> databaseOptions,
        ILogger<ApiClientRepository> logger,
        IAuditContext auditContext
    )
        : this(databaseOptions, logger, auditContext, new TenantContextProvider()) { }

    private TenantContext TenantContext => tenantContextProvider.Context;

    private long? TenantId => TenantContext is TenantContext.Multitenant mt ? mt.TenantId : null;

    private async Task<bool> AreDataStoresVisible(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long[] dataStoreIds
    )
    {
        long[] distinctDataStoreIds = dataStoreIds.Distinct().ToArray();
        if (distinctDataStoreIds.Length == 0)
        {
            return true;
        }

        string sql = $"""
            SELECT COUNT(DISTINCT ds."Id")
            FROM "dmscs"."DataStore" ds
            WHERE ds."Id" = ANY(@DataStoreIds) AND {TenantContext.TenantWhereClause("ds")};
            """;

        int visibleDataStoreCount = await connection.ExecuteScalarAsync<int>(
            sql,
            new { DataStoreIds = distinctDataStoreIds, TenantId },
            transaction
        );

        return visibleDataStoreCount == distinctDataStoreIds.Length;
    }

    private async Task<bool> IsApplicationVisible(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long applicationId
    )
    {
        string sql = $"""
            SELECT EXISTS(
                SELECT 1
                FROM "dmscs"."Application" a
                JOIN "dmscs"."Vendor" v ON v."Id" = a."VendorId"
                WHERE a."Id" = @ApplicationId AND {TenantContext.TenantWhereClause("v")}
            );
            """;

        return await connection.ExecuteScalarAsync<bool>(
            sql,
            new { ApplicationId = applicationId, TenantId },
            transaction
        );
    }

    private async Task<bool> IsApiClientVisible(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long apiClientId
    )
    {
        string sql = $"""
            SELECT EXISTS(
                SELECT 1
                FROM "dmscs"."ApiClient" ac
                JOIN "dmscs"."Application" a ON a."Id" = ac."ApplicationId"
                JOIN "dmscs"."Vendor" v ON v."Id" = a."VendorId"
                WHERE ac."Id" = @ApiClientId AND {TenantContext.TenantWhereClause("v")}
            );
            """;

        return await connection.ExecuteScalarAsync<bool>(
            sql,
            new { ApiClientId = apiClientId, TenantId },
            transaction
        );
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
                SELECT a."Id", @ClientId, @ClientUuid, @Name, @IsApproved, @CreatedBy
                FROM "dmscs"."Application" a
                JOIN "dmscs"."Vendor" v ON v."Id" = a."VendorId"
                WHERE a."Id" = @ApplicationId AND {TenantContext.TenantWhereClause("v")}
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
                await transaction.RollbackAsync();
                return new ApiClientInsertResult.FailureApplicationNotFound();
            }

            if (command.DataStoreIds.Length > 0)
            {
                if (!await AreDataStoresVisible(connection, transaction, command.DataStoreIds))
                {
                    await transaction.RollbackAsync();
                    return new ApiClientInsertResult.FailureDataStoreNotFound();
                }

                sql = $"""
                    INSERT INTO "dmscs"."ApiClientDataStore" ("ApiClientId", "DataStoreId", "CreatedBy")
                    SELECT @ApiClientId, ds."Id", @CreatedBy
                    FROM "dmscs"."DataStore" ds
                    WHERE ds."Id" = ANY(@DataStoreIds) AND {TenantContext.TenantWhereClause("ds")};
                    """;

                await connection.ExecuteAsync(
                    sql,
                    new
                    {
                        ApiClientId = apiClientId.Value,
                        DataStoreIds = command.DataStoreIds.Distinct().ToArray(),
                        CreatedBy = auditContext.GetCurrentUser(),
                        TenantId,
                    },
                    transaction
                );
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
        ["id"] = "\"Id\"",
        ["applicationId"] = "\"ApplicationId\"",
        ["name"] = "\"Name\"",
    };

    private static string ResolveOrderByColumn(ApiClientQuery query) =>
        query.OrderBy is not null && OrderByColumns.TryGetValue(query.OrderBy, out var col) ? col : "\"Id\"";

    private static string QualifyColumn(string tableAlias, string quotedColumn) =>
        $"{tableAlias}.{quotedColumn}";

    private static string BuildOrderByClause(ApiClientQuery query, string tableAlias)
    {
        string col = ResolveOrderByColumn(query);
        return $"ORDER BY {QualifyColumn(tableAlias, col)} {(query.IsDescending ? "DESC" : "ASC")}";
    }

    private static string BuildFilterClause(ApiClientQuery query, string tableAlias)
    {
        var conditions = new List<string>();
        if (query.ApplicationId.HasValue)
        {
            conditions.Add($"{QualifyColumn(tableAlias, "\"ApplicationId\"")} = @ApplicationId");
        }
        return conditions.Count > 0 ? " AND " + string.Join(" AND ", conditions) : string.Empty;
    }

    public async Task<ApiClientQueryResult> QueryApiClient(ApiClientQuery query)
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();
        try
        {
            string orderByClause = BuildOrderByClause(query, "ac");
            string filterClause = BuildFilterClause(query, "ac");
            string outerCol = ResolveOrderByColumn(query);
            string direction = query.IsDescending ? "DESC" : "ASC";
            string sql = $"""
                SELECT ac."Id", ac."ApplicationId", ac."ClientId", ac."ClientUuid", ac."Name", ac."IsApproved",
                       acd."DataStoreId"
                FROM (
                    SELECT ac.*
                    FROM "dmscs"."ApiClient" ac
                    JOIN "dmscs"."Application" a ON a."Id" = ac."ApplicationId"
                    JOIN "dmscs"."Vendor" v ON v."Id" = a."VendorId"
                    WHERE {TenantContext.TenantWhereClause("v")}{filterClause}
                    {orderByClause}
                    {query.BuildPagingClause()}
                ) AS ac
                LEFT OUTER JOIN "dmscs"."ApiClientDataStore" acd ON ac."Id" = acd."ApiClientId"
                ORDER BY {QualifyColumn("ac", outerCol)} {direction};
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
                JOIN "dmscs"."Application" a ON a."Id" = ac."ApplicationId"
                JOIN "dmscs"."Vendor" v ON v."Id" = a."VendorId"
                LEFT OUTER JOIN "dmscs"."ApiClientDataStore" acd ON ac."Id" = acd."ApiClientId"
                WHERE ac."ClientId" = @ClientId AND {TenantContext.TenantWhereClause("v")};
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
                JOIN "dmscs"."Application" a ON a."Id" = ac."ApplicationId"
                JOIN "dmscs"."Vendor" v ON v."Id" = a."VendorId"
                LEFT OUTER JOIN "dmscs"."ApiClientDataStore" acd ON ac."Id" = acd."ApiClientId"
                WHERE ac."Id" = @Id AND {TenantContext.TenantWhereClause("v")};
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
            if (!await IsApiClientVisible(connection, transaction, command.Id))
            {
                await transaction.RollbackAsync();
                return new ApiClientUpdateResult.FailureNotFound();
            }

            if (!await IsApplicationVisible(connection, transaction, command.ApplicationId))
            {
                await transaction.RollbackAsync();
                return new ApiClientUpdateResult.FailureApplicationNotFound();
            }

            if (!await AreDataStoresVisible(connection, transaction, command.DataStoreIds))
            {
                await transaction.RollbackAsync();
                return new ApiClientUpdateResult.FailureDataStoreNotFound();
            }

            // Update ApiClient record
            string updateSql = """
                UPDATE "dmscs"."ApiClient"
                SET "ApplicationId" = @ApplicationId, "Name" = @Name, "IsApproved" = @IsApproved,
                    "ClientUuid" = COALESCE(@ClientUuid, "ClientUuid"),
                    "LastModifiedAt" = @LastModifiedAt, "ModifiedBy" = @ModifiedBy
                WHERE "Id" = @Id;
                """;

            await connection.ExecuteAsync(
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
                },
                transaction
            );

            // Delete existing data store mappings
            string deleteSql = "DELETE FROM \"dmscs\".\"ApiClientDataStore\" WHERE \"ApiClientId\" = @Id;";
            await connection.ExecuteAsync(deleteSql, new { command.Id }, transaction);

            // Insert new data store mappings
            if (command.DataStoreIds.Length > 0)
            {
                string insertSql = $"""
                    INSERT INTO "dmscs"."ApiClientDataStore" ("ApiClientId", "DataStoreId", "CreatedBy")
                    SELECT @ApiClientId, ds."Id", @CreatedBy
                    FROM "dmscs"."DataStore" ds
                    WHERE ds."Id" = ANY(@DataStoreIds) AND {TenantContext.TenantWhereClause("ds")};
                    """;

                await connection.ExecuteAsync(
                    insertSql,
                    new
                    {
                        ApiClientId = command.Id,
                        DataStoreIds = command.DataStoreIds.Distinct().ToArray(),
                        CreatedBy = auditContext.GetCurrentUser(),
                        TenantId,
                    },
                    transaction
                );
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
            if (!await IsApiClientVisible(connection, transaction, id))
            {
                await transaction.RollbackAsync();
                return new ApiClientDeleteResult.FailureNotFound();
            }

            // Delete data store mappings first (due to foreign key constraint)
            string deleteMappingsSql =
                "DELETE FROM \"dmscs\".\"ApiClientDataStore\" WHERE \"ApiClientId\" = @Id;";
            await connection.ExecuteAsync(deleteMappingsSql, new { Id = id }, transaction);

            // Delete ApiClient record
            string deleteSql = "DELETE FROM \"dmscs\".\"ApiClient\" WHERE \"Id\" = @Id;";
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
