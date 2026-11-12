// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Diagnostics;
using System.Net.Sockets;
using CommandLine;
using CommandLine.Text;
using EdFi.DmsConfigurationService.Backend.Deploy;
using EdFi.DmsConfigurationService.Backend.Services;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Installer;

public class Options
{
    [Option(
        'e',
        "engine",
        Required = true,
        HelpText = "The database engine to deploy. Valid values are 'postgresql' and 'mssql'"
    )]
    public string? Datastore { get; set; }

    [Option('c', "connectionString", Required = true, HelpText = "The connection string to the database.")]
    public string? ConnectionString { get; set; }

    [Option(
        "reencrypt-connection-strings",
        HelpText = "Instead of deploying, rewrite every legacy AES-CBC data store connection string in the "
            + "already deployed database as an AES-GCM envelope. Reads the key from the "
            + EncryptionKeyVariable
            + " environment variable. Stop every Configuration Service instance first."
    )]
    public bool ReencryptConnectionStrings { get; set; }

    [Option(
        "check-connection-strings",
        HelpText = "Instead of deploying, verify every data store connection string in the already deployed "
            + "database and count those still in the legacy AES-CBC format, changing nothing. Exits 0 when "
            + "none remain, 2 when some do, and 1 when a value could not be verified. Reads the key from the "
            + EncryptionKeyVariable
            + " environment variable. Can run while the Configuration Service is running."
    )]
    public bool CheckConnectionStrings { get; set; }

    /// <summary>
    /// Collects stray values, such as the "false" in "--reencrypt-connection-strings false", which
    /// the parser would otherwise drop, leaving the switch on.
    /// </summary>
    [Value(0, Hidden = true)]
    public IEnumerable<string> UnexpectedValues { get; set; } = [];

    public const string EncryptionKeyVariable = "DatabaseSettings__EncryptionKey";
}

public static class Program
{
    private static ParserResult<Options>? _parseResult;

    private static readonly string[] _connectionStringSwitches =
    [
        "--reencrypt-connection-strings",
        "--check-connection-strings",
    ];

    public static void Main(string[] args)
    {
        // The parser reads "--switch=anything", even "=false", as the switch turned on, so a switch
        // that would migrate or check is accepted only bare.
        if (
            Array.Find(
                args,
                argument =>
                    Array.Exists(
                        _connectionStringSwitches,
                        name => argument.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase)
                    )
            ) is
            { } assigned
        )
        {
            Console.WriteLine($"{assigned[..assigned.IndexOf('=')]} takes no value.");
            Environment.ExitCode = 1;
            return;
        }

        var parser = new Parser(config => config.HelpWriter = null);
        _parseResult = parser.ParseArguments<Options>(args);
        _parseResult
            .WithParsed(runOptions =>
            {
                if (runOptions.UnexpectedValues.Any())
                {
                    Console.WriteLine("Unexpected value on the command line.");
                    DisplayHelp(_parseResult);
                    Environment.ExitCode = 1;
                    return;
                }

                if (runOptions is { ReencryptConnectionStrings: true, CheckConnectionStrings: true })
                {
                    Console.WriteLine(
                        "--reencrypt-connection-strings and --check-connection-strings cannot be combined."
                    );
                    Environment.ExitCode = 1;
                    return;
                }

                if (runOptions.ReencryptConnectionStrings || runOptions.CheckConnectionStrings)
                {
                    Environment.ExitCode = ProcessConnectionStrings(
                        runOptions,
                        check: runOptions.CheckConnectionStrings
                    );
                    return;
                }

                switch (runOptions.Datastore)
                {
                    case "postgresql":
                        HandleResult(
                            new Postgresql.Deploy.DatabaseDeploy().DeployDatabase(
                                runOptions.ConnectionString!
                            )
                        );
                        break;
                    case "mssql":
                        HandleResult(
                            new Mssql.Deploy.DatabaseDeploy().DeployDatabase(runOptions.ConnectionString!)
                        );
                        break;
                    default:
                        Console.WriteLine("Invalid database engine specified.");
                        DisplayHelp(_parseResult);
                        Environment.ExitCode = 1;
                        break;
                }
            })
            .WithNotParsed(errors =>
            {
                DisplayHelp(_parseResult);

                // Automation must not read an unparsed command, such as a misspelled switch, as one
                // that ran. A requested help or version screen is not a failure.
                if (!errors.IsHelp() && !errors.IsVersion())
                {
                    Environment.ExitCode = 1;
                }
            });

