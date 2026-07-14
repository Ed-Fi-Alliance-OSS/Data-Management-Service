// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel;
using EdFi.DmsConfigurationService.DataModel.Model.Authorization;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Configuration;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure.Authorization;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit;

[TestFixture]
public class StatusCodePagesPipelineTests
{
    /// <summary>
    /// Framework-generated empty 405 and 415 responses receive the complete Ed-Fi Problem Details
    /// contract while their status codes and headers are preserved, and an existing structured 401 is
    /// not overwritten.
    /// </summary>
    [TestFixture]
    public class Given_A_Request_That_Produces_An_Empty_Framework_Status_Code
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

        private static async Task<JsonNode> ParseBody(HttpResponseMessage response) =>
            JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        private static void AssertContract(
            JsonNode body,
            string type,
            string title,
            int status,
            string detail
        )
        {
            body["detail"]!.GetValue<string>().Should().Be(detail);
            body["type"]!.GetValue<string>().Should().Be(type);
            body["title"]!.GetValue<string>().Should().Be(title);
            body["status"]!.GetValue<int>().Should().Be(status);
            body["correlationId"]!.GetValue<string>().Should().NotBeNullOrEmpty();
            body["validationErrors"]!.AsObject().Count.Should().Be(0);
            body["errors"]!.AsArray().Count.Should().Be(0);
        }

        [Test]
        public async Task It_returns_405_with_allow_header_and_complete_body()
        {
            // /connect/token is registered POST-only, so a GET is a method mismatch.
            var response = await _client.GetAsync("/connect/token");

            response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
            response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
            response.Content.Headers.Allow.Should().Contain("POST");

            var body = await ParseBody(response);
            AssertContract(
                body,
                "urn:ed-fi:api:method-not-allowed",
                "Method Not Allowed",
                405,
                "The request method is not allowed for this resource."
            );
        }

        [Test]
        public async Task It_returns_415_with_complete_body()
        {
            // /v3/vendors accepts application/json; a text/plain body is rejected by content negotiation
            // during routing (before authorization), so this is not masked by a 401.
            var content = new StringContent("not json", Encoding.UTF8, "text/plain");
            var response = await _client.PostAsync("/v3/vendors", content);

            response.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
            response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");

            var body = await ParseBody(response);
            AssertContract(
                body,
                "urn:ed-fi:api:unsupported-media-type",
                "Unsupported Media Type",
                415,
                "The request content type is not supported."
            );
        }

        [Test]
        public async Task It_does_not_overwrite_an_existing_structured_401()
        {
            // A secured endpoint without a token yields the authorization handler's structured 401 body,
            // which UseStatusCodePages must not replace.
            var response = await _client.GetAsync("/v3/vendors/");

            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            var body = await ParseBody(response);
            body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:security:authentication");
            body["status"]!.GetValue<int>().Should().Be(401);
        }
    }

    /// <summary>
    /// A successful response with no content is left untouched: UseStatusCodePages runs only for empty
    /// non-success responses, so a 204 stays bodyless.
    /// </summary>
    [TestFixture]
    public class Given_A_Successful_No_Content_Response
    {
        [Test]
        public async Task It_returns_204_with_no_body()
        {
            var vendorRepository = A.Fake<IVendorRepository>();
            A.CallTo(() => vendorRepository.DeleteVendor(A<long>._))
                .Returns(new VendorDeleteResult.Success());

            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureServices(
                    (ctx, collection) =>
                    {
                        collection.AddTestAuthentication();
                        var identitySettings = ctx
                            .Configuration.GetSection("IdentitySettings")
                            .Get<IdentitySettings>()!;
                        collection.AddAuthorization(options =>
                        {
                            options.AddPolicy(
                                SecurityConstants.ServicePolicy,
                                policy =>
                                    policy.RequireClaim(
                                        identitySettings.RoleClaimType,
                                        identitySettings.ConfigServiceRole
                                    )
                            );
                            AuthorizationScopePolicies.Add(options);
                        });
                        collection.AddTransient(_ => vendorRepository);
                    }
                );
            });
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-Test-Scope", AuthorizationScopes.AdminScope.Name);

            var response = await client.DeleteAsync("/v3/vendors/1");

            response.StatusCode.Should().Be(HttpStatusCode.NoContent);
            string raw = await response.Content.ReadAsStringAsync();
            raw.Should().BeEmpty();
        }
    }
}
