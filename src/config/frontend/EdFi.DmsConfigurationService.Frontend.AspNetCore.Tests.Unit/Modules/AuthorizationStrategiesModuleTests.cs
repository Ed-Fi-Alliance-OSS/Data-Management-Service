// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json;
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
            _response = await client.GetAsync("/v3/authorizationStrategies");
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
            _response = await client.GetAsync("/v3/authorizationStrategies");

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
            _response = await client.GetAsync("/v3/authorizationStrategies");

            // Assert
            _response!.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        [TearDown]
        public void TearDown()
        {
            _response!.Dispose();
        }
    }

    [TestFixture]
    public class When_Making_AuthorizationStrategies_Request_With_Query_Parameters
    {
        private readonly IClaimSetRepository _claimSetRepository = A.Fake<IClaimSetRepository>();
        private WebApplicationFactory<Program>? _factory;

        /// <summary>
        /// Deliberately not in id, name or displayName order, so a test that asserts an order is
        /// asserting the endpoint's sorting rather than the order the repository happened to return.
        /// DisplayName is null on one item because the column is nullable.
        /// </summary>
        private static AuthorizationStrategy[] UnsortedStrategies() =>
            [
                new()
                {
                    Id = 3,
                    AuthorizationStrategyName = "Beta",
                    DisplayName = "Zeta",
                },
                new()
                {
                    Id = 1,
                    AuthorizationStrategyName = "Gamma",
                    DisplayName = null,
                },
                new()
                {
                    Id = 2,
                    AuthorizationStrategyName = "alpha",
                    DisplayName = "Eta",
                },
            ];

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
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
                        collection.AddTransient((_) => _claimSetRepository);
                    }
                );
            });
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            _factory?.Dispose();
        }

        [SetUp]
        public void Setup()
        {
            // The fake is shared across the fixture's tests, so recorded calls are cleared to keep
            // the "repository was never called" assertion meaningful.
            Fake.ClearRecordedCalls(_claimSetRepository);

            A.CallTo(() => _claimSetRepository.GetAuthorizationStrategies())
                .Returns(
                    Task.FromResult<AuthorizationStrategyGetResult>(
                        new AuthorizationStrategyGetResult.Success(UnsortedStrategies())
                    )
                );
        }

        private HttpClient SetUpClient()
        {
            var client = _factory!.CreateClient();
            client.DefaultRequestHeaders.Add("X-Test-Scope", AuthorizationScopes.AdminScope.Name);
            return client;
        }

        private async Task<List<AuthorizationStrategy>> GetStrategiesAsync(string queryString)
        {
            using var client = SetUpClient();
            using var response = await client.GetAsync($"/v3/authorizationStrategies{queryString}");

            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            return JsonSerializer.Deserialize<List<AuthorizationStrategy>>(
                await response.Content.ReadAsStringAsync(),
                options
            )!;
        }

        private async Task<JsonObject> GetBadRequestBodyAsync(string queryString)
        {
            using var client = SetUpClient();
            using var response = await client.GetAsync($"/v3/authorizationStrategies{queryString}");

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

            return JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        }

        [Test]
        public async Task Given_no_query_parameters_It_returns_the_repository_list_unchanged()
        {
            var strategies = await GetStrategiesAsync(string.Empty);

            strategies.Select(strategy => strategy.Id).Should().Equal(3, 1, 2);
        }

        [Test]
        public async Task Given_an_offset_It_sorts_by_id_then_skips()
        {
            var strategies = await GetStrategiesAsync("?offset=1");

            strategies.Select(strategy => strategy.Id).Should().Equal(2, 3);
        }

        [Test]
        public async Task Given_a_limit_It_returns_at_most_that_many_items()
        {
            var strategies = await GetStrategiesAsync("?limit=2");

            strategies.Select(strategy => strategy.Id).Should().Equal(1, 2);
        }

        [Test]
        public async Task Given_an_offset_and_a_limit_It_returns_that_page()
        {
            var strategies = await GetStrategiesAsync("?offset=1&limit=1");

            strategies.Select(strategy => strategy.Id).Should().Equal(2);
        }

        [Test]
        public async Task Given_an_offset_past_the_end_It_returns_an_empty_list()
        {
            var strategies = await GetStrategiesAsync("?offset=10");

            strategies.Should().BeEmpty();
        }

        [Test]
        public async Task Given_orderBy_id_It_sorts_in_either_direction()
        {
            var ascending = await GetStrategiesAsync("?orderBy=id&direction=asc");
            var descending = await GetStrategiesAsync("?orderBy=id&direction=desc");

            ascending.Select(strategy => strategy.Id).Should().Equal(1, 2, 3);
            descending.Select(strategy => strategy.Id).Should().Equal(3, 2, 1);
        }

        [Test]
        public async Task Given_orderBy_name_It_sorts_case_insensitively_in_either_direction()
        {
            var ascending = await GetStrategiesAsync("?orderBy=name&direction=asc");
            var descending = await GetStrategiesAsync("?orderBy=name&direction=desc");

            ascending
                .Select(strategy => strategy.AuthorizationStrategyName)
                .Should()
                .Equal("alpha", "Beta", "Gamma");
            descending
                .Select(strategy => strategy.AuthorizationStrategyName)
                .Should()
                .Equal("Gamma", "Beta", "alpha");
        }

        [Test]
        public async Task Given_orderBy_displayName_It_sorts_nulls_first_ascending()
        {
            var ascending = await GetStrategiesAsync("?orderBy=displayName&direction=asc");
            var descending = await GetStrategiesAsync("?orderBy=displayName&direction=desc");

            ascending.Select(strategy => strategy.DisplayName).Should().Equal(null, "Eta", "Zeta");
            descending.Select(strategy => strategy.DisplayName).Should().Equal("Zeta", "Eta", null);
        }

        [Test]
        public async Task Given_a_direction_without_an_orderBy_It_sorts_by_id_in_that_direction()
        {
            var strategies = await GetStrategiesAsync("?direction=descending");

            strategies.Select(strategy => strategy.Id).Should().Equal(3, 2, 1);
        }

        [Test]
        public async Task Given_spec_cased_values_It_accepts_them()
        {
            var strategies = await GetStrategiesAsync("?orderBy=DISPLAYNAME&direction=Ascending");

            strategies.Select(strategy => strategy.DisplayName).Should().Equal(null, "Eta", "Zeta");
        }

        [Test]
        public async Task Given_an_unsupported_orderBy_It_returns_the_parameter_validation_contract()
        {
            var body = await GetBadRequestBodyAsync("?orderBy=uri");

            body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:bad-request:parameter");
            body["title"]!.GetValue<string>().Should().Be("Parameter Validation Failed");
            body["status"]!.GetValue<int>().Should().Be(400);

            // The allowed-values text is built from a set, so membership is asserted rather than the
            // rendered order, and the client-supplied value must never be echoed back.
            var errors = body["errors"]!.AsArray().Select(error => error!.GetValue<string>()).ToList();
            errors.Should().ContainSingle();
            errors[0].Should().StartWith("'orderBy' is not a valid field");
            errors[0].Should().Contain("id").And.Contain("name").And.Contain("displayName");
            errors[0].Should().NotContain("uri");
        }

        [Test]
        public async Task Given_an_invalid_orderBy_It_does_not_query_the_repository()
        {
            using var client = SetUpClient();
            using var response = await client.GetAsync("/v3/authorizationStrategies?orderBy=uri");

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            A.CallTo(() => _claimSetRepository.GetAuthorizationStrategies()).MustNotHaveHappened();
        }

        [Test]
        public async Task Given_an_invalid_direction_It_returns_the_parameter_validation_contract()
        {
            var body = await GetBadRequestBodyAsync("?direction=sideways");

            body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:bad-request:parameter");
            body["errors"]!
                .AsArray()
                .Select(error => error!.GetValue<string>())
                .Should()
                .Equal("The direction query parameter must be one of: asc, ascending, desc, descending.");
        }

        [TestCase("?offset=-1")]
        [TestCase("?limit=0")]
        [TestCase("?offset=abc")]
        [TestCase("?limit=abc")]
        public async Task Given_an_out_of_range_or_unparsable_value_It_returns_bad_request(string queryString)
        {
            using var client = SetUpClient();
            using var response = await client.GetAsync($"/v3/authorizationStrategies{queryString}");

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Test]
        public async Task Given_a_repository_failure_with_paging_It_still_returns_internal_server_error()
        {
            A.CallTo(() => _claimSetRepository.GetAuthorizationStrategies())
                .Returns(
                    Task.FromResult<AuthorizationStrategyGetResult>(
                        new AuthorizationStrategyGetResult.FailureUnknown("failure")
                    )
                );

            using var client = SetUpClient();
            using var response = await client.GetAsync("/v3/authorizationStrategies?limit=1");

            response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        }
    }
};
