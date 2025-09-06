// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Tests.E2E.Authorization;
using EdFi.DataManagementService.Tests.E2E.Management;
using Reqnroll;

namespace EdFi.DataManagementService.Tests.E2E.Hooks;

[Binding]
public static class SetupHooks
{
    private static ContainerSetupBase? _containerSetup;

    [BeforeTestRun]
    public static async Task BeforeTestRun(PlaywrightContext context, TestLogger logger)
    {
        _containerSetup = new OpenSearchContainerSetup();

        await SystemAdministrator.Register("sys-admin " + Guid.NewGuid().ToString(), "SdfH)98&Jk");
    }

    [BeforeFeature]
    public static async Task BeforeFeature(PlaywrightContext context, TestLogger logger)
    {
        try
        {
            context.ApiUrl = _containerSetup!.ApiUrl();
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
        await _containerSetup!.ResetData();
    }

    [AfterTestRun]
    public static void AfterTestRun(PlaywrightContext context)
    {
        context.Dispose();
    }
}
