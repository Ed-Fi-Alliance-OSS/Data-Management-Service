// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
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
    IAuditContext auditContext,
    ITenantContextProvider tenantContextProvider
) : IApplicationRepository
{
    public ApplicationRepository(
        IOptions<DatabaseOptions> databaseOptions,
        ILogger<ApplicationRepository> logger,
        IAuditContext auditContext
    )
        : this(databaseOptions, logger, auditContext, new TenantContextProvider()) { }

    private TenantContext TenantContext => tenantContextProvider.Context;

    private long? TenantId => TenantContext is TenantContext.Multitenant mt ? mt.TenantId : null;

    private async Task<bool> AreDataStoresVisible(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long[] dataStoreIds
    )
    {
        long[] distinctDataStoreIds = dataStoreIds.Distinct().ToArray();
        if (distinctDataStoreIds.Length == 0)
        {
            return true;
        }

        string sql = $"""
            SELECT COUNT(DISTINCT ds."Id")
            FROM "dmscs"."DataStore" ds
            WHERE ds."Id" = ANY(@DataStoreIds) AND {TenantContext.TenantWhereClause("ds")};
            """;

        int visibleDataStoreCount = await connection.ExecuteScalarAsync<int>(
            sql,
            new { DataStoreIds = distinctDataStoreIds, TenantId },
            transaction
        );

        return visibleDataStoreCount == distinctDataStoreIds.Length;
    }

    private async Task<bool> IsApplicationVisible(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long applicationId
    )
    {
        string sql = $"""
            SELECT EXISTS(
                SELECT 1
                FROM "dmscs"."Application" a
                JOIN "dmscs"."Vendor" v ON v."Id" = a."VendorId"
                WHERE a."Id" = @ApplicationId AND {TenantContext.TenantWhereClause("v")}
            );
            """;

        return await connection.ExecuteScalarAsync<bool>(
            sql,
            new { ApplicationId = applicationId, TenantId },
            transaction
        );
    }

    private async Task<bool> IsVendorVisible(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long vendorId
    )
    {
        string sql = $"""
            SELECT EXISTS(
                SELECT 1
                FROM "dmscs"."Vendor" v
                WHERE v."Id" = @VendorId AND {TenantContext.TenantWhereClause("v")}
            );
            """;

        return await connection.ExecuteScalarAsync<bool>(
            sql,
            new { VendorId = vendorId, TenantId },
            transaction
        );
    }

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
            string sql = $"""
                INSERT INTO "dmscs"."Application" ("ApplicationName", "VendorId", "ClaimSetName", "CreatedBy")
                SELECT @ApplicationName, v."Id", @ClaimSetName, @CreatedBy
                FROM "dmscs"."Vendor" v
                WHERE v."Id" = @VendorId AND {TenantContext.TenantWhereClause("v")}
                RETURNING "Id";
                """;

            long? id = await connection.ExecuteScalarAsync<long?>(
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

            if (id is null)
            {
                await transaction.RollbackAsync();
                return new ApplicationInsertResult.FailureVendorNotFound();
            }

            sql = """
                INSERT INTO "dmscs"."ApplicationEducationOrganization" ("ApplicationId", "EducationOrganizationId", "CreatedBy")
                VALUES (@ApplicationId, @EducationOrganizationId, @CreatedBy);
                """;

            var currentUser = auditContext.GetCurrentUser();
            var educationOrganizations = command.EducationOrganizationIds.Select(e => new
            {
                ApplicationId = id.Value,
                EducationOrganizationId = e,
                CreatedBy = currentUser,
            });

            await connection.ExecuteAsync(sql, educationOrganizations, transaction);

            sql = """
                INSERT INTO "dmscs"."ApiClient" ("ApplicationId", "ClientId", "ClientUuid", "Name", "IsApproved", "CreatedBy")
                VALUES (@ApplicationId, @ClientId, @ClientUuid, @Name, @IsApproved, @CreatedBy)
                RETURNING "Id";
                """;

            long apiClientId = await connection.ExecuteScalarAsync<long>(
                sql,
                new
                {
                    ApplicationId = id.Value,
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
                if (!await AreDataStoresVisible(connection, transaction, command.DataStoreIds))
                {
                    await transaction.RollbackAsync();
                    return new ApplicationInsertResult.FailureDataStoreNotFound();
                }

                sql = $"""
                    INSERT INTO "dmscs"."ApiClientDataStore" ("ApiClientId", "DataStoreId", "CreatedBy")
                    SELECT @ApiClientId, ds."Id", @CreatedBy
                    FROM "dmscs"."DataStore" ds
                    WHERE ds."Id" = ANY(@DataStoreIds) AND {TenantContext.TenantWhereClause("ds")};
                    """;

                await connection.ExecuteAsync(
                    sql,
                    new
                    {
                        ApiClientId = apiClientId,
                        DataStoreIds = command.DataStoreIds.Distinct().ToArray(),
                        CreatedBy = currentUser,
                        TenantId,
                    },
                    transaction
                );
            }

            if (command.ProfileIds.Length > 0)
            {
                sql = """
                    INSERT INTO "dmscs"."ApplicationProfile" ("ApplicationId", "ProfileId", "CreatedBy")
                    VALUES (@ApplicationId, @ProfileId, @CreatedBy);
                    """;

                var profileMappings = command
                    .ProfileIds.Distinct()
                    .Select(profileId => new
                    {
                        ApplicationId = id.Value,
                        ProfileId = profileId,
                        CreatedBy = currentUser,
                    });

                await connection.ExecuteAsync(sql, profileMappings, transaction);
            }

            await transaction.CommitAsync();
            return new ApplicationInsertResult.Success(id.Value);
        }
        catch (PostgresException ex)
            when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation
                && ex.ConstraintName == "FK_Application_Vendor"
            )
        {
            logger.LogWarning(ex, "Vendor not found");
            await transaction.RollbackAsync();
            return new ApplicationInsertResult.FailureVendorNotFound();
        }
        catch (PostgresException ex)
            when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation
                && ex.ConstraintName == "FK_ApiClientDataStore_DataStore"
            )
        {
            logger.LogWarning(ex, "Data store not found");
            await transaction.RollbackAsync();
            return new ApplicationInsertResult.FailureDataStoreNotFound();
        }
        catch (PostgresException ex)
            when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation
                && ex.ConstraintName == "FK_ApplicationProfile_Profile"
            )
        {
            logger.LogWarning(ex, "Profile not found");
            await transaction.RollbackAsync();
            return new ApplicationInsertResult.FailureProfileNotFound();
        }
        catch (PostgresException ex)
            when (ex.SqlState == PostgresErrorCodes.UniqueViolation
                && ex.ConstraintName == "UX_Application_VendorId_ApplicationName"
            )
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
        ["id"] = "\"Id\"",
        ["applicationName"] = "\"ApplicationName\"",
        ["vendorId"] = "\"VendorId\"",
        ["claimSetName"] = "\"ClaimSetName\"",
    };

    private static string ResolveOrderByColumn(ApplicationQuery query) =>
        query.OrderBy is not null && OrderByColumns.TryGetValue(query.OrderBy, out var col)
            ? col
            : "\"ApplicationName\"";

    private static string QualifyColumn(string tableAlias, string quotedColumn) =>
        $"{tableAlias}.{quotedColumn}";

    private static string BuildOrderByClause(ApplicationQuery query, string tableAlias)
    {
        string col = ResolveOrderByColumn(query);
        return $"ORDER BY {QualifyColumn(tableAlias, col)} {(query.IsDescending ? "DESC" : "ASC")}";
    }

    private static string BuildFilterClause(ApplicationQuery query, int[] parsedIds, string tableAlias)
    {
        var conditions = new List<string>();
        if (query.Id.HasValue)
        {
            conditions.Add($"{QualifyColumn(tableAlias, "\"Id\"")} = @Id");
        }
        if (query.ApplicationName is not null)
        {
            conditions.Add($"{QualifyColumn(tableAlias, "\"ApplicationName\"")} = @ApplicationName");
        }
        if (query.ClaimSetName is not null)
        {
            conditions.Add($"{QualifyColumn(tableAlias, "\"ClaimSetName\"")} = @ClaimSetName");
        }
        if (parsedIds.Length > 0)
        {
            conditions.Add($"{QualifyColumn(tableAlias, "\"Id\"")} = ANY(@ParsedIds)");
        }
        return conditions.Count > 0 ? " AND " + string.Join(" AND ", conditions) : string.Empty;
    }

    public async Task<ApplicationQueryResult> QueryApplication(ApplicationQuery query)
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
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
            string orderByClause = BuildOrderByClause(query, "a");
            string filterClause = BuildFilterClause(query, parsedIds, "a");
            string outerCol = ResolveOrderByColumn(query);
            // Direction mirrors BuildOrderByClause() — must stay consistent.
            string direction = query.IsDescending ? "DESC" : "ASC";
            string sql = $"""
                SELECT a."Id", a."ApplicationName", a."VendorId", a."ClaimSetName",
                       (SELECT COALESCE(BOOL_AND(ac2."IsApproved"), true)
                        FROM "dmscs"."ApiClient" ac2
                        WHERE ac2."ApplicationId" = a."Id") AS "Enabled",
                       e."EducationOrganizationId", acd."DataStoreId", ap."ProfileId"
                FROM (
                    SELECT a.*
                    FROM "dmscs"."Application" a
                    JOIN "dmscs"."Vendor" v ON v."Id" = a."VendorId"
                    WHERE {TenantContext.TenantWhereClause("v")}{filterClause}
                    {orderByClause}
                    {query.BuildPagingClause()}
                ) AS a
                LEFT OUTER JOIN "dmscs"."ApplicationEducationOrganization" e ON a."Id" = e."ApplicationId"
                LEFT OUTER JOIN "dmscs"."ApiClient" ac ON a."Id" = ac."ApplicationId"
                LEFT OUTER JOIN "dmscs"."ApiClientDataStore" acd ON ac."Id" = acd."ApiClientId"
                LEFT OUTER JOIN "dmscs"."ApplicationProfile" ap ON a."Id" = ap."ApplicationId"
                ORDER BY {QualifyColumn("a", outerCol)} {direction};
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
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();
        try
        {
            string sql = $"""
                SELECT a."Id", a."ApplicationName", a."VendorId", a."ClaimSetName",
                       (SELECT COALESCE(BOOL_AND(ac2."IsApproved"), true)
                        FROM "dmscs"."ApiClient" ac2
                        WHERE ac2."ApplicationId" = a."Id") AS "Enabled",
                       e."EducationOrganizationId", acd."DataStoreId", ap."ProfileId"
                FROM "dmscs"."Application" a
                JOIN "dmscs"."Vendor" v ON v."Id" = a."VendorId"
                LEFT OUTER JOIN "dmscs"."ApplicationEducationOrganization" e ON a."Id" = e."ApplicationId"
                LEFT OUTER JOIN "dmscs"."ApiClient" ac ON a."Id" = ac."ApplicationId"
                LEFT OUTER JOIN "dmscs"."ApiClientDataStore" acd ON ac."Id" = acd."ApiClientId"
                LEFT OUTER JOIN "dmscs"."ApplicationProfile" ap ON a."Id" = ap."ApplicationId"
                WHERE a."Id" = @Id AND {TenantContext.TenantWhereClause("v")};
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
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            if (!await IsApplicationVisible(connection, transaction, command.Id))
            {
                await transaction.RollbackAsync();
                return new ApplicationUpdateResult.FailureNotExists();
            }

            if (!await IsVendorVisible(connection, transaction, command.VendorId))
            {
                await transaction.RollbackAsync();
                return new ApplicationUpdateResult.FailureVendorNotFound();
            }

            if (!await AreDataStoresVisible(connection, transaction, command.DataStoreIds))
            {
                await transaction.RollbackAsync();
                return new ApplicationUpdateResult.FailureDataStoreNotFound();
            }

            string sql = """
                UPDATE "dmscs"."Application"
                SET "ApplicationName"=@ApplicationName, "VendorId"=@VendorId, "ClaimSetName"=@ClaimSetName,
                    "LastModifiedAt"=@LastModifiedAt, "ModifiedBy"=@ModifiedBy
                WHERE "Id" = @Id;
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
                },
                transaction
            );

            if (affectedRows == 0)
            {
                await transaction.RollbackAsync();
                return new ApplicationUpdateResult.FailureNotExists();
            }

            sql =
                "DELETE FROM \"dmscs\".\"ApplicationEducationOrganization\" WHERE \"ApplicationId\" = @ApplicationId";
            await connection.ExecuteAsync(sql, new { ApplicationId = command.Id }, transaction);

            sql = """
                INSERT INTO "dmscs"."ApplicationEducationOrganization" ("ApplicationId", "EducationOrganizationId", "CreatedBy")
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
                UPDATE "dmscs"."ApiClient"
                SET "ClientUuid"=@ClientUuid, "LastModifiedAt"=@LastModifiedAt, "ModifiedBy"=@ModifiedBy
                WHERE "ClientId" = @ClientId AND "ApplicationId" = @ApplicationId;
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
                "SELECT \"Id\" FROM \"dmscs\".\"ApiClient\" WHERE \"ClientId\" = @ClientId AND \"ApplicationId\" = @ApplicationId;";
            long apiClientId = await connection.ExecuteScalarAsync<long>(
                sql,
                new { clientCommand.ClientId, ApplicationId = command.Id },
                transaction
            );

            // Delete existing DataStore relationship
            sql = "DELETE FROM \"dmscs\".\"ApiClientDataStore\" WHERE \"ApiClientId\" = @ApiClientId";
            await connection.ExecuteAsync(sql, new { ApiClientId = apiClientId }, transaction);

            // Insert new DataStore relationships if provided
            if (command.DataStoreIds.Length > 0)
            {
                sql = $"""
                    INSERT INTO "dmscs"."ApiClientDataStore" ("ApiClientId", "DataStoreId", "CreatedBy")
                    SELECT @ApiClientId, ds."Id", @CreatedBy
                    FROM "dmscs"."DataStore" ds
                    WHERE ds."Id" = ANY(@DataStoreIds) AND {TenantContext.TenantWhereClause("ds")};
                    """;

                await connection.ExecuteAsync(
                    sql,
                    new
                    {
                        ApiClientId = apiClientId,
                        DataStoreIds = command.DataStoreIds.Distinct().ToArray(),
                        CreatedBy = currentUser,
                        TenantId,
                    },
                    transaction
                );
            }

            // Delete existing Profile relationships
            sql = "DELETE FROM \"dmscs\".\"ApplicationProfile\" WHERE \"ApplicationId\" = @ApplicationId";
            await connection.ExecuteAsync(sql, new { ApplicationId = command.Id }, transaction);

            // Insert new Profile relationships if provided
            if (command.ProfileIds.Length > 0)
            {
                sql = """
                    INSERT INTO "dmscs"."ApplicationProfile" ("ApplicationId", "ProfileId", "CreatedBy")
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
        catch (PostgresException ex)
            when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation
                && ex.ConstraintName == "FK_Application_Vendor"
            )
        {
            logger.LogWarning(ex, "Update application failure: Vendor not found");
            await transaction.RollbackAsync();
            return new ApplicationUpdateResult.FailureVendorNotFound();
        }
        catch (PostgresException ex)
            when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation
                && ex.ConstraintName == "FK_ApiClientDataStore_DataStore"
            )
        {
            logger.LogWarning(ex, "Update application failure: Data store not found");
            await transaction.RollbackAsync();
            return new ApplicationUpdateResult.FailureDataStoreNotFound();
        }
        catch (PostgresException ex)
            when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation
                && ex.ConstraintName == "FK_ApplicationProfile_Profile"
            )
        {
            logger.LogWarning(ex, "Update application failure: Profile not found");
            await transaction.RollbackAsync();
            return new ApplicationUpdateResult.FailureProfileNotFound();
        }
        catch (PostgresException ex)
            when (ex.SqlState == PostgresErrorCodes.UniqueViolation
                && ex.ConstraintName == "UX_Application_VendorId_ApplicationName"
            )
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
        await connection.OpenAsync();
        try
        {
            string sql = $"""
                DELETE FROM "dmscs"."Application" a
                USING "dmscs"."Vendor" v
                WHERE a."Id" = @Id
                  AND v."Id" = a."VendorId"
                  AND {TenantContext.TenantWhereClause("v")};
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
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();
        try
        {
            string sql = $"""
                SELECT ac."ClientId", ac."ClientUuid", ac."IsApproved"
                FROM "dmscs"."ApiClient" ac
                JOIN "dmscs"."Application" a ON a."Id" = ac."ApplicationId"
                JOIN "dmscs"."Vendor" v ON v."Id" = a."VendorId"
                WHERE ac."ApplicationId" = @Id AND {TenantContext.TenantWhereClause("v")}
                ORDER BY ac."Id"
                """;

            var clients = await connection.QueryAsync<ApiClient>(sql, new { Id = id, TenantId });

            return new ApplicationApiClientsResult.Success(clients.ToArray());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Get application clients failure");
            return new ApplicationApiClientsResult.FailureUnknown(ex.Message);
        }
    }
}
