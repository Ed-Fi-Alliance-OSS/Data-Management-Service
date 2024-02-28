// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using FluentAssertions;
using Microsoft.Playwright;
using Reqnroll;

namespace EdFi.DataManagementService.Api.E2E.Tests.StepDefinitions
{
    [Binding]
    public sealed class PingStepDefinitions
    {

        private PlaywrightContext _PlaywrightContext = null!;
        private IAPIResponse _APIResponse = null!;

        [Given("a ping to the server")]
        public async Task Given_a_ping_to_the_server()
        {
            _PlaywrightContext = new PlaywrightContext();
            _APIResponse = await _PlaywrightContext.ApiRequestContext?.GetAsync("ping")!;
        }

        [Then("it returns the dateTime")]
        public async Task Then_it_returns_the_dateTime()
        {
            string content = await _APIResponse.TextAsync();
            string expectedDate = DateTime.Now.ToString("yyyy-MM-dd");

            _APIResponse.Status.Should().Be((int)HttpStatusCode.OK);
            content.Should().Contain(expectedDate);
        }
    }
}
