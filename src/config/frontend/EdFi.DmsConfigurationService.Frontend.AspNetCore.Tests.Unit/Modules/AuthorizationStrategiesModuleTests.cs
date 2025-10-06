// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json;
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
using AuthorizationStrategy = EdFi.DmsConfigurationService.DataModel.Model.ClaimSets.AuthorizationStrategy;

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
            _mockAuthStrategiesResponse =
            [
                new()
                {
                    Id = 1,
                    AuthorizationStrategyName = "AuthStrategy1",
                    DisplayName = "AuthStrategy1",
                },
                new()
                {
                    Id = 2,
                    AuthorizationStrategyName = "AuthStrategy2",
                    DisplayName = "AuthStrategy2",
                },
                new()
                {
                    Id = 3,
                    AuthorizationStrategyName = "AuthStrategy3",
                    DisplayName = "AuthStrategy3",
                },
                new()
                {
                    Id = 4,
                    AuthorizationStrategyName = "AuthStrategy4",
                    DisplayName = "AuthStrategy4",
                },
            ];
            A.CallTo(() => _claimSetRepository.GetAuthorizationStrategies())
                .Returns(
                    Task.FromResult<AuthorizationStrategyGetResult>(
                        new AuthorizationStrategyGetResult.Success(_mockAuthStrategiesResponse)
                    )
                );
        }

        [Test]
        public async Task Given_valid_token_and_role()
        {
            // Arrange
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureServices(
                    (ctx, collection) =>
                    {
                        // Use the new test authentication extension that mimics production setup
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
                        collection.AddTransient((_) => _claimSetRepository);
                    }
                );
            });
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-Test-Scope", AuthorizationScopes.AdminScope.Name);

            // Act
            _response = await client.GetAsync("/authorizationStrategies");
            var responseString = await _response.Content.ReadAsStringAsync();
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

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
                    (ctx, collection) =>
                    {
                        // Use the new test authentication extension that mimics production setup
                        collection.AddTestAuthentication();

                        var identitySettings = ctx
                            .Configuration.GetSection("IdentitySettings")
                            .Get<IdentitySettings>()!;
                        collection.AddAuthorization(options =>
                        {
                            options.AddPolicy(
                                SecurityConstants.ServicePolicy,
                                policy => policy.RequireClaim(identitySettings.RoleClaimType, "invalid-role")
                            );
                            AuthorizationScopePolicies.Add(options);
                        });
                    }
                );
            });
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-Test-Scope", AuthorizationScopes.AdminScope.Name);

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
