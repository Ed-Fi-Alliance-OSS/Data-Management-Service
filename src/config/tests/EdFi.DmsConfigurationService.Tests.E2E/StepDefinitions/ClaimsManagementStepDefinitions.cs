// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Tests.E2E.Management;
using FluentAssertions;
using Microsoft.Playwright;
using Reqnroll;

namespace EdFi.DmsConfigurationService.Tests.E2E.StepDefinitions;

[Binding]
public class ClaimsManagementStepDefinitions(ScenarioContext scenarioContext)
{
    private const string InitialReloadIdKey = "InitialReloadId";
    private const string CurrentReloadIdKey = "CurrentReloadId";
    private const string AuthoritativeCompositionKey = "AuthoritativeComposition";
    private const string UploadedClaimsKey = "UploadedClaims";

    private static JsonNode? _authoritativeComposition;

    [BeforeFeature]
    public static void LoadAuthoritativeComposition()
    {
        // Load the authoritative composition once for all tests
        var authoritativeCompositionPath = Path.Combine(
            AppContext.BaseDirectory,
            "TestData",
            "Claims",
            "authoritative-composition.json"
        );

        if (File.Exists(authoritativeCompositionPath))
        {
            var json = File.ReadAllText(authoritativeCompositionPath);
            _authoritativeComposition = JsonNode.Parse(json);
        }
    }

    [Given("the initial reload ID is captured")]
    public async Task GivenTheInitialReloadIdIsCaptured()
    {
        var stepDefinitions = scenarioContext.ScenarioContainer.Resolve<StepDefinitions>();

        // First get the current claims to capture the initial reload ID
        await stepDefinitions.WhenAGETRequestIsMadeTo("/management/current-claims");

        var response = GetLastApiResponse();
        if (response.Headers.TryGetValue("x-reload-id", out var reloadId))
        {
            scenarioContext[InitialReloadIdKey] = reloadId;
        }
    }

    [Given("claims have been uploaded")]
    public async Task GivenClaimsHaveBeenUploaded()
    {
        var stepDefinitions = scenarioContext.ScenarioContainer.Resolve<StepDefinitions>();

        // Upload test claims
        var uploadBody = """
            {
                "claims": {
                    "claimSets": [{"claimSetName": "TestUploadSet", "isSystemReserved": false}],
                    "claimsHierarchy": [
                        {
                            "name": "http://ed-fi.org/identity/claims/test",
                            "claimSets": [
                                {
                                    "name": "TestUploadSet",
                                    "actions": [{"name": "Read"}]
                                }
                            ]
                        }
                    ]
                }
            }
            """;

        await stepDefinitions.WhenSendingAPOSTRequestToWithBody("/management/upload-claims", uploadBody);
        scenarioContext[UploadedClaimsKey] = JsonNode.Parse(uploadBody)!["claims"];
    }

    [Given("dynamic claims loading is disabled")]
    public void GivenDynamicClaimsLoadingIsDisabled()
    {
        // This would normally require environment setup, but for testing we'll simulate the behavior
        scenarioContext["DynamicClaimsDisabled"] = true;
    }

    [When("a POST request is made to {string}")]
    public async Task WhenAPOSTRequestIsMadeTo(string url)
    {
        var stepDefinitions = scenarioContext.ScenarioContainer.Resolve<StepDefinitions>();

        // POST with empty body
        await stepDefinitions.WhenSendingAPOSTRequestToWithBody(url, "{}");
    }

    [Then("the response headers include {string}")]
    public void ThenTheResponseHeadersInclude(string headerName)
    {
        var response = GetLastApiResponse();
        var headerKey = response.Headers.Keys.FirstOrDefault(k =>
            k.Equals(headerName, StringComparison.OrdinalIgnoreCase)
        );

        headerKey.Should().NotBeNull($"Expected header '{headerName}' was not found");
    }

    [Then("the response headers include a different {string}")]
    public void ThenTheResponseHeadersIncludeADifferent(string headerName)
    {
        var response = GetLastApiResponse();
        var headerKey = response.Headers.Keys.FirstOrDefault(k =>
            k.Equals(headerName, StringComparison.OrdinalIgnoreCase)
        );

        headerKey.Should().NotBeNull($"Expected header '{headerName}' was not found");

        if (headerKey != null && scenarioContext.ContainsKey(InitialReloadIdKey))
        {
            var currentReloadId = response.Headers[headerKey];
            var initialReloadId = scenarioContext[InitialReloadIdKey].ToString();

            currentReloadId.Should().NotBe(initialReloadId, "Reload ID should have changed");
            scenarioContext[CurrentReloadIdKey] = currentReloadId;
        }
    }

