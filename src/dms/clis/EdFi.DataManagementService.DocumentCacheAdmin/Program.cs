// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.CommandLine;
using EdFi.DataManagementService.DocumentCacheAdmin;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;

var verbose = Array.Exists(args, a => a is "--verbose" or "-v");

using var processCancellationSource = new CancellationTokenSource();
ConsoleCancelEventHandler cancelKeyPressHandler = (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    processCancellationSource.Cancel();
};
Console.CancelKeyPress += cancelKeyPressHandler;

try
{
    RootCommand rootCommand = DocumentCacheAdminCommandSurface.CreateRootCommand();
    var parseResult = rootCommand.Parse(args);

    if (parseResult.Errors.Count > 0)
    {
        if (IsJsonOutputRequested(args))
        {
            foreach (var parseError in parseResult.Errors)
            {
                await Console.Error.WriteLineAsync(
                    DocumentCacheAdminOutput.SanitizeDiagnostic(parseError.Message)
                );
            }

            return DocumentCacheAdminExitCodes.ArgumentError;
        }

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
        await Console.Error.WriteLineAsync(DocumentCacheAdminOutput.SanitizeDiagnostic(targetFailure));
        return DocumentCacheAdminExitCodes.ArgumentError;
    }

    DocumentCacheAdminInvocationTarget validInvocationTarget =
        invocationTarget
        ?? throw new InvalidOperationException("Invocation target parser succeeded without a target.");

    IConfigurationRoot configuration;
    try
    {
        configuration = DocumentCacheAdminConfiguration.Build(parseResult);
    }
    catch (Exception exception) when (IsConfigurationBuildFailure(exception))
    {
        await WriteConfigurationFailureAsync(exception);
        return DocumentCacheAdminExitCodes.ConfigurationError;
    }

    var serviceCollection = new ServiceCollection();
    try
    {
        ConfigureServices(serviceCollection, configuration, verbose, validInvocationTarget.TargetKey);
    }
    catch (Exception exception) when (IsServiceConfigurationFailure(exception))
    {
        await WriteConfigurationFailureAsync(exception);
        return DocumentCacheAdminExitCodes.ConfigurationError;
    }

    await using ServiceProvider serviceProvider = serviceCollection.BuildServiceProvider();

    try
    {
        await DocumentCacheAdminRuntimeInitializer.InitializeAsync(
            serviceProvider,
            processCancellationSource.Token
        );
    }
    catch (Exception exception) when (IsServiceConfigurationFailure(exception))
    {
        await WriteConfigurationFailureAsync(exception);
        return DocumentCacheAdminExitCodes.ConfigurationError;
    }

    int exitCode = await DocumentCacheAdminCommandExecutor.ExecuteAsync(
        parseResult,
        validInvocationTarget,
        serviceProvider,
        Console.Out,
        Console.Error,
        processCancellationSource.Token,
        Console.In
    );
    return exitCode;
}
catch (OperationCanceledException)
{
    await Console.Error.WriteLineAsync(
        "DocumentCache administration was cancelled before a shared result could be produced."
    );
    return DocumentCacheAdminExitCodes.FailedNoMutation;
}
catch (Exception exception)
{
    await Console.Error.WriteLineAsync(
        $"Unexpected DocumentCache administration CLI failure: {DocumentCacheAdminOutput.SanitizeDiagnostic(exception.Message)}"
    );
    return DocumentCacheAdminExitCodes.UnexpectedFailure;
}
finally
{
    Console.CancelKeyPress -= cancelKeyPressHandler;
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
    DocumentCacheAdminLogSanitizingTextFormatter logFormatter =
        DocumentCacheAdminLogSanitizingTextFormatter.Instance;

    if (enableVerbose)
    {
        try
        {
            var logDirectory = Path.Combine(Directory.GetCurrentDirectory(), "logs");
            Directory.CreateDirectory(logDirectory);
            logConfiguration.WriteTo.File(
                logFormatter,
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

        logConfiguration.WriteTo.Console(logFormatter, standardErrorFromLevel: LogEventLevel.Verbose);
    }

    Log.Logger = logConfiguration.CreateLogger();

    services.AddDocumentCacheAdminRuntimeServices(configuration, Log.Logger, invocationTarget);

    services.AddLogging(loggingBuilder =>
    {
        loggingBuilder.ClearProviders();
        loggingBuilder.AddSerilog();
    });
    services.TryAddSingleton<IDocumentCacheAdminCliTelemetry, DocumentCacheAdminCliTelemetry>();
}

static bool IsConfigurationBuildFailure(Exception exception) =>
    exception
        is OptionsValidationException
            or FileNotFoundException
            or DirectoryNotFoundException
            or InvalidOperationException
            or InvalidDataException
            or IOException
            or UnauthorizedAccessException
            or FormatException
            or ArgumentException;

static bool IsServiceConfigurationFailure(Exception exception) =>
    exception
        is OptionsValidationException
            or InvalidOperationException
            or ArgumentException
            or FormatException;

static Task WriteConfigurationFailureAsync(Exception exception) =>
    Console.Error.WriteLineAsync(
        $"DocumentCache configuration error: {DocumentCacheAdminOutput.SanitizeDiagnostic(exception.Message)}"
    );

static bool IsJsonOutputRequested(string[] arguments) =>
    Array.Exists(
        arguments,
        argument =>
            string.Equals(argument, DocumentCacheAdminCommandSurface.JsonOptionName, StringComparison.Ordinal)
    );
