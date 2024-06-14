// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.


using System;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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
        private List<IAPIResponse> _apiResponses = null!;
        private string _id = string.Empty;
        private string _dependentId = string.Empty;
        private string _location = string.Empty;

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

        [Given("the following schools exist")]
        public async Task GivenTheFollowingSchoolsExist(Table table)
        {
            var url = $"data/ed-fi/schools";
            var schools = table
                .Rows.Select(row =>
                {
                    var gradeLevels = JsonSerializer
                        .Deserialize<List<string>>(row["gradeLevels"])
                        ?.Select(descriptor => new GradeLevel(descriptor))
                        .ToList();

                    var educationOrgCategories = JsonSerializer
                        .Deserialize<List<string>>(row["educationOrganizationCategories"])
                        ?.Select(descriptor => new EducationOrganizationCategory(descriptor))
                        .ToList();

                    var schoolId = row["schoolId"] != null ? int.Parse(row["schoolId"]) : (int?)null;
                    var nameOfInstitution = row["nameOfInstitution"];

                    return new School(
                        schoolId: schoolId,
                        nameOfInstitution: nameOfInstitution,
                        gradeLevels: gradeLevels,
                        educationOrganizationCategories: educationOrgCategories
                    );
                })
                .ToList();

            var apiResponses = new List<IAPIResponse>();

            foreach (var school in schools)
            {
                var data = JsonSerializer.Serialize(school);
                _apiResponse = await _playwrightContext.ApiRequestContext?.PostAsync(
                    url,
                    new() { Data = data }
                )!;
                apiResponses.Add(_apiResponse);
            }
            foreach (var response in apiResponses)
            {
                response.Status.Should().BeOneOf([200, 201]);
            }
        }

        #endregion

        #region When

        [When("a POST request with list of required descriptors")]
        public async Task WhenAPOSTRequestWithListOfRequiredDescriptors(DataTable requiredDescriptors)
        {
            await PostDataRows(requiredDescriptors, "descriptorname");
        }

        [When("a POST request with list of required resources")]
        public async Task WhenAPOSTRequestWithListOfRequiredResources(DataTable requiredResources)
        {
            await PostDataRows(requiredResources, "resourcename");
        }

        private async Task PostDataRows(DataTable dataTable, string typeName)
        {
            var options = new JsonSerializerOptions
            {
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                WriteIndented = true
            };

            _apiResponses = new List<IAPIResponse>();
            var baseUrl = $"data/ed-fi";

            foreach (var row in dataTable.Rows)
            {
                var dataUrl = $"{baseUrl}/{row[typeName]}";
                var rowDict = new Dictionary<string, object>();
                foreach (var column in dataTable.Header)
                {
                    if (!column.Equals(typeName))
                    {
                        rowDict[column] = ConvertValueToCorrectType(row[column]);
                    }
                }
                string body = JsonSerializer
                    .Serialize(rowDict, options)
                    .Replace("\"[", "[")
                    .Replace("]\"", "]")
                    .Replace("\\\"", "\"");

                _logger.log.Information(dataUrl);
                _apiResponses.Add(
                    await _playwrightContext.ApiRequestContext?.PostAsync(dataUrl, new() { Data = body })!
                );
            }
        }

        private object ConvertValueToCorrectType(string value)
        {
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue))
                return intValue;

            if (
                decimal.TryParse(
                    value,
                    NumberStyles.Number,
                    CultureInfo.InvariantCulture,
                    out var decimalValue
                )
            )
                return decimalValue;

            if (
                DateTime.TryParse(
                    value,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var dateTimeValue
                )
            )
                return dateTimeValue;

            if (bool.TryParse(value, out var boolValue))
                return boolValue;

            return value;
        }

        [When("a POST request is made to {string} with")]
        public async Task WhenSendingAPOSTRequestToWithBody(string url, string body)
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
            _logger.log.Information(url);
            _apiResponse = await _playwrightContext.ApiRequestContext?.PutAsync(url, new() { Data = body })!;
        }

        [When("a DELETE request is made to {string}")]
        public async Task WhenADELETERequestIsMadeTo(string url)
        {
            url = $"data/{url.Replace("{id}", _id)}";
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
            _apiResponse.Status.Should().Be(statusCode);
        }

        [Then("it should respond with {int} or {int}")]
        public void ThenItShouldRespondWithEither(int statusCode1, int statusCode2)
        {
            _apiResponse.Status.Should().BeOneOf([statusCode1, statusCode2]);
        }

        [Then("the response body is")]
        public void ThenTheResponseBodyIs(string body)
        {
            // Parse the API response to JsonNode
            JsonNode responseJson = JsonNode.Parse(_apiResponse.TextAsync().Result)!;

            body = ReplacePlaceholders(body, responseJson);
            JsonNode bodyJson = JsonNode.Parse(body)!;

            _logger.log.Information(responseJson.ToString());

            JsonNode.DeepEquals(bodyJson, responseJson).Should().BeTrue();
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
            JsonNode responseJson = JsonNode.Parse(_apiResponse.TextAsync().Result)!;
            _logger.log.Information(responseJson.ToString());
            JsonNode.DeepEquals(bodyJson, responseJson).Should().BeTrue();
        }

        [Then("all responses should be {int} or {int}")]
        public void ThenAllResponsesShouldBe(int statusCode1, int statusCode2)
        {
            foreach (var response in _apiResponses)
            {
                response.Status.Should().BeOneOf([statusCode1, statusCode2]);
            }
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
