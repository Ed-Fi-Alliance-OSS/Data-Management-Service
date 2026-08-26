// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Text.Json;
using Dapper;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.OwnershipToken;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Repositories;

public class OwnershipTokenRepository(
    IOptions<DatabaseOptions> databaseOptions,
    ILogger<OwnershipTokenRepository> logger,
    IAuditContext auditContext,
    ITenantContextProvider tenantContextProvider
) : IOwnershipTokenRepository
{
    private TenantContext TenantContext => tenantContextProvider.Context;

    private long? TenantId => TenantContext is TenantContext.Multitenant mt ? mt.TenantId : null;

    /// <summary>
    /// SQL condition constraining an ApiClient row to the current tenant through its
    /// owning Application's Vendor.
    /// </summary>
    private string TenantScopedApplicationCondition(string? tableAlias = null)
    {
        var column = string.IsNullOrEmpty(tableAlias) ? "ApplicationId" : $"{tableAlias}.ApplicationId";
        return $"""
            {column} IN (
                SELECT a.Id FROM dmscs.Application a
                JOIN dmscs.Vendor v ON a.VendorId = v.Id
                WHERE {TenantContext.TenantWhereClause("v")}
            )
            """;
    }

    private static readonly IReadOnlyDictionary<string, string> OrderByColumns = new Dictionary<
        string,
        string
    >(StringComparer.OrdinalIgnoreCase)
    {
        ["id"] = "Id",
        ["description"] = "Description",
    };

    private static string BuildOrderByClause(OwnershipTokenQuery query)
    {
        if (query.OrderBy is not null && OrderByColumns.TryGetValue(query.OrderBy, out string? column))
        {
            return $"ORDER BY ot.{column} {(query.IsDescending ? "DESC" : "ASC")}";
        }

        return "ORDER BY ot.Id ASC";
    }

    public async Task<OwnershipTokenInsertResult> InsertOwnershipToken(OwnershipTokenInsertCommand command)
    {
        await using var connection = new SqlConnection(databaseOptions.Value.DatabaseConnection);
        try
        {
            string sql = """
                INSERT INTO dmscs.OwnershipToken (Description, CreatedBy, TenantId)
                OUTPUT INSERTED.Id
                VALUES (@Description, @CreatedBy, @TenantId);
                """;

            var id = await connection.ExecuteScalarAsync<int>(
                sql,
                new
                {
                    command.Description,
                    CreatedBy = auditContext.GetCurrentUser(),
                    TenantId,
                }
            );
            return new OwnershipTokenInsertResult.Success(id);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Insert OwnershipToken failure");
            return new OwnershipTokenInsertResult.FailureUnknown(ex.Message);
        }
    }

    public async Task<OwnershipTokenQueryResult> QueryOwnershipTokens(OwnershipTokenQuery query)
    {
        await using var connection = new SqlConnection(databaseOptions.Value.DatabaseConnection);
        try
        {
            string orderByClause = BuildOrderByClause(query);
            string sql = $"""
                SELECT ot.Id, ot.Description
                FROM dmscs.OwnershipToken ot
                WHERE {TenantContext.TenantWhereClause("ot")}
                {orderByClause}
                {query.BuildSqlServerPagingClause()};
                """;

            var ownershipTokens = await connection.QueryAsync<OwnershipTokenResponse>(
                sql,
                new
                {
                    query.Limit,
                    query.Offset,
                    TenantId,
                }
            );

            return new OwnershipTokenQueryResult.Success(ownershipTokens.ToList());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Query OwnershipToken failure");
            return new OwnershipTokenQueryResult.FailureUnknown(ex.Message);
        }
    }

    public async Task<OwnershipTokenGetResult> GetOwnershipToken(int id)
    {
        await using var connection = new SqlConnection(databaseOptions.Value.DatabaseConnection);
        try
        {
            string sql = $"""
                SELECT ot.Id, ot.Description
                FROM dmscs.OwnershipToken ot
                WHERE ot.Id = @Id AND {TenantContext.TenantWhereClause("ot")};
                """;

            var ownershipToken = await connection.QuerySingleOrDefaultAsync<OwnershipTokenResponse>(
                sql,
                new { Id = id, TenantId }
            );

            return ownershipToken is not null
                ? new OwnershipTokenGetResult.Success(ownershipToken)
                : new OwnershipTokenGetResult.FailureNotFound();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Get OwnershipToken failure");
            return new OwnershipTokenGetResult.FailureUnknown(ex.Message);
        }
    }

    public async Task<OwnershipTokenUpdateResult> UpdateOwnershipToken(OwnershipTokenUpdateCommand command)
    {
        await using var connection = new SqlConnection(databaseOptions.Value.DatabaseConnection);
        try
        {
            string sql = $"""
                UPDATE dmscs.OwnershipToken
                SET Description = @Description,
                    LastModifiedAt = @LastModifiedAt,
                    ModifiedBy = @ModifiedBy
                WHERE Id = @Id AND {TenantContext.TenantWhereClause()};
                """;

            int affectedRows = await connection.ExecuteAsync(
                sql,
                new
                {
                    command.Id,
                    command.Description,
                    LastModifiedAt = auditContext.GetCurrentTimestamp(),
                    ModifiedBy = auditContext.GetCurrentUser(),
                    TenantId,
                }
            );

            return affectedRows > 0
                ? new OwnershipTokenUpdateResult.Success()
                : new OwnershipTokenUpdateResult.FailureNotFound();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Update OwnershipToken failure");
            return new OwnershipTokenUpdateResult.FailureUnknown(ex.Message);
        }
    }

    public async Task<ApiClientOwnershipGetResult> GetApiClientOwnership(int apiClientId)
    {
        await using var connection = new SqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();
        try
        {
            string apiClientSql = $"""
                SELECT ac.Id AS ApiClientId, ac.CreatorOwnershipTokenId
                FROM dmscs.ApiClient ac
                WHERE ac.Id = @ApiClientId AND {TenantScopedApplicationCondition("ac")};
                """;

            var apiClient = await connection.QuerySingleOrDefaultAsync<(
                int ApiClientId,
                int? CreatorOwnershipTokenId
            )?>(apiClientSql, new { ApiClientId = apiClientId, TenantId });

            if (apiClient is null)
            {
                return new ApiClientOwnershipGetResult.FailureApiClientNotFound();
            }

            List<int> ownershipTokenIds =
            [
                .. await connection.QueryAsync<int>(
                    """
                    SELECT OwnershipTokenId
                    FROM dmscs.ApiClientOwnershipToken
                    WHERE ApiClientId = @ApiClientId
                    ORDER BY OwnershipTokenId;
                    """,
                    new { ApiClientId = apiClientId }
                ),
            ];

            return new ApiClientOwnershipGetResult.Success(
                new ApiClientOwnershipResponse
                {
                    CreatorOwnershipTokenId = apiClient.Value.CreatorOwnershipTokenId,
                    OwnershipTokenIds = ownershipTokenIds,
                }
            );
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Get API-client ownership failure");
            return new ApiClientOwnershipGetResult.FailureUnknown(ex.Message);
        }
    }

    public async Task<ApiClientOwnershipUpdateResult> UpdateApiClientOwnership(
        ApiClientOwnershipUpdateCommand command
    )
    {
        await using var connection = new SqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            string lockApiClientSql = $"""
                SELECT ac.Id
                FROM dmscs.ApiClient ac WITH (UPDLOCK, HOLDLOCK)
                WHERE ac.Id = @ApiClientId AND {TenantScopedApplicationCondition("ac")};
                """;
            int? apiClientId = await connection.QuerySingleOrDefaultAsync<int?>(
                lockApiClientSql,
                new { command.ApiClientId, TenantId },
                transaction
            );
            if (apiClientId is null)
            {
                await transaction.RollbackAsync();
                return new ApiClientOwnershipUpdateResult.FailureApiClientNotFound();
            }

            int[] tokenIdsToValidate =
            [
                .. command
                    .OwnershipTokenIds.Concat(
                        command.CreatorOwnershipTokenId is not null
                            ? [command.CreatorOwnershipTokenId.Value]
                            : []
                    )
                    .Distinct(),
            ];
            string ownershipTokenIdsJson = JsonSerializer.Serialize(tokenIdsToValidate);
            if (
                tokenIdsToValidate.Length > 0
                && !await AllOwnershipTokensInTenant(
                    connection,
                    transaction,
                    ownershipTokenIdsJson,
                    tokenIdsToValidate.Length
                )
            )
            {
                await transaction.RollbackAsync();
                return new ApiClientOwnershipUpdateResult.FailureOwnershipTokenNotFound();
            }

            await connection.ExecuteAsync(
                """
                UPDATE dmscs.ApiClient
                SET CreatorOwnershipTokenId = @CreatorOwnershipTokenId,
                    LastModifiedAt = @LastModifiedAt,
                    ModifiedBy = @ModifiedBy
                WHERE Id = @ApiClientId;
                """,
                new
                {
                    command.ApiClientId,
                    command.CreatorOwnershipTokenId,
                    LastModifiedAt = auditContext.GetCurrentTimestamp(),
                    ModifiedBy = auditContext.GetCurrentUser(),
                },
                transaction
            );

            await connection.ExecuteAsync(
                """
                DELETE FROM dmscs.ApiClientOwnershipToken
                WHERE ApiClientId = @ApiClientId;
                """,
                new { command.ApiClientId },
                transaction
            );

            if (command.OwnershipTokenIds.Length > 0)
            {
                string readModifyOwnershipTokenIdsJson = JsonSerializer.Serialize(command.OwnershipTokenIds);
                await connection.ExecuteAsync(
                    """
                    INSERT INTO dmscs.ApiClientOwnershipToken (ApiClientId, OwnershipTokenId, CreatedBy)
                    SELECT @ApiClientId, CAST([value] AS SMALLINT), @CreatedBy
                    FROM OPENJSON(@OwnershipTokenIdsJson);
                    """,
                    new
                    {
                        command.ApiClientId,
                        OwnershipTokenIdsJson = readModifyOwnershipTokenIdsJson,
                        CreatedBy = auditContext.GetCurrentUser(),
                    },
                    transaction
                );
            }

            await transaction.CommitAsync();
            return new ApiClientOwnershipUpdateResult.Success();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Update API-client ownership failure");
            await RollbackSafelyAsync(transaction);
            return new ApiClientOwnershipUpdateResult.FailureUnknown(ex.Message);
        }
    }

    private async Task<bool> AllOwnershipTokensInTenant(
        SqlConnection connection,
        DbTransaction transaction,
        string ownershipTokenIdsJson,
        int expectedCount
    )
    {
        string sql = $"""
            SELECT COUNT(1)
            FROM dmscs.OwnershipToken ot
            JOIN OPENJSON(@OwnershipTokenIdsJson) tokenIds
                ON ot.Id = CAST(tokenIds.[value] AS SMALLINT)
            WHERE {TenantContext.TenantWhereClause("ot")};
            """;
        int count = await connection.ExecuteScalarAsync<int>(
            sql,
            new { OwnershipTokenIdsJson = ownershipTokenIdsJson, TenantId },
            transaction
        );
        return count == expectedCount;
    }

    private async Task RollbackSafelyAsync(DbTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync();
        }
        catch (Exception rollbackException)
        {
            logger.LogError(rollbackException, "Transaction rollback failed");
        }
    }
}
