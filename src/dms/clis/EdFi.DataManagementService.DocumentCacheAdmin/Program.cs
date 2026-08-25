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

try
{
    RootCommand rootCommand = DocumentCacheAdminCommandSurface.CreateRootCommand();
    var parseResult = rootCommand.Parse(args);

    if (parseResult.Errors.Count > 0)
    {
        await parseResult.InvokeAsync();
        return DocumentCacheAdminExitCodes.ArgumentError;
    }

    if (DocumentCacheAdminCommandSurface.ShouldInvokeWithoutRuntime(parseResult))
    {
        return await parseResult.InvokeAsync();
    }

    if (
        !DocumentCacheAdminInvocationTargetParser.TryParse(
            parseResult,
            Console.In,
            out DocumentCacheAdminInvocationTarget? invocationTarget,
            out string? targetFailure
        )
    )
    {
        await Console.Error.WriteLineAsync(targetFailure);
        return DocumentCacheAdminExitCodes.ArgumentError;
    }

    DocumentCacheAdminInvocationTarget validInvocationTarget =
        invocationTarget
        ?? throw new InvalidOperationException("Invocation target parser succeeded without a target.");

    IConfigurationRoot configuration = DocumentCacheAdminConfiguration.Build(
        parseResult,
        validInvocationTarget.TargetKey
    );

    var serviceCollection = new ServiceCollection();
    ConfigureServices(serviceCollection, configuration, verbose, validInvocationTarget.TargetKey);
    await using ServiceProvider serviceProvider = serviceCollection.BuildServiceProvider();

    int exitCode = await parseResult.InvokeAsync();
    return exitCode;
}
finally
{
    await Log.CloseAndFlushAsync();
}

void ConfigureServices(
    IServiceCollection services,
    IConfiguration configuration,
    bool enableVerbose,
    EdFi.DataManagementService.Core.Configuration.DocumentCacheTargetKey invocationTarget
)
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
        services.AddDocumentCacheAdminRuntimeServices(configuration, Log.Logger, invocationTarget);
    }

    services.AddLogging(loggingBuilder =>
    {
        loggingBuilder.ClearProviders();
        loggingBuilder.AddSerilog();
    });
}
