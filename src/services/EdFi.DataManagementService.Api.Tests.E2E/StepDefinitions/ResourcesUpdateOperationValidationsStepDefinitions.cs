// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using EdFi.DataManagementService.Api.Tests.E2E.Management;
using FluentAssertions;
using Microsoft.Playwright;
using Reqnroll;

namespace EdFi.DataManagementService.Api.Tests.E2E.StepDefinitions
{
    [Binding]
    public class ResourcesUpdateOperationValidationsStepDefinitions(PlaywrightContext _playwrightContext)
    {
        private IAPIResponse _apiResponse = null!;
        private string _id = string.Empty;

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

        [Given("request made to {string} with")]
        public async Task GivenRequestMadeToWith(string url, string body)
        {
            _apiResponse = await _playwrightContext.ApiRequestContext?.PostAsync(url, new() { Data = body })!;
            _id = _apiResponse.Headers["location"].Split('/').Last();
        }

        [Then("it should respond with {int}")]
        public void ThenItShouldRespondWith(int statusCode)
        {
            _apiResponse.Status.Should().Be(statusCode);
            
        }

        [When("a PUT request is made to {string} with")]
        public async Task WhenAPUTRequestIsMadeToWith(string url, string body)
        {
            _apiResponse = await _playwrightContext.ApiRequestContext?.PutAsync(string.Format(url.Replace("{id}", _id)), new() { Data = body.Replace("{id}", _id) })!;
        }

        [When("a GET request is made to {string}")]
        public async Task WhenAGETRequestIsMadeTo(string url)
        {
            _apiResponse = await _playwrightContext.ApiRequestContext?.GetAsync(string.Format(url.Replace("{id}", _id)))!;
        }

        //[Then("the response headers includes")]
        //public void ThenTheResponseHeadersIncludes(string multilineText)
        //{
        //    throw new PendingStepException();
        //}

        [Then("the response message body is")]
        public void ThenTheResponseMessageBodyIs(string body)
        {
            _apiResponse.TextAsync().Result.Should().Be(body.Replace("{id}", _id));
        }

        //[When("a POST request is made to {string} with")]
        //public void WhenAPOSTRequestIsMadeToWith(string p0, string multilineText)
        //{
        //    throw new PendingStepException();
        //}
    }
}
