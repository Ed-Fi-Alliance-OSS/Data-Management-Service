// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EdFi.DataManagementService.Tests.E2E.Authorization;
using EdFi.DataManagementService.Tests.E2E.Extensions;
using EdFi.DataManagementService.Tests.E2E.Management;
using FluentAssertions;
using Json.Schema;
using Microsoft.Playwright;
using Reqnroll;
using static EdFi.DataManagementService.Tests.E2E.Management.JsonComparer;

namespace EdFi.DataManagementService.Tests.E2E.StepDefinitions
{
    [Binding]
    public partial class StepDefinitions(
        PlaywrightContext _playwrightContext,
        TestLogger _logger,
        ScenarioContext _scenarioContext
    )
    {
        private IAPIResponse _apiResponse = null!;
        private string _id = string.Empty;
        private string _location = string.Empty;
        private string _etag = string.Empty;
        private string _dependentId = string.Empty;
        private string _referencedResourceId = string.Empty;
        private string _bearerToken = string.Empty;
        private readonly bool _openSearchEnabled = AppSettings.OpenSearchEnabled;

        #region Given

        [Given("the SIS Vendor is authorized")]
        public async Task GivenTheSisVendorIsAuthorized()
        {
            _bearerToken = await SisAdmin.GetToken();
            _logger.log.Debug(_bearerToken);
        }

        [Given("the Data Management Service must receive a token issued by {string}")]
#pragma warning disable CA1822 // Mark members as static
        public void GivenTheDataManagementServiceMustReceiveATokenIssuedBy(string p0)
#pragma warning restore CA1822 // Mark members as static
        {
            // There is no action to take yet - we haven't developed this functionality
        }

        [Given("user is already authorized")]
#pragma warning disable CA1822 // Mark members as static
        public void GivenUserIsAlreadyAuthorized()
#pragma warning restore CA1822 // Mark members as static
        {
            // There is no action to take yet - we haven't developed this functionality
        }

        [Given("a POST request is made to {string} with")]
        public async Task GivenAPOSTRequestIsMadeToWith(string url, string body)
        {
            url = AddDataPrefixIfNecessary(url);
            await ExecutePostRequest(url, body);

            _apiResponse
                .Status.Should()
                .BeOneOf(OkCreated, $"Given post to {url} failed:\n{_apiResponse.TextAsync().Result}");

            WaitForOpenSearch(_scenarioContext.ScenarioInfo.Tags);
        }

        [Given("there are no schools")]
#pragma warning disable CA1822 // Mark members as static
        public void GivenThereAreNoSchools()
#pragma warning restore CA1822 // Mark members as static
        {
            // There is no action to take - this statement is just a reminder to
            // the reader. Hopefully the statement is really true! We don't have
            // a backend database update mechanism to confirm that there are, in
            // fact, no schools.
        }

        private static (string, Dictionary<string, object>) ExtractDescriptorBody(string descriptorValue)
        {
            // build the descriptor object with string splitting operations

            // eg: "GradeLevelDescriptors"
            var descriptorName =
                descriptorValue.Split('#')[0][(descriptorValue.LastIndexOf('/') + 1)..] + 's';
            // eg: "Tenth Grade"
            var codeValue = descriptorValue.Split('#')[1];
            // eg: "uri://ed-fi.org/GradeLevelDescriptor"
            var namespaceName = descriptorValue.Split('#')[0];

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

        private async Task<List<IAPIResponse>> ProcessDataTable(string entityType, DataTable dataTable)
        {
            List<IAPIResponse> _apiResponses = [];
            var baseUrl = $"data/ed-fi";

            foreach (var descriptor in dataTable.ExtractDescriptors())
            {
                var response = await _playwrightContext.ApiRequestContext?.PostAsync(
                    $"{baseUrl}/{descriptor["descriptorName"]}",
                    new() { DataObject = descriptor }
                )!;
                _apiResponses.Add(response);

                response
                    .Status.Should()
                    .BeOneOf(
                        OkCreated,
                        $"POST request for {entityType} descriptor {descriptor["descriptorName"]} failed:\n{response.TextAsync().Result}"
                    );
            }

            foreach (var row in dataTable.Rows)
            {
                var dataUrl = $"{baseUrl}/{entityType}";

                string body = row.Parse();

                _logger.log.Information(dataUrl);
                var response = await _playwrightContext.ApiRequestContext?.PostAsync(
                    dataUrl,
                    new() { Data = body }
                )!;
                _apiResponses.Add(response);

                response
                    .Status.Should()
                    .BeOneOf(
                        OkCreated,
                        $"POST request for {entityType} failed:\n{response.TextAsync().Result}"
                    );
            }

            return _apiResponses;
        }

        [Given("the system has these {string}")]
        public async Task GivenTheSystemHasThese(string entityType, DataTable dataTable)
        {
            _ = await ProcessDataTable(entityType, dataTable);

            _logger.log.Information($"Responses for Given(the system has these {entityType})");

            WaitForOpenSearch(_scenarioContext.ScenarioInfo.Tags);
        }

        [Given("the system has these descriptors")]
        public async Task GivenTheSystemHasTheseDescriptors(DataTable dataTable)
        {
            _logger.log.Information($"Responses for Given(the system has these descriptors)");

            string baseUrl = $"data/ed-fi";

            foreach (DataTableRow row in dataTable.Rows)
            {
                string descriptorValue = row["descriptorValue"];
                var (descriptorName, descriptorBody) = ExtractDescriptorBody(descriptorValue);

                IAPIResponse apiResponse = await _playwrightContext.ApiRequestContext?.PostAsync(
                    $"{baseUrl}/{descriptorName}",
                    new() { DataObject = descriptorBody }
                )!;

                string body = apiResponse.TextAsync().Result;
                _logger.log.Information(body);

                apiResponse.Status.Should().BeOneOf(OkCreated, $"Request failed:\n{body}");
            }

            WaitForOpenSearch(_scenarioContext.ScenarioInfo.Tags);
        }

        private readonly int[] OkCreated = [200, 201];

        [Given("the system has these {string} references")]
        public async Task GivenTheSystemHasTheseReferences(string entityType, DataTable dataTable)
        {
            var _apiResponses = await ProcessDataTable(entityType, dataTable);

            _logger.log.Information($"Responses for Given(the system has these {entityType} references");

            foreach (var response in _apiResponses)
            {
                if (response.Url.Contains(entityType, StringComparison.InvariantCultureIgnoreCase))
                {
                    // Always saving the LAST id returned. This is very fragile.
                    _referencedResourceId = extractDataFromResponseAndReturnIdIfAvailable(response);
                }
            }
        }

        #endregion

        #region When

        [When("a POST request is made to {string} with")]
        public async Task WhenSendingAPOSTRequestToWithBody(string url, string body)
        {
            url = AddDataPrefixIfNecessary(url);
            await ExecutePostRequest(url, body);
        }

        [When("a POST request is made to {string} with path base {string}")]
        public async Task WhenSendingAPOSTRequestToUrlWithPathBaseWithBody(
            string url,
            string pathBase,
            string body
        )
        {
            url = AddDataPrefixIfNecessary(url);
            if (!string.IsNullOrEmpty(pathBase))
            {
                url = pathBase + "/" + url;
            }
            await ExecutePostRequest(url, body);
        }

        private async Task ExecutePostRequest(string url, string body)
        {
            _logger.log.Information($"POST url: {url}");
            _logger.log.Information($"POST body: {body}");
            _apiResponse = await _playwrightContext.ApiRequestContext?.PostAsync(url, new() { Data = body })!;
            _logger.log.Information(_apiResponse.TextAsync().Result);

            _id = extractDataFromResponseAndReturnIdIfAvailable(_apiResponse);
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

        [When("a POST request is made to {string} with header {string} value {string}")]
        public async Task WhenSendingAPOSTRequestToWithBodyAndCustomHeader(
            string url,
            string header,
            string value,
            string body
        )
        {
            url = AddDataPrefixIfNecessary(url);
            _logger.log.Information($"POST url: {url}");
            _logger.log.Information($"POST body: {body}");

            // Add custom header
            var httpHeaders = new List<KeyValuePair<string, string>> { new(header, value) };

            _apiResponse = await _playwrightContext.ApiRequestContext?.PostAsync(
                url,
                new() { Data = body, Headers = httpHeaders }
            )!;
            _logger.log.Information(_apiResponse.TextAsync().Result);

            _id = extractDataFromResponseAndReturnIdIfAvailable(_apiResponse);
        }

        [When("a POST request is made for dependent resource {string} with")]
        public async Task WhenSendingAPOSTRequestForDependentResourceWithBody(string url, string body)
        {
            url = AddDataPrefixIfNecessary(url);
            _apiResponse = await _playwrightContext.ApiRequestContext?.PostAsync(url, new() { Data = body })!;

            _dependentId = extractDataFromResponseAndReturnIdIfAvailable(_apiResponse);
        }

        [When("a PUT request is made to {string} with")]
        public async Task WhenAPUTRequestIsMadeToWith(string url, string body)
        {
            url = AddDataPrefixIfNecessary(url).Replace("{id}", _id).Replace("{dependentId}", _dependentId);

            body = body.Replace("{id}", _id).Replace("{dependentId}", _dependentId);
            _logger.log.Information($"PUT url: {url}");
            _logger.log.Information($"PUT body: {body}");
            _apiResponse = await _playwrightContext.ApiRequestContext?.PutAsync(url, new() { Data = body })!;

            extractDataFromResponseAndReturnIdIfAvailable(_apiResponse);
        }

        [When("a PUT request is made to referenced resource {string} with")]
        public async Task WhenAPUTRequestIsMadeToReferencedResourceWith(string url, string body)
        {
            url = AddDataPrefixIfNecessary(url).Replace("{id}", _referencedResourceId);

            _logger.log.Information(url);
            body = body.Replace("{id}", _referencedResourceId);
            _logger.log.Information(body);
            _apiResponse = await _playwrightContext.ApiRequestContext?.PutAsync(url, new() { Data = body })!;
            if (_apiResponse.Status != 204)
            {
                var result = _apiResponse.TextAsync().Result;
                _logger.log.Information(result);

                try
                {
                    // Implicitly confirming that we can parse the response as JSON
                    _ = JsonNode.Parse(result)!;
                }
                catch (Exception e)
                {
                    throw new Exception(
                        $"Unable to parse the JSON result from the API server: {e.Message}",
                        e
                    );
                }
            }
        }

        [Given("a DELETE request is made to {string}")]
        [When("a DELETE request is made to {string}")]
        public async Task WhenADELETERequestIsMadeTo(string url)
        {
            url = AddDataPrefixIfNecessary(url).Replace("{id}", _id);
            _apiResponse = await _playwrightContext.ApiRequestContext?.DeleteAsync(url)!;

            WaitForOpenSearch(_scenarioContext.ScenarioInfo.Tags);
        }

        [When("a DELETE request is made to referenced resource {string}")]
        public async Task WhenADELETERequestIsMadeToReferencedResource(string url)
        {
            url = AddDataPrefixIfNecessary(url).Replace("{id}", _referencedResourceId);

            _apiResponse = await _playwrightContext.ApiRequestContext?.DeleteAsync(url)!;
        }

        [When("a GET request is made to {string}")]
        public async Task WhenAGETRequestIsMadeTo(string url)
        {
            url = AddDataPrefixIfNecessary(url).Replace("{id}", _id);
            _logger.log.Information(url);
            _apiResponse = await _playwrightContext.ApiRequestContext?.GetAsync(url)!;
        }

        #endregion

        #region Then

        [Then("it should respond with {int}")]
        public void ThenItShouldRespondWith(int statusCode)
        {
            string body = _apiResponse.TextAsync().Result;
            _logger.log.Information(body);
            _apiResponse.Status.Should().Be(statusCode, body);
        }

        [Then("it should respond with {int} or {int}")]
        public void ThenItShouldRespondWithEither(int statusCode1, int statusCode2)
        {
            string body = _apiResponse.TextAsync().Result;
            _logger.log.Information(body);
            _apiResponse.Status.Should().BeOneOf([statusCode1, statusCode2], body);
        }

        [Then("there is a JSON file in the response body with a list of dependencies")]
        public void ThenThereIsADependencyResponse()
        {
            string responseBody = _apiResponse.TextAsync().Result;
            JsonNode responseJson = JsonNode.Parse(responseBody)!;

            var dependenciesSchema = """
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "type": "array",
  "items":
    {
      "type": "object",
      "properties": {
        "resource": {
          "type": "string"
        },
        "order": {
          "type": "integer"
        },
        "operations": {
          "type": "array",
          "items":
            {
              "type": "string"
            }
        }
      },
      "required": [
        "resource",
        "order",
        "operations"
      ]
    }
}
""";
            var schema = JsonSchema.FromText(dependenciesSchema);

            EvaluationOptions validatorEvaluationOptions = new()
            {
                OutputFormat = OutputFormat.List,
                RequireFormatValidation = true,
            };

            var evaluation = schema.Evaluate(responseJson, validatorEvaluationOptions);
            evaluation.HasErrors.Should().BeFalse("The response does not adhere to the expected schema.");
        }

        [Then("the response body is")]
        public async Task ThenTheResponseBodyIs(string expectedBody)
        {
            await ResponseBodyIs(expectedBody);
        }

        private async Task ResponseBodyIs(string expectedBody, bool IsDiscoveryEndpoint = false)
        {
            // Parse the API response to JsonNode
            string responseJsonString = await _apiResponse.TextAsync();
            JsonDocument responseJsonDoc = JsonDocument.Parse(responseJsonString);
            JsonNode responseJson = JsonNode.Parse(responseJsonDoc.RootElement.ToString())!;

            if (!IsDiscoveryEndpoint && (_apiResponse.Status == 200 || _apiResponse.Status == 201))
            {
                CheckAndRemoveMetadata(responseJson, true);
            }

            expectedBody = ReplacePlaceholders(expectedBody, responseJson);
            JsonNode expectedBodyJson = JsonNode.Parse(expectedBody)!;

            _logger.log.Information(responseJson.ToString());

            (responseJson as JsonObject)?.Remove("correlationId");
            (expectedBodyJson as JsonObject)?.Remove("correlationId");

            AreEqual(expectedBodyJson, responseJson)
                .Should()
                .BeTrue($"Expected:\n{expectedBodyJson}\n\nActual:\n{responseJson}");
        }

        [Then("the general response body is")]
        public async Task ThenTheGeneralResponseBodyIs(string expectedBody)
        {
            await ResponseBodyIs(expectedBody, true);
        }

        /// <summary>
        /// LastModifiedDate and ETag will be added to the EdFi document programmatically, so the retrieved value cannot be verified.
        /// This method ensures the property exists in the response and then removes it.
        /// </summary>
        private static void CheckAndRemoveMetadata(JsonNode responseJson, bool removeEtag)
        {
            if (responseJson is JsonArray jsonArray && jsonArray.Count > 0)
            {
                foreach (JsonObject? item in jsonArray.Cast<JsonObject?>())
                {
                    if (item != null)
                    {
                        var lastModifiedDate = LastModifiedDate(item);
                        lastModifiedDate.Should().NotBeNull();
                        item.Remove("_lastModifiedDate");

                        var eTag = Etag(item);
                        eTag.Should().NotBeNull();
                        if (removeEtag)
                        {
                            item.Remove("_etag");
                        }
                    }
                }
            }
            else if (responseJson is JsonObject jsonObject && jsonObject.Count > 0)
            {
                var lastModifiedDate = LastModifiedDate(responseJson);
                lastModifiedDate.Should().NotBeNull();
                (responseJson as JsonObject)?.Remove("_lastModifiedDate");

                var eTag = Etag(responseJson);
                eTag.Should().NotBeNull();
                if (removeEtag)
                {
                    (responseJson as JsonObject)?.Remove("_etag");
                }
            }
        }

        private static string? CorrelationIdValue(JsonNode response)
        {
            if (
                response is JsonObject jsonObject
                && jsonObject.TryGetPropertyValue("correlationId", out JsonNode? correlationId)
                && correlationId != null
            )
            {
                return correlationId.GetValue<string?>();
            }
            return null;
        }

        private static string? LastModifiedDate(JsonNode response)
        {
            if (
                response is JsonObject jsonObject
                && jsonObject.TryGetPropertyValue("_lastModifiedDate", out JsonNode? lastModifiedDate)
                && lastModifiedDate != null
            )
            {
                return lastModifiedDate.GetValue<string?>();
            }
            return null;
        }

        private static string? Etag(JsonNode response)
        {
            if (
                response is JsonObject jsonObject
                && jsonObject.TryGetPropertyValue("_etag", out JsonNode? etag)
                && etag != null
            )
            {
                return etag.GetValue<string?>();
            }
            return null;
        }

        private static bool AreEqual(JsonNode expectedBodyJson, JsonNode responseJson)
        {
            responseJson = OrderJsonProperties(responseJson);
            expectedBodyJson = OrderJsonProperties(expectedBodyJson);

            JsonElement expectedElement = JsonDocument.Parse(expectedBodyJson.ToJsonString()).RootElement;
            JsonElement responseElement = JsonDocument.Parse(responseJson.ToJsonString()).RootElement;

            return JsonElementEqualityComparer.Instance.Equals(expectedElement, responseElement);
        }

        [Then("the response body should contain header value {string}")]
        public void ThenTheResponseShouldContainHeaderValue(string value, string expectedBody)
        {
            // Parse the API response to JsonNode
            string responseBody = _apiResponse.TextAsync().Result;
            JsonNode responseJson = JsonNode.Parse(responseBody)!;

            expectedBody = ReplacePlaceholders(expectedBody, responseJson);
            JsonNode expectedBodyJson = JsonNode.Parse(expectedBody)!;

            _logger.log.Information(responseJson.ToString());

            // Check for CorrelationId
            var correlationId = CorrelationIdValue(responseJson);
            correlationId.Should().NotBeNull();
            correlationId.Should().BeEquivalentTo(value);

            AreEqual(expectedBodyJson, responseJson).Should().BeTrue();
        }

        // Use Regex to find all occurrences of {id} in the body
        private static readonly Regex _findIds = IdRegex();

        private string ReplacePlaceholders(string body, JsonNode responseJson)
        {
            string replacedBody = "";
            if (body.TrimStart().StartsWith('['))
            {
                var responseAsArray =
                    responseJson.AsArray()
                    ?? throw new AssertionException(
                        "Expected a JSON array response, but it was not an array."
                    );
                if (responseAsArray.Count == 0)
                {
                    return body;
                }

                int index = 0;

                replacedBody = _findIds.Replace(
                    body,
                    match =>
                    {
                        var idValue = responseJson[index]?["id"]?.ToString();
                        index++;
                        return idValue ?? match.ToString();
                    }
                );
            }
            else
            {
                replacedBody = _findIds.Replace(
                    body,
                    match =>
                    {
                        var idValue = responseJson["id"]?.ToString();

                        return idValue ?? match.ToString();
                    }
                );
            }
            replacedBody = replacedBody.Replace("{BASE_URL}/", _playwrightContext.ApiUrl);
            return replacedBody;
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
            expectedBody = expectedBody
                .Replace("{id}", _id)
                .Replace("{dependentId}", _dependentId)
                .Replace("{etag}", _etag);
            JsonNode expectedJson = JsonNode.Parse(expectedBody)!;
            _apiResponse = await _playwrightContext.ApiRequestContext?.GetAsync(_location)!;

            string responseJsonString = await _apiResponse.TextAsync();
            JsonDocument responseJsonDoc = JsonDocument.Parse(responseJsonString);
            JsonNode responseJson = JsonNode.Parse(responseJsonDoc.RootElement.ToString())!;

            // If we are explicitly looking for etag in our tests, do not remove it from the response
            CheckAndRemoveMetadata(responseJson, expectedJson["_etag"] == null);

            _logger.log.Information(responseJson.ToString());

            responseJson = OrderJsonProperties(responseJson);
            expectedJson = OrderJsonProperties(expectedJson);

            JsonElement expectedElement = JsonDocument.Parse(expectedJson.ToJsonString()).RootElement;
            JsonElement responseElement = JsonDocument.Parse(responseJson.ToJsonString()).RootElement;

            bool areEquals = JsonElementEqualityComparer.Instance.Equals(expectedElement, responseElement);

            areEquals.Should().BeTrue();
        }

        [Then("total of records should be {int}")]
        public void ThenTotalOfRecordsShouldBe(int totalRecords)
        {
            JsonNode responseJson = JsonNode.Parse(_apiResponse.TextAsync().Result)!;
            _logger.log.Information(responseJson.ToString());

            int count = responseJson.AsArray().Count;
            count.Should().Be(totalRecords);
        }

        [Then("the response headers does not include total-count")]
        public void ThenTheResponseHeadersDoesNotIncludeTotalCount()
        {
            var headers = _apiResponse.Headers;
            headers.ContainsKey("total-count").Should().BeFalse();
        }

        [Then("getting less schools than the total-count")]
        public async Task ThenGettingLessSchoolsThanTheTotalCount()
        {
            var headers = _apiResponse.Headers;

            string responseJsonString = await _apiResponse.TextAsync();
            JsonDocument responseJsonDoc = JsonDocument.Parse(responseJsonString);
            JsonNode responseJson = JsonNode.Parse(responseJsonDoc.RootElement.ToString())!;

            _logger.log.Information(responseJson.ToString());

            int count = responseJson.AsArray().Count;

            headers.GetValueOrDefault("total-count").Should().NotBe(count.ToString());
        }

        [Then("the ETag is in the response header")]
        public void ThenTheEtagIsInTheResponseHeader()
        {
            _etag = _apiResponse.Headers["etag"];
            _etag.Should().NotBeNullOrEmpty();
        }
        #endregion

        private static string AddDataPrefixIfNecessary(string input)
        {
            // Discovery endpoint
            if (input == "/")
            {
                return input;
            }

            // Prefer that the "url" fragment have a starting slash, but write
            // the code so it will work either way.
            input = input.StartsWith('/') ? input[1..] : input;

            // metadata should not have "data" added to the URL.
            input = input.StartsWith("metadata") ? input : $"data/{input}";

            return input;
        }

        private void WaitForOpenSearch(string[]? waitTags)
        {
            if (waitTags != null && waitTags.Contains("addwait") && _openSearchEnabled)
            {
                Thread.Sleep(5000);
            }
        }

        [GeneratedRegex(@"\{id\}", RegexOptions.Compiled)]
        private static partial Regex IdRegex();
    }
}
