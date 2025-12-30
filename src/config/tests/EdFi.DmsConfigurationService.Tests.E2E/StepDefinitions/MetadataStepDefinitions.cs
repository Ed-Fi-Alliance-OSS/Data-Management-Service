// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Tests.E2E.Management;
using FluentAssertions;
using Reqnroll;

namespace EdFi.DmsConfigurationService.Tests.E2E.StepDefinitions;

[Binding]
public class MetadataStepDefinitions(PlaywrightContext playwrightContext, ScenarioContext scenarioContext)
{
    private readonly PlaywrightContext _playwrightContext = playwrightContext;
    private readonly ScenarioContext _scenarioContext = scenarioContext;

    [Given("the system is ready for E2E testing")]
    public void GivenTheSystemIsReadyForE2ETesting()
    {
        // Verify that the playwright context is initialized
        _playwrightContext.ApiRequestContext.Should().NotBeNull("API context should be initialized");
    }

    [Then("the response contains metadata URLs")]
    public async Task ThenTheResponseContainsMetadataUrls()
    {
        var apiResponse = GetLastApiResponse();
        string responseJsonString = await apiResponse.TextAsync();
        JsonDocument responseJsonDoc = JsonDocument.Parse(responseJsonString);
        JsonNode responseJson = JsonNode.Parse(responseJsonDoc.RootElement.ToString())!;

        // Verify the urls object exists
        responseJson["urls"].Should().NotBeNull("Response should contain urls object");

        // Verify openApiMetadata URL exists
        responseJson["urls"]!
            ["openApiMetadata"]
            .Should()
            .NotBeNull("Response should contain openApiMetadata URL");

        var openApiUrl = responseJson["urls"]!["openApiMetadata"]!.ToString();
        openApiUrl.Should().NotBeNullOrEmpty("OpenAPI metadata URL should not be empty");
    }

    [Then("the response should be valid JSON")]
    public async Task ThenTheResponseShouldBeValidJson()
    {
        var apiResponse = GetLastApiResponse();
        string responseJsonString = await apiResponse.TextAsync();

        // Attempt to parse the JSON - this will throw if invalid
        Action parseAction = () => JsonDocument.Parse(responseJsonString);
        parseAction.Should().NotThrow("Response should be valid JSON");
    }

    [Then("the response body contains OpenAPI specification")]
    public async Task ThenTheResponseBodyContainsOpenApiSpecification(Table table)
    {
        var apiResponse = GetLastApiResponse();
        string responseJsonString = await apiResponse.TextAsync();
        JsonDocument responseJsonDoc = JsonDocument.Parse(responseJsonString);
        JsonNode responseJson = JsonNode.Parse(responseJsonDoc.RootElement.ToString())!;

        foreach (var row in table.Rows)
        {
            var field = row["Field"];
            var expectedValue = row["Value"];

            // Handle nested properties (e.g., "info.title")
            var fieldParts = field.Split('.');
            JsonNode? currentNode = responseJson;

            foreach (var part in fieldParts)
            {
                currentNode.Should().NotBeNull($"Field path '{field}' should exist");
                currentNode = currentNode![part];
            }

            currentNode.Should().NotBeNull($"Field '{field}' should have a value");
            currentNode!
                .ToString()
                .Should()
                .Be(expectedValue, $"Field '{field}' should match expected value");
        }
    }

    [Then("the OpenAPI specification should have required sections")]
    public async Task ThenTheOpenApiSpecificationShouldHaveRequiredSections(Table table)
    {
        var apiResponse = GetLastApiResponse();
        string responseJsonString = await apiResponse.TextAsync();
        JsonDocument responseJsonDoc = JsonDocument.Parse(responseJsonString);
        JsonNode responseJson = JsonNode.Parse(responseJsonDoc.RootElement.ToString())!;

        foreach (var row in table.Rows)
        {
            var section = row["Section"];
            responseJson[section].Should().NotBeNull($"OpenAPI spec should have '{section}' section");
        }
    }

    [Then("the OpenAPI components should include")]
    public async Task ThenTheOpenApiComponentsShouldInclude(Table table)
    {
        var apiResponse = GetLastApiResponse();
        string responseJsonString = await apiResponse.TextAsync();
        JsonDocument responseJsonDoc = JsonDocument.Parse(responseJsonString);
        JsonNode responseJson = JsonNode.Parse(responseJsonDoc.RootElement.ToString())!;

        responseJson["components"].Should().NotBeNull("OpenAPI spec should have 'components' section");

        var components = responseJson["components"]!;

        foreach (var row in table.Rows)
        {
            var component = row["Component"];
            components[component].Should().NotBeNull($"Components section should include '{component}'");
        }
    }

