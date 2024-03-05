// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Api.Tests.E2E.Management;
using Microsoft.Extensions.Configuration;
using Reqnroll;

namespace EdFi.DataManagementService.Api.Tests.E2E.Hooks
{
    [Binding]
    public class SetupHooks
    {
        private static IConfiguration? config;

        [BeforeTestRun]
        public static async Task BeforeTestRun(PlaywrightContext context, ContainerSetup containers)
        {
            try
            {
                config ??= new ConfigurationBuilder()
                        .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                        .Build();

                bool.TryParse(config["useTestContainers"], out bool useTestContainers);

                if (useTestContainers)
                {
                    context.ApiUrl = await containers.SetupDataManagement();
                }
                else
                {
                    //Will print message
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
}
