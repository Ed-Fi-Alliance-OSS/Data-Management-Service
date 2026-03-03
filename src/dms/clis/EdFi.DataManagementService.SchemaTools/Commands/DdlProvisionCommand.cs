// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.CommandLine;
using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.RelationalModel.Build;
using EdFi.DataManagementService.Core.Startup;
using EdFi.DataManagementService.Core.Utilities;
using EdFi.DataManagementService.SchemaTools.Bridge;
using EdFi.DataManagementService.SchemaTools.Provisioning;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.SchemaTools.Commands;

/// <summary>
/// Defines the <c>ddl provision</c> subcommand: generates DDL and executes it against
/// a target database. Provisions one database at a time (no 'both' dialect).
/// </summary>
public static class DdlProvisionCommand
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

        var connectionStringOption = new Option<string>("--connection-string", "-c")
        {
            Description = "ADO.NET connection string for the target database.",
            Required = true,
        };

        var dialectOption = new Option<string>("--dialect", "-d")
        {
            Description = "SQL dialect: pgsql or mssql",
            Required = true,
        };
        dialectOption.AcceptOnlyFromAmong("pgsql", "mssql");

        var createDatabaseOption = new Option<bool>("--create-database")
        {
            Description = "Create the target database if it does not exist before provisioning.",
            DefaultValueFactory = _ => false,
        };

        var command = new Command("provision", "Generate DDL and execute it against a target database");
        command.Options.Add(schemaOption);
        command.Options.Add(connectionStringOption);
        command.Options.Add(dialectOption);
        command.Options.Add(createDatabaseOption);

        command.SetAction(parseResult =>
        {
            var schemas = parseResult.GetValue(schemaOption) ?? [];
            var connectionString = parseResult.GetValue(connectionStringOption)!;
            var dialect = parseResult.GetValue(dialectOption)!;
            var createDatabase = parseResult.GetValue(createDatabaseOption);
            return Execute(
                logger,
                fileLoader,
                schemaSetBuilder,
                schemas,
                connectionString,
                dialect,
                createDatabase
            );
        });

        return command;
    }

    private static int Execute(
        ILogger logger,
        IApiSchemaFileLoader fileLoader,
        EffectiveSchemaSetBuilder schemaSetBuilder,
        string[] schemaPaths,
        string connectionString,
        string dialectName,
        bool createDatabase
    )
    {
        var dialect = ParseDialect(dialectName);

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
            "DDL provisioning",
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

                // Create dialect-specific objects
                var (sqlDialect, dialectRules) = DdlCommandHelpers.CreateDialect(dialect);

                // Deep-clone the effective schema set because
                // DerivedRelationalModelSetBuilder assigns JsonNode.Parent on ProjectSchema
                // nodes, which prevents reuse of the original tree.
                var clonedSchemaSet = DdlCommandHelpers.CloneEffectiveSchemaSet(effectiveSchemaSet);

                // Build relational model
                var modelSetBuilder = new DerivedRelationalModelSetBuilder(
                    RelationalModelSetPasses.CreateDefault()
                );
                var modelSet = modelSetBuilder.Build(clonedSchemaSet, dialect, dialectRules);

                // Log diagnostics for skipped key-unification constraints and decimal precision fallbacks
                foreach (var resource in modelSet.ConcreteResourcesInNameOrder)
                {
                    var resourceLabel = LoggingSanitizer.SanitizeForLogging(
                        $"{resource.ResourceKey.Resource.ProjectName}:{resource.ResourceKey.Resource.ResourceName}"
                    );

                    var skipped = resource.RelationalModel.KeyUnificationEqualityConstraints.Skipped;
                    if (skipped.Count > 0)
                    {
                        logger.LogDebug(
                            "Resource {Resource}: {Count} key-unification constraint(s) skipped due to unresolved binding paths",
                            resourceLabel,
                            skipped.Count
                        );
                        foreach (var entry in skipped)
                        {
                            logger.LogDebug(
                                "  Skipped: source={Source}, target={Target}, unresolved={Endpoint}",
                                LoggingSanitizer.SanitizeForLogging(entry.SourcePath.Canonical),
                                LoggingSanitizer.SanitizeForLogging(entry.TargetPath.Canonical),
                                LoggingSanitizer.SanitizeForLogging(entry.UnresolvedEndpoint)
                            );
                        }
                    }

                    var fallbacks = resource.RelationalModel.DecimalPrecisionFallbacks;
                    if (fallbacks.Count > 0)
                    {
                        logger.LogDebug(
                            "Resource {Resource}: {Count} decimal property/ies fell back to default precision (18,4)",
                            resourceLabel,
                            fallbacks.Count
                        );
                        foreach (var entry in fallbacks)
                        {
                            logger.LogDebug(
                                "  Fallback: path={Path}, reason={Reason}",
                                LoggingSanitizer.SanitizeForLogging(entry.SourcePath.Canonical),
                                LoggingSanitizer.SanitizeForLogging(entry.Reason)
                            );
                        }
                    }
                }

                // Emit DDL: core + relational model + seed DML
                var coreDdl = new CoreDdlEmitter(sqlDialect).Emit();
                var relationalDdl = new RelationalModelDdlEmitter(sqlDialect).Emit(modelSet);
                var seedDml = new SeedDmlEmitter(sqlDialect).Emit(effectiveSchemaInfo);
                var combinedSql = coreDdl + relationalDdl + seedDml;

                // Create the appropriate provisioner
                var provisioner = CreateProvisioner(dialect, logger);
                var databaseName = provisioner.GetDatabaseName(connectionString);

                // Optional: create database if requested
                var databaseWasCreated = false;
                if (createDatabase)
                {
                    databaseWasCreated = provisioner.CreateDatabaseIfNotExists(connectionString);
                }

                // TODO(DMS-952): Preflight hash-mismatch check before provisioning

                // Execute DDL in a transaction
                provisioner.ExecuteInTransaction(connectionString, combinedSql);

                // Check/configure MVCC (SQL Server only; no-op for PostgreSQL)
                provisioner.CheckOrConfigureMvcc(connectionString, databaseWasCreated);

                // Print summary
                Console.WriteLine(
                    $"Provisioning complete for database: {LoggingSanitizer.SanitizeForConsole(databaseName)}"
                );
                Console.WriteLine(
                    $"Effective schema hash: {LoggingSanitizer.SanitizeForConsole(effectiveSchemaInfo.EffectiveSchemaHash)}"
                );
                Console.WriteLine($"Resource key count: {effectiveSchemaInfo.ResourceKeyCount}");
                Console.WriteLine($"Dialect: {LoggingSanitizer.SanitizeForConsole(dialectName)}");

                return 0;
            }
        );
    }

    private static SqlDialect ParseDialect(string dialectName)
    {
        return dialectName.ToLowerInvariant() switch
        {
            "pgsql" => SqlDialect.Pgsql,
            "mssql" => SqlDialect.Mssql,
            _ => throw new ArgumentOutOfRangeException(
                nameof(dialectName),
                dialectName,
                "Invalid dialect (should be rejected by AcceptOnlyFromAmong)"
            ),
        };
    }

    private static IDatabaseProvisioner CreateProvisioner(SqlDialect dialect, ILogger logger)
    {
        return dialect switch
        {
            SqlDialect.Pgsql => new PgsqlDatabaseProvisioner(logger),
            SqlDialect.Mssql => new MssqlDatabaseProvisioner(logger),
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unsupported dialect"),
        };
    }
}
