// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;

namespace EdFi.DataManagementService.Backend.Ddl.Tests.Unit;

/// <summary>
/// Deserialized representation of a fixture.json file.
/// </summary>
public record FixtureConfig(
    string[] ApiSchemaFiles,
    string[] Dialects,
    bool BuildMappingPack = false,
    bool EmitDdlManifest = true
);

/// <summary>
/// Reads and validates fixture.json files from fixture directories.
/// </summary>
public static class FixtureConfigReader
{
    private static readonly string[] _knownDialects = ["pgsql", "mssql"];

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// Reads and validates fixture.json from the given fixture directory.
    /// </summary>
    /// <param name="fixtureDirectory">
    /// Absolute path to the fixture directory (must contain fixture.json and inputs/).
    /// </param>
    /// <returns>A validated <see cref="FixtureConfig"/>.</returns>
    public static FixtureConfig Read(string fixtureDirectory)
    {
        var fixturePath = Path.Combine(fixtureDirectory, "fixture.json");

        if (!File.Exists(fixturePath))
        {
            throw new FileNotFoundException(
                $"fixture.json not found in fixture directory: {fixtureDirectory}"
            );
        }

        var json = File.ReadAllText(fixturePath);
        var config =
            JsonSerializer.Deserialize<FixtureConfig>(json, _jsonOptions)
            ?? throw new InvalidOperationException($"Failed to deserialize fixture.json: {fixturePath}");

        Validate(config, fixtureDirectory);

        return config;
    }

    private static void Validate(FixtureConfig config, string fixtureDirectory)
    {
        if (config.ApiSchemaFiles is null)
        {
            throw new InvalidOperationException("fixture.json is missing required field 'apiSchemaFiles'.");
        }

        if (config.Dialects is null)
        {
            throw new InvalidOperationException("fixture.json is missing required field 'dialects'.");
        }

        if (config.ApiSchemaFiles.Length == 0)
        {
            throw new InvalidOperationException(
                "fixture.json must declare at least one entry in apiSchemaFiles."
            );
        }

        if (config.Dialects.Length == 0)
        {
            throw new InvalidOperationException("fixture.json must declare at least one entry in dialects.");
        }

        foreach (var dialect in config.Dialects)
        {
            if (!_knownDialects.Contains(dialect, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Unknown dialect '{dialect}' in fixture.json. Known dialects: {string.Join(", ", _knownDialects)}"
                );
            }
        }

        if (config.BuildMappingPack)
        {
            throw new NotSupportedException(
                "buildMappingPack=true is not yet supported. Pack emission will be implemented in a future ticket."
            );
        }

        var inputsDir = Path.Combine(fixtureDirectory, "inputs");
        var resolvedInputsDir = Path.GetFullPath(inputsDir) + Path.DirectorySeparatorChar;

        foreach (var schemaFile in config.ApiSchemaFiles)
        {
            if (Path.IsPathRooted(schemaFile))
            {
                throw new InvalidOperationException(
                    $"apiSchemaFiles must be relative paths, but got rooted path: '{schemaFile}'"
                );
            }

            var resolvedPath = Path.GetFullPath(Path.Combine(inputsDir, schemaFile));

            if (!resolvedPath.StartsWith(resolvedInputsDir, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"apiSchemaFiles path escapes the inputs/ directory: '{schemaFile}'"
                );
            }

            if (!File.Exists(resolvedPath))
            {
                throw new FileNotFoundException(
                    $"ApiSchema file declared in fixture.json not found: {resolvedPath}"
                );
            }
        }
    }
}
