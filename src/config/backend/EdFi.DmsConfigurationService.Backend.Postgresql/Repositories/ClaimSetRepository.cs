// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.ClaimSets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Action = EdFi.DmsConfigurationService.DataModel.Model.Action.Action;
using AuthorizationStrategy = EdFi.DmsConfigurationService.DataModel.Model.ClaimSets.AuthorizationStrategy;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Repositories;

public class ClaimSetRepository(IOptions<DatabaseOptions> databaseOptions, ILogger<ClaimSetRepository> logger)
    : IClaimSetRepository
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
            }
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

    private static string SerializeResourceClaim(List<ResourceClaim>? resourceClaims)
    {
        return JsonSerializer.Serialize(
            resourceClaims,
            new JsonSerializerOptions
            {
                WriteIndented = false,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            }
        );
    }

    public async Task<ClaimSetInsertResult> InsertClaimSet(ClaimSetInsertCommand command)
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        try
        {
            string sql = """
                   INSERT INTO dmscs.ClaimSet (ClaimSetName, IsSystemReserved, ResourceClaims)
                   VALUES(@ClaimSetName, @IsSystemReserved, @ResourceClaims::jsonb)
                   RETURNING Id;
                """;

            var parameters = new
            {
                ClaimSetName = command.Name,
                IsSystemReserved = false,
                ResourceClaims = SerializeResourceClaim(command.ResourceClaims),
            };

            long id = await connection.ExecuteScalarAsync<long>(sql, parameters);
            await transaction.CommitAsync();

            return new ClaimSetInsertResult.Success(id);
        }
        catch (PostgresException ex) when (ex.SqlState == "23505" && ex.Message.Contains("uq_claimsetname"))
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
            string baseSql = """
                SELECT c.Id, c.ClaimSetName, c.IsSystemReserved
                    ,(SELECT jsonb_agg(jsonb_build_object('applicationName', a.ApplicationName))
                        FROM dmscs.application a WHERE a.ClaimSetName = c.ClaimSetName ) as applications
                    {0}
                FROM dmscs.ClaimSet c
                ORDER BY c.Id
                LIMIT @Limit OFFSET @Offset;
                """;

            string resourceClaimsColumn = verbose ? ", ResourceClaims::TEXT AS ResourceClaims" : "";
            string sql = string.Format(baseSql, resourceClaimsColumn);

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
                        ResourceClaims = JsonDocument.Parse(row.resourceclaims).RootElement,
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
            string baseSql = """
                SELECT c.Id, c.ClaimSetName, c.IsSystemReserved
                    ,(SELECT jsonb_agg(jsonb_build_object('applicationName', a.ApplicationName))
                        FROM dmscs.application a WHERE a.ClaimSetName = c.ClaimSetName ) as applications
                    {0}
                FROM dmscs.ClaimSet c
                WHERE c.Id = @Id
                """;

            string resourceClaimsColumn = verbose ? ", ResourceClaims" : "";
            string sql = string.Format(baseSql, resourceClaimsColumn);

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
                    ResourceClaims = verbose ? JsonDocument.Parse(result.resourceclaims).RootElement : null,
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
            string sql = """
                UPDATE dmscs.ClaimSet
                SET ClaimSetName=@ClaimSetName, IsSystemReserved=@IsSystemReserved, ResourceClaims=@ResourceClaims::jsonb
                WHERE Id = @Id;
                """;

            var parameters = new
            {
                command.Id,
                ClaimSetName = command.Name,
                IsSystemReserved = false,
                ResourceClaims = SerializeResourceClaim(command.ResourceClaims),
            };

            int affectedRows = await connection.ExecuteAsync(sql, parameters);

            if (affectedRows == 0)
            {
                return new ClaimSetUpdateResult.FailureNotFound();
            }
            await transaction.CommitAsync();

            return new ClaimSetUpdateResult.Success();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Update claim set failure");
            await transaction.RollbackAsync();
            return new ClaimSetUpdateResult.FailureUnknown(ex.Message);
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
                SELECT c.Id, c.ClaimSetName, c.IsSystemReserved, c.ResourceClaims
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
                ResourceClaims = JsonDocument.Parse(result.resourceclaims).RootElement,
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
                   INSERT INTO dmscs.ClaimSet (ClaimSetName, IsSystemReserved, ResourceClaims)
                   VALUES(@ClaimSetName, @IsSystemReserved, @ResourceClaims::jsonb)
                   RETURNING Id;
                """;

            var parameters = new
            {
                ClaimSetName = command.Name,
                IsSystemReserved = false,
                ResourceClaims = SerializeResourceClaim(command.ResourceClaims),
            };

            long id = await connection.ExecuteScalarAsync<long>(sql, parameters);
            await transaction.CommitAsync();

            return new ClaimSetImportResult.Success(id);
        }
        catch (PostgresException ex) when (ex.SqlState == "23505" && ex.Message.Contains("uq_claimsetname"))
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
                SELECT ClaimSetName, IsSystemReserved, ResourceClaims::TEXT AS ResourceClaims
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

            var resourceClaimsJson = JsonDocument.Parse((string)originalClaimSet.resourceclaims).RootElement;

            string insertSql = """
                INSERT INTO dmscs.ClaimSet (ClaimSetName, IsSystemReserved, ResourceClaims)
                VALUES (@ClaimSetName, @IsSystemReserved, @ResourceClaims::jsonb)
                RETURNING Id;
                """;

            long newId = await connection.ExecuteScalarAsync<long>(
                insertSql,
                new
                {
                    ClaimSetName = command.Name,
                    IsSystemReserved = (bool)originalClaimSet.issystemreserved,
                    ResourceClaims = resourceClaimsJson.GetRawText(),
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
