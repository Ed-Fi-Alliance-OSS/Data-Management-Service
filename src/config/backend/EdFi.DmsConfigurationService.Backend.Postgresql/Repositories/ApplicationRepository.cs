// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.Application;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Repositories;

public class ApplicationRepository(
    IOptions<DatabaseOptions> databaseOptions,
    ILogger<ApplicationRepository> logger,
    IAuditContext auditContext
) : IApplicationRepository
{
    public async Task<ApplicationInsertResult> InsertApplication(
        ApplicationInsertCommand command,
        ApiClientCommand clientCommand
    )
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            string sql = """
                INSERT INTO dmscs.Application (ApplicationName, VendorId, ClaimSetName, CreatedBy)
                VALUES (@ApplicationName, @VendorId, @ClaimSetName, @CreatedBy)
                RETURNING Id;
                """;

            long id = await connection.ExecuteScalarAsync<long>(
                sql,
                new
                {
                    command.ApplicationName,
                    command.VendorId,
                    command.ClaimSetName,
                    CreatedBy = auditContext.GetCurrentUser(),
                }
            );

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

            await connection.ExecuteAsync(sql, educationOrganizations);

            sql = """
                INSERT INTO dmscs.ApiClient (ApplicationId, ClientId, ClientUuid, Name, IsApproved, CreatedBy)
                VALUES (@ApplicationId, @ClientId, @ClientUuid, @Name, @IsApproved, @CreatedBy)
                RETURNING Id;
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
                }
            );

            if (command.DmsInstanceIds.Length > 0)
            {
                sql = """
                    INSERT INTO dmscs.ApiClientDmsInstance (ApiClientId, DmsInstanceId, CreatedBy)
                    VALUES (@ApiClientId, @DmsInstanceId, @CreatedBy);
                    """;

                var dmsInstanceMappings = command.DmsInstanceIds.Select(dmsInstanceId => new
                {
                    ApiClientId = apiClientId,
                    DmsInstanceId = dmsInstanceId,
                    CreatedBy = currentUser,
                });

                await connection.ExecuteAsync(sql, dmsInstanceMappings);
            }

            await transaction.CommitAsync();
            return new ApplicationInsertResult.Success(id);
        }
        catch (PostgresException ex) when (ex.SqlState == "23503" && ex.Message.Contains("fk_vendor"))
        {
            logger.LogWarning(ex, "Vendor not found");
            await transaction.RollbackAsync();
            return new ApplicationInsertResult.FailureVendorNotFound();
        }
        catch (PostgresException ex) when (ex.SqlState == "23503" && ex.Message.Contains("fk_dmsinstance"))
        {
            logger.LogWarning(ex, "DMS instance not found");
            await transaction.RollbackAsync();
            return new ApplicationInsertResult.FailureDmsInstanceNotFound();
        }
        catch (PostgresException ex)
            when (ex.SqlState == "23505" && ex.Message.Contains("idx_vendor_applicationname"))
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

    public async Task<ApplicationQueryResult> QueryApplication(PagingQuery query)
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();
        try
        {
            string sql = """
                SELECT a.Id, a.ApplicationName, a.VendorId, a.ClaimSetName,
                       a.CreatedAt, a.CreatedBy, a.LastModifiedAt, a.ModifiedBy,
                       e.EducationOrganizationId, acd.DmsInstanceId
                FROM (SELECT * FROM dmscs.Application ORDER BY Id LIMIT @Limit OFFSET @Offset) AS a
                LEFT OUTER JOIN dmscs.ApplicationEducationOrganization e ON a.Id = e.ApplicationId
                LEFT OUTER JOIN dmscs.ApiClient ac ON a.Id = ac.ApplicationId
                LEFT OUTER JOIN dmscs.ApiClientDmsInstance acd ON ac.Id = acd.ApiClientId
                ORDER BY a.ApplicationName;
                """;
            var applications = await connection.QueryAsync<
                ApplicationResponse,
                long?,
                long?,
                ApplicationResponse
            >(
                sql,
                (application, educationOrganizationId, dmsInstanceId) =>
                {
                    if (educationOrganizationId != null)
                    {
                        application.EducationOrganizationIds.Add(educationOrganizationId.Value);
                    }
                    if (dmsInstanceId != null)
                    {
                        application.DmsInstanceIds.Add(dmsInstanceId.Value);
                    }
                    return application;
                },
                param: query,
                splitOn: "EducationOrganizationId,DmsInstanceId"
            );

            var returnApplications = applications
                .GroupBy(a => a.Id)
                .Select(g =>
                {
                    var grouped = g.First();
                    grouped.EducationOrganizationIds = g.SelectMany(a => a.EducationOrganizationIds)
                        .Distinct()
                        .ToList();
                    grouped.DmsInstanceIds = g.SelectMany(a => a.DmsInstanceIds).Distinct().ToList();
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
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();
        try
        {
            string sql = """
                SELECT a.Id, a.ApplicationName, a.VendorId, a.ClaimSetName,
                       a.CreatedAt, a.CreatedBy, a.LastModifiedAt, a.ModifiedBy,
                       e.EducationOrganizationId, acd.DmsInstanceId
                FROM dmscs.Application a
                LEFT OUTER JOIN dmscs.ApplicationEducationOrganization e ON a.Id = e.ApplicationId
                LEFT OUTER JOIN dmscs.ApiClient ac ON a.Id = ac.ApplicationId
                LEFT OUTER JOIN dmscs.ApiClientDmsInstance acd ON ac.Id = acd.ApiClientId
                WHERE a.Id = @Id;
                """;
            var applications = await connection.QueryAsync<
                ApplicationResponse,
                long?,
                long?,
                ApplicationResponse
            >(
                sql,
                (application, educationOrganizationId, dmsInstanceId) =>
                {
                    if (educationOrganizationId != null)
                    {
                        application.EducationOrganizationIds.Add(educationOrganizationId.Value);
                    }
                    if (dmsInstanceId != null)
                    {
                        application.DmsInstanceIds.Add(dmsInstanceId.Value);
                    }
                    return application;
                },
                param: new { Id = id },
                splitOn: "EducationOrganizationId,DmsInstanceId"
            );

            ApplicationResponse? returnApplication = applications
                .GroupBy(a => a.Id)
                .Select(g =>
                {
                    var grouped = g.First();
                    grouped.EducationOrganizationIds = g.SelectMany(a => a.EducationOrganizationIds)
                        .Distinct()
                        .ToList();
                    grouped.DmsInstanceIds = g.SelectMany(a => a.DmsInstanceIds).Distinct().ToList();
                    return grouped;
                })
                .SingleOrDefault();

            return returnApplication != null
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
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            string sql = """
                UPDATE dmscs.Application
                SET ApplicationName=@ApplicationName, VendorId=@VendorId, ClaimSetName=@ClaimSetName,
                    LastModifiedAt=@LastModifiedAt, ModifiedBy=@ModifiedBy
                WHERE Id = @Id;
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
                }
            );

            if (affectedRows == 0)
            {
                return new ApplicationUpdateResult.FailureNotExists();
            }

            sql = "DELETE FROM dmscs.ApplicationEducationOrganization WHERE ApplicationId = @ApplicationId";
            await connection.ExecuteAsync(sql, new { ApplicationId = command.Id });

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

            await connection.ExecuteAsync(sql, educationOrganizations);

            string updateApiClientsql = """
                UPDATE dmscs.ApiClient
                SET ClientUuid=@ClientUuid, LastModifiedAt=@LastModifiedAt, ModifiedBy=@ModifiedBy
                WHERE ClientId = @ClientId;
                """;

            await connection.ExecuteAsync(
                updateApiClientsql,
                new
                {
                    clientCommand.ClientUuid,
                    clientCommand.ClientId,
                    LastModifiedAt = auditContext.GetCurrentTimestamp(),
                    ModifiedBy = currentUser,
                }
            );

            // Get ApiClient Id for DmsInstance relationship update
            sql = "SELECT Id FROM dmscs.ApiClient WHERE ClientId = @ClientId;";
            long apiClientId = await connection.ExecuteScalarAsync<long>(sql, new { clientCommand.ClientId });

            // Delete existing DmsInstance relationship
            sql = "DELETE FROM dmscs.ApiClientDmsInstance WHERE ApiClientId = @ApiClientId";
            await connection.ExecuteAsync(sql, new { ApiClientId = apiClientId });

            // Insert new DmsInstance relationships if provided
            if (command.DmsInstanceIds.Length > 0)
            {
                sql = """
                    INSERT INTO dmscs.ApiClientDmsInstance (ApiClientId, DmsInstanceId, CreatedBy)
                    VALUES (@ApiClientId, @DmsInstanceId, @CreatedBy);
                    """;

                var dmsInstanceMappings = command.DmsInstanceIds.Select(dmsInstanceId => new
                {
                    ApiClientId = apiClientId,
                    DmsInstanceId = dmsInstanceId,
                    CreatedBy = currentUser,
                });

                await connection.ExecuteAsync(sql, dmsInstanceMappings);
            }

            await transaction.CommitAsync();

            return new ApplicationUpdateResult.Success();
        }
        catch (PostgresException ex) when (ex.SqlState == "23503" && ex.Message.Contains("fk_vendor"))
        {
            logger.LogWarning(ex, "Update application failure: Vendor not found");
            await transaction.RollbackAsync();
            return new ApplicationUpdateResult.FailureVendorNotFound();
        }
        catch (PostgresException ex) when (ex.SqlState == "23503" && ex.Message.Contains("fk_dmsinstance"))
        {
            logger.LogWarning(ex, "Update application failure: DMS instance not found");
            await transaction.RollbackAsync();
            return new ApplicationUpdateResult.FailureDmsInstanceNotFound();
        }
        catch (PostgresException ex)
            when (ex.SqlState == "23505" && ex.Message.Contains("idx_vendor_applicationname"))
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
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        try
        {
            string sql = """
                DELETE FROM dmscs.Application where Id = @Id;
                """;

            int affectedRows = await connection.ExecuteAsync(sql, new { Id = id });
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
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        try
        {
            string sql = """
                SELECT ClientId, ClientUuid
                FROM dmscs.ApiClient
                WHERE ApplicationId = @Id
                ORDER BY Id
                """;

            var clients = await connection.QueryAsync<ApiClient>(sql, new { Id = @id });

            return new ApplicationApiClientsResult.Success(clients.ToArray());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Get application clients failure");
            return new ApplicationApiClientsResult.FailureUnknown(ex.Message);
        }
    }
}
