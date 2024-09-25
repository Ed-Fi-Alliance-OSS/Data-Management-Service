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
public sealed class HealthStepDefinitions
{

    private readonly PlaywrightContext _playwrightContext;

    private IAPIResponse _APIResponse = null!;

    public HealthStepDefinitions(PlaywrightContext context)
    {
        _playwrightContext = context;
    }

    [Given("a request health is made to the server")]
    public async Task Given_a_request_health_to_the_server()
    {
        _APIResponse = await _playwrightContext.ApiRequestContext?.GetAsync("health")!;
    }

    [Then("it returns healthy checks")]
    public async Task Then_it_returns_healthy_checks()
    {
        string content = await _APIResponse.TextAsync();

        _APIResponse.Status.Should().Be((int)HttpStatusCode.OK);
        content.Should().Contain("\"Description\": \"Application is up and running\"");
        content.Should().Contain("\"Description\": \"Database connection is healthy.\"");
    }
}
