// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

/// <summary>
/// The pinned identity of the trusted artifact a managed data store is created from (D-8).
/// <see cref="IDmsDataStoreProvisioner.ResolveArtifactAsync"/> returns it before any server contact;
/// DMS-1439 persists every field; <see cref="IDmsDataStoreProvisioner.CreateAsync"/> and every retry must
/// present it, and the provisioner requires field-by-field equality with the artifact it resolves again.
/// Record equality is that comparison. The pin carries no path, feed, server, or connection detail.
/// </summary>
public sealed record DmsTrustedArtifactPin
{
    public const int CurrentContractVersion = 1;

    public required int ContractVersion { get; init; }

    /// <summary><c>postgresql</c> or <c>mssql</c> (D-17).</summary>
    public required string Provider { get; init; }

    public required string PackageId { get; init; }

    public required string PackageVersion { get; init; }

    /// <summary>Lowercase hex SHA-256 of the nupkg bytes the attestation was verified against.</summary>
    public required string PackageSha256 { get; init; }

    /// <summary>The manifest's <c>artifactSha256</c>, re-verified while extracting.</summary>
    public required string ArtifactSha256 { get; init; }

    /// <summary><c>Minimal</c> or <c>Populated</c>.</summary>
    public required string TemplateKind { get; init; }

    public required string DataStandardVersion { get; init; }

    public required string EffectiveSchemaHash { get; init; }

    public required string InventorySha256 { get; init; }

    public required string ApiSchemaFormatVersion { get; init; }

    public required string RelationalMappingVersion { get; init; }
}
