// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using Dapper;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.Application;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Repositories;

public class ApplicationRepository(
    IOptions<DatabaseOptions> databaseOptions,
    ILogger<ApplicationRepository> logger,
    IAuditContext auditContext,
    ITenantContextProvider tenantContextProvider
) : IApplicationRepository
{
    private TenantContext TenantContext => tenantContextProvider.Context;

    private long? TenantId => TenantContext is TenantContext.Multitenant mt ? mt.TenantId : null;

    /// <summary>
    /// SQL condition constraining an Application row to the current tenant through its owning Vendor.
    /// </summary>
    private string TenantScopedVendorCondition(string? tableAlias = null)
    {
        var column = string.IsNullOrEmpty(tableAlias) ? "VendorId" : $"{tableAlias}.VendorId";
        return $"""
            {column} IN (
                SELECT Id FROM dmscs.Vendor WHERE {TenantContext.TenantWhereClause()}
            )
            """;
    }

    private async Task<bool> AllDataStoresInTenant(
        SqlConnection connection,
        DbTransaction? transaction,
        long[] dataStoreIds
    )
    {
        string sql = $"""
            SELECT COUNT(1) FROM dmscs.DataStore
            WHERE Id IN @DataStoreIds AND {TenantContext.TenantWhereClause()};
            """;
        int count = await connection.ExecuteScalarAsync<int>(
            sql,
            new { DataStoreIds = dataStoreIds, TenantId },
            transaction
        );
        return count == dataStoreIds.Distinct().Count();
    }

    private async Task<bool> ApplicationExistsForTenant(
        SqlConnection connection,
        DbTransaction? transaction,
        long id
    )
    {
        string sql = $"""
            SELECT COUNT(1) FROM dmscs.Application
            WHERE Id = @Id AND {TenantScopedVendorCondition()};
            """;
        return await connection.ExecuteScalarAsync<int>(sql, new { Id = id, TenantId }, transaction) > 0;
    }

    public async Task<ApplicationInsertResult> InsertApplication(
        ApplicationInsertCommand command,
        ApiClientCommand clientCommand
    )
    {
        await using var connection = new SqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            string sql = $"""
                INSERT INTO dmscs.Application (ApplicationName, VendorId, ClaimSetName, CreatedBy)
                OUTPUT INSERTED.Id
                SELECT @ApplicationName, @VendorId, @ClaimSetName, @CreatedBy
                WHERE EXISTS (
                    SELECT 1 FROM dmscs.Vendor
                    WHERE Id = @VendorId AND {TenantContext.TenantWhereClause()}
                );
                """;

            long? insertedId = await connection.ExecuteScalarAsync<long?>(
                sql,
                new
                {
                    command.ApplicationName,
                    command.VendorId,
                    command.ClaimSetName,
                    CreatedBy = auditContext.GetCurrentUser(),
                    TenantId,
                },
                transaction
            );

            if (insertedId is null)
            {
                logger.LogWarning("Vendor not found");
                await transaction.RollbackAsync();
                return new ApplicationInsertResult.FailureVendorNotFound();
            }

            long id = insertedId.Value;

            sql = """
                INSERT INTO dmscs.ApplicationEducationOrganization (ApplicationId, EducationOrganizationId, CreatedBy)
                VALUES (@ApplicationId, @EducationOrganizationId, @CreatedBy);
                """;

            var currentUser = auditContext.GetCurrentUser();
            var educationOrganizations = command.EducationOrganizationIds.Select(e => new
            {
                ApplicationId = id,
                EducationOrganizationId = e,
                CreatedBy = currentUser,
            });

            await connection.ExecuteAsync(sql, educationOrganizations, transaction);

            sql = """
                INSERT INTO dmscs.ApiClient (ApplicationId, ClientId, ClientUuid, Name, IsApproved, CreatedBy)
                OUTPUT INSERTED.Id
                VALUES (@ApplicationId, @ClientId, @ClientUuid, @Name, @IsApproved, @CreatedBy);
                """;

            long apiClientId = await connection.ExecuteScalarAsync<long>(
                sql,
                new
                {
                    ApplicationId = id,
                    clientCommand.ClientId,
                    clientCommand.ClientUuid,
                    Name = command.ApplicationName,
                    IsApproved = true,
                    CreatedBy = currentUser,
                },
                transaction
            );

            if (command.DataStoreIds.Length > 0)
            {
                if (!await AllDataStoresInTenant(connection, transaction, command.DataStoreIds))
                {
                    logger.LogWarning("Data store not found");
                    await transaction.RollbackAsync();
                    return new ApplicationInsertResult.FailureDataStoreNotFound();
                }

                sql = """
                    INSERT INTO dmscs.ApiClientDataStore (ApiClientId, DataStoreId, CreatedBy)
                    VALUES (@ApiClientId, @DataStoreId, @CreatedBy);
                    """;

                var dataStoreMappings = command.DataStoreIds.Select(dataStoreId => new
                {
                    ApiClientId = apiClientId,
                    DataStoreId = dataStoreId,
                    CreatedBy = currentUser,
                });

                await connection.ExecuteAsync(sql, dataStoreMappings, transaction);
            }

            if (command.ProfileIds.Length > 0)
            {
                sql = """
                    INSERT INTO dmscs.ApplicationProfile (ApplicationId, ProfileId, CreatedBy)
                    VALUES (@ApplicationId, @ProfileId, @CreatedBy);
                    """;

                var profileMappings = command
                    .ProfileIds.Distinct()
                    .Select(profileId => new
                    {
                        ApplicationId = id,
                        ProfileId = profileId,
                        CreatedBy = currentUser,
                    });

                await connection.ExecuteAsync(sql, profileMappings, transaction);
            }

            await transaction.CommitAsync();
            return new ApplicationInsertResult.Success(id);
        }
        catch (SqlException ex) when (ex.IsForeignKeyViolation("FK_Application_Vendor"))
        {
            logger.LogWarning(ex, "Vendor not found");
            await transaction.RollbackAsync();
            return new ApplicationInsertResult.FailureVendorNotFound();
        }
        catch (SqlException ex) when (ex.IsForeignKeyViolation("FK_ApiClientDataStore_DataStore"))
        {
            logger.LogWarning(ex, "Data store not found");
            await transaction.RollbackAsync();
            return new ApplicationInsertResult.FailureDataStoreNotFound();
        }
        catch (SqlException ex) when (ex.IsForeignKeyViolation("FK_ApplicationProfile_Profile"))
        {
            logger.LogWarning(ex, "Profile not found");
            await transaction.RollbackAsync();
            return new ApplicationInsertResult.FailureProfileNotFound();
        }
        catch (SqlException ex) when (ex.IsUniqueViolation("UX_Application_VendorId_ApplicationName"))
        {
            logger.LogWarning(
                ex,
                "Application '{ApplicationName}' already exists for vendor",
                command.ApplicationName
            );
            await transaction.RollbackAsync();
            return new ApplicationInsertResult.FailureDuplicateApplication(command.ApplicationName);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Insert application failure");
            await transaction.RollbackAsync();
            return new ApplicationInsertResult.FailureUnknown(ex.Message);
        }
    }

    private static readonly IReadOnlyDictionary<string, string> OrderByColumns = new Dictionary<
        string,
        string
    >(StringComparer.OrdinalIgnoreCase)
    {
        ["id"] = "Id",
        ["applicationName"] = "ApplicationName",
        ["vendorId"] = "VendorId",
        ["claimSetName"] = "ClaimSetName",
    };

    private static string ResolveOrderByColumn(ApplicationQuery query) =>
        query.OrderBy is not null && OrderByColumns.TryGetValue(query.OrderBy, out var col)
            ? col
            : "ApplicationName";

    private static string BuildOrderByClause(ApplicationQuery query)
    {
        string col = ResolveOrderByColumn(query);
        return $"ORDER BY {col} {(query.IsDescending ? "DESC" : "ASC")}";
    }

    private string BuildFilterClause(ApplicationQuery query, int[] parsedIds)
    {
        var conditions = new List<string> { TenantScopedVendorCondition() };
        if (query.Id.HasValue)
        {
            conditions.Add("Id = @Id");
        }
        if (query.ApplicationName is not null)
        {
            conditions.Add("ApplicationName = @ApplicationName");
        }
        if (query.ClaimSetName is not null)
        {
            conditions.Add("ClaimSetName = @ClaimSetName");
        }
        if (parsedIds.Length > 0)
        {
            conditions.Add("Id IN @ParsedIds");
        }
        return "WHERE " + string.Join(" AND ", conditions);
    }

    public async Task<ApplicationQueryResult> QueryApplication(ApplicationQuery query)
    {
        await using var connection = new SqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();
        try
        {
            int[] parsedIds = !string.IsNullOrEmpty(query.Ids)
                ? query
                    .Ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(s => int.TryParse(s, out _))
                    .Select(s => int.Parse(s))
                    .ToArray()
                : [];
            string orderByClause = BuildOrderByClause(query);
            string filterClause = BuildFilterClause(query, parsedIds);
            string outerCol = ResolveOrderByColumn(query);
            // Direction mirrors BuildOrderByClause() — must stay consistent.
            string direction = query.IsDescending ? "DESC" : "ASC";
            string sql = $"""
                SELECT a.Id, a.ApplicationName, a.VendorId, a.ClaimSetName,
                       (SELECT CAST(COALESCE(MIN(CAST(ac2.IsApproved AS INT)), 1) AS BIT)
                        FROM dmscs.ApiClient ac2
                        WHERE ac2.ApplicationId = a.Id) AS Enabled,
                       e.EducationOrganizationId, acd.DataStoreId, ap.ProfileId
                FROM (SELECT * FROM dmscs.Application {filterClause} {orderByClause} {query.BuildSqlServerPagingClause()}) AS a
                LEFT OUTER JOIN dmscs.ApplicationEducationOrganization e ON a.Id = e.ApplicationId
                LEFT OUTER JOIN dmscs.ApiClient ac ON a.Id = ac.ApplicationId
                LEFT OUTER JOIN dmscs.ApiClientDataStore acd ON ac.Id = acd.ApiClientId
                LEFT OUTER JOIN dmscs.ApplicationProfile ap ON a.Id = ap.ApplicationId
                ORDER BY a.{outerCol} {direction};
                """;
            var applications = await connection.QueryAsync<
                ApplicationResponse,
                long?,
                long?,
                long?,
                ApplicationResponse
            >(
                sql,
                (application, educationOrganizationId, dataStoreId, profileId) =>
                {
                    if (educationOrganizationId is not null)
                    {
                        application.EducationOrganizationIds.Add(educationOrganizationId.Value);
                    }
                    if (dataStoreId is not null)
                    {
                        application.DataStoreIds.Add(dataStoreId.Value);
                    }
                    if (profileId is not null)
                    {
                        application.ProfileIds.Add(profileId.Value);
                    }
                    return application;
                },
                param: new
                {
                    query.Limit,
                    query.Offset,
                    query.Id,
                    query.ApplicationName,
                    query.ClaimSetName,
                    ParsedIds = parsedIds,
                    TenantId,
                },
                splitOn: "EducationOrganizationId,DataStoreId,ProfileId"
            );

            var returnApplications = applications
                .GroupBy(a => a.Id)
                .Select(g =>
                {
                    var grouped = g.First();
                    grouped.EducationOrganizationIds = g.SelectMany(a => a.EducationOrganizationIds)
                        .Distinct()
                        .ToList();
                    grouped.DataStoreIds = g.SelectMany(a => a.DataStoreIds).Distinct().ToList();
                    grouped.ProfileIds = g.SelectMany(a => a.ProfileIds).Distinct().ToList();
                    return grouped;
                })
                .ToList();

            return new ApplicationQueryResult.Success(returnApplications);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Query application failure");
            return new ApplicationQueryResult.FailureUnknown(ex.Message);
        }
    }

    public async Task<ApplicationGetResult> GetApplication(long id)
    {
        await using var connection = new SqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();
        try
        {
            string sql = $"""
                SELECT a.Id, a.ApplicationName, a.VendorId, a.ClaimSetName,
                       (SELECT CAST(COALESCE(MIN(CAST(ac2.IsApproved AS INT)), 1) AS BIT)
                        FROM dmscs.ApiClient ac2
                        WHERE ac2.ApplicationId = a.Id) AS Enabled,
                       e.EducationOrganizationId, acd.DataStoreId, ap.ProfileId
                FROM dmscs.Application a
                LEFT OUTER JOIN dmscs.ApplicationEducationOrganization e ON a.Id = e.ApplicationId
                LEFT OUTER JOIN dmscs.ApiClient ac ON a.Id = ac.ApplicationId
                LEFT OUTER JOIN dmscs.ApiClientDataStore acd ON ac.Id = acd.ApiClientId
                LEFT OUTER JOIN dmscs.ApplicationProfile ap ON a.Id = ap.ApplicationId
                WHERE a.Id = @Id AND {TenantScopedVendorCondition("a")};
                """;
            var applications = await connection.QueryAsync<
                ApplicationResponse,
                long?,
                long?,
                long?,
                ApplicationResponse
            >(
                sql,
                (application, educationOrganizationId, dataStoreId, profileId) =>
                {
                    if (educationOrganizationId is not null)
                    {
                        application.EducationOrganizationIds.Add(educationOrganizationId.Value);
                    }
                    if (dataStoreId is not null)
                    {
                        application.DataStoreIds.Add(dataStoreId.Value);
                    }
                    if (profileId is not null)
                    {
                        application.ProfileIds.Add(profileId.Value);
                    }
                    return application;
                },
                param: new { Id = id, TenantId },
                splitOn: "EducationOrganizationId,DataStoreId,ProfileId"
            );

            ApplicationResponse? returnApplication = applications
                .GroupBy(a => a.Id)
                .Select(g =>
                {
                    var grouped = g.First();
                    grouped.EducationOrganizationIds = g.SelectMany(a => a.EducationOrganizationIds)
                        .Distinct()
                        .ToList();
                    grouped.DataStoreIds = g.SelectMany(a => a.DataStoreIds).Distinct().ToList();
                    grouped.ProfileIds = g.SelectMany(a => a.ProfileIds).Distinct().ToList();
                    return grouped;
                })
                .SingleOrDefault();

            return returnApplication is not null
                ? new ApplicationGetResult.Success(returnApplication)
                : new ApplicationGetResult.FailureNotFound();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Get application failure");
            return new ApplicationGetResult.FailureUnknown(ex.Message);
        }
    }

    public async Task<ApplicationUpdateResult> UpdateApplication(
        ApplicationUpdateCommand command,
        ApiClientCommand clientCommand
    )
    {
        await using var connection = new SqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            string sql = $"""
                UPDATE dmscs.Application
                SET ApplicationName=@ApplicationName, VendorId=@VendorId, ClaimSetName=@ClaimSetName,
                    LastModifiedAt=@LastModifiedAt, ModifiedBy=@ModifiedBy
                WHERE Id = @Id
                  AND {TenantScopedVendorCondition()}
                  AND EXISTS (
                      SELECT 1 FROM dmscs.Vendor
                      WHERE Id = @VendorId AND {TenantContext.TenantWhereClause()}
                  );
                """;
            int affectedRows = await connection.ExecuteAsync(
                sql,
                new
                {
                    command.ApplicationName,
                    command.VendorId,
                    command.ClaimSetName,
                    command.Id,
                    LastModifiedAt = auditContext.GetCurrentTimestamp(),
                    ModifiedBy = auditContext.GetCurrentUser(),
                    TenantId,
                },
                transaction
            );

            if (affectedRows == 0)
            {
                if (!await ApplicationExistsForTenant(connection, transaction, command.Id))
                {
                    return new ApplicationUpdateResult.FailureNotExists();
                }

                logger.LogWarning("Update application failure: Vendor not found");
                await transaction.RollbackAsync();
                return new ApplicationUpdateResult.FailureVendorNotFound();
            }

            if (
                command.DataStoreIds.Length > 0
                && !await AllDataStoresInTenant(connection, transaction, command.DataStoreIds)
            )
            {
                logger.LogWarning("Update application failure: Data store not found");
                await transaction.RollbackAsync();
                return new ApplicationUpdateResult.FailureDataStoreNotFound();
            }

            sql = "DELETE FROM dmscs.ApplicationEducationOrganization WHERE ApplicationId = @ApplicationId";
            await connection.ExecuteAsync(sql, new { ApplicationId = command.Id }, transaction);

            sql = """
                INSERT INTO dmscs.ApplicationEducationOrganization (ApplicationId, EducationOrganizationId, CreatedBy)
                VALUES (@ApplicationId, @EducationOrganizationId, @CreatedBy);
                """;

            var currentUser = auditContext.GetCurrentUser();
            var educationOrganizations = command.EducationOrganizationIds.Select(e => new
            {
                ApplicationId = command.Id,
                EducationOrganizationId = e,
                CreatedBy = currentUser,
            });

            await connection.ExecuteAsync(sql, educationOrganizations, transaction);

            string updateApiClientsql = """
                UPDATE dmscs.ApiClient
                SET ClientUuid=@ClientUuid, LastModifiedAt=@LastModifiedAt, ModifiedBy=@ModifiedBy
                WHERE ClientId = @ClientId AND ApplicationId = @ApplicationId;
                """;

            await connection.ExecuteAsync(
                updateApiClientsql,
                new
                {
                    clientCommand.ClientUuid,
                    clientCommand.ClientId,
                    ApplicationId = command.Id,
                    LastModifiedAt = auditContext.GetCurrentTimestamp(),
                    ModifiedBy = currentUser,
                },
                transaction
            );

            // Get ApiClient Id for DataStore relationship update
            sql =
                "SELECT Id FROM dmscs.ApiClient WHERE ClientId = @ClientId AND ApplicationId = @ApplicationId;";
            long apiClientId = await connection.ExecuteScalarAsync<long>(
                sql,
                new { clientCommand.ClientId, ApplicationId = command.Id },
                transaction
            );

            // Delete existing DataStore relationship
            sql = "DELETE FROM dmscs.ApiClientDataStore WHERE ApiClientId = @ApiClientId";
            await connection.ExecuteAsync(sql, new { ApiClientId = apiClientId }, transaction);

            // Insert new DataStore relationships if provided
            if (command.DataStoreIds.Length > 0)
            {
                sql = """
                    INSERT INTO dmscs.ApiClientDataStore (ApiClientId, DataStoreId, CreatedBy)
                    VALUES (@ApiClientId, @DataStoreId, @CreatedBy);
                    """;

                var dataStoreMappings = command.DataStoreIds.Select(dataStoreId => new
                {
                    ApiClientId = apiClientId,
                    DataStoreId = dataStoreId,
                    CreatedBy = currentUser,
                });

                await connection.ExecuteAsync(sql, dataStoreMappings, transaction);
            }

            // Delete existing Profile relationships
            sql = "DELETE FROM dmscs.ApplicationProfile WHERE ApplicationId = @ApplicationId";
            await connection.ExecuteAsync(sql, new { ApplicationId = command.Id }, transaction);

            // Insert new Profile relationships if provided
            if (command.ProfileIds.Length > 0)
            {
                sql = """
                    INSERT INTO dmscs.ApplicationProfile (ApplicationId, ProfileId, CreatedBy)
                    VALUES (@ApplicationId, @ProfileId, @CreatedBy);
                    """;

                var profileMappings = command
                    .ProfileIds.Distinct()
                    .Select(profileId => new
                    {
                        ApplicationId = command.Id,
                        ProfileId = profileId,
                        CreatedBy = currentUser,
                    });

                await connection.ExecuteAsync(sql, profileMappings, transaction);
            }

            await transaction.CommitAsync();

            return new ApplicationUpdateResult.Success();
        }
        catch (SqlException ex) when (ex.IsForeignKeyViolation("FK_Application_Vendor"))
        {
            logger.LogWarning(ex, "Update application failure: Vendor not found");
            await transaction.RollbackAsync();
            return new ApplicationUpdateResult.FailureVendorNotFound();
        }
        catch (SqlException ex) when (ex.IsForeignKeyViolation("FK_ApiClientDataStore_DataStore"))
        {
            logger.LogWarning(ex, "Update application failure: Data store not found");
            await transaction.RollbackAsync();
            return new ApplicationUpdateResult.FailureDataStoreNotFound();
        }
        catch (SqlException ex) when (ex.IsForeignKeyViolation("FK_ApplicationProfile_Profile"))
        {
            logger.LogWarning(ex, "Update application failure: Profile not found");
            await transaction.RollbackAsync();
            return new ApplicationUpdateResult.FailureProfileNotFound();
        }
        catch (SqlException ex) when (ex.IsUniqueViolation("UX_Application_VendorId_ApplicationName"))
        {
            logger.LogWarning(
                ex,
                "Application '{ApplicationName}' already exists for vendor",
                command.ApplicationName
            );
            await transaction.RollbackAsync();
            return new ApplicationUpdateResult.FailureDuplicateApplication(command.ApplicationName);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Update application failure");
            await transaction.RollbackAsync();
            return new ApplicationUpdateResult.FailureUnknown(ex.Message);
        }
    }

    public async Task<ApplicationDeleteResult> DeleteApplication(long id)
    {
        await using var connection = new SqlConnection(databaseOptions.Value.DatabaseConnection);
        try
        {
            string sql = $"""
                DELETE FROM dmscs.Application where Id = @Id AND {TenantScopedVendorCondition()};
                """;

            int affectedRows = await connection.ExecuteAsync(sql, new { Id = id, TenantId });
            return affectedRows > 0
                ? new ApplicationDeleteResult.Success()
                : new ApplicationDeleteResult.FailureNotExists();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Delete application failure");
            return new ApplicationDeleteResult.FailureUnknown(ex.Message);
        }
    }

    public async Task<ApplicationApiClientsResult> GetApplicationApiClients(long id)
    {
        await using var connection = new SqlConnection(databaseOptions.Value.DatabaseConnection);
        try
        {
            string sql = $"""
                SELECT ac.ClientId, ac.ClientUuid, ac.IsApproved
                FROM dmscs.ApiClient ac
                JOIN dmscs.Application a ON ac.ApplicationId = a.Id
                WHERE ac.ApplicationId = @Id AND {TenantScopedVendorCondition("a")}
                ORDER BY ac.Id
                """;

            var clients = await connection.QueryAsync<ApiClient>(sql, new { Id = @id, TenantId });

            return new ApplicationApiClientsResult.Success(clients.ToArray());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Get application clients failure");
            return new ApplicationApiClientsResult.FailureUnknown(ex.Message);
        }
    }
}
