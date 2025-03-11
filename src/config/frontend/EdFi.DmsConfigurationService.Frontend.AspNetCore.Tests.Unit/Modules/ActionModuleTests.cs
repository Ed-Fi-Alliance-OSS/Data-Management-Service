// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Security.Claims;
using System.Text.Json;
using EdFi.DmsConfigurationService.DataModel;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Action = EdFi.DmsConfigurationService.DataModel.Model.Action.Action;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Modules;

public class RegisterActionEndpointTests
{
    [TestFixture]
    public class When_Making_Action_Request
    {
        private Action[] _mockActionResponse = null!;
        private HttpResponseMessage? _response;

        [SetUp]
        public void Setup()
        {
            _mockActionResponse =
            [
                new Action
                {
                    Id = 1,
                    Name = "Create",
                    Uri = "uri://ed-fi.org/api/actions/create",
                },
                new Action
                {
                    Id = 2,
                    Name = "Read",
                    Uri = "uri://ed-fi.org/api/actions/read",
                },
                new Action
                {
                    Id = 3,
                    Name = "Update",
                    Uri = "uri://ed-fi.org/api/actions/update",
                },
                new Action
                {
                    Id = 4,
                    Name = "Delete",
                    Uri = "uri://ed-fi.org/api/actions/delete",
                },
                new Action
                {
                    Id = 5,
                    Name = "ReadChanges",
                    Uri = "uri://ed-fi.org/api/actions/readChanges",
                },
            ];
        }

        [Test]
        public async Task Given_valid_token_and_role()
        {
            // Arrange
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
                                _ => { }
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
            _response = await client.GetAsync("/actions");
            string responseString = await _response.Content.ReadAsStringAsync();
            var content = JsonSerializer.Deserialize<List<Action>>(responseString);

            // Assert
            _response!.StatusCode.Should().Be(HttpStatusCode.OK);
            content.Should().BeEquivalentTo(_mockActionResponse);
        }

        [Test]
        public async Task Given_empty_auth_credentials()
        {
            // Arrange
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
            });

            using var client = factory.CreateClient();

            // Act
            _response = await client.GetAsync("/actions");

            // Assert
            _response!.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        [Test]
        public async Task Given_invalid_client_secret()
        {
            // Arrange
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
                                _ => { }
                            );

                        collection.AddAuthorization(options =>
                            options.AddPolicy(
                                SecurityConstants.ServicePolicy,
                                policy => policy.RequireClaim(ClaimTypes.Role, "invalid-role")
                            )
                        );
                    }
                );
            });
            using var client = factory.CreateClient();

            // Act
            _response = await client.GetAsync("/actions");

            // Assert
            _response!.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        [TearDown]
        public void TearDown()
        {
            _response!.Dispose();
        }
    }
};
