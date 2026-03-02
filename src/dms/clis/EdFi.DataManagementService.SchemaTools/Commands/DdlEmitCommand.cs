// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.CommandLine;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.RelationalModel.Build;
using EdFi.DataManagementService.Backend.RelationalModel.Manifest;
using EdFi.DataManagementService.Core.Startup;
using EdFi.DataManagementService.Core.Utilities;
using EdFi.DataManagementService.SchemaTools.Bridge;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.SchemaTools.Commands;

/// <summary>
/// Defines the <c>ddl emit</c> subcommand: generates DDL SQL + manifests to an output directory.
/// </summary>
public static class DdlEmitCommand
{
    private static readonly System.Text.UTF8Encoding _utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static Command Create(
        ILogger logger,
        IApiSchemaFileLoader fileLoader,
        EffectiveSchemaSetBuilder schemaSetBuilder
    )
    {
        var schemaOption = new Option<string[]>("--schema", "-s")
        {
            Description = "ApiSchema.json path(s). First is core, rest are extensions.",
            Required = true,
            AllowMultipleArgumentsPerToken = true,
        };

        var outputOption = new Option<string>("--output", "-o")
        {
            Description = "Output directory for generated files",
            Required = true,
        };

        var dialectOption = new Option<string>("--dialect", "-d")
        {
            Description = "SQL dialect: pgsql, mssql, or both",
            DefaultValueFactory = _ => "both",
        };
        dialectOption.AcceptOnlyFromAmong("pgsql", "mssql", "both");

        var command = new Command(
            "emit",
            "Generate DDL SQL and manifests to an output directory without database connectivity"
        );
        command.Options.Add(schemaOption);
        command.Options.Add(outputOption);
        command.Options.Add(dialectOption);

        command.SetAction(parseResult =>
        {
            var schemas = parseResult.GetValue(schemaOption) ?? [];
            var output = parseResult.GetValue(outputOption)!;
            var dialect = parseResult.GetValue(dialectOption) ?? "both";
            return Execute(logger, fileLoader, schemaSetBuilder, schemas, output, dialect);
        });

        return command;
    }

    private static int Execute(
        ILogger logger,
        IApiSchemaFileLoader fileLoader,
        EffectiveSchemaSetBuilder schemaSetBuilder,
        string[] schemaPaths,
        string outputDir,
        string dialectName
    )
    {
        // Dialect is validated at parse time by AcceptOnlyFromAmong
        var dialects = ParseDialect(dialectName);

        // Load schemas
        var corePath = schemaPaths[0];
        var extensionPaths = schemaPaths.Skip(1).ToList();

        logger.LogInformation(
            "Loading schemas: core={CorePath}, extensions={ExtensionCount}",
            LoggingSanitizer.SanitizeForLogging(corePath),
            extensionPaths.Count
        );

        var loadResult = fileLoader.Load(corePath, extensionPaths);

        if (loadResult is not ApiSchemaFileLoadResult.SuccessResult success)
        {
            return LoadResultErrorHandler.Handle(logger, loadResult);
        }

        return CommandErrorHandler.Execute(
            logger,
            "DDL emission",
            () =>
            {
                // Build EffectiveSchemaSet
                var effectiveSchemaSet = schemaSetBuilder.Build(success.NormalizedNodes);
                var effectiveSchemaInfo = effectiveSchemaSet.EffectiveSchema;

                logger.LogInformation(
                    "Effective schema hash: {Hash}, resource keys: {ResourceKeyCount}",
                    effectiveSchemaInfo.EffectiveSchemaHash,
                    effectiveSchemaInfo.ResourceKeyCount
                );

                // Create output directory (must be empty or new to avoid stale artifacts)
                Directory.CreateDirectory(outputDir);

                if (Directory.EnumerateFileSystemEntries(outputDir).Any())
                {
                    logger.LogError(
                        "Output directory is not empty: {OutputDir}",
                        LoggingSanitizer.SanitizeForLogging(outputDir)
                    );
                    Console.Error.WriteLine(
                        $"Error: Output directory is not empty: {LoggingSanitizer.SanitizeForConsole(outputDir)}"
                    );
                    Console.Error.WriteLine(
                        "Remove existing files or choose a different output directory to avoid stale artifacts."
                    );
                    return 1;
                }

                var emittedFiles = new List<string>();
                var ddlManifestEntries = new List<DdlManifestEntry>();

                // Emit DDL and model manifests per dialect
                foreach (var dialect in dialects)
                {
                    var (sqlDialect, dialectRules) = CreateDialect(dialect);

                    // Deep-clone the effective schema set for each dialect because
                    // DerivedRelationalModelSetBuilder assigns JsonNode.Parent on ProjectSchema
                    // nodes, which prevents reuse across builds. Ideally the builder should
                    // treat inputs as immutable, but until then we clone before each build.
                    var clonedSchemaSet = CloneEffectiveSchemaSet(effectiveSchemaSet);

                    // Build relational model
                    var modelSetBuilder = new DerivedRelationalModelSetBuilder(
                        RelationalModelSetPasses.CreateDefault()
                    );
                    var modelSet = modelSetBuilder.Build(clonedSchemaSet, dialect, dialectRules);

                    // Emit DDL: core + relational model + seed DML.
                    // SeedDmlEmitter uses the original effectiveSchemaInfo (not the cloned set)
                    // because EffectiveSchemaInfo is an immutable record unaffected by model builder mutation.
                    var coreDdl = new CoreDdlEmitter(sqlDialect).Emit();
                    var relationalDdl = new RelationalModelDdlEmitter(sqlDialect).Emit(modelSet);
                    var seedDml = new SeedDmlEmitter(sqlDialect).Emit(effectiveSchemaInfo);
                    var combinedSql = coreDdl + relationalDdl + seedDml;
                    ddlManifestEntries.Add(new DdlManifestEntry(dialect, combinedSql));

                    // Write SQL file (always dialect-prefixed, matching {dialect}.sql convention)
                    var dialectLabel = DialectLabel(dialect);
                    var sqlFileName = $"{dialectLabel}.sql";
                    var sqlPath = Path.Combine(outputDir, sqlFileName);
                    WriteFileWithUnixLineEndings(sqlPath, combinedSql);
                    emittedFiles.Add(sqlFileName);

                    // Emit relational model manifest (always dialect-suffixed because the
                    // derived model is dialect-dependent via ISqlDialectRules naming/type rules).
                    // Pass all concrete resources as detailedResources to include the full
                    // resource inventory with key-unification surface per the design spec.
                    var allResources = new HashSet<QualifiedResourceName>(
                        modelSet.ConcreteResourcesInNameOrder.Select(r => r.ResourceKey.Resource)
                    );
                    var modelManifest = DerivedModelSetManifestEmitter.Emit(modelSet, allResources);
                    var manifestFileName = $"relational-model.{dialectLabel}.manifest.json";
                    var manifestPath = Path.Combine(outputDir, manifestFileName);
                    WriteFileWithUnixLineEndings(manifestPath, modelManifest);
                    emittedFiles.Add(manifestFileName);
                }

                // Emit DDL manifest (DMS-953)
                var ddlManifest = DdlManifestEmitter.Emit(effectiveSchemaInfo, ddlManifestEntries);
                var ddlManifestPath = Path.Combine(outputDir, "ddl.manifest.json");
                WriteFileWithUnixLineEndings(ddlManifestPath, ddlManifest);
                emittedFiles.Add("ddl.manifest.json");

                // Emit effective schema manifest (dialect-independent, emitted once)
                var schemaManifest = EffectiveSchemaManifestEmitter.Emit(
                    effectiveSchemaInfo,
                    includeResourceKeys: true
                );
                var schemaManifestPath = Path.Combine(outputDir, "effective-schema.manifest.json");
                WriteFileWithUnixLineEndings(schemaManifestPath, schemaManifest);
                emittedFiles.Add("effective-schema.manifest.json");

                // Print summary
                Console.WriteLine(
                    $"DDL emission complete. Output directory: {LoggingSanitizer.SanitizeForConsole(outputDir)}"
                );
                Console.WriteLine($"Effective schema hash: {effectiveSchemaInfo.EffectiveSchemaHash}");
                Console.WriteLine($"Resource key count: {effectiveSchemaInfo.ResourceKeyCount}");
                Console.WriteLine("Files written:");
                foreach (var file in emittedFiles)
                {
                    Console.WriteLine($"  {file}");
                }

                return 0;
            }
        );
    }

