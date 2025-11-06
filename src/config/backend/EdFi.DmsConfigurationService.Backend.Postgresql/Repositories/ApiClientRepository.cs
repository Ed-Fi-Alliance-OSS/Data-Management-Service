// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.ApiClient;
using EdFi.DmsConfigurationService.DataModel.Model.Application;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Repositories;

public class ApiClientRepository(
    IOptions<DatabaseOptions> databaseOptions,
    ILogger<ApiClientRepository> logger
) : IApiClientRepository
{
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
            string sql = """
                INSERT INTO dmscs.ApiClient (ApplicationId, ClientId, ClientUuid, Name, IsApproved)
                VALUES (@ApplicationId, @ClientId, @ClientUuid, @Name, @IsApproved)
                RETURNING Id;
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
                }
            );

            if (command.DmsInstanceIds.Length > 0)
            {
                sql = """
                    INSERT INTO dmscs.ApiClientDmsInstance (ApiClientId, DmsInstanceId)
                    VALUES (@ApiClientId, @DmsInstanceId);
                    """;

                var dmsInstanceMappings = command.DmsInstanceIds.Select(dmsInstanceId => new
                {
                    ApiClientId = apiClientId,
                    DmsInstanceId = dmsInstanceId,
                });

                await connection.ExecuteAsync(sql, dmsInstanceMappings);
            }

            await transaction.CommitAsync();
            return new ApiClientInsertResult.Success(apiClientId);
        }
        catch (PostgresException ex) when (ex.SqlState == "23503" && ex.Message.Contains("fk_application"))
        {
            logger.LogWarning(ex, "Application not found");
            await transaction.RollbackAsync();
            return new ApiClientInsertResult.FailureApplicationNotFound();
        }
        catch (PostgresException ex) when (ex.SqlState == "23503" && ex.Message.Contains("fk_dmsinstance"))
        {
            logger.LogWarning(ex, "DMS instance not found");
            await transaction.RollbackAsync();
            return new ApiClientInsertResult.FailureDmsInstanceNotFound();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Insert ApiClient failure");
            await transaction.RollbackAsync();
            return new ApiClientInsertResult.FailureUnknown(ex.Message);
        }
    }

    public async Task<ApiClientQueryResult> QueryApiClient(PagingQuery query)
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();
        try
        {
            string sql = """
                SELECT ac.Id, ac.ApplicationId, ac.ClientId, ac.ClientUuid, ac.Name, ac.IsApproved, acd.DmsInstanceId
                FROM (SELECT * FROM dmscs.ApiClient ORDER BY Id LIMIT @Limit OFFSET @Offset) AS ac
                LEFT OUTER JOIN dmscs.ApiClientDmsInstance acd ON ac.Id = acd.ApiClientId
                ORDER BY ac.Id;
                """;

            var apiClients = await connection.QueryAsync<ApiClientResponse, long?, ApiClientResponse>(
                sql,
                (apiClient, dmsInstanceId) =>
                {
                    if (dmsInstanceId != null)
                    {
                        apiClient.DmsInstanceIds.Add(dmsInstanceId.Value);
                    }
                    return apiClient;
                },
                param: query,
                splitOn: "DmsInstanceId"
            );

            var returnApiClients = apiClients
                .GroupBy(ac => ac.Id)
                .Select(g =>
                {
                    var grouped = g.First();
                    grouped.DmsInstanceIds = g.SelectMany(ac => ac.DmsInstanceIds).Distinct().ToList();
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
            string sql = """
                SELECT ac.Id, ac.ApplicationId, ac.ClientId, ac.ClientUuid, ac.Name, ac.IsApproved, acd.DmsInstanceId
                FROM dmscs.ApiClient ac
                LEFT OUTER JOIN dmscs.ApiClientDmsInstance acd ON ac.Id = acd.ApiClientId
                WHERE ac.ClientId = @ClientId;
                """;

            var apiClients = await connection.QueryAsync<ApiClientResponse, long?, ApiClientResponse>(
                sql,
                (apiClient, dmsInstanceId) =>
                {
                    if (dmsInstanceId != null)
                    {
                        apiClient.DmsInstanceIds.Add(dmsInstanceId.Value);
                    }
                    return apiClient;
                },
                param: new { ClientId = clientId },
                splitOn: "DmsInstanceId"
            );

            ApiClientResponse? returnApiClient = apiClients
                .GroupBy(ac => ac.Id)
                .Select(g =>
                {
                    var grouped = g.First();
                    grouped.DmsInstanceIds = g.SelectMany(ac => ac.DmsInstanceIds).Distinct().ToList();
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
}
