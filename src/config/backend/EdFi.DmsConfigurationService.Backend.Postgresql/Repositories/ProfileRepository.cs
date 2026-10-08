// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
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
    IAuditContext auditContext,
    ITenantContextProvider tenantContextProvider
) : IProfileRepository
{
    private TenantContext TenantContext => tenantContextProvider.Context;

    private long? TenantId => TenantContext is TenantContext.Multitenant mt ? mt.TenantId : null;

    public async Task<ProfileInsertResult> InsertProfile(ProfileInsertCommand command)
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();
        try
        {
            string sql =
                @"INSERT INTO ""dmscs"".""Profile"" (""ProfileName"", ""Definition"", ""CreatedBy"", ""TenantId"") VALUES (@Name, @Definition, @CreatedBy, @TenantId) RETURNING ""Id"";";
            var id = await connection.ExecuteScalarAsync<int>(
                sql,
                new
                {
                    command.Name,
                    command.Definition,
                    CreatedBy = auditContext.GetCurrentUser(),
                    TenantId,
                }
            );
            return new ProfileInsertResult.Success(id);
        }
        catch (PostgresException ex)
            when (ex.SqlState == PostgresErrorCodes.UniqueViolation
                && ex.ConstraintName == "UX_Profile_TenantId_ProfileName"
            )
        {
            logger.LogWarning(
                ex,
                "Profile name must be unique: {ProfileName}",
                LoggingUtility.SanitizeForLog(command.Name)
            );
            return new ProfileInsertResult.FailureDuplicateName();
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Insert profile failure for ProfileName={ProfileName}",
                LoggingUtility.SanitizeForLog(command.Name)
            );
            return new ProfileInsertResult.FailureUnknown(ex.Message);
        }
    }

    public async Task<ProfileUpdateResult> UpdateProfile(ProfileUpdateCommand command)
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();
        try
        {
            string sql =
                $@"UPDATE ""dmscs"".""Profile"" SET ""ProfileName""=@Name, ""Definition""=@Definition, ""LastModifiedAt""=NOW(), ""ModifiedBy""=@ModifiedBy WHERE ""Id""=@Id AND {TenantContext.TenantWhereClause()};";
            int affected = await connection.ExecuteAsync(
                sql,
                new
                {
                    command.Id,
                    command.Name,
                    command.Definition,
                    ModifiedBy = auditContext.GetCurrentUser(),
                    TenantId,
                }
            );
            if (affected == 0)
            {
                return new ProfileUpdateResult.FailureNotExists(command.Id);
            }
            return new ProfileUpdateResult.Success();
        }
        catch (PostgresException ex)
            when (ex.SqlState == PostgresErrorCodes.UniqueViolation
                && ex.ConstraintName == "UX_Profile_TenantId_ProfileName"
            )
        {
            logger.LogWarning(
                ex,
                "Profile name must be unique: {ProfileName}",
                LoggingUtility.SanitizeForLog(command.Name)
            );
            return new ProfileUpdateResult.FailureDuplicateName();
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Update profile failure for ProfileId={ProfileId}, ProfileName={ProfileName}",
                command.Id,
                LoggingUtility.SanitizeForLog(command.Name)
            );
            return new ProfileUpdateResult.FailureUnknown(ex.Message);
        }
    }

    public async Task<ProfileGetResult> GetProfile(int id)
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();
        try
        {
            string sql =
                $@"SELECT ""Id"", ""ProfileName"" AS ""Name"", ""Definition"" FROM ""dmscs"".""Profile"" WHERE ""Id""=@Id AND {TenantContext.TenantWhereClause()};";
            var profile = await connection.QuerySingleOrDefaultAsync<ProfileResponse>(
                sql,
                new { Id = id, TenantId }
            );
            if (profile is null)
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

    private string BuildFilterClause(ProfileQuery query)
    {
        var conditions = new List<string> { TenantContext.TenantWhereClause() };
        if (query.Id.HasValue)
        {
            conditions.Add("\"Id\" = @Id");
        }

        if (query.Name is not null)
        {
            conditions.Add("\"ProfileName\" = @Name");
        }

        return "WHERE " + string.Join(" AND ", conditions);
    }

    private static IEnumerable<ProfileResponse> ApplyOrdering(
        IEnumerable<ProfileResponse> profiles,
        ProfileQuery query
    )
    {
        var orderBy = query.OrderBy ?? "id";

        return orderBy.ToLowerInvariant() switch
        {
            "name" => query.IsDescending
                ? profiles.OrderByDescending(profile => profile.Name, StringComparer.Ordinal)
                : profiles.OrderBy(profile => profile.Name, StringComparer.Ordinal),
            _ => query.IsDescending
                ? profiles.OrderByDescending(profile => profile.Id)
                : profiles.OrderBy(profile => profile.Id),
        };
    }

    private bool IsProfileValid(ProfileResponse profile)
    {
        var validationResult = ProfileValidationUtils.ValidateProfileXml(profile.Definition);
        if (validationResult.IsValid)
        {
            return true;
        }

        logger.LogWarning(
            "Profile definition failed XSD validation for list query. ProfileId: {ProfileId}, Name: {Name}, ValidationErrors: {ValidationErrors}",
            profile.Id,
            LoggingUtility.SanitizeForLog(profile.Name),
            LoggingUtility.SanitizeForLog(string.Join("; ", validationResult.Errors))
        );

        return false;
    }

    public async Task<IEnumerable<ProfileGetResult>> QueryProfiles(ProfileQuery query)
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();
        var results = new List<ProfileGetResult>();
        try
        {
            string filterClause = BuildFilterClause(query);
            string sql = $"""
                SELECT "Id", "ProfileName" AS "Name", "Definition"
                FROM "dmscs"."Profile"
                {filterClause}
                """;
            var profiles = await connection.QueryAsync<ProfileResponse>(
                sql,
                new
                {
                    query.Id,
                    query.Name,
                    TenantId,
                }
            );

            var validProfiles = ApplyOrdering(profiles.Where(IsProfileValid), query).Skip(query.Offset ?? 0);

            if (query.Limit.HasValue)
            {
                validProfiles = validProfiles.Take(query.Limit.Value);
            }

            foreach (var profile in validProfiles)
            {
                results.Add(new ProfileGetResult.Success(profile));
            }
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Query profiles failure with Limit={Limit}, Offset={Offset}",
                query.Limit,
                query.Offset
            );
            results.Add(new ProfileGetResult.FailureUnknown(ex.Message));
        }
        return results;
    }

    public async Task<ProfileDeleteResult> DeleteProfile(int id)
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();
        try
        {
            string sql =
                $@"DELETE FROM ""dmscs"".""Profile"" WHERE ""Id""=@Id AND {TenantContext.TenantWhereClause()};";
            int affected = await connection.ExecuteAsync(sql, new { Id = id, TenantId });
            if (affected == 0)
            {
                return new ProfileDeleteResult.FailureNotExists(id);
            }
            return new ProfileDeleteResult.Success();
        }
        catch (PostgresException ex)
            when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation
                && ex.ConstraintName == "FK_ApplicationProfile_Profile"
            )
        {
            logger.LogWarning(
                ex,
                "Cannot delete profile Id={ProfileId} because it is assigned to applications",
                id
            );
            return new ProfileDeleteResult.FailureInUse(id);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Delete profile failure for Id={ProfileId}", id);
            return new ProfileDeleteResult.FailureUnknown(ex.Message);
        }
    }
}
