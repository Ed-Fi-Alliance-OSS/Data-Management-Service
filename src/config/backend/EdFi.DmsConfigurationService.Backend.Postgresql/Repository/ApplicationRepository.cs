// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel;
using EdFi.DmsConfigurationService.DataModel.Application;
using Microsoft.Extensions.Options;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Repository
{
    public class ApplicationRepository(IOptions<DatabaseOptions> databaseOptions) : IApplicationRepository
    {
        public async Task<ApplicationInsertResult> InsertApplication(
            ApplicationInsertCommand command,
            Guid clientUuid,
            string clientSecret
        )
        {
            await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            try
            {
                string sql = """
                    INSERT INTO dmscs.Application (ApplicationName, VendorId, ClaimSetName)
                    VALUES (@ApplicationName, @VendorId, @ClaimSetName)
                    RETURNING Id;
                    """;

                long id = await connection.ExecuteScalarAsync<long>(sql, command);

                sql = """
                    INSERT INTO dmscs.ApplicationEducationOrganization (ApplicationId, EducationOrganizationId)
                    VALUES (@ApplicationId, @EducationOrganizationId);
                    """;

                var educationOrganizations = command.EducationOrganizationIds.Select(e => new
                {
                    ApplicationId = id,
                    EducationOrganizationId = e,
                });

                await connection.ExecuteAsync(sql, educationOrganizations);

                sql = """
                    INSERT INTO dmscs.ApiClient (ApplicationId, ClientUuid)
                    VALUES (@ApplicationId, @clientUuid);
                    """;

                await connection.ExecuteAsync(sql, new { ApplicationId = id, ClientUuid = clientUuid });

                await transaction.CommitAsync();
                return new ApplicationInsertResult.Success(id);
            }
            catch (PostgresException ex) when (ex.SqlState == "23503" && ex.Message.Contains("fk_vendor"))
            {
                await transaction.RollbackAsync();
                return new ApplicationInsertResult.FailureVendorNotFound();
            }
            catch (Exception ex)
            {
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
                    SELECT a.Id, a.ApplicationName, a.VendorId, a.ClaimSetName, e.EducationOrganizationId 
                    FROM dmscs.Application a
                    LEFT OUTER JOIN dmscs.ApplicationEducationOrganization e ON a.Id = e.ApplicationId
                    ORDER BY a.ApplicationName;
                    """;
                var applications = await connection.QueryAsync<
                    ApplicationResponse,
                    long,
                    ApplicationResponse
                >(
                    sql,
                    (application, educationOrganizationId) =>
                    {
                        application.EducationOrganizationIds.Add(educationOrganizationId);
                        return application;
                    },
                    splitOn: "EducationOrganizationId"
                );

                var returnApplications = applications
                    .GroupBy(a => a.Id)
                    .Select(g =>
                    {
                        var grouped = g.First();
                        grouped.EducationOrganizationIds = g.Select(e => e.EducationOrganizationIds.Single())
                            .ToList();
                        return grouped;
                    })
                    .ToList();

                return new ApplicationQueryResult.Success(
                    returnApplications.Skip(query.Offset).Take(query.Limit)
                );
            }
            catch (Exception e)
            {
                return new ApplicationQueryResult.FailureUnknown(e.Message);
            }
        }

        public async Task<ApplicationGetResult> GetApplication(long id)
        {
            await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
            await connection.OpenAsync();
            try
            {
                string sql = """
                    SELECT a.Id, a.ApplicationName, a.VendorId, a.ClaimSetName, e.EducationOrganizationId 
                    FROM dmscs.Application a
                    LEFT OUTER JOIN dmscs.ApplicationEducationOrganization e ON a.Id = e.ApplicationId
                    WHERE a.Id = @Id;
                    """;
                var applications = await connection.QueryAsync<
                    ApplicationResponse,
                    long,
                    ApplicationResponse
                >(
                    sql,
                    (application, educationOrganizationId) =>
                    {
                        application.EducationOrganizationIds.Add(educationOrganizationId);
                        return application;
                    },
                    param: new { Id = id },
                    splitOn: "EducationOrganizationId"
                );

                ApplicationResponse? returnApplication = applications
                    .GroupBy(a => a.Id)
                    .Select(g =>
                    {
                        var grouped = g.First();
                        grouped.EducationOrganizationIds = g.Select(e => e.EducationOrganizationIds.Single())
                            .ToList();
                        return grouped;
                    })
                    .SingleOrDefault();

                return returnApplication != null
                    ? new ApplicationGetResult.Success(returnApplication)
                    : new ApplicationGetResult.FailureNotFound();
            }
            catch (Exception e)
            {
                return new ApplicationGetResult.FailureUnknown(e.Message);
            }
        }

        public async Task<ApplicationVendorUpdateResult> UpdateApplication(ApplicationUpdateCommand command)
        {
            await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            try
            {
                string sql = """
                    UPDATE dmscs.Application
                    SET ApplicationName=@ApplicationName, VendorId=@VendorId, ClaimSetName=@ClaimSetName
                    WHERE Id = @Id;
                    """;

                int affectedRows = await connection.ExecuteAsync(sql, command);

                sql =
                    "DELETE FROM dmscs.ApplicationEducationOrganization WHERE ApplicationId = @ApplicationId";
                await connection.ExecuteAsync(sql, new { ApplicationId = command.Id });

                sql = """
                    INSERT INTO dmscs.ApplicationEducationOrganization (ApplicationId, EducationOrganizationId)
                    VALUES (@ApplicationId, @EducationOrganizationId);
                    """;

                var educationOrganizations = command.EducationOrganizationIds.Select(e => new
                {
                    ApplicationId = command.Id,
                    EducationOrganizationId = e,
                });

                await connection.ExecuteAsync(sql, educationOrganizations);
                await transaction.CommitAsync();

                return affectedRows > 0
                    ? new ApplicationVendorUpdateResult.Success()
                    : new ApplicationVendorUpdateResult.FailureNotExists();
            }
            catch (PostgresException ex) when (ex.SqlState == "23503" && ex.Message.Contains("fk_vendor"))
            {
                await transaction.RollbackAsync();
                return new ApplicationVendorUpdateResult.FailureVendorNotFound();
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                return new ApplicationVendorUpdateResult.FailureUnknown(ex.Message);
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
                return new ApplicationDeleteResult.FailureUnknown(ex.Message);
            }
        }
    }
}
