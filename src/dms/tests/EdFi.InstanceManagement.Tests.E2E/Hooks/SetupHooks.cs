// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Reqnroll;
using Serilog;
using Serilog.Extensions.Logging;

namespace EdFi.InstanceManagement.Tests.E2E.Hooks;

[Binding]
public class SetupHooks
{
    private static ILogger<SetupHooks>? _logger;

    private SetupHooks()
    {
        // Private constructor to satisfy SonarAnalyzer
    }

    [BeforeTestRun]
    public static void BeforeTestRun()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .Build();

        Log.Logger = new LoggerConfiguration().ReadFrom.Configuration(configuration).CreateLogger();

        var loggerFactory = new SerilogLoggerFactory(Log.Logger);
        _logger = loggerFactory.CreateLogger<SetupHooks>();

        _logger.LogInformation("Starting Instance Management E2E Tests");
        _logger.LogInformation("Query Handler: {QueryHandler}", AppSettings.QueryHandler);
        _logger.LogInformation(
            "Authentication Service: {AuthenticationService}",
            AppSettings.AuthenticationService
        );
    }

    [AfterTestRun]
    public static void AfterTestRun()
    {
        _logger?.LogInformation("Instance Management E2E Tests Complete");
        Log.CloseAndFlush();
    }
}
