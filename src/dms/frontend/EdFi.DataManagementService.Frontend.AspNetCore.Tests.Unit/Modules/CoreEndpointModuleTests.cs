// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Security.Claims;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Frontend.AspNetCore.Modules;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.Modules;

[TestFixture]
public class CoreEndpointModuleTests
{
    [TestFixture]
    public class When_authorization_enabled_and_given_a_valid_client_token
    {
        private HttpResponseMessage? _response;

        [SetUp]
        public async Task SetUp()
        {
            // Arrange
            var securityMetadataService = A.Fake<ISecurityMetadataService>();
            A.CallTo(() => securityMetadataService.GetClaimSets()).Returns([]);
            var apiService = A.Fake<IApiService>();
            A.CallTo(() => apiService.Get(A<FrontendRequest>.Ignored)).Returns(new FakeFrontendResponse());
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureServices(
                    (collection) =>
                    {
                        collection.AddTransient((x) => apiService);
                        collection.AddTransient((x) => securityMetadataService);
                        collection
                            .AddAuthentication(AuthenticationConstants.AuthenticationSchema)
                            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                                AuthenticationConstants.AuthenticationSchema,
                                options => { }
                            );

                        collection.AddAuthorization(options =>
                            options.AddPolicy(
                                SecurityConstants.ServicePolicy,
                                policy => policy.RequireClaim(ClaimTypes.Role, AuthenticationConstants.Role)
                            )
                        );
                    }
                );
            });
            using var client = factory.CreateClient();

            // Act
            _response = await client.GetAsync("/data/ed-fi/students");
        }

        [TearDown]
        public void TearDownAttribute()
        {
            _response?.Dispose();
        }

        [Test]
        public void Then_it_responds_with_status_OK()
        {
            _response!.StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }

    [TestFixture]
    public class When_authorization_enabled_and_given_a_invalid_client_token
    {
        private HttpResponseMessage? _response;

        [SetUp]
        public async Task SetUp()
        {
            // Arrange
            var securityMetadataService = A.Fake<ISecurityMetadataService>();
            A.CallTo(() => securityMetadataService.GetClaimSets()).Returns([]);
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureServices(
                    (collection) =>
                    {
                        collection
                            .AddAuthentication(AuthenticationConstants.AuthenticationSchema)
                            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                                AuthenticationConstants.AuthenticationSchema,
                                options => { }
                            );

                        collection.AddAuthorization(options =>
                            options.AddPolicy(
                                SecurityConstants.ServicePolicy,
                                policy => policy.RequireClaim(ClaimTypes.Role, "invalid-role")
                            )
                        );
                        collection.AddTransient((x) => securityMetadataService);
                    }
                );
            });
            using var client = factory.CreateClient();

            // Act
            _response = await client.GetAsync("/data/ed-fi/students");
        }

        [TearDown]
        public void TearDownAttribute()
        {
            _response?.Dispose();
        }

        [Test]
        public void Then_it_responds_with_status_forbidden()
        {
            _response!.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
    }
}

public record FakeFrontendResponse : IFrontendResponse
{
    public int StatusCode => 200;

    public JsonNode? Body => null;

    public Dictionary<string, string> Headers => [];

    public string? LocationHeaderPath => null;

    public string? ContentType => "application/json";
}
