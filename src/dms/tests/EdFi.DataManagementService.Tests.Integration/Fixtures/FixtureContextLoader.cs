// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.Tests.Common;

namespace EdFi.DataManagementService.Tests.Integration.Fixtures;

/// <summary>
/// Resolves a <see cref="FixtureKey"/> to a fully populated <see cref="FixtureContext"/>.
/// The fixture's source ApiSchema files (named according to each fixture's manifest)
/// are materialized into a process-cached temp directory whose file names match the
/// <c>ApiSchema*.json</c> pattern the DMS host scans for when
/// <c>AppSettings:UseApiSchemaPath</c> is enabled. The materialized files are
/// augmented with neutral defaults for fields the DMS HTTP middleware expects on
/// every <c>ResourceSchema</c>/<c>projectSchema</c> but which the DDL-only fixtures
/// omit. Both the DMS host and the per-dialect baseline cache consume this same
/// materialized directory so the effective schema seen by the host matches the
/// schema the DDL pipeline runs against.
/// </summary>
internal static class FixtureContextLoader
{
    private static readonly ConcurrentDictionary<FixtureKey, Lazy<FixtureContext>> _cache = new();

    private static readonly JsonSerializerOptions _writeOptions = new() { WriteIndented = true };

    public static FixtureContext Load(FixtureKey key) =>
        _cache
            .GetOrAdd(
                key,
                k => new Lazy<FixtureContext>(
                    () => BuildContext(k),
                    LazyThreadSafetyMode.ExecutionAndPublication
                )
            )
            .Value;

    private static FixtureContext BuildContext(FixtureKey key)
    {
        string fixtureDirectory = FixtureRepositoryPaths.ResolveFixtureDirectory(key);

        IReadOnlyList<string> apiSchemaFiles = ReadApiSchemaFileList(fixtureDirectory);
        IReadOnlyList<string> sourceDialects = ReadDialectsList(fixtureDirectory);
        IReadOnlyList<string> resolvedSourcePaths = ResolveSourcePaths(fixtureDirectory, apiSchemaFiles);

        (string materializedDirectory, IReadOnlyList<string> materializedFilePaths) =
            MaterializeApiSchemaDirectory(key, resolvedSourcePaths, sourceDialects);

        IReadOnlyList<QualifiedResourceName> resources = ResourceEnumerator.FromApiSchemaFiles(
            materializedFilePaths
        );

        string profileXmlDirectory = FixturePathResolver.ResolveRepositoryRelativePath(
            AppContext.BaseDirectory,
            $"src/dms/tests/EdFi.DataManagementService.Tests.Integration/Fixtures/Profiles/{key}"
        );

        return new FixtureContext(key, materializedDirectory, profileXmlDirectory, resources);
    }

    private static IReadOnlyList<string> ReadApiSchemaFileList(string fixtureDirectory)
    {
        string manifestPath = Path.Combine(fixtureDirectory, "fixture.json");
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException($"Fixture manifest not found at '{manifestPath}'.", manifestPath);
        }

        using FileStream stream = File.OpenRead(manifestPath);
        using JsonDocument document = JsonDocument.Parse(stream);

        if (
            !document.RootElement.TryGetProperty("apiSchemaFiles", out JsonElement filesElement)
            || filesElement.ValueKind != JsonValueKind.Array
            || filesElement.GetArrayLength() == 0
        )
        {
            throw new InvalidOperationException(
                $"Fixture manifest '{manifestPath}' must declare at least one apiSchemaFiles entry."
            );
        }

