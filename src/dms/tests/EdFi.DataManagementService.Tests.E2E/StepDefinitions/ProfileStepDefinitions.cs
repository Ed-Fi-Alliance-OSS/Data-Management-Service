// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Tests.E2E.Authorization;
using EdFi.DataManagementService.Tests.E2E.Extensions;
using EdFi.DataManagementService.Tests.E2E.Management;
using FluentAssertions;
using Microsoft.Playwright;
using Reqnroll;

namespace EdFi.DataManagementService.Tests.E2E.StepDefinitions;

/// <summary>
/// Step definitions for profile-related E2E tests.
/// Handles profile authorization setup, profile header requests, and filtered response validation.
/// </summary>
[Binding]
public class ProfileStepDefinitions(
    PlaywrightContext playwrightContext,
    TestLogger logger,
    ScenarioContext scenarioContext
)
{
    private readonly PlaywrightContext _playwrightContext = playwrightContext;
    private readonly TestLogger _logger = logger;
    private readonly ScenarioContext _scenarioContext = scenarioContext;

    private IAPIResponse _apiResponse = null!;
    private string _id = string.Empty;
    private string _location = string.Empty;
    private string _dmsToken = string.Empty;
    private readonly ScenarioVariables _scenarioVariables = new();

    #region Given - Authorization with Profiles

    /// <summary>
    /// Sets up authorization with a single profile assigned to the application.
    /// </summary>
    [Given(
        @"the claimSet ""([^""]*)"" is authorized with profile ""([^""]*)"" and namespacePrefixes ""([^""]*)"""
    )]
    public async Task GivenTheClaimSetIsAuthorizedWithProfileAndNamespaces(
        string claimSetName,
        string profileName,
        string namespacePrefixes
    )
    {
        await SetAuthorizationTokenWithProfiles(namespacePrefixes, string.Empty, claimSetName, profileName);
    }

    /// <summary>
    /// Sets up authorization with multiple profiles assigned to the application.
    /// Profile names should be comma-separated.
    /// </summary>
    [Given(
        @"the claimSet ""([^""]*)"" is authorized with profiles ""([^""]*)"" and namespacePrefixes ""([^""]*)"""
    )]
    public async Task GivenTheClaimSetIsAuthorizedWithProfilesAndNamespaces(
        string claimSetName,
        string profileNames,
        string namespacePrefixes
    )
    {
        await SetAuthorizationTokenWithProfiles(
            namespacePrefixes,
            string.Empty,
            claimSetName,
            ParseCommaSeparatedProfiles(profileNames)
        );
    }

    /// <summary>
    /// Sets up authorization with multiple profiles assigned to the application.
    /// Profile names should be comma-separated.
    /// </summary>
    [Given(
        @"the claimSet ""([^""]*)"" is authorized with profiles ""([^""]*)"" and namespacePrefixes ""([^""]*)"" and educationOrganizationIds ""([^""]*)"""
    )]
    public async Task GivenTheClaimSetIsAuthorizedWithProfilesAndNamespacesAndEducationOrganizationIds(
        string claimSetName,
        string profileNames,
        string namespacePrefixes,
        string educationOrganizationIds
    )
    {
        await SetAuthorizationTokenWithProfiles(
            namespacePrefixes,
            educationOrganizationIds,
            claimSetName,
            ParseCommaSeparatedProfiles(profileNames)
        );
    }

    /// <summary>
    /// Sets up authorization without any profiles (for comparison testing).
    /// </summary>
    [Given(@"the claimSet ""([^""]*)"" is authorized without profiles and namespacePrefixes ""([^""]*)""")]
    public async Task GivenTheClaimSetIsAuthorizedWithoutProfilesAndNamespaces(
        string claimSetName,
        string namespacePrefixes
    )
    {
        await SetAuthorizationTokenWithProfiles(namespacePrefixes, string.Empty, claimSetName);
    }

    private async Task SetAuthorizationTokenWithProfiles(
        string namespacePrefixes,
        string educationOrganizationIds,
        string claimSetName,
        params string[] profileNames
    )
    {
        await ProfileAwareAuthorizationProvider.CreateClientCredentialsWithProfiles(
            Guid.NewGuid().ToString(),
            "Profile Test User",
            "profiletest@example.com",
            namespacePrefixes,
            educationOrganizationIds,
            SystemAdministrator.Token,
            claimSetName,
            profileNames
        );

        string bearerToken = await ProfileAwareAuthorizationProvider.GetToken();
        _dmsToken = $"Bearer {bearerToken}";
        _scenarioContext["dmsToken"] = _dmsToken;

        // Store assigned profiles for use in POST requests (multi-profile apps require Content-Type)
        _scenarioContext["assignedProfiles"] = profileNames;
    }

    #endregion

    #region Given - Data Setup

    /// <summary>
    /// Creates descriptors needed for the test data.
    /// Uses a non-profile token for descriptor creation to avoid profile-based resource restrictions.
    /// The profile test user's token may be restricted from creating descriptors not in the profile.
    /// </summary>
    [Given(@"the system has these descriptors")]
    [Scope(Feature = "Profile Response Filtering")]
    [Scope(Feature = "Profile Resolution")]
    [Scope(Feature = "Profile Header Validation")]
    [Scope(Feature = "Profile Collection Item Filtering")]
    [Scope(Feature = "Profile Extension Filtering")]
    [Scope(Feature = "Profile Write Filtering")]
    [Scope(Feature = "Profile Creatability Validation")]
    [Scope(Feature = "Profile PUT Merge Functionality")]
    [Scope(Feature = "Profile Nested Identity Preservation")]
    public async Task GivenTheSystemHasTheseDescriptors(DataTable dataTable)
    {
        string descriptorToken = await GetTokenForExtensionDescriptors();
        var descriptorHeaders = new List<KeyValuePair<string, string>>
        {
            new("Authorization", $"Bearer {descriptorToken}"),
        };

        foreach (DataTableRow row in dataTable.Rows)
        {
            string descriptorValue = row["descriptorValue"];
            (string descriptorName, Dictionary<string, object> descriptorBody) = ExtractDescriptorBody(
                descriptorValue
            );

            string url = $"data/ed-fi/{descriptorName}";
            _logger.log.Information($"Creating descriptor: {descriptorName} at {url}");

            IAPIResponse response = await _playwrightContext.ApiRequestContext?.PostAsync(
                url,
                new() { DataObject = descriptorBody, Headers = descriptorHeaders }
            )!;

            string body = await response.TextAsync();
            _logger.log.Information($"Descriptor response: {response.Status} - {body}");
        }
    }

    /// <summary>
    /// Creates entities needed for the test data (e.g., schools, schoolYearTypes).
    /// Uses a non-profile token with E2E-NoFurtherAuthRequiredClaimSet to have broad permissions.
    /// </summary>
    [Given(@"the system has these ""([^""]*)""")]
    [Scope(Feature = "Profile Nested Identity Preservation")]
    public async Task GivenTheSystemHasTheseEntities(string entityType, DataTable dataTable)
    {
        string token = await GetTokenForPrerequisiteEntities();
        var headers = new List<KeyValuePair<string, string>> { new("Authorization", $"Bearer {token}") };

        // First create any descriptors referenced in the data
        foreach (var descriptor in dataTable.ExtractDescriptors())
        {
            string descriptorName = descriptor["descriptorName"]?.ToString() ?? "";
            string url = $"data/ed-fi/{descriptorName}";
            _logger.log.Information($"Creating prerequisite descriptor: {descriptorName}");

            IAPIResponse response = await _playwrightContext.ApiRequestContext?.PostAsync(
                url,
                new() { DataObject = descriptor, Headers = headers }
            )!;

            string body = await response.TextAsync();
            _logger.log.Information($"Descriptor response: {response.Status} - {body}");

            response
                .Status.Should()
                .BeOneOf([200, 201], $"POST for {entityType} descriptor {descriptorName} failed:\n{body}");
        }

        // Then create the entities
        foreach (var row in dataTable.Rows)
        {
            string url = $"data/ed-fi/{entityType}";
            string body = row.Parse();
            _logger.log.Information($"Creating {entityType} at {url}");

            IAPIResponse response = await _playwrightContext.ApiRequestContext?.PostAsync(
                url,
                new() { Data = body, Headers = headers }
            )!;

            string responseBody = await response.TextAsync();
            _logger.log.Information($"Response: {response.Status} - {responseBody}");

            response.Status.Should().BeOneOf([200, 201], $"POST for {entityType} failed:\n{responseBody}");
        }
    }

    /// <summary>
    /// Gets a token with EdFiSandbox claimset for creating extension descriptors.
    /// The E2E-NoFurtherAuthRequiredClaimSet doesn't include permissions for extension-only
    /// descriptors like CTEProgramServiceDescriptor, so we use EdFiSandbox which does.
    /// </summary>
    private static async Task<string> GetTokenForExtensionDescriptors()
    {
        await ProfileAwareAuthorizationProvider.CreateClientCredentialsWithProfiles(
            $"Descriptor Creator {Guid.NewGuid()}",
            "Descriptor Creator",
            "descriptor@test.com",
            "uri://ed-fi.org, uri://sample.ed-fi.org",
            "",
            SystemAdministrator.Token,
            "EdFiSandbox"
        );

        return await ProfileAwareAuthorizationProvider.GetToken();
    }

    /// <summary>
    /// Gets a token with E2E-NoFurtherAuthRequiredClaimSet for creating prerequisite entities.
    /// This claimset has broad permissions including SchoolYearType which EdFiSandbox lacks.
    /// </summary>
    private static async Task<string> GetTokenForPrerequisiteEntities()
    {
        await ProfileAwareAuthorizationProvider.CreateClientCredentialsWithProfiles(
            $"Entity Creator {Guid.NewGuid()}",
            "Entity Creator",
            "entity@test.com",
            "uri://ed-fi.org",
            "",
            SystemAdministrator.Token,
            "E2E-NoFurtherAuthRequiredClaimSet"
        );

        return await ProfileAwareAuthorizationProvider.GetToken();
    }

    private static (string, Dictionary<string, object>) ExtractDescriptorBody(string descriptorValue)
    {
        // Extract the descriptor type name from the URI (e.g., "CTEProgramServiceDescriptor")
        string descriptorTypeName = descriptorValue.Split('#')[0][(descriptorValue.LastIndexOf('/') + 1)..];

        // Convert to camelCase for API endpoint (e.g., "cteProgramServiceDescriptors")
        // Handles acronyms like 'CTE' by lowercasing all leading uppercase chars
        string descriptorName = ToCamelCase(descriptorTypeName) + 's';

        // eg: "Tenth Grade"
        string codeValue = descriptorValue.Split('#')[1];
        // eg: "uri://ed-fi.org/GradeLevelDescriptor"
        string namespaceName = descriptorValue.Split('#')[0];

        return (
            descriptorName,
            new Dictionary<string, object>()
            {
                { "codeValue", codeValue },
                { "description", codeValue },
                { "namespace", namespaceName },
                { "shortDescription", codeValue },
            }
        );
    }

    /// <summary>
    /// Converts a PascalCase string to camelCase, handling acronyms properly.
    /// E.g., "CTEProgramService" becomes "cteProgramService"
    /// </summary>
    private static string ToCamelCase(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        // Find the index where lowercase starts
        int lowercaseStart = 0;
        for (int i = 0; i < value.Length; i++)
        {
            if (char.IsLower(value[i]))
            {
                lowercaseStart = i;
                break;
            }
        }

        // If all uppercase or single char, lowercase everything
        if (lowercaseStart == 0)
        {
            lowercaseStart = 1;
        }

        // For acronyms like "CTE" followed by "Program", we want "cteProgram"
        // So lowercase all chars up to (but not including) the last uppercase before the lowercase
        if (lowercaseStart > 1)
        {
            lowercaseStart--;
        }

        return value[..lowercaseStart].ToLowerInvariant() + value[lowercaseStart..];
    }

    /// <summary>
    /// Creates a resource using POST and stores the ID for later use.
    /// Uses profile-aware authorization token.
    /// </summary>
    [Given(@"a profile test POST request is made to ""([^""]*)"" with")]
    public async Task GivenAProfileTestPOSTRequestIsMadeToWith(string url, string body)
    {
        url = AddDataPrefixIfNecessary(url);
        await ExecutePostRequest(url, body);

        _apiResponse
            .Status.Should()
            .BeOneOf([200, 201], $"Given post to {url} failed:\n{await _apiResponse.TextAsync()}");
    }

    #endregion

    #region When - Requests with Profile Headers

    /// <summary>
    /// Makes a GET request with an explicit profile Accept header.
    /// Format: application/vnd.ed-fi.{resource}.{profile}.readable+json
    /// </summary>
    [When(@"a GET request is made to ""([^""]*)"" with profile ""([^""]*)"" for resource ""([^""]*)""")]
    public async Task WhenAGETRequestIsMadeToWithProfile(string url, string profileName, string resourceName)
    {
        url = AddDataPrefixIfNecessary(url)
            .Replace("{id}", _id)
            .ReplacePlaceholdersWithDictionaryValues(_scenarioVariables.VariableByName);

        // Both resource name and profile name are lowercased per Ed-Fi convention
        string acceptHeader =
            $"application/vnd.ed-fi.{resourceName.ToLowerInvariant()}.{profileName.ToLowerInvariant()}.readable+json";

        _logger.log.Information($"GET url: {url}");
        _logger.log.Information($"Accept header: {acceptHeader}");

        var headers = new List<KeyValuePair<string, string>>
        {
            new("Authorization", _dmsToken),
            new("Accept", acceptHeader),
        };

        _apiResponse = await _playwrightContext.ApiRequestContext?.GetAsync(
            url,
            new() { Headers = headers }
        )!;

        _logger.log.Information($"Response status: {_apiResponse.Status}");
        _logger.log.Information($"Response body: {await _apiResponse.TextAsync()}");
    }

    /// <summary>
    /// Makes a GET request without an Accept header (for implicit profile testing).
    /// </summary>
    [When(@"a GET request is made to ""([^""]*)"" without profile header")]
    public async Task WhenAGETRequestIsMadeToWithoutProfileHeader(string url)
    {
        url = AddDataPrefixIfNecessary(url)
            .Replace("{id}", _id)
            .ReplacePlaceholdersWithDictionaryValues(_scenarioVariables.VariableByName);

        _logger.log.Information($"GET url (no profile header): {url}");

        var headers = new List<KeyValuePair<string, string>> { new("Authorization", _dmsToken) };

        _apiResponse = await _playwrightContext.ApiRequestContext?.GetAsync(
            url,
            new() { Headers = headers }
        )!;

        _logger.log.Information($"Response status: {_apiResponse.Status}");
        _logger.log.Information($"Response body: {await _apiResponse.TextAsync()}");
    }

    /// <summary>
    /// Makes a GET request with a custom Accept header value (for error testing).
    /// </summary>
    [When(@"a GET request is made to ""([^""]*)"" with Accept header ""([^""]*)""")]
    public async Task WhenAGETRequestIsMadeToWithAcceptHeader(string url, string acceptHeader)
    {
        url = AddDataPrefixIfNecessary(url)
            .Replace("{id}", _id)
            .ReplacePlaceholdersWithDictionaryValues(_scenarioVariables.VariableByName);

        _logger.log.Information($"GET url: {url}");
        _logger.log.Information($"Accept header: {acceptHeader}");

        var headers = new List<KeyValuePair<string, string>>
        {
            new("Authorization", _dmsToken),
            new("Accept", acceptHeader),
        };

        _apiResponse = await _playwrightContext.ApiRequestContext?.GetAsync(
            url,
            new() { Headers = headers }
        )!;

        _logger.log.Information($"Response status: {_apiResponse.Status}");
        _logger.log.Information($"Response body: {await _apiResponse.TextAsync()}");
    }

    /// <summary>
    /// Makes a POST request with an explicit profile Content-Type header for write filtering tests.
    /// Format: application/vnd.ed-fi.{resource}.{profile}.writable+json
    /// </summary>
    [When(
        @"a POST request is made to ""([^""]*)"" with profile ""([^""]*)"" for resource ""([^""]*)"" with body"
    )]
    public async Task WhenAPOSTRequestIsMadeToWithProfileForResourceWithBody(
        string url,
        string profileName,
        string resourceName,
        string body
    )
    {
        url = AddDataPrefixIfNecessary(url);

        // Build Content-Type header with profile's writable format
        string contentType =
            $"application/vnd.ed-fi.{resourceName.ToLowerInvariant()}.{profileName.ToLowerInvariant()}.writable+json";

        _logger.log.Information($"POST url: {url}");
        _logger.log.Information($"Content-Type header: {contentType}");
        _logger.log.Information($"POST body: {body}");

        var headers = new List<KeyValuePair<string, string>>
        {
            new("Authorization", _dmsToken),
            new("Content-Type", contentType),
        };

        _apiResponse = await _playwrightContext.ApiRequestContext?.PostAsync(
            url,
            new() { Data = body, Headers = headers }
        )!;

        _logger.log.Information($"Response status: {_apiResponse.Status}");
        _logger.log.Information($"Response body: {await _apiResponse.TextAsync()}");

        ExtractIdFromResponse();
    }

    /// <summary>
    /// Makes a PUT request with an explicit profile Content-Type header for write filtering tests.
    /// Format: application/vnd.ed-fi.{resource}.{profile}.writable+json
    /// </summary>
    [When(
        @"a PUT request is made to ""([^""]*)"" with profile ""([^""]*)"" for resource ""([^""]*)"" with body"
    )]
    public async Task WhenAPUTRequestIsMadeToWithProfileForResourceWithBody(
        string url,
        string profileName,
        string resourceName,
        string body
    )
    {
        url = AddDataPrefixIfNecessary(url)
            .Replace("{id}", _id)
            .ReplacePlaceholdersWithDictionaryValues(_scenarioVariables.VariableByName);

        // Replace {id} placeholder in body with actual id
        body = body.Replace("{id}", _id);

        // Build Content-Type header with profile's writable format
        string contentType =
            $"application/vnd.ed-fi.{resourceName.ToLowerInvariant()}.{profileName.ToLowerInvariant()}.writable+json";

        _logger.log.Information($"PUT url: {url}");
        _logger.log.Information($"Content-Type header: {contentType}");
        _logger.log.Information($"PUT body: {body}");

        var headers = new List<KeyValuePair<string, string>>
        {
            new("Authorization", _dmsToken),
            new("Content-Type", contentType),
        };

        _apiResponse = await _playwrightContext.ApiRequestContext?.PutAsync(
            url,
            new() { Data = body, Headers = headers }
        )!;

        _logger.log.Information($"Response status: {_apiResponse.Status}");
        _logger.log.Information($"Response body: {await _apiResponse.TextAsync()}");
    }

    #endregion

    #region Then - Response Validation

    /// <summary>
    /// Verifies the response status code.
    /// </summary>
    [Then(@"the profile response status is (\d+)")]
    public void ThenTheProfileResponseStatusIs(int expectedStatus)
    {
        string body = _apiResponse.TextAsync().Result;
        _logger.log.Information($"Validating status {expectedStatus}, actual: {_apiResponse.Status}");
        _apiResponse.Status.Should().Be(expectedStatus, body);
    }

    /// <summary>
    /// Verifies that the response body contains only the specified fields (plus identity fields).
    /// Fields should be comma-separated.
    /// </summary>
    [Then(@"the response body should only contain fields ""([^""]*)""")]
    public async Task ThenTheResponseBodyShouldOnlyContainFields(string expectedFieldsCsv)
    {
        string responseBody = await _apiResponse.TextAsync();
        JsonNode responseJson = JsonNode.Parse(responseBody)!;

        // Handle both single object and array responses
        JsonObject[] objects = responseJson is JsonArray jsonArray
            ? jsonArray.Select(item => item!.AsObject()).ToArray()
            : [responseJson.AsObject()];

        HashSet<string> expectedFields = expectedFieldsCsv
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(f => f.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Identity fields and metadata are always included
        expectedFields.Add("id");
        expectedFields.Add("_lastModifiedDate");
        expectedFields.Add("_etag");

        foreach (JsonObject obj in objects)
        {
            IEnumerable<string> actualFields = obj.Select(kvp => kvp.Key);

            foreach (string actualField in actualFields)
            {
                expectedFields
                    .Should()
                    .Contain(
                        actualField,
                        $"Field '{actualField}' was present but not expected. Response: {obj}"
                    );
            }
        }
    }

    /// <summary>
    /// Verifies that the response body does not contain the specified fields.
    /// Fields should be comma-separated.
    /// </summary>
    [Then(@"the response body should not contain fields ""([^""]*)""")]
    public async Task ThenTheResponseBodyShouldNotContainFields(string excludedFieldsCsv)
    {
        string responseBody = await _apiResponse.TextAsync();
        JsonNode responseJson = JsonNode.Parse(responseBody)!;

        // Handle both single object and array responses
        JsonObject[] objects = responseJson is JsonArray jsonArray
            ? jsonArray.Select(item => item!.AsObject()).ToArray()
            : [responseJson.AsObject()];

        HashSet<string> excludedFields = excludedFieldsCsv
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(f => f.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (JsonObject obj in objects)
        {
            foreach (string excludedField in excludedFields)
            {
                obj.ContainsKey(excludedField)
                    .Should()
                    .BeFalse($"Field '{excludedField}' should not be present. Response: {obj}");
            }
        }
    }

    /// <summary>
    /// Verifies that the response body contains the specified fields.
    /// Fields should be comma-separated.
    /// </summary>
    [Then(@"the response body should contain fields ""([^""]*)""")]
    public async Task ThenTheResponseBodyShouldContainFields(string requiredFieldsCsv)
    {
        string responseBody = await _apiResponse.TextAsync();
        JsonNode responseJson = JsonNode.Parse(responseBody)!;

        // Handle both single object and array responses
        JsonObject[] objects = responseJson is JsonArray jsonArray
            ? jsonArray.Select(item => item!.AsObject()).ToArray()
            : [responseJson.AsObject()];

        HashSet<string> requiredFields = requiredFieldsCsv
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(f => f.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (JsonObject obj in objects)
        {
            foreach (string requiredField in requiredFields)
            {
                obj.ContainsKey(requiredField)
                    .Should()
                    .BeTrue($"Field '{requiredField}' should be present. Response: {obj}");
            }
        }
    }

    /// <summary>
    /// Verifies that a collection field contains only items matching the filter criteria.
    /// </summary>
    [Then(@"the ""([^""]*)"" collection should only contain items where ""([^""]*)"" is ""([^""]*)""")]
    public async Task ThenTheCollectionShouldOnlyContainItemsWhere(
        string collectionName,
        string propertyName,
        string expectedValue
    )
    {
        string responseBody = await _apiResponse.TextAsync();
        JsonNode responseJson = JsonNode.Parse(responseBody)!;

        // Handle both single object and array responses
        JsonObject[] rootObjects = responseJson is JsonArray jsonArray
            ? jsonArray.Select(item => item!.AsObject()).ToArray()
            : [responseJson.AsObject()];

        foreach (JsonObject rootObject in rootObjects)
        {
            if (rootObject.TryGetPropertyValue(collectionName, out JsonNode? collectionNode))
            {
                JsonArray collection = collectionNode!.AsArray();

                foreach (JsonNode? item in collection)
                {
                    if (
                        item is JsonObject itemObj
                        && itemObj.TryGetPropertyValue(propertyName, out JsonNode? propValue)
                    )
                    {
                        string? actualValue = propValue?.ToString();
                        actualValue
                            .Should()
                            .Be(
                                expectedValue,
                                $"Collection item property '{propertyName}' should be '{expectedValue}' but was '{actualValue}'"
                            );
                    }
                }
            }
        }
    }

    /// <summary>
    /// Verifies that a collection field does not contain items matching the filter criteria.
    /// </summary>
    [Then(@"the ""([^""]*)"" collection should not contain items where ""([^""]*)"" is ""([^""]*)""")]
    public async Task ThenTheCollectionShouldNotContainItemsWhere(
        string collectionName,
        string propertyName,
        string excludedValue
    )
    {
        string responseBody = await _apiResponse.TextAsync();
        JsonNode responseJson = JsonNode.Parse(responseBody)!;

        // Handle both single object and array responses
        JsonObject[] rootObjects = responseJson is JsonArray jsonArray
            ? jsonArray.Select(item => item!.AsObject()).ToArray()
            : [responseJson.AsObject()];

        foreach (JsonObject rootObject in rootObjects)
        {
            if (rootObject.TryGetPropertyValue(collectionName, out JsonNode? collectionNode))
            {
                JsonArray collection = collectionNode!.AsArray();

                foreach (JsonNode? item in collection)
                {
                    if (
                        item is JsonObject itemObj
                        && itemObj.TryGetPropertyValue(propertyName, out JsonNode? propValue)
                    )
                    {
                        string? actualValue = propValue?.ToString();
                        actualValue
                            .Should()
                            .NotBe(
                                excludedValue,
                                $"Collection should not contain items where '{propertyName}' is '{excludedValue}'"
                            );
                    }
                }
            }
        }
    }

    /// <summary>
    /// Verifies that the response body contains a value at the specified JSON path.
    /// Path uses dot notation (e.g., "_ext.sample.isExemplary").
    /// </summary>
    [Then(@"the response body should contain path ""([^""]*)""")]
    public async Task ThenTheResponseBodyShouldContainPath(string jsonPath)
    {
        string responseBody = await _apiResponse.TextAsync();
        JsonNode responseJson = JsonNode.Parse(responseBody)!;

        // Handle both single object and array responses
        JsonObject[] objects = responseJson is JsonArray jsonArray
            ? jsonArray.Select(item => item!.AsObject()).ToArray()
            : [responseJson.AsObject()];

        string[] pathParts = jsonPath.Split('.');

        foreach (JsonObject obj in objects)
        {
            JsonNode? current = obj;

            foreach (string part in pathParts)
            {
                if (
                    current is JsonObject currentObj
                    && currentObj.TryGetPropertyValue(part, out JsonNode? next)
                )
                {
                    current = next;
                }
                else
                {
                    throw new AssertionException(
                        $"Path '{jsonPath}' not found in response. Failed at '{part}'. Response: {obj}"
                    );
                }
            }
        }
    }

    /// <summary>
    /// Verifies that the response body does not contain the specified JSON path.
    /// Path uses dot notation (e.g., "_ext.sample.cteProgramService").
    /// </summary>
    [Then(@"the response body should not contain path ""([^""]*)""")]
    public async Task ThenTheResponseBodyShouldNotContainPath(string jsonPath)
    {
        string responseBody = await _apiResponse.TextAsync();
        JsonNode responseJson = JsonNode.Parse(responseBody)!;

        // Handle both single object and array responses
        JsonObject[] objects = responseJson is JsonArray jsonArray
            ? jsonArray.Select(item => item!.AsObject()).ToArray()
            : [responseJson.AsObject()];

        string[] pathParts = jsonPath.Split('.');

        foreach (JsonObject obj in objects)
        {
            JsonNode? current = obj;
            bool pathExists = true;

            foreach (string part in pathParts)
            {
                if (
                    current is JsonObject currentObj
                    && currentObj.TryGetPropertyValue(part, out JsonNode? next)
                )
                {
                    current = next;
                }
                else
                {
                    pathExists = false;
                    break;
                }
            }

            pathExists
                .Should()
                .BeFalse($"Path '{jsonPath}' should not exist in response but was found. Response: {obj}");
        }
    }

    /// <summary>
    /// Verifies that the response body contains a specific value at the specified JSON path.
    /// Path uses dot notation (e.g., "_ext.sample.isExemplary").
    /// </summary>
    [Then(@"the response body path ""([^""]*)"" should have value ""([^""]*)""")]
    public async Task ThenTheResponseBodyPathShouldHaveValue(string jsonPath, string expectedValue)
    {
        string responseBody = await _apiResponse.TextAsync();
        JsonNode responseJson = JsonNode.Parse(responseBody)!;

        // Handle both single object and array responses
        JsonObject[] objects = responseJson is JsonArray jsonArray
            ? jsonArray.Select(item => item!.AsObject()).ToArray()
            : [responseJson.AsObject()];

        string[] pathParts = jsonPath.Split('.');

        foreach (JsonObject obj in objects)
        {
            JsonNode? current = obj;

            foreach (string part in pathParts)
            {
                if (
                    current is JsonObject currentObj
                    && currentObj.TryGetPropertyValue(part, out JsonNode? next)
                )
                {
                    current = next;
                }
                else
                {
                    throw new AssertionException(
                        $"Path '{jsonPath}' not found in response. Failed at '{part}'. Response: {obj}"
                    );
                }
            }

            string? actualValue = current?.ToString();
            actualValue
                .Should()
                .Be(
                    expectedValue,
                    $"Path '{jsonPath}' should have value '{expectedValue}' but was '{actualValue}'"
                );
        }
    }

    /// <summary>
    /// Verifies the response body contains a specific error type URN.
    /// </summary>
    [Then(@"the response body should have error type ""([^""]*)""")]
    public async Task ThenTheResponseBodyShouldHaveErrorType(string expectedType)
    {
        string responseBody = await _apiResponse.TextAsync();
        JsonNode responseJson = JsonNode.Parse(responseBody)!;

        if (responseJson is JsonObject jsonObject)
        {
            if (jsonObject.TryGetPropertyValue("type", out JsonNode? typeNode))
            {
                string? actualType = typeNode?.ToString();
                actualType.Should().Contain(expectedType, $"Response: {responseBody}");
            }
            else
            {
                throw new AssertionException($"Response does not contain 'type' field: {responseBody}");
            }
        }
    }

    /// <summary>
    /// Verifies the response body errors array contains a specific message.
    /// </summary>
    [Then(@"the response body should have error message ""([^""]*)""")]
    public async Task ThenTheResponseBodyShouldHaveErrorMessage(string expectedMessage)
    {
        string responseBody = await _apiResponse.TextAsync();
        JsonNode responseJson = JsonNode.Parse(responseBody)!;

        if (responseJson is JsonObject jsonObject)
        {
            if (
                jsonObject.TryGetPropertyValue("errors", out JsonNode? errorsNode)
                && errorsNode is JsonArray errorsArray
            )
            {
                List<string?> errorMessages = errorsArray.Select(e => e?.ToString()).ToList();
                errorMessages
                    .Should()
                    .Contain(
                        e => e != null && e.Contains(expectedMessage),
                        $"Expected errors to contain '{expectedMessage}'. Actual errors: {string.Join(", ", errorMessages)}. Response: {responseBody}"
                    );
            }
            else
            {
                throw new AssertionException($"Response does not contain 'errors' array: {responseBody}");
            }
        }
    }

    /// <summary>
    /// Verifies that a collection item at a specific index has a property with the expected value.
    /// </summary>
    [Then(@"the ""([^""]*)"" collection item at index (\d+) should have ""([^""]*)"" value ""([^""]*)""")]
    public async Task ThenTheCollectionItemAtIndexShouldHaveValue(
        string collectionName,
        int index,
        string propertyName,
        string expectedValue
    )
    {
        string responseBody = await _apiResponse.TextAsync();
        JsonNode responseJson = JsonNode.Parse(responseBody)!;

        JsonObject rootObject = responseJson is JsonArray jsonArray
            ? jsonArray[0]!.AsObject()
            : responseJson.AsObject();

        if (rootObject.TryGetPropertyValue(collectionName, out JsonNode? collectionNode))
        {
            JsonArray collection = collectionNode!.AsArray();

            collection
                .Count.Should()
                .BeGreaterThan(index, $"Collection '{collectionName}' does not have item at index {index}");

            JsonObject item = collection[index]!.AsObject();

            if (item.TryGetPropertyValue(propertyName, out JsonNode? propValue))
            {
                string? actualValue = propValue?.ToString();
                actualValue
                    .Should()
                    .Be(
                        expectedValue,
                        $"Collection item property '{propertyName}' at index {index} should be '{expectedValue}' but was '{actualValue}'"
                    );
            }
            else
            {
                throw new AssertionException(
                    $"Collection item at index {index} does not have property '{propertyName}'. Item: {item}"
                );
            }
        }
        else
        {
            throw new AssertionException(
                $"Collection '{collectionName}' not found in response: {responseBody}"
            );
        }
    }

    /// <summary>
    /// Verifies that the collection has a specific count.
    /// </summary>
    [Then(@"the ""([^""]*)"" collection should have (\d+) items?")]
    public async Task ThenTheCollectionShouldHaveItems(string collectionName, int expectedCount)
    {
        string responseBody = await _apiResponse.TextAsync();
        JsonNode responseJson = JsonNode.Parse(responseBody)!;

        JsonObject rootObject = responseJson is JsonArray jsonArray
            ? jsonArray[0]!.AsObject()
            : responseJson.AsObject();

        if (rootObject.TryGetPropertyValue(collectionName, out JsonNode? collectionNode))
        {
            JsonArray collection = collectionNode!.AsArray();
            collection
                .Count.Should()
                .Be(
                    expectedCount,
                    $"Collection '{collectionName}' should have {expectedCount} items but has {collection.Count}"
                );
        }
        else
        {
            if (expectedCount == 0)
            {
                // Collection not present is equivalent to empty
                return;
            }

            throw new AssertionException(
                $"Collection '{collectionName}' not found in response: {responseBody}"
            );
        }
    }

    #endregion

    #region Helper Methods

    private static string[] ParseCommaSeparatedProfiles(string profileNames)
    {
        return profileNames.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).ToArray();
    }

    private async Task ExecutePostRequest(string url, string body)
    {
        _logger.log.Information($"POST url: {url}");
        _logger.log.Information($"POST body: {body}");

        // Build headers, including Content-Type for multi-profile apps
        var headers = GetHeadersForPost(url);

        _apiResponse = await _playwrightContext.ApiRequestContext?.PostAsync(
            url,
            new() { Data = body, Headers = headers }
        )!;

        _logger.log.Information($"Response status: {_apiResponse.Status}");
        _logger.log.Information($"Response body: {await _apiResponse.TextAsync()}");

        ExtractIdFromResponse();
    }

    private void ExtractIdFromResponse()
    {
        if (_apiResponse.Headers.TryGetValue("location", out string? value))
        {
            _location = value;
            string[] segments = _location.Split('/');
            _id = segments[^1];

            // Only add to scenario variables if not already defined (first POST).
            // Subsequent POSTs update the instance field but don't overwrite the scenario variable.
            if (!_scenarioVariables.VariableByName.TryGetValue("id", out _))
            {
                _scenarioVariables.Add("id", _id);
            }
        }
    }

    private static string AddDataPrefixIfNecessary(string input)
    {
        if (input == "/")
        {
            return input;
        }

        input = input.StartsWith('/') ? input[1..] : input;
        input = input.StartsWith("metadata") ? input : $"data/{input}";

        return input;
    }

    /// <summary>
    /// Gets headers for POST requests, including Content-Type for multi-profile applications.
    /// When an application has multiple profiles assigned, DMS requires a Content-Type header
    /// specifying which profile's WriteContentType to use.
    /// </summary>
    private List<KeyValuePair<string, string>> GetHeadersForPost(string url)
    {
        var headers = new List<KeyValuePair<string, string>> { new("Authorization", _dmsToken) };

        // Check if we have multiple profiles assigned
        if (
            _scenarioContext.TryGetValue("assignedProfiles", out object? profilesObj)
            && profilesObj is string[] profiles
            && profiles.Length > 1
        )
        {
            // Extract resource name from URL (e.g., "data/ed-fi/schools" -> "school")
            string resourceName = ExtractResourceNameFromUrl(url);

            // Use the first profile for the Content-Type header
            string profileName = profiles[0];
            string contentType =
                $"application/vnd.ed-fi.{resourceName.ToLowerInvariant()}.{profileName}.writable+json";

            _logger.log.Information($"Multi-profile POST - Content-Type: {contentType}");
            headers.Add(new("Content-Type", contentType));
        }

        return headers;
    }

    /// <summary>
    /// Extracts the singular resource name from a URL path.
    /// E.g., "data/ed-fi/schools" -> "school"
    /// </summary>
    private static string ExtractResourceNameFromUrl(string url)
    {
        string[] segments = url.Split('/');
        string pluralName = segments[^1].Split('?')[0]; // Handle query strings

        // Convert plural to singular (simple rule: remove trailing 's')
        if (pluralName.EndsWith("s", StringComparison.OrdinalIgnoreCase))
        {
            return pluralName[..^1];
        }

        return pluralName;
    }

    #endregion
}
