// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using EdFi.DataManagementService.Tests.E2E.Management;
using FluentAssertions;
using Microsoft.Playwright;
using Reqnroll;

namespace EdFi.DataManagementService.Tests.E2E.StepDefinitions;

[Binding]
public class InformationStepDefinitions
{
    private readonly PlaywrightContext _PlaywrightContext;
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
        _APIResponse.Status.Should().Be((int)HttpStatusCode.OK);
        content.Should().Contain("version");
        content.Should().Contain("dataModels");
    }
}