        if (Debugger.IsAttached)
        {
            Console.WriteLine("Press enter to continue.");
            Console.ReadLine();
        }
    }

    /// <summary>
    /// Re-encrypts, or with <paramref name="check"/> only verifies and counts, and returns the process
    /// exit code. Output names tables, rows and counts only: never the key, a connection string,
    /// cipher text, or exception text.
    /// </summary>
    private static int ProcessConnectionStrings(Options runOptions, bool check)
    {
        string operation = check ? "Connection string check" : "Connection string re-encryption";

        ConnectionStringReencryptionEngine engine;
        switch (runOptions.Datastore)
        {
            case "postgresql":
                engine = ConnectionStringReencryptionEngine.Postgresql;
                break;
            case "mssql":
                engine = ConnectionStringReencryptionEngine.Mssql;
                break;
            default:
                Console.WriteLine("Invalid database engine specified.");
                return 1;
        }

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelOnInterrupt = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelOnInterrupt;

        try
        {
            var reencryption = new ConnectionStringReencryption(
                new DatabaseOptions
                {
                    DatabaseConnection = runOptions.ConnectionString!,
                    EncryptionKey = Environment.GetEnvironmentVariable(Options.EncryptionKeyVariable) ?? "",
                },
                engine
            );

            using DbConnection connection =
                engine == ConnectionStringReencryptionEngine.Postgresql
                    ? new NpgsqlConnection(runOptions.ConnectionString)
                    : new SqlConnection(runOptions.ConnectionString);
            connection.Open();

            IReadOnlyList<ConnectionStringReencryptionCounts> counts = (
                check
                    ? reencryption.CheckAsync(connection, cancellation.Token)
                    : reencryption.RunAsync(connection, cancellation.Token)
            )
                .GetAwaiter()
                .GetResult();

            foreach (ConnectionStringReencryptionCounts tableCounts in counts)
            {
                Console.WriteLine(
                    check
                        ? $"{tableCounts.Table}: {tableCounts.Converted} legacy, {tableCounts.AlreadyCurrent} current."
                        : $"{tableCounts.Table}: {tableCounts.Converted} converted, {tableCounts.AlreadyCurrent} already current."
                );
            }

            if (check)
            {
                int legacy = counts.Sum(tableCounts => tableCounts.Converted);
                Console.WriteLine(
                    legacy == 0
                        ? "Connection string check completed: no legacy values remain."
                        : $"Connection string check completed: {legacy} legacy values remain. Run --reencrypt-connection-strings before disabling legacy values."
                );
                return legacy == 0 ? 0 : 2;
            }

            Console.WriteLine("Connection string re-encryption completed.");
            return 0;
        }
        catch (ConnectionStringReencryptionException ex)
        {
            Console.WriteLine(ex.Message);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine($"{operation} was cancelled. No changes were made.");
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                check
                    ? $"{operation} failed ({Describe(ex)}). It changes nothing, so it is safe to rerun."
                    : $"{operation} failed ({Describe(ex)}). The pass runs in one transaction and a "
                        + "rerun rewrites only values still in the legacy format, so it is safe to rerun."
            );
        }
        finally
        {
            Console.CancelKeyPress -= cancelOnInterrupt;
        }

        return 1;
    }

    /// <summary>
    /// The exception's type and any database or socket error code in its chain: enough to tell a wrong
    /// host from a wrong database or password, and none of it secret.
    /// </summary>
    private static string Describe(Exception exception)
    {
        List<string> parts = [exception.GetType().Name];
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case PostgresException postgres:
                    parts.Add($"SQLSTATE {postgres.SqlState}");
                    break;
                case SqlException sqlServer:
                    parts.Add($"SQL Server error {sqlServer.Number}");
                    break;
                case SocketException socket:
                    parts.Add($"socket error {socket.SocketErrorCode}");
                    break;
            }
        }

        return string.Join(", ", parts.Distinct());
    }

    private static void HandleResult(DatabaseDeployResult result)
    {
        if (result is DatabaseDeployResult.DatabaseDeployFailure failure)
        {
            Console.WriteLine($"Failed to deploy database: {failure.Error.Message}");
            Environment.ExitCode = 1;
        }
    }

    private static void DisplayHelp<T>(ParserResult<T> parseResult)
    {
        var helpText = HelpText.AutoBuild(
            parseResult,
            h =>
            {
                h.Heading = "Ed-Fi API Configuration Service Backend Installer";
                h.Copyright = string.Empty;
                h.AutoVersion = false;
                return h;
            },
            e => e
        );
        Console.WriteLine(helpText);
    }
}