        var files = new List<string>(filesElement.GetArrayLength());
        foreach (JsonElement entry in filesElement.EnumerateArray())
        {
            string? relativePath = entry.GetString();
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                throw new InvalidOperationException(
                    $"Fixture manifest '{manifestPath}' contains an empty apiSchemaFiles entry."
                );
            }
            files.Add(relativePath);
        }

        return files;
    }

    private static IReadOnlyList<string> ReadDialectsList(string fixtureDirectory)
    {
        string manifestPath = Path.Combine(fixtureDirectory, "fixture.json");
        using FileStream stream = File.OpenRead(manifestPath);
        using JsonDocument document = JsonDocument.Parse(stream);

        if (
            !document.RootElement.TryGetProperty("dialects", out JsonElement dialectsElement)
            || dialectsElement.ValueKind != JsonValueKind.Array
        )
        {
            return [];
        }

        var dialects = new List<string>(dialectsElement.GetArrayLength());
        foreach (JsonElement entry in dialectsElement.EnumerateArray())
        {
            string? dialect = entry.GetString();
            if (!string.IsNullOrWhiteSpace(dialect))
            {
                dialects.Add(dialect);
            }
        }
        return dialects;
    }

    private static IReadOnlyList<string> ResolveSourcePaths(
        string fixtureDirectory,
        IReadOnlyList<string> apiSchemaFiles
    )
    {
        var resolved = new List<string>(apiSchemaFiles.Count);
        foreach (string entry in apiSchemaFiles)
        {
            resolved.Add(FixturePathResolver.ResolveFixtureInputPath(fixtureDirectory, entry));
        }
        return resolved;
    }

    /// <summary>
    /// Reads each source ApiSchema file, augments it with neutral defaults for
    /// runtime-required fields the DDL-only fixtures omit, and writes the
    /// augmented document under <c>%TEMP%/dms-api-integration-fixtures/&lt;key&gt;/inputs</c>.
    /// A generated <c>fixture.json</c> manifest sits alongside the inputs so the
    /// per-dialect baseline cache can discover the augmented files using the same
    /// <c>EffectiveSchemaFixtureLoader</c> contract as the on-disk DDL fixtures.
    /// </summary>
    private static (
        string MaterializedDirectory,
        IReadOnlyList<string> MaterializedFilePaths
    ) MaterializeApiSchemaDirectory(
        FixtureKey key,
        IReadOnlyList<string> sourcePaths,
        IReadOnlyList<string> dialects
    )
    {
        string materializedDirectory = Path.Combine(
            Path.GetTempPath(),
            "dms-api-integration-fixtures",
            key.ToString()
        );

        if (Directory.Exists(materializedDirectory))
        {
            Directory.Delete(materializedDirectory, recursive: true);
        }
        Directory.CreateDirectory(materializedDirectory);

        string inputsDirectory = Path.Combine(materializedDirectory, "inputs");
        Directory.CreateDirectory(inputsDirectory);

        var seenTargetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var manifestEntries = new List<string>(sourcePaths.Count);
        var materializedFilePaths = new List<string>(sourcePaths.Count);

        foreach (string sourcePath in sourcePaths)
        {
            string targetName = BuildScannableFileName(Path.GetFileName(sourcePath), seenTargetNames);
            string targetPath = Path.Combine(inputsDirectory, targetName);

            string sourceJson = File.ReadAllText(sourcePath);
            JsonNode root =
                JsonNode.Parse(sourceJson)
                ?? throw new InvalidOperationException(
                    $"ApiSchema file '{sourcePath}' parsed to a null JSON document."
                );

            if (root is JsonObject rootObject)
            {
                AugmentForRuntime(rootObject);
            }

            File.WriteAllText(targetPath, root.ToJsonString(_writeOptions));
            manifestEntries.Add(targetName);
            materializedFilePaths.Add(targetPath);
        }

        WriteManifest(materializedDirectory, manifestEntries, dialects);

        return (materializedDirectory, materializedFilePaths);
    }

    /// <summary>
    /// Inserts neutral defaults onto the root ApiSchema document for fields the
    /// DMS runtime expects but the DDL-only fixtures omit. Pre-existing values
    /// are preserved as-is so fixtures that deliberately declare these fields are
    /// not silently overwritten.
    /// </summary>
    private static void AugmentForRuntime(JsonObject root)
    {
        if (root["projectSchema"] is not JsonObject projectSchema)
        {
            return;
        }

        AugmentProjectSchemaDefaults(projectSchema);

        if (projectSchema["resourceSchemas"] is JsonObject resourceSchemas)
        {
            foreach (KeyValuePair<string, JsonNode?> entry in resourceSchemas)
            {
                if (entry.Value is JsonObject resourceSchema)
                {
                    AugmentResourceSchemaDefaults(resourceSchema);
                }
            }
        }
    }

    private static void AugmentProjectSchemaDefaults(JsonObject projectSchema)
    {
        AddIfMissing(projectSchema, "resourceNameMapping", () => new JsonObject());
        AddIfMissing(projectSchema, "caseInsensitiveEndpointNameMapping", () => new JsonObject());
        AddIfMissing(projectSchema, "educationOrganizationHierarchy", () => new JsonObject());
        AddIfMissing(projectSchema, "educationOrganizationTypes", () => new JsonArray());
        AddIfMissing(projectSchema, "domains", () => new JsonArray());
        AddIfMissing(projectSchema, "description", () => JsonValue.Create(string.Empty));
    }

    private static void AugmentResourceSchemaDefaults(JsonObject resourceSchema)
    {
        AddIfMissing(resourceSchema, "booleanJsonPaths", () => new JsonArray());
        AddIfMissing(resourceSchema, "numericJsonPaths", () => new JsonArray());
        AddIfMissing(resourceSchema, "dateJsonPaths", () => new JsonArray());
        AddIfMissing(resourceSchema, "dateTimeJsonPaths", () => new JsonArray());
        AddIfMissing(resourceSchema, "authorizationPathways", () => new JsonArray());
        AddIfMissing(resourceSchema, "decimalPropertyValidationInfos", () => new JsonArray());
        AddIfMissing(resourceSchema, "queryFieldMapping", () => new JsonObject());
        AddIfMissing(
            resourceSchema,
            "securableElements",
            () =>
                new JsonObject
                {
                    ["Namespace"] = new JsonArray(),
                    ["EducationOrganization"] = new JsonArray(),
                    ["Student"] = new JsonArray(),
                    ["Contact"] = new JsonArray(),
                    ["Staff"] = new JsonArray(),
                }
        );
    }

    private static void AddIfMissing(JsonObject target, string propertyName, Func<JsonNode> defaultFactory)
    {
        if (!target.ContainsKey(propertyName))
        {
            target[propertyName] = defaultFactory();
        }
    }

    private static void WriteManifest(
        string materializedDirectory,
        IReadOnlyList<string> apiSchemaFileNames,
        IReadOnlyList<string> dialects
    )
    {
        var manifest = new JsonObject
        {
            ["apiSchemaFiles"] = new JsonArray([
                .. apiSchemaFileNames.Select(name => (JsonNode)JsonValue.Create(name)),
            ]),
            ["dialects"] = new JsonArray([.. dialects.Select(d => (JsonNode)JsonValue.Create(d))]),
        };

        File.WriteAllText(
            Path.Combine(materializedDirectory, "fixture.json"),
            manifest.ToJsonString(_writeOptions)
        );
    }

    /// <summary>
    /// Produces a file name that matches the host's <c>ApiSchema*.json</c> scan pattern,
    /// preserving the source name when it already conforms and otherwise prefixing it
    /// with <c>ApiSchema-</c>. Collisions are disambiguated with a numeric suffix.
    /// </summary>
    private static string BuildScannableFileName(string sourceFileName, HashSet<string> seen)
    {
        string candidate = sourceFileName.StartsWith("ApiSchema", StringComparison.OrdinalIgnoreCase)
            ? sourceFileName
            : $"ApiSchema-{Path.GetFileNameWithoutExtension(sourceFileName)}{Path.GetExtension(sourceFileName)}";

        if (seen.Add(candidate))
        {
            return candidate;
        }

        string stem = Path.GetFileNameWithoutExtension(candidate);
        string extension = Path.GetExtension(candidate);
        int suffix = 2;
        while (true)
        {
            string disambiguated = $"{stem}-{suffix}{extension}";
            if (seen.Add(disambiguated))
            {
                return disambiguated;
            }
            suffix++;
        }
    }
}