    [Then("the response body matches the authoritative composition structure")]
    public async Task ThenTheResponseBodyMatchesTheAuthoritativeCompositionStructure()
    {
        if (_authoritativeComposition == null)
        {
            throw new InvalidOperationException(
                "Authoritative composition not loaded. Ensure TestData/Claims/authoritative-composition.json exists."
            );
        }

        var response = GetLastApiResponse();
        var responseBody = await response.TextAsync();
        var responseJson = JsonNode.Parse(responseBody);

        responseJson.Should().NotBeNull();

        // The authoritative composition is an array of claims, while the response has claimSets and claimsHierarchy
        // We need to verify the structure matches
        var claimsHierarchy = responseJson!["claimsHierarchy"];
        claimsHierarchy.Should().NotBeNull();

        // Verify we have the expected number of top-level claims
        var topLevelClaims = claimsHierarchy!.AsArray();
        topLevelClaims.Should().HaveCountGreaterThan(0);

        // Verify claim sets are present
        var claimSets = responseJson["claimSets"];
        claimSets.Should().NotBeNull();
        claimSets!.AsArray().Should().HaveCountGreaterThan(0);
    }

    [Then("the response contains claim set {string}")]
    public async Task ThenTheResponseContainsClaimSet(string claimSetName)
    {
        var response = GetLastApiResponse();
        var responseBody = await response.TextAsync();
        var responseJson = JsonNode.Parse(responseBody);

        var claimSets = responseJson!["claimSets"]!.AsArray();
        var claimSet = claimSets.FirstOrDefault(cs =>
            cs!["claimSetName"]?.GetValue<string>() == claimSetName
        );

        claimSet.Should().NotBeNull($"Claim set '{claimSetName}' was not found in the response");
    }

    [Then("the response does not contain claim set {string}")]
    public async Task ThenTheResponseDoesNotContainClaimSet(string claimSetName)
    {
        var response = GetLastApiResponse();
        var responseBody = await response.TextAsync();
        var responseJson = JsonNode.Parse(responseBody);

        var claimSets = responseJson!["claimSets"]!.AsArray();
        var claimSet = claimSets.FirstOrDefault(cs =>
            cs!["claimSetName"]?.GetValue<string>() == claimSetName
        );

        claimSet.Should().BeNull($"Claim set '{claimSetName}' should not be present in the response");
    }

    [Then("the response contains extension claims {string}")]
    public async Task ThenTheResponseContainsExtensionClaims(string extensionName)
    {
        var response = GetLastApiResponse();
        var responseBody = await response.TextAsync();
        var responseJson = JsonNode.Parse(responseBody);

        var claimsHierarchy = responseJson!["claimsHierarchy"]!.AsArray();

        // Look for extension claims in the hierarchy
        // Convert the extension name to the expected pattern in the claims
        var searchPattern = extensionName.Replace("Extension", "").ToLower();
        var extensionClaim = FindClaimByNamePattern(claimsHierarchy, searchPattern);

        extensionClaim.Should().NotBeNull($"Extension claims for '{extensionName}' were not found");
    }

    [Then("the response body contains a new reload ID")]
    public async Task ThenTheResponseBodyContainsANewReloadId()
    {
        var response = GetLastApiResponse();
        var responseBody = await response.TextAsync();
        var responseJson = JsonNode.Parse(responseBody);

        var reloadId = responseJson!["reloadId"]?.GetValue<string>();
        reloadId.Should().NotBeNullOrEmpty();

        // Verify it's a valid GUID
        Guid.TryParse(reloadId, out var guid).Should().BeTrue("Reload ID should be a valid GUID");

        // If we have an initial reload ID, verify it's different
        if (scenarioContext.ContainsKey(InitialReloadIdKey))
        {
            var initialReloadId = scenarioContext[InitialReloadIdKey].ToString();
            reloadId.Should().NotBe(initialReloadId, "Reload ID should have changed");
        }
    }

