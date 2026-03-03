// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.CommandLine;
using EdFi.DataManagementService.Backend.External;
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
                var result = DdlCommandHelpers.BuildDdl(
                    logger,
                    schemaSetBuilder,
                    success.NormalizedNodes,
                    dialect
                );
                var effectiveSchemaInfo = result.EffectiveSchemaSet.EffectiveSchema;

                // Create the appropriate provisioner
                var provisioner = CreateProvisioner(dialect, logger);
                var databaseName = provisioner.GetDatabaseName(connectionString);

                // Optional: create database if requested
                var databaseWasCreated = false;
                if (createDatabase)
                {
                    databaseWasCreated = provisioner.CreateDatabaseIfNotExists(connectionString);
                }

                // Fail-fast: if the database already has a different schema hash, abort
                // before executing any DDL. The in-SQL preflight in SeedDmlEmitter remains
                // as defense-in-depth inside the transaction.
                provisioner.PreflightSchemaHashCheck(
                    connectionString,
                    effectiveSchemaInfo.EffectiveSchemaHash
                );

                // Execute DDL in a transaction
                provisioner.ExecuteInTransaction(connectionString, result.CombinedSql);

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
