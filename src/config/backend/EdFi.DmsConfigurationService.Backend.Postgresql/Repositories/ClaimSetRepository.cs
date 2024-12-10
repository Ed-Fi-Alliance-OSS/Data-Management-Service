// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using Dapper;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel;
using EdFi.DmsConfigurationService.DataModel.Model.ClaimSets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Repositories;

public class ClaimSetRepository(IOptions<DatabaseOptions> databaseOptions, ILogger<ClaimSetRepository> logger)
    : IClaimSetRepository
{
    public IEnumerable<AuthorizationStrategy> GetAuthorizationStrategies()
    {
        var authStrategies = new AuthorizationStrategy[]
        {
            new()
            {
                AuthStrategyId = 1,
                AuthStrategyName = "NoFurtherAuthorizationRequired",
                DisplayName = "No Further Authorization Required",
            },
            new()
            {
                AuthStrategyId = 2,
                AuthStrategyName = "RelationshipsWithEdOrgsAndPeople",
                DisplayName = "Relationships with Education Organizations and People",
            },
            new()
            {
                AuthStrategyId = 3,
                AuthStrategyName = "RelationshipsWithEdOrgsOnly",
                DisplayName = "Relationships with Education Organizations only",
            },
            new()
            {
                AuthStrategyId = 4,
                AuthStrategyName = "NamespaceBased",
                DisplayName = "Namespace Based",
            },
            new()
            {
                AuthStrategyId = 5,
                AuthStrategyName = "RelationshipsWithPeopleOnly",
                DisplayName = "Relationships with People only",
            },
            new()
            {
                AuthStrategyId = 6,
                AuthStrategyName = "RelationshipsWithStudentsOnly",
                DisplayName = "Relationships with Students only",
            },
            new()
            {
                AuthStrategyId = 7,
                AuthStrategyName = "RelationshipsWithStudentsOnlyThroughResponsibility",
                DisplayName =
                    "Relationships with Students only (through StudentEducationOrganizationResponsibilityAssociation)",
            },
            new()
            {
                AuthStrategyId = 8,
                AuthStrategyName = "OwnershipBased",
                DisplayName = "Ownership Based",
            },
            new()
            {
                AuthStrategyId = 9,
                AuthStrategyName = "RelationshipsWithEdOrgsAndPeopleIncludingDeletes",
                DisplayName = "Relationships with Education Organizations and People (including deletes)",
            },
            new()
            {
                AuthStrategyId = 10,
                AuthStrategyName = "RelationshipsWithEdOrgsOnlyInverted",
                DisplayName = "Relationships with Education Organizations only (Inverted)",
            },
            new()
            {
                AuthStrategyId = 11,
                AuthStrategyName = "RelationshipsWithEdOrgsAndPeopleInverted",
                DisplayName = "Relationships with Education Organizations and People (Inverted)",
            },
            new()
            {
                AuthStrategyId = 12,
                AuthStrategyName = "RelationshipsWithStudentsOnlyThroughResponsibilityIncludingDeletes",
                DisplayName =
                    "Relationships with Students only (through StudentEducationOrganizationResponsibilityAssociation, including deletes)",
            },
        };
        return authStrategies;
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
                command.ClaimSetName,
                command.IsSystemReserved,
                ResourceClaims = command.ResourceClaims.ToString(),
            };

            long id = await connection.ExecuteScalarAsync<long>(sql, parameters);
            await transaction.CommitAsync();

            return new ClaimSetInsertResult.Success(id);
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
            string sql = """
                SELECT Id, ClaimSetName, IsSystemReserved, ResourceClaims::TEXT AS ResourceClaims
                FROM dmscs.ClaimSet
                ORDER BY Id
                LIMIT @Limit OFFSET @Offset;
                """;

            var claimSets = await connection.QueryAsync(sql, param: query);

            var returnClaimSets = claimSets
                .Select(row => new ClaimSetResponse
                {
                    Id = (long)row.id,
                    ClaimSetName = (string)row.claimsetname,
                    IsSystemReserved = (bool)row.issystemreserved,
                    ResourceClaims = JsonDocument.Parse((string)row.resourceclaims).RootElement,
                })
                .ToList();

            return new ClaimSetQueryResult.Success(returnClaimSets);
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
            string sql = """
                SELECT Id, ClaimSetName, IsSystemReserved, ResourceClaims FROM dmscs.ClaimSet
                WHERE Id = @Id
                """;

            var claimSets = await connection.QueryAsync<dynamic>(sql, param: new { Id = id });

            if (!claimSets.Any())
            {
                return new ClaimSetGetResult.FailureNotFound();
            }

            var returnClaimSet = claimSets.Select(result => new ClaimSetResponse
            {
                Id = result.id,
                ClaimSetName = result.claimsetname,
                IsSystemReserved = result.issystemreserved,
                ResourceClaims = JsonDocument.Parse(result.resourceclaims).RootElement,
            });

            return new ClaimSetGetResult.Success(returnClaimSet.Single());
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
                command.ClaimSetName,
                command.IsSystemReserved,
                ResourceClaims = JsonSerializer.Serialize(command.ResourceClaims)
            };

            int affectedRows = await connection.ExecuteAsync(sql, parameters);

            if (affectedRows == 0)
            {
                return new ClaimSetUpdateResult.FailureNotExists();
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
                : new ClaimSetDeleteResult.FailureNotExists();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Delete claim set failure");
            return new ClaimSetDeleteResult.FailureUnknown(ex.Message);
        }
    }
}
