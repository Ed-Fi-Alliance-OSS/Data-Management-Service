// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

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
        public static int Main(string[] args)
        {
            // Check for help flag first
            if (args.Length > 0 && (args[0] == "/h" || args[0] == "--help" || args[0] == "-h" || args[0] == "/?"))
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

            // Parse command-line arguments (overrides configuration)
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--input":
                    case "-i":
                        if (i + 1 < args.Length)
                        {
                            inputFilePath = args[++i];
                        }
                        break;
                    case "--output":
                    case "-o":
                        if (i + 1 < args.Length)
                        {
                            outputDirectory = args[++i];
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
                    case "--extensions":
                    case "-e":
                        includeExtensions = true;
                        break;
                    case "--skip-union-views":
                    case "-s":
                        skipUnionViews = true;
                        break;
                }
            }

            if (string.IsNullOrWhiteSpace(inputFilePath) || string.IsNullOrWhiteSpace(outputDirectory))
            {
                logger.LogError("InputFilePath and OutputDirectory are required. Use --help for usage information.");
                Console.Error.WriteLine("\nError: InputFilePath and OutputDirectory are required.");
                Console.Error.WriteLine("Use --help or /h for usage information.");
                return 1;
            }

            try
            {
                logger.LogInformation("Starting DDL generation. Input: {Input}, Output: {Output}, Provider: {Provider}, Extensions: {Extensions}, SkipUnionViews: {SkipUnionViews}",
                    inputFilePath, outputDirectory, databaseProvider, includeExtensions, skipUnionViews);

                var loader = serviceProvider.GetRequiredService<ApiSchemaLoader>();
                var strategies = serviceProvider.GetServices<IDdlGeneratorStrategy>().ToList();
                var apiSchema = loader.Load(inputFilePath);

                if (databaseProvider == "pgsql" || databaseProvider == "all")
                {
                    logger.LogInformation("Generating PostgreSQL DDL...");
                    var pgsql = strategies.FirstOrDefault(s => s.GetType().Name.Contains("Pgsql"));
                    pgsql?.GenerateDdl(apiSchema, outputDirectory, includeExtensions, skipUnionViews);
                    logger.LogInformation("PostgreSQL DDL generation completed");
                }

                if (databaseProvider == "mssql" || databaseProvider == "all")
                {
                    logger.LogInformation("Generating SQL Server DDL...");
                    var mssql = strategies.FirstOrDefault(s => s.GetType().Name.Contains("Mssql"));
                    mssql?.GenerateDdl(apiSchema, outputDirectory, includeExtensions, skipUnionViews);
                    logger.LogInformation("SQL Server DDL generation completed");
                }

                logger.LogInformation("DDL generation completed successfully. Output: {Output}", outputDirectory);
                return 0;
            }
            catch (Exception ex)
            {
                logger.LogCritical(ex, "An error occurred while generating DDL");
                return 2;
            }
        }

        /// <summary>
        /// Displays help information for the CLI.
        /// </summary>
        private static void DisplayHelp()
        {
            Console.WriteLine();
            Console.WriteLine("Ed-Fi Data Management Service - Schema Generator CLI");
            Console.WriteLine("====================================================");
            Console.WriteLine();
            Console.WriteLine("PURPOSE:");
            Console.WriteLine("  Generates database DDL (Data Definition Language) scripts from Ed-Fi API schema files.");
            Console.WriteLine("  Supports PostgreSQL and SQL Server database platforms.");
            Console.WriteLine();
            Console.WriteLine("USAGE:");
            Console.WriteLine("  EdFi.DataManagementService.SchemaGenerator.Cli [options]");
            Console.WriteLine();
            Console.WriteLine("OPTIONS:");
            Console.WriteLine("  --input, -i <path>           (Required) Path to the input API schema JSON file");
            Console.WriteLine("  --output, -o <directory>     (Required) Directory where DDL scripts will be generated");
            Console.WriteLine("  --provider, -p <provider>    Database provider: 'pgsql', 'mssql', or 'all' (default: all)");
            Console.WriteLine("  --extensions, -e             Include extension tables in the generated DDL");
            Console.WriteLine("  --skip-union-views, -s       Skip generation of union views for polymorphic references");
            Console.WriteLine("  --help, -h, /h, /?           Display this help information");
            Console.WriteLine();
            Console.WriteLine("CONFIGURATION:");
            Console.WriteLine("  Parameters can also be configured in appsettings.json:");
            Console.WriteLine("  {");
            Console.WriteLine("    \"SchemaGenerator\": {");
            Console.WriteLine("      \"InputFilePath\": \"path/to/schema.json\",");
            Console.WriteLine("      \"OutputDirectory\": \"path/to/output\",");
            Console.WriteLine("      \"DatabaseProvider\": \"all\",");
            Console.WriteLine("      \"IncludeExtensions\": false,");
            Console.WriteLine("      \"SkipUnionViews\": false");
            Console.WriteLine("    },");
            Console.WriteLine("    \"Logging\": {");
            Console.WriteLine("      \"LogFilePath\": \"logs/SchemaGenerator.log\",");
            Console.WriteLine("      \"MinimumLevel\": \"Information\"");
            Console.WriteLine("    }");
            Console.WriteLine("  }");
            Console.WriteLine();
            Console.WriteLine("  Command-line arguments override appsettings.json values.");
            Console.WriteLine("  Environment variables can also override settings using the format:");
            Console.WriteLine("    SchemaGenerator__InputFilePath, SchemaGenerator__OutputDirectory, etc.");
            Console.WriteLine();
            Console.WriteLine("EXAMPLES:");
            Console.WriteLine("  Generate DDL for all databases:");
            Console.WriteLine("    EdFi.DataManagementService.SchemaGenerator.Cli --input schema.json --output ./ddl");
            Console.WriteLine();
            Console.WriteLine("  Generate only PostgreSQL DDL with extensions:");
            Console.WriteLine("    EdFi.DataManagementService.SchemaGenerator.Cli -i schema.json -o ./ddl -p pgsql -e");
            Console.WriteLine();
            Console.WriteLine("  Generate SQL Server DDL without union views:");
            Console.WriteLine("    EdFi.DataManagementService.SchemaGenerator.Cli -i schema.json -o ./ddl -p mssql -s");
            Console.WriteLine();
            Console.WriteLine("OUTPUT:");
            Console.WriteLine("  PostgreSQL: schema-pgsql.sql");
            Console.WriteLine("  SQL Server: schema-mssql.sql");
            Console.WriteLine("  Log file:   logs/SchemaGenerator.log (configurable)");
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
