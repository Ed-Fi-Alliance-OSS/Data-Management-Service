// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using System.Text.Json;
using Dapper;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.ManagedDataStores;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Repositories;

public sealed partial class DataStoreManageRepository
{
    public Task<ManagedDataStoreDeleteOperation> StartDeleteAsync(
        int id,
        JobExecutionContext execution,
        CancellationToken cancellationToken
    ) =>
        RunDeleteFenceAsync(
            id,
            execution,
            async (row, transaction, token) =>
            {
                if (row.Status == ManagedDataStoreStatus.Deleted)
                {
                    // Rehydration verifies the tombstone. Do not inspect a reused ordinary name.
                    return;
                }
                DateTime now = await transaction.Connection!.ExecuteScalarAsync<DateTime>(
                    new CommandDefinition(
                        "SELECT clock_timestamp() AT TIME ZONE 'UTC';",
                        transaction: transaction,
                        cancellationToken: token
                    )
                );
                row.RecordAttempt(
                    ManagedDataStoreStatus.DeleteInProgress,
                    new DateTimeOffset(DateTime.SpecifyKind(now, DateTimeKind.Utc)),
                    execution.Attempt
                );
                var parameters = new DynamicParameters(new { Id = id, Attempt = execution.Attempt });
                parameters.Add("Now", now, DbType.DateTime2);
                await transaction.Connection!.ExecuteAsync(
                    new CommandDefinition(
                        """
                        UPDATE dmscs."DataStoreManage" SET "Status" = 'DeleteInProgress', "LastAttempt" = @Attempt,
                            "LastFailureCategory" = NULL, "LastFailureProvisioningCode" = NULL, "LastFailureJobCode" = NULL,
                            "UpdatedAt" = @Now, "LastModifiedDate" = @Now, "LastRefreshed" = @Now WHERE "Id" = @Id;
                        """,
                        parameters,
                        transaction,
                        cancellationToken: token
                    )
                );
            },
            cancellationToken
        );

    public async Task AdmitDeleteAsync(
        ManagedDataStoreDeleteOperation operation,
        ManagedDataStoreEndpoint actual,
        JobExecutionContext execution,
        CancellationToken cancellationToken
    ) =>
        _ = await RunDeleteFenceAsync(
            operation.Command.Id,
            execution,
            (row, _, _) =>
            {
                if (
                    operation.AlreadyDeleted
                    || row.Status != ManagedDataStoreStatus.DeleteInProgress
                    || row.LastAttempt != execution.Attempt
                    || DeleteOperation(row).Command != operation.Command
                    || row.Endpoint != actual
                )
                {
                    throw new JobPermanentException(JobErrorCode.HandlerFailed);
                }
                return Task.CompletedTask;
            },
            cancellationToken
        );

    private static ManagedDataStoreDeleteOperation DeleteOperation(ManagedDataStore row) =>
        new(
            new(
                row.Id,
                new(row.Name, row.Template, row.TargetName, row.ExpectedSourceIdentity, row.Endpoint),
                row.PinnedArtifact!,
                row.HistoricalDataStoreId!.Value,
                row.CreateJobId
            ),
            row.Status == ManagedDataStoreStatus.Deleted
        );

    private async Task<ManagedDataStoreDeleteOperation> RunDeleteFenceAsync(
        int id,
        JobExecutionContext execution,
        Func<ManagedDataStore, DbTransaction, CancellationToken, Task> work,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        long? tenantId = tenantContextProvider.Context is TenantContext.Multitenant tenant
            ? tenant.TenantId
            : null;
        long? executionTenant = execution.TenantContext is TenantContext.Multitenant jobTenant
            ? jobTenant.TenantId
            : null;
        if (
            id <= 0
            || tenantId != executionTenant
            || execution.Attempt <= 0
            || string.IsNullOrWhiteSpace(execution.JobId)
        )
        {
            throw new JobPermanentException(JobErrorCode.InvalidPayload);
        }
        ManagedDataStoreDeleteOperation result = null!;
        await execution.Fence.ExecuteAsync(
            async (transaction, token) =>
            {
                await DataStoreCatalog.AcquireTenantGateAsync(transaction, tenantId, token);
                var stored = await transaction.Connection!.QuerySingleOrDefaultAsync<ManagedDataStoreRow>(
                    new CommandDefinition(
                        """
                        SELECT * FROM dmscs."DataStoreManage"
                        WHERE "Id" = @Id AND ("TenantId" = @TenantId OR ("TenantId" IS NULL AND @TenantId IS NULL)) FOR UPDATE;
                        """,
                        new { Id = id, TenantId = tenantId },
                        transaction,
                        cancellationToken: token
                    )
                );
                if (
                    stored is null
                    || stored.DeleteJobId != execution.JobId
                    || stored.LastAttempt > execution.Attempt
                    || stored.Status
                        is not ("PendingDelete" or "DeleteInProgress" or "DeleteFailed" or "Deleted")
                )
                {
                    throw new JobPermanentException(JobErrorCode.HandlerFailed);
                }
                ManagedDataStore row;
                try
                {
                    row = stored.Restore(protectedNames);
                    if (!DeleteOperation(row).Command.IsValid(protectedNames))
                    {
                        throw new ArgumentException("Invalid stored managed deletion.");
                    }
                }
                catch (Exception exception)
                    when (exception
                            is ArgumentException
                                or JsonException
                                or InvalidOperationException
                                or FormatException
                    )
                {
                    throw new JobPermanentException(JobErrorCode.HandlerFailed);
                }
                if (row.Status != ManagedDataStoreStatus.Deleted)
                {
                    // Check only the recorded ID, before any physical action. An absent row is recoverable
                    // only with an absent current link; a different name or partition is never adopted.
                    var ordinary = await transaction.Connection!.QuerySingleOrDefaultAsync<(
                        int? Id,
                        long? TenantId,
                        string? Name,
                        string? DataStoreType,
                        string? Provider
                    )>(
                        new CommandDefinition(
                            """
                            SELECT "Id", "TenantId", "Name", "DataStoreType", "Provider"
                            FROM dmscs."DataStore" WHERE "Id" = @HistoricalDataStoreId FOR UPDATE;
                            """,
                            new { row.HistoricalDataStoreId },
                            transaction,
                            cancellationToken: token
                        )
                    );
                    if (
                        ordinary.Id is null
                            ? row.DataStoreId is not null
                            : row.DataStoreId != ordinary.Id
                                || ordinary.TenantId != tenantId
                                || ordinary.Name != row.Name
                                || ordinary.DataStoreType != "Shared"
                                || ordinary.Provider
                                    != ManagedDataStoreEndpoint.CatalogProvider(row.Endpoint.Provider)
                    )
                    {
                        throw new JobPermanentException(JobErrorCode.HandlerFailed);
                    }
                }
                await work(row, transaction, token);
                result = DeleteOperation(row);
            },
            cancellationToken
        );
        return result;
    }
}
