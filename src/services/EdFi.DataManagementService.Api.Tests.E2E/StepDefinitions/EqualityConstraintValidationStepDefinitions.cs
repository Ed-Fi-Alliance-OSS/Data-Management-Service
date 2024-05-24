// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Api.Tests.E2E.Management;
using FluentAssertions;
using Microsoft.Playwright;
using Reqnroll;

namespace EdFi.DataManagementService.Api.Tests.E2E.StepDefinitions;

[Binding]
public class EqualityConstraintValidationStepDefinitions(PlaywrightContext _playwrightContext)
{
    private IAPIResponse _apiResponse = null!;

    [When("a POST request is made to {string} with")]
    public async Task WhenSendingAPOSTRequestToWithBody(string url, string body)
    {
        _apiResponse = await _playwrightContext.ApiRequestContext?.PostAsync(url, new() { Data = body })!;
    }

    [Then("it should respond with {int}")]
    public void ThenTheResponseCodeIs(int response)
    {
        _apiResponse.Status.Should().Be(response);
    }

    [Then("the response body is")]
    public async Task ThenTheResponseBodyIs(string responseBody)
    {
        string content = await _apiResponse.TextAsync();
        content.Should().Be(responseBody);
    }

}
