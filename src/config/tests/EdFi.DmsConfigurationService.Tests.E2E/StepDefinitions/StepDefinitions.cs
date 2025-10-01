// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EdFi.DmsConfigurationService.Tests.E2E.Extensions;
using EdFi.DmsConfigurationService.Tests.E2E.Management;
using FluentAssertions;
using Microsoft.Playwright;
using Reqnroll;
using static EdFi.DmsConfigurationService.Tests.E2E.Management.JsonComparer;

namespace EdFi.DmsConfigurationService.Tests.E2E.StepDefinitions;

[Binding]
public partial class StepDefinitions(PlaywrightContext playwrightContext, ScenarioContext scenarioContext)
{
    private IAPIResponse _apiResponse = null!;
    private string _token = string.Empty;
    private string _location = string.Empty;
    private readonly Dictionary<string, string> _ids = new();
    private string _applicationKey = string.Empty;
    private string _applicationSecret = string.Empty;

    private IDictionary<string, string> _authHeaders =>
        new Dictionary<string, string>
        {
            { "Content-Type", "application/json" },
            { "Authorization", $"Bearer {_token}" },
        };

    [BeforeScenario]
    public void BeforeScenario()
    {
        // This gives us a random string to use in scenarios
        // For example, keycloak clients are not deleted between
        // feature runs even though the database is truncated
        // so we use a random string for new keycloak clients to
        // avoid keycloak errors.
        scenarioContext["ScenarioRunId"] = Guid.NewGuid().ToString();
    }

    #region Given
    [Given("valid credentials")]
    public async Task GivenValidCredentials()
    {
        await GetClientAccessToken("DmsConfigurationService", "s3creT@09", "edfi_admin_api/full_access");
    }

    [Given("client {string} credentials with {string} scope")]
    public async Task GivenValidCredentialsWithScope(string clientId, string scope)
    {
        await GetClientAccessToken(clientId, "s3creT@09", scope);
    }

    private async Task GetClientAccessToken(string key, string secret, string scope)
    {
        var urlEncodedData = new Dictionary<string, string>
        {
            { "client_id", key },
            { "client_secret", secret },
            { "grant_type", "client_credentials" },
            { "scope", scope },
        };
        var content = new FormUrlEncodedContent(urlEncodedData);
        APIRequestContextOptions? options = new()
        {
            Headers = new Dictionary<string, string>
            {
                { "Content-Type", "application/x-www-form-urlencoded" },
            },
            Data = await content.ReadAsStringAsync(),
        };
        if (playwrightContext.ApiRequestContext != null)
        {
            _apiResponse = await playwrightContext.ApiRequestContext!.PostAsync("/connect/token", options);
        }
    }

    [Given("token received")]
    public async Task TokenReceived()
    {
        var jsonResponse = JsonDocument.Parse(await _apiResponse.TextAsync());
        if (jsonResponse.RootElement.TryGetProperty("access_token", out var accessToken))
        {
            _token = accessToken.ToString();
        }
    }

    [Given("token signature manipulated")]
    public void TokenSignatureManipulated()
    {
        var segments = _token.Split('.');
        var signature = segments[2].ToCharArray();
        new Random().Shuffle(signature);
        _token = $"{segments[0]}.{segments[1]}.{signature}";
    }

    [Given("the system has these {string}")]
    public async Task GivenTheSystemHasThese(string entityType, DataTable dataTable)
    {
        _ = await ProcessDataTable(entityType, dataTable);
    }

    private async Task<List<IAPIResponse>> ProcessDataTable(string entityType, DataTable dataTable)
    {
        List<IAPIResponse> _apiResponses = [];
        var baseUrl = $"v2";
        foreach (var row in dataTable.Rows)
        {
            var dataUrl = $"{baseUrl}/{entityType}";

            string body = ReplaceIds(row.Parse());

            var response = await playwrightContext.ApiRequestContext?.PostAsync(
                dataUrl,
                new() { Data = body, Headers = _authHeaders }
            )!;
            _apiResponses.Add(response);

            response
                .Status.Should()
                .BeOneOf(OkCreated, $"POST request for {entityType} failed:\n{await response.TextAsync()}");
        }

        return _apiResponses;
    }

    private readonly int[] OkCreated = [200, 201];

    #endregion

