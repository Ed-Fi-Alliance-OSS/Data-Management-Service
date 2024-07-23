// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EdFi.DataManagementService.Tests.E2E.Extensions;
using EdFi.DataManagementService.Tests.E2E.Management;
using FluentAssertions;
using Microsoft.Playwright;
using Newtonsoft.Json.Linq;
using Reqnroll;

namespace EdFi.DataManagementService.Tests.E2E.StepDefinitions
{
    [Binding]
    public class StepDefinitions(PlaywrightContext _playwrightContext, TestLogger _logger)
    {
        private IAPIResponse _apiResponse = null!;
        private string _id = string.Empty;
        private string _dependentId = string.Empty;
        private string _location = string.Empty;
        private string _referencedResourceId = string.Empty;

        #region Given

        [Given("the Data Management Service must receive a token issued by {string}")]
        public void GivenTheDataManagementServiceMustReceiveATokenIssuedBy(string p0)
        {
            //throw new PendingStepException();
        }

        [Given("user is already authorized")]
        public void GivenUserIsAlreadyAuthorized()
        {
            //throw new PendingStepException();
        }

        [Given("a POST request is made to {string} with")]
        public async Task GivenAPOSTRequestIsMadeToWith(string url, string body)
        {
            url = $"data/{url}";
            _logger.log.Information(url);
            _apiResponse = await _playwrightContext.ApiRequestContext?.PostAsync(url, new() { Data = body })!;
            if (_apiResponse.Headers.ContainsKey("location"))
            {
                _location = _apiResponse.Headers["location"];
                _id = _apiResponse.Headers["location"].Split('/').Last();
            }
        }

