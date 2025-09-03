// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net.Http;
using EdFi.DataManagementService.Tests.E2E.Authorization;
using EdFi.DataManagementService.Tests.E2E.Management;
using FluentAssertions;
using Reqnroll;

namespace EdFi.DataManagementService.Tests.E2E.Hooks;

[Binding]
public class ClaimSetUploadHooks(PlaywrightContext playwrightContext, TestLogger logger)
{
    private readonly PlaywrightContext _playwrightContext = playwrightContext;
    private readonly TestLogger _logger = logger;

    [AfterScenario("ResetClaimsetsAfterScenario")]
    public async Task ResetClaimsetsAfterScenario()
    {
        _logger.log.Information("===== AfterScenario: Resetting claimsets =====");

        try
        {
            // First reset CMS claimsets
            _logger.log.Information("Resetting CMS claimsets...");

            // Get system administrator token for CMS API access
            if (string.IsNullOrEmpty(SystemAdministrator.Token))
            {
                await SystemAdministrator.Register("SystemAdministratorClient", "SystemAdministratorSecret");
            }

            // Use HttpClient to call CMS reload-claims endpoint
            using var httpClient = new HttpClient
            {
                BaseAddress = new Uri($"http://localhost:{AppSettings.ConfigServicePort}/"),
            };

            httpClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", SystemAdministrator.Token);

            // POST with empty body for reload-claims endpoint
            using var content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
            var cmsResponse = await httpClient.PostAsync("config/management/reload-claims", content);

            _logger.log.Information($"CMS reload-claims response status: {cmsResponse.StatusCode}");
            var cmsResponseBody = await cmsResponse.Content.ReadAsStringAsync();
            _logger.log.Information($"CMS reload-claims response: {cmsResponseBody}");

            cmsResponse
                .StatusCode.Should()
                .Be(System.Net.HttpStatusCode.OK, "CMS claimsets reset should succeed");

            // Wait for CMS to process the reload
            await Task.Delay(500);

            // Then reset DMS claimsets
            _logger.log.Information("Resetting DMS claimsets...");

            // Create authorization header - using Bearer token for DMS
            var headers = new List<KeyValuePair<string, string>>();
            if (!string.IsNullOrEmpty(SystemAdministrator.Token))
            {
                headers.Add(new("Authorization", $"Bearer {SystemAdministrator.Token}"));
            }

            var dmsResponse = await _playwrightContext.ApiRequestContext?.PostAsync(
                "management/reload-claimsets",
                new() { Data = "{}", Headers = headers }
            )!;

            _logger.log.Information($"DMS reload-claimsets response status: {dmsResponse.Status}");
            var dmsResponseBody = await dmsResponse.TextAsync();
            _logger.log.Information($"DMS reload-claimsets response: {dmsResponseBody}");

            dmsResponse.Status.Should().Be(200, "DMS claimsets reset should succeed");

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
}
