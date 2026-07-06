// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.DataStoreDerivative;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Repositories;

public class DataStoreDerivativeRepository(
    IOptions<DatabaseOptions> databaseOptions,
    ILogger<DataStoreDerivativeRepository> logger,
    IConnectionStringEncryptionService encryptionService,
    IAuditContext auditContext,
    ITenantContextProvider tenantContextProvider
) : IDataStoreDerivativeRepository
{
    private TenantContext TenantContext => tenantContextProvider.Context;

    private long? TenantId => TenantContext is TenantContext.Multitenant mt ? mt.TenantId : null;

    private static readonly IReadOnlyDictionary<string, string> OrderByColumns = new Dictionary<
        string,
        string
    >(StringComparer.OrdinalIgnoreCase)
    {
        ["id"] = "Id",
        ["dataStoreId"] = "DataStoreId",
        ["derivativeType"] = "DerivativeType",
    };

    private static string BuildOrderByClause(PagingQuery query)
    {
        if (query.OrderBy is not null && OrderByColumns.TryGetValue(query.OrderBy, out var col))
        {
            return PostgresqlIdentifier.OrderBy(col, query.IsDescending);
        }

        return PostgresqlIdentifier.OrderBy("Id", isDescending: false);
    }

    public async Task<DataStoreDerivativeInsertResult> InsertDataStoreDerivative(
        DataStoreDerivativeInsertCommand command
    )
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        try
        {
            var sql = $"""
                INSERT INTO "dmscs"."DataStoreDerivative" ("DataStoreId", "DerivativeType", "ConnectionString", "CreatedBy")
                SELECT @DataStoreId, @DerivativeType, @ConnectionString, @CreatedBy
                WHERE EXISTS (
                    SELECT 1 FROM "dmscs"."DataStore"
                    WHERE "Id" = @DataStoreId AND {TenantContext.TenantWhereClause()}
                )
                RETURNING "Id";
                """;

            var parameters = new
            {
                command.DataStoreId,
                command.DerivativeType,
                ConnectionString = encryptionService.Encrypt(command.ConnectionString),
                CreatedBy = auditContext.GetCurrentUser(),
                TenantId,
            };

            var id = await connection.ExecuteScalarAsync<long?>(sql, parameters);
            if (id == null)
            {
                return new DataStoreDerivativeInsertResult.FailureForeignKeyViolation();
            }

            return new DataStoreDerivativeInsertResult.Success(id.Value);
        }
        catch (PostgresException ex)
            when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation
                && ex.ConstraintName == "FK_DataStoreDerivative_DataStore"
            )
        {
            logger.LogWarning(ex, "Data store not found");
            return new DataStoreDerivativeInsertResult.FailureForeignKeyViolation();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Insert DataStoreDerivative failure");
            return new DataStoreDerivativeInsertResult.FailureUnknown(ex.Message);
        }
    }

    public async Task<DataStoreDerivativeQueryResult> QueryDataStoreDerivative(PagingQuery query)
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        try
        {
            string orderByClause = BuildOrderByClause(query);
            var sql = $"""
                SELECT dd."Id", dd."DataStoreId", dd."DerivativeType", dd."ConnectionString"
                FROM "dmscs"."DataStoreDerivative" dd
                JOIN "dmscs"."DataStore" ds ON dd."DataStoreId" = ds."Id"
                WHERE {TenantContext.TenantWhereClause("ds")}
                {orderByClause}
                {query.BuildPagingClause()};
                """;

            var results = await connection.QueryAsync<(
                long Id,
                long DataStoreId,
                string DerivativeType,
                byte[]? ConnectionString
            )>(
                sql,
                new
                {
                    query.Limit,
                    query.Offset,
                    TenantId,
                }
            );

            var derivatives = results.Select(row => new DataStoreDerivativeResponse
            {
                Id = row.Id,
                DataStoreId = row.DataStoreId,
                DerivativeType = row.DerivativeType,
                ConnectionString = row.ConnectionString is null
                    ? null
                    : Convert.ToBase64String(row.ConnectionString),
            });

            return new DataStoreDerivativeQueryResult.Success(derivatives);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Query DataStoreDerivative failure");
            return new DataStoreDerivativeQueryResult.FailureUnknown(ex.Message);
        }
    }

    public async Task<DataStoreDerivativeGetResult> GetDataStoreDerivative(long id)
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        try
        {
            var sql = $"""
                SELECT dd."Id", dd."DataStoreId", dd."DerivativeType", dd."ConnectionString"
                FROM "dmscs"."DataStoreDerivative" dd
                JOIN "dmscs"."DataStore" ds ON dd."DataStoreId" = ds."Id"
                WHERE dd."Id" = @Id AND {TenantContext.TenantWhereClause("ds")};
                """;

            var result = await connection.QuerySingleOrDefaultAsync<(
                long Id,
                long DataStoreId,
                string DerivativeType,
                byte[]? ConnectionString
            )?>(sql, new { Id = id, TenantId });

            if (result is null)
            {
                return new DataStoreDerivativeGetResult.FailureNotFound();
            }

            var derivative = new DataStoreDerivativeResponse
            {
                Id = result.Value.Id,
                DataStoreId = result.Value.DataStoreId,
                DerivativeType = result.Value.DerivativeType,
                ConnectionString = result.Value.ConnectionString is null
                    ? null
                    : Convert.ToBase64String(result.Value.ConnectionString),
            };

            return new DataStoreDerivativeGetResult.Success(derivative);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Get DataStoreDerivative failure");
            return new DataStoreDerivativeGetResult.FailureUnknown(ex.Message);
        }
    }

    public async Task<DataStoreDerivativeUpdateResult> UpdateDataStoreDerivative(
        DataStoreDerivativeUpdateCommand command
    )
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();
        try
        {
            var sql = $"""
                UPDATE "dmscs"."DataStoreDerivative" dd
                SET "DataStoreId" = @DataStoreId, "DerivativeType" = @DerivativeType, "ConnectionString" = @ConnectionString,
                    "LastModifiedAt" = @LastModifiedAt, "ModifiedBy" = @ModifiedBy
                FROM "dmscs"."DataStore" ds
                WHERE dd."Id" = @Id
                  AND dd."DataStoreId" = ds."Id"
                  AND {TenantContext.TenantWhereClause("ds")}
                  AND EXISTS (
                      SELECT 1 FROM "dmscs"."DataStore"
                      WHERE "Id" = @DataStoreId AND {TenantContext.TenantWhereClause()}
                  );
                """;

            var parameters = new
            {
                command.Id,
                command.DataStoreId,
                command.DerivativeType,
                ConnectionString = encryptionService.Encrypt(command.ConnectionString),
                LastModifiedAt = auditContext.GetCurrentTimestamp(),
                ModifiedBy = auditContext.GetCurrentUser(),
                TenantId,
            };

            var affectedRows = await connection.ExecuteAsync(sql, parameters);
            if (affectedRows == 0)
            {
                var derivativeExists = await DerivativeExistsForTenant(connection, command.Id);
                if (!derivativeExists)
                {
                    return new DataStoreDerivativeUpdateResult.FailureNotFound();
                }

                return new DataStoreDerivativeUpdateResult.FailureForeignKeyViolation();
            }

            return new DataStoreDerivativeUpdateResult.Success();
        }
        catch (PostgresException ex)
            when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation
                && ex.ConstraintName == "FK_DataStoreDerivative_DataStore"
            )
        {
            logger.LogWarning(ex, "Data store not found");
            return new DataStoreDerivativeUpdateResult.FailureForeignKeyViolation();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Update DataStoreDerivative failure");
            return new DataStoreDerivativeUpdateResult.FailureUnknown(ex.Message);
        }
    }

    public async Task<DataStoreDerivativeDeleteResult> DeleteDataStoreDerivative(long id)
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        try
        {
            var sql = $"""
                DELETE FROM "dmscs"."DataStoreDerivative"
                WHERE "Id" = @Id
                  AND "DataStoreId" IN (
                      SELECT "Id" FROM "dmscs"."DataStore" WHERE {TenantContext.TenantWhereClause()}
                  );
                """;
            var affectedRows = await connection.ExecuteAsync(sql, new { Id = id, TenantId });

            if (affectedRows > 0)
            {
                return new DataStoreDerivativeDeleteResult.Success();
            }

            return new DataStoreDerivativeDeleteResult.FailureNotFound();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Delete DataStoreDerivative failure");
            return new DataStoreDerivativeDeleteResult.FailureUnknown(ex.Message);
        }
    }

    public async Task<DataStoreDerivativeQueryByDataStoreResult> GetDataStoreDerivativesByDataStore(
        long dataStoreId
    )
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        try
        {
            var sql = $"""
                SELECT dd."Id", dd."DataStoreId", dd."DerivativeType", dd."ConnectionString"
                FROM "dmscs"."DataStoreDerivative" dd
                JOIN "dmscs"."DataStore" ds ON dd."DataStoreId" = ds."Id"
                WHERE dd."DataStoreId" = @DataStoreId AND {TenantContext.TenantWhereClause("ds")}
                ORDER BY dd."Id";
                """;

            var results = await connection.QueryAsync<(
                long Id,
                long DataStoreId,
                string DerivativeType,
                byte[]? ConnectionString
            )>(sql, new { DataStoreId = dataStoreId, TenantId });

            var derivatives = results.Select(row => new DataStoreDerivativeResponse
            {
                Id = row.Id,
                DataStoreId = row.DataStoreId,
                DerivativeType = row.DerivativeType,
                ConnectionString = row.ConnectionString is null
                    ? null
                    : Convert.ToBase64String(row.ConnectionString),
            });

            return new DataStoreDerivativeQueryByDataStoreResult.Success(derivatives);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Get data store derivatives by data store failure");
            return new DataStoreDerivativeQueryByDataStoreResult.FailureUnknown(ex.Message);
        }
    }

    public async Task<DataStoreDerivativeQueryByDataStoreIdsResult> GetDataStoreDerivativesByDataStoreIds(
        List<long> dataStoreIds
    )
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        try
        {
            var sql = $"""
                SELECT dd."Id", dd."DataStoreId", dd."DerivativeType", dd."ConnectionString"
                FROM "dmscs"."DataStoreDerivative" dd
                JOIN "dmscs"."DataStore" ds ON dd."DataStoreId" = ds."Id"
                WHERE dd."DataStoreId" = ANY(@DataStoreIds) AND {TenantContext.TenantWhereClause("ds")}
                ORDER BY dd."DataStoreId", dd."Id";
                """;

            var results = await connection.QueryAsync<(
                long Id,
                long DataStoreId,
                string DerivativeType,
                byte[]? ConnectionString
            )>(sql, new { DataStoreIds = dataStoreIds, TenantId });

            var derivatives = results.Select(row => new DataStoreDerivativeResponse
            {
                Id = row.Id,
                DataStoreId = row.DataStoreId,
                DerivativeType = row.DerivativeType,
                ConnectionString = row.ConnectionString is null
                    ? null
                    : Convert.ToBase64String(row.ConnectionString),
            });

            return new DataStoreDerivativeQueryByDataStoreIdsResult.Success(derivatives);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Get data store derivatives by data store IDs failure");
            return new DataStoreDerivativeQueryByDataStoreIdsResult.FailureUnknown(ex.Message);
        }
    }

    private async Task<bool> DerivativeExistsForTenant(NpgsqlConnection connection, long id)
    {
        var sql = $"""
            SELECT COUNT(1) FROM "dmscs"."DataStoreDerivative" dd
            JOIN "dmscs"."DataStore" ds ON dd."DataStoreId" = ds."Id"
            WHERE dd."Id" = @Id AND {TenantContext.TenantWhereClause("ds")};
            """;

        return await connection.ExecuteScalarAsync<int>(sql, new { Id = id, TenantId }) > 0;
    }
}