    [When("a PUT request is made to {string} with")]
    public async Task WhenAPUTRequestIsMadeToWith(string url, string body)
    {
        url = await ReplaceIdsAsync(url);
        body = await ReplaceIdsAsync(body);

        _apiResponse = await playwrightContext.ApiRequestContext?.PutAsync(
            url,
            new() { Data = body, Headers = _authHeaders }
        )!;

        await ExtractIdFromHeader(_apiResponse);
    }

    [When("a GET request is made to {string}")]
    public async Task WhenAGETRequestIsMadeTo(string url)
    {
        url = await ReplaceIdsAsync(url);
        _apiResponse = await playwrightContext.ApiRequestContext?.GetAsync(
            url,
            new() { Headers = _authHeaders }
        )!;
    }

    [When("a GET request is made to {string} for first {int} items")]
    public async Task WhenAGETRequestIsMadeTo(string url, int limit)
    {
        url = await ReplaceIdsAsync(url);
        _apiResponse = await playwrightContext.ApiRequestContext?.GetAsync(
            $"{url}?limit={limit}",
            new() { Headers = _authHeaders }
        )!;
    }

    [When("a GET request is made to {string} for next {int} items after skipping {int} items")]
    public async Task WhenAGETRequestIsMadeTo(string url, int limit, int offset)
    {
        url = await ReplaceIdsAsync(url);
        _apiResponse = await playwrightContext.ApiRequestContext?.GetAsync(
            $"{url}?limit={limit}&offset={offset}",
            new() { Headers = _authHeaders }
        )!;
    }

    [When("a POST request is made to {string} with")]
    [Given("a POST request is made to {string} with")]
    public async Task WhenSendingAPOSTRequestToWithBody(string url, string body)
    {
        APIRequestContextOptions? options = new()
        {
            Headers = _authHeaders,
            Data = await ReplaceIdsAsync(body),
        };
        _apiResponse = await playwrightContext.ApiRequestContext?.PostAsync(url, options)!;
        await ExtractIdFromHeader(_apiResponse);
    }

    [When("a Form URL Encoded POST request is made to {string} with")]
    public async Task WhenAFormUrlPostIsMade(string url, DataTable formData)
    {
        Dictionary<string, string> formDataDictionary = formData.Rows.ToDictionary(
            x => x["Key"].ToString(),
            y => ReplaceIds(y["Value"].ToString())
        );
        var content = new FormUrlEncodedContent(formDataDictionary);
        APIRequestContextOptions? options = new()
        {
            Headers = new Dictionary<string, string>
            {
                { "Content-Type", "application/x-www-form-urlencoded" },
            },
            Data = await content.ReadAsStringAsync(),
        };
        if (playwrightContext.ApiRequestContext != null)
        {
            _apiResponse = await playwrightContext.ApiRequestContext!.PostAsync(url, options);
        }
    }

    [When("a DELETE request is made to {string}")]
    public async Task WhenADELETERequestIsMadeTo(string url)
    {
        url = await ReplaceIdsAsync(url);
        _apiResponse = await playwrightContext.ApiRequestContext?.DeleteAsync(
            url,
            new() { Headers = _authHeaders }
        )!;
    }

    private async Task ExtractIdFromHeader(IAPIResponse apiResponse)
    {
        if (apiResponse.Headers.TryGetValue("location", out string? value))
        {
            _location = value; //eg `http://localhost:8081/v2/vendors/57`
            var segments = _location.Split('/');
            var id = segments[^1]; // eg 57
            var resource = segments[^2]; // eg 'vendors'
            var identifier = resource[0..^1] + "Id"; // eg 'vendorId'

            _ids[identifier] = id;
        }

        // Also extract clientId from response body if present (for application creation)
        if (apiResponse.Status == 200 || apiResponse.Status == 201)
        {
            var responseText = await apiResponse.TextAsync();
            if (!string.IsNullOrEmpty(responseText))
            {
                var jsonResponse = JsonDocument.Parse(responseText);
                if (jsonResponse.RootElement.TryGetProperty("id", out var idProperty))
                {
                    // Only set vendorId if no location header was found
                    if (!apiResponse.Headers.ContainsKey("location"))
                    {
                        _ids["vendorId"] = idProperty.GetInt32().ToString();
                    }
                }
                if (jsonResponse.RootElement.TryGetProperty("key", out var keyProperty))
                {
                    // The 'key' from application creation is the clientId
                    _ids["clientId"] = keyProperty.GetString() ?? "";
                }
            }
        }
    }

    [Then("it should respond with {int}")]
    public async Task ThenItShouldRespondWith(int statusCode)
    {
        string body = await _apiResponse.TextAsync();
        _apiResponse.Status.Should().Be(statusCode, body);
    }

