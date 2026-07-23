// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.CommandLine;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.Startup;
using EdFi.DataManagementService.Core.Utilities;
using EdFi.DataManagementService.SchemaTools.Connections;
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

        var timeoutOption = new Option<int>("--timeout", "-t")
        {
            Description = "Command timeout in seconds for DDL execution (default: 300).",
            DefaultValueFactory = _ => 300,
        };

        var overrideHostOption = new Option<string?>("--override-host")
        {
            Description =
                "Optional host to substitute for the connection string's endpoint. Must be supplied together "
                + "with --override-port. The exact provider rewrites only the endpoint; every other option "
                + "(credentials, SSL, ...) is preserved.",
        };

        var overridePortOption = new Option<int?>("--override-port")
        {
            Description =
                "Optional port (1-65535) to substitute for the connection string's endpoint. Must be supplied "
                + "together with --override-host.",
        };

        var command = new Command("provision", "Generate DDL and execute it against a target database");
        command.Options.Add(schemaOption);
        command.Options.Add(connectionStringOption);
        command.Options.Add(dialectOption);
        command.Options.Add(createDatabaseOption);
        command.Options.Add(timeoutOption);
        command.Options.Add(overrideHostOption);
        command.Options.Add(overridePortOption);

        command.SetAction(parseResult =>
        {
            var schemas = parseResult.GetValue(schemaOption) ?? [];
            var connectionString = parseResult.GetValue(connectionStringOption)!;
            var dialect = parseResult.GetValue(dialectOption)!;
            var createDatabase = parseResult.GetValue(createDatabaseOption);
            var timeout = parseResult.GetValue(timeoutOption);
            var overrideHost = parseResult.GetValue(overrideHostOption);
            var overridePort = parseResult.GetValue(overridePortOption);
            return Execute(
                logger,
                fileLoader,
                schemaSetBuilder,
                schemas,
                connectionString,
                dialect,
                createDatabase,
                timeout,
                overrideHost,
                overridePort
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
        bool createDatabase,
        int commandTimeoutSeconds,
        string? overrideHost,
        int? overridePort
    )
    {
        if (schemaPaths.Length == 0)
        {
            Console.Error.WriteLine("At least one --schema path is required.");
            return 1;
        }

        var dialect = ParseDialect(dialectName);

        // Endpoint-override contract (atomic): supply --override-host and --override-port together or not at
        // all; a non-blank host and a port in 1-65535. When supplied, the exact provider rewrites ONLY the
        // endpoint (every other option is preserved), and the rewritten connection is then used for every
        // operation below - database-name lookup, optional create, MVCC, seed/schema preflight, and
        // transactional DDL. Validated up front, before any schema load or database connection.
        if (
            !TryResolveEffectiveConnectionString(
                dialect,
                connectionString,
                overrideHost,
                overridePort,
                out connectionString,
                out var overrideError
            )
        )
        {
            // Route provider-derived text (from user-controlled connection parsing) through the console
            // safety boundary before emission.
            Console.Error.WriteLine(LoggingSanitizer.SanitizeForConsole(overrideError));
            return 1;
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

                // Check/configure MVCC (SQL Server only; no-op for PostgreSQL).
                // Runs before DDL so a post-DDL MVCC failure cannot turn a successful
                // provision into exit code 1. Safe because ALTER DATABASE runs on
                // master independently of the target DB connection.
                provisioner.CheckOrConfigureMvcc(connectionString, databaseWasCreated);

                // Fail-fast preflight: verify schema hash and seed data integrity before
                // executing any DDL. PreflightSeedValidation validates the schema hash
                // internally, so a separate PreflightSchemaHashCheck call is not needed.
                //
                // Defense-in-depth: the in-SQL validation in SeedDmlEmitter also checks
                // the hash inside the DDL transaction as an ultimate safety net.
                provisioner.PreflightSeedValidation(connectionString, effectiveSchemaInfo);

                // Execute DDL in a transaction
                provisioner.ExecuteInTransaction(connectionString, result.CombinedSql, commandTimeoutSeconds);

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

    /// <summary>
    /// Validates the atomic endpoint-override contract and, when supplied, rewrites the connection string's
    /// endpoint through the exact provider (<see cref="ConnectionInspectors"/>), preserving every other
    /// option. Returns false with a usage message when the contract is violated.
    /// </summary>
    private static bool TryResolveEffectiveConnectionString(
        SqlDialect dialect,
        string connectionString,
        string? overrideHost,
        int? overridePort,
        out string effectiveConnectionString,
        out string? error
    )
    {
        effectiveConnectionString = connectionString;
        error = null;

        var hostProvided = overrideHost is not null;
        var portProvided = overridePort is not null;
        if (!hostProvided && !portProvided)
        {
            // Neither supplied: the connection string is used unchanged.
            return true;
        }
        if (hostProvided != portProvided)
        {
            error = "--override-host and --override-port must be supplied together.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(overrideHost))
        {
            error = "--override-host must be a non-blank host.";
            return false;
        }
        if (overridePort < 1 || overridePort > 65535)
        {
            error = $"--override-port must be between 1 and 65535 (got {overridePort}).";
            return false;
        }

        var engine = dialect == SqlDialect.Pgsql ? "postgresql" : "mssql";
        var inspector = ConnectionInspectors.ForEngine(engine)!;
        try
        {
            effectiveConnectionString = inspector.ApplyEndpointOverride(
                connectionString,
                overrideHost!,
                overridePort!.Value
            );
        }
        catch (Exception ex)
        {
            // The exact provider rejected the connection string. Surface a controlled usage error with the
            // provider's own (keyword-level) message - never a stack trace, and never the connection string or
            // the password (the provider message references the offending keyword, not its value).
            error =
                $"The connection string is not a valid '{engine}' connection, so the endpoint override cannot be applied: {ex.Message}";
            return false;
        }
        return true;
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
