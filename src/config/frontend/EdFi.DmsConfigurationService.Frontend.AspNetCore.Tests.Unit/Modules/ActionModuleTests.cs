// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.DataModel;
using EdFi.DmsConfigurationService.DataModel.Model.Authorization;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Configuration;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure.Authorization;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
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
                    }
                );
            });
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-Test-Scope", AuthorizationScopes.AdminScope.Name);

            // Act
            _response = await client.GetAsync("/v3/actions");
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
            _response = await client.GetAsync("/v3/actions");

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
                            options.AddPolicy(
                                SecurityConstants.ServicePolicy,
                                policy => policy.RequireClaim(identitySettings.RoleClaimType, "invalid-role")
                            )
                        );
                    }
                );
            });
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-Test-Scope", AuthorizationScopes.AdminScope.Name);

            // Act
            _response = await client.GetAsync("/v3/actions");

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
    public class When_Making_Action_Request_With_Query_Parameters
    {
        private WebApplicationFactory<Program>? _factory;

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
                    }
                );
            });
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            _factory?.Dispose();
        }

        private HttpClient SetUpClient()
        {
            var client = _factory!.CreateClient();
            client.DefaultRequestHeaders.Add("X-Test-Scope", AuthorizationScopes.AdminScope.Name);
            return client;
        }

        private async Task<List<Action>> GetActionsAsync(string queryString)
        {
            using var client = SetUpClient();
            using var response = await client.GetAsync($"/v3/actions{queryString}");

            response.StatusCode.Should().Be(HttpStatusCode.OK);

            return JsonSerializer.Deserialize<List<Action>>(await response.Content.ReadAsStringAsync())!;
        }

        private async Task<JsonObject> GetBadRequestBodyAsync(string queryString)
        {
            using var client = SetUpClient();
            using var response = await client.GetAsync($"/v3/actions{queryString}");

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

            return JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        }

        [Test]
        public async Task Given_no_query_parameters_It_returns_the_full_list_unchanged()
        {
            var actions = await GetActionsAsync(string.Empty);

            actions.Select(action => action.Id).Should().Equal(1, 2, 3, 4, 5);
            actions
                .Select(action => action.Name)
                .Should()
                .Equal("Create", "Read", "Update", "Delete", "ReadChanges");
        }

        [Test]
        public async Task Given_an_offset_It_skips_that_many_items()
        {
            var actions = await GetActionsAsync("?offset=2");

            actions.Select(action => action.Id).Should().Equal(3, 4, 5);
        }

        [Test]
        public async Task Given_a_limit_It_returns_at_most_that_many_items()
        {
            var actions = await GetActionsAsync("?limit=2");

            actions.Select(action => action.Id).Should().Equal(1, 2);
        }

        [Test]
        public async Task Given_an_offset_and_a_limit_It_returns_that_page()
        {
            var actions = await GetActionsAsync("?offset=1&limit=2");

            actions.Select(action => action.Id).Should().Equal(2, 3);
        }

        [Test]
        public async Task Given_an_offset_past_the_end_It_returns_an_empty_list()
        {
            var actions = await GetActionsAsync("?offset=10");

            actions.Should().BeEmpty();
        }

        [Test]
        public async Task Given_orderBy_id_ascending_It_sorts_by_id()
        {
            var actions = await GetActionsAsync("?orderBy=id&direction=asc");

            actions.Select(action => action.Id).Should().Equal(1, 2, 3, 4, 5);
        }

        [Test]
        public async Task Given_orderBy_id_descending_It_reverses_the_id_order()
        {
            var actions = await GetActionsAsync("?orderBy=id&direction=desc");

            actions.Select(action => action.Id).Should().Equal(5, 4, 3, 2, 1);
        }

        [Test]
        public async Task Given_orderBy_name_ascending_It_sorts_by_name()
        {
            var actions = await GetActionsAsync("?orderBy=name&direction=asc");

            actions
                .Select(action => action.Name)
                .Should()
                .Equal("Create", "Delete", "Read", "ReadChanges", "Update");
        }

        [Test]
        public async Task Given_orderBy_name_descending_It_reverses_the_name_order()
        {
            var actions = await GetActionsAsync("?orderBy=name&direction=descending");

            actions
                .Select(action => action.Name)
                .Should()
                .Equal("Update", "ReadChanges", "Read", "Delete", "Create");
        }

        [Test]
        public async Task Given_a_direction_without_an_orderBy_It_sorts_by_id_in_that_direction()
        {
            var actions = await GetActionsAsync("?direction=desc");

            actions.Select(action => action.Id).Should().Equal(5, 4, 3, 2, 1);
        }

        [Test]
        public async Task Given_sorting_and_paging_It_pages_the_sorted_set()
        {
            var actions = await GetActionsAsync("?orderBy=name&direction=desc&offset=1&limit=2");

            actions.Select(action => action.Name).Should().Equal("ReadChanges", "Read");
        }

        [Test]
        public async Task Given_an_id_filter_It_returns_only_that_action()
        {
            var actions = await GetActionsAsync("?id=3");

            actions.Select(action => action.Name).Should().Equal("Update");
        }

        [Test]
        public async Task Given_a_name_filter_It_matches_exactly_and_case_insensitively()
        {
            var actions = await GetActionsAsync("?name=read");

            actions.Select(action => action.Name).Should().Equal("Read");
        }

        [Test]
        public async Task Given_a_name_filter_with_no_match_It_returns_an_empty_list()
        {
            var actions = await GetActionsAsync("?name=nomatch");

            actions.Should().BeEmpty();
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
            errors[0].Should().Contain("id").And.Contain("name");
            errors[0].Should().NotContain("uri");
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
        [TestCase("?id=abc")]
        [TestCase("?offset=abc")]
        [TestCase("?limit=abc")]
        public async Task Given_an_out_of_range_or_unparsable_value_It_returns_bad_request(string queryString)
        {
            using var client = SetUpClient();
            using var response = await client.GetAsync($"/v3/actions{queryString}");

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
    }
};
