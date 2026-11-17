// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using System.Text.Json;
using Dapper;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.ManagedDataStores;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Repositories;

public sealed partial class DataStoreManageRepository
{
    public Task<ManagedDataStoreCreateOperation> StartCreateAsync(
        int id,
        JobExecutionContext execution,
        CancellationToken cancellationToken
    ) =>
        RunCreateFenceAsync(
            id,
            execution,
            async (row, transaction, token) =>
            {
                if (row.Status == ManagedDataStoreStatus.Created)
                {
                    var linked = await transaction.Connection!.QuerySingleOrDefaultAsync<(
                        int? Id,
                        string? Name,
                        string? DataStoreType,
                        string? Provider
                    )>(
                        new CommandDefinition(
                            """
                            SELECT "Id", "Name", "DataStoreType", "Provider" FROM dmscs."DataStore"
                            WHERE "Id" = @DataStoreId AND ("TenantId" = @TenantId OR ("TenantId" IS NULL AND @TenantId IS NULL)) FOR UPDATE;
                            """,
                            new { row.DataStoreId, row.TenantId },
                            transaction,
                            cancellationToken: token
                        )
                    );
                    if (
                        linked.Id != row.HistoricalDataStoreId
                        || linked.Id is null
                        || linked.Name != row.Name
                        || linked.DataStoreType != "Shared"
                        || linked.Provider != ManagedDataStoreEndpoint.CatalogProvider(row.Endpoint.Provider)
                    )
                    {
                        throw new JobPermanentException(JobErrorCode.HandlerFailed);
                    }
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
                    ManagedDataStoreStatus.CreateInProgress,
                    new DateTimeOffset(DateTime.SpecifyKind(now, DateTimeKind.Utc)),
                    execution.Attempt
                );
                var parameters = new DynamicParameters(new { Id = id, Attempt = execution.Attempt });
                parameters.Add("Now", now, DbType.DateTime2);
                await transaction.Connection!.ExecuteAsync(
                    new CommandDefinition(
                        """
                        UPDATE dmscs."DataStoreManage" SET "Status" = 'CreateInProgress', "LastAttempt" = @Attempt,
                            "LastFailureCategory" = NULL, "LastFailureProvisioningCode" = NULL, "LastFailureJobCode" = NULL,
                            "UpdatedAt" = @Now, "LastModifiedDate" = @Now, "LastRefreshed" = @Now WHERE "Id" = @Id;
                        """,
                        parameters,
                        transaction,
                        cancellationToken: token
                    )
                );
            },
            cancellationToken,
            start: true
        );

    public Task<ManagedDataStoreCreateOperation> PinCreateAsync(
        ManagedDataStoreCreateOperation operation,
        DmsTrustedArtifactPin pin,
        JobExecutionContext execution,
        CancellationToken cancellationToken
    ) =>
        RunCreateFenceAsync(
            operation.Id,
            execution,
            async (row, transaction, token) =>
            {
                RequireCurrentCreate(row, operation, execution);
                if (row.PinnedArtifact is { } existing && existing != pin)
                {
                    throw new JobPermanentException(JobErrorCode.HandlerFailed);
                }
                // Validate every required pin field using the same boundary as publication.
                if (!operation.Identity.AcceptsPin(pin))
                {
                    throw new JobPermanentException(JobErrorCode.HandlerFailed);
                }
                row.PinArtifact(pin);
                var parameters = new DynamicParameters(new { row.Id, pin.ContractVersion });
                foreach (
                    var (name, value) in new (string, string)[]
                    {
                        ("Provider", pin.Provider),
                        ("PackageId", pin.PackageId),
                        ("PackageVersion", pin.PackageVersion),
                        ("PackageSha256", pin.PackageSha256),
                        ("ArtifactSha256", pin.ArtifactSha256),
                        ("TemplateKind", pin.TemplateKind),
                        ("DataStandardVersion", pin.DataStandardVersion),
                        ("EffectiveSchemaHash", pin.EffectiveSchemaHash),
                        ("InventorySha256", pin.InventorySha256),
                        ("ApiSchemaFormatVersion", pin.ApiSchemaFormatVersion),
                        ("RelationalMappingVersion", pin.RelationalMappingVersion),
                    }
                )
                {
                    parameters.Add(name, value, DbType.String, size: -1);
                }
                await transaction.Connection!.ExecuteAsync(
                    new CommandDefinition(
                        """
                        UPDATE dmscs."DataStoreManage" SET "PinnedContractVersion" = @ContractVersion,
                            "PinnedProvider" = @Provider, "PinnedPackageId" = @PackageId, "PinnedPackageVersion" = @PackageVersion,
                            "PinnedPackageSha256" = @PackageSha256, "PinnedArtifactSha256" = @ArtifactSha256,
                            "PinnedTemplateKind" = @TemplateKind, "PinnedDataStandardVersion" = @DataStandardVersion,
                            "PinnedEffectiveSchemaHash" = @EffectiveSchemaHash, "PinnedInventorySha256" = @InventorySha256,
                            "PinnedApiSchemaFormatVersion" = @ApiSchemaFormatVersion, "PinnedRelationalMappingVersion" = @RelationalMappingVersion
                        WHERE "Id" = @Id;
                        """,
                        parameters,
                        transaction,
                        cancellationToken: token
                    )
                );
            },
            cancellationToken
        );

