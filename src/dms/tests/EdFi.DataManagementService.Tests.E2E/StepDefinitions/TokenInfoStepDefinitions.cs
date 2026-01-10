// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Tests.E2E.Management;
using FluentAssertions;
using Microsoft.Playwright;
using Reqnroll;

namespace EdFi.DataManagementService.Tests.E2E.StepDefinitions;

[Binding]
public sealed class TokenInfoStepDefinitions
{
    private readonly PlaywrightContext _playwrightContext;
    private readonly TestLogger _logger;
    private readonly ScenarioContext _scenarioContext;

    private IAPIResponse _apiResponse = null!;
    private JsonNode? _responseBody;
    private string _currentToken = string.Empty;

    public TokenInfoStepDefinitions(
        PlaywrightContext playwrightContext,
        TestLogger logger,
        ScenarioContext scenarioContext
    )
    {
        _playwrightContext = playwrightContext;
        _logger = logger;
        _scenarioContext = scenarioContext;
    }

    [When("a POST request is made to {string} with the current bearer token")]
    public async Task WhenPostRequestWithCurrentToken(string endpoint)
    {
        // Get the current token from scenario context (set by authorization step)
        _currentToken = _scenarioContext.Get<string>("token");

        var requestBody = new { token = _currentToken };
        var json = JsonSerializer.Serialize(requestBody);

        _apiResponse = await _playwrightContext.ApiRequestContext!.PostAsync(
            endpoint,
            new APIRequestContextOptions
            {
                Data = json,
                Headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" },
            }
        );

        var responseText = await _apiResponse.TextAsync();
        if (!string.IsNullOrEmpty(responseText))
        {
            try
            {
                _responseBody = JsonNode.Parse(responseText);
            }
            catch
            {
                _logger.log.Information("Response is not valid JSON: {ResponseText}", responseText);
            }
        }
    }

    [When("a POST request is made to {string} with form-encoded token")]
    public async Task WhenPostRequestWithFormEncodedToken(string endpoint)
    {
        _currentToken = _scenarioContext.Get<string>("token");

        var formData = new Dictionary<string, string> { ["token"] = _currentToken };
        var content = new FormUrlEncodedContent(formData);

        _apiResponse = await _playwrightContext.ApiRequestContext!.PostAsync(
            endpoint,
            new APIRequestContextOptions
            {
                Data = await content.ReadAsStringAsync(),
                Headers = new Dictionary<string, string>
                {
                    ["Content-Type"] = "application/x-www-form-urlencoded",
                },
            }
        );

        var responseText = await _apiResponse.TextAsync();
        if (!string.IsNullOrEmpty(responseText))
        {
            try
            {
                _responseBody = JsonNode.Parse(responseText);
            }
            catch
            {
                _logger.log.Information("Response is not valid JSON: {ResponseText}", responseText);
            }
        }
    }

    [When("a POST request is made to {string} without a token")]
    public async Task WhenPostRequestWithoutToken(string endpoint)
    {
        var requestBody = new { };
        var json = JsonSerializer.Serialize(requestBody);

        _apiResponse = await _playwrightContext.ApiRequestContext!.PostAsync(
            endpoint,
            new APIRequestContextOptions
            {
                Data = json,
                Headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" },
            }
        );
    }

    [When("a POST request is made to {string} with an invalid token")]
    public async Task WhenPostRequestWithInvalidToken(string endpoint)
    {
        var invalidToken = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.invalid.signature";
        var requestBody = new { token = invalidToken };
        var json = JsonSerializer.Serialize(requestBody);

        _apiResponse = await _playwrightContext.ApiRequestContext!.PostAsync(
            endpoint,
            new APIRequestContextOptions
            {
                Data = json,
                Headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" },
            }
        );

        var responseText = await _apiResponse.TextAsync();
        if (!string.IsNullOrEmpty(responseText))
        {
            try
            {
                _responseBody = JsonNode.Parse(responseText);
            }
            catch
            {
                _logger.log.Information("Response is not valid JSON: {ResponseText}", responseText);
            }
        }
    }

    [When("a POST request is made to {string} with token {string}")]
    public async Task WhenPostRequestWithSpecificToken(string endpoint, string token)
    {
        var requestBody = new { token };
        var json = JsonSerializer.Serialize(requestBody);

        _apiResponse = await _playwrightContext.ApiRequestContext!.PostAsync(
            endpoint,
            new APIRequestContextOptions
            {
                Data = json,
                Headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" },
            }
        );

        var responseText = await _apiResponse.TextAsync();
        if (!string.IsNullOrEmpty(responseText))
        {
            try
            {
                _responseBody = JsonNode.Parse(responseText);
            }
            catch
            {
                _logger.log.Information("Response is not valid JSON: {ResponseText}", responseText);
            }
        }
    }

    [When("a POST request is made to {string} without authorization")]
    public async Task WhenPostRequestWithoutAuthorization(string endpoint)
    {
        // Send request with no body and minimal headers (no Authorization header)
        _apiResponse = await _playwrightContext.ApiRequestContext!.PostAsync(
            endpoint,
            new APIRequestContextOptions { Data = string.Empty }
        );
    }

    [Then("the token info response should contain {string}")]
    public void ThenTokenInfoResponseShouldContain(string propertyName)
    {
        _responseBody.Should().NotBeNull();
        _responseBody![propertyName].Should().NotBeNull($"{propertyName} should be present in response");
    }

    [Then("the token info response should have at least {int} education organization")]
    [Then("the token info response should have at least {int} education organizations")]
    public void ThenTokenInfoResponseShouldHaveEducationOrganizations(int count)
    {
        _responseBody.Should().NotBeNull();
        var edOrgs = _responseBody!["education_organizations"];
        edOrgs.Should().NotBeNull();
        edOrgs!.AsArray().Should().NotBeEmpty();
        edOrgs!.AsArray().Count.Should().BeGreaterThanOrEqualTo(count);
    }

    [Then("the token info resources should use pluralized endpoint names")]
    public void ThenResourcesShouldUsePluralizedEndpointNames()
    {
        _responseBody.Should().NotBeNull();
        var resources = _responseBody!["resources"];
        resources.Should().NotBeNull();
        resources!.AsArray().Should().NotBeEmpty();

        // Check that resource paths end with 's' (pluralized)
        foreach (var resource in resources.AsArray())
        {
            var resourcePath = resource!["resource"]!.GetValue<string>();
            resourcePath.Should().NotBeNullOrEmpty();
            // Most Ed-Fi resources should be pluralized (ending with 's')
            // Examples: /ed-fi/students, /ed-fi/schools, /ed-fi/academicWeeks
            var segments = resourcePath.Split('/');
            var lastSegment = segments[segments.Length - 1];
            lastSegment.Should().MatchRegex("s$", "resources should be pluralized");
        }
    }

    [Then("the token info resources should include operations")]
    public void ThenResourcesShouldIncludeOperations()
    {
        _responseBody.Should().NotBeNull();
        var resources = _responseBody!["resources"];
        resources.Should().NotBeNull();
        resources!.AsArray().Should().NotBeEmpty();

        foreach (var resource in resources.AsArray())
        {
            var operations = resource!["operations"];
            operations.Should().NotBeNull();
            operations!.AsArray().Should().NotBeEmpty("each resource should have at least one operation");
        }
    }

    [Then("the token is marked as inactive")]
    public void ThenTokenIsMarkedAsInactive()
    {
        _responseBody.Should().NotBeNull();
        var active = _responseBody!["active"];
        active.Should().NotBeNull();
        active!.GetValue<bool>().Should().BeFalse("invalid tokens should be marked as inactive");
    }
}
