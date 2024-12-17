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
public partial class StepDefinitions(PlaywrightContext _playwrightContext)
{
    private IAPIResponse _apiResponse = null!;
    private string _token = string.Empty;
    private string _vendorId = string.Empty;
    private string _id = string.Empty;
    private string _location = string.Empty;

    private IDictionary<string, string> _authHeaders =>
        new Dictionary<string, string>
        {
            { "Content-Type", "application/json" },
            { "Authorization", $"Bearer {_token}" },
        };

    #region Given
    [Given("valid credentials")]
    public async Task GivenValidCredentials()
    {
        var urlEncodedData = new Dictionary<string, string>
        {
            { "client_id", "DmsConfigurationService" },
            { "client_secret", "s3creT@09" },
            { "grant_type", "client_credentials" },
            { "scope", "edfi_admin_api/full_access" },
        };
        var content = new FormUrlEncodedContent(urlEncodedData);
        APIRequestContextOptions? options = new()
        {
            Headers = new Dictionary<string, string>
            {
                { "Content-Type", "application/x-www-form-urlencoded" },
            },
            Data = content.ReadAsStringAsync().Result,
        };
        if (_playwrightContext.ApiRequestContext != null)
        {
            _apiResponse = await _playwrightContext.ApiRequestContext!.PostAsync("/connect/token", options);
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

    [Given("vendor created")]
    public async Task VendorCreated(string body)
    {
        APIRequestContextOptions? options = new() { Headers = _authHeaders, Data = body };
        _apiResponse = await _playwrightContext.ApiRequestContext?.PostAsync("/v2/vendors", options)!;
        _vendorId = extractDataFromResponseAndReturnIdIfAvailable(_apiResponse);
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

            string body = row.Parse().Replace("_vendorId", _vendorId);

            var response = await _playwrightContext.ApiRequestContext?.PostAsync(
                dataUrl,
                new() { Data = body, Headers = _authHeaders }
            )!;
            _apiResponses.Add(response);

            response
                .Status.Should()
                .BeOneOf(OkCreated, $"POST request for {entityType} failed:\n{response.TextAsync().Result}");
        }

        return _apiResponses;
    }

    private readonly int[] OkCreated = [200, 201];

    #endregion

    [When("a PUT request is made to {string} with")]
    public async Task WhenAPUTRequestIsMadeToWith(string url, string body)
    {
        url = url.Replace("{id}", _id);
        body = ReplacePlaceholdersInRequest(body);

        _apiResponse = await _playwrightContext.ApiRequestContext?.PutAsync(
            url,
            new() { Data = body, Headers = _authHeaders }
        )!;

        extractDataFromResponseAndReturnIdIfAvailable(_apiResponse);
    }

    [When("a GET request is made to {string}")]
    public async Task WhenAGETRequestIsMadeTo(string url)
    {
        url = url.Replace("{id}", _id).Replace("{vendorId}", _vendorId);
        _apiResponse = await _playwrightContext.ApiRequestContext?.GetAsync(
            url,
            new() { Headers = _authHeaders }
        )!;
    }

    [When("a POST request is made to {string} with")]
    public async Task WhenSendingAPOSTRequestToWithBody(string url, string body)
    {
        APIRequestContextOptions? options = new()
        {
            Headers = _authHeaders,
            Data = ReplacePlaceholdersInRequest(body),
        };
        _apiResponse = await _playwrightContext.ApiRequestContext?.PostAsync(url, options)!;
        _id = extractDataFromResponseAndReturnIdIfAvailable(_apiResponse);
    }

    [When("a DELETE request is made to {string}")]
    public async Task WhenADELETERequestIsMadeTo(string url)
    {
        url = url.Replace("{id}", _id);
        _apiResponse = await _playwrightContext.ApiRequestContext?.DeleteAsync(
            url,
            new() { Headers = _authHeaders }
        )!;
    }

    private string extractDataFromResponseAndReturnIdIfAvailable(IAPIResponse apiResponse)
    {
        if (apiResponse.Headers.TryGetValue("location", out string? value))
        {
            _location = value;
#pragma warning disable S6608 // Prefer indexing instead of "Enumerable" methods on types implementing "IList"
            return _location.Split('/').Last();
#pragma warning restore S6608 // Prefer indexing instead of "Enumerable" methods on types implementing "IList"
        }
        if (apiResponse.Status == 400)
        {
            // This is here to help step through debugging when there is an
            // unexpected error while doing background setup, in which case
            // it is difficult to ever see the error details.
#pragma warning disable S1481 // Unused local variables should be removed
            var errorOnPostOrPutRequest = _apiResponse.TextAsync().Result;
#pragma warning restore S1481 // Unused local variables should be removed
        }

        return string.Empty;
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
            var expectedValue = header.Value!.ToString();

            if (expectedValue.Contains("{id}"))
            {
                _apiResponse.Headers[header.Key].Should().EndWith(expectedValue.Replace("{id}", _id));
            }
            else
            {
                string? key = _apiResponse.Headers.Keys.FirstOrDefault(k =>
                    k.Equals(header.Key, StringComparison.OrdinalIgnoreCase)
                );

                if (key != null)
                {
                    _apiResponse.Headers[key].Should().Contain(expectedValue);
                }
            }
        }
    }

    [Then("the record can be retrieved with a GET request")]
    public async Task ThenTheRecordCanBeRetrievedWithAGETRequest(string expectedBody)
    {
        _apiResponse = await _playwrightContext.ApiRequestContext?.GetAsync(
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

    private async Task ResponseBodyIs(string expectedBody)
    {
        // Parse the API response to JsonNode
        string responseJsonString = await _apiResponse.TextAsync();
        JsonDocument responseJsonDoc = JsonDocument.Parse(responseJsonString);
        JsonNode responseJson = JsonNode.Parse(responseJsonDoc.RootElement.ToString())!;

        expectedBody = ReplacePlaceholdersInResponse(expectedBody, responseJson);
        JsonNode expectedBodyJson = JsonNode.Parse(expectedBody)!;

        (responseJson as JsonObject)?.Remove("correlationId");
        (expectedBodyJson as JsonObject)?.Remove("correlationId");

        AreEqual(expectedBodyJson, responseJson)
            .Should()
            .BeTrue($"Expected:\n{expectedBodyJson}\n\nActual:\n{responseJson}");
    }

    private string ReplacePlaceholdersInResponse(string body, JsonNode responseJson)
    {
        var replacements = new Dictionary<string, Regex>()
        {
            { "id", new(@"\{id\}") },
            { "vendorId", new(@"\{vendorId\}") },
            { "key", new(@"\{key\}") },
            { "secret", new(@"\{secret\}") },
        };

        string replacedBody = body;
        foreach (var replacement in replacements)
        {
            if (replacedBody.TrimStart().StartsWith('['))
            {
                var responseAsArray =
                    responseJson.AsArray()
                    ?? throw new AssertionException(
                        "Expected a JSON array response, but it was not an array."
                    );
                if (responseAsArray.Count == 0)
                {
                    return replacedBody;
                }

                int index = 0;

                replacedBody = replacement.Value.Replace(
                    replacedBody,
                    match =>
                    {
                        var idValue = responseJson[index]?[replacement.Key]?.ToString();
                        index++;
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
                        return idValue ?? match.ToString();
                    }
                );
            }
            replacedBody = replacedBody.Replace("{BASE_URL}/", _playwrightContext.ApiUrl);
        }

        return replacedBody;
    }

    private string ReplacePlaceholdersInRequest(string body)
    {
        return body.Replace("{id}", _id).Replace("{vendorId}", _vendorId);
    }
}