        [Given("there are no schools")]
        public void GivenThereAreNoSchools()
        {
            //throw new PendingStepException();
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
                    { "shortDescription", codeValue }
                }
            );
        }

        private async Task<List<IAPIResponse>> ProcessDataTable(string entityType, DataTable dataTable)
        {
            List<IAPIResponse> _apiResponses = [];
            var baseUrl = $"data/ed-fi";

            foreach (var row in dataTable.Rows)
            {
                var dataUrl = $"{baseUrl}/{entityType}";

                string body = row.Parse();

                _logger.log.Information(dataUrl);
                _apiResponses.Add(
                    await _playwrightContext.ApiRequestContext?.PostAsync(dataUrl, new() { Data = body })!
                );
            }

            foreach (var apiResponse in _apiResponses)
            {
                if (apiResponse.Status != 200 && apiResponse.Status != 201)
                {
                    JsonNode responseJson = JsonNode.Parse(apiResponse.TextAsync().Result)!;

                    _logger.log.Information(responseJson.ToString());
                }
            }
            return _apiResponses;
        }

        [Given("the system has these {string}")]
        public async Task GivenTheSystemHasThese(string entityType, DataTable dataTable)
        {
            var _apiResponses = await ProcessDataTable(entityType, dataTable);

            _logger.log.Information($"Responses for Given(the system has these {entityType})");

            foreach (var response in _apiResponses)
            {
                string body = response.TextAsync().Result;
                _logger.log.Information(body);
            }
        }

        [Given("the system has these descriptors")]
        public async Task GivenTheSystemHasTheseDescriptors(DataTable dataTable)
        {
            _logger.log.Information($"Responses for Given(the system has these descriptors)");

            string baseUrl = $"data/ed-fi";

            foreach (DataTableRow row in dataTable.Rows)
            {
                string descriptorValue = row.Parse();
                var (descriptorName, descriptorBody) = ExtractDescriptorBody(descriptorValue);

                IAPIResponse apiResponse = await _playwrightContext.ApiRequestContext?.PostAsync(
                    $"{baseUrl}/{descriptorName}",
                    new() { DataObject = descriptorBody }
                )!;

                string body = apiResponse.TextAsync().Result;
                _logger.log.Information(body);

                apiResponse.Status.Should().BeOneOf([201, 200]);
            }
        }

        [Given("the system has these {string} references")]
        public async Task GivenTheSystemHasTheseReferences(string entityType, DataTable dataTable)
        {
            var _apiResponses = await ProcessDataTable(entityType, dataTable);

            _logger.log.Information($"Responses for Given(the system has these {entityType} references");

            foreach (var response in _apiResponses)
            {
                string body = response.TextAsync().Result;
                _logger.log.Information(body);
                response.Status.Should().BeOneOf([201, 200]);

                if (
                    response.Headers.ContainsKey("location")
                    && response.Url.Contains(entityType, StringComparison.InvariantCultureIgnoreCase)
                )
                {
                    _location = response.Headers["location"];
                    _referencedResourceId = response.Headers["location"].Split('/').Last();
                }
            }
        }

        #endregion

        #region When

        [When("a POST request is made to {string} with")]
        public async Task WhenSendingAPOSTRequestToWithBody(string url, string body)
        {
            url = $"data/{url}";
            _logger.log.Information($"POST url: {url}");
            _logger.log.Information($"POST body: {body}");
            _apiResponse = await _playwrightContext.ApiRequestContext?.PostAsync(url, new() { Data = body })!;
            _logger.log.Information(_apiResponse.TextAsync().Result);
            if (_apiResponse.Headers.ContainsKey("location"))
            {
                _location = _apiResponse.Headers["location"];
                _id = _apiResponse.Headers["location"].Split('/').Last();
            }
        }

        [When("a POST request is made for dependent resource {string} with")]
        public async Task WhenSendingAPOSTRequestForDependentResourceWithBody(string url, string body)
        {
            url = $"data/{url}";
            _apiResponse = await _playwrightContext.ApiRequestContext?.PostAsync(url, new() { Data = body })!;
            if (_apiResponse.Headers.ContainsKey("location"))
            {
                _location = _apiResponse.Headers["location"];
                _dependentId = _apiResponse.Headers["location"].Split('/').Last();
            }
        }

        [When("a PUT request is made to {string} with")]
        public async Task WhenAPUTRequestIsMadeToWith(string url, string body)
        {
            url = $"data/{url.Replace("{id}", _id)}";
            body = body.Replace("{id}", _id);
            _logger.log.Information($"PUT url: {url}");
            _logger.log.Information($"PUT body: {body}");
            _apiResponse = await _playwrightContext.ApiRequestContext?.PutAsync(url, new() { Data = body })!;
        }

        [When("a PUT request is made to referenced resource {string} with")]
        public async Task WhenAPUTRequestIsMadeToReferencedResourceWith(string url, string body)
        {
            url = $"data/{url.Replace("{id}", _referencedResourceId)}";
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
                    JsonNode responseJson = JsonNode.Parse(result)!;
                }
                catch (Exception e)
                {
                    throw new Exception($"Unable to parse the JSON result from the API server: {e.Message}", e);
                }
            }
        }

        [When("a DELETE request is made to {string}")]
        public async Task WhenADELETERequestIsMadeTo(string url)
        {
            url = $"data/{url.Replace("{id}", _id)}";
            _apiResponse = await _playwrightContext.ApiRequestContext?.DeleteAsync(url)!;
        }

        [When("a DELETE request is made to referenced resource {string}")]
        public async Task WhenADELETERequestIsMadeToReferencedResource(string url)
        {
            url = $"data/{url.Replace("{id}", _referencedResourceId)}";
            _apiResponse = await _playwrightContext.ApiRequestContext?.DeleteAsync(url)!;
        }

        [When("a GET request is made to {string}")]
        public async Task WhenAGETRequestIsMadeTo(string url)
        {
            url = $"data/{url.Replace("{id}", _id)}";
            _logger.log.Information(url);
            _apiResponse = await _playwrightContext.ApiRequestContext?.GetAsync(url)!;
        }

        [When("a GET request is made to {string} using values as")]
        public async Task WhenAGETRequestIsMadeToUsingValuesAs(string url, Table table)
        {
            url = $"data/{url}";
            foreach (var row in table.Rows)
            {
                var value = row["Values"];
                var requestUrl = $"{url}{value}";
                _apiResponse = await _playwrightContext.ApiRequestContext?.GetAsync(url)!;
            }
        }

        #endregion

        #region Then

        [Then("it should respond with {int}")]
        public void ThenItShouldRespondWith(int statusCode)
        {
            string body = _apiResponse.TextAsync().Result;
            _logger.log.Information(body);
            _apiResponse.Status.Should().Be(statusCode);
        }

        [Then("it should respond with {int} or {int}")]
        public void ThenItShouldRespondWithEither(int statusCode1, int statusCode2)
        {
            string body = _apiResponse.TextAsync().Result;
            _logger.log.Information(body);
            _apiResponse.Status.Should().BeOneOf([statusCode1, statusCode2]);
        }

        [Then("the response body is")]
        public void ThenTheResponseBodyIs(string expectedBody)
        {
            // Parse the API response to JsonNode
            string responseBody = _apiResponse.TextAsync().Result;
            JsonNode responseJson = JsonNode.Parse(responseBody)!;

            expectedBody = ReplacePlaceholders(expectedBody, responseJson);
            JsonNode expectedBodyJson = JsonNode.Parse(expectedBody)!;

            _logger.log.Information(responseJson.ToString());

            (responseJson as JsonObject)?.Remove("correlationId");
            (expectedBodyJson as JsonObject)?.Remove("correlationId");

            try
            {
                responseJson.Should().BeEquivalentTo(expectedBodyJson, options => options
                    .WithoutStrictOrdering()
                    .IgnoringCyclicReferences()
                    .RespectingRuntimeTypes()
                );
            }
            catch (Exception e)
            {
                _logger.log.Information(e.Message);
                throw;
            }
        }

        // Use Regex to find all occurrences of {id} in the body
        private static readonly Regex _findIds = new Regex(@"\{id\}", RegexOptions.Compiled);

        private string ReplacePlaceholders(string body, JsonNode responseJson)
        {
            string replacedBody = "";
            if (body.TrimStart().StartsWith("["))
            {
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
            return replacedBody;
        }

        [Then("the response headers includes")]
        public void ThenTheResponseHeadersIncludes(string headers)
        {
            var value = JsonNode.Parse(headers)!;
            foreach (var header in value.AsObject())
            {
                if (header.Value != null)
                    _apiResponse
                        .Headers[header.Key]
                        .Should()
                        .EndWith("data" + header.Value.ToString().Replace("{id}", _id));
            }
        }

        [Then("the record can be retrieved with a GET request")]
        public async Task ThenTheRecordCanBeRetrievedWithAGETRequest(string body)
        {
            body = body.Replace("{id}", _id);
            JsonNode bodyJson = JsonNode.Parse(body)!;
            _apiResponse = await _playwrightContext.ApiRequestContext?.GetAsync(_location)!;
            string responseJsonString = _apiResponse.TextAsync().Result;
            JsonNode responseJson = JsonNode.Parse(responseJsonString)!;
            _logger.log.Information(responseJson.ToString());
            responseJson.Should().BeEquivalentTo(bodyJson, options => options
                .WithoutStrictOrdering()
                .IgnoringCyclicReferences()
            );
        }

        [Then("total of records should be {int}")]
        public void ThenTotalOfRecordsShouldBe(int totalRecords)
        {
            JsonNode responseJson = JsonNode.Parse(_apiResponse.TextAsync().Result)!;
            _logger.log.Information(responseJson.ToString());

            int count = responseJson.AsArray().Count();
            count.Should().Be(totalRecords);
        }

        [Then("the response headers includes total-count {int}")]
        public void ThenTheResponseHeadersIncludesTotalCount(string totalCount)
        {
            var headers = _apiResponse.Headers;
            headers.GetValueOrDefault("total-count").Should().Be(totalCount);
        }

        [Then("the response headers does not include total-count")]
        public void ThenTheResponseHeadersDoesNotIncludeTotalCount()
        {
            var headers = _apiResponse.Headers;
            headers.ContainsKey("total-count").Should().BeFalse();
        }

        [Then("getting less schools than the total-count")]
        public void ThenGettingLessSchoolsThanTheTotalCount()
        {
            var headers = _apiResponse.Headers;

            JsonNode responseJson = JsonNode.Parse(_apiResponse.TextAsync().Result)!;
            _logger.log.Information(responseJson.ToString());

            int count = responseJson.AsArray().Count();

            headers.GetValueOrDefault("total-count").Should().NotBe(count.ToString());
        }

        [Then("schools returned")]
        public void ThenSchoolsReturned(Table table)
        {
            var url = $"data/ed-fi/schools?offset=3&limit=5";

            var jsonResponse = _apiResponse.TextAsync().Result;
            var responseArray = JArray.Parse(jsonResponse);

            foreach (var row in table.Rows)
            {
                var expectedSchoolId = row["schoolId"];
                var expectedNameOfInstitution = row["nameOfInstitution"];

                var matchSchoolId = responseArray.Any(school =>
                    school["schoolId"]?.ToString() == expectedSchoolId
                );
                var matchNameOfInstitution = responseArray.Any(school =>
                    school["nameOfInstitution"]?.ToString() == expectedNameOfInstitution
                );

                matchSchoolId.Should().BeTrue();
                matchNameOfInstitution.Should().BeTrue();
            }
        }

        #endregion
    }
}
