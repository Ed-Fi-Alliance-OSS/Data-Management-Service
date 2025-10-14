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

            // Manual argument parsing (simple, extend as needed)
            string? input = null;
            string? output = null;
            string provider = "all";
            bool extensions = false;
            bool skipUnionViews = false;
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--input":
                        if (i + 1 < args.Length)
                        {
                            input = args[++i];
                        }
                        break;
                    case "--output":
                        if (i + 1 < args.Length)
                        {
                            output = args[++i];
                        }
                        break;
                    case "--provider":
                    case "--db":
                        if (i + 1 < args.Length)
                        {
                            provider = args[++i];
                        }
                        break;
                    case "--extensions":
                        extensions = true;
                        break;
                    case "--skip-union-views":
                        skipUnionViews = true;
                        break;
                }
            }

            if (string.IsNullOrWhiteSpace(input) || string.IsNullOrWhiteSpace(output))
            {
                logger.LogError("--input and --output are required (via command line, env, or appsettings.json)");
                return 1;
            }

            try
            {
                logger.LogInformation("Starting DDL generation. Input: {Input}, Output: {Output}, Provider: {Provider}, Extensions: {Extensions}, SkipUnionViews: {SkipUnionViews}",
                    input, output, provider, extensions, skipUnionViews);

                var loader = serviceProvider.GetRequiredService<ApiSchemaLoader>();
                var strategies = serviceProvider.GetServices<IDdlGeneratorStrategy>().ToList();
                var apiSchema = loader.Load(input);

                if (provider == "pgsql" || provider == "all")
                {
                    logger.LogInformation("Generating PostgreSQL DDL...");
                    var pgsql = strategies.FirstOrDefault(s => s.GetType().Name.Contains("Pgsql"));
                    pgsql?.GenerateDdl(apiSchema, output, extensions, skipUnionViews);
                    logger.LogInformation("PostgreSQL DDL generation completed");
                }

                if (provider == "mssql" || provider == "all")
                {
                    logger.LogInformation("Generating SQL Server DDL...");
                    var mssql = strategies.FirstOrDefault(s => s.GetType().Name.Contains("Mssql"));
                    mssql?.GenerateDdl(apiSchema, output, extensions, skipUnionViews);
                    logger.LogInformation("SQL Server DDL generation completed");
                }

                logger.LogInformation("DDL generation completed successfully. Output: {Output}", output);
                return 0;
            }
            catch (Exception ex)
            {
                logger.LogCritical(ex, "An error occurred while generating DDL");
                return 2;
            }
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