    private static string DialectLabel(SqlDialect dialect) =>
        dialect switch
        {
            SqlDialect.Pgsql => "pgsql",
            SqlDialect.Mssql => "mssql",
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unsupported dialect"),
        };

    private static List<SqlDialect> ParseDialect(string dialectName)
    {
        return dialectName.ToLowerInvariant() switch
        {
            "pgsql" => [SqlDialect.Pgsql],
            "mssql" => [SqlDialect.Mssql],
            "both" => [SqlDialect.Pgsql, SqlDialect.Mssql],
            _ => throw new ArgumentOutOfRangeException(
                nameof(dialectName),
                dialectName,
                "Invalid dialect (should be rejected by AcceptOnlyFromAmong)"
            ),
        };
    }

    private static (ISqlDialect Dialect, ISqlDialectRules Rules) CreateDialect(SqlDialect dialect)
    {
        return dialect switch
        {
            SqlDialect.Pgsql => CreatePgsqlDialect(),
            SqlDialect.Mssql => CreateMssqlDialect(),
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unsupported dialect"),
        };

        static (ISqlDialect, ISqlDialectRules) CreatePgsqlDialect()
        {
            var rules = new PgsqlDialectRules();
            return (new PgsqlDialect(rules), rules);
        }

        static (ISqlDialect, ISqlDialectRules) CreateMssqlDialect()
        {
            var rules = new MssqlDialectRules();
            return (new MssqlDialect(rules), rules);
        }
    }

    private static EffectiveSchemaSet CloneEffectiveSchemaSet(EffectiveSchemaSet original)
    {
        var clonedProjects = original
            .ProjectsInEndpointOrder.Select(p => new EffectiveProjectSchema(
                p.ProjectEndpointName,
                p.ProjectName,
                p.ProjectVersion,
                p.IsExtensionProject,
                (JsonObject)p.ProjectSchema.DeepClone()
            ))
            .ToList();

        return new EffectiveSchemaSet(original.EffectiveSchema, clonedProjects);
    }

    private static void WriteFileWithUnixLineEndings(string path, string content)
    {
        if (!content.Contains('\r'))
        {
            File.WriteAllText(path, content, _utf8NoBom);
            return;
        }

        // Normalize to LF line endings for deterministic output
        var normalized = content.Replace("\r\n", "\n").Replace("\r", "\n");
        File.WriteAllText(path, normalized, _utf8NoBom);
    }
}
