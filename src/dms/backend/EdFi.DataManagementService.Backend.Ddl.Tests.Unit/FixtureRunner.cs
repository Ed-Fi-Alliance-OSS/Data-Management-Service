// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.RelationalModel.Manifest;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Startup;
using EdFi.DataManagementService.SchemaTools.Bridge;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdFi.DataManagementService.Backend.Ddl.Tests.Unit;

/// <summary>
/// Orchestrates the fixture pipeline: loads inputs, builds the effective schema,
/// derives relational models per dialect, emits all artifacts, and writes them to actual/.
/// Reuses the same emitter code as the CLI to prevent pipeline drift.
/// </summary>
public static class FixtureRunner
{
    private static readonly UTF8Encoding _utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Runs a fixture: loads inputs, builds all artifacts, writes them to actual/.
    /// </summary>
    /// <param name="fixtureDirectory">Absolute path to the fixture directory containing fixture.json and inputs/.</param>
    /// <returns>The absolute path to the actual/ output directory.</returns>
    public static string Run(string fixtureDirectory)
    {
        var config = FixtureConfigReader.Read(fixtureDirectory);
        var actualDir = Path.Combine(fixtureDirectory, "actual");

        // Clean and recreate actual/ to avoid stale artifacts
        if (Directory.Exists(actualDir))
        {
            Directory.Delete(actualDir, recursive: true);
        }
        Directory.CreateDirectory(actualDir);

        // Load ApiSchema.json files from inputs/
        var inputsDir = Path.Combine(fixtureDirectory, "inputs");
        var nodes = LoadSchemaNodes(config, inputsDir);

        // Build the dialect-independent EffectiveSchemaSet
        var builder = CreateEffectiveSchemaSetBuilder();
        var effectiveSchemaSet = builder.Build(nodes);
        var effectiveSchemaInfo = effectiveSchemaSet.EffectiveSchema;

        // Build DDL and write per-dialect outputs
        var ddlManifestEntries = new List<DdlManifestEntry>();
        var dialects = ParseDialects(config.Dialects);

        foreach (var dialect in dialects)
        {
            var (modelSet, combinedSql) = DdlPipelineHelpers.BuildDdlForDialect(effectiveSchemaSet, dialect);
            ddlManifestEntries.Add(new DdlManifestEntry(dialect, combinedSql));

            var dialectLabel = DdlManifestEmitter.DialectLabel(dialect);

            // Write {dialect}.sql
            WriteFileNormalized(Path.Combine(actualDir, $"{dialectLabel}.sql"), combinedSql);

            // Write relational-model.{dialect}.manifest.json
            var allResources = new HashSet<QualifiedResourceName>(
                modelSet.ConcreteResourcesInNameOrder.Select(r => r.ResourceKey.Resource)
            );
            var modelManifest = DerivedModelSetManifestEmitter.Emit(modelSet, allResources);
            WriteFileNormalized(
                Path.Combine(actualDir, $"relational-model.{dialectLabel}.manifest.json"),
                modelManifest
            );
        }

        // Write effective-schema.manifest.json (always)
        var schemaManifest = EffectiveSchemaManifestEmitter.Emit(
            effectiveSchemaInfo,
            includeResourceKeys: true
        );
        WriteFileNormalized(Path.Combine(actualDir, "effective-schema.manifest.json"), schemaManifest);

        // Write ddl.manifest.json (when configured and dialects are present)
        if (config.EmitDdlManifest && ddlManifestEntries.Count > 0)
        {
            var ddlManifest = DdlManifestEmitter.Emit(effectiveSchemaInfo, ddlManifestEntries);
            WriteFileNormalized(Path.Combine(actualDir, "ddl.manifest.json"), ddlManifest);
        }

        return actualDir;
    }

    private static ApiSchemaDocumentNodes LoadSchemaNodes(FixtureConfig config, string inputsDir)
    {
        var corePath = Path.Combine(inputsDir, config.ApiSchemaFiles[0]);
        var coreNode =
            JsonNode.Parse(File.ReadAllText(corePath))
            ?? throw new InvalidOperationException($"Core schema parsed to null: {corePath}");

        var extensionNodes = config
            .ApiSchemaFiles.Skip(1)
            .Select(relativePath =>
            {
                var fullPath = Path.Combine(inputsDir, relativePath);
                return JsonNode.Parse(File.ReadAllText(fullPath))
                    ?? throw new InvalidOperationException($"Extension schema parsed to null: {fullPath}");
            })
            .ToArray();

        var rawNodes = new ApiSchemaDocumentNodes(coreNode, extensionNodes);

        // Normalize inputs to match CLI pipeline (ApiSchemaFileLoader.Load → Normalize)
        var normalizer = new ApiSchemaInputNormalizer(NullLogger<ApiSchemaInputNormalizer>.Instance);
        var normalizationResult = normalizer.Normalize(rawNodes);

        if (normalizationResult is not ApiSchemaNormalizationResult.SuccessResult success)
        {
            throw new InvalidOperationException(
                $"ApiSchema normalization failed for fixture inputs: {normalizationResult}"
            );
        }

        return success.NormalizedNodes;
    }

    private static EffectiveSchemaSetBuilder CreateEffectiveSchemaSetBuilder() =>
        new(
            new EffectiveSchemaHashProvider(NullLogger<EffectiveSchemaHashProvider>.Instance),
            new ResourceKeySeedProvider(NullLogger<ResourceKeySeedProvider>.Instance)
        );

    private static List<SqlDialect> ParseDialects(string[] dialectNames) =>
        dialectNames
            .Select(name =>
                name.ToLowerInvariant() switch
                {
                    "pgsql" => SqlDialect.Pgsql,
                    "mssql" => SqlDialect.Mssql,
                    _ => throw new InvalidOperationException($"Unknown dialect: {name}"),
                }
            )
            .ToList();

    private static void WriteFileNormalized(string path, string content)
    {
        // Normalize to LF line endings for deterministic output
        var normalized = content.Contains('\r') ? content.Replace("\r\n", "\n").Replace("\r", "\n") : content;

        File.WriteAllText(path, normalized, _utf8NoBom);
    }
}
