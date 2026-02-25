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
        // Validate dialect
        var dialects = ParseDialect(dialectName);
        if (dialects is null)
        {
            logger.LogError(
                "Invalid dialect: {Dialect}. Must be pgsql, mssql, or both.",
                LoggingSanitizer.SanitizeForLogging(dialectName)
            );
            Console.Error.WriteLine(
                $"Error: Invalid dialect '{LoggingSanitizer.SanitizeForLogging(dialectName)}'. Must be pgsql, mssql, or both."
            );
            return 1;
        }

        // Validate schema paths
        if (schemaPaths.Length < 1)
        {
            Console.Error.WriteLine("Error: At least one schema path (core) is required.");
            return 1;
        }

        foreach (var schemaPath in schemaPaths)
        {
            if (!File.Exists(schemaPath))
            {
                logger.LogError(
                    "Schema file not found: {FilePath}",
                    LoggingSanitizer.SanitizeForLogging(schemaPath)
                );
                Console.Error.WriteLine(
                    $"Error: Schema file not found: {LoggingSanitizer.SanitizeForLogging(schemaPath)}"
                );
                return 1;
            }
        }

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
            return HandleLoadError(logger, loadResult);
        }

        // Build EffectiveSchemaSet
        var effectiveSchemaSet = schemaSetBuilder.Build(success.NormalizedNodes);
        var effectiveSchemaInfo = effectiveSchemaSet.EffectiveSchema;

        logger.LogInformation(
            "Effective schema hash: {Hash}, resource keys: {ResourceKeyCount}",
            effectiveSchemaInfo.EffectiveSchemaHash,
            effectiveSchemaInfo.ResourceKeyCount
        );

        // Create output directory
        Directory.CreateDirectory(outputDir);

        var emittedFiles = new List<string>();
        var emitMultipleDialects = dialects.Count > 1;

        // Emit DDL and model manifests per dialect
        foreach (var dialect in dialects)
        {
            var (sqlDialect, dialectRules) = CreateDialect(dialect);

            // Deep-clone the effective schema set for each dialect because the
            // relational model builder mutates ProjectSchema JsonObjects by parenting them.
            var clonedSchemaSet = CloneEffectiveSchemaSet(effectiveSchemaSet);

            // Build relational model
            var modelSetBuilder = new DerivedRelationalModelSetBuilder(
                RelationalModelSetPasses.CreateDefault()
            );
            var modelSet = modelSetBuilder.Build(clonedSchemaSet, dialect, dialectRules);

            // Emit DDL: core + relational model + seed DML
            var coreDdl = new CoreDdlEmitter(sqlDialect).Emit();
            var relationalDdl = new RelationalModelDdlEmitter(sqlDialect).Emit(modelSet);
            var seedDml = new SeedDmlEmitter(sqlDialect).Emit(effectiveSchemaInfo);
            var combinedSql = coreDdl + relationalDdl + seedDml;

            // Write SQL file
            var dialectLabel = dialect == SqlDialect.Pgsql ? "pgsql" : "mssql";
            var sqlFileName = $"{dialectLabel}.sql";
            var sqlPath = Path.Combine(outputDir, sqlFileName);
            WriteFileWithUnixLineEndings(sqlPath, combinedSql);
            emittedFiles.Add(sqlFileName);

            // Emit relational model manifest
            var modelManifest = DerivedModelSetManifestEmitter.Emit(modelSet);
            var manifestFileName = emitMultipleDialects
                ? $"relational-model.{dialectLabel}.manifest.json"
                : "relational-model.manifest.json";
            var manifestPath = Path.Combine(outputDir, manifestFileName);
            WriteFileWithUnixLineEndings(manifestPath, modelManifest);
            emittedFiles.Add(manifestFileName);
        }

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
            $"DDL emission complete. Output directory: {LoggingSanitizer.SanitizeForLogging(outputDir)}"
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

    private static List<SqlDialect>? ParseDialect(string dialectName)
    {
        return dialectName.ToLowerInvariant() switch
        {
            "pgsql" => [SqlDialect.Pgsql],
            "mssql" => [SqlDialect.Mssql],
            "both" => [SqlDialect.Pgsql, SqlDialect.Mssql],
            _ => null,
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
        // Normalize to LF line endings for deterministic output
        var normalized = content.Replace("\r\n", "\n");
        File.WriteAllText(path, normalized);
    }

    private static int HandleLoadError(ILogger logger, ApiSchemaFileLoadResult result)
    {
        switch (result)
        {
            case ApiSchemaFileLoadResult.FileNotFoundResult failure:
                logger.LogError(
                    "File not found: {FilePath}",
                    LoggingSanitizer.SanitizeForLogging(failure.FilePath)
                );
                Console.Error.WriteLine(
                    $"Error: File not found: {LoggingSanitizer.SanitizeForLogging(failure.FilePath)}"
                );
                return 1;

            case ApiSchemaFileLoadResult.FileReadErrorResult failure:
                logger.LogError(
                    "Failed to read file {FilePath}: {Error}",
                    LoggingSanitizer.SanitizeForLogging(failure.FilePath),
                    LoggingSanitizer.SanitizeForLogging(failure.ErrorMessage)
                );
                Console.Error.WriteLine(
                    $"Error: Failed to read file {LoggingSanitizer.SanitizeForLogging(failure.FilePath)}: {LoggingSanitizer.SanitizeForLogging(failure.ErrorMessage)}"
                );
                return 1;

            case ApiSchemaFileLoadResult.InvalidJsonResult failure:
                logger.LogError(
                    "Invalid JSON in file {FilePath}: {Error}",
                    LoggingSanitizer.SanitizeForLogging(failure.FilePath),
                    LoggingSanitizer.SanitizeForLogging(failure.ErrorMessage)
                );
                Console.Error.WriteLine(
                    $"Error: Invalid JSON in file {LoggingSanitizer.SanitizeForLogging(failure.FilePath)}: {LoggingSanitizer.SanitizeForLogging(failure.ErrorMessage)}"
                );
                return 1;

            case ApiSchemaFileLoadResult.NormalizationFailureResult failure:
                var message = failure.FailureResult switch
                {
                    ApiSchemaNormalizationResult.MissingOrMalformedProjectSchemaResult r =>
                        $"Schema '{r.SchemaSource}' is malformed: {r.Details}",
                    ApiSchemaNormalizationResult.ApiSchemaVersionMismatchResult r =>
                        $"Version mismatch in '{r.SchemaSource}': expected {r.ExpectedVersion}, got {r.ActualVersion}",
                    ApiSchemaNormalizationResult.ProjectEndpointNameCollisionResult r =>
                        $"Endpoint name collision(s): {string.Join("; ", r.Collisions.Select(c => $"'{c.ProjectEndpointName}' in [{string.Join(", ", c.ConflictingSources)}]"))}",
                    _ => "Unknown normalization failure",
                };
                logger.LogError(
                    "Schema normalization failed: {Message}",
                    LoggingSanitizer.SanitizeForLogging(message)
                );
                Console.Error.WriteLine($"Error: {LoggingSanitizer.SanitizeForLogging(message)}");
                return 1;

            default:
                logger.LogError("Unknown result type: {ResultType}", result.GetType().Name);
                Console.Error.WriteLine($"Error: Unknown result type: {result.GetType().Name}");
                return 1;
        }
    }
}
