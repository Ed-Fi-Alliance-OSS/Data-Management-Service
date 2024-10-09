// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Tests.E2E.Management;
using Reqnroll;

namespace EdFi.DataManagementService.Tests.E2E.Hooks;

[Binding]
public static class SetupHooks
{
    private static ContainerSetupBase? _containerSetup;

    [BeforeTestRun]
    public static async Task BeforeTestRun()
    {
        if (AppSettings.UseTestContainers)
        {
            if (AppSettings.OpenSearchEnabled)
            {
                _containerSetup = new OpenSearchContainerSetup();
            }
            else
            {
                _containerSetup = new ContainerSetup();
            }

            await _containerSetup.StartContainers();
        }
    }

    [BeforeFeature]
    public static async Task BeforeFeature(PlaywrightContext context, TestLogger logger)
    {
        try
        {
            if (AppSettings.UseTestContainers)
            {
                logger.log.Debug("Using TestContainers to set environment");
                context.ApiUrl = _containerSetup!.ApiUrl();
            }
            else
            {
                logger.log.Debug("Using local environment, verify that it's correctly set.");
            }

            await context.InitializeApiContext();
        }
        catch (Exception exception)
        {
            logger.log.Error($"Unable to configure environment\nError starting API: {exception}");
        }
    }

    [AfterFeature]
    public static async Task AfterFeature(TestLogger logger)
    {
        if (AppSettings.UseTestContainers)
        {
            await _containerSetup!.ApiLogs(logger);
            await _containerSetup!.ResetData();
        }
    }

    [AfterTestRun]
    public static void AfterTestRun(PlaywrightContext context)
    {
        context.Dispose();
    }
}
