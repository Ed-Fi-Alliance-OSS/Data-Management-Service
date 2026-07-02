// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using System.Text.Json;
using Dapper;
using EdFi.DmsConfigurationService.Backend.Models.ClaimsHierarchy;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.DataModel;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.ClaimSets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Polly;
using Polly.Retry;
using Action = EdFi.DmsConfigurationService.DataModel.Model.Action.Action;
using AuthorizationStrategy = EdFi.DmsConfigurationService.DataModel.Model.ClaimSets.AuthorizationStrategy;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Repositories;

public class ClaimSetRepository(
    IOptions<DatabaseOptions> databaseOptions,
    ILogger<ClaimSetRepository> logger,
    IClaimsHierarchyRepository claimsHierarchyRepository,
    IClaimsHierarchyManager claimsHierarchyManager,
    IAuditContext auditContext,
    ITenantContextProvider tenantContextProvider
) : IClaimSetRepository
{
    private TenantContext TenantContext => tenantContextProvider.Context;

    private long? TenantId => TenantContext is TenantContext.Multitenant mt ? mt.TenantId : null;

    /// <summary>
    /// Gets the SQL WHERE clause for ClaimSet queries that includes system-reserved claimsets.
    /// System-reserved claimsets are always visible regardless of tenant.
    /// Non-system-reserved claimsets are filtered by tenant.
    /// </summary>
    private string ClaimSetWhereClause(string? tableAlias = null)
    {
        var isSystemReservedColumn = string.IsNullOrEmpty(tableAlias)
            ? "\"IsSystemReserved\""
            : $"{tableAlias}.\"IsSystemReserved\"";
        return $"({isSystemReservedColumn} = true OR {TenantContext.TenantWhereClause(tableAlias)})";
    }

    public IEnumerable<Action> GetActions()
    {
        var actions = new Action[]
        {
            new()
            {
                Id = 1,
                Name = "Create",
                Uri = "uri://ed-fi.org/api/actions/create",
            },
            new()
            {
                Id = 2,
                Name = "Read",
                Uri = "uri://ed-fi.org/api/actions/read",
            },
            new()
            {
                Id = 3,
                Name = "Update",
                Uri = "uri://ed-fi.org/api/actions/update",
            },
            new()
            {
                Id = 4,
                Name = "Delete",
                Uri = "uri://ed-fi.org/api/actions/delete",
            },
            new()
            {
                Id = 5,
                Name = "ReadChanges",
                Uri = "uri://ed-fi.org/api/actions/readChanges",
            },
        };

        return actions;
    }

    public async Task<AuthorizationStrategyGetResult> GetAuthorizationStrategies()
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        try
        {
            string sql = $"""
                SELECT "Id", "AuthorizationStrategyName", "DisplayName"
                FROM "dmscs"."AuthorizationStrategy"
                WHERE {TenantContext.TenantWhereClause()};
                """;

            var authorizationStrategies = await connection.QueryAsync(sql, new { TenantId });

            var authStratResponse = authorizationStrategies
                .Select(row => new AuthorizationStrategy
                {
                    Id = row.Id,
                    AuthorizationStrategyName = row.AuthorizationStrategyName,
                    DisplayName = row.DisplayName,
                })
                .ToList();

            return new AuthorizationStrategyGetResult.Success(authStratResponse);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Get Authorization Strategies failure");
            return new AuthorizationStrategyGetResult.FailureUnknown(ex.Message);
        }
    }

    public async Task<ClaimSetInsertResult> InsertClaimSet(ClaimSetInsertCommand command)
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        try
        {
            string sql = """
                   INSERT INTO "dmscs"."ClaimSet" ("ClaimSetName", "IsSystemReserved", "CreatedBy", "TenantId")
                   VALUES(@ClaimSetName, @IsSystemReserved, @CreatedBy, @TenantId)
                   RETURNING "Id";
                """;

            var parameters = new
            {
                ClaimSetName = command.Name,
                IsSystemReserved = command.IsSystemReserved,
                CreatedBy = auditContext.GetCurrentUser(),
                TenantId,
            };

            long id = await connection.ExecuteScalarAsync<long>(sql, parameters);
            await transaction.CommitAsync();

            return new ClaimSetInsertResult.Success(id);
        }
        catch (PostgresException ex)
            when (ex.SqlState == PostgresErrorCodes.UniqueViolation
                && ex.ConstraintName == "UX_ClaimSet_ClaimSetName"
            )
        {
            logger.LogWarning(ex, "ClaimSetName must be unique");
            await transaction.RollbackAsync();
            return new ClaimSetInsertResult.FailureDuplicateClaimSetName();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Insert claim set failure");
            await transaction.RollbackAsync();
            return new ClaimSetInsertResult.FailureUnknown(ex.Message);
        }
    }

    private static readonly IReadOnlyDictionary<string, string> OrderByColumns = new Dictionary<
        string,
        string
    >(StringComparer.OrdinalIgnoreCase)
    {
        ["id"] = "c.\"Id\"",
        ["name"] = "c.\"ClaimSetName\"",
        ["claimSetName"] = "c.\"ClaimSetName\"",
    };

    private static string BuildOrderByClause(ClaimSetQuery query)
    {
        if (query.OrderBy is not null && OrderByColumns.TryGetValue(query.OrderBy, out var col))
        {
            return $"ORDER BY {col} {(query.IsDescending ? "DESC" : "ASC")}";
        }
        return "ORDER BY c.\"Id\"";
    }

    private static string BuildFilterClause(ClaimSetQuery query)
    {
        var conditions = new List<string>();
        if (query.Id.HasValue)
        {
            conditions.Add("c.\"Id\" = @Id");
        }
        if (query.Name is not null)
        {
            conditions.Add("c.\"ClaimSetName\" = @Name");
        }
        return conditions.Count > 0 ? " AND " + string.Join(" AND ", conditions) : string.Empty;
    }

    public async Task<ClaimSetQueryResult> QueryClaimSet(ClaimSetQuery query)
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        try
        {
            string orderByClause = BuildOrderByClause(query);
            string filterClause = BuildFilterClause(query);
            string sql = $"""
                SELECT c."Id", c."ClaimSetName", c."IsSystemReserved"
                    ,(SELECT jsonb_agg(jsonb_build_object('applicationName', a."ApplicationName"))
                        FROM "dmscs"."Application" a
                        INNER JOIN "dmscs"."Vendor" v ON a."VendorId" = v."Id"
                        WHERE a."ClaimSetName" = c."ClaimSetName" AND {TenantContext.TenantWhereClause(
                    "v"
                )}) AS "Applications"
                FROM "dmscs"."ClaimSet" c
                WHERE {ClaimSetWhereClause("c")}{filterClause}
                {orderByClause}
                {query.BuildPagingClause()};
                """;

            var claimSets = await connection.QueryAsync(
                sql,
                param: new
                {
                    query.Limit,
                    query.Offset,
                    TenantId,
                    query.Id,
                    query.Name,
                }
            );

            var items = claimSets
                .Select(row => new ClaimSetResponse
                {
                    Id = row.Id,
                    Name = row.ClaimSetName,
                    IsSystemReserved = row.IsSystemReserved,
                    Applications = ParseApplications(row.Applications),
                })
                .ToList();

            return new ClaimSetQueryResult.Success(items);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Query claim set failure");
            return new ClaimSetQueryResult.FailureUnknown(ex.Message);
        }
    }

    public async Task<ClaimSetGetResult> GetClaimSet(long id)
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);

        try
        {
            string sql = $"""
                SELECT c."Id", c."ClaimSetName", c."IsSystemReserved"
                    ,(SELECT jsonb_agg(jsonb_build_object('applicationName', a."ApplicationName"))
                        FROM "dmscs"."Application" a
                        INNER JOIN "dmscs"."Vendor" v ON a."VendorId" = v."Id"
                        WHERE a."ClaimSetName" = c."ClaimSetName" AND {TenantContext.TenantWhereClause(
                    "v"
                )}) AS "Applications"
                FROM "dmscs"."ClaimSet" c
                WHERE c."Id" = @Id AND {ClaimSetWhereClause("c")}
                """;

            var claimSets = (
                await connection.QueryAsync<dynamic>(sql, param: new { Id = id, TenantId })
            ).ToList();

            if (claimSets.Count == 0)
            {
                return new ClaimSetGetResult.FailureNotFound();
            }

            var hierarchyResult = await claimsHierarchyRepository.GetClaimsHierarchy();
            if (hierarchyResult is not ClaimsHierarchyGetResult.Success hierarchy)
            {
                return new ClaimSetGetResult.FailureUnknown(
                    GetClaimsHierarchyFailureMessage(hierarchyResult)
                );
            }

            var row = claimSets.Single();
            var returnClaimSet = CreateClaimSetResponse(row, hierarchy.Claims);

            return new ClaimSetGetResult.Success(returnClaimSet);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Get claim set failure");
            return new ClaimSetGetResult.FailureUnknown(ex.Message);
        }
    }

    public async Task<ClaimSetUpdateResult> UpdateClaimSet(ClaimSetUpdateCommand command)
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        try
        {
            // Get the current claim set (including system-reserved for proper error messages)
            string getClaimSetSql = $"""
                SELECT "ClaimSetName", "IsSystemReserved"
                FROM "dmscs"."ClaimSet"
                WHERE "Id" = @Id AND {ClaimSetWhereClause()};
                """;

            var existingClaimSet = await connection.QuerySingleOrDefaultAsync<(
                string claimSetName,
                bool isSystemReserved
            )>(getClaimSetSql, new { Id = command.Id, TenantId }, transaction);

            if (existingClaimSet == default)
            {
                return new ClaimSetUpdateResult.FailureNotFound();
            }

            if (existingClaimSet.isSystemReserved)
            {
                return new ClaimSetUpdateResult.FailureSystemReserved();
            }

            string? oldClaimSetName = existingClaimSet.claimSetName;

            string renameClaimSetSql = $"""
                UPDATE "dmscs"."ClaimSet"
                SET "ClaimSetName"=@ClaimSetName, "LastModifiedAt"=@LastModifiedAt, "ModifiedBy"=@ModifiedBy
                WHERE "Id" = @Id AND {TenantContext.TenantWhereClause()};
                """;

            string newClaimSetName = command.Name;

            int affectedRows = await connection.ExecuteAsync(
                renameClaimSetSql,
                new
                {
                    command.Id,
                    ClaimSetName = newClaimSetName,
                    LastModifiedAt = auditContext.GetCurrentTimestamp(),
                    ModifiedBy = auditContext.GetCurrentUser(),
                    TenantId,
                },
                transaction
            );

            if (affectedRows == 0)
            {
                return new ClaimSetUpdateResult.FailureNotFound();
            }

            // Update applications belonging to vendors in the same tenant
            string updateApplicationSql = $"""
                UPDATE "dmscs"."Application" a
                SET "ClaimSetName" = @NewClaimSetName
                FROM "dmscs"."Vendor" v
                WHERE a."VendorId" = v."Id"
                  AND a."ClaimSetName" = @OldClaimSetName
                  AND {TenantContext.TenantWhereClause("v")};
                """;

            await connection.ExecuteAsync(
                updateApplicationSql,
                new
                {
                    NewClaimSetName = newClaimSetName,
                    OldClaimSetName = oldClaimSetName,
                    TenantId,
                },
                transaction
            );

            // Polly retry policy for handling multi-user conflicts
            AsyncRetryPolicy<ClaimsHierarchySaveResult> retryPolicy = Policy
                .HandleResult<ClaimsHierarchySaveResult>(result =>
                    result is ClaimsHierarchySaveResult.FailureMultiUserConflict
                )
                .RetryAsync(
                    3,
                    onRetry: (result, retryCount) =>
                    {
                        logger.LogWarning(
                            "Retrying ApplyNameChangeToClaimsHierarchy due to multi-user conflict. Attempt {RetryCount}.",
                            retryCount
                        );
                    }
                );

            ClaimsHierarchySaveResult nameChangeResult =
                (
                    await retryPolicy.ExecuteAsync(() =>
                        ApplyNameChangeToClaimsHierarchy(oldClaimSetName, newClaimSetName)
                    )
                )
                ?? new ClaimsHierarchySaveResult.FailureUnknown(
                    "No claim set name change result was returned."
                );

            ClaimSetUpdateResult result = nameChangeResult switch
            {
                ClaimsHierarchySaveResult.Success or ClaimsHierarchySaveResult.FailureHierarchyNotFound =>
                    new ClaimSetUpdateResult.Success(),
                ClaimsHierarchySaveResult.FailureMultipleHierarchiesFound =>
                    new ClaimSetUpdateResult.FailureMultipleHierarchiesFound(),
                ClaimsHierarchySaveResult.FailureMultiUserConflict =>
                    new ClaimSetUpdateResult.FailureMultiUserConflict(),
                ClaimsHierarchySaveResult.FailureUnknown unknown => new ClaimSetUpdateResult.FailureUnknown(
                    unknown.FailureMessage
                ),
                _ => new ClaimSetUpdateResult.FailureUnknown(
                    $"Unhandled ClaimsHierarchyGetResult of type '{nameChangeResult.GetType().Name}'"
                ),
            };

            if (result is ClaimSetUpdateResult.Success)
            {
                await transaction.CommitAsync();
            }
            else
            {
                await transaction.RollbackAsync();
            }

            if (result is ClaimSetUpdateResult.FailureUnknown failureUnknown)
            {
                logger.LogError(failureUnknown.FailureMessage);
            }

            return result;
        }
        catch (PostgresException ex)
            when (ex.SqlState == PostgresErrorCodes.UniqueViolation
                && ex.ConstraintName == "UX_ClaimSet_ClaimSetName"
            )
        {
            logger.LogWarning(ex, "ClaimSetName must be unique");
            await transaction.RollbackAsync();
            return new ClaimSetUpdateResult.FailureDuplicateClaimSetName();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Update claim set failure");
            await transaction.RollbackAsync();
            return new ClaimSetUpdateResult.FailureUnknown(ex.Message);
        }

        void RenameClaimSetInHierarchy(List<Claim> claims, string oldClaimSetName, string newClaimSetName)
        {
            foreach (var claim in claims)
            {
                foreach (var claimSet in claim.ClaimSets)
                {
                    if (claimSet.Name == oldClaimSetName)
                    {
                        claimSet.Name = newClaimSetName;
                    }
                }

                if (claim.Claims.Any())
                {
                    RenameClaimSetInHierarchy(claim.Claims, oldClaimSetName, newClaimSetName);
                }
            }
        }

        async Task<ClaimsHierarchySaveResult> ApplyNameChangeToClaimsHierarchy(
            string oldClaimSetName,
            string newClaimSetName
        )
        {
            // Update all occurrences of claim set name in JSON hierarchy
            var hierarchyResult = await claimsHierarchyRepository.GetClaimsHierarchy();

            return hierarchyResult switch
            {
                ClaimsHierarchyGetResult.FailureHierarchyNotFound =>
                    new ClaimsHierarchySaveResult.FailureHierarchyNotFound(),
                ClaimsHierarchyGetResult.FailureMultipleHierarchiesFound =>
                    new ClaimsHierarchySaveResult.FailureMultipleHierarchiesFound(),
                ClaimsHierarchyGetResult.FailureUnknown unknownFailure =>
                    new ClaimsHierarchySaveResult.FailureUnknown(unknownFailure.FailureMessage),
                ClaimsHierarchyGetResult.Success success => await RenameAndSaveClaimsHierarchy(
                    oldClaimSetName,
                    newClaimSetName,
                    success.Claims,
                    success.LastModifiedDate
                ),
                _ => new ClaimsHierarchySaveResult.FailureUnknown(
                    $"Unhandled ClaimsHierarchyGetResult of type '{hierarchyResult.GetType().Name}'"
                ),
            };
        }

        async Task<ClaimsHierarchySaveResult> RenameAndSaveClaimsHierarchy(
            string oldClaimSetName,
            string newClaimSetName,
            List<Claim> claimsHierarchy,
            DateTime lastModifiedDate
        )
        {
            RenameClaimSetInHierarchy(claimsHierarchy, oldClaimSetName, newClaimSetName);

            return await claimsHierarchyRepository.SaveClaimsHierarchy(
                claimsHierarchy,
                lastModifiedDate,
                transaction
            );
        }
    }

    public async Task<ClaimSetDeleteResult> DeleteClaimSet(long id)
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            // Get the current claim set (including system-reserved for proper error messages)
            string getClaimSetSql = $"""
                SELECT "ClaimSetName", "IsSystemReserved"
                FROM "dmscs"."ClaimSet"
                WHERE "Id" = @Id AND {ClaimSetWhereClause()};
                """;

            var existingClaimSet = await connection.QuerySingleOrDefaultAsync<(
                string claimSetName,
                bool isSystemReserved
            )>(getClaimSetSql, new { Id = id, TenantId }, transaction);

            if (existingClaimSet == default)
            {
                await transaction.RollbackAsync();
                return new ClaimSetDeleteResult.FailureNotFound();
            }

            if (existingClaimSet.isSystemReserved)
            {
                await transaction.RollbackAsync();
                return new ClaimSetDeleteResult.FailureSystemReserved();
            }

            string sql = $"""
                DELETE FROM "dmscs"."ClaimSet" WHERE "Id" = @Id AND {TenantContext.TenantWhereClause()}
                """;
            int affectedRows = await connection.ExecuteAsync(sql, new { Id = id, TenantId }, transaction);

            if (affectedRows == 0)
            {
                await transaction.RollbackAsync();
                return new ClaimSetDeleteResult.FailureNotFound();
            }

            var hierarchyResult = await claimsHierarchyRepository.GetClaimsHierarchy(transaction);
            var deleteResult = hierarchyResult switch
            {
                ClaimsHierarchyGetResult.Success success => await RemoveFromHierarchyAndSave(
                    existingClaimSet.claimSetName,
                    success.Claims,
                    success.LastModifiedDate,
                    transaction
                ),
                ClaimsHierarchyGetResult.FailureHierarchyNotFound => new ClaimSetDeleteResult.Success(),
                ClaimsHierarchyGetResult.FailureMultipleHierarchiesFound =>
                    new ClaimSetDeleteResult.FailureMultipleHierarchiesFound(),
                ClaimsHierarchyGetResult.FailureUnknown failureUnknown =>
                    new ClaimSetDeleteResult.FailureUnknown(failureUnknown.FailureMessage),
                _ => new ClaimSetDeleteResult.FailureUnknown(
                    $"Unhandled ClaimsHierarchyGetResult of type '{hierarchyResult.GetType().Name}'"
                ),
            };

            if (deleteResult is ClaimSetDeleteResult.Success)
            {
                await transaction.CommitAsync();
            }
            else
            {
                await transaction.RollbackAsync();
            }

            return deleteResult;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Delete claim set failure");
            await transaction.RollbackAsync();
            return new ClaimSetDeleteResult.FailureUnknown(ex.Message);
        }

        async Task<ClaimSetDeleteResult> RemoveFromHierarchyAndSave(
            string claimSetName,
            List<Claim> claims,
            DateTime lastModifiedDate,
            DbTransaction deleteTransaction
        )
        {
            claimsHierarchyManager.RemoveClaimSetFromHierarchy(claimSetName, claims);
            var saveResult = await claimsHierarchyRepository.SaveClaimsHierarchy(
                claims,
                lastModifiedDate,
                deleteTransaction
            );

            return saveResult switch
            {
                ClaimsHierarchySaveResult.Success => new ClaimSetDeleteResult.Success(),
                ClaimsHierarchySaveResult.FailureHierarchyNotFound => new ClaimSetDeleteResult.Success(),
                ClaimsHierarchySaveResult.FailureMultipleHierarchiesFound =>
                    new ClaimSetDeleteResult.FailureMultipleHierarchiesFound(),
                ClaimsHierarchySaveResult.FailureMultiUserConflict =>
                    new ClaimSetDeleteResult.FailureMultiUserConflict(),
                ClaimsHierarchySaveResult.FailureUnknown failureUnknown =>
                    new ClaimSetDeleteResult.FailureUnknown(failureUnknown.FailureMessage),
                _ => new ClaimSetDeleteResult.FailureUnknown(
                    $"Unhandled ClaimsHierarchySaveResult of type '{saveResult.GetType().Name}'"
                ),
            };
        }
    }

    public async Task<ClaimSetExportResult> Export(long id)
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        try
        {
            string sql = $"""
                SELECT c."Id", c."ClaimSetName", c."IsSystemReserved"
                    ,(SELECT jsonb_agg(jsonb_build_object('applicationName', a."ApplicationName"))
                        FROM "dmscs"."Application" a
                        INNER JOIN "dmscs"."Vendor" v ON a."VendorId" = v."Id"
                        WHERE a."ClaimSetName" = c."ClaimSetName" AND {TenantContext.TenantWhereClause(
                    "v"
                )}) AS "Applications"
                FROM "dmscs"."ClaimSet" c
                WHERE c."Id" = @Id AND {ClaimSetWhereClause("c")}
                """;
            var claimSets = await connection.QueryAsync<dynamic>(sql, param: new { Id = id, TenantId });

            if (!claimSets.Any())
            {
                return new ClaimSetExportResult.FailureNotFound();
            }

            var hierarchyResult = await claimsHierarchyRepository.GetClaimsHierarchy();
            if (hierarchyResult is not ClaimsHierarchyGetResult.Success hierarchy)
            {
                return new ClaimSetExportResult.FailureUnknown(
                    GetClaimsHierarchyFailureMessage(hierarchyResult)
                );
            }

            var row = claimSets.Single();
            var returnClaimSet = CreateClaimSetResponse(row, hierarchy.Claims);

            return new ClaimSetExportResult.Success(
                new ClaimSetExportResponse
                {
                    Id = returnClaimSet.Id,
                    Name = returnClaimSet.Name,
                    IsSystemReserved = returnClaimSet.IsSystemReserved,
                    Applications = returnClaimSet.Applications,
                    ResourceClaims = returnClaimSet.ResourceClaims,
                }
            );
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Get claim set failure");
            return new ClaimSetExportResult.FailureUnknown(ex.Message);
        }
    }

    private static string GetClaimsHierarchyFailureMessage(ClaimsHierarchyGetResult hierarchyResult)
    {
        return hierarchyResult switch
        {
            ClaimsHierarchyGetResult.FailureHierarchyNotFound => "Claims hierarchy not found.",
            ClaimsHierarchyGetResult.FailureMultipleHierarchiesFound =>
                "Multiple claims hierarchies found when exactly one was expected.",
            ClaimsHierarchyGetResult.FailureUnknown failureUnknown => failureUnknown.FailureMessage,
            _ => $"Unhandled ClaimsHierarchyGetResult of type '{hierarchyResult.GetType().Name}'",
        };
    }

    public async Task<ClaimSetImportResult> Import(ClaimSetImportCommand command)
    {
        if (string.IsNullOrEmpty(command.Name))
        {
            return new ClaimSetImportResult.FailureUnknown("Import claim set name is required.");
        }

        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        try
        {
            string insertSql = """
                INSERT INTO "dmscs"."ClaimSet" ("ClaimSetName", "IsSystemReserved", "CreatedBy", "TenantId")
                VALUES (@ClaimSetName, @IsSystemReserved, @CreatedBy, @TenantId)
                ON CONFLICT ON CONSTRAINT "UX_ClaimSet_ClaimSetName" DO NOTHING
                RETURNING "Id", "IsSystemReserved", "TenantId";
                """;

            var claimSet = await connection.QuerySingleOrDefaultAsync<ClaimSetImportLookupResult>(
                insertSql,
                new
                {
                    ClaimSetName = command.Name,
                    IsSystemReserved = false,
                    CreatedBy = auditContext.GetCurrentUser(),
                    TenantId,
                },
                transaction
            );

            if (claimSet is null)
            {
                string existingSql = $"""
                    SELECT "Id", "IsSystemReserved", "TenantId"
                    FROM "dmscs"."ClaimSet"
                    WHERE "ClaimSetName" = @ClaimSetName;
                    """;

                claimSet = await connection.QuerySingleOrDefaultAsync<ClaimSetImportLookupResult>(
                    existingSql,
                    new { ClaimSetName = command.Name },
                    transaction
                );
            }

            if (claimSet is null)
            {
                throw new InvalidOperationException("Claim set upsert did not return a record.");
            }

            if (claimSet.IsSystemReserved)
            {
                await transaction.RollbackAsync();
                return new ClaimSetImportResult.FailureSystemReserved();
            }

            if (claimSet.TenantId != TenantId)
            {
                await transaction.RollbackAsync();
                return new ClaimSetImportResult.FailureDuplicateClaimSetName();
            }

            long claimSetId = claimSet.Id;

            var claimsHierarchyResult = await claimsHierarchyRepository.GetClaimsHierarchy(transaction);

            var claimImportResult = claimsHierarchyResult switch
            {
                ClaimsHierarchyGetResult.FailureHierarchyNotFound => new ClaimSetImportResult.FailureUnknown(
                    "Claims hierarchy not found."
                ),
                ClaimsHierarchyGetResult.FailureMultipleHierarchiesFound =>
                    new ClaimSetImportResult.FailureUnknown(
                        "Multiple claims hierarchies found when exactly one was expected."
                    ),
                ClaimsHierarchyGetResult.FailureUnknown failureUnknown =>
                    new ClaimSetImportResult.FailureUnknown(failureUnknown.FailureMessage),
                ClaimsHierarchyGetResult.Success => null,
                _ => throw new NotSupportedException(
                    $"'{claimsHierarchyResult.GetType().Name}' is not a handled {nameof(claimsHierarchyResult)}."
                ),
            };

            if (claimImportResult is not null)
            {
                return claimImportResult;
            }

            var success = claimsHierarchyResult as ClaimsHierarchyGetResult.Success;
            var claimsHierarchy = success!.Claims;
            var lastModifiedDate = success!.LastModifiedDate;

            // Remove existing metadata for the claim set
            claimsHierarchyManager.RemoveClaimSetFromHierarchy(command.Name, claimsHierarchy);

            // Apply imported claim set metadata
            var skippedClaims = claimsHierarchyManager.ApplyImportedClaimSetToHierarchy(
                command,
                claimsHierarchy
            );

            if (skippedClaims.Count > 0)
            {
                string sanitizedClaimSetName = LoggingUtility.SanitizeForLog(command.Name);
                string sanitizedSkippedClaims = string.Join(
                    ", ",
                    skippedClaims.Select(LoggingUtility.SanitizeForLog)
                );

                logger.LogWarning(
                    "Skipped {SkippedCount} claims while importing claim set {ClaimSetName}: {SkippedClaims}",
                    skippedClaims.Count,
                    sanitizedClaimSetName,
                    sanitizedSkippedClaims
                );
            }

            // Save the JSON hierarchy with optimistic locking
            var retryPolicy = Policy
                .Handle<DBConcurrencyException>()
                .RetryAsync(
                    3,
                    async (exception, retryCount) =>
                    {
                        logger.LogWarning("Retrying save due to conflict. Attempt {RetryCount}.", retryCount);

                        // Reload hierarchy and reapply changes
                        claimsHierarchyResult = (
                            await claimsHierarchyRepository.GetClaimsHierarchy(transaction)
                        );

                        success = claimsHierarchyResult as ClaimsHierarchyGetResult.Success;

                        if (success is null)
                        {
                            throw new Exception(
                                "An unexpected error occurred while reloading the claims hierarchy due to a concurrency check failure."
                            );
                        }

                        claimsHierarchy = success.Claims;
                        lastModifiedDate = success.LastModifiedDate;

                        // Apply the changes to the refreshed claims hierarchy
                        claimsHierarchyManager.RemoveClaimSetFromHierarchy(command.Name, claimsHierarchy!);
                        var retriedSkippedClaims = claimsHierarchyManager.ApplyImportedClaimSetToHierarchy(
                            command,
                            claimsHierarchy!
                        );

                        if (retriedSkippedClaims.Count > 0)
                        {
                            string sanitizedClaimSetName = LoggingUtility.SanitizeForLog(command.Name);
                            string sanitizedRetriedSkippedClaims = string.Join(
                                ", ",
                                retriedSkippedClaims.Select(LoggingUtility.SanitizeForLog)
                            );

                            logger.LogWarning(
                                "Skipped {SkippedCount} claims while reapplying claim set {ClaimSetName}: {SkippedClaims}",
                                retriedSkippedClaims.Count,
                                sanitizedClaimSetName,
                                sanitizedRetriedSkippedClaims
                            );
                        }

                        skippedClaims = retriedSkippedClaims;
                    }
                );

            await retryPolicy.ExecuteAsync(async () =>
            {
                var saveResults = await claimsHierarchyRepository.SaveClaimsHierarchy(
                    claimsHierarchy,
                    lastModifiedDate,
                    transaction
                );

                if (saveResults is ClaimsHierarchySaveResult.FailureMultiUserConflict)
                {
                    throw new DBConcurrencyException("Optimistic lock concurrency failure.");
                }

                if (saveResults is not ClaimsHierarchySaveResult.Success)
                {
                    throw new Exception(
                        "An unexpected error occurred while saving reloading the claims hierarchy due to a concurrency check failure."
                    );
                }
            });

            await transaction.CommitAsync();
            return new ClaimSetImportResult.Success(claimSetId, skippedClaims);
        }
        catch (PostgresException ex)
            when (ex.SqlState == PostgresErrorCodes.UniqueViolation
                && ex.ConstraintName == "UX_ClaimSet_ClaimSetName"
            )
        {
            logger.LogWarning(ex, "ClaimSetName must be unique");
            await transaction.RollbackAsync();
            return new ClaimSetImportResult.FailureDuplicateClaimSetName();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Import claim set failure");
            await transaction.RollbackAsync();
            return new ClaimSetImportResult.FailureUnknown(ex.Message);
        }
    }

    private sealed record ClaimSetImportLookupResult(long Id, bool IsSystemReserved, long? TenantId);

    public async Task<ClaimSetCopyResult> Copy(ClaimSetCopyCommand command)
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            // Include system-reserved claimsets so users can copy from them
            string selectSql = $"""
                SELECT "ClaimSetName", "IsSystemReserved"
                FROM "dmscs"."ClaimSet"
                WHERE "Id" = @OriginalId AND {ClaimSetWhereClause()};
                """;

            var originalClaimSet = await connection.QuerySingleOrDefaultAsync(
                selectSql,
                new { command.OriginalId, TenantId },
                transaction
            );

            if (originalClaimSet is null)
            {
                await transaction.RollbackAsync();
                return new ClaimSetCopyResult.FailureNotFound();
            }

            string insertSql = """
                INSERT INTO "dmscs"."ClaimSet" ("ClaimSetName", "IsSystemReserved", "CreatedBy", "TenantId")
                VALUES (@ClaimSetName, @IsSystemReserved, @CreatedBy, @TenantId)
                RETURNING "Id";
                """;

            long newId = await connection.ExecuteScalarAsync<long>(
                insertSql,
                new
                {
                    ClaimSetName = command.Name,
                    IsSystemReserved = false,
                    CreatedBy = auditContext.GetCurrentUser(),
                    TenantId,
                },
                transaction
            );

            var hierarchyResult = await claimsHierarchyRepository.GetClaimsHierarchy(transaction);
            var copyResult = hierarchyResult switch
            {
                ClaimsHierarchyGetResult.Success success => await CloneAndSaveHierarchy(
                    (string)originalClaimSet.ClaimSetName,
                    command.Name,
                    success.Claims,
                    success.LastModifiedDate,
                    transaction,
                    newId
                ),
                ClaimsHierarchyGetResult.FailureHierarchyNotFound => new ClaimSetCopyResult.Success(newId),
                ClaimsHierarchyGetResult.FailureMultipleHierarchiesFound =>
                    new ClaimSetCopyResult.FailureMultipleHierarchiesFound(),
                ClaimsHierarchyGetResult.FailureUnknown failureUnknown =>
                    new ClaimSetCopyResult.FailureUnknown(failureUnknown.FailureMessage),
                _ => new ClaimSetCopyResult.FailureUnknown(
                    $"Unhandled ClaimsHierarchyGetResult of type '{hierarchyResult.GetType().Name}'"
                ),
            };

            if (copyResult is ClaimSetCopyResult.Success)
            {
                await transaction.CommitAsync();
            }
            else
            {
                await transaction.RollbackAsync();
            }

            return copyResult;
        }
        catch (PostgresException ex)
            when (ex.SqlState == PostgresErrorCodes.UniqueViolation
                && ex.ConstraintName == "UX_ClaimSet_ClaimSetName"
            )
        {
            logger.LogWarning(ex, "ClaimSetName must be unique");
            await transaction.RollbackAsync();
            return new ClaimSetCopyResult.FailureDuplicateClaimSetName();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Copy claim set failure");
            await transaction.RollbackAsync();
            return new ClaimSetCopyResult.FailureUnknown(ex.Message);
        }

        async Task<ClaimSetCopyResult> CloneAndSaveHierarchy(
            string sourceClaimSetName,
            string targetClaimSetName,
            List<Claim> claims,
            DateTime lastModifiedDate,
            DbTransaction copyTransaction,
            long copiedId
        )
        {
            claimsHierarchyManager.CloneClaimSetInHierarchy(sourceClaimSetName, targetClaimSetName, claims);
            var saveResult = await claimsHierarchyRepository.SaveClaimsHierarchy(
                claims,
                lastModifiedDate,
                copyTransaction
            );

            return saveResult switch
            {
                ClaimsHierarchySaveResult.Success => new ClaimSetCopyResult.Success(copiedId),
                ClaimsHierarchySaveResult.FailureHierarchyNotFound => new ClaimSetCopyResult.Success(
                    copiedId
                ),
                ClaimsHierarchySaveResult.FailureMultipleHierarchiesFound =>
                    new ClaimSetCopyResult.FailureMultipleHierarchiesFound(),
                ClaimsHierarchySaveResult.FailureMultiUserConflict =>
                    new ClaimSetCopyResult.FailureMultiUserConflict(),
                ClaimsHierarchySaveResult.FailureUnknown failureUnknown =>
                    new ClaimSetCopyResult.FailureUnknown(failureUnknown.FailureMessage),
                _ => new ClaimSetCopyResult.FailureUnknown(
                    $"Unhandled ClaimsHierarchySaveResult of type '{saveResult.GetType().Name}'"
                ),
            };
        }
    }

    private static ClaimSetResponse CreateClaimSetResponse(dynamic row, List<Claim> hierarchy)
    {
        var claimSetName = (string)row.ClaimSetName;
        return new ClaimSetResponse
        {
            Id = row.Id,
            Name = claimSetName,
            IsSystemReserved = row.IsSystemReserved,
            Applications = ParseApplications(row.Applications),
            ResourceClaims = BuildResourceClaims(claimSetName, hierarchy),
        };
    }

    private static JsonElement ParseApplications(object? applications)
    {
        using var document = JsonDocument.Parse(applications?.ToString() ?? "[]");
        return document.RootElement.Clone();
    }

    private static List<ResourceClaim> BuildResourceClaims(string claimSetName, IEnumerable<Claim> claims)
    {
        var resourceClaims = new List<ResourceClaim>();

        foreach (var claim in claims)
        {
            AddResourceClaims(claim, null);
        }

        return resourceClaims;

        void AddResourceClaims(Claim claim, string? parentClaimName)
        {
            var matchingClaimSet = claim.ClaimSets.Find(cs =>
                cs.Name.Equals(claimSetName, StringComparison.OrdinalIgnoreCase)
            );

            if (matchingClaimSet is not null)
            {
                resourceClaims.Add(
                    new ResourceClaim
                    {
                        Name = GetLeafName(claim.Name),
                        ClaimName = claim.Name,
                        ParentClaimName = parentClaimName,
                        Actions = matchingClaimSet
                            .Actions.Select(action => new ResourceClaimAction
                            {
                                Name = action.Name,
                                Enabled = true,
                            })
                            .ToList(),
                        DefaultAuthorizationStrategies =
                            claim
                                .DefaultAuthorization?.Actions.Select(
                                    defaultAction => new ClaimSetResourceClaimActionAuthStrategies
                                    {
                                        ActionName = defaultAction.Name,
                                        AuthorizationStrategies = defaultAction
                                            .AuthorizationStrategies.Select(
                                                strategy => new AuthorizationStrategy
                                                {
                                                    AuthorizationStrategyName = strategy.Name,
                                                }
                                            )
                                            .ToList(),
                                    }
                                )
                                .ToList()
                            ?? [],
                        AuthorizationStrategyOverrides = matchingClaimSet
                            .Actions.Where(action =>
                                action.AuthorizationStrategyOverrides is not null
                                && action.AuthorizationStrategyOverrides.Any()
                            )
                            .Select(action => new ClaimSetResourceClaimActionAuthStrategies
                            {
                                ActionName = action.Name,
                                AuthorizationStrategies = action
                                    .AuthorizationStrategyOverrides.Select(
                                        strategy => new AuthorizationStrategy
                                        {
                                            AuthorizationStrategyName = strategy.Name,
                                        }
                                    )
                                    .ToList(),
                            })
                            .ToList(),
                    }
                );
            }

            foreach (var childClaim in claim.Claims)
            {
                AddResourceClaims(childClaim, claim.Name);
            }
        }
    }

    private static string GetLeafName(string claimName)
    {
        int lastSlashIndex = claimName.LastIndexOf('/');
        return lastSlashIndex >= 0 ? claimName[(lastSlashIndex + 1)..] : claimName;
    }
}
