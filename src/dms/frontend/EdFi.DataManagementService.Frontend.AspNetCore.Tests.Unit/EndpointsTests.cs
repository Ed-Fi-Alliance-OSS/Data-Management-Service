// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.TestBase;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit;

[TestFixture]
[NonParallelizable]
public class EndpointsTests : FrontendTestBase
{
    [Test]
    public async Task TestHealthEndpoint()
    {
        // Arrange
        var claimSetCacheService = A.Fake<IClaimSetCacheService>();
        A.CallTo(() => claimSetCacheService.GetClaimSets()).Returns([]);

        await using var factory = CreateTestFactory(services =>
        {
            services.AddTransient((x) => claimSetCacheService);
        });

        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/health");
        var content = await response.Content.ReadAsStringAsync();

        // Assert - if failing, log the error content
        if (response.StatusCode != HttpStatusCode.OK)
        {
            Console.WriteLine($"Response Status: {response.StatusCode}");
            Console.WriteLine($"Response Content: {content}");
        }

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        content.Should().Contain("\"Status\": \"Healthy\"");
    }
}
