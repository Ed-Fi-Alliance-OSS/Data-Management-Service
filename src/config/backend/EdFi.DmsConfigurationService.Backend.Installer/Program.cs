// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Diagnostics;
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

    public const string EncryptionKeyVariable = "DatabaseSettings__EncryptionKey";
}

public static class Program
{
    private static ParserResult<Options>? _parseResult;

    private const string ReencryptSwitch = "--reencrypt-connection-strings";

    /// <summary>
    /// The switch alone or in the parser's assignment form, such as "--reencrypt-connection-strings=true".
    /// </summary>
    private static bool IsReencryptSwitch(string argument) =>
        argument == ReencryptSwitch || argument.StartsWith(ReencryptSwitch + "=", StringComparison.Ordinal);

    public static void Main(string[] args)
    {
        var parser = new Parser(config => config.HelpWriter = null);
        _parseResult = parser.ParseArguments<Options>(args);
        _parseResult
            .WithParsed(runOptions =>
            {
                if (runOptions.ReencryptConnectionStrings)
                {
                    Environment.ExitCode = ReencryptConnectionStrings(runOptions);
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
                        break;
                }
            })
            .WithNotParsed(errors =>
            {
                DisplayHelp(_parseResult);

                // Automation must not read an unparsed migration as a successful one. A requested help
                // or version screen is not a failure, and the deploy mode keeps its exit status.
                if (Array.Exists(args, IsReencryptSwitch) && !errors.IsHelp() && !errors.IsVersion())
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
    /// Returns the process exit code. Output names tables, rows and counts only: never the key, a
    /// connection string, cipher text, or exception text.
    /// </summary>
    private static int ReencryptConnectionStrings(Options runOptions)
    {
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

            IReadOnlyList<ConnectionStringReencryptionCounts> counts = reencryption
                .RunAsync(connection, cancellation.Token)
                .GetAwaiter()
                .GetResult();

            foreach (ConnectionStringReencryptionCounts tableCounts in counts)
            {
                Console.WriteLine(
                    $"{tableCounts.Table}: {tableCounts.Converted} converted, {tableCounts.AlreadyCurrent} already current."
                );
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
            Console.WriteLine("Connection string re-encryption was cancelled. No changes were made.");
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"Connection string re-encryption failed ({ex.GetType().Name}). The pass runs in one "
                    + "transaction and a rerun rewrites only values still in the legacy format, so it is safe to rerun."
            );
        }
        finally
        {
            Console.CancelKeyPress -= cancelOnInterrupt;
        }

        return 1;
    }

    private static void HandleResult(DatabaseDeployResult result)
    {
        if (result is DatabaseDeployResult.DatabaseDeployFailure failure)
        {
            Console.WriteLine($"Failed to deploy database: {failure.Error.Message}");
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
