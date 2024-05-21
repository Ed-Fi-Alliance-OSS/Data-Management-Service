// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Api.Tests.E2E.Management;
using Microsoft.Extensions.Configuration;
using Reqnroll;

namespace EdFi.DataManagementService.Api.Tests.E2E.Hooks;

[Binding]
public class SetupHooks
{
    private static IConfiguration? _configuration;

    [BeforeTestRun]
    public static async Task BeforeTestRun(
        PlaywrightContext context,
        ContainerSetup containers,
        TestLogger logger
    )
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

                await containers.SetupPostgresBackend();

                context.ApiUrl = await containers.SetupDataManagement();
            }
            else
            {
                logger.log.Debug("Using local environment, verify that it's correctly set.");
            }

            await context.CreateApiContext();
        }
        catch (Exception exception)
        {
            Assert.Fail($"Unable to configure environment\nError starting API: {exception}");
        }
    }

    [AfterTestRun]
    public static void AfterTestRun(PlaywrightContext context)
    {
        context.Dispose();
    }
}
