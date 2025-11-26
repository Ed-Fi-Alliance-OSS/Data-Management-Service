// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Text.Json;
using Dapper;
using EdFi.DmsConfigurationService.Backend.Models.ClaimsHierarchy;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
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
                SELECT Id, AuthorizationStrategyName, DisplayName
                FROM dmscs.AuthorizationStrategy
                WHERE {TenantContext.TenantWhereClause()};
                """;

            var authorizationStrategies = await connection.QueryAsync(sql, new { TenantId });

            var authStratResponse = authorizationStrategies
                .Select(row => new AuthorizationStrategy
                {
                    Id = row.id,
                    AuthorizationStrategyName = row.authorizationstrategyname,
                    DisplayName = row.displayname,
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
                   INSERT INTO dmscs.ClaimSet (ClaimSetName, IsSystemReserved, CreatedBy, TenantId)
                   VALUES(@ClaimSetName, @IsSystemReserved, @CreatedBy, @TenantId)
                   RETURNING Id;
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
            when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.Message.Contains("idx_claimsetname")
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

    public async Task<ClaimSetQueryResult> QueryClaimSet(PagingQuery query)
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        try
        {
            string sql = $"""
                SELECT c.Id, c.ClaimSetName, c.IsSystemReserved
                    ,(SELECT jsonb_agg(jsonb_build_object('applicationName', a.ApplicationName))
                        FROM dmscs.application a
                        INNER JOIN dmscs.vendor v ON a.VendorId = v.Id
                        WHERE a.ClaimSetName = c.ClaimSetName AND {TenantContext.TenantWhereClause(
                    "v"
                )}) as applications
                FROM dmscs.ClaimSet c
                WHERE {TenantContext.TenantWhereClause("c")}
                ORDER BY c.Id
                LIMIT @Limit OFFSET @Offset;
                """;

            var claimSets = await connection.QueryAsync(
                sql,
                param: new
                {
                    query.Limit,
                    query.Offset,
                    TenantId,
                }
            );

            var items = claimSets
                .Select(row => new ClaimSetResponse
                {
                    Id = row.id,
                    Name = row.claimsetname,
                    IsSystemReserved = row.issystemreserved,
                    Applications = JsonDocument.Parse(row.applications?.ToString() ?? "{}").RootElement,
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
                SELECT c.Id, c.ClaimSetName, c.IsSystemReserved
                    ,(SELECT jsonb_agg(jsonb_build_object('applicationName', a.ApplicationName))
                        FROM dmscs.application a
                        INNER JOIN dmscs.vendor v ON a.VendorId = v.Id
                        WHERE a.ClaimSetName = c.ClaimSetName AND {TenantContext.TenantWhereClause(
                    "v"
                )}) as applications
                FROM dmscs.ClaimSet c
                WHERE c.Id = @Id AND {TenantContext.TenantWhereClause("c")}
                """;

            var claimSets = (
                await connection.QueryAsync<dynamic>(sql, param: new { Id = id, TenantId })
            ).ToList();

            if (claimSets.Count == 0)
            {
                return new ClaimSetGetResult.FailureNotFound();
            }

            var returnClaimSet = (
                claimSets.Select(result => new ClaimSetResponse
                {
                    Id = result.id,
                    Name = result.claimsetname,
                    IsSystemReserved = result.issystemreserved,
                    Applications = JsonDocument.Parse(result.applications?.ToString() ?? "{}").RootElement,
                })
            ).Single();

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
            // Get the current claim set name
            string getClaimSetSql = $"""
                SELECT ClaimSetName, IsSystemReserved
                FROM dmscs.ClaimSet
                WHERE Id = @Id AND {TenantContext.TenantWhereClause()};
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
                UPDATE dmscs.ClaimSet
                SET ClaimSetName=@ClaimSetName, LastModifiedAt=@LastModifiedAt, ModifiedBy=@ModifiedBy
                WHERE Id = @Id AND {TenantContext.TenantWhereClause()};
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
                UPDATE dmscs.Application a
                SET ClaimSetName = @NewClaimSetName
                FROM dmscs.Vendor v
                WHERE a.VendorId = v.Id
                  AND a.ClaimSetName = @OldClaimSetName
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
            when (ex is { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "idx_claimsetname" })
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
        try
        {
            // Get the current claim set name to distinguish between Not Found and System Reserved responses
            string getClaimSetSql = $"""
                SELECT ClaimSetName, IsSystemReserved
                FROM dmscs.ClaimSet
                WHERE Id = @Id AND {TenantContext.TenantWhereClause()};
                """;

            var existingClaimSet = await connection.QuerySingleOrDefaultAsync<(
                string claimSetName,
                bool isSystemReserved
            )>(getClaimSetSql, new { Id = id, TenantId });

            if (existingClaimSet == default)
            {
                return new ClaimSetDeleteResult.FailureNotFound();
            }

            if (existingClaimSet.isSystemReserved)
            {
                return new ClaimSetDeleteResult.FailureSystemReserved();
            }

            string sql = $"""
                DELETE FROM dmscs.ClaimSet WHERE Id = @Id AND {TenantContext.TenantWhereClause()}
                """;
            int affectedRows = await connection.ExecuteAsync(sql, new { Id = id, TenantId });

            return affectedRows > 0
                ? new ClaimSetDeleteResult.Success()
                : new ClaimSetDeleteResult.FailureNotFound();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Delete claim set failure");
            return new ClaimSetDeleteResult.FailureUnknown(ex.Message);
        }
    }

    public async Task<ClaimSetExportResult> Export(long id)
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        try
        {
            string sql = $"""
                SELECT c.Id, c.ClaimSetName, c.IsSystemReserved
                    ,(SELECT jsonb_agg(jsonb_build_object('applicationName', a.ApplicationName))
                        FROM dmscs.application a
                        INNER JOIN dmscs.vendor v ON a.VendorId = v.Id
                        WHERE a.ClaimSetName = c.ClaimSetName AND {TenantContext.TenantWhereClause(
                    "v"
                )}) as applications
                FROM dmscs.ClaimSet c
                WHERE c.Id = @Id AND {TenantContext.TenantWhereClause("c")}
                """;
            var claimSets = await connection.QueryAsync<dynamic>(sql, param: new { Id = id, TenantId });

            if (!claimSets.Any())
            {
                return new ClaimSetExportResult.FailureNotFound();
            }

            var returnClaimSet = claimSets.Select(result => new ClaimSetExportResponse
            {
                Id = result.id,
                Name = result.claimsetname,
                IsSystemReserved = result.issystemreserved,
                Applications = JsonDocument.Parse(result.applications?.ToString() ?? "{}").RootElement,
            });

            return new ClaimSetExportResult.Success(returnClaimSet.Single());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Get claim set failure");
            return new ClaimSetExportResult.FailureUnknown(ex.Message);
        }
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
            // Check if the claim set already exists for this tenant
            string checkSql = $"""
                    SELECT Id FROM dmscs.ClaimSet WHERE ClaimSetName = @ClaimSetName AND {TenantContext.TenantWhereClause()};
                """;
            var existingClaimSetId = await connection.QuerySingleOrDefaultAsync<long?>(
                checkSql,
                new { ClaimSetName = command.Name, TenantId },
                transaction
            );

            if (existingClaimSetId != null)
            {
                return new ClaimSetImportResult.FailureDuplicateClaimSetName();
            }

            // Insert new claim set
            string insertSql = """
                    INSERT INTO dmscs.ClaimSet (ClaimSetName, IsSystemReserved, CreatedBy, TenantId)
                    VALUES(@ClaimSetName, @IsSystemReserved, @CreatedBy, @TenantId)
                    RETURNING Id;
                """;
            existingClaimSetId = await connection.ExecuteScalarAsync<long>(
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

            if (claimImportResult != null)
            {
                return claimImportResult;
            }

            var success = claimsHierarchyResult as ClaimsHierarchyGetResult.Success;
            var claimsHierarchy = success!.Claims;
            var lastModifiedDate = success!.LastModifiedDate;

            // Remove existing metadata for the claim set
            claimsHierarchyManager.RemoveClaimSetFromHierarchy(command.Name, claimsHierarchy);

            // Apply imported claim set metadata
            claimsHierarchyManager.ApplyImportedClaimSetToHierarchy(command, claimsHierarchy);

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

                        if (success == null)
                        {
                            throw new Exception(
                                "An unexpected error occurred while reloading the claims hierarchy due to a concurrency check failure."
                            );
                        }

                        claimsHierarchy = success.Claims;
                        lastModifiedDate = success.LastModifiedDate;

                        // Apply the changes to the refreshed claims hierarchy
                        claimsHierarchyManager.RemoveClaimSetFromHierarchy(command.Name, claimsHierarchy!);
                        claimsHierarchyManager.ApplyImportedClaimSetToHierarchy(command, claimsHierarchy!);
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
            return new ClaimSetImportResult.Success(existingClaimSetId.Value);
        }
        catch (PostgresException ex) when (ex.SqlState == "23505" && ex.Message.Contains("idx_claimsetname"))
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

    public async Task<ClaimSetCopyResult> Copy(ClaimSetCopyCommand command)
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            string selectSql = $"""
                SELECT ClaimSetName, IsSystemReserved
                FROM dmscs.ClaimSet
                WHERE Id = @OriginalId AND {TenantContext.TenantWhereClause()};
                """;

            var originalClaimSet = await connection.QuerySingleOrDefaultAsync(
                selectSql,
                new { command.OriginalId, TenantId },
                transaction
            );

            if (originalClaimSet == null)
            {
                return new ClaimSetCopyResult.FailureNotFound();
            }

            string insertSql = """
                INSERT INTO dmscs.ClaimSet (ClaimSetName, IsSystemReserved, CreatedBy, TenantId)
                VALUES (@ClaimSetName, @IsSystemReserved, @CreatedBy, @TenantId)
                RETURNING Id;
                """;

            long newId = await connection.ExecuteScalarAsync<long>(
                insertSql,
                new
                {
                    ClaimSetName = command.Name,
                    IsSystemReserved = (bool)originalClaimSet.issystemreserved,
                    CreatedBy = auditContext.GetCurrentUser(),
                    TenantId,
                },
                transaction
            );

            await transaction.CommitAsync();

            return new ClaimSetCopyResult.Success(newId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Copy claim set failure");
            await transaction.RollbackAsync();
            return new ClaimSetCopyResult.FailureUnknown(ex.Message);
        }
    }
}
