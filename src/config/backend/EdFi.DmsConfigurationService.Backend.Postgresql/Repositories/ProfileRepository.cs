// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.Profile;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Repositories;


public class ProfileRepository(
    IOptions<DatabaseOptions> databaseOptions,
    ILogger<ProfileRepository> logger,
    IAuditContext auditContext
) : IProfileRepository
{
    public async Task<ProfileInsertResult> InsertProfile(ProfileInsertCommand command)
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();
        try
        {
            string sql = @"INSERT INTO dmscs.Profile (ProfileName, Definition, CreatedBy) VALUES (@Name, @Definition, @CreatedBy) RETURNING Id;";
            var id = await connection.ExecuteScalarAsync<long>(
                sql,
                new
                {
                    command.Name,
                    command.Definition,
                    CreatedBy = auditContext.GetCurrentUser(),
                }
            );
            return new ProfileInsertResult.Success(id);
        }
        catch (PostgresException ex) when (ex.SqlState == "23505" && ex.Message.Contains("uq_profile_name"))
        {
            logger.LogWarning(ex, "Profile name must be unique: {ProfileName}", LoggingUtility.SanitizeForLog(command.Name));
            return new ProfileInsertResult.FailureDuplicateName(command.Name);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Insert profile failure for ProfileName={ProfileName}", LoggingUtility.SanitizeForLog(command.Name));
            return new ProfileInsertResult.FailureUnknown(ex.Message);
        }
    }

    public async Task<ProfileUpdateResult> UpdateProfile(ProfileUpdateCommand command)
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();
        try
        {
            string sql = @"UPDATE dmscs.Profile SET ProfileName=@Name, Definition=@Definition, LastModifiedAt=NOW(), ModifiedBy=@ModifiedBy WHERE Id=@Id;";
            int affected = await connection.ExecuteAsync(
                sql,
                new
                {
                    command.Id,
                    command.Name,
                    command.Definition,
                    ModifiedBy = auditContext.GetCurrentUser(),
                }
            );
            if (affected == 0)
            {
                return new ProfileUpdateResult.FailureNotExists(command.Id);
            }
            return new ProfileUpdateResult.Success();
        }
        catch (PostgresException ex) when (ex.SqlState == "23505" && ex.Message.Contains("uq_profile_name"))
        {
            logger.LogWarning(ex, "Profile name must be unique: {ProfileName}", LoggingUtility.SanitizeForLog(command.Name));
            return new ProfileUpdateResult.FailureDuplicateName(command.Name);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Update profile failure for ProfileId={ProfileId}, ProfileName={ProfileName}", command.Id, LoggingUtility.SanitizeForLog(command.Name));
            return new ProfileUpdateResult.FailureUnknown(ex.Message);
        }
    }


    public async Task<ProfileGetResult> GetProfile(long id)
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();
        try
        {
            string sql = @"SELECT Id, ProfileName AS Name, Definition, CreatedAt, CreatedBy, LastModifiedAt, ModifiedBy FROM dmscs.Profile WHERE Id=@Id;";
            var profile = await connection.QuerySingleOrDefaultAsync<ProfileResponse>(sql, new { Id = id });
            if (profile == null)
            {
                return new ProfileGetResult.FailureNotFound();
            }
            return new ProfileGetResult.Success(profile);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Get profile failure for Id={ProfileId}", id);
            return new ProfileGetResult.FailureUnknown(ex.Message);
        }
    }


    public async Task<IEnumerable<ProfileGetResult>> QueryProfiles(PagingQuery query)
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();
        var results = new List<ProfileGetResult>();
        try
        {
            string sql = @"SELECT Id, ProfileName AS Name, Definition, CreatedAt, CreatedBy, LastModifiedAt, ModifiedBy FROM dmscs.Profile ORDER BY Id LIMIT @Limit OFFSET @Offset;";
            var profiles = await connection.QueryAsync<ProfileResponse>(sql, new { Limit = query.Limit, Offset = query.Offset });
            foreach (var profile in profiles)
            {
                results.Add(new ProfileGetResult.Success(profile));
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Query profiles failure with Limit={Limit}, Offset={Offset}", query.Limit, query.Offset);
            results.Add(new ProfileGetResult.FailureUnknown(ex.Message));
        }
        return results;
    }


    public async Task<ProfileDeleteResult> DeleteProfile(long id)
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();
        try
        {
            string sql = @"DELETE FROM dmscs.Profile WHERE Id=@Id;";
            int affected = await connection.ExecuteAsync(sql, new { Id = id });
            if (affected == 0)
            {
                return new ProfileDeleteResult.FailureNotExists(id);
            }
            return new ProfileDeleteResult.Success();
        }
        catch (PostgresException ex) when (ex.SqlState == "23503" && ex.Message.Contains("fk_applicationprofile_profile"))
        {
            logger.LogWarning(ex, "Cannot delete profile Id={ProfileId} because it is assigned to applications", id);
            return new ProfileDeleteResult.FailureInUse(id);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Delete profile failure for Id={ProfileId}", id);
            return new ProfileDeleteResult.FailureUnknown(ex.Message);
        }
    }
}
