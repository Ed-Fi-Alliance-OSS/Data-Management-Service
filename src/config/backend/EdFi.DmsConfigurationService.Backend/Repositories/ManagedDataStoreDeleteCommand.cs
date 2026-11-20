// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

namespace EdFi.DmsConfigurationService.Backend.Repositories;

/// <summary>Exact recorded operation and publication identity; contains no runtime credentials.</summary>
public sealed record ManagedDataStoreDeleteCommand(
    int Id,
    ManagedDataStoreCreateCommand Identity,
    DmsTrustedArtifactPin PinnedArtifact,
    int HistoricalDataStoreId,
    string CreateJobId
)
{
    public bool IsValid(ProtectedDatabaseNames protectedNames) =>
        Id > 0
        && HistoricalDataStoreId > 0
        && !string.IsNullOrWhiteSpace(CreateJobId)
        && Identity is not null
        && Identity.IsValid(protectedNames)
        && PinnedArtifact is { } pin
        && pin.ContractVersion == DmsTrustedArtifactPin.CurrentContractVersion
        && pin.Provider == Identity.Endpoint.Provider
        && pin.TemplateKind == Identity.Template.TemplateKind
        && !Array.Exists(
            new[]
            {
                pin.PackageId,
                pin.PackageVersion,
                pin.PackageSha256,
                pin.ArtifactSha256,
                pin.DataStandardVersion,
                pin.EffectiveSchemaHash,
                pin.InventorySha256,
                pin.ApiSchemaFormatVersion,
                pin.RelationalMappingVersion,
            },
            string.IsNullOrWhiteSpace
        );
}
