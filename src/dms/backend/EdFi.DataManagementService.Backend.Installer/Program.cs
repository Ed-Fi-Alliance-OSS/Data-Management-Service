// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using CommandLine;
using CommandLine.Text;

namespace EdFi.DataManagementService.Backend.Installer;

public class Options
{
    [Option(
        'e',
        "engine",
        Required = true,
        HelpText = "The database engine to deploy. Valid values are 'postgresql' and 'mssql'"
    )]
    public string Datastore { get; set; } = string.Empty;

    [Option('c', "connectionString", Required = true, HelpText = "The connection string to the database.")]
    public string ConnectionString { get; set; } = string.Empty;
}

public static class Program
{
    private static ParserResult<Options>? _parseResult;

    public static void Main(string[] args)
    {
        var parser = new Parser(config => config.HelpWriter = null);
        _parseResult = parser.ParseArguments<Options>(args);
        _parseResult
            .WithParsed(runOptions =>
            {
                Console.Error.WriteLine(
                    $"The DMS backend installer no longer provisions {runOptions.Datastore} databases. Use SchemaTools ddl provision for relational schema provisioning."
                );
                Environment.ExitCode = 1;
            })
            .WithNotParsed(_ =>
            {
                DisplayHelp(_parseResult);
            });

        if (Debugger.IsAttached)
        {
            Console.WriteLine("Press enter to continue.");
            Console.ReadLine();
        }
    }

    private static void DisplayHelp<T>(ParserResult<T> parseResult)
    {
        var helpText = HelpText.AutoBuild(
            parseResult,
            h =>
            {
                h.Heading = "Ed-Fi API Backend Installer";
                h.Copyright = string.Empty;
                h.AutoVersion = false;
                return h;
            },
            e => e
        );
        Console.WriteLine(helpText);
    }
}
