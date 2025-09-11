// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Tests.E2E.Authorization;
using EdFi.DataManagementService.Tests.E2E.Management;
using Reqnroll;

namespace EdFi.DataManagementService.Tests.E2E.Hooks;

[Binding]
public class CacheClearingHooks(PlaywrightContext playwrightContext, TestLogger logger)
{
    private readonly PlaywrightContext _playwrightContext = playwrightContext;
    private readonly TestLogger _logger = logger;

    /// <summary>
    /// Universal cache clearing hook that runs before EVERY scenario to ensure test isolation.
    /// This prevents claimset cache pollution between tests.
    /// </summary>
    [BeforeScenario(Order = 100)] // Run early in the scenario setup
    public async Task ClearCacheBeforeScenario()
    {
        _logger.log.Information("===== BeforeScenario: Universal cache clearing =====");

        try
        {
            // Ensure we have a system administrator token
            if (string.IsNullOrEmpty(SystemAdministrator.Token))
            {
                _logger.log.Information("No SystemAdministrator token found, registering new admin client");
                await SystemAdministrator.Register(
                    "sys-admin-cache-clear-" + Guid.NewGuid().ToString(),
                    "CacheClear#2024!"
                );
            }

            // Clear DMS claimsets cache
            _logger.log.Information("Clearing DMS claimsets cache...");

            // Create authorization header
            var headers = new List<KeyValuePair<string, string>>
            {
                new("Authorization", $"Bearer {SystemAdministrator.Token}"),
            };

            // Call reload-claimsets to clear the cache
            var reloadResponse = await _playwrightContext.ApiRequestContext?.PostAsync(
                "management/reload-claimsets",
                new() { Data = "{}", Headers = headers }
            )!;

            _logger.log.Information($"DMS reload-claimsets response status: {reloadResponse.Status}");

            // Verify the cache was cleared successfully
            if (reloadResponse.Status != 200)
            {
                var responseBody = await reloadResponse.TextAsync();
                _logger.log.Error(
                    $"Failed to clear DMS cache. Status: {reloadResponse.Status}, Body: {responseBody}"
                );

                // Fail fast - don't run tests with potentially polluted cache
                throw new InvalidOperationException(
                    $"Failed to clear DMS claimsets cache. Status: {reloadResponse.Status}. "
                        + "Test isolation cannot be guaranteed."
                );
            }

            // Verify cache state by calling view-claimsets
            _logger.log.Information("Verifying cache state...");

            var viewResponse = await _playwrightContext.ApiRequestContext?.GetAsync(
                "management/view-claimsets",
                new() { Headers = headers }
            )!;

            if (viewResponse.Status == 200)
            {
                var viewBody = await viewResponse.TextAsync();
                _logger.log.Information($"Cache verification - current claimsets: {viewBody.Length} chars");

                // The cache should now contain fresh data from CMS
                // We're not checking for empty because reload-claimsets immediately fetches from CMS
                _logger.log.Information("Cache successfully cleared and reloaded from CMS");
            }
            else
            {
                _logger.log.Warning(
                    $"Could not verify cache state. View-claimsets status: {viewResponse.Status}"
                );
            }

            // Small delay to ensure cache operations complete
            await Task.Delay(100);

            _logger.log.Information("===== Cache clearing completed successfully =====");
        }
        catch (Exception ex)
        {
            _logger.log.Error($"Critical error during cache clearing: {ex.Message}");
            _logger.log.Error($"Stack trace: {ex.StackTrace}");

            // Re-throw to fail the test - we cannot proceed with potentially polluted cache
            throw new InvalidOperationException(
                "Failed to clear cache before scenario. Test isolation cannot be guaranteed.",
                ex
            );
        }
    }
}
