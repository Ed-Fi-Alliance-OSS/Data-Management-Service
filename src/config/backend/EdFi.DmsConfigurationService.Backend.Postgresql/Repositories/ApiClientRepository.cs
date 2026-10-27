// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using Dapper;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.ApiClient;
using EdFi.DmsConfigurationService.DataModel.Model.Application;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Repositories;

public class ApiClientRepository(
    IOptions<DatabaseOptions> databaseOptions,
    ILogger<ApiClientRepository> logger,
    IAuditContext auditContext,
    ITenantContextProvider tenantContextProvider
) : IApiClientRepository
{
    private TenantContext TenantContext => tenantContextProvider.Context;

    private long? TenantId => TenantContext is TenantContext.Multitenant mt ? mt.TenantId : null;

    /// <summary>
    /// SQL condition constraining an ApiClient row to the current tenant through its
    /// owning Application's Vendor.
    /// </summary>
    private string TenantScopedApplicationCondition(string? tableAlias = null)
    {
        var column = string.IsNullOrEmpty(tableAlias)
            ? "\"ApplicationId\""
            : $"{tableAlias}.\"ApplicationId\"";
        return $"""
            {column} IN (
                SELECT a."Id" FROM "dmscs"."Application" a
                JOIN "dmscs"."Vendor" v ON a."VendorId" = v."Id"
                WHERE {TenantContext.TenantWhereClause("v")}
            )
            """;
    }

    private string ApplicationInTenantExistsCondition =>
        $"""
            EXISTS (
                SELECT 1 FROM "dmscs"."Application" a
                JOIN "dmscs"."Vendor" v ON a."VendorId" = v."Id"
                WHERE a."Id" = @ApplicationId AND {TenantContext.TenantWhereClause("v")}
            )
            """;

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
    /// The effective education organization ids of the ApiClient aliased <c>ac</c>, comma-separated
    /// in ascending order, as a column named EducationOrganizationIds; null when there are none. An
    /// inheriting client reads its Application's current ids, which its own rows can lag behind; a
    /// narrowed client reads its own rows. Computed in the statement that reads the client row, so
    /// the list always belongs to that row's flag and Application.
    /// </summary>
    private const string EffectiveEducationOrganizationIdsColumn = """
        CASE WHEN ac."InheritsApplicationEducationOrganizations"
            THEN (
                SELECT string_agg(aeo."EducationOrganizationId"::text, ',' ORDER BY aeo."EducationOrganizationId")
                FROM "dmscs"."ApplicationEducationOrganization" aeo
                WHERE aeo."ApplicationId" = ac."ApplicationId"
            )
            ELSE (
                SELECT string_agg(ace."EducationOrganizationId"::text, ',' ORDER BY ace."EducationOrganizationId")
                FROM "dmscs"."ApiClientEducationOrganization" ace
                WHERE ace."ApiClientId" = ac."Id"
            )
        END AS "EducationOrganizationIds"
        """;

    private static List<long> ParseEducationOrganizationIds(string? educationOrganizationIds) =>
        string.IsNullOrEmpty(educationOrganizationIds)
            ? []
            :
            [
                .. educationOrganizationIds
                    .Split(',')
                    .Select(id => long.Parse(id, CultureInfo.InvariantCulture)),
            ];

    private const string DuplicateNameConstraint = "UX_ApiClient_ApplicationId_Name";
    private const string DuplicateClientIdConstraint = "UX_ApiClient_ClientId";
    private const string DuplicateClientUuidConstraint = "UX_ApiClient_ClientUuid";

    public async Task<ApiClientInsertResult> InsertApiClient(
        ApiClientInsertCommand command,
        ApiClientCommand clientCommand
    )
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            ApiClientEducationOrganizationScope scope =
                command.EducationOrganizationScope ?? new ApiClientEducationOrganizationScope.Inherit();

            string sql = $"""
                INSERT INTO "dmscs"."ApiClient" ("ApplicationId", "ClientId", "ClientUuid", "Name", "IsApproved", "InheritsApplicationEducationOrganizations", "CreatedBy")
                SELECT @ApplicationId, @ClientId, @ClientUuid, @Name, @IsApproved, @Inherits, @CreatedBy
                WHERE {ApplicationInTenantExistsCondition}
                RETURNING "Id";
                """;

            int? apiClientId = await connection.ExecuteScalarAsync<int?>(
                sql,
                new
                {
                    command.ApplicationId,
                    clientCommand.ClientId,
                    clientCommand.ClientUuid,
                    command.Name,
                    command.IsApproved,
                    Inherits = scope is not ApiClientEducationOrganizationScope.Narrow,
                    CreatedBy = auditContext.GetCurrentUser(),
                    TenantId,
                },
                transaction
            );

            if (apiClientId is null)
            {
                logger.LogWarning("Application not found");
                await transaction.RollbackAsync();
                return new ApiClientInsertResult.FailureApplicationNotFound();
            }

            if (scope is ApiClientEducationOrganizationScope.Narrow narrow)
            {
                long[] narrowedEducationOrganizationIds = DistinctEducationOrganizationIds(narrow);
                long[] missingIds = narrowedEducationOrganizationIds
                    .Except(
                        await ApplicationEducationOrganizationIdsAsync(
                            connection,
                            transaction,
                            command.ApplicationId
                        )
                    )
                    .ToArray();

                if (missingIds.Length > 0)
                {
                    logger.LogWarning(
                        "Insert ApiClient failure: education organizations not on the Application"
                    );
                    await transaction.RollbackAsync();
                    return new ApiClientInsertResult.FailureRequestedEducationOrganizationsNotOnApplication(
                        missingIds
                    );
                }

                await InsertStoredEducationOrganizationsAsync(
                    connection,
                    transaction,
                    apiClientId.Value,
                    command.ApplicationId,
                    narrowedEducationOrganizationIds
                );
            }
            else
            {
                await InsertApplicationEducationOrganizationsAsync(
                    connection,
                    transaction,
                    apiClientId.Value,
                    command.ApplicationId
                );
            }

            if (command.DataStoreIds.Length > 0)
            {
                if (!await AllDataStoresInTenant(connection, transaction, command.DataStoreIds))
                {
                    logger.LogWarning("Data store not found");
                    await transaction.RollbackAsync();
                    return new ApiClientInsertResult.FailureDataStoreNotFound();
                }

                sql = """
                    INSERT INTO "dmscs"."ApiClientDataStore" ("ApiClientId", "DataStoreId", "CreatedBy")
                    VALUES (@ApiClientId, @DataStoreId, @CreatedBy);
                    """;

                var currentUser = auditContext.GetCurrentUser();
                var dataStoreMappings = command.DataStoreIds.Select(dataStoreId => new
                {
                    ApiClientId = apiClientId,
                    DataStoreId = dataStoreId,
                    CreatedBy = currentUser,
                });

                await connection.ExecuteAsync(sql, dataStoreMappings, transaction);
            }

            await transaction.CommitAsync();
            return new ApiClientInsertResult.Success(apiClientId.Value);
        }
        catch (PostgresException ex)
            when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation
                && ex.ConstraintName == "FK_ApiClient_Application"
            )
        {
            logger.LogWarning(ex, "Application not found");
            await transaction.RollbackAsync();
            return new ApiClientInsertResult.FailureApplicationNotFound();
        }
        catch (PostgresException ex)
            when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation
                && ex.ConstraintName == "FK_ApiClientDataStore_DataStore"
            )
        {
            logger.LogWarning(ex, "Data store not found");
            await transaction.RollbackAsync();
            return new ApiClientInsertResult.FailureDataStoreNotFound();
        }
        catch (PostgresException ex)
            when (ex.SqlState == PostgresErrorCodes.UniqueViolation
                && ex.ConstraintName == DuplicateNameConstraint
            )
        {
            // With IncludeErrorDetail the exception carries the conflicting client-supplied name, so only
            // the fixed text and the safe diagnostic fields are logged, never the exception itself.
#pragma warning disable S6667 // Logging in a catch clause should pass the caught exception - deliberately omitted, see comment above
            logger.LogWarning(
                "API client name must be unique within the application ({SqlState}, {ConstraintName})",
                ex.SqlState,
                ex.ConstraintName
            );
#pragma warning restore S6667
            await transaction.RollbackAsync();
            return new ApiClientInsertResult.FailureDuplicateName();
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
            return new ApiClientInsertResult.FailureDuplicateClientId();
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
            return new ApiClientInsertResult.FailureDuplicateClientUuid();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Insert ApiClient failure");
            await transaction.RollbackAsync();
            return new ApiClientInsertResult.FailureUnknown(ex.Message);
        }
    }

    private static readonly IReadOnlyDictionary<string, string> OrderByColumns = new Dictionary<
        string,
        string
    >(StringComparer.OrdinalIgnoreCase)
    {
        ["id"] = "Id",
        ["applicationId"] = "ApplicationId",
        ["name"] = "Name",
    };

    private static string ResolveOrderByColumnName(ApiClientQuery query) =>
        query.OrderBy is not null && OrderByColumns.TryGetValue(query.OrderBy, out var col) ? col : "Id";

    private static string BuildOrderByClause(ApiClientQuery query, string? tableAlias = null) =>
        PostgresqlIdentifier.OrderBy(ResolveOrderByColumnName(query), query.IsDescending, tableAlias);

    private string BuildFilterClause(ApiClientQuery query)
    {
        var conditions = new List<string> { TenantScopedApplicationCondition() };
        if (query.ApplicationId.HasValue)
        {
            conditions.Add("\"ApplicationId\" = @ApplicationId");
        }
        return "WHERE " + string.Join(" AND ", conditions);
    }

    public async Task<ApiClientQueryResult> QueryApiClient(ApiClientQuery query)
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();
        try
        {
            string orderByClause = BuildOrderByClause(query);
            string filterClause = BuildFilterClause(query);
            string outerOrderByClause = BuildOrderByClause(query, "ac");
            string sql = $"""
                SELECT ac."Id", ac."ApplicationId", ac."ClientId", ac."ClientUuid", ac."Name", ac."IsApproved",
                       ac."InheritsApplicationEducationOrganizations", {EffectiveEducationOrganizationIdsColumn},
                       acd."DataStoreId"
                FROM (SELECT * FROM "dmscs"."ApiClient" {filterClause} {orderByClause} {query.BuildPagingClause()}) AS ac
                LEFT OUTER JOIN "dmscs"."ApiClientDataStore" acd ON ac."Id" = acd."ApiClientId"
                {outerOrderByClause};
                """;

            var apiClients = await connection.QueryAsync<ApiClientResponse, string?, int?, ApiClientResponse>(
                sql,
                (apiClient, educationOrganizationIds, dataStoreId) =>
                {
                    apiClient.EducationOrganizationIds = ParseEducationOrganizationIds(
                        educationOrganizationIds
                    );
                    if (dataStoreId is not null)
                    {
                        apiClient.DataStoreIds.Add(dataStoreId.Value);
                    }
                    return apiClient;
                },
                param: new
                {
                    query.Limit,
                    query.Offset,
                    query.ApplicationId,
                    TenantId,
                },
                splitOn: "EducationOrganizationIds,DataStoreId"
            );

            var returnApiClients = apiClients
                .GroupBy(ac => ac.Id)
                .Select(g =>
                {
                    var grouped = g.First();
                    grouped.DataStoreIds = g.SelectMany(ac => ac.DataStoreIds).Distinct().ToList();
                    return grouped;
                })
                .ToList();

            await HydrateOwnershipAsync(connection, returnApiClients);
            return new ApiClientQueryResult.Success(returnApiClients);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Query ApiClient failure");
            return new ApiClientQueryResult.FailureUnknown(ex.Message);
        }
    }

    public async Task<ApiClientGetResult> GetApiClientByClientId(string clientId)
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();
        try
        {
            string sql = $"""
                SELECT ac."Id", ac."ApplicationId", ac."ClientId", ac."ClientUuid", ac."Name", ac."IsApproved",
                       ac."InheritsApplicationEducationOrganizations", {EffectiveEducationOrganizationIdsColumn},
                       acd."DataStoreId"
                FROM "dmscs"."ApiClient" ac
                LEFT OUTER JOIN "dmscs"."ApiClientDataStore" acd ON ac."Id" = acd."ApiClientId"
                WHERE ac."ClientId" = @ClientId AND {TenantScopedApplicationCondition("ac")};
                """;

            var apiClients = await connection.QueryAsync<ApiClientResponse, string?, int?, ApiClientResponse>(
                sql,
                (apiClient, educationOrganizationIds, dataStoreId) =>
                {
                    apiClient.EducationOrganizationIds = ParseEducationOrganizationIds(
                        educationOrganizationIds
                    );
                    if (dataStoreId is not null)
                    {
                        apiClient.DataStoreIds.Add(dataStoreId.Value);
                    }
                    return apiClient;
                },
                param: new { ClientId = clientId, TenantId },
                splitOn: "EducationOrganizationIds,DataStoreId"
            );

            if (
                apiClients
                    .GroupBy(ac => ac.Id)
                    .Select(g =>
                    {
                        var grouped = g.First();
                        grouped.DataStoreIds = g.SelectMany(ac => ac.DataStoreIds).Distinct().ToList();
                        return grouped;
                    })
                    .SingleOrDefault()
                is not { } returnApiClient
            )
            {
                return new ApiClientGetResult.FailureNotFound();
            }

            await HydrateOwnershipAsync(connection, [returnApiClient]);
            return new ApiClientGetResult.Success(returnApiClient);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Get ApiClient by ClientId failure");
            return new ApiClientGetResult.FailureUnknown(ex.Message);
        }
    }

    public async Task<ApiClientGetResult> GetApiClientById(int id)
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();
        try
        {
            string sql = $"""
                SELECT ac."Id", ac."ApplicationId", ac."ClientId", ac."ClientUuid", ac."Name", ac."IsApproved",
                       ac."InheritsApplicationEducationOrganizations", {EffectiveEducationOrganizationIdsColumn},
                       acd."DataStoreId"
                FROM "dmscs"."ApiClient" ac
                LEFT OUTER JOIN "dmscs"."ApiClientDataStore" acd ON ac."Id" = acd."ApiClientId"
                WHERE ac."Id" = @Id AND {TenantScopedApplicationCondition("ac")};
                """;

            var apiClients = await connection.QueryAsync<ApiClientResponse, string?, int?, ApiClientResponse>(
                sql,
                (apiClient, educationOrganizationIds, dataStoreId) =>
                {
                    apiClient.EducationOrganizationIds = ParseEducationOrganizationIds(
                        educationOrganizationIds
                    );
                    if (dataStoreId is not null)
                    {
                        apiClient.DataStoreIds.Add(dataStoreId.Value);
                    }
                    return apiClient;
                },
                param: new { Id = id, TenantId },
                splitOn: "EducationOrganizationIds,DataStoreId"
            );

            if (
                apiClients
                    .GroupBy(ac => ac.Id)
                    .Select(g =>
                    {
                        var grouped = g.First();
                        grouped.DataStoreIds = g.SelectMany(ac => ac.DataStoreIds).Distinct().ToList();
                        return grouped;
                    })
                    .SingleOrDefault()
                is not { } returnApiClient
            )
            {
                return new ApiClientGetResult.FailureNotFound();
            }

            await HydrateOwnershipAsync(connection, [returnApiClient]);
            return new ApiClientGetResult.Success(returnApiClient);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Get ApiClient by Id failure");
            return new ApiClientGetResult.FailureUnknown(ex.Message);
        }
    }

    public async Task<ApiClientUpdateResult> UpdateApiClient(ApiClientUpdateCommand command)
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            // Lock the ApiClient within the current tenant; a move rewrites its education organization rows
            string checkSql = $"""
                SELECT "ApplicationId", "InheritsApplicationEducationOrganizations" FROM "dmscs"."ApiClient"
                WHERE "Id" = @Id AND {TenantScopedApplicationCondition()}
                FOR UPDATE;
                """;
            var existing = await connection.QuerySingleOrDefaultAsync<(
                int ApplicationId,
                bool InheritsApplicationEducationOrganizations
            )?>(checkSql, new { command.Id, TenantId }, transaction);
            if (existing is null)
            {
                await transaction.RollbackAsync();
                return new ApiClientUpdateResult.FailureNotFound();
            }

            bool isMove = existing.Value.ApplicationId != command.ApplicationId;

            if (isMove)
            {
                string destinationSql =
                    $"""SELECT CASE WHEN {ApplicationInTenantExistsCondition} THEN 1 ELSE 0 END;""";
                if (
                    await connection.ExecuteScalarAsync<int>(
                        destinationSql,
                        new { command.ApplicationId, TenantId },
                        transaction
                    ) == 0
                )
                {
                    logger.LogWarning("Application not found");
                    await transaction.RollbackAsync();
                    return new ApiClientUpdateResult.FailureApplicationNotFound();
                }
            }

            // With no requested scope the client keeps its own. Its rows are rewritten only for a requested
            // scope or a move: a moved inheriting client takes the destination list, and a moved narrowed
            // client keeps its list, which must fit the destination
            bool rewritesScope = command.EducationOrganizationScope is not null || isMove;
            ApiClientEducationOrganizationScope scope =
                command.EducationOrganizationScope
                ?? (
                    existing.Value.InheritsApplicationEducationOrganizations
                        ? new ApiClientEducationOrganizationScope.Inherit()
                        : new ApiClientEducationOrganizationScope.Narrow(
                            await StoredEducationOrganizationIdsAsync(connection, transaction, command.Id)
                        )
                );

            if (rewritesScope && scope is ApiClientEducationOrganizationScope.Narrow narrow)
            {
                long[] missingIds = DistinctEducationOrganizationIds(narrow)
                    .Except(
                        await ApplicationEducationOrganizationIdsAsync(
                            connection,
                            transaction,
                            command.ApplicationId
                        )
                    )
                    .ToArray();

                if (missingIds.Length > 0)
                {
                    logger.LogWarning(
                        "Update ApiClient failure: education organizations not on the client's Application"
                    );
                    await transaction.RollbackAsync();
                    return command.EducationOrganizationScope is null
                        ? new ApiClientUpdateResult.FailureEducationOrganizationsNotOnApplication(missingIds)
                        : new ApiClientUpdateResult.FailureRequestedEducationOrganizationsNotOnApplication(
                            missingIds
                        );
                }
            }

            if (rewritesScope)
            {
                // The rows reference the client's current Application with NO ACTION, so they go
                // before the ApplicationId changes and are reinserted for the scope below
                await DeleteStoredEducationOrganizationsAsync(connection, transaction, command.Id);
            }

            // Update ApiClient record; the target application must belong to the current tenant
            string updateSql = $"""
                UPDATE "dmscs"."ApiClient"
                SET "ApplicationId" = @ApplicationId, "Name" = @Name, "IsApproved" = @IsApproved,
                    "ClientUuid" = COALESCE(@ClientUuid, "ClientUuid"),
                    "InheritsApplicationEducationOrganizations" = COALESCE(@Inherits, "InheritsApplicationEducationOrganizations"),
                    "IdentityProviderSyncPendingSince" = CASE WHEN @CompletesIdentityProviderSync THEN NULL ELSE "IdentityProviderSyncPendingSince" END,
                    "LastModifiedAt" = @LastModifiedAt, "ModifiedBy" = @ModifiedBy
                WHERE "Id" = @Id AND {ApplicationInTenantExistsCondition};
                """;

            int updatedRows = await connection.ExecuteAsync(
                updateSql,
                new
                {
                    command.Id,
                    command.ApplicationId,
                    command.Name,
                    command.IsApproved,
                    ClientUuid = command.ClientUuid,
                    Inherits = rewritesScope
                        ? scope is ApiClientEducationOrganizationScope.Inherit
                        : (bool?)null,
                    command.CompletesIdentityProviderSync,
                    LastModifiedAt = auditContext.GetCurrentTimestamp(),
                    ModifiedBy = auditContext.GetCurrentUser(),
                    TenantId,
                },
                transaction
            );

            if (updatedRows == 0)
            {
                logger.LogWarning("Application not found");
                await transaction.RollbackAsync();
                return new ApiClientUpdateResult.FailureApplicationNotFound();
            }

            if (rewritesScope && scope is ApiClientEducationOrganizationScope.Narrow narrowed)
            {
                await InsertStoredEducationOrganizationsAsync(
                    connection,
                    transaction,
                    command.Id,
                    command.ApplicationId,
                    DistinctEducationOrganizationIds(narrowed)
                );
            }
            else if (rewritesScope)
            {
                await InsertApplicationEducationOrganizationsAsync(
                    connection,
                    transaction,
                    command.Id,
                    command.ApplicationId
                );
            }

            if (
                command.DataStoreIds.Length > 0
                && !await AllDataStoresInTenant(connection, transaction, command.DataStoreIds)
            )
            {
                logger.LogWarning("Data store not found");
                await transaction.RollbackAsync();
                return new ApiClientUpdateResult.FailureDataStoreNotFound();
            }

            // Delete existing data store mappings
            string deleteSql = """DELETE FROM "dmscs"."ApiClientDataStore" WHERE "ApiClientId" = @Id;""";
            await connection.ExecuteAsync(deleteSql, new { command.Id }, transaction);

            // Insert new data store mappings
            if (command.DataStoreIds.Length > 0)
            {
                string insertSql = """
                    INSERT INTO "dmscs"."ApiClientDataStore" ("ApiClientId", "DataStoreId", "CreatedBy")
                    VALUES (@ApiClientId, @DataStoreId, @CreatedBy);
                    """;

                var currentUser = auditContext.GetCurrentUser();
                var dataStoreMappings = command.DataStoreIds.Select(dataStoreId => new
                {
                    ApiClientId = command.Id,
                    DataStoreId = dataStoreId,
                    CreatedBy = currentUser,
                });

                await connection.ExecuteAsync(insertSql, dataStoreMappings, transaction);
            }

            await transaction.CommitAsync();
            return new ApiClientUpdateResult.Success();
        }
        catch (PostgresException ex)
            when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation
                && ex.ConstraintName == "FK_ApiClient_Application"
            )
        {
            logger.LogWarning(ex, "Application not found");
            await RollbackSafelyAsync(transaction);
            return new ApiClientUpdateResult.FailureApplicationNotFound();
        }
        catch (PostgresException ex)
            when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation
                && ex.ConstraintName == "FK_ApiClientDataStore_DataStore"
            )
        {
            logger.LogWarning(ex, "Data store not found");
            await RollbackSafelyAsync(transaction);
            return new ApiClientUpdateResult.FailureDataStoreNotFound();
        }
        catch (PostgresException ex)
            when (ex.SqlState == PostgresErrorCodes.UniqueViolation
                && ex.ConstraintName == DuplicateNameConstraint
            )
        {
            // With IncludeErrorDetail the exception carries the conflicting client-supplied name, so only
            // the fixed text and the safe diagnostic fields are logged, never the exception itself.
#pragma warning disable S6667 // Logging in a catch clause should pass the caught exception - deliberately omitted, see comment above
            logger.LogWarning(
                "API client name must be unique within the application ({SqlState}, {ConstraintName})",
                ex.SqlState,
                ex.ConstraintName
            );
#pragma warning restore S6667
            await RollbackSafelyAsync(transaction);
            return new ApiClientUpdateResult.FailureDuplicateName();
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
            await RollbackSafelyAsync(transaction);
            return new ApiClientUpdateResult.FailureDuplicateClientUuid();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Update ApiClient failure");
            await RollbackSafelyAsync(transaction);
            return new ApiClientUpdateResult.FailureUnknown(ex.Message);
        }
    }

    public async Task<ApiClientDeleteResult> DeleteApiClient(int id)
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            // Check if ApiClient exists within the current tenant
            string checkSql = $"""
                SELECT COUNT(1) FROM "dmscs"."ApiClient"
                WHERE "Id" = @Id AND {TenantScopedApplicationCondition()};
                """;
            int exists = await connection.ExecuteScalarAsync<int>(
                checkSql,
                new { Id = id, TenantId },
                transaction
            );
            if (exists == 0)
            {
                await transaction.RollbackAsync();
                return new ApiClientDeleteResult.FailureNotFound();
            }

            // Delete data store mappings first (due to foreign key constraint)
            string deleteMappingsSql =
                """DELETE FROM "dmscs"."ApiClientDataStore" WHERE "ApiClientId" = @Id;""";
            await connection.ExecuteAsync(deleteMappingsSql, new { Id = id }, transaction);

            // Education organization rows reference the client with NO ACTION, so they go first
            string deleteEducationOrganizationsSql =
                """DELETE FROM "dmscs"."ApiClientEducationOrganization" WHERE "ApiClientId" = @Id;""";
            await connection.ExecuteAsync(deleteEducationOrganizationsSql, new { Id = id }, transaction);

            // Delete ApiClient record
            string deleteSql = """DELETE FROM "dmscs"."ApiClient" WHERE "Id" = @Id;""";
            await connection.ExecuteAsync(deleteSql, new { Id = id }, transaction);

            await transaction.CommitAsync();
            return new ApiClientDeleteResult.Success();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Delete ApiClient failure");
            await transaction.RollbackAsync();
            return new ApiClientDeleteResult.FailureUnknown(ex.Message);
        }
    }

    public async Task<ApiClientEducationOrganizationStateResult> GetApiClientEducationOrganizationState(
        int id
    )
    {
        try
        {
            await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
            await connection.OpenAsync();

            // One statement reads one snapshot, so the flag, the Application and both lists
            // always come from the same committed state
            string sql = $"""
                SELECT ac."ApplicationId", ac."InheritsApplicationEducationOrganizations",
                    ac."IdentityProviderSyncPendingSince",
                    ARRAY(
                        SELECT ace."EducationOrganizationId" FROM "dmscs"."ApiClientEducationOrganization" ace
                        WHERE ace."ApiClientId" = ac."Id"
                        ORDER BY ace."EducationOrganizationId"
                    ) AS "StoredEducationOrganizationIds",
                    ARRAY(
                        SELECT aeo."EducationOrganizationId" FROM "dmscs"."ApplicationEducationOrganization" aeo
                        WHERE aeo."ApplicationId" = ac."ApplicationId"
                        ORDER BY aeo."EducationOrganizationId"
                    ) AS "ApplicationEducationOrganizationIds"
                FROM "dmscs"."ApiClient" ac
                WHERE ac."Id" = @Id AND {TenantScopedApplicationCondition("ac")};
                """;
            var client = await connection.QuerySingleOrDefaultAsync<(
                int ApplicationId,
                bool InheritsApplicationEducationOrganizations,
                DateTime? IdentityProviderSyncPendingSince,
                long[] StoredEducationOrganizationIds,
                long[] ApplicationEducationOrganizationIds
            )?>(sql, new { Id = id, TenantId });

            if (client is null)
            {
                return new ApiClientEducationOrganizationStateResult.FailureNotFound();
            }

            return new ApiClientEducationOrganizationStateResult.Success(
                new ApiClientEducationOrganizationState(
                    client.Value.ApplicationId,
                    client.Value.InheritsApplicationEducationOrganizations,
                    client.Value.StoredEducationOrganizationIds,
                    client.Value.InheritsApplicationEducationOrganizations
                        ? client.Value.ApplicationEducationOrganizationIds
                        : client.Value.StoredEducationOrganizationIds,
                    client.Value.IdentityProviderSyncPendingSince
                )
            );
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Get ApiClient education organization state failure");
            return new ApiClientEducationOrganizationStateResult.FailureUnknown(ex.Message);
        }
    }

    public async Task<ApiClientSyncPendingResult> ClearApiClientIdentityProviderSyncPending(int id)
    {
        try
        {
            await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
            await connection.OpenAsync();

            string sql = $"""
                UPDATE "dmscs"."ApiClient"
                SET "IdentityProviderSyncPendingSince" = NULL
                WHERE "Id" = @Id AND {TenantScopedApplicationCondition()};
                """;
            int updatedRows = await connection.ExecuteAsync(sql, new { Id = id, TenantId });

            return updatedRows == 0
                ? new ApiClientSyncPendingResult.FailureNotFound()
                : new ApiClientSyncPendingResult.Success();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Clear ApiClient sync-pending failure");
            return new ApiClientSyncPendingResult.FailureUnknown(ex.Message);
        }
    }

    private static async Task<long[]> StoredEducationOrganizationIdsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int apiClientId
    ) =>
        (
            await connection.QueryAsync<long>(
                """
                SELECT "EducationOrganizationId" FROM "dmscs"."ApiClientEducationOrganization"
                WHERE "ApiClientId" = @ApiClientId
                ORDER BY "EducationOrganizationId";
                """,
                new { ApiClientId = apiClientId },
                transaction
            )
        ).ToArray();

    /// <summary>
    /// The Application's education organization ids, each row locked until the transaction ends. A
    /// concurrent Application update that removes one either waits for this transaction, then cascades
    /// to the client rows it stored, or commits first and leaves the id out of the result, so a list
    /// checked against these ids can always be stored.
    /// </summary>
    private static async Task<long[]> ApplicationEducationOrganizationIdsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int applicationId
    ) =>
        (
            await connection.QueryAsync<long>(
                """
                SELECT "EducationOrganizationId" FROM "dmscs"."ApplicationEducationOrganization"
                WHERE "ApplicationId" = @ApplicationId
                ORDER BY "EducationOrganizationId"
                FOR KEY SHARE;
                """,
                new { ApplicationId = applicationId },
                transaction
            )
        ).ToArray();

    private static Task DeleteStoredEducationOrganizationsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int apiClientId
    ) =>
        connection.ExecuteAsync(
            """DELETE FROM "dmscs"."ApiClientEducationOrganization" WHERE "ApiClientId" = @ApiClientId;""",
            new { ApiClientId = apiClientId },
            transaction
        );

    private Task InsertStoredEducationOrganizationsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int apiClientId,
        int applicationId,
        long[] educationOrganizationIds
    )
    {
        string currentUser = auditContext.GetCurrentUser();
        return connection.ExecuteAsync(
            """
            INSERT INTO "dmscs"."ApiClientEducationOrganization" ("ApiClientId", "ApplicationId", "EducationOrganizationId", "CreatedBy")
            VALUES (@ApiClientId, @ApplicationId, @EducationOrganizationId, @CreatedBy);
            """,
            educationOrganizationIds.Select(educationOrganizationId => new
            {
                ApiClientId = apiClientId,
                ApplicationId = applicationId,
                EducationOrganizationId = educationOrganizationId,
                CreatedBy = currentUser,
            }),
            transaction
        );
    }

    /// <summary>The distinct ids of a narrowed scope.</summary>
    private static long[] DistinctEducationOrganizationIds(
        ApiClientEducationOrganizationScope.Narrow narrow
    ) => [.. narrow.EducationOrganizationIds.Distinct()];

    /// <summary>Stores the Application's current list as an inheriting client's rows.</summary>
    private Task InsertApplicationEducationOrganizationsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int apiClientId,
        int applicationId
    ) =>
        connection.ExecuteAsync(
            """
            INSERT INTO "dmscs"."ApiClientEducationOrganization" ("ApiClientId", "ApplicationId", "EducationOrganizationId", "CreatedBy")
            SELECT @ApiClientId, "ApplicationId", "EducationOrganizationId", @CreatedBy
            FROM "dmscs"."ApplicationEducationOrganization"
            WHERE "ApplicationId" = @ApplicationId;
            """,
            new
            {
                ApiClientId = apiClientId,
                ApplicationId = applicationId,
                CreatedBy = auditContext.GetCurrentUser(),
            },
            transaction
        );

    public async Task<ApiClientResolutionResult> GetApiClientResolutionState(int id)
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            // Locking the ApiClient row waits out any in-flight update transaction, so the
            // reads below observe that transaction's final outcome.
            string clientSql = $"""
                SELECT ac."ApplicationId", ac."Name", ac."IsApproved", ac."ClientId", ac."ClientUuid",
                    ac."InheritsApplicationEducationOrganizations", ac."IdentityProviderSyncPendingSince"
                FROM "dmscs"."ApiClient" ac
                WHERE ac."Id" = @Id AND {TenantScopedApplicationCondition("ac")}
                FOR UPDATE OF ac;
                """;
            var client = await connection.QuerySingleOrDefaultAsync<(
                int ApplicationId,
                string Name,
                bool IsApproved,
                string ClientId,
                Guid ClientUuid,
                bool InheritsApplicationEducationOrganizations,
                DateTime? IdentityProviderSyncPendingSince
            )?>(clientSql, new { Id = id, TenantId }, transaction);

            if (client is null)
            {
                return new ApiClientResolutionResult.FailureNotExists();
            }

            int[] dataStoreIds =
            [
                .. await connection.QueryAsync<int>(
                    """
                    SELECT "DataStoreId" FROM "dmscs"."ApiClientDataStore"
                    WHERE "ApiClientId" = @Id;
                    """,
                    new { Id = id },
                    transaction
                ),
            ];

            // The client's own rows are read under the same row lock, so the flag and the rows
            // come from one committed state
            long[] storedEducationOrganizationIds = await StoredEducationOrganizationIdsAsync(
                connection,
                transaction,
                id
            );

            await transaction.CommitAsync();

            return new ApiClientResolutionResult.Success(
                new ApiClientResolutionState(
                    client.Value.ApplicationId,
                    client.Value.Name,
                    client.Value.IsApproved,
                    client.Value.ClientId,
                    client.Value.ClientUuid,
                    dataStoreIds,
                    client.Value.InheritsApplicationEducationOrganizations,
                    storedEducationOrganizationIds,
                    client.Value.IdentityProviderSyncPendingSince
                )
            );
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Get ApiClient resolution state failure");
            await RollbackSafelyAsync(transaction);
            return new ApiClientResolutionResult.FailureUnknown(ex.Message);
        }
    }

    public async Task<ApiClientUuidSyncResult> SyncApiClientUuid(
        int id,
        Guid expectedClientUuid,
        Guid newClientUuid
    )
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            string selectSql = $"""
                SELECT ac."ClientUuid"
                FROM "dmscs"."ApiClient" ac
                WHERE ac."Id" = @Id AND {TenantScopedApplicationCondition("ac")}
                FOR UPDATE OF ac;
                """;
            Guid? storedUuid = await connection.QuerySingleOrDefaultAsync<Guid?>(
                selectSql,
                new { Id = id, TenantId },
                transaction
            );

            if (storedUuid is null)
            {
                // The caller's deletion decision needs to know whether any row still references
                // the new UUID; the check is deliberately cross-tenant because it protects a
                // provider-level object and exposes no tenant data.
                bool referenced = await connection.ExecuteScalarAsync<bool>(
                    """
                    SELECT EXISTS (
                        SELECT 1 FROM "dmscs"."ApiClient" WHERE "ClientUuid" = @NewClientUuid
                    );
                    """,
                    new { NewClientUuid = newClientUuid },
                    transaction
                );
                await transaction.CommitAsync();
                return referenced
                    ? new ApiClientUuidSyncResult.FailureNotExists()
                    : new ApiClientUuidSyncResult.FailureNotExistsSafeToDelete();
            }

            if (storedUuid == newClientUuid)
            {
                await transaction.CommitAsync();
                return new ApiClientUuidSyncResult.AlreadyApplied();
            }

            if (storedUuid != expectedClientUuid)
            {
                await transaction.RollbackAsync();
                return new ApiClientUuidSyncResult.FailureStaleState();
            }

            await connection.ExecuteAsync(
                """
                UPDATE "dmscs"."ApiClient"
                SET "ClientUuid" = @NewClientUuid, "LastModifiedAt" = @LastModifiedAt, "ModifiedBy" = @ModifiedBy
                WHERE "Id" = @Id;
                """,
                new
                {
                    NewClientUuid = newClientUuid,
                    Id = id,
                    LastModifiedAt = auditContext.GetCurrentTimestamp(),
                    ModifiedBy = auditContext.GetCurrentUser(),
                },
                transaction
            );

            await transaction.CommitAsync();
            return new ApiClientUuidSyncResult.Success();
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
                "The new identity provider client UUID is already referenced by another API client; nothing was written ({SqlState}, {ConstraintName})",
                ex.SqlState,
                ex.ConstraintName
            );