    [Then("the OpenAPI specification should have OAuth2 security scheme")]
    public async Task ThenTheOpenApiSpecificationShouldHaveOAuth2SecurityScheme(Table table)
    {
        var apiResponse = GetLastApiResponse();
        string responseJsonString = await apiResponse.TextAsync();
        JsonDocument responseJsonDoc = JsonDocument.Parse(responseJsonString);
        JsonNode responseJson = JsonNode.Parse(responseJsonDoc.RootElement.ToString())!;

        responseJson["components"].Should().NotBeNull("OpenAPI spec should have 'components' section");
        responseJson["components"]!
            ["securitySchemes"]
            .Should()
            .NotBeNull("Components should have 'securitySchemes'");

        var securitySchemes = responseJson["components"]!["securitySchemes"]!;

        // Find OAuth2 security scheme
        JsonNode? oauth2Scheme = null;
        foreach (var property in securitySchemes.AsObject())
        {
            if (property.Value?["type"]?.ToString() == "oauth2")
            {
                oauth2Scheme = property.Value;
                break;
            }
        }

        oauth2Scheme.Should().NotBeNull("Security schemes should include OAuth2");

        // Verify OAuth2 flows and scopes
        oauth2Scheme!["flows"].Should().NotBeNull("OAuth2 scheme should have flows");

        // Check for client credentials flow scopes
        var flows = oauth2Scheme["flows"]!;
        JsonNode? scopesNode = null;

        // Try different OAuth2 flow types
        if (flows["clientCredentials"] != null)
        {
            scopesNode = flows["clientCredentials"]!["scopes"];
        }
        else if (flows["authorizationCode"] != null)
        {
            scopesNode = flows["authorizationCode"]!["scopes"];
        }

        scopesNode.Should().NotBeNull("OAuth2 flow should have scopes defined");

        var scopes = scopesNode!.AsObject();

        foreach (var row in table.Rows)
        {
            var scope = row["Scope"];
            scopes.Should().ContainKey(scope, $"OAuth2 scopes should include '{scope}'");
        }
    }

    [Then("the OpenAPI paths should include")]
    public async Task ThenTheOpenApiPathsShouldInclude(Table table)
    {
        var apiResponse = GetLastApiResponse();
        string responseJsonString = await apiResponse.TextAsync();
        JsonDocument responseJsonDoc = JsonDocument.Parse(responseJsonString);
        JsonNode responseJson = JsonNode.Parse(responseJsonDoc.RootElement.ToString())!;

        responseJson["paths"].Should().NotBeNull("OpenAPI spec should have 'paths' section");

        var paths = responseJson["paths"]!.AsObject();

        foreach (var row in table.Rows)
        {
            var path = row["Path"];
            paths.Should().ContainKey(path, $"OpenAPI paths should include '{path}'");
        }
    }

    [Given("a GET request is made to {string}")]
    public async Task GivenAGetRequestIsMadeTo(string url)
    {
        var apiResponse = await _playwrightContext.ApiRequestContext!.GetAsync(url);
        _scenarioContext["lastApiResponse"] = apiResponse;
    }

    [When("the response URLs are extracted")]
    public async Task WhenTheResponseUrlsAreExtracted()
    {
        var apiResponse = GetLastApiResponse();
        string responseJsonString = await apiResponse.TextAsync();
        JsonDocument responseJsonDoc = JsonDocument.Parse(responseJsonString);
        JsonNode responseJson = JsonNode.Parse(responseJsonDoc.RootElement.ToString())!;

        responseJson["urls"].Should().NotBeNull("Response should contain urls object");

        _scenarioContext["extractedUrls"] = responseJson["urls"];
    }

    [Then("each metadata URL should be valid")]
    public async Task ThenEachMetadataUrlShouldBeValid(Table table)
    {
        var urls = _scenarioContext.Get<JsonNode>("extractedUrls");

        foreach (var row in table.Rows)
        {
            var urlField = row["URL Field"];
            urls[urlField].Should().NotBeNull($"URL field '{urlField}' should exist");

            var url = urls[urlField]!.ToString();
            url.Should().NotBeNullOrEmpty($"URL field '{urlField}' should not be empty");

            // Verify the URL is accessible by making a GET request
            var response = await _playwrightContext.ApiRequestContext!.GetAsync(url);
            response.Status.Should().Be(200, $"Metadata URL '{urlField}' at '{url}' should be accessible");
        }
    }

    private Microsoft.Playwright.IAPIResponse GetLastApiResponse()
    {
        // Try to get from scenario context first (for steps that store it)
        if (_scenarioContext.ContainsKey("lastApiResponse"))
        {
            return _scenarioContext.Get<Microsoft.Playwright.IAPIResponse>("lastApiResponse");
        }

        // Otherwise, get it from the base StepDefinitions class
        var stepDefinitions = _scenarioContext.ScenarioContainer.Resolve<StepDefinitions>();
        var responseField = stepDefinitions
            .GetType()
            .GetField(
                "_apiResponse",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance
            );

        var response = responseField?.GetValue(stepDefinitions) as Microsoft.Playwright.IAPIResponse;
        response.Should().NotBeNull("API response should be available");
        return response!;
    }
}
