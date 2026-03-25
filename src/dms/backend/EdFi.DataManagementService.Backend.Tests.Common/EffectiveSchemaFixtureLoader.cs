// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Startup;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdFi.DataManagementService.Backend.Tests.Common;

public static class EffectiveSchemaFixtureLoader
{
    private sealed record GeneratedDdlFixtureConfig(string[] ApiSchemaFiles);

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static EffectiveSchemaSet LoadFromFixtureDirectory(
        string fixtureDirectory,
        string? repositoryRoot = null
    )
    {
        var resolvedFixtureDirectory = Path.GetFullPath(fixtureDirectory);
        var config = ReadFixtureConfig(resolvedFixtureDirectory);

        return LoadEffectiveSchemaSet(resolvedFixtureDirectory, config.ApiSchemaFiles, repositoryRoot);
    }

    public static EffectiveSchemaSet LoadEffectiveSchemaSet(
        string fixtureDirectory,
        IReadOnlyList<string> apiSchemaFiles,
        string? repositoryRoot = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fixtureDirectory);
        ArgumentNullException.ThrowIfNull(apiSchemaFiles);

        if (apiSchemaFiles.Count == 0)
        {
            throw new InvalidOperationException(
                $"Fixture manifest '{Path.Combine(fixtureDirectory, "fixture.json")}' must declare at least one apiSchemaFiles entry."
            );
        }

        var resolvedPaths = apiSchemaFiles
            .Select(path =>
                FixturePathResolver.ResolveFixtureInputPath(fixtureDirectory, path, repositoryRoot)
            )
            .ToArray();

        var coreNode = ParseJsonNode(resolvedPaths[0]);
        var extensionNodes = resolvedPaths.Skip(1).Select(ParseJsonNode).ToArray();
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

        if (config.ApiSchemaFiles is null || config.ApiSchemaFiles.Length == 0)
        {
            throw new InvalidOperationException(
                $"Fixture manifest '{fixturePath}' must declare at least one apiSchemaFiles entry."
            );
        }

        return config;
    }

    private static JsonNode ParseJsonNode(string path)
    {
        return JsonNode.Parse(File.ReadAllText(path))
            ?? throw new InvalidOperationException($"Fixture input '{path}' parsed to null.");
    }
}
