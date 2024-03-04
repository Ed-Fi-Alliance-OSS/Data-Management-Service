// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Api.Tests.E2E.Management;
using Reqnroll;

namespace EdFi.DataManagementService.Api.Tests.E2E.Hooks
{
    [Binding]
    public class SetupHooks
    {

        [BeforeTestRun]
        public static async Task BeforeTestRun(PlaywrightContext context, ContainerSetup containers, IReqnrollOutputHelper _outputHelper)
        {
            try
            {
                string containerURL = await containers.SetupDataManagement();

                context.API_URL = containerURL;
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
