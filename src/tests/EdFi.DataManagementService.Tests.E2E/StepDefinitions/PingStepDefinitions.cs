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
public sealed class PingStepDefinitions
{

    private PlaywrightContext _playwrightContext = null!;
    private IAPIResponse _APIResponse = null!;

    public PingStepDefinitions(PlaywrightContext context)
    {
        _playwrightContext = context;
    }

    [Given("a ping to the server")]
    public async Task Given_a_ping_to_the_server()
    {
        _APIResponse = await _playwrightContext.ApiRequestContext?.GetAsync("ping")!;
    }

    [Then("it returns the dateTime")]
    public async Task Then_it_returns_the_dateTime()
    {
        string content = await _APIResponse.TextAsync();
        string expectedDate = DateTime.Now.ToUniversalTime().ToString("yyyy-");

        _APIResponse.Status.Should().Be((int)HttpStatusCode.OK);
        content.Should().Contain(expectedDate);
    }
}
