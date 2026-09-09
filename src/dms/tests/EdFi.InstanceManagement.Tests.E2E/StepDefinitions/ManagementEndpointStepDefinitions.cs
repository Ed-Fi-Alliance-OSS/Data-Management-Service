// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.InstanceManagement.Tests.E2E.Management;
using FluentAssertions;
using Reqnroll;

namespace EdFi.InstanceManagement.Tests.E2E.StepDefinitions;

[Binding]
public class ManagementEndpointStepDefinitions(InstanceManagementContext context)
{
    private const string NoToken = "";

    [When("a GET request is made to view-claimsets endpoint without tenant")]
    public async Task WhenAGetRequestIsMadeToViewClaimsetsEndpointWithoutTenant()
    {
        Console.WriteLine("GET /management/view-claimsets (no tenant)");

        // The unscoped forms are anonymous 404 stubs in multi-tenant mode; deliberately no token.
        using var client = new DmsApiClient(TestConfiguration.DmsApiUrl, NoToken);
        context.LastResponse = await client.GetViewClaimsetsAsync(tenant: null);

        LogResponse();
    }

    [When("a GET request is made to view-claimsets endpoint with tenant {string}")]
    public async Task WhenAGetRequestIsMadeToViewClaimsetsEndpointWithTenant(string tenantName)
    {
        Console.WriteLine($"GET /management/{tenantName}/view-claimsets");

        using var client = new DmsApiClient(TestConfiguration.DmsApiUrl, await GetManagementTokenAsync());
        context.LastResponse = await client.GetViewClaimsetsAsync(tenant: tenantName);

        await LogResponseBodyOnFailureAsync();
    }

    [When("a GET request is made to view-claimsets endpoint with tenant {string} and no token")]
    public async Task WhenAGetRequestIsMadeToViewClaimsetsEndpointWithTenantAndNoToken(string tenantName)
    {
        Console.WriteLine($"GET /management/{tenantName}/view-claimsets (no token)");

        using var client = new DmsApiClient(TestConfiguration.DmsApiUrl, NoToken);
        context.LastResponse = await client.GetViewClaimsetsAsync(tenant: tenantName);

        LogResponse();
    }

    [When("a GET request is made to view-claimsets endpoint with tenant {string} and a wrong-role token")]
    public async Task WhenAGetRequestIsMadeToViewClaimsetsEndpointWithTenantAndWrongRoleToken(
        string tenantName
    )
    {
        Console.WriteLine($"GET /management/{tenantName}/view-claimsets (wrong role)");

        using var client = new DmsApiClient(
            TestConfiguration.DmsApiUrl,
            await GetWrongRoleDmsTokenAsync(tenantName)
        );
        context.LastResponse = await client.GetViewClaimsetsAsync(tenant: tenantName);

        LogResponse();
    }

    [When("a POST request is made to reload-claimsets endpoint without tenant")]
    public async Task WhenAPostRequestIsMadeToReloadClaimsetsEndpointWithoutTenant()
    {
        Console.WriteLine("POST /management/reload-claimsets (no tenant)");

        using var client = new DmsApiClient(TestConfiguration.DmsApiUrl, NoToken);
        context.LastResponse = await client.PostReloadClaimsetsAsync(tenant: null);

        LogResponse();
    }

    [When("a POST request is made to reload-claimsets endpoint with tenant {string}")]
    public async Task WhenAPostRequestIsMadeToReloadClaimsetsEndpointWithTenant(string tenantName)
    {
        Console.WriteLine($"POST /management/{tenantName}/reload-claimsets");

        using var client = new DmsApiClient(TestConfiguration.DmsApiUrl, await GetManagementTokenAsync());
        context.LastResponse = await client.PostReloadClaimsetsAsync(tenant: tenantName);

        await LogResponseBodyOnFailureAsync();
    }

    [When("a POST request is made to reload-claimsets endpoint with tenant {string} and no token")]
    public async Task WhenAPostRequestIsMadeToReloadClaimsetsEndpointWithTenantAndNoToken(string tenantName)
    {
        Console.WriteLine($"POST /management/{tenantName}/reload-claimsets (no token)");

        using var client = new DmsApiClient(TestConfiguration.DmsApiUrl, NoToken);
        context.LastResponse = await client.PostReloadClaimsetsAsync(tenant: tenantName);

        LogResponse();
    }

    [When("a POST request is made to reload-claimsets endpoint with tenant {string} and a wrong-role token")]
    public async Task WhenAPostRequestIsMadeToReloadClaimsetsEndpointWithTenantAndWrongRoleToken(
        string tenantName
    )
    {
        Console.WriteLine($"POST /management/{tenantName}/reload-claimsets (wrong role)");

        using var client = new DmsApiClient(
            TestConfiguration.DmsApiUrl,
            await GetWrongRoleDmsTokenAsync(tenantName)
        );
        context.LastResponse = await client.PostReloadClaimsetsAsync(tenant: tenantName);

        LogResponse();
    }

    private static async Task<string> GetManagementTokenAsync() =>
        await TokenHelper.GetConfigServiceTokenAsync(
            $"{TestConfiguration.ConfigServiceUrl}/connect/token",
            "DmsConfigurationService",
            "ValidClientSecret1234567890!Abcd"
        );

    private async Task<string> GetWrongRoleDmsTokenAsync(string tenantName)
    {
        context
            .CredentialsByTenant.Should()
            .ContainKey(tenantName, $"fixture credentials for tenant {tenantName} must exist");

        var (clientKey, clientSecret) = context.CredentialsByTenant[tenantName];
        return await TokenHelper.GetDmsTokenAsync(
            $"{TestConfiguration.ConfigServiceUrl}/connect/token/",
            clientKey,
            clientSecret
        );
    }

    private void LogResponse()
    {
        Console.WriteLine(
            $"Response: {(int)context.LastResponse!.StatusCode} ({context.LastResponse.StatusCode})"
        );
    }

    private async Task LogResponseBodyOnFailureAsync()
    {
        LogResponse();

        if (!context.LastResponse!.IsSuccessStatusCode)
        {
            var responseBody = await context.LastResponse.Content.ReadAsStringAsync();
            Console.WriteLine($"Response body: {responseBody}");
        }
    }
}
