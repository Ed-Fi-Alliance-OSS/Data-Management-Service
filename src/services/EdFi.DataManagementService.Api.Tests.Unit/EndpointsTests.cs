// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using NUnit.Framework;

namespace EdFi.DataManagementService.Api.Tests.Unit;

[TestFixture]
public class EndpointsTests
{
    [Test]
    public async Task TestPingEndpoint()
    {
        // Arrange
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            // This environment has an extreme rate limit
            builder.UseEnvironment("Test");
        });
        using var client = factory.CreateClient();
        var expectedDate = DateTime.Now.ToString("yyyy-MM-dd");

        // Act
        var response = await client.GetAsync("/ping");
        var content = await response.Content.ReadAsStringAsync();

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        content.Should().Contain(expectedDate);
    }
}
