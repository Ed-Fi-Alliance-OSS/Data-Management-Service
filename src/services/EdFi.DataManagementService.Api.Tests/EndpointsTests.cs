// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;
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
            var expectedDate = DateTime.Now.ToShortDateString();

            // Act
            var response = await client.GetAsync("/api/ping");
            var content = await response.Content.ReadAsStringAsync();

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            content.Should().Contain(expectedDate);
        }
    }
}
