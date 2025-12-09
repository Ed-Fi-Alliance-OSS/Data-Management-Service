// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.InstanceManagement.Tests.E2E.Management;
using Reqnroll;

namespace EdFi.InstanceManagement.Tests.E2E.StepDefinitions;

[Binding]
public class ManagementEndpointStepDefinitions(InstanceManagementContext context)
{
    [When("a GET request is made to view-claimsets endpoint without tenant")]
    public async Task WhenAGetRequestIsMadeToViewClaimsetsEndpointWithoutTenant()
    {
        Console.WriteLine("GET /management/view-claimsets (no tenant)");

        using var client = new DmsApiClient(TestConfiguration.DmsApiUrl, "");
        context.LastResponse = await client.GetViewClaimsetsAsync(tenant: null);

        Console.WriteLine(
            $"Response: {(int)context.LastResponse.StatusCode} ({context.LastResponse.StatusCode})"
        );
    }

    [When("a GET request is made to view-claimsets endpoint with tenant {string}")]
    public async Task WhenAGetRequestIsMadeToViewClaimsetsEndpointWithTenant(string tenantName)
    {
        Console.WriteLine($"GET /management/{tenantName}/view-claimsets");

        using var client = new DmsApiClient(TestConfiguration.DmsApiUrl, "");
        context.LastResponse = await client.GetViewClaimsetsAsync(tenant: tenantName);

        Console.WriteLine(
            $"Response: {(int)context.LastResponse.StatusCode} ({context.LastResponse.StatusCode})"
        );

        if (!context.LastResponse.IsSuccessStatusCode)
        {
            var responseBody = await context.LastResponse.Content.ReadAsStringAsync();
            Console.WriteLine($"Response body: {responseBody}");
        }
    }

    [When("a POST request is made to reload-claimsets endpoint without tenant")]
    public async Task WhenAPostRequestIsMadeToReloadClaimsetsEndpointWithoutTenant()
    {
        Console.WriteLine("POST /management/reload-claimsets (no tenant)");

        using var client = new DmsApiClient(TestConfiguration.DmsApiUrl, "");
        context.LastResponse = await client.PostReloadClaimsetsAsync(tenant: null);

        Console.WriteLine(
            $"Response: {(int)context.LastResponse.StatusCode} ({context.LastResponse.StatusCode})"
        );
    }

    [When("a POST request is made to reload-claimsets endpoint with tenant {string}")]
    public async Task WhenAPostRequestIsMadeToReloadClaimsetsEndpointWithTenant(string tenantName)
    {
        Console.WriteLine($"POST /management/{tenantName}/reload-claimsets");

        using var client = new DmsApiClient(TestConfiguration.DmsApiUrl, "");
        context.LastResponse = await client.PostReloadClaimsetsAsync(tenant: tenantName);

        Console.WriteLine(
            $"Response: {(int)context.LastResponse.StatusCode} ({context.LastResponse.StatusCode})"
        );

        if (!context.LastResponse.IsSuccessStatusCode)
        {
            var responseBody = await context.LastResponse.Content.ReadAsStringAsync();
            Console.WriteLine($"Response body: {responseBody}");
        }
    }
}
