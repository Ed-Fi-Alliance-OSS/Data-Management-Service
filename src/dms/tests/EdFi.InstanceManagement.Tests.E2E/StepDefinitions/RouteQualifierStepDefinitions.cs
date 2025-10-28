// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.InstanceManagement.Tests.E2E.Management;
using EdFi.InstanceManagement.Tests.E2E.Models;
using FluentAssertions;
using Reqnroll;

namespace EdFi.InstanceManagement.Tests.E2E.StepDefinitions;

[Binding]
public class RouteQualifierStepDefinitions(InstanceManagementContext context)
{
    private DmsApiClient? _dmsClient;

    [Given("the system is configured with route qualifiers")]
    public void GivenTheSystemIsConfiguredWithRouteQualifiers()
    {
        // Verify ROUTE_QUALIFIER_SEGMENTS is set in environment
        // This is validated by the setup script
        TestConfiguration.RouteQualifierSegments.Should().NotBeEmpty();

        Console.WriteLine(
            $"Route qualifier segments configured: {string.Join(", ", TestConfiguration.RouteQualifierSegments)}"
        );
        Console.WriteLine($"DMS API URL: {TestConfiguration.DmsApiUrl}");
    }

    [Given("I have completed instance setup with {int} instances")]
    public async Task GivenIHaveCompletedInstanceSetupWithInstances(int count)
    {
        var setupSteps = new InstanceSetupStepDefinitions(context);

        // Authenticate to Config Service
        await setupSteps.GivenIAmAuthenticatedToTheConfigurationServiceAsSystemAdmin();

        // Create vendor
        await setupSteps.GivenAVendorExists();

        // Create instances with route contexts
        await setupSteps.GivenInstancesExistWithRouteContexts(count);

        // Create application
        var edOrgIds = new[] { 255901, 255902 };
        var application = await setupSteps._configClient!.CreateApplicationAsync(
            new ApplicationRequest(
                context.VendorId!.Value,
                "Multi-District Test App",
                "E2E-NoFurtherAuthRequiredClaimSet",
                edOrgIds,
                [.. context.InstanceIds]
            )
        );
        context.ApplicationId = application.Id;
        context.ClientKey = application.Key;
        context.ClientSecret = application.Secret;
    }

    [Given("I am authenticated to DMS with application credentials")]
    public async Task GivenIAmAuthenticatedToDmsWithApplicationCredentials()
    {
        context.ClientKey.Should().NotBeNullOrEmpty("Application must be created first");
        context.ClientSecret.Should().NotBeNullOrEmpty("Application must be created first");

        // TODO: Update once Config Service supports route qualifiers in OAuth endpoint
        // The discovery API returns OAuth URLs with route qualifiers (e.g., /connect/token/{districtId}/{schoolYear})
        // but the Config Service doesn't yet support this pattern. Once it does, we should get the URL from
        // discovery API instead of hardcoding it here.
        var tokenUrl = "http://localhost:8081/connect/token/";

        context.DmsToken = await TokenHelper.GetDmsTokenAsync(
            tokenUrl,
            context.ClientKey!,
            context.ClientSecret!
        );

        _dmsClient = new DmsApiClient(TestConfiguration.DmsApiUrl, context.DmsToken);
    }

    [When("a POST request is made to instance {string} and resource {string} with body:")]
    public async Task WhenAPostRequestIsMadeToInstanceAndResourceWithBody(
        string instanceRoute,
        string resource,
        string jsonBody
    )
    {
        _dmsClient.Should().NotBeNull("Must be authenticated to DMS first");

        var parts = instanceRoute.Split('/');
        parts.Should().HaveCount(2, "Instance route must be in format districtId/schoolYear");

        var districtId = parts[0];
        var schoolYear = parts[1];

        Console.WriteLine($"POST to instance route: {instanceRoute}, resource: {resource}");
        Console.WriteLine($"Request body: {jsonBody}");

        // Parse JSON body to JsonElement which preserves the structure
        var body = JsonSerializer.Deserialize<JsonElement>(jsonBody);

        context.LastResponse = await _dmsClient!.PostResourceAsync(districtId, schoolYear, resource, body);

        Console.WriteLine(
            $"Response: {(int)context.LastResponse.StatusCode} ({context.LastResponse.StatusCode})"
        );
        if (!context.LastResponse.IsSuccessStatusCode)
        {
            var responseBody = await context.LastResponse.Content.ReadAsStringAsync();
            Console.WriteLine($"Response body: {responseBody}");
        }
    }

    [Then("it should respond with {int}")]
    public void ThenItShouldRespondWith(int expectedStatusCode)
    {
        context.LastResponse.Should().NotBeNull();
        ((int)context.LastResponse!.StatusCode).Should().Be(expectedStatusCode);
    }

    [Then("it should respond with success")]
    public async Task ThenItShouldRespondWithSuccess()
    {
        context.LastResponse.Should().NotBeNull();

        if (!context.LastResponse!.IsSuccessStatusCode)
        {
            var responseBody = await context.LastResponse.Content.ReadAsStringAsync();
            Console.WriteLine(
                $"Expected success status but got {(int)context.LastResponse.StatusCode} ({context.LastResponse.StatusCode})"
            );
            Console.WriteLine($"Response body: {responseBody}");
            Console.WriteLine($"Request URL: {context.LastResponse.RequestMessage?.RequestUri}");
        }

        context
            .LastResponse!.IsSuccessStatusCode.Should()
            .BeTrue(
                $"Expected success status but got {(int)context.LastResponse.StatusCode} ({context.LastResponse.StatusCode})"
            );
    }

