// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.CommandLine;
using EdFi.DataManagementService.DocumentCacheAdmin;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

var verbose = Array.Exists(args, a => a is "--verbose" or "-v");
IConfigurationRoot configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();

var serviceCollection = new ServiceCollection();
ConfigureServices(serviceCollection, configuration, verbose);
await using ServiceProvider serviceProvider = serviceCollection.BuildServiceProvider();

try
{
    var rootCommand = new RootCommand("Ed-Fi DMS DocumentCache administration tool");

    var verboseOption = new Option<bool>("--verbose", "-v")
    {
        Description = "Enable verbose (debug-level) logging",
        Recursive = true,
    };
    rootCommand.Options.Add(verboseOption);

    var parseResult = rootCommand.Parse(args);
    return await parseResult.InvokeAsync();
}
finally
{
    await Log.CloseAndFlushAsync();
}

void ConfigureServices(IServiceCollection services, IConfiguration configuration, bool enableVerbose)
{
    var logConfiguration = new LoggerConfiguration().MinimumLevel.Is(
        enableVerbose ? LogEventLevel.Debug : LogEventLevel.Information
    );

    if (enableVerbose)
    {
        try
        {
            var logDirectory = Path.Combine(Directory.GetCurrentDirectory(), "logs");
            Directory.CreateDirectory(logDirectory);
            logConfiguration.WriteTo.File(
                Path.Combine(logDirectory, $"{DocumentCacheAdminCliConstants.ToolCommandName}.log"),
                rollingInterval: RollingInterval.Day
            );
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(
                $"Warning: Unable to create log file, continuing with console-only logging ({exception.GetType().Name})."
            );
        }
    }

    if (Console.IsOutputRedirected)
    {
        logConfiguration.WriteTo.Console(standardErrorFromLevel: LogEventLevel.Verbose);
    }
    else
    {
        logConfiguration.WriteTo.Console();
    }

    Log.Logger = logConfiguration.CreateLogger();

    if (!string.IsNullOrWhiteSpace(configuration.GetSection("AppSettings:Datastore").Value))
    {
        services.AddDocumentCacheAdminRuntimeServices(configuration, Log.Logger);
    }

    services.AddLogging(loggingBuilder =>
    {
        loggingBuilder.ClearProviders();
        loggingBuilder.AddSerilog();
    });
}
