// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using EdFi.DataManagementService.SchemaTools.Connections;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.SchemaTools.Commands;

/// <summary>
/// Defines the <c>connection</c> subcommand group. Both verbs read the connection string from stdin (never an
/// argument, so a password does not appear in the process arguments) and parse it with the single
/// exact-provider authority (<see cref="IConnectionInspector"/>, backed by
/// <see cref="Npgsql.NpgsqlConnectionStringBuilder"/> for PostgreSQL and
/// <see cref="Microsoft.Data.SqlClient.SqlConnectionStringBuilder"/> for SQL Server):
///
/// <list type="bullet">
/// <item><c>validate</c> prints a JSON <c>{ valid, database, error }</c> result - the stable contract the
/// docker-compose start scripts consume for host-side pre-flight validation.</item>
/// <item><c>inspect</c> prints a JSON <c>{ valid, database, host, port, username, error }</c> result of the
/// non-secret canonical coordinates provisioning needs. It never emits the password.</item>
/// </list>
///
/// Both use the same builder semantics the Configuration Service uses at runtime: alias canonicalization,
/// last-wins duplicate synonyms (Database/DB, Database/Initial Catalog), and rejection of any keyword the
/// provider does not support (e.g. Data Source/Encrypt under PostgreSQL, Host under SQL Server).
/// </summary>
public static class ConnectionCommand
{
    public static Command Create(ILogger logger)
    {
        var command = new Command("connection", "Connection-string commands");
        command.Subcommands.Add(CreateValidate(logger));
        command.Subcommands.Add(CreateInspect(logger));
        return command;
    }

    private static Option<string> NewEngineOption() =>
        new("--engine") { Description = "Target database engine: postgresql or mssql", Required = true };

    private static Command CreateValidate(ILogger logger)
    {
        var engineOption = NewEngineOption();
        var validate = new Command(
            "validate",
            "Parse a connection string read from stdin with the exact runtime provider and print a JSON { valid, database, error } result"
        );
        validate.Options.Add(engineOption);
        validate.SetAction(parseResult =>
        {
            var engine = parseResult.GetValue(engineOption)!;
            var connectionString = Console.In.ReadToEnd().Trim();
            return ExecuteValidate(logger, engine, connectionString);
        });
        return validate;
    }

    private static Command CreateInspect(ILogger logger)
    {
        var engineOption = NewEngineOption();
        var inspect = new Command(
            "inspect",
            "Parse a connection string read from stdin with the exact runtime provider and print a JSON { valid, database, host, port, username, error } result of non-secret canonical fields"
        );
        inspect.Options.Add(engineOption);
        inspect.SetAction(parseResult =>
        {
            var engine = parseResult.GetValue(engineOption)!;
            var connectionString = Console.In.ReadToEnd().Trim();
            return ExecuteInspect(logger, engine, connectionString);
        });
        return inspect;
    }

    private static int ExecuteValidate(ILogger logger, string engine, string connectionString)
    {
        // Canonical engine-token boundary. An unsupported value or a surrounding-whitespace variant is a
        // usage error - distinct from an invalid connection string - and exits non-zero rather than producing
        // a { valid: false } result.
        var inspector = ConnectionInspectors.ForEngine(engine);
        if (inspector is null)
        {
            Console.Error.WriteLine($"Unsupported engine '{engine}'. Use 'postgresql' or 'mssql'.");
            return 2;
        }

        bool valid;
        string? database = null;
        string? error = null;
        try
        {
            database = inspector.Parse(connectionString).Database;
            valid = true;
        }
        catch (Exception ex)
        {
            valid = false;
            error = ex.Message;
            logger.LogDebug(ex, "Connection-string validation failed for engine {Engine}", engine);
        }

        // Structured result on stdout; Program.cs routes all logs to stderr when stdout is redirected,
        // so a caller capturing stdout receives exactly this one JSON line.
        Console.Out.WriteLine(
            JsonSerializer.Serialize(
                new ConnectionValidationResult(valid, database, error),
                SerializerOptions
            )
        );
        return 0;
    }

    private static int ExecuteInspect(ILogger logger, string engine, string connectionString)
    {
        var inspector = ConnectionInspectors.ForEngine(engine);
        if (inspector is null)
        {
            Console.Error.WriteLine($"Unsupported engine '{engine}'. Use 'postgresql' or 'mssql'.");
            return 2;
        }

        bool valid;
        ConnectionTarget? target = null;
        string? error = null;
        try
        {
            target = inspector.Parse(connectionString);
            valid = true;
        }
        catch (Exception ex)
        {
            valid = false;
            error = ex.Message;
            logger.LogDebug(ex, "Connection-string inspection failed for engine {Engine}", engine);
        }

        // Non-secret canonical fields only - the password is never read out of the builder or emitted.
        Console.Out.WriteLine(
            JsonSerializer.Serialize(
                new ConnectionInspectResult(
                    valid,
                    target?.Database,
                    target?.Host,
                    target?.Port,
                    target?.Username,
                    error
                ),
                SerializerOptions
            )
        );
        return 0;
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private sealed record ConnectionValidationResult(
        [property: JsonPropertyName("valid")] bool Valid,
        [property: JsonPropertyName("database")] string? Database,
        [property: JsonPropertyName("error")] string? Error
    );

    private sealed record ConnectionInspectResult(
        [property: JsonPropertyName("valid")] bool Valid,
        [property: JsonPropertyName("database")] string? Database,
        [property: JsonPropertyName("host")] string? Host,
        [property: JsonPropertyName("port")] int? Port,
        [property: JsonPropertyName("username")] string? Username,
        [property: JsonPropertyName("error")] string? Error
    );
}
