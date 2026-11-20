// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using Dapper;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.ManagedDataStores;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Repositories;

public sealed partial class DataStoreManageRepository
{
    // Provider-internal admission: callers must hold the fence and gate and verify the persisted
    // identity/state before constructing this value. It is never a public reservation-bypass option.
    internal sealed record DeletionAdmission(int DataStoreId, long? TenantId, bool AlreadyAbsent);

    public async Task CompleteDeleteAsync(
        ManagedDataStoreDeleteCommand command,
        JobExecutionContext execution,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(execution);
        cancellationToken.ThrowIfCancellationRequested();
        long? tenantId = tenantContextProvider.Context is TenantContext.Multitenant tenant
            ? tenant.TenantId
            : null;
        long? executionTenantId = execution.TenantContext is TenantContext.Multitenant jobTenant
            ? jobTenant.TenantId
            : null;
        if (
            !command.IsValid(protectedNames)
            || tenantId != executionTenantId
            || string.IsNullOrWhiteSpace(execution.JobId)
            || execution.Attempt <= 0
        )
        {
            throw new ArgumentException("Invalid managed deletion identity or execution partition.");
        }

        // The supplied fence owns the connection, transaction, lease validation, rollback and commit.
        // Never call the ordinary repository's connection-owning public delete here.
        await execution.Fence.ExecuteAsync(
            async (transaction, token) =>
            {
                await DataStoreCatalog.AcquireTenantGateAsync(transaction, tenantId, token);
                var identity = command.Identity;
                var pin = command.PinnedArtifact;
                DynamicParameters parameters = new(
                    new
                    {
                        command.Id,
                        TenantId = tenantId,
                        identity.ExpectedSourceIdentity,
                        PinnedContractVersion = pin.ContractVersion,
                    }
                );
                foreach (
                    var (name, value) in new (string, string)[]
                    {
                        ("Name", identity.Name),
                        ("Template", identity.Template.WireValue),
                        ("TargetDatabaseName", identity.TargetName.Value),
                        ("EndpointDescriptor", identity.Endpoint.CanonicalEncoding),
                        ("CreateJobId", command.CreateJobId),
                        ("DeleteJobId", execution.JobId),
                        ("PinnedProvider", pin.Provider),
                        ("PinnedPackageId", pin.PackageId),
                        ("PinnedPackageVersion", pin.PackageVersion),
                        ("PinnedPackageSha256", pin.PackageSha256),
                        ("PinnedArtifactSha256", pin.ArtifactSha256),
                        ("PinnedTemplateKind", pin.TemplateKind),
                        ("PinnedDataStandardVersion", pin.DataStandardVersion),
                        ("PinnedEffectiveSchemaHash", pin.EffectiveSchemaHash),
                        ("PinnedInventorySha256", pin.InventorySha256),
                        ("PinnedApiSchemaFormatVersion", pin.ApiSchemaFormatVersion),
                        ("PinnedRelationalMappingVersion", pin.RelationalMappingVersion),
                    }
                )
                {
                    parameters.Add(name, value, DbType.String, size: -1);
                }

                var row = await transaction.Connection!.QuerySingleOrDefaultAsync<(
                    string? Status,
                    int? DataStoreId,
                    int? HistoricalDataStoreId,
                    string? HistoricalDataStoreName,
                    int LastAttempt,
                    DateTime? DeletedAt
                )>(
                    new CommandDefinition(
                        """
                        SELECT "Status", "DataStoreId", "HistoricalDataStoreId", "HistoricalDataStoreName", "LastAttempt", "DeletedAt"
                        FROM dmscs."DataStoreManage"
                        WHERE "Id" = @Id AND ("TenantId" = @TenantId OR ("TenantId" IS NULL AND @TenantId IS NULL))
                            AND "ExpectedSourceIdentity" = @ExpectedSourceIdentity AND "PinnedContractVersion" = @PinnedContractVersion
                            AND "LastAttempt" > 0
                            AND "LastFailureCategory" IS NULL AND "LastFailureProvisioningCode" IS NULL AND "LastFailureJobCode" IS NULL
                            AND "Name" = @Name COLLATE "C"
                            AND "Template" = @Template COLLATE "C"
                            AND "TargetDatabaseName" = @TargetDatabaseName COLLATE "C"
                            AND "EndpointDescriptor" = @EndpointDescriptor COLLATE "C"
                            AND "CreateJobId" = @CreateJobId COLLATE "C"
                            AND "DeleteJobId" = @DeleteJobId COLLATE "C"
                            AND "PinnedProvider" = @PinnedProvider COLLATE "C"
                            AND "PinnedPackageId" = @PinnedPackageId COLLATE "C"
                            AND "PinnedPackageVersion" = @PinnedPackageVersion COLLATE "C"
                            AND "PinnedPackageSha256" = @PinnedPackageSha256 COLLATE "C"
                            AND "PinnedArtifactSha256" = @PinnedArtifactSha256 COLLATE "C"
                            AND "PinnedTemplateKind" = @PinnedTemplateKind COLLATE "C"
                            AND "PinnedDataStandardVersion" = @PinnedDataStandardVersion COLLATE "C"
                            AND "PinnedEffectiveSchemaHash" = @PinnedEffectiveSchemaHash COLLATE "C"
                            AND "PinnedInventorySha256" = @PinnedInventorySha256 COLLATE "C"
                            AND "PinnedApiSchemaFormatVersion" = @PinnedApiSchemaFormatVersion COLLATE "C"
                            AND "PinnedRelationalMappingVersion" = @PinnedRelationalMappingVersion COLLATE "C" FOR UPDATE;
                        """,
                        parameters,
                        transaction,
                        cancellationToken: token
                    )
                );
                if (
                    row.Status is not ("DeleteInProgress" or "Deleted")
                    || row.HistoricalDataStoreId != command.HistoricalDataStoreId
                    || row.HistoricalDataStoreName != identity.Name
                )
                {
                    throw new InvalidOperationException(
                        "The persisted managed deletion identity or state has changed."
                    );
                }
                if (row.Status == "Deleted")
                {
                    if (row.DataStoreId is not null || row.DeletedAt is null)
                    {
                        throw new InvalidOperationException(
                            "The managed deletion tombstone is inconsistent."
                        );
                    }
                    // An exact recorded-operation replay never looks up or removes a reused catalog name.
                    return;
                }
                if (row.LastAttempt != execution.Attempt || row.DeletedAt is not null)
                {
                    throw new InvalidOperationException("The managed deletion attempt has changed.");
                }

                // Lock only the recorded ID, then check its partition as well as its publication identity.
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
                        FROM dmscs."DataStore"
                        WHERE "Id" = @HistoricalDataStoreId FOR UPDATE;
                        """,
                        new { command.HistoricalDataStoreId },
                        transaction,
                        cancellationToken: token
                    )
                );
                bool absent = ordinary.Id is null;
                if (
                    absent
                        ? row.DataStoreId is not null
                        : row.DataStoreId != command.HistoricalDataStoreId
                            || ordinary.TenantId != tenantId
                            || ordinary.Name != identity.Name
                            || ordinary.DataStoreType != "Shared"
                            || ordinary.Provider
                                != ManagedDataStoreEndpoint.CatalogProvider(identity.Endpoint.Provider)
                )
                {
                    throw new InvalidOperationException("The managed deletion catalog link is inconsistent.");
                }
                await transaction.Connection!.ExecuteAsync(
                    new CommandDefinition(
                        """
                        UPDATE dmscs."DataStoreManage" SET "DataStoreId" = NULL
                        WHERE "Id" = @Id AND ("TenantId" = @TenantId OR ("TenantId" IS NULL AND @TenantId IS NULL));
                        """,
                        parameters,
                        transaction,
                        cancellationToken: token
                    )
                );
                await dataStoreRepository.DeleteManagedDataStore(
                    new DeletionAdmission(command.HistoricalDataStoreId, tenantId, absent),
                    transaction,
                    token
                );
                int updated = await transaction.Connection!.ExecuteAsync(
                    new CommandDefinition(
                        """

                        UPDATE dmscs."DataStoreManage"
                        SET "Status" = 'Deleted', "DeletedAt" = sample."Now",
                            "UpdatedAt" = sample."Now", "LastModifiedDate" = sample."Now", "LastRefreshed" = sample."Now"
                        FROM (SELECT clock_timestamp() AT TIME ZONE 'UTC' AS "Now") AS sample
                        WHERE "Id" = @Id AND ("TenantId" = @TenantId OR ("TenantId" IS NULL AND @TenantId IS NULL))
                            AND "Status" = 'DeleteInProgress' AND "DataStoreId" IS NULL;
                        """,
                        parameters,
                        transaction,
                        cancellationToken: token
                    )
                );
                if (updated != 1)
                {
                    throw new InvalidOperationException("Managed deletion was not saved.");
                }
            },
            cancellationToken
        );
    }
}
