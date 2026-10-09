// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.CommandLine;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.Startup;
using EdFi.DataManagementService.SchemaTools.Restamping;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace EdFi.DataManagementService.SchemaTools.Commands;

public static class DdlRestampCommand
{
    public static Command Create(
        ILogger logger,
        IApiSchemaFileLoader fileLoader,
        EffectiveSchemaSetBuilder schemaSetBuilder,
        ISchemaRestamper restamper,
        TextWriter output,
        TextWriter error
    )
    {
        var schemaOption = new Option<string[]>("--schema", "-s")
        {
            Description = "ApiSchema.json path(s), with core first and extensions after.",
            Required = true,
            AllowMultipleArgumentsPerToken = true,
        };
        var connectionOption = new Option<string>("--connection-string", "-c")
        {
            Description = "ADO.NET connection string for an existing target database.",
            Required = true,
        };
        var dialectOption = new Option<string>("--dialect", "-d")
        {
            Description = "SQL dialect: pgsql or mssql.",
            Required = true,
        };
        dialectOption.AcceptOnlyFromAmong("pgsql", "mssql");
        var timeoutOption = new Option<int>("--timeout", "-t")
        {
            Description = "Database command timeout in seconds (default: 300).",
            DefaultValueFactory = _ => 300,
        };
        var migrationCompletedOption = new Option<bool>("--migration-completed")
        {
            Description = "Confirm that the data-preserving physical migration is complete.",
            DefaultValueFactory = _ => false,
        };

        var command = new Command(
            "re-stamp",
            "Update the effective schema fingerprint after a validated physical migration"
        );
        command.Options.Add(schemaOption);
        command.Options.Add(connectionOption);
        command.Options.Add(dialectOption);
        command.Options.Add(timeoutOption);
        command.Options.Add(migrationCompletedOption);
        command.SetAction(
            (parseResult, cancellationToken) =>
                ExecuteAsync(
                    logger,
                    fileLoader,
                    schemaSetBuilder,
                    restamper,
                    output,
                    error,
                    parseResult.GetValue(schemaOption) ?? [],
                    parseResult.GetValue(connectionOption) ?? string.Empty,
                    parseResult.GetValue(dialectOption) ?? string.Empty,
                    parseResult.GetValue(timeoutOption),
                    parseResult.GetValue(migrationCompletedOption),
                    cancellationToken
                )
        );
        return command;
    }

    public static async Task<int> InvokeAsync(
        ParseResult parseResult,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default
    )
    {
        if (parseResult.Errors.Count > 0)
        {
            await error.WriteLineAsync("Invalid arguments for ddl re-stamp.");
            return 1;
        }

        try
        {
            return await parseResult.InvokeAsync(
                new InvocationConfiguration
                {
                    Output = output,
                    Error = error,
                    EnableDefaultExceptionHandler = false,
                    ProcessTerminationTimeout = TimeSpan.FromMinutes(5),
                },
                cancellationToken
            );
        }
        catch (OperationCanceledException)
        {
            await error.WriteLineAsync("Schema re-stamp was cancelled.");
            return 1;
        }
        catch (Exception exception)
        {
            await error.WriteLineAsync($"Schema re-stamp failed. ({exception.GetType().Name})");
            return 1;
        }
    }

    private static async Task<int> ExecuteAsync(
        ILogger logger,
        IApiSchemaFileLoader fileLoader,
        EffectiveSchemaSetBuilder schemaSetBuilder,
        ISchemaRestamper restamper,
        TextWriter output,
        TextWriter error,
        IReadOnlyList<string> schemaPaths,
        string connectionString,
        string dialectName,
        int timeout,
        bool migrationCompleted,
        CancellationToken cancellationToken
    )
    {
        if (timeout <= 0)
        {
            await error.WriteLineAsync("The command timeout must be a positive number of seconds.");
            return 1;
        }

        var dialect = dialectName switch
        {
            "pgsql" => SqlDialect.Pgsql,
            "mssql" => SqlDialect.Mssql,
            _ => (SqlDialect?)null,
        };
        if (dialect is null)
        {
            await error.WriteLineAsync("The SQL dialect must be pgsql or mssql.");
            return 1;
        }

        var connectionError = ValidateConnectionString(connectionString, dialect.Value);
        if (connectionError is not null)
        {
            await error.WriteLineAsync(connectionError);
            return 1;
        }
        if (schemaPaths.Count == 0 || string.IsNullOrWhiteSpace(schemaPaths[0]))
        {
            await error.WriteLineAsync("At least one schema path is required.");
            return 1;
        }

        SchemaRestampResult result;
        try
        {
            var loadResult = fileLoader.Load(schemaPaths[0], schemaPaths.Skip(1).ToArray());
            if (loadResult is not ApiSchemaFileLoadResult.SuccessResult success)
            {
                await error.WriteLineAsync("The schema inputs could not be loaded or normalized.");
                logger.LogError("Schema re-stamp inputs could not be loaded or normalized.");
                return 1;
            }

            var target = schemaSetBuilder.Build(success.NormalizedNodes).EffectiveSchema;
            result = await restamper.RestampAsync(
                dialect.Value,
                connectionString,
                timeout,
                target,
                migrationCompleted,
                cancellationToken
            );
        }
        catch (SchemaRestampException exception)
        {
            await error.WriteLineAsync(exception.Message);
            return 1;
        }
        catch (OperationCanceledException)
        {
            await error.WriteLineAsync("Schema re-stamp was cancelled.");
            return 1;
        }
        catch (Exception exception)
        {
            await error.WriteLineAsync($"Schema re-stamp failed. ({exception.GetType().Name})");
            return 1;
        }

        if (result.Changed)
        {
            await output.WriteLineAsync("Effective schema re-stamp committed.");
            await output.WriteLineAsync($"Previous effective schema hash: {result.PreviousHash}");
            await output.WriteLineAsync($"Target effective schema hash: {result.TargetHash}");
            await output.WriteLineAsync($"Schema component count: {result.ComponentCount}");
        }
        else
        {
            await output.WriteLineAsync("Already stamped; no changes.");
        }
        await output.WriteLineAsync(
            "This command updates schema metadata only. Run 'ddl provision' separately, then restart DMS and workers."
        );
        return 0;
    }

    private static string? ValidateConnectionString(string connectionString, SqlDialect dialect)
    {
        try
        {
            var hasDatabase = dialect switch
            {
                SqlDialect.Pgsql => new NpgsqlConnectionStringBuilder(connectionString) is var pg
                    && !string.IsNullOrWhiteSpace(pg.Database),
                SqlDialect.Mssql => new SqlConnectionStringBuilder(connectionString) is var ms
                    && !string.IsNullOrWhiteSpace(ms.InitialCatalog),
                _ => false,
            };
            return hasDatabase ? null : "An explicit target database name is required.";
        }
        catch (ArgumentException)
        {
            return "The target connection string is invalid.";
        }
    }
}
