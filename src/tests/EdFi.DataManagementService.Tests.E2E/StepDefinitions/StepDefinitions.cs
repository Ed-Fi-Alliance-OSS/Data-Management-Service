// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Tests.E2E.Management;
using FluentAssertions;
using Microsoft.Playwright;
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

        #endregion

        #region When

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
            body = body.Replace("{id}", _id);
            JsonNode bodyJson = JsonNode.Parse(body)!;
            JsonNode responseJson = JsonNode.Parse(_apiResponse.TextAsync().Result)!;
            _logger.log.Information(responseJson.ToString());
            JsonNode.DeepEquals(bodyJson, responseJson).Should().BeTrue();
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

        [Then("total of records should be {int}")]
        public void ThenTotalOfRecordsShouldBe(int totalRecords)
        {
            JsonNode responseJson = JsonNode.Parse(_apiResponse.TextAsync().Result)!;
            _logger.log.Information(responseJson.ToString());

            int count = 0;
            if (responseJson != null)
            {
                if (responseJson is JsonObject jsonObject)
                {
                    foreach (var property in jsonObject)
                    {
                        count += 1;
                    }
                }
                // If the node is an array, count its elements
                else if (responseJson is JsonArray jsonArray)
                {
                    foreach (var item in jsonArray)
                    {
                        count += 1;
                    }
                }
            }

            count.Should().Be(totalRecords);
        }
        #endregion


    }
}
