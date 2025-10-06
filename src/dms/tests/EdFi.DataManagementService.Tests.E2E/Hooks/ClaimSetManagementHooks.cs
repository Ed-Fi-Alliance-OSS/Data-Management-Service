// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Tests.E2E.Authorization;
using EdFi.DataManagementService.Tests.E2E.Management;
using FluentAssertions;
using Microsoft.Playwright;
using Reqnroll;

namespace EdFi.DataManagementService.Tests.E2E.Hooks;

/// <summary>
/// Manages claimset caching and synchronization for E2E tests to ensure proper test isolation
/// and prevent cache pollution between test runs.
/// </summary>
[Binding]
public class ClaimSetManagementHooks(PlaywrightContext playwrightContext, TestLogger logger)
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

        const int MaxRetries = 3;
        int attempt = 0;
        Exception? lastException = null;

        while (attempt < MaxRetries)
        {
            try
            {
                attempt++;
                _logger.log.Information($"Cache clear attempt {attempt}/{MaxRetries}");

                // Ensure we have a system administrator token (use consistent credentials)
                await EnsureSystemAdministratorToken(
                    "SystemAdministratorClient",
                    "SystemAdministratorSecret"
                );

                // Clear DMS claimsets cache
                _logger.log.Information("Clearing DMS claimsets cache...");
                await ReloadDmsClaimsets();

                // Verify cache state by calling view-claimsets
                _logger.log.Information("Verifying cache state...");
                await VerifyDmsCacheState();

                // Small delay to ensure cache operations complete
                await Task.Delay(100);

                _logger.log.Information("===== Cache clearing completed successfully =====");
                return; // Success
            }
            catch (Exception ex)
            {
                lastException = ex;
                _logger.log.Warning($"Cache clearing attempt {attempt} failed: {ex.Message}");

                if (attempt < MaxRetries)
                {
                    // Wait before retry with exponential backoff
                    int delayMs = 500 * attempt;
                    _logger.log.Information($"Retrying in {delayMs}ms...");
                    await Task.Delay(delayMs);
                }
            }
        }

        // Re-throw to fail the test - we cannot proceed with potentially polluted cache
        throw new InvalidOperationException(
            $"Failed to clear cache before scenario after {MaxRetries} attempts. Test isolation cannot be guaranteed.",
            lastException
        );
    }

    /// <summary>
    /// Resets both CMS and DMS claimsets after scenarios tagged with @ResetClaimsetsAfterScenario.
    /// This provides a clean state for subsequent tests that need fresh claimsets.
    /// </summary>
    [AfterScenario("ResetClaimsetsAfterScenario")]
    public async Task ResetClaimsetsAfterScenario()
    {
        _logger.log.Information("===== AfterScenario: Resetting claimsets =====");

        try
        {
            // First reset CMS claimsets
            _logger.log.Information("Resetting CMS claimsets...");
            await EnsureSystemAdministratorToken("SystemAdministratorClient", "SystemAdministratorSecret");
            await ReloadCmsClaimsets();

            // Wait for CMS to process the reload
            await Task.Delay(500);

            // Then reset DMS claimsets
            _logger.log.Information("Resetting DMS claimsets...");
            await ReloadDmsClaimsets();

            // Wait for DMS to process the reload
            await Task.Delay(1000);

            _logger.log.Information("Claimsets successfully reset in both CMS and DMS");
        }
        catch (Exception ex)
        {
            _logger.log.Error($"Error resetting claimsets: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Ensures we have a valid system administrator token, registering a new admin client if needed.
    /// </summary>
    private async Task EnsureSystemAdministratorToken(string clientId, string clientSecret)
    {
        if (string.IsNullOrEmpty(SystemAdministrator.Token))
        {
            _logger.log.Information(
                $"No SystemAdministrator token found, registering new admin client: {clientId}"
            );
            await SystemAdministrator.Register(clientId, clientSecret);
        }
    }

    /// <summary>
    /// Reloads DMS claimsets by calling the management/reload-claimsets endpoint.
    /// </summary>
    private async Task ReloadDmsClaimsets()
    {
        List<KeyValuePair<string, string>> headers = CreateAuthHeaders();

        // Call reload-claimsets to clear the cache
        IAPIResponse reloadResponse = await _playwrightContext.ApiRequestContext?.PostAsync(
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
    }

    /// <summary>
    /// Reloads CMS claimsets by calling the config/management/reload-claims endpoint.
    /// </summary>
    private async Task ReloadCmsClaimsets()
    {
        using HttpClient httpClient = new()
        {
            BaseAddress = new Uri($"http://localhost:{AppSettings.ConfigServicePort}/"),
        };

        httpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", SystemAdministrator.Token);

        // POST with empty body for reload-claims endpoint
        using StringContent content = new("{}", System.Text.Encoding.UTF8, "application/json");
        var cmsResponse = await httpClient.PostAsync("config/management/reload-claims", content);

        _logger.log.Information($"CMS reload-claims response status: {cmsResponse.StatusCode}");
        var cmsResponseBody = await cmsResponse.Content.ReadAsStringAsync();
        _logger.log.Information($"CMS reload-claims response: {cmsResponseBody}");

        cmsResponse
            .StatusCode.Should()
            .Be(System.Net.HttpStatusCode.OK, "CMS claimsets reset should succeed");
    }

    /// <summary>
    /// Verifies the DMS cache state by calling the view-claimsets endpoint.
    /// </summary>
    private async Task VerifyDmsCacheState()
    {
        var headers = CreateAuthHeaders();

        IAPIResponse viewResponse = await _playwrightContext.ApiRequestContext?.GetAsync(
            "management/view-claimsets",
            new() { Headers = headers }
        )!;

        if (viewResponse.Status == 200)
        {
            var viewBody = await viewResponse.TextAsync();
            _logger.log.Information($"Cache verification - current claimsets: {viewBody.Length} chars");

            // The cache should now contain fresh data from CMS
            _logger.log.Information("Cache successfully cleared and reloaded from CMS");
        }
        else
        {
            _logger.log.Warning(
                $"Could not verify cache state. View-claimsets status: {viewResponse.Status}"
            );
        }
    }

    /// <summary>
    /// Creates authorization headers with the system administrator bearer token.
    /// </summary>
    private static List<KeyValuePair<string, string>> CreateAuthHeaders()
    {
        List<KeyValuePair<string, string>> headers = [];
        if (!string.IsNullOrEmpty(SystemAdministrator.Token))
        {
            headers.Add(new("Authorization", $"Bearer {SystemAdministrator.Token}"));
        }
        return headers;
    }
}
