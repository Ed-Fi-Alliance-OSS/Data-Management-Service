// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Startup;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdFi.DataManagementService.Backend.Tests.Common;

public static class EffectiveSchemaFixtureLoader
{
    private sealed record GeneratedDdlFixtureConfig(string[] ApiSchemaFiles);

    internal sealed record FixtureInputFile(string Path, string Content);

    internal sealed record FixtureContentDescriptor(
        string FixtureDirectory,
        FixtureInputFile[] ResolvedInputs,
        string CacheKey
    );

    private static readonly ConcurrentDictionary<string, Lazy<EffectiveSchemaSet>> _cache = new(
        StringComparer.Ordinal
    );
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static EffectiveSchemaSet LoadFromFixtureDirectory(
        string fixtureDirectory,
        string? repositoryRoot = null
    )
    {
        return LoadEffectiveSchemaSet(DescribeFixtureDirectory(fixtureDirectory, repositoryRoot));
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

        return LoadEffectiveSchemaSet(
            DescribeEffectiveSchemaInputs(fixtureDirectory, apiSchemaFiles, repositoryRoot)
        );
    }

    internal static FixtureContentDescriptor DescribeFixtureDirectory(
        string fixtureDirectory,
        string? repositoryRoot = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fixtureDirectory);

        var resolvedFixtureDirectory = Path.GetFullPath(fixtureDirectory);
        var fixturePath = Path.Combine(resolvedFixtureDirectory, "fixture.json");
        var fixtureManifestContent = ReadFixtureManifestContent(fixturePath);
        var config = ReadFixtureConfig(fixturePath, fixtureManifestContent);

        return DescribeEffectiveSchemaInputs(
            resolvedFixtureDirectory,
            config.ApiSchemaFiles,
            fixtureManifestContent,
            repositoryRoot
        );
    }

    internal static EffectiveSchemaSet LoadEffectiveSchemaSet(FixtureContentDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var lazyEffectiveSchemaSet = _cache.GetOrAdd(
            descriptor.CacheKey,
            _ =>
                new(
                    () => LoadEffectiveSchemaSetCore(descriptor),
                    LazyThreadSafetyMode.ExecutionAndPublication
                )
        );

        try
        {
            return lazyEffectiveSchemaSet.Value;
        }
        catch
        {
            _cache.TryRemove(new(descriptor.CacheKey, lazyEffectiveSchemaSet));
            throw;
        }
    }

    private static FixtureContentDescriptor DescribeEffectiveSchemaInputs(
        string fixtureDirectory,
        IReadOnlyList<string> apiSchemaFiles,
        string? repositoryRoot = null
    )
    {
        return DescribeEffectiveSchemaInputs(
            fixtureDirectory,
            apiSchemaFiles,
            fixtureManifestContent: null,
            repositoryRoot
        );
    }

    private static FixtureContentDescriptor DescribeEffectiveSchemaInputs(
        string fixtureDirectory,
        IReadOnlyList<string> apiSchemaFiles,
        string? fixtureManifestContent,
        string? repositoryRoot
    )
    {
        var resolvedFixtureDirectory = Path.GetFullPath(fixtureDirectory);
        var resolvedInputs = ResolveFixtureInputPaths(
                resolvedFixtureDirectory,
                apiSchemaFiles,
                repositoryRoot
            )
            .Select(path => new FixtureInputFile(path, File.ReadAllText(path)))
            .ToArray();
        var cacheKey = BuildCacheKey(resolvedFixtureDirectory, fixtureManifestContent, resolvedInputs);

        return new(resolvedFixtureDirectory, resolvedInputs, cacheKey);
    }

    private static EffectiveSchemaSet LoadEffectiveSchemaSetCore(FixtureContentDescriptor descriptor)
    {
        var coreNode = ParseJsonNode(descriptor.ResolvedInputs[0]);
        var extensionNodes = descriptor.ResolvedInputs.Skip(1).Select(ParseJsonNode).ToArray();
        var rawNodes = new ApiSchemaDocumentNodes(coreNode, extensionNodes);
        var normalizer = new ApiSchemaInputNormalizer(NullLogger<ApiSchemaInputNormalizer>.Instance);
        var normalizationResult = normalizer.Normalize(rawNodes);

        var normalizedNodes = normalizationResult is ApiSchemaNormalizationResult.SuccessResult success
            ? success.NormalizedNodes
            : throw new InvalidOperationException(
                $"ApiSchema normalization failed for fixture '{descriptor.FixtureDirectory}': {normalizationResult}"
            );

        var builder = new EffectiveSchemaSetBuilder(
            new EffectiveSchemaHashProvider(NullLogger<EffectiveSchemaHashProvider>.Instance),
            new ResourceKeySeedProvider(NullLogger<ResourceKeySeedProvider>.Instance)
        );

        return builder.Build(normalizedNodes);
    }

    private static string[] ResolveFixtureInputPaths(
        string fixtureDirectory,
        IReadOnlyList<string> apiSchemaFiles,
        string? repositoryRoot
    )
    {
        return apiSchemaFiles
            .Select(path =>
                FixturePathResolver.ResolveFixtureInputPath(fixtureDirectory, path, repositoryRoot)
            )
            .ToArray();
    }

    private static string BuildCacheKey(
        string fixtureDirectory,
        string? fixtureManifestContent,
        IReadOnlyList<FixtureInputFile> resolvedInputs
    )
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendHashString(hash, fixtureDirectory);

        if (fixtureManifestContent is not null)
        {
            AppendHashString(hash, fixtureManifestContent);
        }

        foreach (var resolvedInput in resolvedInputs)
        {
            AppendHashString(hash, resolvedInput.Path);
            AppendHashString(hash, resolvedInput.Content);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string ReadFixtureManifestContent(string fixturePath)
    {
        if (!File.Exists(fixturePath))
        {
            throw new FileNotFoundException($"Fixture manifest not found: {fixturePath}", fixturePath);
        }

        return File.ReadAllText(fixturePath);
    }

    private static GeneratedDdlFixtureConfig ReadFixtureConfig(
        string fixturePath,
        string fixtureManifestContent
    )
    {
        var config =
            JsonSerializer.Deserialize<GeneratedDdlFixtureConfig>(fixtureManifestContent, _jsonOptions)
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

    private static JsonNode ParseJsonNode(FixtureInputFile inputFile)
    {
        ArgumentNullException.ThrowIfNull(inputFile);

        return JsonNode.Parse(inputFile.Content)
            ?? throw new InvalidOperationException($"Fixture input '{inputFile.Path}' parsed to null.");
    }

    private static void AppendHashString(IncrementalHash hash, string value)
    {
        var encoded = Encoding.UTF8.GetBytes(value);
        Span<byte> lengthPrefix = stackalloc byte[sizeof(int)];
        BitConverter.TryWriteBytes(lengthPrefix, encoded.Length);
        hash.AppendData(lengthPrefix);
        hash.AppendData(encoded);
    }
}
