// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.SchemaTools.Commands;

/// <summary>
/// Defines the <c>connection validate</c> subcommand: parses a connection string (read from stdin) using
/// the EXACT runtime provider - <see cref="Npgsql.NpgsqlConnectionStringBuilder"/> for PostgreSQL and
/// <see cref="Microsoft.Data.SqlClient.SqlConnectionStringBuilder"/> for SQL Server - and prints a JSON
/// result { valid, database, error } to stdout.
///
/// This is the single authoritative connection-string parser the docker-compose start scripts call, so
/// host-side pre-flight validation uses the same builder semantics the Configuration Service uses at
/// runtime: alias canonicalization, last-wins duplicate synonyms (Database/DB, Database/Initial Catalog),
/// and rejection of any keyword the provider does not support (e.g. Data Source/Encrypt under PostgreSQL,
/// Host under SQL Server). The connection string is read from stdin, never an argument, so a password does
/// not appear in the process arguments.
/// </summary>
public static class ConnectionCommand
{
    public static Command Create(ILogger logger)
    {
        var engineOption = new Option<string>("--engine")
        {
            Description = "Target database engine: postgresql or mssql",
            Required = true,
        };

        var validate = new Command(
            "validate",
            "Parse a connection string read from stdin with the exact runtime provider and print a JSON { valid, database, error } result"
        );
        validate.Options.Add(engineOption);
        validate.SetAction(parseResult =>
        {
            var engine = parseResult.GetValue(engineOption)!;
            var connectionString = Console.In.ReadToEnd().Trim();
            return Execute(logger, engine, connectionString);
        });

        var command = new Command("connection", "Connection-string commands");
        command.Subcommands.Add(validate);
        return command;
    }

    private static int Execute(ILogger logger, string engine, string connectionString)
    {
        // The engine is supplied by the caller's explicit provider selection (already an exact
        // postgresql|mssql enum); an unrecognized value is a usage error, distinct from an invalid
        // connection string, so it exits non-zero rather than producing a { valid: false } result.
        if (engine is not ("postgresql" or "mssql"))
        {
            Console.Error.WriteLine($"Unsupported engine '{engine}'. Use 'postgresql' or 'mssql'.");
            return 2;
        }

        bool valid;
        string? database = null;
        string? error = null;
        try
        {
            database = engine == "postgresql"
                ? ParsePostgreSql(connectionString)
                : ParseSqlServer(connectionString);
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
            JsonSerializer.Serialize(new ConnectionValidationResult(valid, database, error), SerializerOptions)
        );
        return 0;
    }

    private static string? ParsePostgreSql(string connectionString)
    {
        // The constructor invokes the setter, which throws on any keyword Npgsql does not recognize and
        // canonicalizes aliases (DB -> Database) with last-wins semantics.
        var builder = new Npgsql.NpgsqlConnectionStringBuilder(connectionString);
        return string.IsNullOrEmpty(builder.Database) ? null : builder.Database;
    }

    private static string? ParseSqlServer(string connectionString)
    {
        // Database and Initial Catalog are synonyms; Microsoft.Data.SqlClient collapses them last-wins and
        // exposes the effective value as InitialCatalog. Unsupported keywords throw.
        var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connectionString);
        return string.IsNullOrEmpty(builder.InitialCatalog) ? null : builder.InitialCatalog;
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
}
