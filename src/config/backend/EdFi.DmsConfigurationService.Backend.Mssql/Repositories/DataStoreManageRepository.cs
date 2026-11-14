// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.ManagedDataStores;
using EdFi.DmsConfigurationService.Backend.Mssql.Jobs;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Repositories;

public sealed class DataStoreManageRepository(
    IOptions<DatabaseOptions> databaseOptions,
    ITenantContextProvider tenantContextProvider,
    IDmsDataStoreProvisionerReadiness readiness,
    IJobEnqueuer enqueuer,
    ProtectedDatabaseNames protectedNames
) : IDataStoreManageRepository
{
    public async Task<DataStoreManageCreateResult> AcceptCreateAsync(
        ManagedDataStoreCreateCommand command,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();
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
}