    public async Task AdmitCreateAsync(
        ManagedDataStoreCreateOperation operation,
        ManagedDataStoreEndpoint actual,
        JobExecutionContext execution,
        CancellationToken cancellationToken
    ) =>
        _ = await RunCreateFenceAsync(
            operation.Id,
            execution,
            (row, _, _) =>
            {
                RequireCurrentCreate(row, operation, execution);
                if (
                    row.PinnedArtifact is null
                    || row.PinnedArtifact != operation.PinnedArtifact
                    || actual != row.Endpoint
                )
                {
                    throw new JobPermanentException(JobErrorCode.HandlerFailed);
                }
                return Task.CompletedTask;
            },
            cancellationToken
        );

    private static ManagedDataStoreCreateOperation CreateOperation(ManagedDataStore row) =>
        new(
            row.Id,
            new(row.Name, row.Template, row.TargetName, row.ExpectedSourceIdentity, row.Endpoint),
            row.PinnedArtifact,
            row.Status == ManagedDataStoreStatus.Created
        );

    private static void RequireCurrentCreate(
        ManagedDataStore row,
        ManagedDataStoreCreateOperation operation,
        JobExecutionContext execution
    )
    {
        if (
            row.Status != ManagedDataStoreStatus.CreateInProgress
            || row.LastAttempt != execution.Attempt
            || CreateOperation(row).Identity != operation.Identity
            || operation.AlreadyCreated
        )
        {
            throw new JobPermanentException(JobErrorCode.HandlerFailed);
        }
    }

    private async Task<ManagedDataStoreCreateOperation> RunCreateFenceAsync(
        int id,
        JobExecutionContext execution,
        Func<ManagedDataStore, DbTransaction, CancellationToken, Task> work,
        CancellationToken cancellationToken,
        bool start = false
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
        ManagedDataStoreCreateOperation result = null!;
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
                // DELETE admits only Created, so a delete of this exact create proves its publication committed
                // before its job completion was acknowledged. Starting again completes the create without
                // physical work or any write; the newer delete keeps its own state and attempts.
                bool publishedThenDeleteAccepted =
                    start
                    && stored is not null
                    && stored.CreateJobId == execution.JobId
                    && stored.DeleteJobId is not null
                    && stored.HistoricalDataStoreId is not null
                    && stored.Status
                        is "PendingDelete"
                            or "DeleteInProgress"
                            or "DeleteFailed"
                            or "DeleteError"
                            or "Deleted";
                if (
                    !publishedThenDeleteAccepted
                    && (
                        stored is null
                        || stored.CreateJobId != execution.JobId
                        || stored.DeleteJobId is not null
                        || stored.LastAttempt > execution.Attempt
                        || stored.Status
                            is not ("PendingCreate" or "CreateInProgress" or "CreateFailed" or "Created")
                    )
                )
                {
                    throw new JobPermanentException(JobErrorCode.HandlerFailed);
                }
                ManagedDataStore row;
                try
                {
                    row = stored!.Restore(protectedNames);
                    if (!CreateOperation(row).Identity.IsValid(protectedNames))
                    {
                        throw new ArgumentException("Invalid stored managed identity.");
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
                if (publishedThenDeleteAccepted)
                {
                    result = CreateOperation(row) with { AlreadyCreated = true };
                    return;
                }
                await work(row, transaction, token);
                result = CreateOperation(row);
            },
            cancellationToken
        );
        return result;
    }
}