    [Then("the response headers include")]
    public void ThenTheResponseHeadersIncludes(string headers)
    {
        var value = JsonNode.Parse(headers)!;
        foreach (var header in value.AsObject())
        {
            var expectedValue = ReplaceIds(header.Value!.ToString());

            string? key = _apiResponse.Headers.Keys.FirstOrDefault(k =>
                k.Equals(header.Key, StringComparison.OrdinalIgnoreCase)
            );

            if (key != null)
            {
                _apiResponse.Headers[key].Should().Contain(expectedValue);
            }
        }
    }

    [Then("the record can be retrieved with a GET request")]
    public async Task ThenTheRecordCanBeRetrievedWithAGETRequest(string expectedBody)
    {
        _apiResponse = await playwrightContext.ApiRequestContext?.GetAsync(
            _location,
            new() { Headers = _authHeaders }
        )!;
        await ResponseBodyIs(expectedBody);
    }

    private static bool AreEqual(JsonNode expectedBodyJson, JsonNode responseJson)
    {
        responseJson = OrderJsonProperties(responseJson);
        expectedBodyJson = OrderJsonProperties(expectedBodyJson);

        JsonElement expectedElement = JsonDocument.Parse(expectedBodyJson.ToJsonString()).RootElement;
        JsonElement responseElement = JsonDocument.Parse(responseJson.ToJsonString()).RootElement;

        return JsonElementEqualityComparer.Instance.Equals(expectedElement, responseElement);
    }

    [Then("the response body is")]
    public async Task ThenTheResponseBodyIs(string expectedBody)
    {
        await ResponseBodyIs(expectedBody);
    }

    [Then(@"the response body is an array with more than one object where each object")]
    public async Task ThenTheResponseBodyIsAnArrayWithMoreThanOneObjectWhere(Table table)
    {
        string content = await _apiResponse.TextAsync();
        var jsonArray = JsonNode.Parse(content)?.AsArray();

        jsonArray.Should().NotBeNull();
        jsonArray.Should().HaveCountGreaterThan(1);

        foreach (var obj in jsonArray!)
        {
            ValidateResponseBodyObject(table, obj);
        }
    }

    [Then(@"the response body is an array with one object where")]
    public async Task ThenTheResponseBodyIsAnArrayWithOneObjectWhere(Table table)
    {
        string content = await _apiResponse.TextAsync();
        var jsonArray = JsonNode.Parse(content)?.AsArray();

        jsonArray.Should().NotBeNull();
        jsonArray.Should().HaveCount(1);

        foreach (var obj in jsonArray!)
        {
            ValidateResponseBodyObject(table, obj);
        }
    }

    private static void ValidateResponseBodyObject(Table table, JsonNode? obj)
    {
        foreach (var row in table.Rows)
        {
            var property = row["property"];
            var expectedValue = row["value / condition"];

            // Make sure property exists
            obj!
                .AsObject()
                .ContainsKey(property)
                .Should()
                .BeTrue("the '{0}' property did not exist on the object", property);

            // Handle literal string checks
            if (expectedValue.StartsWith("\"") && expectedValue.EndsWith("\""))
            {
                var expectedLiteral = expectedValue.Trim('\"');
                obj[property]?.ToString().Should().Be(expectedLiteral);
            }
            // Handle non-empty arrays
            else if (expectedValue.Equals("non-empty array"))
            {
                obj[property]
                    ?.AsArray()
                    .Should()
                    .NotBeNullOrEmpty("property '{0}' was empty or null: {1}", property, obj.ToJsonString());
            }
            // Handle non-empty strings
            else if (expectedValue.Equals("non-empty string"))
            {
                obj[property]?.ToString().Should().NotBeEmpty();
            }
            else
            {
                // We can extend logic here for other condition types in the future
                throw new NotImplementedException(
                    $"Condition '{expectedValue}' not implemented in step definition."
                );
            }
        }
    }

    [Then("the response body has key and secret")]
    public async Task ThenTheResponseBodyHasKeyAndSecret()
    {
        string responseJsonString = await _apiResponse.TextAsync();
        JsonDocument responseJsonDoc = JsonDocument.Parse(responseJsonString);
        JsonNode responseJson = JsonNode.Parse(responseJsonDoc.RootElement.ToString())!;
        responseJson["id"].Should().NotBeNull();
        responseJson["key"].Should().NotBeNull();
        responseJson["secret"].Should().NotBeNull();
    }

