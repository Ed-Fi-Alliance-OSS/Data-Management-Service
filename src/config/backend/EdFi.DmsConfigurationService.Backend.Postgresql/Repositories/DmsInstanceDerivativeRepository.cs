// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.DmsInstanceDerivative;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Repositories;

public class DmsInstanceDerivativeRepository(
    IOptions<DatabaseOptions> databaseOptions,
    ILogger<DmsInstanceDerivativeRepository> logger,
    IConnectionStringEncryptionService encryptionService
) : IDmsInstanceDerivativeRepository
{
    public async Task<DmsInstanceDerivativeInsertResult> InsertDmsInstanceDerivative(
        DmsInstanceDerivativeInsertCommand command
    )
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        try
        {
            var sql = """
                INSERT INTO dmscs.DmsInstanceDerivative (InstanceId, DerivativeType, ConnectionString)
                VALUES (@InstanceId, @DerivativeType, @ConnectionString)
                RETURNING Id;
                """;

            var parameters = new
            {
                command.InstanceId,
                command.DerivativeType,
                ConnectionString = encryptionService.Encrypt(command.ConnectionString),
            };

            var id = await connection.ExecuteScalarAsync<long>(sql, parameters);
            return new DmsInstanceDerivativeInsertResult.Success(id);
        }
        catch (PostgresException ex)
            when (ex.SqlState == "23503" && ex.Message.Contains("fk_dmsinstancederivative_instance"))
        {
            logger.LogWarning(ex, "Instance not found");
            return new DmsInstanceDerivativeInsertResult.FailureForeignKeyViolation();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Insert DmsInstanceDerivative failure");
            return new DmsInstanceDerivativeInsertResult.FailureUnknown(ex.Message);
        }
    }

    public async Task<DmsInstanceDerivativeQueryResult> QueryDmsInstanceDerivative(PagingQuery query)
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        try
        {
            var sql = """
                SELECT Id, InstanceId, DerivativeType, ConnectionString
                FROM dmscs.DmsInstanceDerivative
                ORDER BY Id
                LIMIT @Limit OFFSET @Offset;
                """;

            var results = await connection.QueryAsync<(
                long Id,
                long InstanceId,
                string DerivativeType,
                byte[]? ConnectionString
            )>(sql, query);

            var derivatives = results.Select(row => new DmsInstanceDerivativeResponse
            {
                Id = row.Id,
                InstanceId = row.InstanceId,
                DerivativeType = row.DerivativeType,
                ConnectionString = encryptionService.Decrypt(row.ConnectionString),
            });

            return new DmsInstanceDerivativeQueryResult.Success(derivatives);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Query DmsInstanceDerivative failure");
            return new DmsInstanceDerivativeQueryResult.FailureUnknown(ex.Message);
        }
    }

    public async Task<DmsInstanceDerivativeGetResult> GetDmsInstanceDerivative(long id)
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        try
        {
            var sql = """
                SELECT Id, InstanceId, DerivativeType, ConnectionString
                FROM dmscs.DmsInstanceDerivative
                WHERE Id = @Id;
                """;

            var result = await connection.QuerySingleOrDefaultAsync<(
                long Id,
                long InstanceId,
                string DerivativeType,
                byte[]? ConnectionString
            )?>(sql, new { Id = id });

            if (result == null)
            {
                return new DmsInstanceDerivativeGetResult.FailureNotFound();
            }

            var derivative = new DmsInstanceDerivativeResponse
            {
                Id = result.Value.Id,
                InstanceId = result.Value.InstanceId,
                DerivativeType = result.Value.DerivativeType,
                ConnectionString = encryptionService.Decrypt(result.Value.ConnectionString),
            };

            return new DmsInstanceDerivativeGetResult.Success(derivative);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Get DmsInstanceDerivative failure");
            return new DmsInstanceDerivativeGetResult.FailureUnknown(ex.Message);
        }
    }

    public async Task<DmsInstanceDerivativeUpdateResult> UpdateDmsInstanceDerivative(
        DmsInstanceDerivativeUpdateCommand command
    )
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        try
        {
            var sql = """
                UPDATE dmscs.DmsInstanceDerivative
                SET InstanceId = @InstanceId, DerivativeType = @DerivativeType, ConnectionString = @ConnectionString
                WHERE Id = @Id;
                """;

            var parameters = new
            {
                command.Id,
                command.InstanceId,
                command.DerivativeType,
                ConnectionString = encryptionService.Encrypt(command.ConnectionString),
            };

            var affectedRows = await connection.ExecuteAsync(sql, parameters);
            if (affectedRows == 0)
            {
                return new DmsInstanceDerivativeUpdateResult.FailureNotFound();
            }

            return new DmsInstanceDerivativeUpdateResult.Success();
        }
        catch (PostgresException ex)
            when (ex.SqlState == "23503" && ex.Message.Contains("fk_dmsinstancederivative_instance"))
        {
            logger.LogWarning(ex, "Instance not found");
            return new DmsInstanceDerivativeUpdateResult.FailureForeignKeyViolation();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Update DmsInstanceDerivative failure");
            return new DmsInstanceDerivativeUpdateResult.FailureUnknown(ex.Message);
        }
    }

    public async Task<DmsInstanceDerivativeDeleteResult> DeleteDmsInstanceDerivative(long id)
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        try
        {
            var sql = "DELETE FROM dmscs.DmsInstanceDerivative WHERE Id = @Id;";
            var affectedRows = await connection.ExecuteAsync(sql, new { Id = id });

            if (affectedRows > 0)
            {
                return new DmsInstanceDerivativeDeleteResult.Success();
            }

            return new DmsInstanceDerivativeDeleteResult.FailureNotFound();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Delete DmsInstanceDerivative failure");
            return new DmsInstanceDerivativeDeleteResult.FailureUnknown(ex.Message);
        }
    }
}
