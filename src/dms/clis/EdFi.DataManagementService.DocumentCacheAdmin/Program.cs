// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.CommandLine;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Startup;
using EdFi.DataManagementService.DocumentCacheAdmin;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
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
        configuration = DocumentCacheAdminConfiguration.Build(parseResult, validInvocationTarget.TargetKey);
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
        await InitializeRuntimeSchemaAsync(serviceProvider, processCancellationSource.Token);
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
        processCancellationSource.Token
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

        logConfiguration.WriteTo.Console(standardErrorFromLevel: LogEventLevel.Verbose);
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
        is FileNotFoundException
            or DirectoryNotFoundException
            or InvalidDataException
            or IOException
            or UnauthorizedAccessException
            or FormatException
            or ArgumentException;

static bool IsServiceConfigurationFailure(Exception exception) =>
    exception is InvalidOperationException or ArgumentException or FormatException;

static async Task InitializeRuntimeSchemaAsync(
    ServiceProvider serviceProvider,
    CancellationToken cancellationToken
)
{
    cancellationToken.ThrowIfCancellationRequested();

    var apiSchemaProvider = serviceProvider.GetRequiredService<IApiSchemaProvider>();
    var rawNodes = apiSchemaProvider.GetApiSchemaNodes();
    if (!apiSchemaProvider.IsSchemaValid)
    {
        throw new InvalidOperationException(
            $"API schema validation failed with {apiSchemaProvider.ApiSchemaFailures.Count} error(s)."
        );
    }

    var inputNormalizer = serviceProvider.GetRequiredService<IApiSchemaInputNormalizer>();
    var normalizationResult = inputNormalizer.Normalize(rawNodes);
    var normalizedNodes = normalizationResult switch
    {
        ApiSchemaNormalizationResult.SuccessResult success => success.NormalizedNodes,
        ApiSchemaNormalizationResult.MissingOrMalformedProjectSchemaResult failure =>
            throw new InvalidOperationException(
                $"Schema normalization failed for '{failure.SchemaSource}': {failure.Details}"
            ),
        ApiSchemaNormalizationResult.ApiSchemaVersionMismatchResult failure =>
            throw new InvalidOperationException(
                $"apiSchemaVersion mismatch in '{failure.SchemaSource}': expected '{failure.ExpectedVersion}', got '{failure.ActualVersion}'"
            ),
        ApiSchemaNormalizationResult.ProjectEndpointNameCollisionResult failure =>
            throw new InvalidOperationException(
                $"Duplicate projectEndpointName(s) found: {string.Join("; ", failure.Collisions.Select(c => $"'{c.ProjectEndpointName}' in [{string.Join(", ", c.ConflictingSources)}]"))}"
            ),
        _ => throw new InvalidOperationException("Unknown schema normalization result."),
    };

    var effectiveSchemaSet = serviceProvider
        .GetRequiredService<EffectiveSchemaSetBuilder>()
        .Build(normalizedNodes);
    serviceProvider.GetRequiredService<IEffectiveSchemaSetProvider>().Initialize(effectiveSchemaSet);

    IRuntimeMappingSetCompiler runtimeCompiler =
        serviceProvider.GetServices<IRuntimeMappingSetCompiler>().SingleOrDefault()
        ?? throw new InvalidOperationException("No runtime mapping-set compiler is configured.");
    MappingSetKey mappingSetKey = runtimeCompiler.GetCurrentKey();
    _ = await serviceProvider
        .GetRequiredService<IMappingSetProvider>()
        .GetOrCreateAsync(mappingSetKey, cancellationToken);
}

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
