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
    private TenantContext TenantContext => tenantContextProvider.Context;

    private long? TenantId => TenantContext is TenantContext.Multitenant mt ? mt.TenantId : null;

    private const string DuplicateClientIdConstraint = "UX_ApiClient_ClientId";
    private const string DuplicateClientUuidConstraint = "UX_ApiClient_ClientUuid";

    /// <summary>
    /// SQL condition constraining an Application row to the current tenant through its owning Vendor.
    /// </summary>
    private string TenantScopedVendorCondition(string? tableAlias = null)
    {
        var column = string.IsNullOrEmpty(tableAlias) ? "\"VendorId\"" : $"{tableAlias}.\"VendorId\"";
        return $"""
            {column} IN (
                SELECT "Id" FROM "dmscs"."Vendor" WHERE {TenantContext.TenantWhereClause()}
            )
            """;
    }

    private async Task<bool> AllDataStoresInTenant(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int[] dataStoreIds
    )
    {
        string sql = $"""
            SELECT COUNT(1) FROM "dmscs"."DataStore"
            WHERE "Id" = ANY(@DataStoreIds) AND {TenantContext.TenantWhereClause()};
            """;
        int count = await connection.ExecuteScalarAsync<int>(
            sql,
            new { DataStoreIds = dataStoreIds, TenantId },
            transaction
        );
        return count == dataStoreIds.Distinct().Count();
    }

    /// <summary>
    /// True when every requested profile belongs to the current tenant. A profile created in another
    /// tenant counts as missing, exactly like an id that does not exist.
    /// </summary>
    private async Task<bool> AllProfilesInTenant(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int[] profileIds
    )
    {
        int[] distinctProfileIds = [.. profileIds.Distinct()];
        string sql = $"""
            SELECT COUNT(1) FROM "dmscs"."Profile"
            WHERE "Id" = ANY(@ProfileIds) AND {TenantContext.TenantWhereClause()};
            """;
        int count = await connection.ExecuteScalarAsync<int>(
            sql,
            new { ProfileIds = distinctProfileIds, TenantId },
            transaction
        );
        return count == distinctProfileIds.Length;
    }

    private async Task<bool> ApplicationExistsForTenant(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int id
    )
    {
        string sql = $"""
            SELECT COUNT(1) FROM "dmscs"."Application"
            WHERE "Id" = @Id AND {TenantScopedVendorCondition()};
            """;
        return await connection.ExecuteScalarAsync<int>(sql, new { Id = id, TenantId }, transaction) > 0;
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
                SELECT @ApplicationName, @VendorId, @ClaimSetName, @CreatedBy
                WHERE EXISTS (
                    SELECT 1 FROM "dmscs"."Vendor"
                    WHERE "Id" = @VendorId AND {TenantContext.TenantWhereClause()}
                )
                RETURNING "Id";
                """;

            int? insertedId = await connection.ExecuteScalarAsync<int?>(
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

            int id = insertedId.Value;

            sql = """
                INSERT INTO "dmscs"."ApplicationEducationOrganization" ("ApplicationId", "EducationOrganizationId", "CreatedBy")
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

            // Without default credentials the Application is created with no API client, so
            // there is no client row for data store assignments to attach to.
            if (command.CreateDefaultCredentials)
            {
                sql = """
                    INSERT INTO "dmscs"."ApiClient" ("ApplicationId", "ClientId", "ClientUuid", "Name", "IsApproved", "CreatedBy")
                    VALUES (@ApplicationId, @ClientId, @ClientUuid, @Name, @IsApproved, @CreatedBy)
                    RETURNING "Id";
                    """;

                int apiClientId = await connection.ExecuteScalarAsync<int>(
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

                // The default client inherits, so its rows are the Application's list
                sql = """
                    INSERT INTO "dmscs"."ApiClientEducationOrganization" ("ApiClientId", "ApplicationId", "EducationOrganizationId", "CreatedBy")
                    SELECT @ApiClientId, "ApplicationId", "EducationOrganizationId", @CreatedBy
                    FROM "dmscs"."ApplicationEducationOrganization"
                    WHERE "ApplicationId" = @ApplicationId;
                    """;
                await connection.ExecuteAsync(
                    sql,
                    new
                    {
                        ApiClientId = apiClientId,
                        ApplicationId = id,
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
                        INSERT INTO "dmscs"."ApiClientDataStore" ("ApiClientId", "DataStoreId", "CreatedBy")
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
            }

            if (command.ProfileIds.Length > 0)
            {
                if (!await AllProfilesInTenant(connection, transaction, command.ProfileIds))
                {
                    logger.LogWarning("Profile not found");
                    await transaction.RollbackAsync();
                    return new ApplicationInsertResult.FailureProfileNotFound();
                }

                sql = """
                    INSERT INTO "dmscs"."ApplicationProfile" ("ApplicationId", "ProfileId", "CreatedBy")
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
        catch (PostgresException ex)
            when (ex.SqlState == PostgresErrorCodes.UniqueViolation
                && ex.ConstraintName == DuplicateClientIdConstraint
            )
        {
            // With IncludeErrorDetail the exception carries the conflicting key, so only the
            // fixed text and the safe diagnostic fields are logged, never the exception itself.
#pragma warning disable S6667 // Logging in a catch clause should pass the caught exception - deliberately omitted, see comment above
            logger.LogWarning(
                "API client key is already in use ({SqlState}, {ConstraintName})",
                ex.SqlState,
                ex.ConstraintName
            );
#pragma warning restore S6667
            await transaction.RollbackAsync();
            return new ApplicationInsertResult.FailureDuplicateClientId();
        }
        catch (PostgresException ex)
            when (ex.SqlState == PostgresErrorCodes.UniqueViolation
                && ex.ConstraintName == DuplicateClientUuidConstraint
            )
        {
            // With IncludeErrorDetail the exception carries the conflicting UUID, so only the
            // fixed text and the safe diagnostic fields are logged, never the exception itself.
#pragma warning disable S6667 // Logging in a catch clause should pass the caught exception - deliberately omitted, see comment above
            logger.LogWarning(
                "Identity provider client is already associated with another API client ({SqlState}, {ConstraintName})",
                ex.SqlState,
                ex.ConstraintName
            );
#pragma warning restore S6667
            await transaction.RollbackAsync();
            return new ApplicationInsertResult.FailureDuplicateClientUuid();
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

    private static string ResolveOrderByColumnName(ApplicationQuery query) =>
        query.OrderBy is not null && OrderByColumns.TryGetValue(query.OrderBy, out var col)
            ? col
            : "ApplicationName";

    private static string BuildOrderByClause(ApplicationQuery query, string? tableAlias = null) =>
        PostgresqlIdentifier.OrderBy(ResolveOrderByColumnName(query), query.IsDescending, tableAlias);

    private string BuildFilterClause(ApplicationQuery query, int[] parsedIds)
    {
        var conditions = new List<string> { TenantScopedVendorCondition() };
        if (query.Id.HasValue)
        {
            conditions.Add("\"Id\" = @Id");
        }
        if (query.ApplicationName is not null)
        {
            conditions.Add("\"ApplicationName\" = @ApplicationName");
        }
        if (query.ClaimSetName is not null)
        {
            conditions.Add("\"ClaimSetName\" = @ClaimSetName");
        }
        if (parsedIds.Length > 0)
        {
            conditions.Add("\"Id\" = ANY(@ParsedIds)");
        }
        return "WHERE " + string.Join(" AND ", conditions);
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
            string orderByClause = BuildOrderByClause(query);
            string filterClause = BuildFilterClause(query, parsedIds);
            string outerOrderByClause = BuildOrderByClause(query, "a");
            string sql = $"""
                SELECT a."Id", a."ApplicationName", a."VendorId", a."ClaimSetName",
                       (SELECT COALESCE(BOOL_AND(ac2."IsApproved"), false)
                        FROM "dmscs"."ApiClient" ac2
                        WHERE ac2."ApplicationId" = a."Id") AS "Enabled",
                       e."EducationOrganizationId", acd."DataStoreId", ap."ProfileId"
                FROM (SELECT * FROM "dmscs"."Application" {filterClause} {orderByClause} {query.BuildPagingClause()}) AS a
                LEFT OUTER JOIN "dmscs"."ApplicationEducationOrganization" e ON a."Id" = e."ApplicationId"
                LEFT OUTER JOIN "dmscs"."ApiClient" ac ON a."Id" = ac."ApplicationId"
                LEFT OUTER JOIN "dmscs"."ApiClientDataStore" acd ON ac."Id" = acd."ApiClientId"
                LEFT OUTER JOIN "dmscs"."ApplicationProfile" ap ON a."Id" = ap."ApplicationId"
                {outerOrderByClause};
                """;
            var applications = await connection.QueryAsync<
                ApplicationResponse,
                long?,
                int?,
                int?,
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

    public async Task<ApplicationGetResult> GetApplication(int id)
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();
        try
        {
            string sql = $"""
                SELECT a."Id", a."ApplicationName", a."VendorId", a."ClaimSetName",
                       (SELECT COALESCE(BOOL_AND(ac2."IsApproved"), false)
                        FROM "dmscs"."ApiClient" ac2
                        WHERE ac2."ApplicationId" = a."Id") AS "Enabled",
                       e."EducationOrganizationId", acd."DataStoreId", ap."ProfileId"
                FROM "dmscs"."Application" a
                LEFT OUTER JOIN "dmscs"."ApplicationEducationOrganization" e ON a."Id" = e."ApplicationId"
                LEFT OUTER JOIN "dmscs"."ApiClient" ac ON a."Id" = ac."ApplicationId"
                LEFT OUTER JOIN "dmscs"."ApiClientDataStore" acd ON ac."Id" = acd."ApiClientId"
                LEFT OUTER JOIN "dmscs"."ApplicationProfile" ap ON a."Id" = ap."ApplicationId"
                WHERE a."Id" = @Id AND {TenantScopedVendorCondition("a")};
                """;
            var applications = await connection.QueryAsync<
                ApplicationResponse,
                long?,
                int?,
                int?,
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

    public async Task<ApplicationSaveResult> SaveApplication(ApplicationUpdateCommand command)
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        bool committing = false;
        try
        {
            // FOR UPDATE, not the UPDATE's weaker lock, also holds off a client being added to or
            // moved into the Application until this transaction ends.
            string applicationSql = $"""
                SELECT "ApplicationName", "ClaimSetName"
                FROM "dmscs"."Application"
                WHERE "Id" = @Id AND {TenantScopedVendorCondition()}
                FOR UPDATE;
                """;
            var current = await connection.QuerySingleOrDefaultAsync<(
                string ApplicationName,
                string ClaimSetName
            )?>(applicationSql, new { command.Id, TenantId }, transaction);

            if (current is null)
            {
                await RollbackSafelyAsync(transaction);
                return new ApplicationSaveResult.FailureNotExists();
            }

            long[] currentEducationOrganizationIds = await ApplicationEducationOrganizationIdsAsync(
                connection,
                transaction,
                command.Id
            );
            long[] requestedEducationOrganizationIds = command.EducationOrganizationIds.Distinct().ToArray();
            long[] removedEducationOrganizationIds = currentEducationOrganizationIds
                .Except(requestedEducationOrganizationIds)
                .ToArray();
            // Marking precedes the removal: once a removed row cascades from a narrowed client's
            // rows, nothing shows that the client held it. The statement also returns, and locks,
            // every client already pending, so the result names all of them.
            string markSql = """
                UPDATE "dmscs"."ApiClient" ac
                SET "IdentityProviderSyncPendingSince" = COALESCE(ac."IdentityProviderSyncPendingSince", @PendingSince)
                WHERE ac."ApplicationId" = @ApplicationId
                  AND (
                      ac."IdentityProviderSyncPendingSince" IS NOT NULL
                      OR @AllClientsAffected
                      OR (@EducationOrganizationsChanged AND ac."InheritsApplicationEducationOrganizations")
                      OR EXISTS (
                          SELECT 1 FROM "dmscs"."ApiClientEducationOrganization" ace
                          WHERE ace."ApiClientId" = ac."Id"
                            AND ace."EducationOrganizationId" = ANY(@RemovedEducationOrganizationIds)
                      )
                  )
                RETURNING ac."Id", ac."ClientId", ac."ClientUuid", ac."IsApproved",
                    ac."InheritsApplicationEducationOrganizations", ac."IdentityProviderSyncPendingSince";
                """;
            PendingClientRow[] pendingClients =
            [
                .. await connection.QueryAsync<PendingClientRow>(
                    markSql,
                    new
                    {
                        ApplicationId = command.Id,
                        PendingSince = auditContext.GetCurrentTimestamp(),
                        AllClientsAffected = current.Value.ApplicationName != command.ApplicationName
                            || current.Value.ClaimSetName != command.ClaimSetName,
                        EducationOrganizationsChanged = removedEducationOrganizationIds.Length > 0
                            || requestedEducationOrganizationIds
                                .Except(currentEducationOrganizationIds)
                                .Any(),
                        RemovedEducationOrganizationIds = removedEducationOrganizationIds,
                    },
                    transaction
                ),
            ];

            if (await ApplyApplicationChangesAsync(connection, transaction, command) is { } failure)
            {
                await RollbackSafelyAsync(transaction);
                return failure;
            }

            ApplicationSyncState state = await ReadSyncStateAsync(
                connection,
                transaction,
                command.Id,
                pendingClients
            );

            committing = true;
            await transaction.CommitAsync();
            return new ApplicationSaveResult.Success(state);
        }
        catch (Exception ex) when (committing)
        {
            logger.LogError(ex, "Save application commit failure; the outcome is unknown");
            return new ApplicationSaveResult.FailureCommitOutcomeUnknown(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Save application failure");
            await RollbackSafelyAsync(transaction);
            return new ApplicationSaveResult.FailureUnknown(ex.Message);
        }
    }

    public async Task<ApplicationSyncStateResult> GetApplicationSyncState(int applicationId)
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            // Locking the Application row waits out any in-flight save, so the reads below observe
            // that save's final outcome.
            string lockSql = $"""
                SELECT 1 FROM "dmscs"."Application"
                WHERE "Id" = @Id AND {TenantScopedVendorCondition()}
                FOR UPDATE;
                """;
            if (
                await connection.ExecuteScalarAsync<int?>(
                    lockSql,
                    new { Id = applicationId, TenantId },
                    transaction
                )
                is null
            )
            {
                await RollbackSafelyAsync(transaction);
                return new ApplicationSyncStateResult.FailureNotExists();
            }

            PendingClientRow[] pendingClients =
            [
                .. await connection.QueryAsync<PendingClientRow>(
                    """
                    SELECT "Id", "ClientId", "ClientUuid", "IsApproved",
                        "InheritsApplicationEducationOrganizations", "IdentityProviderSyncPendingSince"
                    FROM "dmscs"."ApiClient"
                    WHERE "ApplicationId" = @ApplicationId AND "IdentityProviderSyncPendingSince" IS NOT NULL;
                    """,
                    new { ApplicationId = applicationId },
                    transaction
                ),
            ];
            ApplicationSyncState state = await ReadSyncStateAsync(
                connection,
                transaction,
                applicationId,
                pendingClients
            );
            await transaction.CommitAsync();
            return new ApplicationSyncStateResult.Success(state);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Get application sync state failure");
            await RollbackSafelyAsync(transaction);
            return new ApplicationSyncStateResult.FailureUnknown(ex.Message);
        }
    }

    /// <summary>
    /// Saves the Application's scalars, education organizations and profiles, returning the failure
    /// that leaves the transaction to be rolled back, or null. The Application UPDATE comes first and
    /// its row lock covers the list read that follows. Only the education organization
    /// differences are applied: deleting a row cascades to every API client's copy of it, so a
    /// surviving education organization must keep its row.
    /// </summary>
    private async Task<ApplicationSaveResult?> ApplyApplicationChangesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ApplicationUpdateCommand command
    )
    {
        try
        {
            string sql = $"""
                UPDATE "dmscs"."Application"
                SET "ApplicationName"=@ApplicationName, "VendorId"=@VendorId, "ClaimSetName"=@ClaimSetName,
                    "LastModifiedAt"=@LastModifiedAt, "ModifiedBy"=@ModifiedBy
                WHERE "Id" = @Id
                  AND {TenantScopedVendorCondition()}
                  AND EXISTS (
                      SELECT 1 FROM "dmscs"."Vendor"
                      WHERE "Id" = @VendorId AND {TenantContext.TenantWhereClause()}
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
                    return new ApplicationSaveResult.FailureNotExists();
                }

                logger.LogWarning("Update application failure: Vendor not found");
                return new ApplicationSaveResult.FailureVendorNotFound();
            }

            if (
                command.ProfileIds.Length > 0
                && !await AllProfilesInTenant(connection, transaction, command.ProfileIds)
            )
            {
                logger.LogWarning("Update application failure: Profile not found");
                return new ApplicationSaveResult.FailureProfileNotFound();
            }

            // Read under the Application row lock the UPDATE above holds, so a concurrent change to the
            // list committed while this transaction waited is already visible
            long[] currentEducationOrganizationIds = await ApplicationEducationOrganizationIdsAsync(
                connection,
                transaction,
                command.Id
            );
            long[] requestedEducationOrganizationIds = command.EducationOrganizationIds.Distinct().ToArray();

            sql = """
                DELETE FROM "dmscs"."ApplicationEducationOrganization"
                WHERE "ApplicationId" = @ApplicationId AND "EducationOrganizationId" = @EducationOrganizationId;
                """;
            var removedEducationOrganizations = currentEducationOrganizationIds
                .Except(requestedEducationOrganizationIds)
                .Select(e => new { ApplicationId = command.Id, EducationOrganizationId = e });

            await connection.ExecuteAsync(sql, removedEducationOrganizations, transaction);

            sql = """
                INSERT INTO "dmscs"."ApplicationEducationOrganization" ("ApplicationId", "EducationOrganizationId", "CreatedBy")
                VALUES (@ApplicationId, @EducationOrganizationId, @CreatedBy);
                """;

            var currentUser = auditContext.GetCurrentUser();
            var addedEducationOrganizations = requestedEducationOrganizationIds
                .Except(currentEducationOrganizationIds)
                .Select(e => new
                {
                    ApplicationId = command.Id,
                    EducationOrganizationId = e,
                    CreatedBy = currentUser,
                });

            await connection.ExecuteAsync(sql, addedEducationOrganizations, transaction);

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

            return null;
        }
        catch (PostgresException ex)
            when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation
                && ex.ConstraintName == "FK_Application_Vendor"
            )
        {
            logger.LogWarning(ex, "Update application failure: Vendor not found");
            return new ApplicationSaveResult.FailureVendorNotFound();
        }
        catch (PostgresException ex)
            when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation
                && ex.ConstraintName == "FK_ApplicationProfile_Profile"
            )
        {
            logger.LogWarning(ex, "Update application failure: Profile not found");
            return new ApplicationSaveResult.FailureProfileNotFound();
        }
        catch (PostgresException ex)
            when (ex.SqlState == PostgresErrorCodes.UniqueViolation
                && ex.ConstraintName == "UX_Application_VendorId_ApplicationName"
            )
        {
            // The exception's detail carries the raw name, so only the sanitized name and the safe
            // diagnostic fields are logged, never the exception itself.
#pragma warning disable S6667 // Logging in a catch clause should pass the caught exception - deliberately omitted, see comment above
            logger.LogWarning(
                "Application '{ApplicationName}' already exists for vendor ({SqlState}, {ConstraintName})",
                LoggingUtility.SanitizeForLog(command.ApplicationName),
                ex.SqlState,
                ex.ConstraintName
            );
#pragma warning restore S6667
            return new ApplicationSaveResult.FailureDuplicateApplication(command.ApplicationName);
        }
    }

    private static async Task<long[]> ApplicationEducationOrganizationIdsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int applicationId
    ) =>
        [
            .. await connection.QueryAsync<long>(
                """
                SELECT "EducationOrganizationId" FROM "dmscs"."ApplicationEducationOrganization"
                WHERE "ApplicationId" = @ApplicationId;
                """,
                new { ApplicationId = applicationId },
                transaction
            ),
        ];

    private sealed record PendingClientRow(
        int Id,
        string ClientId,
        Guid ClientUuid,
        bool IsApproved,
        bool InheritsApplicationEducationOrganizations,
        DateTime IdentityProviderSyncPendingSince
    );

    /// <summary>
    /// Reads the Application and completes the given pending clients with their data stores and
    /// effective education organizations, each list read for all of the clients at once.
    /// </summary>
    private static async Task<ApplicationSyncState> ReadSyncStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int applicationId,
        PendingClientRow[] pendingClients
    )
    {
        var application = await connection.QuerySingleAsync<(
            string ApplicationName,
            int VendorId,
            string ClaimSetName,
            long[] EducationOrganizationIds,
            int[] ProfileIds
        )>(
            """
            SELECT a."ApplicationName", a."VendorId", a."ClaimSetName",
                ARRAY(
                    SELECT aeo."EducationOrganizationId" FROM "dmscs"."ApplicationEducationOrganization" aeo
                    WHERE aeo."ApplicationId" = a."Id"
                    ORDER BY aeo."EducationOrganizationId"
                ) AS "EducationOrganizationIds",
                ARRAY(
                    SELECT ap."ProfileId" FROM "dmscs"."ApplicationProfile" ap
                    WHERE ap."ApplicationId" = a."Id"
                    ORDER BY ap."ProfileId"
                ) AS "ProfileIds"
            FROM "dmscs"."Application" a
            WHERE a."Id" = @ApplicationId;
            """,
            new { ApplicationId = applicationId },
            transaction
        );

        int[] pendingClientIds = [.. pendingClients.Select(client => client.Id)];

        ILookup<int, int> dataStoreIds = (
            await connection.QueryAsync<(int ApiClientId, int DataStoreId)>(
                """
                SELECT "ApiClientId", "DataStoreId" FROM "dmscs"."ApiClientDataStore"
                WHERE "ApiClientId" = ANY(@ApiClientIds)
                ORDER BY "ApiClientId", "DataStoreId";
                """,
                new { ApiClientIds = pendingClientIds },
                transaction
            )
        ).ToLookup(row => row.ApiClientId, row => row.DataStoreId);

        ILookup<int, long> storedEducationOrganizationIds = (
            await connection.QueryAsync<(int ApiClientId, long EducationOrganizationId)>(
                """
                SELECT "ApiClientId", "EducationOrganizationId" FROM "dmscs"."ApiClientEducationOrganization"
                WHERE "ApiClientId" = ANY(@ApiClientIds)
                ORDER BY "ApiClientId", "EducationOrganizationId";
                """,
                new { ApiClientIds = pendingClientIds },
                transaction
            )
        ).ToLookup(row => row.ApiClientId, row => row.EducationOrganizationId);

        return new ApplicationSyncState(
            application.ApplicationName,
            application.VendorId,
            application.ClaimSetName,
            application.EducationOrganizationIds,
            application.ProfileIds,
            [
                .. pendingClients
                    .OrderBy(client => client.Id)
                    .Select(client => new ApplicationSyncPendingClient(
                        client.Id,
                        client.ClientId,
                        client.ClientUuid,
                        client.IsApproved,
                        [.. dataStoreIds[client.Id]],
                        client.InheritsApplicationEducationOrganizations
                            ? application.EducationOrganizationIds
                            : [.. storedEducationOrganizationIds[client.Id]],
                        client.IdentityProviderSyncPendingSince
                    )),
            ]
        );
    }

    public async Task<ApplicationDeleteResult> DeleteApplication(int id)
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        try
        {
            string sql = $"""
                DELETE FROM "dmscs"."Application" where "Id" = @Id AND {TenantScopedVendorCondition()};
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

    public async Task<ApplicationApiClientsResult> GetApplicationApiClients(int id)
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        try
        {
            string sql = $"""
                SELECT ac."ClientId", ac."ClientUuid", ac."IsApproved"
                FROM "dmscs"."ApiClient" ac
                JOIN "dmscs"."Application" a ON ac."ApplicationId" = a."Id"
                WHERE ac."ApplicationId" = @Id AND {TenantScopedVendorCondition("a")}
                ORDER BY ac."Id"
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

    /// <summary>
    /// Rolls the transaction back without letting a rollback failure replace the intended
    /// failure result; the disposal of the transaction remains the backstop.
    /// </summary>
    private async Task RollbackSafelyAsync(NpgsqlTransaction transaction)
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