    [Then("retrieve created key and secret")]
    public async Task ThenRetrieveKeyAndSecret()
    {
        string responseJsonString = await _apiResponse.TextAsync();
        JsonDocument responseJsonDoc = JsonDocument.Parse(responseJsonString);
        JsonNode responseJson = JsonNode.Parse(responseJsonDoc.RootElement.ToString())!;
        responseJson["id"].Should().NotBeNull();
        responseJson["key"].Should().NotBeNull();
        _applicationKey = responseJson["key"]!.GetValue<string>();
        responseJson["secret"].Should().NotBeNull();
        _applicationSecret = responseJson["secret"]!.GetValue<string>();
    }

    [Then("the token should have {string} scope and {string} namespacePrefix")]
    public async Task ThenValidateTheScope(string claimset, string namespacePrefix)
    {
        await GetClientAccessToken(_applicationKey, _applicationSecret, claimset);
        await TokenReceived();
        _token.Should().NotBeNull();
        var claimsetResponse = JwtTokenValidator.ValidateClaimset(_token, claimset);
        claimsetResponse.Should().BeTrue();

        var namespaceResponse = JwtTokenValidator.ValidateNamespace(_token, namespacePrefix);
        namespaceResponse.Should().BeTrue();
    }

    [Then("the token should have {string} scope and {string} edOrgIds")]
    public async Task ThenValidateTheScopeAndClaim(string claimset, string edOrgIds)
    {
        await GetClientAccessToken(_applicationKey, _applicationSecret, claimset);
        await TokenReceived();
        _token.Should().NotBeNull();
        var claimsetResponse = JwtTokenValidator.ValidateClaimset(_token, claimset);
        claimsetResponse.Should().BeTrue();

        var edOrgResponse = JwtTokenValidator.ValidateEdOrgIds(_token, edOrgIds);
        edOrgResponse.Should().BeTrue();
    }

    private async Task ResponseBodyIs(string expectedBody)
    {
        // Parse the API response to JsonNode
        string responseJsonString = await _apiResponse.TextAsync();
        JsonDocument responseJsonDoc = JsonDocument.Parse(responseJsonString);
        JsonNode responseJson = JsonNode.Parse(responseJsonDoc.RootElement.ToString())!;

        expectedBody = await ReplacePlaceholdersInResponseAsync(expectedBody, responseJson);
        JsonNode expectedBodyJson = JsonNode.Parse(expectedBody)!;

        (responseJson as JsonObject)?.Remove("correlationId");
        (expectedBodyJson as JsonObject)?.Remove("correlationId");

        AreEqual(expectedBodyJson, responseJson)
            .Should()
            .BeTrue($"Expected:\n{expectedBodyJson}\n\nActual:\n{responseJson}");
    }

    private async Task<string> ReplacePlaceholdersInResponseAsync(string body, JsonNode responseJson)
    {
        var replacements = new Dictionary<string, Regex>()
        {
            { "id", new(@"\{id\}") },
            { "vendorId", new(@"\{vendorId\}") },
            { "applicationId", new(@"\{applicationId\}") },
            { "dmsInstanceId", new(@"\{dmsInstanceId\}") },
            { "key", new(@"\{key\}") },
            { "secret", new(@"\{secret\}") },
            { "access_token", new(@"\{access_token\}") },
            { "clientId", new(@"\{clientId\}") },
            { "clientUuid", new(@"\{clientUuid\}") },
        };

        string replacedBody = await ReplaceIdsAsync(body);
        foreach (var replacement in replacements)
        {
            if (replacedBody.TrimStart().StartsWith('['))
            {
                // Handle arrays - replace placeholders in each array element
                var arrayElementIndex = 0;
                replacedBody = replacement.Value.Replace(
                    replacedBody,
                    match =>
                    {
                        var arrayItem = responseJson.AsArray()?[arrayElementIndex];
                        var idValue = arrayItem?[replacement.Key]?.ToString();

                        // Special handling for dmsInstanceId - it's stored in dmsInstanceIds array
                        if (idValue == null && replacement.Key == "dmsInstanceId")
                        {
                            var dmsInstanceIds = arrayItem?["dmsInstanceIds"]?.AsArray();
                            if (dmsInstanceIds != null && dmsInstanceIds.Count > 0)
                            {
                                idValue = dmsInstanceIds[0]?.ToString();
                            }
                        }

                        var index = arrayElementIndex;
                        arrayElementIndex++;
                        return idValue ?? match.ToString();
                    }
                );
            }
            else
            {
                replacedBody = replacement.Value.Replace(
                    replacedBody,
                    match =>
                    {
                        var idValue = responseJson[replacement.Key]?.ToString();

                        // Special handling for dmsInstanceId - it's stored in dmsInstanceIds array
                        if (idValue == null && replacement.Key == "dmsInstanceId")
                        {
                            var dmsInstanceIds = responseJson["dmsInstanceIds"]?.AsArray();
                            if (dmsInstanceIds != null && dmsInstanceIds.Count > 0)
                            {
                                idValue = dmsInstanceIds[0]?.ToString();
                            }
                        }

                        return idValue ?? match.ToString();
                    }
                );
            }
            replacedBody = replacedBody.Replace("{BASE_URL}/", playwrightContext.ApiUrl);
        }

        return replacedBody;
    }

