// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

namespace EdFi.DmsConfigurationService.Backend.ManagedDataStores;

/// <summary>Shared provider row mapping. Never an API projection; restore validates stored lifecycle state.</summary>
public sealed class ManagedDataStoreRow
{
    public int Id { get; set; }
    public long? TenantId { get; set; }
    public string Name { get; set; } = "";
    public string Template { get; set; } = "";
    public string Status { get; set; } = "";
    public string TargetDatabaseName { get; set; } = "";
    public Guid ExpectedSourceIdentity { get; set; }
    public string EndpointDescriptor { get; set; } = "";
    public string CreateJobId { get; set; } = "";
    public string? DeleteJobId { get; set; }
    public int? DataStoreId { get; set; }
    public int? HistoricalDataStoreId { get; set; }
    public string? HistoricalDataStoreName { get; set; }
    public int LastAttempt { get; set; }
    public string? LastFailureCategory { get; set; }
    public string? LastFailureProvisioningCode { get; set; }
    public string? LastFailureJobCode { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }
    public DateTime LastRefreshed { get; set; }
    public DateTime LastModifiedDate { get; set; }
    public int? PinnedContractVersion { get; set; }
    public string? PinnedProvider { get; set; }
    public string? PinnedPackageId { get; set; }
    public string? PinnedPackageVersion { get; set; }
    public string? PinnedPackageSha256 { get; set; }
    public string? PinnedArtifactSha256 { get; set; }
    public string? PinnedTemplateKind { get; set; }
    public string? PinnedDataStandardVersion { get; set; }
    public string? PinnedEffectiveSchemaHash { get; set; }
    public string? PinnedInventorySha256 { get; set; }
    public string? PinnedApiSchemaFormatVersion { get; set; }
    public string? PinnedRelationalMappingVersion { get; set; }

    public ManagedDataStore Restore(ProtectedDatabaseNames protectedNames)
    {
        if (
            !DmsDatabaseTemplate.TryFromWireValue(Template, out var template)
            || DmsTargetDatabaseName.TryCreate(TargetDatabaseName, protectedNames, out var target)
                != DmsTargetNameValidation.Valid
        )
        {
            throw new ArgumentException("Invalid persisted managed target.");
        }
        DmsProvisioningFailureCode? failureCode = LastFailureProvisioningCode is null
            ? null
            : Enum.Parse<DmsProvisioningFailureCode>(LastFailureProvisioningCode);
        return ManagedDataStore.Rehydrate(
            new(
                Id,
                TenantId,
                Name,
                template,
                target,
                ExpectedSourceIdentity,
                ManagedDataStoreEndpoint.FromCanonicalEncoding(EndpointDescriptor),
                PinnedContractVersion is { } version
                    ? new DmsTrustedArtifactPin
                    {
                        ContractVersion = version,
                        Provider = PinnedProvider!,
                        PackageId = PinnedPackageId!,
                        PackageVersion = PinnedPackageVersion!,
                        PackageSha256 = PinnedPackageSha256!,
                        ArtifactSha256 = PinnedArtifactSha256!,
                        TemplateKind = PinnedTemplateKind!,
                        DataStandardVersion = PinnedDataStandardVersion!,
                        EffectiveSchemaHash = PinnedEffectiveSchemaHash!,
                        InventorySha256 = PinnedInventorySha256!,
                        ApiSchemaFormatVersion = PinnedApiSchemaFormatVersion!,
                        RelationalMappingVersion = PinnedRelationalMappingVersion!,
                    }
                    : null,
                Enum.Parse<ManagedDataStoreStatus>(Status),
                DataStoreId,
                HistoricalDataStoreId,
                HistoricalDataStoreName,
                CreateJobId,
                DeleteJobId,
                LastAttempt,
                LastFailureCategory is null
                    ? null
                    : ManagedDataStoreFailure.Rehydrate(
                        Enum.Parse<DmsProvisioningFailureCategory>(LastFailureCategory),
                        failureCode,
                        LastFailureJobCode
                    ),
                Utc(CreatedAt),
                Utc(UpdatedAt),
                DeletedAt is { } deleted ? Utc(deleted) : null,
                Utc(LastRefreshed),
                Utc(LastModifiedDate)
            )
        );
    }

    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
