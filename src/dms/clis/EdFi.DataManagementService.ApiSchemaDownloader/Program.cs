// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.


using CommandLine;
using EdFi.DataManagementService.ApiSchemaDownloader;
using EdFi.DataManagementService.ApiSchemaDownloader.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

var serviceCollection = new ServiceCollection();
ConfigureServices(serviceCollection);
var serviceProvider = serviceCollection.BuildServiceProvider();

var logger = serviceProvider.GetRequiredService<ILogger<Program>>();
var downloader = serviceProvider.GetRequiredService<IApiSchemaDownloader>();

try
{
    // Parse command-line arguments
    var result = Parser.Default.ParseArguments<CommandLineOverrides>(args);

    // Handle parsing errors
    result.WithNotParsed(errors =>
    {
        logger.LogError("Error parsing command-line arguments. Please provide valid parameters.");
        Environment.Exit(1);
    });

    // Execute program logic if parsing is successful
    await result.WithParsedAsync(async options =>
    {
        // Validate required parameters
        if (string.IsNullOrWhiteSpace(options.PackageId))
        {
            logger.LogCritical("Error: packageId is required.");
        }

        if (string.IsNullOrWhiteSpace(options.ApiSchemaFolder))
        {
            logger.LogCritical("Error: apiSchemaFolder is required.");
        }

        string packageId = options.PackageId;
        string? packageVersion = options.PackageVersion;
        string feedUrl = options.FeedUrl;

        // Output directory for the downloaded package and extracted files
        string outputDir = Path.Combine(options.ApiSchemaFolder, "Plugin/" + packageId);
        Directory.CreateDirectory(outputDir);

        // Download the package
        string packagePath = await downloader.DownloadNuGetPackageAsync(
            packageId,
            packageVersion,
            feedUrl,
            outputDir
        );
        Console.WriteLine($"Package downloaded to: {packagePath}");
        logger.LogInformation("Package downloaded to: {PackagePath}", packagePath);


        // Extract the API schema
        downloader.ExtractApiSchemaJsonFromAssembly(packageId, packagePath, options.ApiSchemaFolder);
        Console.WriteLine($"ApiSchema.json extracted to folder: {options.ApiSchemaFolder}");
        logger.LogInformation(
            "ApiSchema.json extracted to folder: {ApiSchemaFolder}",
            options.ApiSchemaFolder
        );
    });

    return 0;
}
catch (Exception ex)
{
    logger.LogCritical(ex, "An error occurred while generating the OpenAPI spec.");
    return 1;
}

static void ConfigureServices(IServiceCollection services)
{
    var logConfiguration = new LoggerConfiguration().MinimumLevel.Debug();

    if (Console.IsOutputRedirected)
    {
        logConfiguration.WriteTo.File("logs/ApiSchemaDownloader.log", rollingInterval: RollingInterval.Day);
    }
    else
    {
        logConfiguration.WriteTo.Console();
        logConfiguration.WriteTo.File("logs/ApiSchemaDownloader.log", rollingInterval: RollingInterval.Day);
    }

    Log.Logger = logConfiguration.CreateLogger();

    services.AddLogging(config =>
    {
        config.ClearProviders();
        config.AddSerilog();
    });

    services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));

    services.AddSingleton<IApiSchemaDownloader, ApiSchemaDownloader>();
}
