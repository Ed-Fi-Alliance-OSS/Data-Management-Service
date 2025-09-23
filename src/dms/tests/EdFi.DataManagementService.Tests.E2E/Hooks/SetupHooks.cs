// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Tests.E2E.Authorization;
using EdFi.DataManagementService.Tests.E2E.Management;
using Reqnroll;

namespace EdFi.DataManagementService.Tests.E2E.Hooks;

/// <summary>
/// Provides test lifecycle hooks for end-to-end test setup and teardown operations.
/// This class manages the initialization and cleanup of test infrastructure including
/// container orchestration, API context configuration, and system administrator registration.
/// </summary>
[Binding]
public static class SetupHooks
{
    private static ContainerSetupBase? _containerSetup;

    /// <summary>
    /// Executes once before the entire test run begins.
    /// Initializes the container setup strategy and registers a system administrator
    /// with dynamically generated credentials for test isolation.
    /// </summary>
    [BeforeTestRun]
    public static async Task BeforeTestRun(PlaywrightContext context, TestLogger logger)
    {
        _containerSetup = new SearchContainerSetup();

        await SystemAdministrator.Register("sys-admin " + Guid.NewGuid().ToString(), "SdfH)98&Jk");
    }

    /// <summary>
    /// Executes before each feature/scenario group begins.
    /// Configures the API URL from the container setup and initializes the API context
    /// for the current test feature. Logs any configuration failures for debugging.
    /// </summary>
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

    /// <summary>
    /// Executes after each feature/scenario group completes.
    /// Resets the data state in the container environment to ensure test isolation
    /// between different feature groups.
    /// </summary>
    [AfterFeature]
    public static async Task AfterFeature(TestLogger logger)
    {
        await _containerSetup!.ResetData();
    }

    /// <summary>
    /// Executes once after the entire test run completes.
    /// Disposes of the Playwright context to release resources and
    /// clean up any remaining test artifacts.
    /// </summary>
    [AfterTestRun]
    public static void AfterTestRun(PlaywrightContext context)
    {
        context.Dispose();
    }
}
