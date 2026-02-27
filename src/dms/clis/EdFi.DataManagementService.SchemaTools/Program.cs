// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.CommandLine;
using EdFi.DataManagementService.Core.Startup;
using EdFi.DataManagementService.SchemaTools.Bridge;
using EdFi.DataManagementService.SchemaTools.Commands;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

var serviceCollection = new ServiceCollection();
ConfigureServices(serviceCollection);
await using var serviceProvider = serviceCollection.BuildServiceProvider();

try
{
    var logger = serviceProvider.GetRequiredService<ILogger<Program>>();
    var fileLoader = serviceProvider.GetRequiredService<IApiSchemaFileLoader>();
    var hashProvider = serviceProvider.GetRequiredService<IEffectiveSchemaHashProvider>();
    var schemaSetBuilder = serviceProvider.GetRequiredService<EffectiveSchemaSetBuilder>();

    var rootCommand = new RootCommand("Ed-Fi DMS schema tool for hashing and DDL generation");

    // hash subcommand
    rootCommand.Subcommands.Add(HashCommand.Create(logger, fileLoader, hashProvider));

    // ddl command group
    var ddlCommand = new Command("ddl", "DDL generation commands");
    ddlCommand.Subcommands.Add(DdlEmitCommand.Create(logger, fileLoader, schemaSetBuilder));
    rootCommand.Subcommands.Add(ddlCommand);

    var parseResult = rootCommand.Parse(args);
    return await parseResult.InvokeAsync();
}
finally
{
    await Log.CloseAndFlushAsync();
}

void ConfigureServices(IServiceCollection services)
{
    var logConfiguration = new LoggerConfiguration()
        .MinimumLevel.Information()
        .WriteTo.File("logs/dms-schema.log", rollingInterval: RollingInterval.Day);

    if (Console.IsOutputRedirected)
    {
        // When stdout is piped/redirected (e.g. CI capture, `> file`), route all
        // diagnostic logs to stderr so they don't corrupt the structured stdout output.
        logConfiguration.WriteTo.Console(standardErrorFromLevel: LogEventLevel.Verbose);
    }
    else
    {
        logConfiguration.WriteTo.Console();
    }

    Log.Logger = logConfiguration.CreateLogger();

    services.AddLogging(config =>
    {
        config.ClearProviders();
        config.AddSerilog();
    });

    services.AddSingleton<IApiSchemaInputNormalizer, ApiSchemaInputNormalizer>();
    services.AddSingleton<IApiSchemaFileLoader, ApiSchemaFileLoader>();
    services.AddSingleton<IEffectiveSchemaHashProvider, EffectiveSchemaHashProvider>();
    services.AddSingleton<IResourceKeySeedProvider, ResourceKeySeedProvider>();
    services.AddSingleton<EffectiveSchemaSetBuilder>();
}
