// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using EdFi.DmsConfigurationService.DataModel;
using Microsoft.Extensions.Options;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql;

public class ApplicationRepository(IOptions<DatabaseOptions> databaseOptions) : IRepository<Application>
{
    public async Task<GetResult<Application>> GetAllAsync()
    {
        string sql = """
            SELECT a.Id, a.ApplicationName, a.VendorId, a.ClaimSetName, e.EducationOrganizationId 
            FROM dmscs.Application a
            LEFT OUTER JOIN dmscs.ApplicationEducationOrganization e ON a.Id = e.ApplicationId;
            """;
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        try
        {
            var applications = await connection.QueryAsync<Application, long, Application>(
                sql,
                (application, educationOrganizationId) =>
                {
                    application.ApplicationEducationOrganizations.Add(educationOrganizationId);
                    return application;
                },
                splitOn: "EducationOrganizationId"
            );

            var returnApplications = applications
                .GroupBy(a => a.Id)
                .Select(g =>
                {
                    var grouped = g.First();
                    grouped.ApplicationEducationOrganizations = g.Select(e =>
                            e.ApplicationEducationOrganizations.Single()
                        )
                        .ToList();
                    return grouped;
                })
                .ToList();
            return new GetResult<Application>.GetSuccess(returnApplications);
        }
        catch (Exception ex)
        {
            return new GetResult<Application>.UnknownFailure(ex.Message);
        }
    }

    public async Task<GetResult<Application>> GetByIdAsync(long id)
    {
        string sql = """
            SELECT a.Id, a.ApplicationName, a.VendorId, a.ClaimSetName, e.EducationOrganizationId
            FROM dmscs.Application a
            LEFT OUTER JOIN dmscs.ApplicationEducationOrganization e ON a.Id = e.ApplicationId
            WHERE a.Id = @Id;
            """;
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        try
        {
            var applications = await connection.QueryAsync<Application, long, Application>(
                sql,
                (application, educationOrganizationId) =>
                {
                    application.ApplicationEducationOrganizations.Add(educationOrganizationId);
                    return application;
                },
                param: new { Id = id },
                splitOn: "EducationOrganizationId"
            );

            var returnApplication = applications
                .GroupBy(a => a.Id)
                .Select(g =>
                {
                    var grouped = g.First();
                    grouped.ApplicationEducationOrganizations = g.Select(e =>
                            e.ApplicationEducationOrganizations.Single()
                        )
                        .ToList();
                    return grouped;
                });

            return new GetResult<Application>.GetByIdSuccess(returnApplication.Single());
        }
        catch (Exception ex)
        {
            return new GetResult<Application>.UnknownFailure(ex.Message);
        }
    }

    public async Task<InsertResult> AddAsync(Application application)
    {
        string sql = """
            INSERT INTO dmscs.Application (ApplicationName, VendorId, ClaimSetName)
            VALUES (@ApplicationName, @VendorId, @ClaimSetName)
            RETURNING Id;
            """;
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            long id = await connection.ExecuteScalarAsync<long>(sql, application);

            sql = """
                INSERT INTO dmscs.ApplicationEducationOrganization (ApplicationId, EducationOrganizationId)
                VALUES (@ApplicationId, @EducationOrganizationId);
                """;

            var educationOrganizations = application.ApplicationEducationOrganizations.Select(e => new
            {
                ApplicationId = id,
                EducationOrganizationId = e
            });

            await connection.ExecuteAsync(sql, educationOrganizations);
            await transaction.CommitAsync();
            return new InsertResult.InsertSuccess(id);
        }
        catch (PostgresException ex) when (ex.SqlState == "23503" && ex.Message.Contains("fk_vendor"))
        {
            return new InsertResult.FailureReferenceNotFound("VendorId");
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            return new InsertResult.UnknownFailure(ex.Message);
        }
    }

    public async Task<UpdateResult> UpdateAsync(Application application)
    {
        string sql = """
            UPDATE dmscs.Application
            SET ApplicationName=@ApplicationName, VendorId=@VendorId, ClaimSetName=@ClaimSetName
            WHERE Id = @Id;
            """;
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            int affectedRows = await connection.ExecuteAsync(sql, application);

            sql = "DELETE FROM dmscs.ApplicationEducationOrganization WHERE ApplicationId = @ApplicationId";
            await connection.ExecuteAsync(sql, new { ApplicationId = application.Id });

            sql = """
                INSERT INTO dmscs.ApplicationEducationOrganization (ApplicationId, EducationOrganizationId)
                VALUES (@ApplicationId, @EducationOrganizationId);
                """;

            var educationOrganizations = application.ApplicationEducationOrganizations.Select(e => new
            {
                ApplicationId = application.Id,
                EducationOrganizationId = e
            });

            await connection.ExecuteAsync(sql, educationOrganizations);
            await transaction.CommitAsync();

            return affectedRows > 0
                ? new UpdateResult.UpdateSuccess(affectedRows)
                : new UpdateResult.UpdateFailureNotExists();
        }
        catch (PostgresException ex) when (ex.SqlState == "23503" && ex.Message.Contains("fk_vendor"))
        {
            return new UpdateResult.FailureReferenceNotFound("VendorId");
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            return new UpdateResult.UnknownFailure(ex.Message);
        }
    }

    public async Task<DeleteResult> DeleteAsync(long id)
    {
        string sql = """
            DELETE FROM dmscs.Application where Id = @Id;
            DELETE FROM dmscs.ApplicationEducationOrganization WHERE ApplicationId = @Id;
            """;
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        try
        {
            int affectedRows = await connection.ExecuteAsync(sql, new { Id = id });
            return affectedRows > 0
                ? new DeleteResult.DeleteSuccess(affectedRows)
                : new DeleteResult.DeleteFailureNotExists();
        }
        catch (Exception ex)
        {
            return new DeleteResult.UnknownFailure(ex.Message);
        }
    }
}
