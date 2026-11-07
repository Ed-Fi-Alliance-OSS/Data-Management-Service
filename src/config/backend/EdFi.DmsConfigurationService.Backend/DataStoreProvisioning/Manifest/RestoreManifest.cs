// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Manifest;

/// <summary>
/// A version-1 restore manifest of the delivered template package contract (F-P7), as
/// <see cref="RestoreManifestReader"/> accepted it: every field present with the required JSON kind and value
/// shape, hashes in lowercase 64-hex form, the fixed <see cref="ContentProfileDmsDatastoreOnly"/> profile,
/// engine-consistent artifact extension and compatibility-level rules, and an inventory whose recomputed
/// canonical hash equals <see cref="InventorySha256"/>. String values are kept exactly as written; the
/// inventory is held in canonical form. The type defines no structural equality: the pin
/// (<see cref="DmsTrustedArtifactPin"/>) carries the fields that identify an artifact.
/// </summary>
public sealed class RestoreManifest
{
    public const int SupportedVersion = 1;

    /// <summary>The fixed entry name of the manifest inside a template package.</summary>
    public const string FileName = "restore-manifest.json";

    /// <summary>The only content profile eligible for restore.</summary>
    public const string ContentProfileDmsDatastoreOnly = "DmsDatastoreOnly";

    public const string PostgresqlEngine = "postgresql";

    public const string MssqlEngine = "mssql";

    public const string MinimalTemplateKind = "Minimal";

    public const string PopulatedTemplateKind = "Populated";

    /// <summary>The lowest SQL Server compatibility level a manifest may declare.</summary>
    public const int MinimumMssqlCompatibilityLevel = 90;

    /// <summary>The physical type every <c>DocumentJson</c> column has on PostgreSQL (<c>Get-RestoreDocumentJsonBaselineType</c>).</summary>
    public const string PostgresqlDocumentJsonColumnType = "jsonb";

    /// <summary>The physical type every <c>DocumentJson</c> column has on SQL Server, with its declared length.</summary>
    public const string MssqlDocumentJsonColumnType = "nvarchar(max)";

    public required int Version { get; init; }

    public required string PackageId { get; init; }

    public required string PackageVersion { get; init; }

    /// <summary><see cref="PostgresqlEngine"/> or <see cref="MssqlEngine"/>, case-sensitive.</summary>
    public required string DatabaseEngine { get; init; }

    /// <summary><see cref="MinimalTemplateKind"/> or <see cref="PopulatedTemplateKind"/>, case-sensitive.</summary>
    public required string TemplateKind { get; init; }

    public required string DataStandardVersion { get; init; }

    public required string ContentProfile { get; init; }

    /// <summary>The project schema names, in document order; non-empty and unique ignoring case.</summary>
    public required IReadOnlyList<string> Projects { get; init; }

    public required string ApiSchemaFormatVersion { get; init; }

    /// <summary>Lowercase hex SHA-256 the template's <c>dms.EffectiveSchema</c> row must carry.</summary>
    public required string EffectiveSchemaHash { get; init; }

    public required int ResourceKeyCount { get; init; }

    /// <summary>Base64 of the 32-byte resource key seed hash, as written.</summary>
    public required string ResourceKeySeedHashB64 { get; init; }

    public required string RelationalMappingVersion { get; init; }

    public required string EngineVersion { get; init; }

    /// <summary>The SQL Server compatibility level; present only when <see cref="DatabaseEngine"/> is <see cref="MssqlEngine"/>.</summary>
    public required int? DatabaseCompatibilityLevel { get; init; }

    public required string DocumentJsonColumnType { get; init; }

    public required CanonicalInventory Inventory { get; init; }

    /// <summary>Lowercase hex SHA-256 of the canonical inventory JSON, equal to <see cref="CanonicalInventoryHash.Compute"/> over <see cref="Inventory"/>.</summary>
    public required string InventorySha256 { get; init; }

    /// <summary>The package entry name of the database artifact: letters, digits, dots, dashes, and underscores only, with the engine's extension.</summary>
    public required string ArtifactFileName { get; init; }

    /// <summary>Lowercase hex SHA-256 of the artifact bytes.</summary>
    public required string ArtifactSha256 { get; init; }

    /// <summary>The artifact file extension a manifest for <paramref name="databaseEngine"/> must name: <c>.bak</c> for SQL Server, <c>.sql</c> for PostgreSQL.</summary>
    public static string ArtifactExtensionFor(string databaseEngine) =>
        string.Equals(databaseEngine, MssqlEngine, StringComparison.Ordinal) ? ".bak" : ".sql";

    /// <summary>The <c>documentJsonColumnType</c> a manifest for <paramref name="databaseEngine"/> must declare (F-P10 baseline).</summary>
    public static string DocumentJsonColumnTypeFor(string databaseEngine) =>
        string.Equals(databaseEngine, MssqlEngine, StringComparison.Ordinal)
            ? MssqlDocumentJsonColumnType
            : PostgresqlDocumentJsonColumnType;
}
