// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend.Repositories;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit;

[TestFixture]
internal class TenantResolutionExceptionPipelineTests
{
    /// <summary>
    /// When tenant resolution throws (e.g. a database connection failure in TenantRepository), the
    /// global exception handler must wrap the tenant middleware and return the standardized Ed-Fi
    /// internal-server-error response without exposing the exception text.
    /// </summary>
    [TestFixture]
    public class Given_The_Tenant_Repository_Throws_During_Resolution
    {
        private const string SensitiveMessage = "connection to host=db.internal;Password=sup3rsecret failed";

        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;

        [SetUp]
        public void Setup()
        {
            var tenantRepository = A.Fake<ITenantRepository>();
            A.CallTo(() => tenantRepository.GetTenantByName(A<string>._))
                .Throws(new InvalidOperationException(SensitiveMessage));

            _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureAppConfiguration(
                    (_, configurationBuilder) =>
                        configurationBuilder.AddInMemoryCollection(
                            new Dictionary<string, string?> { ["AppSettings:MultiTenancy"] = "true" }
                        )
                );
                builder.ConfigureServices((_, collection) => collection.AddTransient(_ => tenantRepository));
            });
            _client = _factory.CreateClient();
            _client.DefaultRequestHeaders.Add("Tenant", "some-tenant");
        }

        [TearDown]
        public void TearDown()
        {
            _client?.Dispose();
            _factory?.Dispose();
        }

        [Test]
        public async Task It_returns_a_safe_internal_server_error_without_exposing_the_exception()
        {
            var response = await _client.GetAsync("/v3/vendors");

            response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");

            string content = await response.Content.ReadAsStringAsync();
            content.Should().NotContain("sup3rsecret");
            content.Should().NotContain("host=db.internal");
            content.Should().NotContain("connection to host");
            content.Should().NotContain("InvalidOperationException");

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
