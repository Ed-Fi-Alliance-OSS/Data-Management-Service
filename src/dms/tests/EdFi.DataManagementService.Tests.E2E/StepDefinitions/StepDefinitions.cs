// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using EdFi.DataManagementService.Tests.E2E.Authorization;
using EdFi.DataManagementService.Tests.E2E.Extensions;
using EdFi.DataManagementService.Tests.E2E.Management;
using FluentAssertions;
using Json.Schema;
using Microsoft.Playwright;
using Reqnroll;
using static EdFi.DataManagementService.Tests.E2E.Management.JsonComparer;
using static EdFi.DataManagementService.Tests.E2E.Management.JsonTestUtilities;

namespace EdFi.DataManagementService.Tests.E2E.StepDefinitions
{
    [Binding]
    public partial class StepDefinitions
    {
        private readonly PlaywrightContext _playwrightContext;
        private readonly TestLogger _logger;
        private readonly ScenarioContext _scenarioContext;
        private readonly FeatureContext _featureContext;

        public StepDefinitions(
            PlaywrightContext playwrightContext,
            TestLogger logger,
            ScenarioContext scenarioContext,
            FeatureContext featureContext
        )
        {
            _playwrightContext = playwrightContext;
            _logger = logger;
            _scenarioContext = scenarioContext;
            _featureContext = featureContext;

            _featureContext.TryAdd("_waitOnNextQuery", false);
        }

        private IAPIResponse _apiResponse = null!;
        private string _id = string.Empty;
        private string _location = string.Empty;
        private string _etag = string.Empty;
        private string _lastModifiedDate = string.Empty;
        private string _dependentId = string.Empty;
        private string _referencedResourceId = string.Empty;
        private ScenarioVariables _scenarioVariables = new();
        private string _dmsToken = string.Empty;
        private readonly bool _openSearchEnabled = AppSettings.OpenSearchEnabled;
        private Dictionary<string, string> _relationships = [];

        #region Given

        [Given("the SIS Vendor is authorized with namespacePrefixes {string}")]
        public async Task GivenTheSisVendorIsAuthorized(string namespacePrefixes)
        {
            await SetAuthorizationToken(namespacePrefixes, string.Empty);
        }

        [Given("the claimSet {string} is authorized with namespacePrefixes {string}")]
        public async Task GivenTheClaimSetIsAuthorized(string claimSetName, string namespacePrefixes)
        {
            await SetAuthorizationToken(namespacePrefixes, string.Empty, claimSetName);
        }

        [Given("the claimSet {string} is authorized with educationOrganizationIds {string}")]
        public async Task GivenTheClaimSetIsAuthorizedWithEdOrgIds(
            string claimSetName,
            string educationOrganizationIds
        )
        {
            await SetAuthorizationToken("uri://ed-fi.org", educationOrganizationIds, claimSetName);
        }

        [Given("the resulting token is stored in the {string} variable")]
        public void GivenTheResultingTokenIsStoredInTheVariable(string variableName)
        {
            _scenarioVariables.Add(variableName, _dmsToken);
        }

        [Given("the token gets switched to the one in the {string} variable")]
        public void GivenTheTokenGetsSwitchedToTheOneInTheVariable(string variableName)
        {
            _dmsToken = _scenarioVariables.GetValueByName(variableName);
        }

        private async Task SetAuthorizationToken(
            string namespacePrefixes,
            string educationOrganizationIds,
            string claimSetName = "E2E-NoFurtherAuthRequiredClaimSet"
        )
        {
            await AuthorizationDataProvider.Create(
                Guid.NewGuid().ToString(),
                "C. M. Burns",
                "cmb@example.com",
                namespacePrefixes,
                educationOrganizationIds,
                SystemAdministrator.Token,
                claimSetName
            );

            var bearerToken = await AuthorizationDataProvider.GetToken();
            _dmsToken = $"Bearer {bearerToken}";
        }

        [Given("there is no Authorization header")]
        public void GivenThereIsNoAuthorizationHeader()
        {
            _dmsToken = string.Empty;
        }