    [Then("the location should be stored as {string}")]
    public void ThenTheLocationShouldBeStoredAs(string key)
    {
        context.LastResponse.Should().NotBeNull();
        var location = context.LastResponse!.Headers.Location?.ToString();
        location.Should().NotBeNullOrEmpty();

        context.DescriptorLocations[key] = location!;
    }

    [When("a GET request is made to instance {string} and resource {string}")]
    public async Task WhenAGetRequestIsMadeToInstanceAndResource(string instanceRoute, string resource)
    {
        _dmsClient.Should().NotBeNull("Must be authenticated to DMS first");

        var parts = instanceRoute.Split('/');
        parts.Should().HaveCount(2, "Instance route must be in format districtId/schoolYear");

        var districtId = parts[0];
        var schoolYear = parts[1];

        Console.WriteLine($"GET from instance route: {instanceRoute}, resource: {resource}");

        context.LastResponse = await _dmsClient!.GetResourceAsync(districtId, schoolYear, resource);

        Console.WriteLine(
            $"Response: {(int)context.LastResponse.StatusCode} ({context.LastResponse.StatusCode})"
        );
        if (!context.LastResponse.IsSuccessStatusCode)
        {
            var responseBody = await context.LastResponse.Content.ReadAsStringAsync();
            Console.WriteLine($"Response body: {responseBody}");
        }
    }

    [Then("the response should contain {string}")]
    public async Task ThenTheResponseShouldContain(string expectedContent)
    {
        context.LastResponse.Should().NotBeNull();
        context.LastResponse!.IsSuccessStatusCode.Should().BeTrue();

        var content = await context.LastResponse.Content.ReadAsStringAsync();
        content.Should().Contain(expectedContent);
    }

    [Then("the response should not contain {string}")]
    public async Task ThenTheResponseShouldNotContain(string unexpectedContent)
    {
        context.LastResponse.Should().NotBeNull();
        context.LastResponse!.IsSuccessStatusCode.Should().BeTrue();

        var content = await context.LastResponse.Content.ReadAsStringAsync();
        content.Should().NotContain(unexpectedContent);
    }

    [When("I GET resource {string} by location")]
    public async Task WhenIGetResourceByLocation(string key)
    {
        _dmsClient.Should().NotBeNull("Must be authenticated to DMS first");
        context.DescriptorLocations.Should().ContainKey(key);

        var location = context.DescriptorLocations[key];
        context.LastResponse = await _dmsClient!.GetByLocationAsync(location);
    }

    [When("a GET request is made to discovery endpoint with route {string}")]
    public async Task WhenAGetRequestIsMadeToDiscoveryEndpointWithRoute(string route)
    {
        // Discovery endpoints are public and don't require authentication
        var discoveryClient = new DmsApiClient(TestConfiguration.DmsApiUrl, "");

        Console.WriteLine($"GET discovery endpoint with route: '{route}'");

        context.LastResponse = await discoveryClient.GetDiscoveryWithRouteAsync(route);

        Console.WriteLine(
            $"Response: {(int)context.LastResponse.StatusCode} ({context.LastResponse.StatusCode})"
        );
        if (!context.LastResponse.IsSuccessStatusCode)
        {
            var responseBody = await context.LastResponse.Content.ReadAsStringAsync();
            Console.WriteLine($"Response body: {responseBody}");
        }
    }

    [Then("the urls should be")]
    public async Task ThenTheUrlsShouldBe(string expectedJson)
    {
        context.LastResponse.Should().NotBeNull();
        context.LastResponse!.IsSuccessStatusCode.Should().BeTrue();

        var responseBody = await context.LastResponse.Content.ReadAsStringAsync();
        Console.WriteLine($"Response body: {responseBody}");

        var responseDoc = JsonDocument.Parse(responseBody);
        var actualUrls = responseDoc.RootElement.GetProperty("urls");

        var expectedDoc = JsonDocument.Parse(expectedJson);
        var expectedUrls = expectedDoc.RootElement;

        // Compare each URL property
        foreach (var expectedProperty in expectedUrls.EnumerateObject())
        {
            var propertyName = expectedProperty.Name;
            var expectedValue = expectedProperty.Value.GetString();

            actualUrls
                .TryGetProperty(propertyName, out var actualProperty)
                .Should()
                .BeTrue($"Response should contain URL property '{propertyName}'");

            var actualValue = actualProperty.GetString();
            actualValue
                .Should()
                .Be(expectedValue, $"URL property '{propertyName}' should match expected value");
        }

        // Ensure no extra properties in actual response
        var actualPropertyCount = actualUrls.EnumerateObject().Count();
        var expectedPropertyCount = expectedUrls.EnumerateObject().Count();
        actualPropertyCount
            .Should()
            .Be(expectedPropertyCount, "Response should not contain extra URL properties");
    }
}
