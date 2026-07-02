// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.InstanceManagement.Tests.E2E.Management;
using FluentAssertions;
using Reqnroll;

namespace EdFi.InstanceManagement.Tests.E2E.StepDefinitions;

[Binding]
public class ChangeQueryStepDefinitions(InstanceManagementContext context)
{
    private static (string districtId, string schoolYear) SplitRoute(string instanceRoute)
    {
        var parts = instanceRoute.Split('/');
        parts.Should().HaveCount(2, "Instance route must be in format districtId/schoolYear");
        return (parts[0], parts[1]);
    }

    private static async Task<long> ParseNewestChangeVersionAsync(HttpResponseMessage response)
    {
        var content = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(content);
        return doc.RootElement.GetProperty("newestChangeVersion").GetInt64();
    }

    private DmsApiClient NewTenantClient(string tenantName) =>
        new(TestConfiguration.DmsApiUrl, context.DmsToken!, tenantName);

    [When("I capture the newest change version for tenant {string} instance {string} as {string}")]
    public async Task WhenICaptureNewestChangeVersion(
        string tenantName,
        string instanceRoute,
        string variableName
    )
    {
        context.DmsToken.Should().NotBeNullOrEmpty("Must be authenticated to DMS first");
        var (districtId, schoolYear) = SplitRoute(instanceRoute);

        using var client = NewTenantClient(tenantName);
        context.LastResponse = await client.GetAvailableChangeVersionsAsync(districtId, schoolYear);
        context.LastResponse.IsSuccessStatusCode.Should().BeTrue();

        context.CapturedChangeVersions[variableName] = await ParseNewestChangeVersionAsync(
            context.LastResponse
        );
        Console.WriteLine(
            $"Captured newest change version '{variableName}' = {context.CapturedChangeVersions[variableName]} "
                + $"for {tenantName} {instanceRoute}"
        );
    }

    [Then("the newest change version for tenant {string} instance {string} equals captured {string}")]
    public async Task ThenNewestChangeVersionEqualsCaptured(
        string tenantName,
        string instanceRoute,
        string variableName
    )
    {
        var (districtId, schoolYear) = SplitRoute(instanceRoute);
        using var client = NewTenantClient(tenantName);
        var response = await client.GetAvailableChangeVersionsAsync(districtId, schoolYear);
        response.IsSuccessStatusCode.Should().BeTrue();

        var actual = await ParseNewestChangeVersionAsync(response);
        actual
            .Should()
            .Be(
                context.CapturedChangeVersions[variableName],
                $"{tenantName} {instanceRoute} change version must be unchanged (isolated)"
            );
    }

    [Then(
        "the newest change version for tenant {string} instance {string} is greater than captured {string}"
    )]
    public async Task ThenNewestChangeVersionGreaterThanCaptured(
        string tenantName,
        string instanceRoute,
        string variableName
    )
    {
        var (districtId, schoolYear) = SplitRoute(instanceRoute);
        using var client = NewTenantClient(tenantName);
        var response = await client.GetAvailableChangeVersionsAsync(districtId, schoolYear);
        response.IsSuccessStatusCode.Should().BeTrue();

        var actual = await ParseNewestChangeVersionAsync(response);
        actual
            .Should()
            .BeGreaterThan(
                context.CapturedChangeVersions[variableName],
                $"{tenantName} {instanceRoute} change version must advance after writes"
            );
    }

    [Then("the newest change version for tenant {string} instance {string} is greater than {int}")]
    public async Task ThenNewestChangeVersionGreaterThanLiteral(
        string tenantName,
        string instanceRoute,
        int threshold
    )
    {
        var (districtId, schoolYear) = SplitRoute(instanceRoute);
        using var client = NewTenantClient(tenantName);
        context.LastResponse = await client.GetAvailableChangeVersionsAsync(districtId, schoolYear);
        context.LastResponse.IsSuccessStatusCode.Should().BeTrue();

        var actual = await ParseNewestChangeVersionAsync(context.LastResponse);
        actual.Should().BeGreaterThan(threshold);
    }

    [When("a GET request is made to deletes for tenant {string} instance {string} resource {string}")]
    public async Task WhenAGetRequestIsMadeToDeletes(string tenantName, string instanceRoute, string resource)
    {
        var (districtId, schoolYear) = SplitRoute(instanceRoute);
        using var client = NewTenantClient(tenantName);
        context.LastResponse = await client.GetTrackedChangesAsync(
            districtId,
            schoolYear,
            resource,
            "deletes"
        );
        Console.WriteLine(
            $"GET deletes {tenantName} {instanceRoute} {resource}: {(int)context.LastResponse.StatusCode}"
        );
    }

    [When("a GET request is made to keyChanges for tenant {string} instance {string} resource {string}")]
    public async Task WhenAGetRequestIsMadeToKeyChanges(
        string tenantName,
        string instanceRoute,
        string resource
    )
    {
        var (districtId, schoolYear) = SplitRoute(instanceRoute);
        using var client = NewTenantClient(tenantName);
        context.LastResponse = await client.GetTrackedChangesAsync(
            districtId,
            schoolYear,
            resource,
            "keyChanges"
        );
        Console.WriteLine(
            $"GET keyChanges {tenantName} {instanceRoute} {resource}: {(int)context.LastResponse.StatusCode}"
        );
    }

    [When("a DELETE request is made for stored location {string}")]
    public async Task WhenADeleteRequestIsMadeForStoredLocation(string key)
    {
        context.DescriptorLocations.Should().ContainKey(key);
        using var client = new DmsApiClient(TestConfiguration.DmsApiUrl, context.DmsToken!);
        context.LastResponse = await client.DeleteByLocationAsync(context.DescriptorLocations[key]);
    }

    [When("a PUT request is made for stored location {string} with body:")]
    public async Task WhenAPutRequestIsMadeForStoredLocation(string key, string jsonBody)
    {
        context.DescriptorLocations.Should().ContainKey(key);
        var location = context.DescriptorLocations[key];

        // PUT bodies must echo the resource id; derive it from the stored location's last segment.
        var segments = location.TrimEnd('/').Split('/');
        var id = segments[^1];
        var resolvedBody = jsonBody.Replace("{id}", id);
        var body = JsonSerializer.Deserialize<JsonElement>(resolvedBody);

        using var client = new DmsApiClient(TestConfiguration.DmsApiUrl, context.DmsToken!);
        context.LastResponse = await client.PutByLocationAsync(location, body);
        Console.WriteLine($"PUT {location}: {(int)context.LastResponse.StatusCode}");
    }
}
