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

namespace EdFi.DmsConfigurationService.Backend.Mssql.Repositories;

public sealed partial class DataStoreManageRepository
{
    // Provider-internal admission: callers must hold the fence and gate and verify the persisted
    // identity/state before constructing this value. It is never a public reservation-bypass option.
    internal sealed record PublicationAdmission(int Id, long? TenantId, string Name, string Provider);

    public async Task<int> PublishCreateAsync(
        ManagedDataStorePublishCommand command,
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
            throw new ArgumentException("Invalid managed publication identity or execution partition.");
        }

        int publishedId = 0;
        // The supplied fence owns the connection, transaction, lease validation, rollback and commit.
        // Never call the ordinary repository's connection-owning public insert here.
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
                        ("CreateJobId", execution.JobId),
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
                    // These two persisted tokens are varchar; the other identity/pin fields are nvarchar.
                    parameters.Add(
                        name,
                        value,
                        name is "Template" or "TargetDatabaseName" ? DbType.AnsiString : DbType.String,
                        size: -1
                    );
                }

                var row = await transaction.Connection!.QuerySingleOrDefaultAsync<(
                    string? Status,
                    int? DataStoreId,
                    int? HistoricalDataStoreId,
                    string? HistoricalDataStoreName,
                    int LastAttempt
                )>(
                    new CommandDefinition(
                        """
                        SELECT "Status", "DataStoreId", "HistoricalDataStoreId", "HistoricalDataStoreName", "LastAttempt"
                        FROM dmscs."DataStoreManage" WITH (UPDLOCK, HOLDLOCK)
                        WHERE "Id" = @Id AND ("TenantId" = @TenantId OR ("TenantId" IS NULL AND @TenantId IS NULL))
                            AND "ExpectedSourceIdentity" = @ExpectedSourceIdentity AND "PinnedContractVersion" = @PinnedContractVersion
                            AND "DeleteJobId" IS NULL AND "DeletedAt" IS NULL AND "LastAttempt" > 0
                            AND "LastFailureCategory" IS NULL AND "LastFailureProvisioningCode" IS NULL AND "LastFailureJobCode" IS NULL
                            AND CONVERT(varbinary(max), "Name") = CONVERT(varbinary(max), @Name) AND DATALENGTH("Name") = DATALENGTH(@Name)
                            AND CONVERT(varbinary(max), "Template") = CONVERT(varbinary(max), @Template) AND DATALENGTH("Template") = DATALENGTH(@Template)
                            AND CONVERT(varbinary(max), "TargetDatabaseName") = CONVERT(varbinary(max), @TargetDatabaseName) AND DATALENGTH("TargetDatabaseName") = DATALENGTH(@TargetDatabaseName)
                            AND CONVERT(varbinary(max), "EndpointDescriptor") = CONVERT(varbinary(max), @EndpointDescriptor) AND DATALENGTH("EndpointDescriptor") = DATALENGTH(@EndpointDescriptor)
                            AND CONVERT(varbinary(max), "CreateJobId") = CONVERT(varbinary(max), @CreateJobId) AND DATALENGTH("CreateJobId") = DATALENGTH(@CreateJobId)
                            AND CONVERT(varbinary(max), "PinnedProvider") = CONVERT(varbinary(max), @PinnedProvider) AND DATALENGTH("PinnedProvider") = DATALENGTH(@PinnedProvider)
                            AND CONVERT(varbinary(max), "PinnedPackageId") = CONVERT(varbinary(max), @PinnedPackageId) AND DATALENGTH("PinnedPackageId") = DATALENGTH(@PinnedPackageId)
                            AND CONVERT(varbinary(max), "PinnedPackageVersion") = CONVERT(varbinary(max), @PinnedPackageVersion) AND DATALENGTH("PinnedPackageVersion") = DATALENGTH(@PinnedPackageVersion)
                            AND CONVERT(varbinary(max), "PinnedPackageSha256") = CONVERT(varbinary(max), @PinnedPackageSha256) AND DATALENGTH("PinnedPackageSha256") = DATALENGTH(@PinnedPackageSha256)
                            AND CONVERT(varbinary(max), "PinnedArtifactSha256") = CONVERT(varbinary(max), @PinnedArtifactSha256) AND DATALENGTH("PinnedArtifactSha256") = DATALENGTH(@PinnedArtifactSha256)
                            AND CONVERT(varbinary(max), "PinnedTemplateKind") = CONVERT(varbinary(max), @PinnedTemplateKind) AND DATALENGTH("PinnedTemplateKind") = DATALENGTH(@PinnedTemplateKind)
                            AND CONVERT(varbinary(max), "PinnedDataStandardVersion") = CONVERT(varbinary(max), @PinnedDataStandardVersion) AND DATALENGTH("PinnedDataStandardVersion") = DATALENGTH(@PinnedDataStandardVersion)
                            AND CONVERT(varbinary(max), "PinnedEffectiveSchemaHash") = CONVERT(varbinary(max), @PinnedEffectiveSchemaHash) AND DATALENGTH("PinnedEffectiveSchemaHash") = DATALENGTH(@PinnedEffectiveSchemaHash)
                            AND CONVERT(varbinary(max), "PinnedInventorySha256") = CONVERT(varbinary(max), @PinnedInventorySha256) AND DATALENGTH("PinnedInventorySha256") = DATALENGTH(@PinnedInventorySha256)
                            AND CONVERT(varbinary(max), "PinnedApiSchemaFormatVersion") = CONVERT(varbinary(max), @PinnedApiSchemaFormatVersion) AND DATALENGTH("PinnedApiSchemaFormatVersion") = DATALENGTH(@PinnedApiSchemaFormatVersion)
                            AND CONVERT(varbinary(max), "PinnedRelationalMappingVersion") = CONVERT(varbinary(max), @PinnedRelationalMappingVersion) AND DATALENGTH("PinnedRelationalMappingVersion") = DATALENGTH(@PinnedRelationalMappingVersion);
                        """,
                        parameters,
                        transaction,
                        cancellationToken: token
                    )
                );
                if (row.Status is not ("CreateInProgress" or "Created"))
                {
                    throw new InvalidOperationException(
                        "The persisted managed publication identity or state has changed."
                    );
                }

                string provider = ManagedDataStoreEndpoint.CatalogProvider(identity.Endpoint.Provider);
                if (row.Status == "Created")
                {
                    // Recovery uses the durable link, never a same-name lookup or the caller's runtime secret.
                    var linked = await transaction.Connection!.QuerySingleOrDefaultAsync<(
                        int? Id,
                        string? Name,
                        string? DataStoreType,
                        string? Provider
                    )>(
                        new CommandDefinition(
                            """
                            SELECT "Id", "Name", "DataStoreType", "Provider"
                            FROM dmscs."DataStore" WITH (UPDLOCK, HOLDLOCK)
                            WHERE "Id" = @DataStoreId AND ("TenantId" = @TenantId OR ("TenantId" IS NULL AND @TenantId IS NULL));
                            """,
                            new { row.DataStoreId, TenantId = tenantId },
                            transaction,
                            cancellationToken: token
                        )
                    );
                    if (
                        linked.Id is null
                        || linked.Id != row.HistoricalDataStoreId
                        || row.HistoricalDataStoreName != identity.Name
                        || linked.Name != identity.Name
                        || linked.DataStoreType != "Shared"
                        || linked.Provider != provider
                    )
                    {
                        throw new InvalidOperationException(
                            "The published ordinary data store link is inconsistent."
                        );
                    }
                    publishedId = linked.Id.Value;
                    return;
                }

                if (
                    row.LastAttempt != execution.Attempt
                    || row.DataStoreId is not null
                    || row.HistoricalDataStoreId is not null
                    || row.HistoricalDataStoreName is not null
                )
                {
                    throw new InvalidOperationException(
                        "An unpublished managed data store cannot have catalog links."
                    );
                }
                publishedId = await dataStoreRepository.InsertManagedDataStore(
                    new PublicationAdmission(command.Id, tenantId, identity.Name, provider),
                    command.RuntimeConnectionString,
                    transaction,
                    token
                );
                parameters.Add("DataStoreId", publishedId);
                int updated = await transaction.Connection!.ExecuteAsync(
                    new CommandDefinition(
                        """
                        DECLARE @Now datetime2(7) = SYSUTCDATETIME();
                        UPDATE dmscs."DataStoreManage"
                        SET "Status" = 'Created', "DataStoreId" = @DataStoreId,
                            "HistoricalDataStoreId" = @DataStoreId, "HistoricalDataStoreName" = @Name,
                            "UpdatedAt" = @Now, "LastModifiedDate" = @Now, "LastRefreshed" = @Now
                        WHERE "Id" = @Id AND ("TenantId" = @TenantId OR ("TenantId" IS NULL AND @TenantId IS NULL))
                            AND "Status" = 'CreateInProgress' AND "DataStoreId" IS NULL;
                        """,
                        parameters,
                        transaction,
                        cancellationToken: token
                    )
                );
                if (updated != 1)
                {
                    throw new InvalidOperationException("Managed publication was not saved.");
                }
            },
            cancellationToken
        );
        return publishedId;
    }
}
