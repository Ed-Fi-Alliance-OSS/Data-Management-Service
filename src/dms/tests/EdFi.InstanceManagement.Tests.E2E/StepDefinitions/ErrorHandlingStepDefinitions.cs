// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.InstanceManagement.Tests.E2E.Management;
using FluentAssertions;
using Reqnroll;

namespace EdFi.InstanceManagement.Tests.E2E.StepDefinitions;

[Binding]
public class ErrorHandlingStepDefinitions(InstanceManagementContext context)
{
    [BeforeScenario(Order = 100)]
    public void EnsureDmsClientIsAvailable()
    {
        if (context.DmsToken != null && context.DmsClient == null)
        {
            context.DmsClient = new DmsApiClient(TestConfiguration.DmsApiUrl, context.DmsToken);
        }
    }

    [When("a GET request is made without route qualifiers to resource {string}")]
    public async Task WhenAGetRequestIsMadeWithoutRouteQualifiersToResource(string resource)
    {
        context.DmsClient ??= new DmsApiClient(TestConfiguration.DmsApiUrl, context.DmsToken ?? "");

        context.LastResponse = await context.DmsClient.GetResourceWithoutQualifiersAsync(resource);
    }

    [Then("it should respond with {int} or {int}")]
    public void ThenItShouldRespondWithOr(int statusCode1, int statusCode2)
    {
        context.LastResponse.Should().NotBeNull();
        var actualStatusCode = (int)context.LastResponse!.StatusCode;

        actualStatusCode
            .Should()
            .Match(
                code => code == statusCode1 || code == statusCode2,
                $"Expected {statusCode1} or {statusCode2} but got {actualStatusCode}"
            );
    }
}
