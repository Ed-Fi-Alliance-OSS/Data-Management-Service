// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Runtime.InteropServices.ComTypes;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.ClaimSets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Polly;
using Action = EdFi.DmsConfigurationService.DataModel.Model.Action.Action;
using AuthorizationStrategy = EdFi.DmsConfigurationService.DataModel.Model.ClaimSets.AuthorizationStrategy;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Repositories;

public class ClaimSetRepository(
    IOptions<DatabaseOptions> databaseOptions,
    ILogger<ClaimSetRepository> logger,
    IClaimsHierarchyRepository claimsHierarchyRepository
) : IClaimSetRepository
{
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
            string sql = """
                  SELECT Id, AuthorizationStrategyName, DisplayName
                  FROM dmscs.AuthorizationStrategy;
                """;

            var authorizationStrategies = await connection.QueryAsync(sql);

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
                   INSERT INTO dmscs.ClaimSet (ClaimSetName, IsSystemReserved)
                   VALUES(@ClaimSetName, @IsSystemReserved)
                   RETURNING Id;
                """;

            var parameters = new { ClaimSetName = command.Name, IsSystemReserved = false };

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

    public async Task<ClaimSetQueryResult> QueryClaimSet(PagingQuery query, bool verbose)
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        try
        {
            string sql = """
                SELECT c.Id, c.ClaimSetName, c.IsSystemReserved
                    ,(SELECT jsonb_agg(jsonb_build_object('applicationName', a.ApplicationName))
                        FROM dmscs.application a WHERE a.ClaimSetName = c.ClaimSetName ) as applications
                FROM dmscs.ClaimSet c
                ORDER BY c.Id
                LIMIT @Limit OFFSET @Offset;
                """;

            var claimSets = await connection.QueryAsync(sql, param: query);

            if (verbose)
            {
                var verboseResponses = claimSets
                    .Select(row => new ClaimSetResponse
                    {
                        Id = row.id,
                        Name = row.claimsetname,
                        IsSystemReserved = row.issystemreserved,
                        Applications = JsonDocument.Parse(row.applications?.ToString() ?? "{}").RootElement,
                    })
                    .ToList();
                return new ClaimSetQueryResult.Success(verboseResponses);
            }

            var reducedResponses = claimSets
                .Select(row => new ClaimSetResponseReduced
                {
                    Id = row.id,
                    Name = row.claimsetname,
                    IsSystemReserved = row.issystemreserved,
                    Applications = JsonDocument.Parse(row.applications?.ToString() ?? "{}").RootElement,
                })
                .ToList();
            return new ClaimSetQueryResult.Success(reducedResponses);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Query claim set failure");
            return new ClaimSetQueryResult.FailureUnknown(ex.Message);
        }
    }

    public async Task<ClaimSetGetResult> GetClaimSet(long id, bool verbose)
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        try
        {
            string sql = """
                SELECT c.Id, c.ClaimSetName, c.IsSystemReserved
                    ,(SELECT jsonb_agg(jsonb_build_object('applicationName', a.ApplicationName))
                        FROM dmscs.application a WHERE a.ClaimSetName = c.ClaimSetName ) as applications
                FROM dmscs.ClaimSet c
                WHERE c.Id = @Id
                """;

            var claimSets = await connection.QueryAsync<dynamic>(sql, param: new { Id = id });

            if (!claimSets.Any())
            {
                return new ClaimSetGetResult.FailureNotFound();
            }

            if (verbose)
            {
                var returnClaimSet = claimSets.Select(result => new ClaimSetResponse
                {
                    Id = result.id,
                    Name = result.claimsetname,
                    IsSystemReserved = result.issystemreserved,
                    Applications = JsonDocument.Parse(result.applications?.ToString() ?? "{}").RootElement,
                });

                return new ClaimSetGetResult.Success(returnClaimSet.Single());
            }
            var returnClaimSetReduced = claimSets.Select(result => new ClaimSetResponseReduced
            {
                Id = result.id,
                Name = result.claimsetname,
                IsSystemReserved = result.issystemreserved,
                Applications = JsonDocument.Parse(result.applications?.ToString() ?? "{}").RootElement,
            });

            return new ClaimSetGetResult.Success(returnClaimSetReduced.Single());
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
            string getClaimSetSql = """
                SELECT ClaimSetName
                FROM dmscs.ClaimSet
                WHERE Id = @Id;
                """;

            var getClaimSetParameters = new { Id = command.Id };

            string? oldClaimSetName = await connection.ExecuteScalarAsync<string?>(
                getClaimSetSql,
                getClaimSetParameters,
                transaction
            );

            if (oldClaimSetName == null)
            {
                return new ClaimSetUpdateResult.FailureNotFound();
            }

            string renameClaimSetSql = """
                UPDATE dmscs.ClaimSet
                SET ClaimSetName=@ClaimSetName
                WHERE Id = @Id;
                """;

            string newClaimSetName = command.Name;

            var renameClaimSetParameters = new { command.Id, ClaimSetName = newClaimSetName };

            int affectedRows = await connection.ExecuteAsync(
                renameClaimSetSql,
                renameClaimSetParameters,
                transaction
            );

            if (affectedRows == 0)
            {
                return new ClaimSetUpdateResult.FailureNotFound();
            }

            var updateApplicationParameters = new
            {
                NewClaimSetName = newClaimSetName,
                OldClaimSetName = oldClaimSetName,
            };

            string updateApplicationSql = """
                UPDATE dmscs.Application
                SET ClaimSetName = @NewClaimSetName
                WHERE ClaimSetName = @OldClaimSetName;
                """;

            await connection.ExecuteAsync(updateApplicationSql, updateApplicationParameters, transaction);

            // Polly retry policy for handling multi-user conflicts
            var retryPolicy = Policy
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
                    await retryPolicy.ExecuteAsync(
                        () => ApplyNameChangeToClaimsHierarchy(oldClaimSetName, newClaimSetName)
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
            string sql = """
                DELETE FROM dmscs.ClaimSet WHERE Id = @Id
                """;
            int affectedRows = await connection.ExecuteAsync(sql, new { Id = id });

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
            string sql = """
                SELECT c.Id, c.ClaimSetName, c.IsSystemReserved
                    ,(SELECT jsonb_agg(jsonb_build_object('applicationName', a.ApplicationName))
                        FROM dmscs.application a WHERE a.ClaimSetName = c.ClaimSetName ) as applications
                FROM dmscs.ClaimSet c
                WHERE c.Id = @Id
                """;
            var claimSets = await connection.QueryAsync<dynamic>(sql, param: new { Id = id });

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
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        try
        {
            string sql = """
                   INSERT INTO dmscs.ClaimSet (ClaimSetName, IsSystemReserved)
                   VALUES(@ClaimSetName, @IsSystemReserved)
                   RETURNING Id;
                """;

            var parameters = new { ClaimSetName = command.Name, IsSystemReserved = false };

            long id = await connection.ExecuteScalarAsync<long>(sql, parameters);
            await transaction.CommitAsync();

            return new ClaimSetImportResult.Success(id);
        }
        catch (PostgresException ex) when (ex.SqlState == "23505" && ex.Message.Contains("idx_claimsetname"))
        {
            logger.LogWarning(ex, "ClaimSetName must be unique");
            await transaction.RollbackAsync();
            return new ClaimSetImportResult.FailureDuplicateClaimSetName();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Insert claim set failure");
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
            string selectSql = """
                SELECT ClaimSetName, IsSystemReserved
                FROM dmscs.ClaimSet
                WHERE Id = @OriginalId;
                """;

            var originalClaimSet = await connection.QuerySingleOrDefaultAsync(
                selectSql,
                new { command.OriginalId },
                transaction
            );

            if (originalClaimSet == null)
            {
                return new ClaimSetCopyResult.FailureNotFound();
            }

            string insertSql = """
                INSERT INTO dmscs.ClaimSet (ClaimSetName, IsSystemReserved)
                VALUES (@ClaimSetName, @IsSystemReserved)
                RETURNING Id;
                """;

            long newId = await connection.ExecuteScalarAsync<long>(
                insertSql,
                new
                {
                    ClaimSetName = command.Name,
                    IsSystemReserved = (bool)originalClaimSet.issystemreserved,
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
