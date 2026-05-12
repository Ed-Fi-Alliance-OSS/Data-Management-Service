// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Text.Json;
using EdFi.DataManagementService.Backend.Tests.Common;

namespace EdFi.DataManagementService.Tests.Integration.Fixtures;

/// <summary>
/// Resolves a <see cref="FixtureKey"/> to a fully populated <see cref="FixtureContext"/>.
/// The fixture's source ApiSchema files (named according to each fixture's manifest)
/// are materialized into a process-cached temp directory whose file names match the
/// <c>ApiSchema*.json</c> pattern the DMS host scans for when
/// <c>AppSettings:UseApiSchemaPath</c> is enabled.
/// </summary>
internal static class FixtureContextLoader
{
    private static readonly ConcurrentDictionary<FixtureKey, Lazy<FixtureContext>> _cache = new();

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
        IReadOnlyList<string> resolvedSourcePaths = ResolveSourcePaths(fixtureDirectory, apiSchemaFiles);

        string apiSchemaDirectory = MaterializeApiSchemaDirectory(key, resolvedSourcePaths);

        IReadOnlyList<QualifiedResourceName> resources = ResourceEnumerator.FromApiSchemaFiles(
            resolvedSourcePaths
        );

        string profileXmlDirectory = FixturePathResolver.ResolveRepositoryRelativePath(
            AppContext.BaseDirectory,
            $"src/dms/tests/EdFi.DataManagementService.Tests.Integration/Fixtures/Profiles/{key}"
        );

        return new FixtureContext(key, apiSchemaDirectory, profileXmlDirectory, resources);
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
    /// Copies the fixture's ApiSchema source files into a per-fixture temp directory using
    /// file names that match the host's <c>ApiSchema*.json</c> scan pattern. The directory
    /// is cached for the lifetime of the test process.
    /// </summary>
    private static string MaterializeApiSchemaDirectory(FixtureKey key, IReadOnlyList<string> sourcePaths)
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

        var seenTargetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string sourcePath in sourcePaths)
        {
            string targetName = BuildScannableFileName(Path.GetFileName(sourcePath), seenTargetNames);
            string targetPath = Path.Combine(materializedDirectory, targetName);
            File.Copy(sourcePath, targetPath, overwrite: true);
        }

        return materializedDirectory;
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
