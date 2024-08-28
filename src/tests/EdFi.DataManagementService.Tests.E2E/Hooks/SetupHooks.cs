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
    private static IConfiguration? _configuration = new ConfigurationBuilder()
        .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
        .Build();

    [BeforeTestRun]
    public static async Task BeforeTestRun(PlaywrightContext context, TestLogger logger)
    {
        bool.TryParse(_configuration!["useTestContainers"], out bool useTestContainers);
        bool.TryParse(_configuration!["OpenSearchEnabled"], out bool OpenSearchEnabled);
        if (useTestContainers)
            if (OpenSearchEnabled)
            {
                await OpenSearchContainerSetup.CreateContainers();
            }
            else
            {
                await ContainerSetup.CreateContainers();
            }
    }

    [BeforeFeature]
    public static async Task BeforeFeature(PlaywrightContext context, TestLogger logger)
    {
        try
        {
            bool.TryParse(_configuration!["useTestContainers"], out bool useTestContainers);
            bool.TryParse(_configuration!["OpenSearchEnabled"], out bool OpenSearchEnabled);

            if (useTestContainers)
            {
                logger.log.Debug("Using TestContainers to set environment");
                if (OpenSearchEnabled)
                {
                    context.ApiUrl = await OpenSearchContainerSetup.StartContainers(context, logger);
                }
                else
                {
                    context.ApiUrl = await ContainerSetup.StartContainers(context, logger);
                }
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
        bool.TryParse(_configuration!["OpenSearchEnabled"], out bool OpenSearchEnabled);

        if (OpenSearchEnabled)
        {
            await OpenSearchContainerSetup.ResetContainers(context, logger);
        }
        else
        {
            await ContainerSetup.ResetContainers(context, logger);
        }
    }

    [AfterTestRun]
    public static void AfterTestRun(PlaywrightContext context, TestLogger logger)
    {
        context.Dispose();
    }
}
