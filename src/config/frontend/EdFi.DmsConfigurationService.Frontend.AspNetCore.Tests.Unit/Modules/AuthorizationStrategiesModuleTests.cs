// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Security.Claims;
using System.Text.Json;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel;
using EdFi.DmsConfigurationService.DataModel.Model.ClaimSets;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Modules;

public class AuthorizationStrategiesModuleTests
{
    [TestFixture]
    public class When_Making_AuthorizationStrategies_Request
    {
        private AuthorizationStrategy[] _mockAuthStrategiesResponse = null!;
        private HttpResponseMessage? _response;
        private readonly IClaimSetRepository _claimSetRepository = A.Fake<IClaimSetRepository>();

        [SetUp]
        public void Setup()
        {
            _mockAuthStrategiesResponse = [
                new()
                {
                    AuthStrategyId = 1,
                    AuthStrategyName = "AuthStrategy1",
                    DisplayName = "AuthStrategy1"
                },
                new()
                {
                    AuthStrategyId = 2,
                    AuthStrategyName = "AuthStrategy2",
                    DisplayName = "AuthStrategy2"
                },
                new()
                {
                    AuthStrategyId = 3,
                    AuthStrategyName = "AuthStrategy3",
                    DisplayName = "AuthStrategy3"
                },
                new()
                {
                    AuthStrategyId = 4,
                    AuthStrategyName = "AuthStrategy4",
                    DisplayName = "AuthStrategy4"
                },
            ];
            A.CallTo(() => _claimSetRepository.GetAuthorizationStrategies())
               .Returns(_mockAuthStrategiesResponse);
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
                        ));
                        collection.AddTransient((_) => _claimSetRepository);
                    }
                );
            });
            using var client = factory.CreateClient();

            // Act
            _response = await client.GetAsync("/authorizationStrategies");
            var responseString = await _response.Content.ReadAsStringAsync();
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };

            var content = JsonSerializer.Deserialize<List<AuthorizationStrategy>>(responseString, options);

            // Assert
            _response!.StatusCode.Should().Be(HttpStatusCode.OK);
            content.Should().BeEquivalentTo(_mockAuthStrategiesResponse);
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
            _response = await client.GetAsync("/authorizationStrategies");

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
            _response = await client.GetAsync("/authorizationStrategies");

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