    /// <summary>
    /// Queries ClaimSet ID by name for dynamic resolution
    /// </summary>
    private async Task<int> GetClaimSetIdByNameAsync(string claimSetName)
    {
        var cacheKey = $"ClaimSetId_{claimSetName}";

        // Check if already cached in scenario context
        if (scenarioContext.ContainsKey(cacheKey))
        {
            return (int)scenarioContext[cacheKey];
        }

        // Query the API to get all claim sets
        var response = await playwrightContext.ApiRequestContext?.GetAsync(
            "/v2/claimSets",
            new() { Headers = _authHeaders }
        )!;
        response.Status.Should().Be(200, "Failed to fetch claim sets for ID resolution");

        var responseText = await response.TextAsync();
        var claimSets = JsonDocument.Parse(responseText);

        // Find the claim set with the matching name
        foreach (var claimSet in claimSets.RootElement.EnumerateArray())
        {
            if (
                claimSet.TryGetProperty("name", out var nameProperty)
                && nameProperty.GetString() == claimSetName
                && claimSet.TryGetProperty("id", out var idProperty)
            )
            {
                var id = idProperty.GetInt32();
                scenarioContext[cacheKey] = id; // Cache for future use
                return id;
            }
        }

        throw new InvalidOperationException($"ClaimSet with name '{claimSetName}' not found");
    }

    private string ReplaceIds(string str)
    {
        // Handle {claimSetId:ClaimSetName} pattern synchronously by looking up cached values
        var claimSetPattern = @"\{claimSetId:([^}]+)\}";
        var claimSetMatches = Regex.Matches(str, claimSetPattern);
        foreach (Match match in claimSetMatches)
        {
            var claimSetName = match.Groups[1].Value;
            var cacheKey = $"ClaimSetId_{claimSetName}";

            // Use cached value if available, otherwise use a placeholder that will be resolved later
            if (scenarioContext.ContainsKey(cacheKey))
            {
                var claimSetId = (int)scenarioContext[cacheKey];
                str = str.Replace(match.Value, claimSetId.ToString());
            }
            else
            {
                // For scenarios that need dynamic resolution, we'll handle this in the async version
                // For now, keep the placeholder for later async resolution
            }
        }

        foreach (var key in _ids.Keys)
        {
            // Replace both formats {resourceId} and _resourceId
            str = str.Replace($"{{{key}}}", _ids[key]).Replace($"_{key}", _ids[key]);
        }

        str = str.Replace("{scenarioRunId}", scenarioContext["ScenarioRunId"].ToString())
            .Replace("_scenarioRunId", scenarioContext["ScenarioRunId"].ToString());

        return str;
    }

    private async Task<string> ReplaceIdsAsync(string str)
    {
        // Handle {claimSetId:ClaimSetName} pattern
        var claimSetPattern = @"\{claimSetId:([^}]+)\}";
        var claimSetMatches = Regex.Matches(str, claimSetPattern);
        foreach (Match match in claimSetMatches)
        {
            var claimSetName = match.Groups[1].Value;
            var claimSetId = await GetClaimSetIdByNameAsync(claimSetName);
            str = str.Replace(match.Value, claimSetId.ToString());
        }

        foreach (var key in _ids.Keys)
        {
            // Replace both formats {resourceId} and _resourceId
            str = str.Replace($"{{{key}}}", _ids[key]).Replace($"_{key}", _ids[key]);
        }

        str = str.Replace("{scenarioRunId}", scenarioContext["ScenarioRunId"].ToString())
            .Replace("_scenarioRunId", scenarioContext["ScenarioRunId"].ToString());

        return str;
    }
}
