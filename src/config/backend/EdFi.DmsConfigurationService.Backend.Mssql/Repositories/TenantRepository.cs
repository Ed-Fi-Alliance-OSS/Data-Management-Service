// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.Tenant;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Repositories;

public class TenantRepository(
    IOptions<DatabaseOptions> databaseOptions,
    ILogger<TenantRepository> logger,
    IAuditContext auditContext
) : ITenantRepository
{
    public async Task<TenantInsertResult> InsertTenant(TenantInsertCommand command)
    {
        await using var connection = new SqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();

        try
        {
            var sql = """
                INSERT INTO dmscs.Tenant (Name, CreatedBy)
                OUTPUT INSERTED.Id
                VALUES (@Name, @CreatedBy);
                """;

            var id = await connection.ExecuteScalarAsync<long>(
                sql,
                new { command.Name, CreatedBy = auditContext.GetCurrentUser() }
            );
            return new TenantInsertResult.Success(id);
        }
        catch (SqlException ex) when (ex.IsUniqueViolation("UX_Tenant_Name"))
        {
            logger.LogWarning(ex, "Tenant name must be unique");
            return new TenantInsertResult.FailureDuplicateName();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Insert tenant failure");
            return new TenantInsertResult.FailureUnknown(ex.Message);
        }
    }

    private static readonly IReadOnlyDictionary<string, string> OrderByColumns = new Dictionary<
        string,
        string
    >(StringComparer.OrdinalIgnoreCase)
    {
        ["id"] = "Id",
        ["name"] = "Name",
    };

    private static string BuildOrderByClause(PagingQuery query)
    {
        if (query.OrderBy is not null && OrderByColumns.TryGetValue(query.OrderBy, out var col))
        {
            return $"ORDER BY {col} {(query.IsDescending ? "DESC" : "ASC")}";
        }

        return "ORDER BY Id";
    }

    public async Task<TenantQueryResult> QueryTenant(PagingQuery query)
    {
        await using var connection = new SqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();

        try
        {
            string orderByClause = BuildOrderByClause(query);
            var sql = $"""
                SELECT Id, Name
                FROM dmscs.Tenant
                {orderByClause}
                {query.BuildSqlServerPagingClause()};
                """;

            var tenants = await connection.QueryAsync<TenantResponse>(sql, query);
            return new TenantQueryResult.Success(tenants);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Query tenant failure");
            return new TenantQueryResult.FailureUnknown(ex.Message);
        }
    }

    public async Task<TenantGetResult> GetTenant(long id)
    {
        await using var connection = new SqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();

        try
        {
            var sql = """
                SELECT Id, Name
                FROM dmscs.Tenant
                WHERE Id = @Id;
                """;

            var tenant = await connection.QuerySingleOrDefaultAsync<TenantResponse>(sql, new { Id = id });

            return tenant is null
                ? new TenantGetResult.FailureNotFound()
                : new TenantGetResult.Success(tenant);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Get tenant failure");
            return new TenantGetResult.FailureUnknown(ex.Message);
        }
    }

    public async Task<TenantGetByNameResult> GetTenantByName(string name)
    {
        await using var connection = new SqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();

        try
        {
            // Case-insensitive tenant lookup to support any casing in requests
            var sql = """
                SELECT Id, Name
                FROM dmscs.Tenant
                WHERE LOWER(Name) = LOWER(@Name);
                """;

            var tenant = await connection.QuerySingleOrDefaultAsync<TenantResponse>(sql, new { Name = name });

            return tenant is null
                ? new TenantGetByNameResult.FailureNotFound()
                : new TenantGetByNameResult.Success(tenant);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Get tenant by name failure");
            return new TenantGetByNameResult.FailureUnknown(ex.Message);
        }
    }
}
