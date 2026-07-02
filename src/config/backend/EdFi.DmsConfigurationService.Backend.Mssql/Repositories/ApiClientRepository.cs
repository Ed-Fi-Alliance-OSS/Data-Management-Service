// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.ApiClient;
using EdFi.DmsConfigurationService.DataModel.Model.Application;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Repositories;

public class ApiClientRepository(
    IOptions<DatabaseOptions> databaseOptions,
    ILogger<ApiClientRepository> logger,
    IAuditContext auditContext
) : IApiClientRepository
{
    public async Task<ApiClientInsertResult> InsertApiClient(
        ApiClientInsertCommand command,
        ApiClientCommand clientCommand
    )
    {
        await using var connection = new SqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            string sql = """
                INSERT INTO dmscs.ApiClient (ApplicationId, ClientId, ClientUuid, Name, IsApproved, CreatedBy)
                OUTPUT INSERTED.Id
                VALUES (@ApplicationId, @ClientId, @ClientUuid, @Name, @IsApproved, @CreatedBy);
                """;

            long apiClientId = await connection.ExecuteScalarAsync<long>(
                sql,
                new
                {
                    command.ApplicationId,
                    clientCommand.ClientId,
                    clientCommand.ClientUuid,
                    command.Name,
                    command.IsApproved,
                    CreatedBy = auditContext.GetCurrentUser(),
                },
                transaction
            );

            if (command.DataStoreIds.Length > 0)
            {
                sql = """
                    INSERT INTO dmscs.ApiClientDataStore (ApiClientId, DataStoreId, CreatedBy)
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
            return new ApiClientInsertResult.Success(apiClientId);
        }
        catch (SqlException ex) when (ex.IsForeignKeyViolation("fk_apiclient_application"))
        {
            logger.LogWarning(ex, "Application not found");
            await transaction.RollbackAsync();
            return new ApiClientInsertResult.FailureApplicationNotFound();
        }
        catch (SqlException ex) when (ex.IsForeignKeyViolation("fk_datastore"))
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

    private static string ResolveOrderByColumn(ApiClientQuery query) =>
        query.OrderBy is not null && OrderByColumns.TryGetValue(query.OrderBy, out var col) ? col : "Id";

    private static string BuildOrderByClause(ApiClientQuery query)
    {
        string col = ResolveOrderByColumn(query);
        return $"ORDER BY {col} {(query.IsDescending ? "DESC" : "ASC")}";
    }

    private static string BuildFilterClause(ApiClientQuery query)
    {
        var conditions = new List<string>();
        if (query.ApplicationId.HasValue)
        {
            conditions.Add("ApplicationId = @ApplicationId");
        }
        return conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : string.Empty;
    }

    public async Task<ApiClientQueryResult> QueryApiClient(ApiClientQuery query)
    {
        await using var connection = new SqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();
        try
        {
            string orderByClause = BuildOrderByClause(query);
            string filterClause = BuildFilterClause(query);
            string outerCol = ResolveOrderByColumn(query);
            string direction = query.IsDescending ? "DESC" : "ASC";
            string sql = $"""
                SELECT ac.Id, ac.ApplicationId, ac.ClientId, ac.ClientUuid, ac.Name, ac.IsApproved,
                       acd.DataStoreId
                FROM (SELECT * FROM dmscs.ApiClient {filterClause} {orderByClause} {query.BuildSqlServerPagingClause()}) AS ac
                LEFT OUTER JOIN dmscs.ApiClientDataStore acd ON ac.Id = acd.ApiClientId
                ORDER BY ac.{outerCol} {direction};
                """;

            var apiClients = await connection.QueryAsync<ApiClientResponse, long?, ApiClientResponse>(
                sql,
                (apiClient, dataStoreId) =>
                {
                    if (dataStoreId != null)
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
        await using var connection = new SqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();
        try
        {
            string sql = """
                SELECT ac.Id, ac.ApplicationId, ac.ClientId, ac.ClientUuid, ac.Name, ac.IsApproved,
                       acd.DataStoreId
                FROM dmscs.ApiClient ac
                LEFT OUTER JOIN dmscs.ApiClientDataStore acd ON ac.Id = acd.ApiClientId
                WHERE ac.ClientId = @ClientId;
                """;

            var apiClients = await connection.QueryAsync<ApiClientResponse, long?, ApiClientResponse>(
                sql,
                (apiClient, dataStoreId) =>
                {
                    if (dataStoreId != null)
                    {
                        apiClient.DataStoreIds.Add(dataStoreId.Value);
                    }
                    return apiClient;
                },
                param: new { ClientId = clientId },
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

            return returnApiClient != null
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
        await using var connection = new SqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();
        try
        {
            string sql = """
                SELECT ac.Id, ac.ApplicationId, ac.ClientId, ac.ClientUuid, ac.Name, ac.IsApproved,
                       acd.DataStoreId
                FROM dmscs.ApiClient ac
                LEFT OUTER JOIN dmscs.ApiClientDataStore acd ON ac.Id = acd.ApiClientId
                WHERE ac.Id = @Id;
                """;

            var apiClients = await connection.QueryAsync<ApiClientResponse, long?, ApiClientResponse>(
                sql,
                (apiClient, dataStoreId) =>
                {
                    if (dataStoreId != null)
                    {
                        apiClient.DataStoreIds.Add(dataStoreId.Value);
                    }
                    return apiClient;
                },
                param: new { Id = id },
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

            return returnApiClient != null
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
        await using var connection = new SqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            // Check if ApiClient exists
            string checkSql = "SELECT COUNT(1) FROM dmscs.ApiClient WHERE Id = @Id;";
            int exists = await connection.ExecuteScalarAsync<int>(checkSql, new { command.Id }, transaction);
            if (exists == 0)
            {
                await transaction.RollbackAsync();
                return new ApiClientUpdateResult.FailureNotFound();
            }

            // Update ApiClient record
            string updateSql = """
                UPDATE dmscs.ApiClient
                SET ApplicationId = @ApplicationId, Name = @Name, IsApproved = @IsApproved,
                    ClientUuid = COALESCE(@ClientUuid, ClientUuid),
                    LastModifiedAt = @LastModifiedAt, ModifiedBy = @ModifiedBy
                WHERE Id = @Id;
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
            string deleteSql = "DELETE FROM dmscs.ApiClientDataStore WHERE ApiClientId = @Id;";
            await connection.ExecuteAsync(deleteSql, new { command.Id }, transaction);

            // Insert new data store mappings
            if (command.DataStoreIds.Length > 0)
            {
                string insertSql = """
                    INSERT INTO dmscs.ApiClientDataStore (ApiClientId, DataStoreId, CreatedBy)
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
        catch (SqlException ex) when (ex.IsForeignKeyViolation("fk_apiclient_application"))
        {
            logger.LogWarning(ex, "Application not found");
            await transaction.RollbackAsync();
            return new ApiClientUpdateResult.FailureApplicationNotFound();
        }
        catch (SqlException ex) when (ex.IsForeignKeyViolation("fk_datastore"))
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
        await using var connection = new SqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            // Check if ApiClient exists
            string checkSql = "SELECT COUNT(1) FROM dmscs.ApiClient WHERE Id = @Id;";
            int exists = await connection.ExecuteScalarAsync<int>(checkSql, new { Id = id }, transaction);
            if (exists == 0)
            {
                await transaction.RollbackAsync();
                return new ApiClientDeleteResult.FailureNotFound();
            }

            // Delete data store mappings first (due to foreign key constraint)
            string deleteMappingsSql = "DELETE FROM dmscs.ApiClientDataStore WHERE ApiClientId = @Id;";
            await connection.ExecuteAsync(deleteMappingsSql, new { Id = id }, transaction);

            // Delete ApiClient record
            string deleteSql = "DELETE FROM dmscs.ApiClient WHERE Id = @Id;";
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