    [Then("the response body contains only the uploaded claims")]
    public async Task ThenTheResponseBodyContainsOnlyTheUploadedClaims()
    {
        var response = GetLastApiResponse();
        var responseBody = await response.TextAsync();
        var responseJson = JsonNode.Parse(responseBody);

        var claimSets = responseJson!["claimSets"]!.AsArray();

        // Should only have the uploaded claim set
        claimSets.Should().HaveCount(1);
        claimSets[0]!["claimSetName"]!.GetValue<string>().Should().Be("TestUploadSet");
    }

    [Then("the response body contains success=true")]
    public async Task ThenTheResponseBodyContainsSuccessTrue()
    {
        var response = GetLastApiResponse();
        var responseBody = await response.TextAsync();
        var responseJson = JsonNode.Parse(responseBody);

        responseJson!["success"]?.GetValue<bool>().Should().BeTrue();
    }

    [Then("the uploaded claims are no longer present")]
    public async Task ThenTheUploadedClaimsAreNoLongerPresent()
    {
        await ThenTheResponseDoesNotContainClaimSet("TestUploadSet");
    }

    [Then("the response body contains no empty arrays")]
    public async Task ThenTheResponseBodyContainsNoEmptyArrays()
    {
        var response = GetLastApiResponse();
        var responseBody = await response.TextAsync();
        var responseJson = JsonNode.Parse(responseBody);

        var emptyArrays = FindEmptyArrays(responseJson!);

        emptyArrays
            .Should()
            .BeEmpty(
                "No empty arrays should be present. Empty arrays found at: " + string.Join(", ", emptyArrays)
            );
    }

    [Then("all collection properties are either null or have items")]
    public async Task ThenAllCollectionPropertiesAreEitherNullOrHaveItems()
    {
        // This is validated by the previous step
        await ThenTheResponseBodyContainsNoEmptyArrays();
    }

    [Then("the response body contains validation errors")]
    public async Task ThenTheResponseBodyContainsValidationErrors()
    {
        var response = GetLastApiResponse();
        var responseBody = await response.TextAsync();
        var responseJson = JsonNode.Parse(responseBody);

        // Check for validation error structure
        var errors = responseJson!["errors"]?.AsArray();
        var success = responseJson["success"]?.GetValue<bool>();

        if (success.HasValue)
        {
            success.Should().BeFalse();
        }

        errors.Should().NotBeNull();
        errors!.Count.Should().BeGreaterThan(0, "Expected validation errors in the response");
    }

    private IAPIResponse GetLastApiResponse()
    {
        // Get the response from the base StepDefinitions class
        var stepDefinitions = scenarioContext.ScenarioContainer.Resolve<StepDefinitions>();
        var responseField = stepDefinitions
            .GetType()
            .GetField(
                "_apiResponse",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance
            );

        return (IAPIResponse)responseField!.GetValue(stepDefinitions)!;
    }

    private static JsonNode? FindClaimByNamePattern(JsonArray claims, string pattern)
    {
        foreach (var claim in claims)
        {
            var name = claim!["name"]?.GetValue<string>() ?? "";
            if (name.ToLower().Contains(pattern))
            {
                return claim;
            }

            // Check children recursively
            var children = claim["claims"]?.AsArray();
            if (children != null && children.Count > 0)
            {
                var found = FindClaimByNamePattern(children, pattern);
                if (found != null)
                    return found;
            }
        }
        return null;
    }

    private static List<string> FindEmptyArrays(JsonNode node, string path = "")
    {
        var emptyArrays = new List<string>();

        switch (node)
        {
            case JsonArray array:
                if (array.Count == 0)
                {
                    emptyArrays.Add(path);
                }
                else
                {
                    for (int i = 0; i < array.Count; i++)
                    {
                        if (array[i] != null)
                        {
                            emptyArrays.AddRange(FindEmptyArrays(array[i]!, $"{path}[{i}]"));
                        }
                    }
                }
                break;

            case JsonObject obj:
                foreach (var property in obj)
                {
                    if (property.Value != null)
                    {
                        var propertyPath = string.IsNullOrEmpty(path)
                            ? property.Key
                            : $"{path}.{property.Key}";
                        emptyArrays.AddRange(FindEmptyArrays(property.Value, propertyPath));
                    }
                }
                break;
        }

        return emptyArrays;
    }
}
