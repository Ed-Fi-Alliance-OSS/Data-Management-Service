// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit;

[TestFixture]
public class InvalidConfigurationPipelineTests
{
    /// <summary>
    /// Proves, through the real HTTP pipeline, that invalid configuration produces the structured Ed-Fi
    /// internal-server-error response. This fails if any middleware that resolves validated options (e.g.
    /// TenantResolutionMiddleware reading AppSettings.Value) runs before the reporting middleware, because
    /// the resulting OptionsValidationException would escape upstream and the body would not be the
    /// Ed-Fi Problem Details contract.
    /// </summary>
    [TestFixture]
    public class Given_Invalid_Configuration
    {
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;

        [SetUp]
        public void Setup()
        {
            _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                // Make AppSettings fail IValidateOptions validation without disturbing startup DI.
                builder.ConfigureAppConfiguration(
                    (_, configurationBuilder) =>
                        configurationBuilder.AddInMemoryCollection(
                            new Dictionary<string, string?>
                            {
                                ["AppSettings:SpecificationVersion"] = "v99-invalid",
                            }
                        )
                );
            });
            _client = _factory.CreateClient();
        }

        [TearDown]
        public void TearDown()
        {
            _client?.Dispose();
            _factory?.Dispose();
        }

        [Test]
        public async Task It_returns_the_structured_internal_server_error_for_invalid_configuration()
        {
            var response = await _client.GetAsync("/health");

            response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");

            string content = await response.Content.ReadAsStringAsync();

            // No configuration detail or exception text may leak into the public body.
            content.Should().NotContain("SpecificationVersion");
            content.Should().NotContain("v99-invalid");
            content.Should().NotContain("OptionsValidationException");

            var actual = JsonNode.Parse(content);
            actual!["correlationId"]!.GetValue<string>().Should().NotBeNullOrEmpty();
            var expected = JsonNode.Parse(
                """
                {
                  "detail": "",
                  "type": "urn:ed-fi:api:internal-server-error",
                  "title": "Internal Server Error",
                  "status": 500,
                  "correlationId": "{correlationId}",
                  "validationErrors": {},
                  "errors": []
                }
                """.Replace("{correlationId}", actual!["correlationId"]!.GetValue<string>())
            );
            JsonNode.DeepEquals(actual, expected).Should().BeTrue();
        }
    }
}
