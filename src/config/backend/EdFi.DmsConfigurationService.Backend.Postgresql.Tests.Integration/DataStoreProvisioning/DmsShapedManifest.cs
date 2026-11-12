// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Manifest;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration.DataStoreProvisioning;

/// <summary>
/// The restore manifest a template built from the committed DMS-shaped fixture would carry: the fixture's
/// effective-schema row, the live engine major, the PostgreSQL baseline, and an inventory hash computed
/// from the facts under test (so a validator run proves every other rule, not the hash).
/// </summary>
internal static class DmsShapedManifest
{
    /// <summary>The <c>dms.DataStoreIdentity.SourceIdentity</c> the fixture inserts.</summary>
    public static readonly Guid SourceIdentity = Guid.Parse("6f1c9a52-2d3e-4b7a-9c8d-1e2f3a4b5c6d");

    public static readonly string EffectiveSchemaHash = string.Concat(Enumerable.Repeat("ab", 32));

    public const int ResourceKeyCount = 42;

    public static readonly string ResourceKeySeedHashB64 = Convert.ToBase64String(
        Enumerable.Range(1, 32).Select(value => (byte)value).ToArray()
    );

    public const string ApiSchemaFormatVersion = "1.0.0";

    public static RestoreManifest For(DmsTargetFacts facts, string templateKind, params string[] projects)
    {
        if (
            !DmsTargetValidator.TryBuildInventory(
                facts,
                out CanonicalInventory? inventory,
                out string? defect
            )
        )
        {
            throw new InvalidOperationException($"The facts do not form a canonical inventory: {defect}");
        }

        return new RestoreManifest
        {
            Version = RestoreManifest.SupportedVersion,
            PackageId = "EdFi.Api.Minimal.Template.PostgreSql.5.2.0",
            PackageVersion = "1.0.123",
            DatabaseEngine = RestoreManifest.PostgresqlEngine,
            TemplateKind = templateKind,
            DataStandardVersion = "5.2.0",
            ContentProfile = RestoreManifest.ContentProfileDmsDatastoreOnly,
            Projects = projects,
            ApiSchemaFormatVersion = ApiSchemaFormatVersion,
            EffectiveSchemaHash = EffectiveSchemaHash,
            ResourceKeyCount = ResourceKeyCount,
            ResourceKeySeedHashB64 = ResourceKeySeedHashB64,
            RelationalMappingVersion = "v3",
            EngineVersion = $"{facts.EngineMajorVersion}.0",
            DatabaseCompatibilityLevel = null,
            DocumentJsonColumnType = RestoreManifest.PostgresqlDocumentJsonColumnType,
            Inventory = inventory,
            InventorySha256 = CanonicalInventoryHash.Compute(inventory),
            ArtifactFileName = "EdFi.Api.Minimal.Template.PostgreSql.5.2.0.sql",
            ArtifactSha256 = string.Concat(Enumerable.Repeat("cd", 32)),
        };
    }
}
