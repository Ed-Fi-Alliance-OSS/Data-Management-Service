// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using EdFi.DataManagementService.Core.External.Security;
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
        private string _rawEtag = string.Empty;
        private string _lastModifiedDate = string.Empty;
        private string _dependentId = string.Empty;
        private string _referencedResourceId = string.Empty;
        private ScenarioVariables _scenarioVariables = new();
        private Dictionary<string, string> _relationships = [];
        private int _lastUploadStatusCode = 0;
        private int? _directResponseStatusCode;
        private const string ApiResponseContextKey = "apiResponse";
        private const string IdContextKey = "id";

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

        [Given(
            "the claimSet {string} is authorized with namespace {string} and educationOrganizationIds {string}"
        )]
        public async Task GivenTheClaimSetIsAuthorizedWithNamespaceAndEdOrgIds(
            string claimSetName,
            string namespacePrefixes,
            string educationOrganizationIds
        )
        {
            await SetAuthorizationToken(namespacePrefixes, educationOrganizationIds, claimSetName);
        }

        [Given("a claim set is uploaded to CMS that grants {string} access to {string}")]
        public async Task GivenAClaimSetIsUploadedToCMSThatGrantsEndpointAccess(
            string endpointName,
            string claimSetName
        )
        {
            await WhenAClaimSetIsUploadedToCMSThatGrantsEndpointAccess(endpointName, claimSetName);
        }

        [Given("the claim set upload to CMS should be successful")]
        public void GivenTheClaimSetUploadToCMSShouldBeSuccessful()
        {
            ThenTheClaimSetUploadToCMSShouldBeSuccessful();
        }

        [Given("the resulting token is stored in the {string} variable")]
        public void GivenTheResultingTokenIsStoredInTheVariable(string variableName)
        {
            _scenarioVariables.Add(variableName, GetDmsTokenFromContext());
        }

        [Given("the token gets switched to the one in the {string} variable")]
        public void GivenTheTokenGetsSwitchedToTheOneInTheVariable(string variableName)
        {
            _scenarioContext["dmsToken"] = _scenarioVariables.GetValueByName(variableName);
        }

        private async Task SetAuthorizationToken(
            string namespacePrefixes,
            string educationOrganizationIds,
            string claimSetName = "E2E-NoFurtherAuthRequiredClaimSet"
        )
        {
            string systemAdministratorToken = await SystemAdministrator.GetToken();

            // Clear the DMS claimset cache before setting a new authorization context
            // This ensures test isolation when scenarios have different auth contexts
            await ClearDmsClaimsetCache(systemAdministratorToken);

            await AuthorizationDataProvider.CreateClientCredentials(
                Guid.NewGuid().ToString(),
                "C. M. Burns",
                "cmb@example.com",
                namespacePrefixes,
                educationOrganizationIds,
                systemAdministratorToken,
                claimSetName
            );

            var bearerToken = await AuthorizationDataProvider.GetToken();
            _scenarioContext["dmsToken"] = $"Bearer {bearerToken}";
        }

        // Helper method to clear the DMS claimset cache
        private async Task ClearDmsClaimsetCache(string systemAdministratorToken)
        {
            try
            {
                // Create authorization header - using Bearer token for DMS
                var headers = new List<KeyValuePair<string, string>>
                {
                    new("Authorization", $"Bearer {systemAdministratorToken}"),
                };

                var dmsResponse = await _playwrightContext.ApiRequestContext?.PostAsync(
                    "management/reload-claimsets",
                    new() { Data = "{}", Headers = headers }
                )!;

                if (dmsResponse.Status != 200)
                {
                    _logger.log.Warning($"DMS cache clear returned status {dmsResponse.Status}");
                }
                else
                {
                    // Add a small delay to ensure cache is fully cleared and reloaded
                    await Task.Delay(500);
                }
            }
            catch (Exception ex)
            {
                _logger.log.Warning($"Failed to clear DMS claimset cache: {ex.Message}");
                // Don't fail the test if cache clear fails - continue
            }
        }

        [Given("there is no Authorization header")]
        public void GivenThereIsNoAuthorizationHeader()
        {
            _scenarioContext["dmsToken"] = string.Empty;
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
            var token = GetDmsTokenFromContext();
            var segments = token.Split('.');
            var signature = segments[2].ToCharArray();
            new Random().Shuffle(signature);
            _scenarioContext["dmsToken"] = $"{segments[0]}.{segments[1]}.{new string(signature)}";
        }

        [Given("the token is expired")]
        public void GivenTheTokenIsExpired()
        {
            var token = GetDmsTokenFromContext().Replace("Bearer ", string.Empty);
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException("No JWT token available to expire.");
            }

            var segments = token.Split('.');
            if (segments.Length < 2)
            {
                throw new InvalidOperationException("Token is not a valid JWT.");
            }

            var payloadJson = Encoding.UTF8.GetString(Base64UrlDecode(segments[1]));
            var payload =
                JsonNode.Parse(payloadJson)?.AsObject()
                ?? throw new InvalidOperationException("Token payload is not valid JSON.");

            payload["exp"] = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds();

            var updatedPayload = Base64UrlEncode(Encoding.UTF8.GetBytes(payload.ToJsonString()));
            var updatedToken = segments.Length switch
            {
                2 => $"{segments[0]}.{updatedPayload}",
                _ => $"{segments[0]}.{updatedPayload}.{segments[2]}",
            };

            _scenarioContext["dmsToken"] = $"Bearer {updatedToken}";
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
                    new() { DataByte = System.Text.Encoding.UTF8.GetBytes(body), Headers = GetWriteHeaders() }
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

            if (body.Contains("{token}"))
            {
                body = body.Replace("{token}", GetDmsTokenFromContext().Replace("Bearer ", string.Empty));
            }

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
            SetCurrentApiResponse(
                await _playwrightContext.ApiRequestContext?.PostAsync(
                    url,
                    new() { DataByte = System.Text.Encoding.UTF8.GetBytes(body), Headers = GetWriteHeaders() }
                )!
            );
            _featureContext["_waitOnNextQuery"] = true;
            _logger.log.Information(await _apiResponse.TextAsync());

            _id = extractDataFromResponseAndReturnIdIfAvailable(_apiResponse);
        }

        private string extractDataFromResponseAndReturnIdIfAvailable(IAPIResponse apiResponse)
        {
            if (apiResponse.Headers.TryGetValue("etag", out string? etagValue))
            {
                _etag = StripEtagQuotes(etagValue);
            }
            if (apiResponse.Headers.TryGetValue("location", out string? value))
            {
                _location = value;
                var segments = _location.Split('/');
                string id = segments[^1];
                _scenarioContext[IdContextKey] = id;

                return id;
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

            var headers = GetWriteHeaders();
            headers[header] = value;

            _apiResponse = await _playwrightContext.ApiRequestContext?.PostAsync(
                url,
                new() { DataByte = System.Text.Encoding.UTF8.GetBytes(body), Headers = headers }
            )!;
            _featureContext["_waitOnNextQuery"] = true;
            _logger.log.Information(_apiResponse.TextAsync().Result);

            _id = extractDataFromResponseAndReturnIdIfAvailable(_apiResponse);
        }

        [When("an unauthenticated POST request is made to {string} with header {string} value {string} and")]
        public async Task WhenAnUnauthenticatedPostRequestIsMadeToWithHeaderAnd(
            string url,
            string header,
            string value,
            string body
        )
        {
            url = AddDataPrefixIfNecessary(url)
                .Replace("{id}", _id)
                .ReplacePlaceholdersWithDictionaryValues(_scenarioVariables.VariableByName);
            body = body.Replace("{id}", _id)
                .ReplacePlaceholdersWithDictionaryValues(_scenarioVariables.VariableByName);

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Content-Type"] = "application/json",
                [header] = value,
            };

            _apiResponse = await _playwrightContext.ApiRequestContext?.PostAsync(
                url,
                new() { DataByte = System.Text.Encoding.UTF8.GetBytes(body), Headers = headers }
            )!;
            _featureContext["_waitOnNextQuery"] = true;
            _logger.log.Information(await _apiResponse.TextAsync());
        }

        [When("an unauthenticated Form URL Encoded POST request is made to {string} with")]
        public async Task WhenAnUnauthenticatedFormUrlEncodedPostRequestIsMadeToWith(
            string url,
            DataTable formData
        )
        {
            url = AddDataPrefixIfNecessary(url)
                .Replace("{id}", _id)
                .ReplacePlaceholdersWithDictionaryValues(_scenarioVariables.VariableByName);

            Dictionary<string, string> formDataDictionary = formData.Rows.ToDictionary(
                x => x["Key"],
                y => y["Value"].ReplacePlaceholdersWithDictionaryValues(_scenarioVariables.VariableByName)
            );
            var content = new FormUrlEncodedContent(formDataDictionary);

            _apiResponse = await _playwrightContext.ApiRequestContext?.PostAsync(
                url,
                new()
                {
                    Headers = new Dictionary<string, string>
                    {
                        ["Content-Type"] = "application/x-www-form-urlencoded",
                    },
                    Data = await content.ReadAsStringAsync(),
                }
            )!;
            _featureContext["_waitOnNextQuery"] = true;
        }

        [When("a POST request is made for dependent resource {string} with")]
        public async Task WhenSendingAPOSTRequestForDependentResourceWithBody(string url, string body)
        {
            url = AddDataPrefixIfNecessary(url);
            _apiResponse = await _playwrightContext.ApiRequestContext?.PostAsync(
                url,
                new() { DataByte = System.Text.Encoding.UTF8.GetBytes(body), Headers = GetWriteHeaders() }
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

            ifMatch = ResolveEtagHeaderValue(ifMatch);
            _apiResponse = await _playwrightContext.ApiRequestContext?.PutAsync(
                url,
                new()
                {
                    DataByte = System.Text.Encoding.UTF8.GetBytes(body),
                    Headers = GetWriteHeadersWithIfMatch(ifMatch),
                }
            )!;
            _featureContext["_waitOnNextQuery"] = true;

            extractDataFromResponseAndReturnIdIfAvailable(_apiResponse);
        }

        [When("a PUT if-none-match {string} request is made to {string} with")]
        public async Task WhenAPUTIf_NoneMatchRequestIsMadeToWith(string ifNoneMatch, string url, string body)
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

            ifNoneMatch = ResolveEtagHeaderValue(ifNoneMatch);
            _apiResponse = await _playwrightContext.ApiRequestContext?.PutAsync(
                url,
                new()
                {
                    DataByte = System.Text.Encoding.UTF8.GetBytes(body),
                    Headers = GetWriteHeadersWithIfNoneMatch(ifNoneMatch),
                }
            )!;
            _featureContext["_waitOnNextQuery"] = true;

            extractDataFromResponseAndReturnIdIfAvailable(_apiResponse);
        }

        [When("a PUT request is made to {string} with")]
        public async Task WhenAPUTRequestIsMadeToWith(string url, string body)
        {
            string id = GetCurrentId();
            url = AddDataPrefixIfNecessary(url)
                .Replace("{id}", id)
                .Replace("{dependentId}", _dependentId)
                .ReplacePlaceholdersWithDictionaryValues(_scenarioVariables.VariableByName);

            body = body.Replace("{id}", id)
                .Replace("{dependentId}", _dependentId)
                .ReplacePlaceholdersWithDictionaryValues(_scenarioVariables.VariableByName);

            _logger.log.Information($"PUT url: {url}");
            _logger.log.Information($"PUT body: {body}");
            SetCurrentApiResponse(
                await _playwrightContext.ApiRequestContext?.PutAsync(
                    url,
                    new() { DataByte = System.Text.Encoding.UTF8.GetBytes(body), Headers = GetWriteHeaders() }
                )!
            );
            _featureContext["_waitOnNextQuery"] = true;

            extractDataFromResponseAndReturnIdIfAvailable(_apiResponse);
        }

        private string GetCurrentId()
        {
            if (
                string.IsNullOrWhiteSpace(_id)
                && _scenarioContext.TryGetValue(IdContextKey, out object? idObject)
                && idObject is string currentId
                && !string.IsNullOrWhiteSpace(currentId)
            )
            {
                _id = currentId;
            }

            return _id;
        }

        private void SetCurrentApiResponse(IAPIResponse apiResponse)
        {
            _apiResponse = apiResponse;
            _scenarioContext[ApiResponseContextKey] = apiResponse;
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
                new() { DataByte = System.Text.Encoding.UTF8.GetBytes(body), Headers = GetWriteHeaders() }
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
            string id = GetCurrentId();

            url = AddDataPrefixIfNecessary(url)
                .Replace("{id}", id)
                .ReplacePlaceholdersWithDictionaryValues(_scenarioVariables.VariableByName);

            _logger.log.Information(url);

            SetCurrentApiResponse(
                await _playwrightContext.ApiRequestContext?.GetAsync(url, new() { Headers = GetHeaders() })!
            );
        }

        [Then("the response body path {string} is stored in request variable {string}")]
        public async Task ThenTheResponseBodyPathIsStoredInRequestVariable(
            string jsonPath,
            string variableName
        )
        {
            JsonNode current = await ResolveResponseBodyPath(jsonPath);

            _scenarioVariables.Add(variableName, current.ToString());
        }

        [Then("the response body path {string} should equal request variable {string}")]
        public async Task ThenTheResponseBodyPathShouldEqualRequestVariable(
            string jsonPath,
            string variableName
        )
        {
            JsonNode current = await ResolveResponseBodyPath(jsonPath);

            current.ToString().Should().Be(_scenarioVariables.GetValueByName(variableName));
        }

        private async Task<JsonNode> ResolveResponseBodyPath(string jsonPath)
        {
            string responseBody = await _apiResponse.TextAsync();
            JsonNode responseJson = JsonNode.Parse(responseBody)!;

            JsonNode? current = responseJson;
            foreach (string pathPart in jsonPath.Split('.'))
            {
                if (current is JsonObject jsonObject)
                {
                    jsonObject.TryGetPropertyValue(pathPart, out current).Should().BeTrue();
                    continue;
                }

                if (current is JsonArray jsonArray && int.TryParse(pathPart, out int index))
                {
                    index.Should().BeGreaterThanOrEqualTo(0);
                    index.Should().BeLessThan(jsonArray.Count);
                    current = jsonArray[index];
                    continue;
                }

                current = null;
                break;
            }

            current
                .Should()
                .NotBeNull($"Path '{jsonPath}' should resolve to a non-null value. Response: {responseBody}");

            return current!;
        }

        [When("a GET request is made to {string} with header {string} value {string}")]
        public async Task WhenAGETRequestIsMadeToWithHeader(string url, string header, string value)
        {
            string id = GetCurrentId();

            url = AddDataPrefixIfNecessary(url)
                .Replace("{id}", id)
                .ReplacePlaceholdersWithDictionaryValues(_scenarioVariables.VariableByName);

            _logger.log.Information($"GET url: {url}");

            var headers = GetHeaders(KeyValuePair.Create(header, ResolveEtagHeaderValue(value)));

            SetCurrentApiResponse(
                await _playwrightContext.ApiRequestContext?.GetAsync(url, new() { Headers = headers })!
            );
        }

        // Alias used in OWASP security scenarios to make their intent explicit in feature files.
        // Behaviour is identical to WhenAGETRequestIsMadeToWithHeader.
        [When("a security GET request is made to {string} with header {string} value {string}")]
        public async Task WhenASecurityGetRequestIsMadeToWithHeader(
            string url,
            string header,
            string value
        ) => await WhenAGETRequestIsMadeToWithHeader(url, header, value);

        [When("an {string} request is made to {string} with headers")]
        public async Task WhenAnRequestIsMadeToWithHeaders(string method, string url, DataTable headersTable)
        {
            url = AddDataPrefixIfNecessary(url)
                .Replace("{id}", _id)
                .ReplacePlaceholdersWithDictionaryValues(_scenarioVariables.VariableByName);

            var headers = GetHeaders();

            foreach (var row in headersTable.Rows)
            {
                headers[row["Key"]] = row["Value"];
            }

            _apiResponse = await _playwrightContext.ApiRequestContext!.FetchAsync(
                url,
                new() { Method = method, Headers = headers }
            );
        }

        [When("a POST request larger than {int} MB is made to {string}")]
        public async Task WhenAPostRequestLargerThanMbIsMadeTo(int sizeInMb, string url)
        {
            url = AddDataPrefixIfNecessary(url);

            // Build a valid JSON payload body that exceeds the configured max body size.
            var oversizedValue = new string('A', sizeInMb * AppSettings.BytesPerMegabyte);
            var body = $$"""
                {
                    "schoolId": 1701,
                    "nameOfInstitution": "{{oversizedValue}}",
                    "gradeLevels": [
                        {
                            "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth grade"
                        }
                    ],
                    "educationOrganizationCategories": [
                        {
                            "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"
                        }
                    ]
                }
                """;

            using var httpClient = new HttpClient
            {
                BaseAddress = new Uri(_playwrightContext.ApiUrl.TrimEnd('/') + "/"),
                Timeout = TimeSpan.FromSeconds(30),
            };
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.TryAddWithoutValidation("Authorization", GetDmsTokenFromContext());
            request.Headers.ExpectContinue = true;
            request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");

            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead
            );
            _directResponseStatusCode = (int)response.StatusCode;
        }

        [Then("the direct response should be {int}")]
        public void ThenTheDirectResponseShouldBe(int statusCode)
        {
            _directResponseStatusCode.Should().NotBeNull("a direct request status should have been captured");
            _directResponseStatusCode.Should().Be(statusCode);
        }

        [When("a DELETE if-match {string} request is made to {string}")]
        public async Task WhenADeleteIf_MatchRequestIsMadeToWith(string ifMatch, string url)
        {
            url = AddDataPrefixIfNecessary(url)
                .Replace("{id}", _id)
                .Replace("{dependentId}", _dependentId)
                .ReplacePlaceholdersWithDictionaryValues(_scenarioVariables.VariableByName);

            _logger.log.Information($"DELETE url: {url}");

            ifMatch = ResolveEtagHeaderValue(ifMatch);
            _apiResponse = await _playwrightContext.ApiRequestContext?.DeleteAsync(
                url,
                new() { Headers = GetHeadersWithIfMatch(ifMatch) }
            )!;

            extractDataFromResponseAndReturnIdIfAvailable(_apiResponse);
        }

        [When("a GET if-none-match {string} request is made to {string}")]
        public async Task WhenAGetIf_NoneMatchRequestIsMadeTo(string ifNoneMatch, string url) =>
            await WhenAGETRequestIsMadeToWithHeader(url, "If-None-Match", ifNoneMatch);

        [When("the lastModifiedDate is stored")]
        public async Task GivenTheLastModifiedDateIsStored()
        {
            var responseJson = JsonNode.Parse(await _apiResponse.TextAsync());

            if (responseJson is not null)
            {
                _lastModifiedDate = LastModifiedDate(responseJson) ?? string.Empty;
            }
        }

        [When("a claim set is uploaded to CMS that grants {string} access to {string}")]
        public async Task WhenAClaimSetIsUploadedToCMSThatGrantsEndpointAccess(
            string endpointName,
            string claimSetName
        )
        {
            await UploadClaimSetToCms(
                endpointName,
                claimSetName,
                AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired
            );
        }

        [Given(
            "a claim set is uploaded to CMS that grants {string} access to {string} using authorization strategy {string}"
        )]
        public async Task GivenAClaimSetIsUploadedToCMSThatGrantsEndpointAccessUsingAuthorizationStrategy(
            string endpointName,
            string claimSetName,
            string authorizationStrategyName
        )
        {
            await UploadClaimSetToCms(endpointName, claimSetName, [authorizationStrategyName]);
        }

        [Given(
            "a claim set is uploaded to CMS that grants {string} access to {string} with no authorization strategies"
        )]
        public async Task GivenAClaimSetIsUploadedToCMSThatGrantsEndpointAccessWithNoAuthorizationStrategies(
            string endpointName,
            string claimSetName
        )
        {
            await UploadClaimSetToCms(endpointName, claimSetName, []);
        }

        // Composition: configures the claim's actions (including ReadChanges) with TWO authorization
        // strategies so that a ReadChanges row must satisfy every configured strategy. Used by the
        // TrackedChangeAuthorization composition scenarios.
        [Given(
            "a claim set is uploaded to CMS that grants {string} access to {string} using authorization strategies {string} and {string}"
        )]
        public async Task GivenAClaimSetIsUploadedToCMSThatGrantsEndpointAccessUsingTwoAuthorizationStrategies(
            string endpointName,
            string claimSetName,
            string firstAuthorizationStrategyName,
            string secondAuthorizationStrategyName
        )
        {
            await UploadClaimSetToCms(
                endpointName,
                claimSetName,
                [firstAuthorizationStrategyName, secondAuthorizationStrategyName]
            );
        }

        private async Task UploadClaimSetToCms(
            string endpointName,
            string claimSetName,
            string authorizationStrategyName
        )
        {
            await UploadClaimSetToCms(endpointName, claimSetName, [authorizationStrategyName]);
        }

        private async Task UploadClaimSetToCms(
            string endpointName,
            string claimSetName,
            IReadOnlyCollection<string> authorizationStrategyNames
        )
        {
            JsonObject BuildAction(string actionName) =>
                new()
                {
                    ["name"] = actionName,
                    ["authorizationStrategyOverrides"] = new JsonArray([
                        .. authorizationStrategyNames.Select(name => new JsonObject { ["name"] = name }),
                    ]),
                };

            string claimName = endpointName.StartsWith("domains/", StringComparison.Ordinal)
                ? $"http://ed-fi.org/identity/claims/{endpointName}"
                : $"http://ed-fi.org/identity/claims/ed-fi/{endpointName}";

            JsonObject claimNode = new()
            {
                ["name"] = claimName,
                ["claimSets"] = new JsonArray(
                    new JsonObject
                    {
                        ["name"] = claimSetName,
                        ["actions"] = new JsonArray(
                            BuildAction("Create"),
                            BuildAction("Read"),
                            BuildAction("Update"),
                            BuildAction("Delete"),
                            // ReadChanges authorizes the /deletes and /keyChanges Change Query
                            // endpoints. Granting it the same strategy as CRUD lets scenarios
                            // exercise ReadChanges authorization (relationship/namespace filtering,
                            // unsupported-strategy 500, no-prefixes 403) through this upload step.
                            BuildAction("ReadChanges")
                        ),
                    }
                ),
            };

            JsonObject claimsHierarchyNode = endpointName.StartsWith("domains/", StringComparison.Ordinal)
                ? claimNode
                : new JsonObject
                {
                    ["name"] = "http://ed-fi.org/identity/claims/domains/edFi",
                    ["claims"] = new JsonArray(claimNode),
                };

            string claimsJson = new JsonObject
            {
                ["claims"] = new JsonObject
                {
                    ["claimSets"] = new JsonArray(
                        new JsonObject { ["claimSetName"] = claimSetName, ["isSystemReserved"] = false }
                    ),
                    ["claimsHierarchy"] = new JsonArray(claimsHierarchyNode),
                },
            }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

            // Call the CMS endpoint to upload the claim set
            var httpClient = new HttpClient();
            var content = new StringContent(claimsJson, System.Text.Encoding.UTF8, "application/json");

            // Get SystemAdministrator auth header for CMS request
            httpClient.DefaultRequestHeaders.Add(
                "Authorization",
                $"Bearer {await SystemAdministrator.GetToken()}"
            );

            var response = await httpClient.PostAsync(
                $"http://localhost:{AppSettings.ConfigServicePort}/config/management/upload-claims",
                content
            );

            // Store the status code for verification
            _lastUploadStatusCode = (int)response.StatusCode;

            var responseBody = await response.Content.ReadAsStringAsync();
            _logger.log.Information(
                $"CMS Upload URL: http://localhost:{AppSettings.ConfigServicePort}/config/management/upload-claims"
            );
            _logger.log.Information($"CMS Upload Request JSON: {claimsJson}");
            _logger.log.Information($"CMS Upload Response Status: {_lastUploadStatusCode}");
            _logger.log.Information($"CMS Upload Response Body: {responseBody}");
        }

        [When("a POST request is made to DMS management endpoint {string}")]
        public async Task WhenAPOSTRequestIsMadeToDMSManagementEndpoint(string endpoint)
        {
            // Use DMS URL directly with the management endpoint
            var url = endpoint.StartsWith('/') ? endpoint[1..] : endpoint;

            _logger.log.Information($"DMS POST url: {url}");
            _apiResponse = await _playwrightContext.ApiRequestContext?.PostAsync(
                url,
                new APIRequestContextOptions
                {
                    Headers = new Dictionary<string, string>
                    {
                        { "Authorization", $"Bearer {await SystemAdministrator.GetToken()}" },
                    },
                }
            )!;

            _logger.log.Information($"DMS POST Response Status: {_apiResponse.Status}");
            var responseBody = await _apiResponse.TextAsync();
            _logger.log.Information($"DMS POST Response Body: {responseBody}");
        }

        [When("a GET request is made to DMS management endpoint {string}")]
        public async Task WhenAGETRequestIsMadeToDMSManagementEndpoint(string endpoint)
        {
            // Use DMS URL directly with the management endpoint
            var url = endpoint.StartsWith('/') ? endpoint[1..] : endpoint;

            _logger.log.Information($"DMS GET url: {url}");
            _apiResponse = await _playwrightContext.ApiRequestContext?.GetAsync(
                url,
                new APIRequestContextOptions
                {
                    Headers = new Dictionary<string, string>
                    {
                        { "Authorization", $"Bearer {await SystemAdministrator.GetToken()}" },
                    },
                }
            )!;

            _logger.log.Information($"DMS GET Response Status: {_apiResponse.Status}");
            var responseBody = await _apiResponse.TextAsync();
            _logger.log.Information($"DMS GET Response Body: {responseBody}");

            _scenarioContext["dmsViewResponse"] = responseBody;
        }

        [When("a POST request is made to CMS {string}")]
        public async Task WhenAPOSTRequestIsMadeToCMS(string endpoint)
        {
            string systemAdministratorToken = await SystemAdministrator.GetToken();

            // Use HttpClient to call CMS management API
            using var httpClient = new HttpClient
            {
                BaseAddress = new Uri($"http://localhost:{AppSettings.ConfigServicePort}/"),
            };

            httpClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", systemAdministratorToken);

            // POST with empty body for reload-claims endpoint
            using var content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
            var response = await httpClient.PostAsync($"config{endpoint}", content);

            // Store response for verification
            _lastUploadStatusCode = (int)response.StatusCode;

            // Also store the response body for reload ID verification if needed
            if (response.IsSuccessStatusCode)
            {
                var responseBody = await response.Content.ReadAsStringAsync();
                _scenarioContext["reloadResponse"] = responseBody;
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

        [Then("the token_info response body is")]
        public async Task ThenTheTokenInfoResponseBodyIs(string expectedBody)
        {
            await ResponseBodyIs(expectedBody, isTokenInfoEndpoint: true);
        }

        private async Task ResponseBodyIs(
            string expectedBody,
            bool isDiscoveryEndpoint = false,
            bool isTokenInfoEndpoint = false,
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

            if (
                !isDiscoveryEndpoint
                && !isTokenInfoEndpoint
                && (_apiResponse.Status == 200 || _apiResponse.Status == 201)
            )
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
            if (isDiscoveryEndpoint)
            {
                (responseJson as JsonObject)?.Remove("version");
                (expectedBodyJson as JsonObject)?.Remove("version");
            }

            if (isTokenInfoEndpoint && _apiResponse.Status == 200)
            {
                var clientId = ClientId(responseJson);
                clientId.Should().NotBeNull();
                (responseJson as JsonObject)?.Remove("client_id");
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

            // Remove version and the top-level informationalVersion as they vary by build:
            // locally/CI-built images fall back to the Directory.Build.props value, while
            // published images carry a MinVer-derived version baked in at build time. The
            // schema-driven informationalVersion values nested under dataModels are stable and
            // remain asserted.
            (responseJson as JsonObject)?.Remove("version");
            (expectedBodyJson as JsonObject)?.Remove("version");
            (responseJson as JsonObject)?.Remove("informationalVersion");
            (expectedBodyJson as JsonObject)?.Remove("informationalVersion");

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
            await ResponseBodyIs(expectedBody, isDiscoveryEndpoint: true, isXml: true);
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

                        JsonTestUtilities.RemoveLinkSubtrees(item);
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

                JsonTestUtilities.RemoveLinkSubtrees(jsonObject);
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

        private static string? ClientId(JsonNode response)
        {
            if (
                response is JsonObject jsonObject
                && jsonObject.TryGetPropertyValue("client_id", out JsonNode? clientId)
                && clientId != null
            )
            {
                return clientId.GetValue<string?>();
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

        [Then("the response body has a non-empty correlationId")]
        public async Task ThenTheResponseBodyHasANonEmptyCorrelationId()
        {
            string body = await _apiResponse.TextAsync();
            JsonNode responseJson = JsonNode.Parse(body)!;

            CorrelationIdValue(responseJson).Should().NotBeNullOrWhiteSpace();
        }

        [Then("the response body should not contain {string}")]
        public async Task ThenTheResponseBodyShouldNotContain(string text)
        {
            string body = await _apiResponse.TextAsync();
            body.Should().NotContain(text);
        }

        [Then("the response body should not contain {string} ignoring case")]
        public async Task ThenTheResponseBodyShouldNotContainIgnoringCase(string text)
        {
            string body = await _apiResponse.TextAsync();
            body.Should().NotContainEquivalentOf(text);
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

                    key.Should().NotBeNull($"response should include header '{header.Key}'");
                    _apiResponse.Headers[key!].Should().Contain(expectedValue);
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

        [Then("the response header {string} is not present")]
        public void ThenTheResponseHeaderIsNotPresent(string headerName)
        {
            var key = _apiResponse.Headers.Keys.FirstOrDefault(k =>
                k.Equals(headerName, StringComparison.OrdinalIgnoreCase)
            );

            key.Should().BeNull($"Header '{headerName}' should not be present.");
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
            _etag = StripEtagQuotes(_apiResponse.Headers["etag"]);
            _etag.Should().NotBeNullOrEmpty();
        }

        [Then("the quoted ETag is in the response header")]
        public void ThenTheQuotedEtagIsInTheResponseHeader()
        {
            _rawEtag = _apiResponse.Headers["etag"];
            _rawEtag.Should().NotBeNullOrEmpty();
            _rawEtag.Should().StartWith("\"").And.EndWith("\""); // RFC 9110 §8.8.3 quoted strong validator
        }

        // Asserts the opaque ETag value (quotes stripped) conforms to the DMS-1252 format:
        // "{ContentVersion}-{schemaEpoch}.{format}.{profileCode}.{linkFlag}". This locks in the
        // served shape at the API boundary rather than only asserting that some ETag exists.
        [Then("the ETag value matches the pattern {string}")]
        public void ThenTheEtagValueMatchesThePattern(string pattern)
        {
            string opaque = StripEtagQuotes(_apiResponse.Headers["etag"]);
            opaque.Should().MatchRegex(pattern);
        }

        // Captures the most recently served ETag (quotes stripped) under a scenario variable so a later
        // step can assert the ETag advanced. Use the resulting variable as "{name}" in an If-Match header.
        [Then("the ETag is stored in request variable {string}")]
        public void ThenTheEtagIsStoredInRequestVariable(string variableName)
        {
            _etag = StripEtagQuotes(_apiResponse.Headers["etag"]);
            _etag.Should().NotBeNullOrEmpty();
            _scenarioVariables.Add(variableName, _etag);
        }

        [Then("the ETag differs from request variable {string}")]
        public void ThenTheEtagDiffersFromRequestVariable(string variableName)
        {
            string current = StripEtagQuotes(_apiResponse.Headers["etag"]);
            current.Should().NotBeNullOrEmpty();
            current.Should().NotBe(_scenarioVariables.GetValueByName(variableName));
        }

        // The ETag response header is served as a quoted strong validator (RFC 9110 §8.8.3). Strip the
        // surrounding quotes so the captured value matches the unquoted _etag in the response body and
        // round-trips as an If-Match request header (the API accepts both quoted and unquoted forms).
        private static string StripEtagQuotes(string? etag) =>
            etag is { Length: >= 2 } && etag[0] == '"' && etag[^1] == '"'
                ? etag[1..^1]
                : etag ?? string.Empty;

        private string ResolveEtagHeaderValue(string value) =>
            value
                .Replace("{IfNoneMatch}", _etag)
                .Replace("{IfMatch}", _etag)
                .Replace("{IfMatchQuoted}", _rawEtag)
                .ReplacePlaceholdersWithDictionaryValues(_scenarioVariables.VariableByName);

        [Then("the lastModifiedDate has not changed")]
        public async Task ThenTheLastModifiedDateHasNotChanged()
        {
            var responseJson = JsonNode.Parse(await _apiResponse.TextAsync());

            if (responseJson is not null)
            {
                _lastModifiedDate.Should().Be(LastModifiedDate(responseJson));
            }
        }

        [Then("the claim set upload to CMS should be successful")]
        public void ThenTheClaimSetUploadToCMSShouldBeSuccessful()
        {
            _lastUploadStatusCode.Should().Be(200, "Claim set upload should succeed");
        }

        [Then("the DMS claimsets reload should be successful")]
        public void ThenTheDMSClaimsetsReloadShouldBeSuccessful()
        {
            _apiResponse.Status.Should().Be(200, "DMS claimsets reload should succeed");

            var responseBody = _apiResponse.TextAsync().Result;
            _logger.log.Information($"DMS Reload Response: {responseBody}");
        }

        [Then("the DMS view claimsets should be successful")]
        public void ThenTheDMSViewClaimsetsShouldBeSuccessful()
        {
            _apiResponse.Status.Should().Be(200, "DMS view claimsets should succeed");

            // Store the response body for verification
            var responseBody = _apiResponse.TextAsync().Result;
            _logger.log.Information($"DMS View Response: {responseBody}");
            _scenarioContext["dmsViewResponse"] = responseBody;
        }

        [Then("the DMS view claimsets response should contain {string}")]
        public void ThenTheDMSViewClaimsetsResponseShouldContain(string claimSetName)
        {
            _scenarioContext.Should().ContainKey("dmsViewResponse", "DMS view response should be available");

            var responseBody = _scenarioContext["dmsViewResponse"] as string;
            responseBody.Should().NotBeNullOrEmpty("DMS view response should not be empty");

            responseBody
                .Should()
                .Contain(claimSetName, $"Claimsets view should contain the claimset '{claimSetName}'");
        }

        [Then("the DMS view claimsets response should not contain {string}")]
        public void ThenTheDMSViewClaimsetsResponseShouldNotContain(string claimSetName)
        {
            _scenarioContext.Should().ContainKey("dmsViewResponse", "DMS view response should be available");

            var responseBody = _scenarioContext["dmsViewResponse"] as string;
            responseBody.Should().NotBeNullOrEmpty("DMS view response should not be empty");

            responseBody
                .Should()
                .NotContain(claimSetName, $"Claimsets view should not contain the claimset '{claimSetName}'");
        }

        [Then("the CMS reload should be successful")]
        public void ThenTheCMSReloadShouldBeSuccessful()
        {
            _lastUploadStatusCode.Should().Be(200, "Claim set reload should succeed");

            // Optionally verify the response contains success flag
            if (_scenarioContext.ContainsKey("reloadResponse"))
            {
                var responseBody = _scenarioContext["reloadResponse"].ToString();
                responseBody.Should().Contain("\"success\":true", "Reload response should indicate success");
            }
        }

        [Then("system claim sets should have empty resource claims")]
        public void ThenSystemClaimSetsShouldHaveEmptyResourceClaims()
        {
            _scenarioContext.Should().ContainKey("dmsViewResponse", "DMS view response should be available");

            var responseBody = _scenarioContext["dmsViewResponse"] as string;
            responseBody.Should().NotBeNullOrEmpty("DMS view response should not be empty");

            // Parse the JSON response
            List<ClaimSetResponse>? deserializedClaimSets = System.Text.Json.JsonSerializer.Deserialize<
                List<ClaimSetResponse>
            >(
                responseBody!,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            );

            List<ClaimSetResponse> claimSets = deserializedClaimSets ?? [];

            claimSets.Should().NotBeNull("Claim sets should be deserializable");

            // Define known system claim sets
            var systemClaimSetNames = new HashSet<string>
            {
                "SISVendor",
                "EdFiSandbox",
                "RosterVendor",
                "AssessmentVendor",
                "AssessmentRead",
                "BootstrapDescriptorsandEdOrgs",
                "DistrictHostedSISVendor",
                "EdFiODSAdminApp",
                "ABConnect",
                "E2E-NameSpaceBasedClaimSet",
                "E2E-NoFurtherAuthRequiredClaimSet",
                "E2E-RelationshipsWithEdOrgsOnlyClaimSet",
                "E2E-RelationshipsWithEdOrgsOnlyInvertedClaimSet",
                "E2E-RelationshipsWithEdOrgsOnlyOrInvertedClaimSet",
                "E2E-RelationshipsWithEdOrgsOnlyMixedStrategyClaimSet",
            };

            // Find and verify system claim sets
            List<ClaimSetResponse> systemClaimSets = claimSets!
                .Where(cs => systemClaimSetNames.Contains(cs.Name))
                .ToList();

            _logger.log.Information(
                "Found {SystemClaimSetCount} system claim sets out of {TotalClaimSetCount} total claim sets",
                systemClaimSets.Count,
                claimSets!.Count
            );

            foreach (var systemClaimSet in systemClaimSets)
            {
                _logger.log.Information(
                    "Verifying system claim set '{ClaimSetName}' has {ResourceClaimCount} resource claims",
                    systemClaimSet.Name,
                    systemClaimSet.ResourceClaims?.Count ?? 0
                );

                systemClaimSet
                    .ResourceClaims.Should()
                    .BeEmpty($"System claim set '{systemClaimSet.Name}' should have empty resource claims");
            }

            // Also verify that at least some system claim sets were found
            systemClaimSets
                .Should()
                .NotBeEmpty("At least some system claim sets should be present in the response");
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

            // metadata, oauth, and changeQueries are server-root endpoints, not under /data.
            input =
                input.StartsWith("metadata") || input.StartsWith("oauth") || input.StartsWith("changeQueries")
                    ? input
                    : $"data/{input}";

            return input;
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

        // Auth-only headers for requests without a body (GET/DELETE).
        private Dictionary<string, string> GetHeaders(params KeyValuePair<string, string>[] additionalHeaders)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Authorization"] = GetDmsTokenFromContext(),
            };

            foreach (var header in additionalHeaders)
            {
                headers[header.Key] = header.Value;
            }

            return headers;
        }

        // Headers for write requests (POST/PUT) carrying a JSON body. An explicit Content-Type is
        // required because Playwright defaults string/byte bodies to application/octet-stream, which
        // DMS rejects with 415 (DMS-1224). Read/delete requests have no body and use GetHeaders().
        private Dictionary<string, string> GetWriteHeaders() =>
            GetHeaders(KeyValuePair.Create("Content-Type", "application/json"));

        private Dictionary<string, string> GetHeadersWithIfMatch(string ifMatch) =>
            GetHeaders(KeyValuePair.Create("If-Match", ifMatch));

        private Dictionary<string, string> GetWriteHeadersWithIfMatch(string ifMatch) =>
            GetHeaders(
                KeyValuePair.Create("Content-Type", "application/json"),
                KeyValuePair.Create("If-Match", ifMatch)
            );

        private Dictionary<string, string> GetWriteHeadersWithIfNoneMatch(string ifNoneMatch) =>
            GetHeaders(
                KeyValuePair.Create("Content-Type", "application/json"),
                KeyValuePair.Create("If-None-Match", ifNoneMatch)
            );

        [Then("the JWT token should contain the dataStoreIds claim")]
        public void ThenTheJwtTokenShouldContainTheDataStoreIdsClaim()
        {
            // Extract the Bearer token
            var token = GetDmsTokenFromContext().Replace("Bearer ", string.Empty);

            // Parse the JWT token
            var tokenHandler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
            tokenHandler.CanReadToken(token).Should().BeTrue("Token should be a valid JWT");

            var jwtToken = tokenHandler.ReadJwtToken(token);

            // Verify the dataStoreIds claim exists
            var dataStoreIdsClaim = jwtToken.Claims.FirstOrDefault(c => c.Type == "dataStoreIds");
            dataStoreIdsClaim.Should().NotBeNull("JWT token should contain dataStoreIds claim");

            _logger.log.Information($"dataStoreIds claim value: {dataStoreIdsClaim?.Value}");
        }

        // Helper class for deserializing the claim set response
        private class ClaimSetResponse
        {
            public string Name { get; set; } = string.Empty;
            public List<ResourceClaimResponse> ResourceClaims { get; set; } = [];
        }

        private class ResourceClaimResponse
        {
            // Properties intentionally left empty as we only care about the count
        }

        private string GetDmsTokenFromContext()
        {
            return _scenarioContext.GetValueOrDefault("dmsToken")?.ToString() ?? string.Empty;
        }

        private static string Base64UrlEncode(byte[] input)
        {
            return Convert.ToBase64String(input).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private static byte[] Base64UrlDecode(string input)
        {
            string padded = input.Replace('-', '+').Replace('_', '/');
            padded = (padded.Length % 4) switch
            {
                2 => padded + "==",
                3 => padded + "=",
                _ => padded,
            };

            return Convert.FromBase64String(padded);
        }
    }
}
