// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.SchemaGenerator.Abstractions;
using EdFi.DataManagementService.SchemaGenerator.Cli.Services;
using EdFi.DataManagementService.SchemaGenerator.Mssql;
using EdFi.DataManagementService.SchemaGenerator.Pgsql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

namespace EdFi.DataManagementService.SchemaGenerator.Cli
{
    /// <summary>
    /// Entry point for the Ed-Fi DMS Schema Generator CLI.
    /// </summary>
    public class Program
    {
        /// <summary>
        /// Main entry point. Configures DI, parses arguments, and invokes DDL generation strategies.
        /// </summary>
        /// <param name="args">Command-line arguments.</param>
        /// <returns>Exit code.</returns>
        public static async Task<int> Main(string[] args)
        {
            // Check for help flag first
            if (
                args.Length > 0
                && (args[0] == "/h" || args[0] == "--help" || args[0] == "-h" || args[0] == "/?")
            )
            {
                DisplayHelp();
                return 0;
            }

            // Build configuration: CommandLine > Environment > appsettings.json
            var configBuilder = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                .AddEnvironmentVariables();
            var config = configBuilder.Build();

            // Setup DI
            var services = new ServiceCollection();
            ConfigureServices(services, config);
            var serviceProvider = services.BuildServiceProvider();

            var logger = serviceProvider.GetRequiredService<ILogger<Program>>();

            // Read configuration with command line override precedence
            string? inputFilePath = config.GetValue<string>("SchemaGenerator:InputFilePath");
            string? outputDirectory = config.GetValue<string>("SchemaGenerator:OutputDirectory");
            string databaseProvider = config.GetValue<string>("SchemaGenerator:DatabaseProvider") ?? "all";
            bool includeExtensions = config.GetValue<bool>("SchemaGenerator:IncludeExtensions");
            bool skipUnionViews = config.GetValue<bool>("SchemaGenerator:SkipUnionViews");
            bool usePrefixedTableNames = config.GetValue<bool>("SchemaGenerator:UsePrefixedTableNames", true);
            bool skipDescriptorFk = config.GetValue<bool>("SchemaGenerator:SkipDescriptorFk", false);

            string? schemaUrl = null;

            // Parse command-line arguments (overrides configuration)
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--input":
                    case "-i":
                        if (i + 1 < args.Length)
                        {
                            inputFilePath = args[++i]?.Trim();
                            if (string.IsNullOrWhiteSpace(inputFilePath))
                            {
                                inputFilePath = null; // Treat whitespace-only as null
                            }
                        }
                        break;
                    case "--output":
                    case "-o":
                        if (i + 1 < args.Length)
                        {
                            outputDirectory = args[++i]?.Trim();
                            if (string.IsNullOrWhiteSpace(outputDirectory))
                            {
                                outputDirectory = null; // Treat whitespace-only as null
                            }
                        }
                        break;
                    case "--provider":
                    case "--database":
                    case "-p":
                        if (i + 1 < args.Length)
                        {
                            databaseProvider = args[++i];
                        }
                        break;
                    case "--url":
                    case "-u":
                        if (i + 1 < args.Length)
                        {
                            schemaUrl = args[++i];
                        }
                        break;
                    case "--extensions":
                    case "-e":
                        includeExtensions = true;
                        break;
                    case "--skip-union-views":
                    case "-s":
                        skipUnionViews = true;
                        break;
                    case "--use-schemas":
                    case "--separate-schemas":
                        usePrefixedTableNames = false;
                        break;
                    case "--use-prefixed-names":
                    case "--prefixed-tables":
                        usePrefixedTableNames = true;
                        break;
                }
            }

            if (!string.IsNullOrWhiteSpace(schemaUrl) && !string.IsNullOrWhiteSpace(inputFilePath))
            {
                logger.LogError("Both --input and --url cannot be specified. Use one or the other.");
                return 1;
            }

            // Early validation: check if both input sources and output directory are provided
            if (
                (string.IsNullOrWhiteSpace(inputFilePath) && string.IsNullOrWhiteSpace(schemaUrl))
                || string.IsNullOrWhiteSpace(outputDirectory)
            )
            {
                logger.LogError(
                    "InputFilePath and OutputDirectory are required. Use --help for usage information."
                );
                return 1;
            }

