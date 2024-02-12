// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using NUnit.Framework;
using System.Net;

namespace EdFi.DataManagementService.Api.Tests
{
    [TestFixture]
    public class EndpointsTests
    {
        [Test]
        public async Task TestPingEndpoint()
        {
            // Arrange
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();
            var expectedDate = DateTime.Now.ToString("yyyy-MM-dd");

            // Act
            var response = await client.GetAsync("/api/ping");
            var content = await response.Content.ReadAsStringAsync();

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            content.Should().Contain(expectedDate);
        }

        [Test]
        public async Task TestRateLimit()
        {
            // Arrange
            await using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
            });
            using var client = factory.CreateClient();

            // Act
            var response1 = await client.GetAsync("/api/ping");
            await client.GetAsync("/api/ping");
            await client.GetAsync("/api/ping");
            await client.GetAsync("/api/ping");
            var response5 = await client.GetAsync("/api/ping");

            // Assert
            response1.StatusCode.Should().Be(HttpStatusCode.OK);
            response5.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        }
    }
}
