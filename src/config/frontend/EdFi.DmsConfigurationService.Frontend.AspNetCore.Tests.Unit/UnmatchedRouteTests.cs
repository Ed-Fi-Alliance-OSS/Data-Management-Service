// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit;

public class UnmatchedRouteTests
{
    /// <summary>
    /// Verifies that an unmatched CMS route returns the complete Ed-Fi not-found Problem Details
    /// contract (written by the status-code-pages handler) rather than an empty framework 404.
    /// </summary>
    [TestFixture]
    public class Given_An_Unmatched_Route
    {
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;

        [SetUp]
        public void Setup()
        {
            _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
                builder.UseEnvironment("Test")
            );
            _client = _factory.CreateClient();
        }

        [TearDown]
        public void TearDown()
        {
            _client?.Dispose();
            _factory?.Dispose();
        }

        [Test]
        public async Task It_returns_a_compliant_not_found_for_an_unmatched_route()
        {
            var response = await _client.GetAsync("/this-route-does-not-exist");

            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
            response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");

            string content = await response.Content.ReadAsStringAsync();
            var actual = JsonNode.Parse(content);
            actual!["correlationId"]!.GetValue<string>().Should().NotBeNullOrEmpty();

            var expected = JsonNode.Parse(
                """
                {
                  "detail": "The requested resource was not found.",
                  "type": "urn:ed-fi:api:not-found",
                  "title": "Not Found",
                  "status": 404,
                  "correlationId": "{correlationId}",
                  "validationErrors": {},
                  "errors": []
                }
                """.Replace("{correlationId}", actual!["correlationId"]!.GetValue<string>())
            );
            JsonNode.DeepEquals(actual, expected).Should().Be(true);
        }

        [Test]
        public async Task It_returns_a_compliant_not_found_for_an_unmatched_post_route()
        {
            var response = await _client.PostAsync(
                "/management/not-a-real-endpoint",
                new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
            );

            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
            response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");

            string content = await response.Content.ReadAsStringAsync();
            var actual = JsonNode.Parse(content);
            actual!["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:not-found");
            actual["status"]!.GetValue<int>().Should().Be(404);
            actual["correlationId"]!.GetValue<string>().Should().NotBeNullOrEmpty();
        }
    }
}
