// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.CommandLine;
using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.RelationalModel.Manifest;
using EdFi.DataManagementService.Backend.RelationalModel.Schema;
using EdFi.DataManagementService.Core.Startup;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.SchemaTools.Commands;

/// <summary>
/// Defines the <c>ddl emit</c> subcommand: generates DDL SQL + manifests to an output directory.
/// </summary>
public static class DdlEmitCommand
{
    private static readonly System.Text.UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

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

        var ddlManifestOption = new Option<bool>("--ddl-manifest")
        {
            Description = "Emit ddl.manifest.json (default: false)",
            DefaultValueFactory = _ => false,
        };

        var command = new Command(
            "emit",
            "Generate DDL SQL and manifests to an output directory without database connectivity"
        );
        command.Options.Add(schemaOption);
        command.Options.Add(outputOption);
        command.Options.Add(dialectOption);
        command.Options.Add(ddlManifestOption);

        command.SetAction(parseResult =>
        {
            var schemas = parseResult.GetValue(schemaOption) ?? [];
            var output = parseResult.GetValue(outputOption)!;
            var dialect = parseResult.GetValue(dialectOption) ?? "both";
            var emitDdlManifest = parseResult.GetValue(ddlManifestOption);
            return Execute(logger, fileLoader, schemaSetBuilder, schemas, output, dialect, emitDdlManifest);
        });

        return command;
    }

    private static int Execute(
        ILogger logger,
        IApiSchemaFileLoader fileLoader,
        EffectiveSchemaSetBuilder schemaSetBuilder,
        string[] schemaPaths,
        string outputDir,
        string dialectName,
        bool emitDdlManifest
    )
    {
        if (schemaPaths.Length == 0)
        {
            Console.Error.WriteLine("At least one --schema path is required.");
            return 1;
        }

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

                // Build the dialect-independent EffectiveSchemaSet once
                var effectiveSchemaSet = DdlCommandHelpers.BuildEffectiveSchemaSet(
                    logger,
                    schemaSetBuilder,
                    success.NormalizedNodes
                );
                var effectiveSchemaInfo = effectiveSchemaSet.EffectiveSchema;

                // Build DDL and write per-dialect outputs
                foreach (var dialect in dialects)
                {
                    var result = DdlCommandHelpers.BuildDdlFromSchemaSet(logger, effectiveSchemaSet, dialect);
                    ddlManifestEntries.Add(new DdlManifestEntry(dialect, result.CombinedSql));

                    // Write SQL file (always dialect-prefixed, matching {dialect}.sql convention)
                    var dialectLabel = DdlManifestEmitter.DialectLabel(dialect);
                    var sqlFileName = $"{dialectLabel}.sql";
                    WriteFileWithUnixLineEndings(Path.Combine(outputDir, sqlFileName), result.CombinedSql);
                    emittedFiles.Add(sqlFileName);

                    // Emit relational model manifest (always dialect-suffixed because the
                    // derived model is dialect-dependent via ISqlDialectRules naming/type rules).
                    // Pass all concrete resources as detailedResources to include the full
                    // resource inventory with key-unification surface per the design spec.
                    var allResources = new HashSet<QualifiedResourceName>(
                        result.ModelSet.ConcreteResourcesInNameOrder.Select(r => r.ResourceKey.Resource)
                    );
                    var modelManifest = DerivedModelSetManifestEmitter.Emit(result.ModelSet, allResources);
                    var manifestFileName = $"relational-model.{dialectLabel}.manifest.json";
                    WriteFileWithUnixLineEndings(Path.Combine(outputDir, manifestFileName), modelManifest);
                    emittedFiles.Add(manifestFileName);
                }

                // Emit DDL manifest (dialect-independent summary of emitted SQL per dialect).
                // The manifest reflects only the dialect(s) selected via --dialect.
                // Controlled by --ddl-manifest (default false).
                if (emitDdlManifest)
                {
                    var ddlManifest = DdlManifestEmitter.Emit(effectiveSchemaInfo, ddlManifestEntries);
                    WriteFileWithUnixLineEndings(Path.Combine(outputDir, "ddl.manifest.json"), ddlManifest);
                    emittedFiles.Add("ddl.manifest.json");
                }

                // Emit effective schema manifest (dialect-independent, emitted once)
                var schemaManifest = EffectiveSchemaManifestEmitter.Emit(
                    effectiveSchemaInfo,
                    includeResourceKeys: true
                );
                WriteFileWithUnixLineEndings(
                    Path.Combine(outputDir, "effective-schema.manifest.json"),
                    schemaManifest
                );
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

    private static void WriteFileWithUnixLineEndings(string path, string content)
    {
        if (!content.Contains('\r'))
        {
            File.WriteAllText(path, content, Utf8NoBom);
            return;
        }

        // Normalize to LF line endings for deterministic output
        var normalized = content.Replace("\r\n", "\n").Replace("\r", "\n");
        File.WriteAllText(path, normalized, Utf8NoBom);
    }
}