            if (!string.IsNullOrWhiteSpace(inputFilePath) && !string.IsNullOrWhiteSpace(schemaUrl))
            {
                logger.LogError("Both --input and --url cannot be specified. Use one or the other.");
                return 1;
            }

            if (string.IsNullOrWhiteSpace(inputFilePath) && !string.IsNullOrWhiteSpace(schemaUrl))
            {
                // Create a temporary file path for URL downloads
                string executableDirectory = AppContext.BaseDirectory;
                string tempFileName = $"{DateTime.Now:yyyyMMdd_HHmmss}_downloaded_apischema.json";
                inputFilePath = Path.Combine(executableDirectory, tempFileName);
                logger.LogInformation("Creating temporary file for URL download: {TempFile}", inputFilePath);
            }

            try
            {
                if (!string.IsNullOrWhiteSpace(schemaUrl))
                {
                    try
                    {
                        logger.LogInformation("Fetching schema from URL: {Url}", schemaUrl);
                        using var httpClient = new HttpClient();
                        var schemaJson = await httpClient.GetStringAsync(schemaUrl);

                        // Save to the temporary file
                        if (inputFilePath is not null)
                        {
                            await File.WriteAllTextAsync(inputFilePath, schemaJson);
                        }
                        else
                        {
                            logger.LogError("Input file path is null. Cannot write schema JSON to file.");
                            throw new ArgumentNullException(
                                nameof(inputFilePath),
                                "Input file path cannot be null."
                            );
                        }
                        await File.WriteAllTextAsync(inputFilePath, schemaJson);
                        logger.LogInformation(
                            "Schema downloaded and saved to temporary file: {TempFile}",
                            inputFilePath
                        );
                    }
                    catch (Exception ex)
                    {
                        logger.LogCritical(ex, "Failed to fetch schema from URL: {Url}", schemaUrl);
                        return 2;
                    }
                }

                logger.LogInformation(
                    "Starting DDL generation. Input: {Input}, Output: {Output}, Provider: {Provider}, Extensions: {Extensions}, SkipUnionViews: {SkipUnionViews}, UsePrefixedTableNames: {UsePrefixedTableNames}",
                    inputFilePath,
                    outputDirectory,
                    databaseProvider,
                    includeExtensions,
                    skipUnionViews,
                    usePrefixedTableNames
                );

                var loader = serviceProvider.GetRequiredService<ApiSchemaLoader>();
                var strategies = serviceProvider.GetServices<IDdlGeneratorStrategy>().ToList();

                ApiSchema apiSchema;
                try
                {
                    // At this point inputFilePath should not be null due to earlier validation, but add safety check
                    if (string.IsNullOrWhiteSpace(inputFilePath))
                    {
                        logger.LogError("Input file path is null or empty");
                        return 1;
                    }

                    apiSchema = loader.Load(inputFilePath!);
                }
                catch (FileNotFoundException)
                {
                    logger.LogError("ApiSchema file not found: {InputFile}", inputFilePath);
                    return 2;
                }
                catch (JsonException ex)
                {
                    logger.LogError(ex, "Invalid JSON in ApiSchema file: {InputFile}", inputFilePath);
                    return 2;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to load ApiSchema file: {InputFile}", inputFilePath);
                    return 2;
                }

                var options = new DdlGenerationOptions
                {
                    IncludeExtensions = includeExtensions,
                    SkipUnionViews = skipUnionViews,
                    UsePrefixedTableNames = usePrefixedTableNames,
                };

                if (databaseProvider is "pgsql" or "postgresql" or "all")
                {
                    logger.LogInformation("Generating PostgreSQL DDL...");
                    var pgsql = strategies.FirstOrDefault(s => s.GetType().Name.Contains("Pgsql"));

                    if (pgsql != null)
                    {
                        pgsql.GenerateDdl(apiSchema, outputDirectory, options);
                    }

                    logger.LogInformation("PostgreSQL DDL generation completed");
                }

                if (databaseProvider is "mssql" or "all")
                {
                    logger.LogInformation("Generating SQL Server DDL...");
                    var mssql = strategies.FirstOrDefault(s => s.GetType().Name.Contains("Mssql"));

                    if (mssql != null)
                    {
                        mssql.GenerateDdl(apiSchema, outputDirectory, options);
                    }

                    logger.LogInformation("SQL Server DDL generation completed");
                }

                logger.LogInformation(
                    "DDL generation completed successfully. Output: {Output}",
                    outputDirectory
                );
                return 0;
            }
            finally
            {
                // Clean up the temporary file if it was created
                if (!string.IsNullOrWhiteSpace(schemaUrl) && File.Exists(inputFilePath))
                {
                    try
                    {
                        File.Delete(inputFilePath);
                        logger.LogInformation("Temporary file deleted: {TempFile}", inputFilePath);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Failed to delete temporary file: {TempFile}", inputFilePath);
                    }
                }
            }
        }

        /// <summary>
        /// Displays help information for the CLI.
        /// </summary>
        private static void DisplayHelp()
        {
            Console.WriteLine();
            Console.WriteLine("Ed-Fi Data Management Service - Schema Generator CLI");
            Console.WriteLine(
                "  Generates database DDL (Data Definition Language) scripts from Ed-Fi API schema files."
            );
            Console.WriteLine();
            Console.WriteLine("Usage:");
            Console.WriteLine(
                "  --input, -i <path>           (Required) Path to the input API schema JSON file"
            );
            Console.WriteLine(
                "  --output, -o <directory>     (Required) Directory where DDL scripts will be generated"
            );
            Console.WriteLine(
                "  --provider, --database, -p <provider>  Database provider: 'pgsql'/'postgresql', 'mssql', or 'all' (default: all)"
            );
            Console.WriteLine("  --url, -u <url>              URL to fetch the API schema JSON file");
            Console.WriteLine(
                "  --skip-union-views, -s       Skip generation of union views for polymorphic references"
            );
            Console.WriteLine(
                "  --use-schemas, --separate-schemas       Generate separate database schemas (edfi, tpdm, etc.)"
            );
            Console.WriteLine(
                "  --use-prefixed-names, --prefixed-tables Use prefixed table names in dms schema (default)"
            );
            Console.WriteLine();
        }

        /// <summary>
        /// Configures dependency injection services.
        /// </summary>
        private static void ConfigureServices(IServiceCollection services, IConfiguration config)
        {
            // Read log file path from configuration with default value
            var logFilePath = config.GetValue<string>("Logging:LogFilePath") ?? "logs/SchemaGenerator.log";
            var minimumLevel = config.GetValue<string>("Logging:MinimumLevel") ?? "Information";

            var logConfiguration = new LoggerConfiguration();

            // Set minimum level from configuration
            if (minimumLevel.Equals("Debug", StringComparison.OrdinalIgnoreCase))
            {
                logConfiguration.MinimumLevel.Debug();
            }
            else if (minimumLevel.Equals("Warning", StringComparison.OrdinalIgnoreCase))
            {
                logConfiguration.MinimumLevel.Warning();
            }
            else if (minimumLevel.Equals("Error", StringComparison.OrdinalIgnoreCase))
            {
                logConfiguration.MinimumLevel.Error();
            }
            else
            {
                logConfiguration.MinimumLevel.Information();
            }

            if (Console.IsOutputRedirected)
            {
                logConfiguration.WriteTo.File(logFilePath, rollingInterval: RollingInterval.Day);
            }
            else
            {
                logConfiguration.WriteTo.Console();
                logConfiguration.WriteTo.File(logFilePath, rollingInterval: RollingInterval.Day);
            }

            Log.Logger = logConfiguration.CreateLogger();

            services.AddLogging(loggingConfig =>
            {
                loggingConfig.ClearProviders();
                loggingConfig.AddSerilog();
            });

            services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
            services.AddSingleton<IConfiguration>(config);
            services.AddTransient<ApiSchemaLoader>();
            services.AddTransient<IDdlGeneratorStrategy, PgsqlDdlGeneratorStrategy>();
            services.AddTransient<IDdlGeneratorStrategy, MssqlDdlGeneratorStrategy>();
        }
    }
}
