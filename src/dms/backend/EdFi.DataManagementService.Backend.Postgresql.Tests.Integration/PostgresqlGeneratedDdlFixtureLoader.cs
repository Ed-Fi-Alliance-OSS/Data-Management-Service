// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Startup;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

internal sealed record GeneratedDdlFixtureConfig(string[] ApiSchemaFiles);

internal sealed record PostgresqlGeneratedDdlFixture(
    string FixtureDirectory,
    EffectiveSchemaSet EffectiveSchemaSet,
    DerivedRelationalModelSet ModelSet,
    string GeneratedDdl
);

internal static class PostgresqlGeneratedDdlFixtureLoader
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static PostgresqlGeneratedDdlFixture LoadFromRepositoryRelativePath(string relativePath)
    {
        return LoadFromFixtureDirectory(RepositoryPathHelper.ResolveRepositoryRelativePath(relativePath));
    }

    public static PostgresqlGeneratedDdlFixture LoadFromFixtureDirectory(string fixtureDirectory)
    {
        var resolvedFixtureDirectory = Path.GetFullPath(fixtureDirectory);
        var config = ReadFixtureConfig(resolvedFixtureDirectory);
        var effectiveSchemaSet = LoadEffectiveSchemaSet(resolvedFixtureDirectory, config);
        var (modelSet, generatedDdl) = DdlPipelineHelpers.BuildDdlForDialect(
            effectiveSchemaSet,
            SqlDialect.Pgsql
        );

        return new(resolvedFixtureDirectory, effectiveSchemaSet, modelSet, generatedDdl);
    }

    private static GeneratedDdlFixtureConfig ReadFixtureConfig(string fixtureDirectory)
    {
        var fixturePath = Path.Combine(fixtureDirectory, "fixture.json");

        if (!File.Exists(fixturePath))
        {
            throw new FileNotFoundException($"Fixture manifest not found: {fixturePath}", fixturePath);
        }

        var config =
            JsonSerializer.Deserialize<GeneratedDdlFixtureConfig>(File.ReadAllText(fixturePath), _jsonOptions)
            ?? throw new InvalidOperationException(
                $"Failed to deserialize fixture manifest '{fixturePath}'."
            );

        if (config.ApiSchemaFiles.Length == 0)
        {
            throw new InvalidOperationException(
                $"Fixture manifest '{fixturePath}' must declare at least one apiSchemaFiles entry."
            );
        }

        return config;
    }

    private static EffectiveSchemaSet LoadEffectiveSchemaSet(
        string fixtureDirectory,
        GeneratedDdlFixtureConfig config
    )
    {
        var inputsDirectory = Path.Combine(fixtureDirectory, "inputs");
        var corePath = ResolveInputPath(inputsDirectory, config.ApiSchemaFiles[0]);
        var extensionPaths = config
            .ApiSchemaFiles.Skip(1)
            .Select(path => ResolveInputPath(inputsDirectory, path));

        var coreNode = ParseJsonNode(corePath);
        var extensionNodes = extensionPaths.Select(ParseJsonNode).ToArray();

        var rawNodes = new ApiSchemaDocumentNodes(coreNode, extensionNodes);
        var normalizer = new ApiSchemaInputNormalizer(NullLogger<ApiSchemaInputNormalizer>.Instance);
        var normalizationResult = normalizer.Normalize(rawNodes);

        var normalizedNodes = normalizationResult is ApiSchemaNormalizationResult.SuccessResult success
            ? success.NormalizedNodes
            : throw new InvalidOperationException(
                $"ApiSchema normalization failed for fixture '{fixtureDirectory}': {normalizationResult}"
            );

        var builder = new EffectiveSchemaSetBuilder(
            new EffectiveSchemaHashProvider(NullLogger<EffectiveSchemaHashProvider>.Instance),
            new ResourceKeySeedProvider(NullLogger<ResourceKeySeedProvider>.Instance)
        );

        return builder.Build(normalizedNodes);
    }

    private static string ResolveInputPath(string inputsDirectory, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidOperationException(
                $"Fixture inputs must be relative paths, but got '{relativePath}'."
            );
        }

        var resolvedPath = Path.GetFullPath(Path.Combine(inputsDirectory, relativePath));
        var resolvedInputsDirectory = Path.GetFullPath(inputsDirectory);
        var relativeResolvedPath = Path.GetRelativePath(resolvedInputsDirectory, resolvedPath);

        if (relativeResolvedPath.StartsWith("..", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Fixture input path escapes inputs directory: '{relativePath}'."
            );
        }

        if (!File.Exists(resolvedPath))
        {
            throw new FileNotFoundException($"Fixture input not found: {resolvedPath}", resolvedPath);
        }

        return resolvedPath;
    }

    private static JsonNode ParseJsonNode(string path)
    {
        return JsonNode.Parse(File.ReadAllText(path))
            ?? throw new InvalidOperationException($"Fixture input '{path}' parsed to null.");
    }
}
