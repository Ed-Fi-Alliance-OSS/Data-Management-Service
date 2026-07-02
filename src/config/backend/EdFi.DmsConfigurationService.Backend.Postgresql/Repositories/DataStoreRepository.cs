// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.Application;
using EdFi.DmsConfigurationService.DataModel.Model.DataStore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Repositories;

public class DataStoreRepository(
    IOptions<DatabaseOptions> databaseOptions,
    ILogger<DataStoreRepository> logger,
    IConnectionStringEncryptionService encryptionService,
    IDataStoreContextRepository contextRepository,
    IDataStoreDerivativeRepository derivativeRepository,
    IAuditContext auditContext,
    ITenantContextProvider tenantContextProvider
) : IDataStoreRepository
{
    private TenantContext TenantContext => tenantContextProvider.Context;

    private long? TenantId => TenantContext is TenantContext.Multitenant mt ? mt.TenantId : null;

    public async Task<DataStoreInsertResult> InsertDataStore(DataStoreInsertCommand command)
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        try
        {
            var sql = """
                INSERT INTO "dmscs"."DataStore" ("DataStoreType", "Name", "ConnectionString", "CreatedBy", "TenantId")
                VALUES (@DataStoreType, @Name, @ConnectionString, @CreatedBy, @TenantId)
                RETURNING "Id";
                """;

            var parameters = new
            {
                command.DataStoreType,
                command.Name,
                ConnectionString = encryptionService.Encrypt(command.ConnectionString),
                CreatedBy = auditContext.GetCurrentUser(),
                TenantId,
            };

            var id = await connection.ExecuteScalarAsync<long>(sql, parameters);
            return new DataStoreInsertResult.Success(id);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Insert DataStore failure");
            return new DataStoreInsertResult.FailureUnknown(ex.Message);
        }
    }

    private static readonly IReadOnlyDictionary<string, string> OrderByColumns = new Dictionary<
        string,
        string
    >(StringComparer.OrdinalIgnoreCase)
    {
        ["id"] = "Id",
        ["dataStoreType"] = "DataStoreType",
        ["name"] = "Name",
    };

    private static string BuildOrderByClause(DataStoreQuery query)
    {
        if (query.OrderBy is not null && OrderByColumns.TryGetValue(query.OrderBy, out var col))
        {
            return PostgresqlIdentifier.OrderBy(col, query.IsDescending);
        }
        return PostgresqlIdentifier.OrderBy("Id", isDescending: false);
    }

    private static string BuildFilterClause(DataStoreQuery query)
    {
        var conditions = new List<string>();
        if (query.Id.HasValue)
        {
            conditions.Add("\"Id\" = @Id");
        }
        if (query.Name is not null)
        {
            conditions.Add("\"Name\" = @Name");
        }
        if (query.DataStoreType is not null)
        {
            conditions.Add("\"DataStoreType\" = @DataStoreType");
        }
        return conditions.Count > 0 ? " AND " + string.Join(" AND ", conditions) : string.Empty;
    }

    public async Task<DataStoreQueryResult> QueryDataStore(DataStoreQuery query)
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        try
        {
            string orderByClause = BuildOrderByClause(query);
            string filterClause = BuildFilterClause(query);
            var sql = $"""
                SELECT "Id", "DataStoreType", "Name", "ConnectionString", "TenantId"
                FROM "dmscs"."DataStore"
                WHERE {TenantContext.TenantWhereClause()}{filterClause}
                {orderByClause}
                {query.BuildPagingClause()};
                """;

            var results = await connection.QueryAsync<(
                long Id,
                string DataStoreType,
                string Name,
                byte[]? ConnectionString,
                long? TenantId
            )>(
                sql,
                new
                {
                    query.Limit,
                    query.Offset,
                    TenantId,
                    query.Id,
                    query.Name,
                    query.DataStoreType,
                }
            );

            var dataStoreList = results.ToList();
            if (!dataStoreList.Any())
            {
                return new DataStoreQueryResult.Success([]);
            }

            var dataStoreIds = dataStoreList.Select(i => i.Id).ToList();

            var contextResult = await contextRepository.GetDataStoreContextsByDataStoreIds(dataStoreIds);
            var contextsByDataStoreId = contextResult switch
            {
                DataStoreContextQueryByDataStoreIdsResult.Success success => success
                    .DataStoreContextResponses.GroupBy(rc => rc.DataStoreId)
                    .ToDictionary(
                        g => g.Key,
                        g =>
                            g.Select(rc => new DataStoreContextItem(
                                    rc.Id,
                                    rc.DataStoreId,
                                    rc.ContextKey,
                                    rc.ContextValue
                                ))
                                .ToList()
                    ),
                _ => new Dictionary<long, List<DataStoreContextItem>>(),
            };

            var derivativesResult = await derivativeRepository.GetDataStoreDerivativesByDataStoreIds(
                dataStoreIds
            );
            var derivativesByDataStoreId = derivativesResult switch
            {
                DataStoreDerivativeQueryByDataStoreIdsResult.Success success => success
                    .DataStoreDerivativeResponses.GroupBy(d => d.DataStoreId)
                    .ToDictionary(
                        g => g.Key,
                        g =>
                            g.Select(d => new DataStoreDerivativeItem(
                                    d.Id,
                                    d.DataStoreId,
                                    d.DerivativeType,
                                    d.ConnectionString
                                ))
                                .ToList()
                    ),
                _ => new Dictionary<long, List<DataStoreDerivativeItem>>(),
            };

            var dataStores = dataStoreList.Select(row => new DataStoreResponse
            {
                Id = row.Id,
                DataStoreType = row.DataStoreType,
                Name = row.Name,
                ConnectionString = row.ConnectionString is null
                    ? null
                    : Convert.ToBase64String(row.ConnectionString),
                DataStoreContexts = contextsByDataStoreId.GetValueOrDefault(row.Id, []),
                DataStoreDerivatives = derivativesByDataStoreId.GetValueOrDefault(row.Id, []),
                TenantId = row.TenantId,
            });
            return new DataStoreQueryResult.Success(dataStores);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Query DataStore failure");
            return new DataStoreQueryResult.FailureUnknown(ex.Message);
        }
    }

    public async Task<DataStoreGetResult> GetDataStore(long id)
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        try
        {
            var sql = $"""
                SELECT "Id", "DataStoreType", "Name", "ConnectionString", "TenantId"
                FROM "dmscs"."DataStore"
                WHERE "Id" = @Id AND {TenantContext.TenantWhereClause()};
                """;

            var result = await connection.QuerySingleOrDefaultAsync<(
                long Id,
                string DataStoreType,
                string Name,
                byte[]? ConnectionString,
                long? TenantId
            )?>(sql, new { Id = id, TenantId });
            if (result is null)
            {
                return new DataStoreGetResult.FailureNotFound();
            }

            var contextResult = await contextRepository.GetDataStoreContextsByDataStore(id);
            var contexts = contextResult switch
            {
                DataStoreContextQueryByDataStoreResult.Success success =>
                    success.DataStoreContextResponses.Select(rc => new DataStoreContextItem(
                        rc.Id,
                        rc.DataStoreId,
                        rc.ContextKey,
                        rc.ContextValue
                    )),
                _ => [],
            };

            var derivativesResult = await derivativeRepository.GetDataStoreDerivativesByDataStore(id);
            var derivatives = derivativesResult switch
            {
                DataStoreDerivativeQueryByDataStoreResult.Success success =>
                    success.DataStoreDerivativeResponses.Select(d => new DataStoreDerivativeItem(
                        d.Id,
                        d.DataStoreId,
                        d.DerivativeType,
                        d.ConnectionString
                    )),
                _ => [],
            };

            var dataStore = new DataStoreResponse
            {
                Id = result.Value.Id,
                DataStoreType = result.Value.DataStoreType,
                Name = result.Value.Name,
                ConnectionString = result.Value.ConnectionString is null
                    ? null
                    : Convert.ToBase64String(result.Value.ConnectionString),
                DataStoreContexts = contexts,
                DataStoreDerivatives = derivatives,
                TenantId = result.Value.TenantId,
            };
            return new DataStoreGetResult.Success(dataStore);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Get DataStore failure");
            return new DataStoreGetResult.FailureUnknown(ex.Message);
        }
    }

    public async Task<DataStoreUpdateResult> UpdateDataStore(DataStoreUpdateCommand command)
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        try
        {
            var sql = $"""
                UPDATE "dmscs"."DataStore"
                SET "DataStoreType" = @DataStoreType, "Name" = @Name, "ConnectionString" = @ConnectionString,
                    "LastModifiedAt" = @LastModifiedAt, "ModifiedBy" = @ModifiedBy
                WHERE "Id" = @Id AND {TenantContext.TenantWhereClause()};
                """;

            var parameters = new
            {
                command.Id,
                command.DataStoreType,
                command.Name,
                ConnectionString = encryptionService.Encrypt(command.ConnectionString),
                LastModifiedAt = auditContext.GetCurrentTimestamp(),
                ModifiedBy = auditContext.GetCurrentUser(),
                TenantId,
            };

            var affectedRows = await connection.ExecuteAsync(sql, parameters);
            if (affectedRows == 0)
            {
                return new DataStoreUpdateResult.FailureNotExists();
            }
            return new DataStoreUpdateResult.Success();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Update DataStore failure");
            return new DataStoreUpdateResult.FailureUnknown(ex.Message);
        }
    }

    public async Task<DataStoreDeleteResult> DeleteDataStore(long id)
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        try
        {
            var sql =
                $"DELETE FROM \"dmscs\".\"DataStore\" WHERE \"Id\" = @Id AND {TenantContext.TenantWhereClause()};";

            var affectedRows = await connection.ExecuteAsync(sql, new { Id = id, TenantId });
            if (affectedRows > 0)
            {
                return new DataStoreDeleteResult.Success();
            }
            return new DataStoreDeleteResult.FailureNotExists();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Delete DataStore failure");
            return new DataStoreDeleteResult.FailureUnknown(ex.Message);
        }
    }

    public async Task<DataStoreIdsExistResult> GetExistingDataStoreIds(long[] ids)
    {
        if (ids.Length == 0)
        {
            return new DataStoreIdsExistResult.Success([]);
        }

        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        try
        {
            var sql = $"""
                SELECT "Id"
                FROM "dmscs"."DataStore"
                WHERE "Id" = ANY(@Ids) AND {TenantContext.TenantWhereClause()};
                """;

            var existingIds = await connection.QueryAsync<long>(sql, new { Ids = ids, TenantId });
            return new DataStoreIdsExistResult.Success([.. existingIds]);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Get existing DataStore IDs failure");
            return new DataStoreIdsExistResult.FailureUnknown(ex.Message);
        }
    }

    public async Task<ApplicationByDataStoreQueryResult> QueryApplicationByDataStore(
        long dataStoreId,
        PagingQuery query
    )
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        try
        {
            var sql = $"""
                SELECT application.*, aeo."EducationOrganizationId", acds."DataStoreId"
                FROM (
                    SELECT DISTINCT a."Id", a."ApplicationName", a."ClaimSetName", a."VendorId",
                        -- Enabled: application is enabled only if ALL its ApiClients linked to this data store are approved
                        (SELECT COALESCE(BOOL_AND(ac2."IsApproved"), true)
                         FROM "dmscs"."ApiClient" ac2
                         JOIN "dmscs"."ApiClientDataStore" acds2 ON acds2."ApiClientId" = ac2."Id"
                         WHERE ac2."ApplicationId" = a."Id" AND acds2."DataStoreId" = @DataStoreId) AS "Enabled"
                    FROM "dmscs"."ApiClientDataStore" acds
                    JOIN "dmscs"."ApiClient" ac ON ac."Id" = acds."ApiClientId"
                    JOIN "dmscs"."Application" a ON a."Id" = ac."ApplicationId"
                    JOIN "dmscs"."DataStore" ds ON ds."Id" = acds."DataStoreId"
                    WHERE acds."DataStoreId" = @DataStoreId AND {TenantContext.TenantWhereClause(
                    tableAlias: "ds"
                )}
                    ORDER BY a."Id" LIMIT @Limit OFFSET @Offset
                ) application
                LEFT JOIN "dmscs"."ApplicationEducationOrganization" aeo ON aeo."ApplicationId" = application."Id"
                JOIN "dmscs"."ApiClient" ac ON ac."ApplicationId" = application."Id"
                JOIN "dmscs"."ApiClientDataStore" acds ON acds."ApiClientId" = ac."Id"
                JOIN "dmscs"."DataStore" ds ON ds."Id" = acds."DataStoreId"
                WHERE {TenantContext.TenantWhereClause(tableAlias: "ds")}
                """;

            var parameters = new
            {
                dataStoreId,
                query.Limit,
                query.Offset,
                TenantId,
            };

            var rows = await connection.QueryAsync<(
                long Id,
                string ApplicationName,
                string ClaimSetName,
                long VendorId,
                bool Enabled,
                long? EducationOrganizationId,
                long DataStoreId
            )>(sql, parameters);

            var applications = rows.GroupBy(row => row.Id)
                .Select(group =>
                {
                    var application = group.First();
                    return new ApplicationResponse()
                    {
                        Id = application.Id,
                        ApplicationName = application.ApplicationName,
                        ClaimSetName = application.ClaimSetName,
                        VendorId = application.VendorId,
                        Enabled = application.Enabled,
                        EducationOrganizationIds = group
                            .Where(row => row.EducationOrganizationId is not null)
                            .Select(row => row.EducationOrganizationId!.Value)
                            .Distinct()
                            .ToList(),
                        DataStoreIds = group.Select(row => row.DataStoreId).Distinct().ToList(),
                    };
                })
                .ToList();

            if (applications.Count > 0)
            {
                var applicationIds = applications.Select(a => a.Id).ToArray();
                string sqlProfiles = """
                        SELECT
                            ap."ApplicationId",
                            ap."ProfileId"
                        FROM "dmscs"."ApplicationProfile" ap
                        WHERE ap."ApplicationId" = ANY(@ApplicationIds);
                    """;

                var profileRows = await connection.QueryAsync<(long ApplicationId, long ProfileId)>(
                    sqlProfiles,
                    new { ApplicationIds = applicationIds }
                );

                foreach (var profileRow in profileRows)
                {
                    var app = applications.Find(a => a.Id == profileRow.ApplicationId);
                    if (app is not null && !app.ProfileIds.Contains(profileRow.ProfileId))
                    {
                        app.ProfileIds.Add(profileRow.ProfileId);
                    }
                }
            }

            if (applications.Count == 0)
            {
                var dataStoreExists = await connection.ExecuteScalarAsync<bool>(
                    $"SELECT EXISTS(SELECT 1 FROM \"dmscs\".\"DataStore\" WHERE \"Id\" = @dataStoreId AND {TenantContext.TenantWhereClause()})",
                    new { dataStoreId, TenantId }
                );

                if (!dataStoreExists)
                {
                    return new ApplicationByDataStoreQueryResult.FailureNotExists();
                }
            }

            return new ApplicationByDataStoreQueryResult.Success(applications);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Query ApplicationByDataStore failure");
            return new ApplicationByDataStoreQueryResult.FailureUnknown(ex.Message);
        }
    }
}