#pragma warning restore S6667
            await RollbackSafelyAsync(transaction);
            return new ApiClientUuidSyncResult.FailureNewUuidInUse();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Sync ApiClient uuid failure");
            await RollbackSafelyAsync(transaction);
            return new ApiClientUuidSyncResult.FailureUnknown(ex.Message);
        }
    }

    public async Task<ApiClientUuidReferenceResult> HasApiClientUuidReference(Guid clientUuid)
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        try
        {
            bool referenced = await connection.ExecuteScalarAsync<bool>(
                """
                SELECT EXISTS (
                    SELECT 1 FROM "dmscs"."ApiClient" WHERE "ClientUuid" = @ClientUuid
                );
                """,
                new { ClientUuid = clientUuid }
            );
            return referenced
                ? new ApiClientUuidReferenceResult.Referenced()
                : new ApiClientUuidReferenceResult.None();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ApiClient uuid reference check failure");
            return new ApiClientUuidReferenceResult.FailureUnknown(ex.Message);
        }
    }

    private static async Task HydrateOwnershipAsync(
        NpgsqlConnection connection,
        IReadOnlyCollection<ApiClientResponse> apiClients
    )
    {
        if (apiClients.Count == 0)
        {
            return;
        }

        int[] apiClientIds = [.. apiClients.Select(apiClient => apiClient.Id).Distinct()];
        var ownershipRows = await connection.QueryAsync<(
            int ApiClientId,
            int? CreatorOwnershipTokenId,
            int? OwnershipTokenId
        )>(
            """
            SELECT ac."Id" AS "ApiClientId",
                   ac."CreatorOwnershipTokenId",
                   acot."OwnershipTokenId"
            FROM "dmscs"."ApiClient" ac
            LEFT OUTER JOIN "dmscs"."ApiClientOwnershipToken" acot ON ac."Id" = acot."ApiClientId"
            WHERE ac."Id" = ANY(@ApiClientIds)
            ORDER BY ac."Id", acot."OwnershipTokenId";
            """,
            new { ApiClientIds = apiClientIds }
        );

        var ownershipByApiClientId = ownershipRows
            .GroupBy(row => row.ApiClientId)
            .ToDictionary(
                group => group.Key,
                group => new
                {
                    CreatorOwnershipTokenId = group.First().CreatorOwnershipTokenId,
                    OwnershipTokenIds = group
                        .Where(row => row.OwnershipTokenId is not null)
                        .Select(row => row.OwnershipTokenId!.Value)
                        .Distinct()
                        .Order()
                        .ToList(),
                }
            );

        foreach (var apiClient in apiClients)
        {
            if (ownershipByApiClientId.TryGetValue(apiClient.Id, out var ownership))
            {
                apiClient.CreatorOwnershipTokenId = ownership.CreatorOwnershipTokenId;
                apiClient.OwnershipTokenIds = ownership.OwnershipTokenIds;
            }
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
