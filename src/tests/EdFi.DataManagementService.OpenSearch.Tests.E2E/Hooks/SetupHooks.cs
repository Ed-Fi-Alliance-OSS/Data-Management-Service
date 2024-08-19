// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Tests.E2E.Management;
using Microsoft.Extensions.Configuration;
using Reqnroll;

namespace EdFi.DataManagementService.Tests.E2E.Hooks;

[Binding]
public class SetupHooks
{
    private static IConfiguration? _configuration;

    [BeforeFeature]
    public static async Task BeforeFeature(PlaywrightContext context, TestLogger logger)
    {
        try
        {
            _configuration ??= new ConfigurationBuilder()
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .Build();

            bool.TryParse(_configuration["useTestContainers"], out bool useTestContainers);

            if (useTestContainers)
            {
                logger.log.Debug("Using TestContainers to set environment");
                context.ApiUrl = await ContainerSetup.SetupDataManagement();
            }
            else
            {
                logger.log.Debug("Using local environment, verify that it's correctly set.");
            }

            await context.CreateApiContext();
        }
        catch (Exception exception)
        {
            logger.log.Error($"Unable to configure environment\nError starting API: {exception}");
        }
    }

    [AfterFeature]
    public static async Task AfterFeature(PlaywrightContext context, TestLogger logger)
    {
        if (ContainerSetup.ApiContainer == null || ContainerSetup.DbContainer == null)
        {
            return;
        }

        var logs = await ContainerSetup.ApiContainer!.GetLogsAsync();
        logger.log.Information($"{Environment.NewLine}API stdout logs:{Environment.NewLine}{logs.Stdout}");

        if (!string.IsNullOrEmpty(logs.Stderr))
        {
            logger.log.Error($"{Environment.NewLine}API stderr logs:{Environment.NewLine}{logs.Stderr}");
        }

        await ContainerSetup.DbContainer!.DisposeAsync();
        await ContainerSetup.ApiContainer!.DisposeAsync();
    }

    [AfterTestRun]
    public static void AfterTestRun(PlaywrightContext context, TestLogger logger)
    {
        context.Dispose();
    }
}