        [Given("a POST request is made to {string} with")]
        public async Task GivenAPOSTRequestIsMadeToWith(string url, string body)
        {
            url = AddDataPrefixIfNecessary(url);
            await ExecutePostRequest(url, body);

            _apiResponse
                .Status.Should()
                .BeOneOf(OkCreated, $"Given post to {url} failed:\n{_apiResponse.TextAsync().Result}");
        }

        [Given("the token signature is manipulated")]
        public void TokenSignatureManipulated()
        {
            var token = _dmsToken;
            var segments = token.Split('.');
            var signature = segments[2].ToCharArray();
            new Random().Shuffle(signature);
            _dmsToken = $"{segments[0]}.{segments[1]}.{signature}";
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
                    new() { DataObject = descriptor, Headers = GetHeaders() }
                )!;
                _featureContext["_waitOnNextQuery"] = true;
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
                    new() { Data = body, Headers = GetHeaders() }
                )!;
                _featureContext["_waitOnNextQuery"] = true;
                _apiResponses.Add(response);

                response
                    .Status.Should()
                    .BeOneOf(
                        OkCreated,
                        $"POST request for {entityType} failed:\n{response.TextAsync().Result}"
                    );

                // Add to relationship list
                AddRelationships(_scenarioContext.ScenarioInfo.Tags, response, entityType);

