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
        public async Task GivenRequestMadeToWith(string p0, string multilineText)
        {
            _apiResponse = await _playwrightContext.ApiRequestContext?.PostAsync(p0, new() { Data = multilineText })!;
            _id = _apiResponse.Headers["location"].Split('/').Last();
        }

        [Then("it should respond with {int}")]
        public void ThenItShouldRespondWith(int p0)
        {
            _apiResponse.Status.Should().Be(p0);
            
        }

        [When("a PUT request is made to {string} with")]
        public async Task WhenAPUTRequestIsMadeToWith(string p0, string multilineText)
        {
            _apiResponse = await _playwrightContext.ApiRequestContext?.PutAsync(string.Format(p0.Replace("{id}", _id)), new() { Data = multilineText.Replace("{id}", _id) })!;
        }

        //[When("a GET request is made to {string}")]
        //public void WhenAGETRequestIsMadeTo(string p0)
        //{
        //    throw new PendingStepException();
        //}

        //[Then("the response headers includes")]
        //public void ThenTheResponseHeadersIncludes(string multilineText)
        //{
        //    throw new PendingStepException();
        //}

        //[Then("the response message body is")]
        //public void ThenTheResponseMessageBodyIs(string multilineText)
        //{
        //    throw new PendingStepException();
        //}

        //[When("a POST request is made to {string} with")]
        //public void WhenAPOSTRequestIsMadeToWith(string p0, string multilineText)
        //{
        //    throw new PendingStepException();
        //}
    }
}
