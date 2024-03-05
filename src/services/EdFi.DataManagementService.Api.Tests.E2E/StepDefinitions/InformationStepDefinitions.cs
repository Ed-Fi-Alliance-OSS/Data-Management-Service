using System;
using FluentAssertions;
using Microsoft.Playwright;
using System.Net;
using Reqnroll;
using EdFi.DataManagementService.Api.Tests.E2E.Management;

namespace EdFi.DataManagementService.Api.Tests.E2E.StepDefinitions;

[Binding]
public class InformationStepDefinitions
{

    private PlaywrightContext _PlaywrightContext = null!;
    private IAPIResponse _APIResponse = null!;

    public InformationStepDefinitions(PlaywrightContext context)
    {
        _PlaywrightContext = context;
    }

    [Given("a get to the root of the API")]
    public async Task GivenAGetToTheRootOfTheAPIAsync()
    {
        _APIResponse = await _PlaywrightContext.ApiRequestContext?.GetAsync("")!;
    }

    [Then("retrieves the information about the API")]
    public async Task ThenRetrievesTheInformationAboutTheAPIAsync()
    {
        string content = await _APIResponse.TextAsync();
        string expectedInformation = "Data Management Service";

        _APIResponse.Status.Should().Be((int)HttpStatusCode.OK);
        content.Should().Contain(expectedInformation);
    }

}