                if (
                    row.TryGetValue("_storeResultingIdInVariable", out string variableName)
                    && !string.IsNullOrWhiteSpace(variableName)
                )
                {
                    _scenarioVariables.Add(
                        variableName,
                        extractDataFromResponseAndReturnIdIfAvailable(response)
                    );
                }
            }

            return _apiResponses;
        }

        [Given("the system has these {string}")]
        public async Task GivenTheSystemHasThese(string entityType, DataTable dataTable)
        {
            _ = await ProcessDataTable(entityType, dataTable);

            _logger.log.Information($"Responses for Given(the system has these {entityType})");
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
                    new() { DataObject = descriptorBody, Headers = GetHeaders() }
                )!;
                _featureContext["_waitOnNextQuery"] = true;

                string body = apiResponse.TextAsync().Result;
                _logger.log.Information(body);

                apiResponse.Status.Should().BeOneOf(OkCreated, $"Request failed:\n{body}");
            }
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

        [Given("the resulting id is stored in the {string} variable")]
        [When("the resulting id is stored in the {string} variable")]
        public void WhenResultingIdStoredInTheVariable(string variableName)
        {
            _scenarioVariables.Add(variableName, _id);
        }

        private async Task ExecutePostRequest(string url, string body)
        {
            _logger.log.Information($"POST url: {url}");
            _logger.log.Information($"POST body: {body}");
            _apiResponse = await _playwrightContext.ApiRequestContext?.PostAsync(
                url,
                new() { Data = body, Headers = GetHeaders() }
            )!;
            _featureContext["_waitOnNextQuery"] = true;
            _featureContext["_apiResponse"] = _apiResponse;
            _logger.log.Information(_apiResponse.TextAsync().Result);

            _id = extractDataFromResponseAndReturnIdIfAvailable(_apiResponse);
        }

        private string extractDataFromResponseAndReturnIdIfAvailable(IAPIResponse apiResponse)
        {
            if (apiResponse.Headers.TryGetValue("etag", out string? etagValue))
            {
                _etag = etagValue;
            }
            if (apiResponse.Headers.TryGetValue("location", out string? value))
            {
                _location = value;
                var segments = _location.Split('/');
                return segments[^1];
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
            httpHeaders.AddRange(GetHeaders());

            _apiResponse = await _playwrightContext.ApiRequestContext?.PostAsync(
                url,
                new() { Data = body, Headers = httpHeaders }
            )!;
            _featureContext["_waitOnNextQuery"] = true;
            _logger.log.Information(_apiResponse.TextAsync().Result);

            _id = extractDataFromResponseAndReturnIdIfAvailable(_apiResponse);
        }

        [When("a POST request is made for dependent resource {string} with")]
        public async Task WhenSendingAPOSTRequestForDependentResourceWithBody(string url, string body)
        {
            url = AddDataPrefixIfNecessary(url);
            _apiResponse = await _playwrightContext.ApiRequestContext?.PostAsync(
                url,
                new() { Data = body, Headers = GetHeaders() }
            )!;
            _featureContext["_waitOnNextQuery"] = true;

            _dependentId = extractDataFromResponseAndReturnIdIfAvailable(_apiResponse);
        }

        [When("a PUT if-match {string} request is made to {string} with")]
        public async Task WhenAPUTIf_MatchRequestIsMadeToWith(string ifMatch, string url, string body)
        {
            url = AddDataPrefixIfNecessary(url)
                .Replace("{id}", _id)
                .Replace("{dependentId}", _dependentId)
                .ReplacePlaceholdersWithDictionaryValues(_scenarioVariables.VariableByName);

            body = body.Replace("{id}", _id)
                .Replace("{dependentId}", _dependentId)
                .ReplacePlaceholdersWithDictionaryValues(_scenarioVariables.VariableByName);

            _logger.log.Information($"PUT url: {url}");
            _logger.log.Information($"PUT body: {body}");

            ifMatch = ifMatch.Replace("{IfMatch}", _etag);
            _apiResponse = await _playwrightContext.ApiRequestContext?.PutAsync(
                url,
                new() { Data = body, Headers = GetHeadersWithIfMatch(ifMatch) }
            )!;
            _featureContext["_waitOnNextQuery"] = true;

            extractDataFromResponseAndReturnIdIfAvailable(_apiResponse);
        }

        [When("a PUT request is made to {string} with")]
        public async Task WhenAPUTRequestIsMadeToWith(string url, string body)
        {
            url = AddDataPrefixIfNecessary(url)
                .Replace("{id}", _id)
                .Replace("{dependentId}", _dependentId)
                .ReplacePlaceholdersWithDictionaryValues(_scenarioVariables.VariableByName);

            body = body.Replace("{id}", _id)
                .Replace("{dependentId}", _dependentId)
                .ReplacePlaceholdersWithDictionaryValues(_scenarioVariables.VariableByName);

            _logger.log.Information($"PUT url: {url}");
            _logger.log.Information($"PUT body: {body}");
            _apiResponse = await _playwrightContext.ApiRequestContext?.PutAsync(
                url,
                new() { Data = body, Headers = GetHeaders() }
            )!;
            _featureContext["_waitOnNextQuery"] = true;

            extractDataFromResponseAndReturnIdIfAvailable(_apiResponse);
        }

        [When("a PUT request is made to referenced resource {string} with")]
        public async Task WhenAPUTRequestIsMadeToReferencedResourceWith(string url, string body)
        {
            url = AddDataPrefixIfNecessary(url).Replace("{id}", _referencedResourceId);

            _logger.log.Information(url);
            body = body.Replace("{id}", _referencedResourceId);
            _logger.log.Information(body);
            _apiResponse = await _playwrightContext.ApiRequestContext?.PutAsync(
                url,
                new() { Data = body, Headers = GetHeaders() }
            )!;
            _featureContext["_waitOnNextQuery"] = true;

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
            url = AddDataPrefixIfNecessary(url)
                .Replace("{id}", _id)
                .ReplacePlaceholdersWithDictionaryValues(_scenarioVariables.VariableByName);

            _apiResponse = await _playwrightContext.ApiRequestContext?.DeleteAsync(
                url,
                new() { Headers = GetHeaders() }
            )!;
            _featureContext["_waitOnNextQuery"] = true;
        }

        [When("a relationship with {string} is deleted")]
        public async Task WhenARelationShipDeleted(string relationshipKey)
        {
            var baseUrl = $"data/ed-fi/";
            var id = _relationships[relationshipKey];
            var url = $"{baseUrl}{relationshipKey}/{id}";
            _apiResponse = await _playwrightContext.ApiRequestContext?.DeleteAsync(
                url,
                new() { Headers = GetHeaders() }
            )!;
            _featureContext["_waitOnNextQuery"] = true;
        }

        [When("a DELETE request is made to referenced resource {string}")]
        public async Task WhenADELETERequestIsMadeToReferencedResource(string url)
        {
            url = AddDataPrefixIfNecessary(url).Replace("{id}", _referencedResourceId);

            _apiResponse = await _playwrightContext.ApiRequestContext?.DeleteAsync(
                url,
                new() { Headers = GetHeaders() }
            )!;
            _featureContext["_waitOnNextQuery"] = true;
        }

        [When("a GET request is made to {string}")]
        public async Task WhenAGETRequestIsMadeTo(string url)
        {
            url = AddDataPrefixIfNecessary(url)
                .Replace("{id}", _id)
                .ReplacePlaceholdersWithDictionaryValues(_scenarioVariables.VariableByName);

            _logger.log.Information(url);
            await WaitForOpenSearch(url);

            // Discovery API endpoints should not require authentication
            var headers = IsDiscoveryEndpoint(url) ? new List<KeyValuePair<string, string>>() : GetHeaders();

            _apiResponse = await _playwrightContext.ApiRequestContext?.GetAsync(
                url,
                new() { Headers = headers }
            )!;
        }

        [When("a DELETE if-match {string} request is made to {string}")]
        public async Task WhenADeleteIf_MatchRequestIsMadeToWith(string ifMatch, string url)
        {
            url = AddDataPrefixIfNecessary(url)
                .Replace("{id}", _id)
                .Replace("{dependentId}", _dependentId)
                .ReplacePlaceholdersWithDictionaryValues(_scenarioVariables.VariableByName);

            _logger.log.Information($"DELETE url: {url}");

            ifMatch = ifMatch.Replace("{IfMatch}", _etag);
            _apiResponse = await _playwrightContext.ApiRequestContext?.DeleteAsync(
                url,
                new() { Headers = GetHeadersWithIfMatch(ifMatch) }
            )!;

            extractDataFromResponseAndReturnIdIfAvailable(_apiResponse);
        }

        [When("the lastModifiedDate is stored")]
        public async Task GivenTheLastModifiedDateIsStored()
        {
            var responseJson = JsonNode.Parse(await _apiResponse.TextAsync());

            if (responseJson is not null)
            {
                _lastModifiedDate = LastModifiedDate(responseJson) ?? string.Empty;
            }
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

        private async Task ResponseBodyIs(
            string expectedBody,
            bool IsDiscoveryEndpoint = false,
            bool isXml = false
        )
        {
            string responseContent = await _apiResponse.TextAsync();

            if (isXml)
            {
                var actualXml = XDocument.Parse(responseContent.Trim());
                var expectedXml = XDocument.Parse(expectedBody.Trim());

                actualXml
                    .ToString()
                    .Should()
                    .Be(expectedXml.ToString(), $"Expected:\n{expectedXml}\n\nActual:\n{actualXml}");
                return;
            }

            JsonDocument responseJsonDoc = JsonDocument.Parse(responseContent);
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

            // The version value is retrieved from the DMS assembly,
            // so it will not not match value when testing against a published DMS.
            if (IsDiscoveryEndpoint)
            {
                (responseJson as JsonObject)?.Remove("version");
                (expectedBodyJson as JsonObject)?.Remove("version");
            }

            AreEqual(expectedBodyJson, responseJson)
                .Should()
                .BeTrue($"Expected:\n{expectedBodyJson}\n\nActual:\n{responseJson}");
        }

        [Then("the general response body is")]
        public async Task ThenTheGeneralResponseBodyIs(string expectedBody)
        {
            await ResponseBodyIs(expectedBody, true);
        }

        [Then("the discovery API root response body is")]
        public async Task ThenTheDiscoveryApiRootResponseBodyIs(string expectedBody)
        {
            string responseContent = await _apiResponse.TextAsync();
            _logger.log.Information(responseContent);

            // Parse both expected and actual responses
            JsonNode responseJson = JsonNode.Parse(responseContent)!;
            expectedBody = ReplacePlaceholders(expectedBody, responseJson);
            JsonNode expectedBodyJson = JsonNode.Parse(expectedBody)!;

            // Remove version as it varies between environments
            (responseJson as JsonObject)?.Remove("version");
            (expectedBodyJson as JsonObject)?.Remove("version");

            // Normalize OAuth URLs - accept both internal Docker and external localhost URLs
            NormalizeOAuthUrl(responseJson);
            NormalizeOAuthUrl(expectedBodyJson);

            AreEqual(expectedBodyJson, responseJson)
                .Should()
                .BeTrue($"Expected:\n{expectedBodyJson}\n\nActual:\n{responseJson}");
        }

        private static void NormalizeOAuthUrl(JsonNode? json)
        {
            if (
                json is JsonObject obj
                && obj.TryGetPropertyValue("urls", out var urls)
                && urls is JsonObject urlsObj
                && urlsObj.TryGetPropertyValue("oauth", out var oauth)
            )
            {
                var oauthStr = oauth?.ToString();
                if (
                    !string.IsNullOrEmpty(oauthStr)
                    && (oauthStr.Contains("dms-keycloak:8080") || oauthStr.Contains("localhost:8045"))
                )
                {
                    // Normalize both OAuth URL patterns to a common format
                    urlsObj["oauth"] = "OAUTH_URL_NORMALIZED";
                }
            }
        }

        [Then("the xsd response body is")]
        public async Task ThenTheXsdResponseBodyIs(string expectedBody)
        {
            await ResponseBodyIs(expectedBody, true, true);
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
            replacedBody = replacedBody
                .Replace("{BASE_URL}/", _playwrightContext.ApiUrl)
                .ReplacePlaceholdersWithDictionaryValues(_scenarioVariables.VariableByName);
            replacedBody = replacedBody.Replace("{OAUTH_URL}", AppSettings.AuthenticationService);
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
            _apiResponse = await _playwrightContext.ApiRequestContext?.GetAsync(
                _location,
                new() { Headers = GetHeaders() }
            )!;

            string responseJsonString = await _apiResponse.TextAsync();
            JsonDocument responseJsonDoc = JsonDocument.Parse(responseJsonString);
            JsonNode responseJson = JsonNode.Parse(responseJsonDoc.RootElement.ToString())!;

            _logger.log.Information(responseJson.ToString());

            JsonNode expectedJson = JsonNode.Parse(expectedBody)!;
            bool removeEtagFromActual = expectedJson["_etag"] == null;

            bool areEqual = CompareJsonWithPlaceholderReplacement(
                expectedBody,
                responseJson,
                _id,
                _dependentId,
                _etag,
                _scenarioVariables.VariableByName,
                _playwrightContext.ApiUrl,
                AppSettings.AuthenticationService,
                removeMetadataFromActual: true,
                removeEtagFromActual: removeEtagFromActual
            );

            areEqual.Should().BeTrue($"Expected:\n{expectedBody}\n\nActual:\n{responseJson}");
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

        [Then("the lastModifiedDate has not changed")]
        public async Task ThenTheLastModifiedDateHasNotChanged()
        {
            var responseJson = JsonNode.Parse(await _apiResponse.TextAsync());

            if (responseJson is not null)
            {
                _lastModifiedDate.Should().Be(LastModifiedDate(responseJson));
            }
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

        private static bool IsDiscoveryEndpoint(string url)
        {
            // Discovery API endpoints that don't require authentication
            return url == "/" || url.StartsWith("metadata");
        }

        private async Task WaitForOpenSearch(string requestUrl)
        {
            if (_openSearchEnabled && _featureContext.Get<bool>("_waitOnNextQuery"))
            {
                var isGetById = Guid.TryParse(requestUrl.Split('/')[^1], out Guid _);
                if (!isGetById)
                {
                    // Sleep before executing GetAll requests so that OpenSearch gets up to date
                    await Task.Delay(5000);
                    _featureContext["_waitOnNextQuery"] = false;
                }
            }
        }

        private void AddRelationships(string[]? relationTags, IAPIResponse apiResponse, string entityType)
        {
            if (relationTags != null && relationTags.Contains("addrelationships"))
            {
                var id = extractDataFromResponseAndReturnIdIfAvailable(apiResponse);
                _relationships.Add(entityType, id);
            }
        }

        [GeneratedRegex(@"\{id\}", RegexOptions.Compiled)]
        private static partial Regex IdRegex();

        private IEnumerable<KeyValuePair<string, string>> GetHeaders()
        {
            var list = new List<KeyValuePair<string, string>> { new("Authorization", _dmsToken) };
            return list;
        }

        private IEnumerable<KeyValuePair<string, string>> GetHeadersWithIfMatch(string ifMatch)
        {
            var list = new List<KeyValuePair<string, string>>
            {
                new("Authorization", _dmsToken),
                new("If-Match", ifMatch),
            };
            return list;
        }
    }
}
