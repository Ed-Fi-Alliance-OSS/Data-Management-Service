// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Manifest;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Manifest;

/// <summary>
/// The complete valid version-1 sample of the delivered Pester suite
/// (<c>eng/DatabaseTemplates/tests/Template-RestoreCore.Tests.ps1</c>, <c>New-ValidRestoreManifest</c> and
/// <c>New-TestInventory</c>), as mutable JSON so each shape test starts from a passing baseline and changes
/// exactly one thing. The inventory hash and canonical text were computed by the delivered PowerShell module
/// over that inventory, so accepting the baseline is itself a parity check.
/// </summary>
internal static class RestoreManifestSample
{
    /// <summary><c>Get-CanonicalInventoryHash</c> over <see cref="Inventory"/>.</summary>
    public const string InventorySha256 = "c6566d4f7ba0f4bd00318bdda6295eee47f6ef66d4d42c99a9b3455bbec00a2e";

    /// <summary><c>ConvertTo-CanonicalInventoryJson</c> over <see cref="Inventory"/>.</summary>
    public const string CanonicalInventoryText =
        "{\"schemas\":[{\"schemaName\":\"dms\",\"objects\":[{\"name\":\"GetMaxChangeVersion\",\"type\":\"function\"},{\"name\":\"Document\",\"type\":\"table\"},{\"name\":\"EffectiveSchema\",\"type\":\"table\"}]},{\"schemaName\":\"edfi\",\"objects\":[{\"name\":\"AcademicWeek\",\"type\":\"table\"},{\"name\":\"School\",\"type\":\"table\"}]}],\"principals\":[]}";

    public const string PostgresqlPackageId = "EdFi.Api.Minimal.Template.PostgreSql.5.2.0";

    public const string MssqlPackageId = "EdFi.Api.Minimal.Template.MsSql.5.2.0";

    public static readonly string EffectiveSchemaHash = string.Concat(Enumerable.Repeat("ab", 32));

    public static readonly string ArtifactSha256 = string.Concat(Enumerable.Repeat("cd", 32));

    public static readonly string ResourceKeySeedHashB64 = Convert.ToBase64String(
        Enumerable.Range(1, 32).Select(value => (byte)value).ToArray()
    );

    /// <summary>The Pester sample inventory: deliberately unsorted, with one uppercase type.</summary>
    public static JsonObject Inventory() =>
        new()
        {
            ["schemas"] = new JsonArray(
                new JsonObject
                {
                    ["schemaName"] = "edfi",
                    ["objects"] = new JsonArray(Entry("School", "table"), Entry("AcademicWeek", "table")),
                },
                new JsonObject
                {
                    ["schemaName"] = "dms",
                    ["objects"] = new JsonArray(
                        Entry("GetMaxChangeVersion", "function"),
                        Entry("Document", "TABLE"),
                        Entry("EffectiveSchema", "table")
                    ),
                }
            ),
            ["principals"] = new JsonArray(),
        };

    public static JsonObject Entry(string name, string type) => new() { ["name"] = name, ["type"] = type };

    public static JsonObject PostgresqlManifest() =>
        new()
        {
            ["version"] = 1,
            ["packageId"] = PostgresqlPackageId,
            ["packageVersion"] = "1.0.123",
            ["databaseEngine"] = "postgresql",
            ["templateKind"] = "Minimal",
            ["dataStandardVersion"] = "5.2.0",
            ["contentProfile"] = "DmsDatastoreOnly",
            ["projects"] = new JsonArray("edfi", "tpdm"),
            ["apiSchemaFormatVersion"] = "1.0.0",
            ["effectiveSchemaHash"] = EffectiveSchemaHash,
            ["resourceKeyCount"] = 42,
            ["resourceKeySeedHashB64"] = ResourceKeySeedHashB64,
            ["relationalMappingVersion"] = "v2",
            ["engineVersion"] = "16.8",
            ["documentJsonColumnType"] = "jsonb",
            ["inventory"] = Inventory(),
            ["inventorySha256"] = InventorySha256,
            ["artifactFileName"] = $"{PostgresqlPackageId}.sql",
            ["artifactSha256"] = ArtifactSha256,
        };

    public static JsonObject MssqlManifest()
    {
        JsonObject manifest = PostgresqlManifest();
        manifest["packageId"] = MssqlPackageId;
        manifest["databaseEngine"] = "mssql";
        manifest["engineVersion"] = "17.0.900.7";
        manifest["databaseCompatibilityLevel"] = 170;
        manifest["documentJsonColumnType"] = "nvarchar(max)";
        manifest["artifactFileName"] = $"{MssqlPackageId}.bak";
        return manifest;
    }

    public static string ToJson(JsonNode node) => node.ToJsonString();

    public static RestoreManifest Read(JsonNode node) => RestoreManifestReader.Read(ToJson(node));

    /// <summary>Applies <paramref name="mutate"/> to a fresh PostgreSQL sample and returns the message of the <see cref="RestoreManifestException"/> reading it throws, or null when it is accepted.</summary>
    public static string? RejectionOf(Action<JsonObject> mutate, bool mssql = false)
    {
        JsonObject manifest = mssql ? MssqlManifest() : PostgresqlManifest();
        mutate(manifest);
        return RejectionOf(ToJson(manifest));
    }

    public static string? RejectionOf(string json)
    {
        try
        {
            RestoreManifestReader.Read(json);
            return null;
        }
        catch (RestoreManifestException exception)
        {
            return exception.Message;
        }
    }

    public static JsonElement ParseStrictly(string json)
    {
        // The inventory reader requires a strictly parsed element; cloning keeps it usable after disposal.
        using JsonDocument document = JsonDocument.Parse(
            json,
            new JsonDocumentOptions { AllowDuplicateProperties = false }
        );
        return document.RootElement.Clone();
    }
}
