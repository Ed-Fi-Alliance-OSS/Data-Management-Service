// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.DmsInstance;
using EdFi.DmsConfigurationService.DataModel.Model.DmsInstanceRouteContext;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Repositories;

public class DmsInstanceRepository(
    IOptions<DatabaseOptions> databaseOptions,
    ILogger<DmsInstanceRepository> logger,
    IConnectionStringEncryptionService encryptionService,
    IDmsInstanceRouteContextRepository routeContextRepository,
    IDmsInstanceDerivativeRepository derivativeRepository,
    IAuditContext auditContext
) : IDmsInstanceRepository
{
    public async Task<DmsInstanceInsertResult> InsertDmsInstance(DmsInstanceInsertCommand command)
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        try
        {
            var sql = """
                INSERT INTO dmscs.DmsInstance (InstanceType, InstanceName, ConnectionString, CreatedBy)
                VALUES (@InstanceType, @InstanceName, @ConnectionString, @CreatedBy)
                RETURNING Id;
                """;

            var parameters = new
            {
                command.InstanceType,
                command.InstanceName,
                ConnectionString = encryptionService.Encrypt(command.ConnectionString),
                CreatedBy = auditContext.GetCurrentUser()
            };

            var id = await connection.ExecuteScalarAsync<long>(sql, parameters);
            return new DmsInstanceInsertResult.Success(id);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Insert DmsInstance failure");
            return new DmsInstanceInsertResult.FailureUnknown(ex.Message);
        }
    }

    public async Task<DmsInstanceQueryResult> QueryDmsInstance(PagingQuery query)
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        try
        {
            var sql = """
                SELECT Id, InstanceType, InstanceName, ConnectionString,
                       CreatedAt, CreatedBy, LastModifiedAt, ModifiedBy
                FROM dmscs.DmsInstance
                ORDER BY Id
                LIMIT @Limit OFFSET @Offset;
                """;

            var results = await connection.QueryAsync<(
                long Id,
                string InstanceType,
                string InstanceName,
                byte[]? ConnectionString,
                DateTime CreatedAt,
                string? CreatedBy,
                DateTime? LastModifiedAt,
                string? ModifiedBy
            )>(sql, query);

            var instancesList = results.ToList();
            if (!instancesList.Any())
            {
                return new DmsInstanceQueryResult.Success([]);
            }

            // Fetch route contexts for all instances in this page
            var instanceIds = instancesList.Select(i => i.Id).ToList();
            var routeContextsResult = await routeContextRepository.GetInstanceRouteContextsByInstanceIds(
                instanceIds
            );

            // Group route contexts by instance ID
            var routeContextsByInstanceId = routeContextsResult switch
            {
                InstanceRouteContextQueryByInstanceIdsResult.Success success => success
                    .DmsInstanceRouteContextResponses.GroupBy(rc => rc.InstanceId)
                    .ToDictionary(
                        g => g.Key,
                        g =>
                            g.Select(rc => new DmsInstanceRouteContextItem(
                                    rc.Id,
                                    rc.InstanceId,
                                    rc.ContextKey,
                                    rc.ContextValue
                                ))
                                .ToList()
                    ),
                _ => new Dictionary<long, List<DmsInstanceRouteContextItem>>(),
            };

            // Fetch derivatives for all instances in this page
            var derivativesResult = await derivativeRepository.GetInstanceDerivativesByInstanceIds(
                instanceIds
            );

            // Group derivatives by instance ID
            var derivativesByInstanceId = derivativesResult switch
            {
                InstanceDerivativeQueryByInstanceIdsResult.Success success => success
                    .DmsInstanceDerivativeResponses.GroupBy(d => d.InstanceId)
                    .ToDictionary(
                        g => g.Key,
                        g =>
                            g.Select(d => new DmsInstanceDerivativeItem(
                                    d.Id,
                                    d.InstanceId,
                                    d.DerivativeType,
                                    d.ConnectionString
                                ))
                                .ToList()
                    ),
                _ => new Dictionary<long, List<DmsInstanceDerivativeItem>>(),
            };

            var instances = instancesList.Select(row => new DmsInstanceResponse
            {
                Id = row.Id,
                InstanceType = row.InstanceType,
                InstanceName = row.InstanceName,
                ConnectionString = encryptionService.Decrypt(row.ConnectionString),
                DmsInstanceRouteContexts = routeContextsByInstanceId.GetValueOrDefault(row.Id, []),
                DmsInstanceDerivatives = derivativesByInstanceId.GetValueOrDefault(row.Id, []),
                CreatedAt = row.CreatedAt,
                CreatedBy = row.CreatedBy,
                LastModifiedAt = row.LastModifiedAt,
                ModifiedBy = row.ModifiedBy
            });
            return new DmsInstanceQueryResult.Success(instances);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Query DmsInstance failure");
            return new DmsInstanceQueryResult.FailureUnknown(ex.Message);
        }
    }

    public async Task<DmsInstanceGetResult> GetDmsInstance(long id)
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        try
        {
            var sql = """
                SELECT Id, InstanceType, InstanceName, ConnectionString,
                       CreatedAt, CreatedBy, LastModifiedAt, ModifiedBy
                FROM dmscs.DmsInstance
                WHERE Id = @Id;
                """;

            var result = await connection.QuerySingleOrDefaultAsync<(
                long Id,
                string InstanceType,
                string InstanceName,
                byte[]? ConnectionString,
                DateTime CreatedAt,
                string? CreatedBy,
                DateTime? LastModifiedAt,
                string? ModifiedBy
            )?>(sql, new { Id = id });
            if (result == null)
            {
                return new DmsInstanceGetResult.FailureNotFound();
            }

            // Fetch route contexts for this instance
            var routeContextsResult = await routeContextRepository.GetInstanceRouteContextsByInstance(id);
            var routeContexts = routeContextsResult switch
            {
                InstanceRouteContextQueryByInstanceResult.Success success =>
                    success.DmsInstanceRouteContextResponses.Select(rc => new DmsInstanceRouteContextItem(
                        rc.Id,
                        rc.InstanceId,
                        rc.ContextKey,
                        rc.ContextValue
                    )),
                _ => [],
            };

            // Fetch derivatives for this instance
            var derivativesResult = await derivativeRepository.GetInstanceDerivativesByInstance(id);
            var derivatives = derivativesResult switch
            {
                InstanceDerivativeQueryByInstanceResult.Success success =>
                    success.DmsInstanceDerivativeResponses.Select(d => new DmsInstanceDerivativeItem(
                        d.Id,
                        d.InstanceId,
                        d.DerivativeType,
                        d.ConnectionString
                    )),
                _ => [],
            };

            var instance = new DmsInstanceResponse
            {
                Id = result.Value.Id,
                InstanceType = result.Value.InstanceType,
                InstanceName = result.Value.InstanceName,
                ConnectionString = encryptionService.Decrypt(result.Value.ConnectionString),
                DmsInstanceRouteContexts = routeContexts,
                DmsInstanceDerivatives = derivatives,
                CreatedAt = result.Value.CreatedAt,
                CreatedBy = result.Value.CreatedBy,
                LastModifiedAt = result.Value.LastModifiedAt,
                ModifiedBy = result.Value.ModifiedBy
            };
            return new DmsInstanceGetResult.Success(instance);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Get DmsInstance failure");
            return new DmsInstanceGetResult.FailureUnknown(ex.Message);
        }
    }

    public async Task<DmsInstanceUpdateResult> UpdateDmsInstance(DmsInstanceUpdateCommand command)
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        try
        {
            var sql = """
                UPDATE dmscs.DmsInstance
                SET InstanceType = @InstanceType, InstanceName = @InstanceName, ConnectionString = @ConnectionString,
                    LastModifiedAt = @LastModifiedAt, ModifiedBy = @ModifiedBy
                WHERE Id = @Id;
                """;

            var parameters = new
            {
                command.Id,
                command.InstanceType,
                command.InstanceName,
                ConnectionString = encryptionService.Encrypt(command.ConnectionString),
                LastModifiedAt = auditContext.GetCurrentTimestamp(),
                ModifiedBy = auditContext.GetCurrentUser()
            };

            var affectedRows = await connection.ExecuteAsync(sql, parameters);
            if (affectedRows == 0)
            {
                return new DmsInstanceUpdateResult.FailureNotExists();
            }
            return new DmsInstanceUpdateResult.Success();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Update DmsInstance failure");
            return new DmsInstanceUpdateResult.FailureUnknown(ex.Message);
        }
    }

    public async Task<DmsInstanceDeleteResult> DeleteDmsInstance(long id)
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        try
        {
            var sql = "DELETE FROM dmscs.DmsInstance WHERE Id = @Id;";
            var affectedRows = await connection.ExecuteAsync(sql, new { Id = id });
            if (affectedRows > 0)
            {
                return new DmsInstanceDeleteResult.Success();
            }
            return new DmsInstanceDeleteResult.FailureNotExists();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Delete DmsInstance failure");
            return new DmsInstanceDeleteResult.FailureUnknown(ex.Message);
        }
    }

    public async Task<DmsInstanceIdsExistResult> GetExistingDmsInstanceIds(long[] ids)
    {
        if (ids.Length == 0)
        {
            return new DmsInstanceIdsExistResult.Success([]);
        }

        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        try
        {
            var sql = """
                SELECT Id
                FROM dmscs.DmsInstance
                WHERE Id = ANY(@Ids);
                """;

            var existingIds = await connection.QueryAsync<long>(sql, new { Ids = ids });
            return new DmsInstanceIdsExistResult.Success([.. existingIds]);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Get existing DmsInstance IDs failure");
            return new DmsInstanceIdsExistResult.FailureUnknown(ex.Message);
        }
    }
}
