// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using Dapper;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.Application;
using EdFi.DmsConfigurationService.DataModel.Model.DataStore;
using EdFi.DmsConfigurationService.DataModel.Model.DataStoreManage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Repositories;

public class DataStoreRepository(
    IOptions<DatabaseOptions> databaseOptions,
    ILogger<DataStoreRepository> logger,
    IConnectionStringEncryptionService encryptionService,
    IConnectionStringReader connectionStringReader,
    IDataStoreContextRepository contextRepository,
    IDataStoreDerivativeRepository derivativeRepository,
    IAuditContext auditContext,
    ITenantContextProvider tenantContextProvider
) : IDataStoreRepository
{
    private TenantContext TenantContext => tenantContextProvider.Context;

    private long? TenantId => TenantContext is TenantContext.Multitenant mt ? mt.TenantId : null;

    private const string DuplicateNameConstraint = "UX_DataStore_TenantId_Name";

    public async Task<int> InsertDataStore(
        DataStoreInsertCommand command,
        DbTransaction transaction,
        CancellationToken cancellationToken
    )
    {
        _ = GetTransactionConnection(transaction, cancellationToken);
        if (
            await DataStoreCatalog.HasManagedNameReservationAsync(
                transaction,
                TenantId,
                command.Name,
                null,
                cancellationToken
            )
        )
        {
            throw new DataStoreCatalog.NameReservedException();
        }
        return await InsertDataStoreRow(command, transaction, cancellationToken);
    }

    /// <summary>Requires admission minted by managed publication under the supplied fence and tenant gate.</summary>
    internal async Task<int> InsertManagedDataStore(
        DataStoreManageRepository.PublicationAdmission admission,
        string runtimeConnectionString,
        DbTransaction transaction,
        CancellationToken cancellationToken
    )
    {
        DbConnection connection = GetTransactionConnection(transaction, cancellationToken);
        if (TenantId != admission.TenantId)
        {
            throw new InvalidOperationException(
                "The ordinary repository publication partition does not match."
            );
        }
        await DataStoreCatalog.AcquireTenantGateAsync(transaction, admission.TenantId, cancellationToken);
        DynamicParameters parameters = new(new { admission.Id, admission.TenantId });
        parameters.Add("Name", CatalogName.Canonicalize(admission.Name), DbType.String, size: -1);
        if (
            await connection.ExecuteScalarAsync<bool>(
                new CommandDefinition(
                    """
                    SELECT EXISTS (SELECT 1 FROM dmscs."DataStore" WHERE ("TenantId" = @TenantId OR ("TenantId" IS NULL AND @TenantId IS NULL)) AND "NameComparisonKey" = @Name COLLATE "C"
                        UNION ALL
                        SELECT 1 FROM dmscs."DataStoreManage"
                        WHERE ("TenantId" = @TenantId OR ("TenantId" IS NULL AND @TenantId IS NULL)) AND "NameComparisonKey" = @Name COLLATE "C"
                            AND "Status" <> 'Deleted' AND "Id" <> @Id);
                    """,
                    parameters,
                    transaction,
                    cancellationToken: cancellationToken
                )
            )
        )
        {
            throw new DataStoreCatalog.NameReservedException();
        }
        return await InsertDataStoreRow(
            new DataStoreInsertCommand
            {
                Name = admission.Name,
                DataStoreType = "Shared",
                Provider = admission.Provider,
                ConnectionString = runtimeConnectionString,
            },
            transaction,
            cancellationToken
        );
    }

    private async Task<int> InsertDataStoreRow(
        DataStoreInsertCommand command,
        DbTransaction transaction,
        CancellationToken cancellationToken
    )
    {
        DbConnection connection = GetTransactionConnection(transaction, cancellationToken);
        var sql = """
            INSERT INTO "dmscs"."DataStore" ("DataStoreType", "Name", "Provider", "ConnectionString", "CreatedBy", "TenantId")
            VALUES (@DataStoreType, @Name, @Provider, @ConnectionString, @CreatedBy, @TenantId)
            RETURNING "Id";
            """;

        var parameters = new
        {
            command.DataStoreType,
            command.Name,
            command.Provider,
            ConnectionString = encryptionService.Encrypt(command.ConnectionString),
            CreatedBy = auditContext.GetCurrentUser(),
            TenantId,
        };

        return await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(sql, parameters, transaction, cancellationToken: cancellationToken)
        );
    }

    private static DbConnection GetTransactionConnection(
        DbTransaction transaction,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(transaction);
        if (transaction is not NpgsqlTransaction)
        {
            throw new ArgumentException(
                "The supplied transaction must be a PostgreSQL transaction.",
                nameof(transaction)
            );
        }

        // Npgsql retains Connection after completion and can otherwise execute in autocommit.
        // Reading IsolationLevel rejects completed or disposed transactions before any side effects.
        _ = transaction.IsolationLevel;
        if (transaction.Connection is not NpgsqlConnection { State: ConnectionState.Open } connection)
        {
            throw new InvalidOperationException("The supplied transaction must have an open connection.");
        }
        return connection;
    }

    /// <summary>
    /// Removes the ordinary row for provider-local managed lifecycle persistence. The caller must hold
    /// the job fence and clear the managed aggregate's link in this same transaction before removal.
    /// Exceptions, including absence and cancellation, must propagate to the owner for rollback;
    /// this method neither commits nor rolls back the caller's transaction.
    /// </summary>
    internal async Task DeleteManagedDataStore(
        int id,
        DbTransaction transaction,
        CancellationToken cancellationToken
    )
    {
        if (await DeleteDataStoreRows(id, transaction, cancellationToken) == 0)
        {
            throw new InvalidOperationException("The data store does not exist in the selected tenant.");
        }
    }

    // Admission is issued only after the managed repository verifies the locked recorded operation.
    internal async Task DeleteManagedDataStore(
        DataStoreManageRepository.DeletionAdmission admission,
        DbTransaction transaction,
        CancellationToken cancellationToken
    )
    {
        if (admission.TenantId != TenantId)
        {
            throw new InvalidOperationException("The managed deletion partition has changed.");
        }
        int removed = await DeleteDataStoreRows(admission.DataStoreId, transaction, cancellationToken);
        if (removed != (admission.AlreadyAbsent ? 0 : 1))
        {
            throw new InvalidOperationException("The verified ordinary data store has changed.");
        }
    }

    private async Task<int> DeleteDataStoreRows(
        int id,
        DbTransaction transaction,
        CancellationToken cancellationToken
    )
    {
        DbConnection connection = GetTransactionConnection(transaction, cancellationToken);
        await DataStoreCatalog.AcquireTenantGateAsync(transaction, TenantId, cancellationToken);
        var sql =
            $"DELETE FROM \"dmscs\".\"DataStore\" WHERE \"Id\" = @Id AND {TenantContext.TenantWhereClause()};";
        return await connection.ExecuteAsync(
            new CommandDefinition(
                sql,
                new { Id = id, TenantId },
                transaction,
                cancellationToken: cancellationToken
            )
        );
    }

    public async Task<DataStoreInsertResult> InsertDataStore(DataStoreInsertCommand command)
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        try
        {
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            int id = await InsertDataStore(command, transaction, CancellationToken.None);
            await transaction.CommitAsync();
            return new DataStoreInsertResult.Success(id);
        }
        catch (DataStoreCatalog.NameReservedException)
        {
            return new DataStoreInsertResult.FailureDuplicateName();
        }
        catch (PostgresException ex)
            when (ex.SqlState == PostgresErrorCodes.UniqueViolation
                && ex.ConstraintName == DuplicateNameConstraint
            )
        {
            logger.LogWarning(ex, "Data store name must be unique within the tenant");
            return new DataStoreInsertResult.FailureDuplicateName();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Insert DataStore failure");
            return new DataStoreInsertResult.FailureUnknown(ex.Message);
        }
    }

    private static readonly IReadOnlyDictionary<string, string> OrderByColumns = new Dictionary<
        string,
        string
    >(StringComparer.OrdinalIgnoreCase)
    {
        ["id"] = "\"Id\"",
        ["dataStoreType"] = "\"DataStoreType\" COLLATE \"C\"",
        ["name"] = "\"NameComparisonKey\" COLLATE \"C\"",
    };

    private static string BuildOrderByClause(DataStoreQuery query)
    {
        if (query.OrderBy is not null && OrderByColumns.TryGetValue(query.OrderBy, out var col))
        {
            string direction = query.IsDescending ? "DESC" : "ASC";
            string tieBreaker = col == "\"Id\"" ? string.Empty : $", \"Id\" {direction}";
            return $"ORDER BY {col} {direction}{tieBreaker}";
        }
        return PostgresqlIdentifier.OrderBy("Id", isDescending: false);
    }

    private static string BuildFilterClause(DataStoreQuery query)
    {
        var conditions = new List<string>();
        if (query.Id.HasValue)
        {
            conditions.Add("\"Id\" = @Id");
        }
        if (query.Name is not null)
        {
            conditions.Add("\"NameComparisonKey\" = @Name COLLATE \"C\"");
        }
        if (query.DataStoreType is not null)
        {
            conditions.Add("convert_to(\"DataStoreType\", 'UTF8') = convert_to(@DataStoreType, 'UTF8')");
        }
        return conditions.Count > 0 ? " AND " + string.Join(" AND ", conditions) : string.Empty;
    }

    public async Task<DataStoreQueryResult> QueryDataStore(DataStoreQuery query)
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        try
        {
            string orderByClause = BuildOrderByClause(query);
            string filterClause = BuildFilterClause(query);
            var sql = $"""
                SELECT "Id", "DataStoreType", "Name", "Provider", "ConnectionString", "TenantId"
                FROM "dmscs"."DataStore"
                WHERE {TenantContext.TenantWhereClause()}{filterClause}
                {orderByClause}
                {query.BuildPagingClause()};
                """;

            var results = await connection.QueryAsync<(
                int Id,
                string DataStoreType,
                string Name,
                string? Provider,
                byte[]? ConnectionString,
                long? TenantId
            )>(
                sql,
                new
                {
                    query.Limit,
                    query.Offset,
                    TenantId,
                    query.Id,
                    Name = query.Name is null ? null : CatalogName.Canonicalize(query.Name),
                    query.DataStoreType,
                }
            );

            var dataStoreList = results.ToList();
            if (!dataStoreList.Any())
            {
                return new DataStoreQueryResult.Success([]);
            }

            var dataStoreIds = dataStoreList.Select(i => i.Id).ToList();

            var contextResult = await contextRepository.GetDataStoreContextsByDataStoreIds(dataStoreIds);
            var contextsByDataStoreId = contextResult switch
            {
                DataStoreContextQueryByDataStoreIdsResult.Success success => success
                    .DataStoreContextResponses.GroupBy(rc => rc.DataStoreId)
                    .ToDictionary(
                        g => g.Key,
                        g =>
                            g.Select(rc => new DataStoreContextItem(
                                    rc.Id,
                                    rc.DataStoreId,
                                    rc.ContextKey,
                                    rc.ContextValue
                                ))
                                .ToList()
                    ),
                _ => new Dictionary<int, List<DataStoreContextItem>>(),
            };

            var derivativesResult = await derivativeRepository.GetDataStoreDerivativesByDataStoreIds(
                dataStoreIds
            );
            var derivativesByDataStoreId = derivativesResult switch
            {
                DataStoreDerivativeQueryByDataStoreIdsResult.Success success => success
                    .DataStoreDerivativeResponses.GroupBy(d => d.DataStoreId)
                    .ToDictionary(
                        g => g.Key,
                        g =>
                            g.Select(d => new DataStoreDerivativeItem(
                                    d.Id,
                                    d.DataStoreId,
                                    d.DerivativeType,
                                    d.ConnectionString
                                ))
                                .ToList()
                    ),
                _ => new Dictionary<int, List<DataStoreDerivativeItem>>(),
            };

            // Awaited row by row here, inside the try, so a read failure reaches the failure arm
            // rather than escaping while the response is serialized.
            List<DataStoreResponse> dataStores = [];
            foreach (var row in dataStoreList)
            {
                dataStores.Add(
                    new DataStoreResponse
                    {
                        Id = row.Id,
                        DataStoreType = row.DataStoreType,
                        Name = row.Name,
                        Provider = row.Provider,
                        ConnectionString = await connectionStringReader.ReadAsync(
                            row.ConnectionString,
                            new ConnectionStringRow.DataStore(row.Id)
                        ),
                        DataStoreContexts = contextsByDataStoreId.GetValueOrDefault(row.Id, []),
                        DataStoreDerivatives = derivativesByDataStoreId.GetValueOrDefault(row.Id, []),
                        TenantId = row.TenantId,
                    }
                );
            }
            return new DataStoreQueryResult.Success(dataStores);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Query DataStore failure");
            return new DataStoreQueryResult.FailureUnknown(ex.Message);
        }
    }

    public async Task<DataStoreGetResult> GetDataStore(int id)
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        try
        {
            var sql = $"""
                SELECT "Id", "DataStoreType", "Name", "Provider", "ConnectionString", "TenantId"
                FROM "dmscs"."DataStore"
                WHERE "Id" = @Id AND {TenantContext.TenantWhereClause()};
                """;

            var result = await connection.QuerySingleOrDefaultAsync<(
                int Id,
                string DataStoreType,
                string Name,
                string? Provider,
                byte[]? ConnectionString,
                long? TenantId
            )?>(sql, new { Id = id, TenantId });
            if (result is null)
            {
                return new DataStoreGetResult.FailureNotFound();
            }

            var contextResult = await contextRepository.GetDataStoreContextsByDataStore(id);
            var contexts = contextResult switch
            {
                DataStoreContextQueryByDataStoreResult.Success success =>
                    success.DataStoreContextResponses.Select(rc => new DataStoreContextItem(
                        rc.Id,
                        rc.DataStoreId,
                        rc.ContextKey,
                        rc.ContextValue
                    )),
                _ => [],
            };

            var derivativesResult = await derivativeRepository.GetDataStoreDerivativesByDataStore(id);
            var derivatives = derivativesResult switch
            {
                DataStoreDerivativeQueryByDataStoreResult.Success success =>
                    success.DataStoreDerivativeResponses.Select(d => new DataStoreDerivativeItem(
                        d.Id,
                        d.DataStoreId,
                        d.DerivativeType,
                        d.ConnectionString
                    )),
                _ => [],
            };

            var dataStore = new DataStoreResponse
            {
                Id = result.Value.Id,
                DataStoreType = result.Value.DataStoreType,
                Name = result.Value.Name,
                Provider = result.Value.Provider,
                ConnectionString = await connectionStringReader.ReadAsync(
                    result.Value.ConnectionString,
                    new ConnectionStringRow.DataStore(result.Value.Id)
                ),
                DataStoreContexts = contexts,
                DataStoreDerivatives = derivatives,
                TenantId = result.Value.TenantId,
            };
            return new DataStoreGetResult.Success(dataStore);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Get DataStore failure");
            return new DataStoreGetResult.FailureUnknown(ex.Message);
        }
    }

    // The tenant gate precedes every mutable read and is held with the row lock through commit.
    // Publication/unlinking must use the same gate so a current link cannot appear after this guard.
    private async Task<bool> LockDataStore(int id, DbTransaction transaction)
    {
        DbConnection connection = GetTransactionConnection(transaction, CancellationToken.None);
        await DataStoreCatalog.AcquireTenantGateAsync(transaction, TenantId, CancellationToken.None);
        return await connection.QuerySingleOrDefaultAsync<int?>(
            $"""SELECT "Id" FROM dmscs."DataStore" WHERE "Id" = @Id AND {TenantContext.TenantWhereClause()} FOR UPDATE;""",
            new { Id = id, TenantId },
            transaction
        )
            is not null;
    }

    private Task<int?> FindManagedLink(int id, DbTransaction transaction) =>
        GetTransactionConnection(transaction, CancellationToken.None)
            .QuerySingleOrDefaultAsync<int?>(
                $"""SELECT "Id" FROM dmscs."DataStoreManage" WHERE "DataStoreId" = @Id AND {TenantContext.TenantWhereClause()};""",
                new { Id = id, TenantId },
                transaction
            );

    public async Task<DataStoreUpdateResult> UpdateDataStore(DataStoreUpdateCommand command)
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        try
        {
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            if (!await LockDataStore(command.Id, transaction))
            {
                return new DataStoreUpdateResult.FailureNotExists();
            }
            if (await FindManagedLink(command.Id, transaction) is int manageId)
            {
                return new DataStoreUpdateResult.FailureManaged(manageId);
            }
            if (
                await DataStoreCatalog.HasManagedNameReservationAsync(
                    transaction,
                    TenantId,
                    command.Name,
                    command.Id,
                    CancellationToken.None
                )
            )
            {
                return new DataStoreUpdateResult.FailureDuplicateName();
            }

            bool preserveConnectionString = ConnectionStringWrite.PreservesExistingValue(
                command.ConnectionString
            );

            // Left out of the SET clause when the caller provided no connection string, so the row
            // keeps the cipher text a reader can still decrypt.
            string connectionStringAssignment = preserveConnectionString
                ? string.Empty
                : "\"ConnectionString\" = @ConnectionString, ";

            var sql = $"""
                UPDATE "dmscs"."DataStore"
                SET "DataStoreType" = @DataStoreType, "Name" = @Name,
                    "Provider" = COALESCE(@Provider, "Provider"), {connectionStringAssignment}
                    "LastModifiedAt" = @LastModifiedAt, "ModifiedBy" = @ModifiedBy
                WHERE "Id" = @Id AND {TenantContext.TenantWhereClause()};
                """;

            var parameters = new DynamicParameters(
                new
                {
                    command.Id,
                    command.DataStoreType,
                    command.Name,
                    command.Provider,
                    LastModifiedAt = auditContext.GetCurrentTimestamp(),
                    ModifiedBy = auditContext.GetCurrentUser(),
                    TenantId,
                }
            );

            if (!preserveConnectionString)
            {
                // Typed explicitly: a null byte[] carries no type the provider can infer.
                parameters.Add(
                    "ConnectionString",
                    encryptionService.Encrypt(command.ConnectionString),
                    DbType.Binary
                );
            }

            var affectedRows = await connection.ExecuteAsync(sql, parameters, transaction);
            if (affectedRows == 0)
            {
                return new DataStoreUpdateResult.FailureNotExists();
            }
            await transaction.CommitAsync();
            return new DataStoreUpdateResult.Success();
        }
        catch (PostgresException ex)
            when (ex.SqlState == PostgresErrorCodes.UniqueViolation
                && ex.ConstraintName == DuplicateNameConstraint
            )
        {
            logger.LogWarning(ex, "Data store name must be unique within the tenant");
            return new DataStoreUpdateResult.FailureDuplicateName();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Update DataStore failure");
            return new DataStoreUpdateResult.FailureUnknown(ex.Message);
        }
    }

    public async Task<DataStoreDeleteResult> DeleteDataStore(int id)
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        try
        {
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            if (!await LockDataStore(id, transaction))
            {
                return new DataStoreDeleteResult.FailureNotExists();
            }
            if (await FindManagedLink(id, transaction) is int manageId)
            {
                return new DataStoreDeleteResult.FailureManaged(manageId);
            }
            int affectedRows = await DeleteDataStoreRows(id, transaction, CancellationToken.None);
            if (affectedRows > 0)
            {
                await transaction.CommitAsync();
                return new DataStoreDeleteResult.Success();
            }
            return new DataStoreDeleteResult.FailureNotExists();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Delete DataStore failure");
            return new DataStoreDeleteResult.FailureUnknown(ex.Message);
        }
    }

    public async Task<DataStoreIdsExistResult> GetExistingDataStoreIds(int[] ids)
    {
        if (ids.Length == 0)
        {
            return new DataStoreIdsExistResult.Success([]);
        }

        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        try
        {
            var sql = $"""
                SELECT "Id"
                FROM "dmscs"."DataStore"
                WHERE "Id" = ANY(@Ids) AND {TenantContext.TenantWhereClause()};
                """;

            var existingIds = await connection.QueryAsync<int>(sql, new { Ids = ids, TenantId });
            return new DataStoreIdsExistResult.Success([.. existingIds]);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Get existing DataStore IDs failure");
            return new DataStoreIdsExistResult.FailureUnknown(ex.Message);
        }
    }

    public async Task<ApplicationByDataStoreQueryResult> QueryApplicationByDataStore(
        int dataStoreId,
        PagingQuery query
    )
    {
        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        try
        {
            var sql = $"""
                SELECT application.*, aeo."EducationOrganizationId", acds."DataStoreId"
                FROM (
                    SELECT DISTINCT a."Id", a."ApplicationName", a."ClaimSetName", a."VendorId",
                        -- Enabled: application is enabled only if ALL its ApiClients linked to this data store are approved
                        (SELECT COALESCE(BOOL_AND(ac2."IsApproved"), true)
                         FROM "dmscs"."ApiClient" ac2
                         JOIN "dmscs"."ApiClientDataStore" acds2 ON acds2."ApiClientId" = ac2."Id"
                         WHERE ac2."ApplicationId" = a."Id" AND acds2."DataStoreId" = @DataStoreId) AS "Enabled"
                    FROM "dmscs"."ApiClientDataStore" acds
                    JOIN "dmscs"."ApiClient" ac ON ac."Id" = acds."ApiClientId"
                    JOIN "dmscs"."Application" a ON a."Id" = ac."ApplicationId"
                    JOIN "dmscs"."DataStore" ds ON ds."Id" = acds."DataStoreId"
                    WHERE acds."DataStoreId" = @DataStoreId AND {TenantContext.TenantWhereClause(
                    tableAlias: "ds"
                )}
                    ORDER BY a."Id" LIMIT @Limit OFFSET @Offset
                ) application
                LEFT JOIN "dmscs"."ApplicationEducationOrganization" aeo ON aeo."ApplicationId" = application."Id"
                JOIN "dmscs"."ApiClient" ac ON ac."ApplicationId" = application."Id"
                JOIN "dmscs"."ApiClientDataStore" acds ON acds."ApiClientId" = ac."Id"
                JOIN "dmscs"."DataStore" ds ON ds."Id" = acds."DataStoreId"
                WHERE {TenantContext.TenantWhereClause(tableAlias: "ds")}
                """;

            var parameters = new
            {
                dataStoreId,
                query.Limit,
                query.Offset,
                TenantId,
            };

            var rows = await connection.QueryAsync<(
                int Id,
                string ApplicationName,
                string ClaimSetName,
                int VendorId,
                bool Enabled,
                long? EducationOrganizationId,
                int DataStoreId
            )>(sql, parameters);

            var applications = rows.GroupBy(row => row.Id)
                .Select(group =>
                {
                    var application = group.First();
                    return new ApplicationResponse()
                    {
                        Id = application.Id,
                        ApplicationName = application.ApplicationName,
                        ClaimSetName = application.ClaimSetName,
                        VendorId = application.VendorId,
                        Enabled = application.Enabled,
                        EducationOrganizationIds = group
                            .Where(row => row.EducationOrganizationId is not null)
                            .Select(row => row.EducationOrganizationId!.Value)
                            .Distinct()
                            .ToList(),
                        DataStoreIds = group.Select(row => row.DataStoreId).Distinct().ToList(),
                    };
                })
                .ToList();

            if (applications.Count > 0)
            {
                var applicationIds = applications.Select(a => a.Id).ToArray();
                string sqlProfiles = """
                        SELECT
                            ap."ApplicationId",
                            ap."ProfileId"
                        FROM "dmscs"."ApplicationProfile" ap
                        WHERE ap."ApplicationId" = ANY(@ApplicationIds);
                    """;

                var profileRows = await connection.QueryAsync<(int ApplicationId, int ProfileId)>(
                    sqlProfiles,
                    new { ApplicationIds = applicationIds }
                );

                foreach (var profileRow in profileRows)
                {
                    var app = applications.Find(a => a.Id == profileRow.ApplicationId);
                    if (app is not null && !app.ProfileIds.Contains(profileRow.ProfileId))
                    {
                        app.ProfileIds.Add(profileRow.ProfileId);
                    }
                }
            }

            if (applications.Count == 0)
            {
                var dataStoreExists = await connection.ExecuteScalarAsync<bool>(
                    $"SELECT EXISTS(SELECT 1 FROM \"dmscs\".\"DataStore\" WHERE \"Id\" = @dataStoreId AND {TenantContext.TenantWhereClause()})",
                    new { dataStoreId, TenantId }
                );

                if (!dataStoreExists)
                {
                    return new ApplicationByDataStoreQueryResult.FailureNotExists();
                }
            }

            return new ApplicationByDataStoreQueryResult.Success(applications);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Query ApplicationByDataStore failure");
            return new ApplicationByDataStoreQueryResult.FailureUnknown(ex.Message);
        }
    }
}
