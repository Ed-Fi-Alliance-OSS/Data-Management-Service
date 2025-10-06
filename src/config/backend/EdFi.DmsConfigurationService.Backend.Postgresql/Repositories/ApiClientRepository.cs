// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.ApiClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Repositories;

public class ApiClientRepository(
    IOptions<DatabaseOptions> databaseOptions,
    ILogger<ApiClientRepository> logger
) : IApiClientRepository
{
    public async Task<ApiClientQueryResult> QueryApiClient(PagingQuery query)
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();
        try
        {
            string sql = """
                SELECT ac.Id, ac.ApplicationId, ac.ClientId, ac.ClientUuid, acd.DmsInstanceId
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
                SELECT ac.Id, ac.ApplicationId, ac.ClientId, ac.ClientUuid, acd.DmsInstanceId
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
