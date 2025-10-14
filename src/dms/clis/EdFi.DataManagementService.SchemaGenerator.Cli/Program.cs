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
            services.AddSingleton<IConfiguration>(config);
            services.AddTransient<ApiSchemaLoader>();
            services.AddTransient<IDdlGeneratorStrategy, PgsqlDdlGeneratorStrategy>();
            services.AddTransient<IDdlGeneratorStrategy, MssqlDdlGeneratorStrategy>();
            var serviceProvider = services.BuildServiceProvider();

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
                Console.Error.WriteLine("--input and --output are required (via command line, env, or appsettings.json)");
                return 1;
            }

            try
            {
                var loader = serviceProvider.GetRequiredService<ApiSchemaLoader>();
                var strategies = serviceProvider.GetServices<IDdlGeneratorStrategy>().ToList();
                var apiSchema = loader.Load(input);

                if (provider == "pgsql" || provider == "all")
                {
                    var pgsql = strategies.FirstOrDefault(s => s.GetType().Name.Contains("Pgsql"));
                    pgsql?.GenerateDdl(apiSchema, output, extensions, skipUnionViews);
                }

                if (provider == "mssql" || provider == "all")
                {
                    var mssql = strategies.FirstOrDefault(s => s.GetType().Name.Contains("Mssql"));
                    mssql?.GenerateDdl(apiSchema, output, extensions, skipUnionViews);
                }

                Console.WriteLine($"DDL generation completed successfully. Output: {output}");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                return 2;
            }
        }
    }
}
