// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using Dapper;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.ManagedDataStores;
using EdFi.DmsConfigurationService.Backend.Mssql.Jobs;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.DataModel.Model.DataStoreManage;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Repositories;

public sealed partial class DataStoreManageRepository(
    IOptions<DatabaseOptions> databaseOptions,
    ITenantContextProvider tenantContextProvider,
    IDmsDataStoreProvisionerReadiness readiness,
    IJobEnqueuer enqueuer,
    ProtectedDatabaseNames protectedNames,
    DataStoreRepository dataStoreRepository,
    JobClaimPolicy claimPolicy
) : IDataStoreManageRepository
{
    public async Task<DataStoreManageGetResult> GetByIdAsync(int id, CancellationToken cancellationToken) =>
        await QueryAsync(new DataStoreManageQuery { Id = id, Limit = 1 }, cancellationToken) switch
        {
            DataStoreManageQueryResult.Success { DataStores.Count: 0 } =>
                new DataStoreManageGetResult.NotFound(),
            DataStoreManageQueryResult.Success success => new DataStoreManageGetResult.Success(
                success.DataStores[0]
            ),
            DataStoreManageQueryResult.FailureUnknown failure => new DataStoreManageGetResult.FailureUnknown(
                failure.Diagnostic
            ),
            _ => throw new InvalidOperationException("The managed by-ID query must be valid."),
        };

    public async Task<DataStoreManageQueryResult> QueryAsync(
        DataStoreManageQuery query,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        if (!new DataStoreManageQuery.Validator().Validate(query).IsValid)
        {
            return new DataStoreManageQueryResult.InvalidRequest();
        }
        // PostgreSQL text cannot carry NUL; it cannot match any valid managed name on either provider.
        if (query.Name?.Contains('\0') == true)
        {
            return new DataStoreManageQueryResult.Success([]);
        }

        // Only the nine public fields can select SQL expressions; no caller text enters the SQL.
        string column = query.OrderBy switch
        {
            "id" => "Id",
            "name" => "Name",
            "dataStoreId" => "HistoricalDataStoreId",
            "dataStoreName" => "HistoricalDataStoreName",
            "status" => "Status",
            "databaseTemplate" => "Template",
            "databaseName" => "CASE WHEN HistoricalDataStoreId IS NULL THEN NULL ELSE TargetDatabaseName END",
            "lastRefreshed" => "LastRefreshed",
            "lastModifiedDate" => "LastModifiedDate",
            _ => throw new InvalidOperationException("The managed sort field must be valid."),
        };
        string sort = query.OrderBy
            is "name"
                or "dataStoreName"
                or "status"
                or "databaseTemplate"
                or "databaseName"
            ? $"CONVERT(varbinary(max), CONVERT(varchar(max), TRANSLATE({column} COLLATE Latin1_General_100_BIN2, 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz')))"
            : column;
        string direction = query.Direction == "Ascending" ? "ASC" : "DESC";
        string tieBreaker = query.OrderBy == "id" ? "" : $", Id {direction}";
        TenantContext tenant = tenantContextProvider.Context;
        string filters = query.Id.HasValue ? " AND Id = @Id" : "";
        if (query.Name is not null)
        {
            filters +=
                " AND NameComparisonKey = CONVERT(binary(4), DATALENGTH(@Name)) + CONVERT(varbinary(max), @Name)";
        }
        string sql = $"""
            SELECT Id, Name, HistoricalDataStoreId, HistoricalDataStoreName, Status, Template,
                CASE WHEN HistoricalDataStoreId IS NULL THEN NULL ELSE TargetDatabaseName END, LastRefreshed, LastModifiedDate
            FROM dmscs.DataStoreManage
            WHERE {tenant.TenantWhereClause()}{filters}
            ORDER BY CASE WHEN {column} IS NULL THEN 1 ELSE 0 END, {sort} {direction}{tieBreaker}
            OFFSET @Offset ROWS FETCH NEXT @Limit ROWS ONLY;
            """;
        var parameters = new DynamicParameters(
            new
            {
                query.Id,
                query.Offset,
                query.Limit,
                TenantId = tenant is TenantContext.Multitenant selected ? (long?)selected.TenantId : null,
            }
        );
        // Unbounded filters must never be truncated into a matching stored-name prefix.
        parameters.Add(
            "Name",
            query.Name is null ? null : CatalogName.Canonicalize(query.Name),
            DbType.String,
            size: -1
        );
        try
        {
            await using SqlConnection connection = new(databaseOptions.Value.DatabaseConnection);
            await connection.OpenAsync(cancellationToken);
            var rows = await connection.QueryAsync<(
                int Id,
                string Name,
                int? DataStoreId,
                string? DataStoreName,
                string Status,
                string DatabaseTemplate,
                string? DatabaseName,
                DateTime LastRefreshed,
                DateTime LastModifiedDate
            )>(new CommandDefinition(sql, parameters, cancellationToken: cancellationToken));
            // TIMESTAMP / DATETIME2 stores UTC without an offset. Never interpret it in the host time zone.
            return new DataStoreManageQueryResult.Success(
                rows.Select(row => new DataStoreManageResponse(
                        row.Id,
                        row.Name,
                        row.DataStoreId,
                        row.DataStoreName,
                        row.Status,
                        row.DatabaseTemplate,
                        row.DatabaseName,
                        new DateTimeOffset(DateTime.SpecifyKind(row.LastRefreshed, DateTimeKind.Utc)),
                        new DateTimeOffset(DateTime.SpecifyKind(row.LastModifiedDate, DateTimeKind.Utc))
                    ))
                    .ToList()
            );
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return new DataStoreManageQueryResult.FailureUnknown(
                MssqlJobDiagnostics.From(exception, "QueryManagedDataStores")
            );
        }
    }

    public async Task<DataStoreManageCreateResult> AcceptCreateAsync(
        ManagedDataStoreCreateCommand command,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();
        if (claimPolicy.ExcludesManagedDataStores)
        {
            return new DataStoreManageCreateResult.ManagementDisabled();
        }
        if (!command.IsValid(protectedNames))
        {
            return new DataStoreManageCreateResult.InvalidRequest();
        }
        if (readiness.Current is not DmsProvisionerReadiness.Ready ready)
        {
            return new DataStoreManageCreateResult.ProvisioningUnavailable();
        }
        if (ready.Provider != command.Endpoint.Provider)
        {
            return new DataStoreManageCreateResult.InvalidRequest();
        }
        long? tenantId = tenantContextProvider.Context is TenantContext.Multitenant tenant
            ? tenant.TenantId
            : null;
        try
        {
            await using SqlConnection connection = new(databaseOptions.Value.DatabaseConnection);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            if (
                await DataStoreCatalog.HasNameReservationAsync(
                    transaction,
                    tenantId,
                    command.Name,
                    cancellationToken
                )
            )
            {
                return new DataStoreManageCreateResult.NameReserved();
            }
            if (
                await DataStoreCatalog.HasTargetReservationAsync(
                    transaction,
                    tenantId,
                    command.Endpoint,
                    command.TargetName,
                    cancellationToken
                )
            )
            {
                return new DataStoreManageCreateResult.TargetReserved();
            }
            // Recheck the singleton after lock acquisition and immediately before the first write.
            if (readiness.Current is not DmsProvisionerReadiness.Ready current)
            {
                return new DataStoreManageCreateResult.ProvisioningUnavailable();
            }
            if (current.Provider != command.Endpoint.Provider)
            {
                return new DataStoreManageCreateResult.InvalidRequest();
            }
            int id = await connection.ExecuteScalarAsync<int>(
                new CommandDefinition(
                    """
                    DECLARE @AcceptedAt datetime2(7) = SYSUTCDATETIME();
                    INSERT INTO dmscs."DataStoreManage" (
                        "TenantId", "Name", "Template", "Status", "TargetDatabaseName", "ExpectedSourceIdentity",
                        "EndpointDescriptor", "CreateJobId", "CreatedAt", "UpdatedAt", "LastRefreshed", "LastModifiedDate")
                    OUTPUT INSERTED.Id
                    SELECT @TenantId, @Name, @Template, 'PendingCreate', @Target, @Source,
                        @Endpoint, 'pending-enqueue', @AcceptedAt, @AcceptedAt, @AcceptedAt, @AcceptedAt;
                    """,
                    new
                    {
                        TenantId = tenantId,
                        command.Name,
                        Template = command.Template.WireValue,
                        Target = command.TargetName.Value,
                        Source = command.ExpectedSourceIdentity,
                        Endpoint = command.Endpoint.CanonicalEncoding,
                    },
                    transaction,
                    cancellationToken: cancellationToken
                )
            );

            var payload = (JobPayloadWriteResult.Success)
                JobPayloadSerializer.Serialize(new CreateManagedDataStorePayload(id));
            JobEnqueueResult enqueued = await enqueuer.EnqueueAsync(
                new(ManagedJobTypes.Create, ManagedJobTypes.PayloadVersion, payload.Json),
                transaction,
                cancellationToken
            );
            if (enqueued is not JobEnqueueResult.Success success)
            {
                return new DataStoreManageCreateResult.EnqueueFailed(enqueued);
            }
            // CreateJobId is non-nullable and the enqueuer owns ID generation. This placeholder exists
            // only inside this transaction; no path commits until exactly one row has its actual job ID.
            int updated = await connection.ExecuteAsync(
                new CommandDefinition(
                    """UPDATE dmscs."DataStoreManage" SET "CreateJobId" = @JobId WHERE "Id" = @Id AND ("TenantId" = @TenantId OR ("TenantId" IS NULL AND @TenantId IS NULL)) AND "CreateJobId" = 'pending-enqueue';""",
                    new
                    {
                        success.JobId,
                        Id = id,
                        TenantId = tenantId,
                    },
                    transaction,
                    cancellationToken: cancellationToken
                )
            );
            if (updated != 1)
            {
                throw new InvalidOperationException("Managed create job linkage was not saved.");
            }
            await transaction.CommitAsync(cancellationToken);
            return new DataStoreManageCreateResult.Success(id, success.JobId);
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Disposal rolls back failures before commit. A failed commit acknowledgement may be
            // ambiguous, so this operation never retries or generates another aggregate identity.
            return new DataStoreManageCreateResult.FailureUnknown(
                MssqlJobDiagnostics.From(exception, "AcceptManagedDataStoreCreate")
            );
        }
    }

    public async Task<DataStoreManageDeleteResult> AcceptDeleteAsync(
        int id,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (claimPolicy.ExcludesManagedDataStores)
        {
            return new DataStoreManageDeleteResult.ManagementDisabled();
        }
        long? tenantId = tenantContextProvider.Context is TenantContext.Multitenant tenant
            ? tenant.TenantId
            : null;
        try
        {
            await using SqlConnection connection = new(databaseOptions.Value.DatabaseConnection);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await DataStoreCatalog.AcquireTenantGateAsync(transaction, tenantId, cancellationToken);
            var parameters = new { Id = id, TenantId = tenantId };
            var row = await connection.QuerySingleOrDefaultAsync<(string? Status, int? DataStoreId)>(
                new CommandDefinition(
                    """
                    SELECT "Status", "DataStoreId" FROM dmscs."DataStoreManage" WITH (UPDLOCK, HOLDLOCK)
                    WHERE "Id" = @Id AND ("TenantId" = @TenantId OR ("TenantId" IS NULL AND @TenantId IS NULL));
                    """,
                    parameters,
                    transaction,
                    cancellationToken: cancellationToken
                )
            );
            if (row.Status is null or "Deleted")
            {
                return new DataStoreManageDeleteResult.NotFound();
            }
            // Only known wire names become caller-visible states, never arbitrary persisted text.
            if (
                !Enum.TryParse(row.Status, out ManagedDataStoreStatus status)
                || !Enum.IsDefined(status)
                || status.ToString() != row.Status
            )
            {
                throw new InvalidOperationException("The managed lifecycle status is invalid.");
            }
            if (status != ManagedDataStoreStatus.Created)
            {
                return new DataStoreManageDeleteResult.InvalidState(status);
            }
            // The FK alone does not establish tenant ownership. Lock order is gate, aggregate, ordinary row.
            int? ordinaryId = await connection.QuerySingleOrDefaultAsync<int?>(
                new CommandDefinition(
                    """
                    SELECT "Id" FROM dmscs."DataStore" WITH (UPDLOCK, HOLDLOCK)
                    WHERE "Id" = @DataStoreId AND ("TenantId" = @TenantId OR ("TenantId" IS NULL AND @TenantId IS NULL));
                    """,
                    new { row.DataStoreId, TenantId = tenantId },
                    transaction,
                    cancellationToken: cancellationToken
                )
            );
            if (ordinaryId is null)
            {
                throw new InvalidOperationException("The current managed data store link is invalid.");
            }
            // State/tenant outcomes take precedence. Check the latched gate immediately before writing.
            if (readiness.Current is not DmsProvisionerReadiness.Ready)
            {
                return new DataStoreManageDeleteResult.ProvisioningUnavailable();
            }
            int updated = await connection.ExecuteAsync(
                new CommandDefinition(
                    """
                    DECLARE @AcceptedAt datetime2(7) = SYSUTCDATETIME();
                    UPDATE dmscs."DataStoreManage"
                    SET "Status" = 'PendingDelete', "LastAttempt" = 0,
                        "LastFailureCategory" = NULL, "LastFailureProvisioningCode" = NULL, "LastFailureJobCode" = NULL,
                        "UpdatedAt" = @AcceptedAt, "LastModifiedDate" = @AcceptedAt
                    WHERE "Id" = @Id AND ("TenantId" = @TenantId OR ("TenantId" IS NULL AND @TenantId IS NULL))
                        AND "Status" = 'Created' AND "DeleteJobId" IS NULL;
                    """,
                    parameters,
                    transaction,
                    cancellationToken: cancellationToken
                )
            );
            if (updated != 1)
            {
                throw new InvalidOperationException("Managed delete acceptance was not saved.");
            }
            var payload = (JobPayloadWriteResult.Success)
                JobPayloadSerializer.Serialize(new DeleteManagedDataStorePayload(id));
            JobEnqueueResult enqueued = await enqueuer.EnqueueAsync(
                new(ManagedJobTypes.Delete, ManagedJobTypes.PayloadVersion, payload.Json),
                transaction,
                cancellationToken
            );
            if (enqueued is not JobEnqueueResult.Success success)
            {
                return new DataStoreManageDeleteResult.EnqueueFailed(enqueued);
            }
            updated = await connection.ExecuteAsync(
                new CommandDefinition(
                    """
                    UPDATE dmscs."DataStoreManage" SET "DeleteJobId" = @JobId
                    WHERE "Id" = @Id AND ("TenantId" = @TenantId OR ("TenantId" IS NULL AND @TenantId IS NULL))
                        AND "Status" = 'PendingDelete' AND "DeleteJobId" IS NULL;
                    """,
                    new
                    {
                        success.JobId,
                        Id = id,
                        TenantId = tenantId,
                    },
                    transaction,
                    cancellationToken: cancellationToken
                )
            );
            if (updated != 1)
            {
                throw new InvalidOperationException("Managed delete job linkage was not saved.");
            }
            await transaction.CommitAsync(cancellationToken);
            return new DataStoreManageDeleteResult.Success(success.JobId);
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Disposal rolls back every non-success. An ambiguous commit acknowledgement is never retried.
            return new DataStoreManageDeleteResult.FailureUnknown(
                MssqlJobDiagnostics.From(exception, "AcceptManagedDataStoreDelete")
            );
        }
    }
}
