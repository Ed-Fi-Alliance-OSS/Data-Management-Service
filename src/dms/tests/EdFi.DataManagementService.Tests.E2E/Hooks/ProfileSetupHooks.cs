// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Tests.E2E.Authorization;
using EdFi.DataManagementService.Tests.E2E.Management;
using EdFi.DataManagementService.Tests.E2E.Profiles;
using Reqnroll;

namespace EdFi.DataManagementService.Tests.E2E.Hooks;

/// <summary>
/// Provides test lifecycle hooks for profile setup.
/// Creates all test profiles once at the start of the test run and stores their IDs
/// for reuse across all profile-related tests.
/// </summary>
[Binding]
public static class ProfileSetupHooks
{
    /// <summary>
    /// Executes once before the entire test run begins, after the main SetupHooks.
    /// Creates all test profiles defined in ProfileDefinitions and stores their IDs.
    /// </summary>
    /// <remarks>
    /// Order = 10100 ensures this runs after SetupHooks.BeforeTestRun (default order 10000)
    /// which registers the SystemAdministrator. We need the admin token to create profiles.
    /// </remarks>
    [BeforeTestRun(Order = 10100)]
    public static async Task CreateTestProfiles(TestLogger logger)
    {
        logger.log.Information("===== ProfileSetupHooks: Creating test profiles =====");

        // SystemAdministrator should already be registered by SetupHooks (Order = 10000)
        if (string.IsNullOrEmpty(SystemAdministrator.Token))
        {
            throw new InvalidOperationException(
                "SystemAdministrator.Token is not available. Ensure SetupHooks.BeforeTestRun runs before ProfileSetupHooks."
            );
        }

        int successCount = 0;
        int failCount = 0;

        foreach ((string name, string xml) in ProfileDefinitions.AllProfiles)
        {
            try
            {
                logger.log.Information($"Creating profile: {name}");

                int profileId = await ProfileAwareAuthorizationProvider.CreateProfile(
                    name,
                    xml,
                    SystemAdministrator.Token
                );

                ProfileTestData.RegisterProfile(name, profileId);
                successCount++;

                logger.log.Information($"Profile '{name}' created with ID: {profileId}");
            }
            catch (Exception ex)
            {
                failCount++;
                logger.log.Error($"Failed to create profile '{name}': {ex.Message}");

                // Don't throw here - continue creating other profiles
                // Tests using this profile will fail with a clear error
            }
        }

        ProfileTestData.MarkInitialized();

        logger.log.Information(
            $"===== Profile setup complete: {successCount} succeeded, {failCount} failed ====="
        );
        logger.log.Information(
            $"Registered profiles: {string.Join(", ", ProfileTestData.RegisteredProfileNames)}"
        );
    }
}
