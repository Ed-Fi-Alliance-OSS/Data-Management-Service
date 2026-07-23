// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.Authorization;
using EdFi.DmsConfigurationService.DataModel.Model.ResourceClaims;
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

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Modules;

public class ResourceClaimModuleTests
{
    private readonly IResourceClaimRepository _repository = A.Fake<IResourceClaimRepository>();

    private HttpClient SetUpClient()
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
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
                    collection.AddTransient(_ => _repository);
                }
            );
        });
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Scope", AuthorizationScopes.AdminScope.Name);
        return client;
    }

    [TestFixture]
    public class Given_resource_claims_exist : ResourceClaimModuleTests
    {
        private static readonly List<ResourceClaimResponse> FakeResourceClaims =
        [
            new ResourceClaimResponse
            {
                Id = 1,
                Name = "types",
                ParentId = 0,
                ParentName = null,
                Children = [],
            },
        ];

        private static readonly List<ResourceClaimActionResponse> FakeActions =
        [
            new ResourceClaimActionResponse
            {
                ResourceClaimId = 1,
                ResourceName = "types",
                ClaimName = "http://ed-fi.org/identity/claims/domains/edFiTypes",
                Actions = [new ActionNameResponse { Name = "Read" }],
            },
        ];

        private static readonly List<ResourceClaimActionAuthStrategyResponse> FakeAuthStrategies =
        [
            new ResourceClaimActionAuthStrategyResponse
            {
                ResourceClaimId = 1,
                ResourceName = "types",
                ClaimName = "http://ed-fi.org/identity/claims/domains/edFiTypes",
                AuthorizationStrategiesForActions =
                [
                    new ActionWithAuthorizationStrategyResponse
                    {
                        ActionId = 2,
                        ActionName = "Read",
                        AuthorizationStrategies =
                        [
                            new AuthorizationStrategyForActionResponse
                            {
                                AuthStrategyId = 1,
                                AuthStrategyName = "NoFurtherAuthorizationRequired",
                            },
                        ],
                    },
                ],
            },
        ];

        [SetUp]
        public void SetUp()
        {
            A.CallTo(() => _repository.GetResourceClaims(A<ResourceClaimQuery>.Ignored))
                .Returns(new ResourceClaimListResult.Success(FakeResourceClaims));

            A.CallTo(() => _repository.GetResourceClaim(1L))
                .Returns(new ResourceClaimGetResult.Success(FakeResourceClaims[0]));

            A.CallTo(() => _repository.GetResourceClaim(999L))
                .Returns(new ResourceClaimGetResult.FailureNotFound());

            A.CallTo(() => _repository.GetResourceClaimActions(A<ResourceClaimActionQuery>.Ignored))
                .Returns(new ResourceClaimActionListResult.Success(FakeActions));

            A.CallTo(() =>
                    _repository.GetResourceClaimActionAuthStrategies(
                        A<ResourceClaimActionAuthStrategyQuery>.Ignored
                    )
                )
                .Returns(new ResourceClaimActionAuthStrategyListResult.Success(FakeAuthStrategies));
        }

        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        [Test]
        public async Task It_returns_200_with_resource_claims()
        {
            var client = SetUpClient();
            var response = await client.GetAsync("/v3/resourceClaims");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var content = await response.Content.ReadAsStringAsync();
            var claims = JsonSerializer.Deserialize<List<ResourceClaimResponse>>(content, JsonOptions)!;
            claims.Should().ContainSingle(c => c.Name == "types" && c.ParentId == 0);
        }

        [Test]
        public async Task It_returns_200_when_found()
        {
            var client = SetUpClient();
            var response = await client.GetAsync("/v3/resourceClaims/1");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var content = await response.Content.ReadAsStringAsync();
            var claim = JsonSerializer.Deserialize<ResourceClaimResponse>(content, JsonOptions)!;
            claim.Name.Should().Be("types");
            claim.ParentId.Should().Be(0);
        }

        [Test]
        public async Task It_returns_404_when_not_found()
        {
            var client = SetUpClient();
            var response = await client.GetAsync("/v3/resourceClaims/999");

            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        [Test]
        public async Task It_ignores_query_parameters_on_getById()
        {
            var client = SetUpClient();
            var response = await client.GetAsync(
                "/v3/resourceClaims/1?orderBy=invalidField&direction=sideways&limit=999"
            );

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var content = await response.Content.ReadAsStringAsync();
            var claim = JsonSerializer.Deserialize<ResourceClaimResponse>(content, JsonOptions)!;
            claim.Name.Should().Be("types");
        }

        [Test]
        public async Task It_returns_200_with_actions()
        {
            var client = SetUpClient();
            var response = await client.GetAsync("/v3/resourceClaimActions");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var content = await response.Content.ReadAsStringAsync();
            var actions = JsonSerializer.Deserialize<List<ResourceClaimActionResponse>>(
                content,
                JsonOptions
            )!;
            actions.Should().ContainSingle(a => a.Actions.Any(x => x.Name == "Read"));
        }

        [Test]
        public async Task It_returns_200_with_auth_strategies()
        {
            var client = SetUpClient();
            var response = await client.GetAsync("/v3/resourceClaimActionAuthStrategies");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var content = await response.Content.ReadAsStringAsync();
            var strategies = JsonSerializer.Deserialize<List<ResourceClaimActionAuthStrategyResponse>>(
                content,
                JsonOptions
            )!;
            strategies
                .Should()
                .ContainSingle(s =>
                    s.AuthorizationStrategiesForActions.Any(a =>
                        a.AuthorizationStrategies.Any(x =>
                            x.AuthStrategyName == "NoFurtherAuthorizationRequired"
                        )
                    )
                );
        }

        [Test]
        public async Task It_returns_400_for_invalid_orderBy()
        {
            var client = SetUpClient();
            var response = await client.GetAsync("/v3/resourceClaims?orderBy=invalidField");

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Test]
        public async Task It_returns_400_for_invalid_direction()
        {
            var client = SetUpClient();
            var response = await client.GetAsync("/v3/resourceClaims?direction=sideways");

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Test]
        public async Task It_returns_400_for_negative_offset()
        {
            var client = SetUpClient();
            var response = await client.GetAsync("/v3/resourceClaims?offset=-1");

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Test]
        public async Task It_returns_400_for_invalid_orderBy_on_actions_endpoint()
        {
            var client = SetUpClient();
            var response = await client.GetAsync("/v3/resourceClaimActions?orderBy=invalidField");

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Test]
        public async Task It_returns_400_for_invalid_orderBy_on_auth_strategies_endpoint()
        {
            var client = SetUpClient();
            var response = await client.GetAsync(
                "/v3/resourceClaimActionAuthStrategies?orderBy=invalidField"
            );

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Test]
        public async Task It_returns_parameter_validation_failure_for_invalid_limit_on_resource_claims_endpoint()
        {
            var client = SetUpClient();
            var response = await client.GetAsync("/v3/resourceClaims?limit=0");

            await response.ShouldBeProblemDetailAsync(
                HttpStatusCode.BadRequest,
                "urn:ed-fi:api:bad-request:parameter",
                "Parameter Validation Failed",
                "One or more query parameters were invalid. See 'errors' for details.",
                errors: ["'limit' must be greater than 0."]
            );
        }

        [Test]
        public async Task It_returns_parameter_validation_failure_for_invalid_limit_on_actions_endpoint()
        {
            var client = SetUpClient();
            var response = await client.GetAsync("/v3/resourceClaimActions?limit=0");

            await response.ShouldBeProblemDetailAsync(
                HttpStatusCode.BadRequest,
                "urn:ed-fi:api:bad-request:parameter",
                "Parameter Validation Failed",
                "One or more query parameters were invalid. See 'errors' for details.",
                errors: ["'limit' must be greater than 0."]
            );
        }

        [Test]
        public async Task It_returns_parameter_validation_failure_for_invalid_limit_on_auth_strategies_endpoint()
        {
            var client = SetUpClient();
            var response = await client.GetAsync("/v3/resourceClaimActionAuthStrategies?limit=0");

            await response.ShouldBeProblemDetailAsync(
                HttpStatusCode.BadRequest,
                "urn:ed-fi:api:bad-request:parameter",
                "Parameter Validation Failed",
                "One or more query parameters were invalid. See 'errors' for details.",
                errors: ["'limit' must be greater than 0."]
            );
        }

        [Test]
        public async Task It_returns_parameter_validation_failure_for_a_non_numeric_offset_on_resource_claims_endpoint()
        {
            var client = SetUpClient();
            var response = await client.GetAsync("/v3/resourceClaims?offset=abc");

            await response.ShouldBeProblemDetailAsync(
                HttpStatusCode.BadRequest,
                "urn:ed-fi:api:bad-request:parameter",
                "Parameter Validation Failed",
                "One or more query parameters were invalid. See 'errors' for details.",
                errors: ["'offset' must be an integer."]
            );
            (await response.Content.ReadAsStringAsync()).Should().NotContain("abc");
        }

        [Test]
        public async Task It_returns_parameter_validation_failure_for_a_non_numeric_offset_on_actions_endpoint()
        {
            var client = SetUpClient();
            var response = await client.GetAsync("/v3/resourceClaimActions?offset=abc");

            await response.ShouldBeProblemDetailAsync(
                HttpStatusCode.BadRequest,
                "urn:ed-fi:api:bad-request:parameter",
                "Parameter Validation Failed",
                "One or more query parameters were invalid. See 'errors' for details.",
                errors: ["'offset' must be an integer."]
            );
            (await response.Content.ReadAsStringAsync()).Should().NotContain("abc");
        }

        [Test]
        public async Task It_returns_parameter_validation_failure_for_a_non_numeric_offset_on_auth_strategies_endpoint()
        {
            var client = SetUpClient();
            var response = await client.GetAsync("/v3/resourceClaimActionAuthStrategies?offset=abc");

            await response.ShouldBeProblemDetailAsync(
                HttpStatusCode.BadRequest,
                "urn:ed-fi:api:bad-request:parameter",
                "Parameter Validation Failed",
                "One or more query parameters were invalid. See 'errors' for details.",
                errors: ["'offset' must be an integer."]
            );
            (await response.Content.ReadAsStringAsync()).Should().NotContain("abc");
        }
    }

    [TestFixture]
    public class Given_hierarchy_not_found : ResourceClaimModuleTests
    {
        [SetUp]
        public void SetUp()
        {
            A.CallTo(() => _repository.GetResourceClaims(A<ResourceClaimQuery>.Ignored))
                .Returns(new ResourceClaimListResult.FailureHierarchyNotFound());

            A.CallTo(() => _repository.GetResourceClaim(A<long>.Ignored))
                .Returns(new ResourceClaimGetResult.FailureHierarchyNotFound());

            A.CallTo(() => _repository.GetResourceClaimActions(A<ResourceClaimActionQuery>.Ignored))
                .Returns(new ResourceClaimActionListResult.FailureHierarchyNotFound());

            A.CallTo(() =>
                    _repository.GetResourceClaimActionAuthStrategies(
                        A<ResourceClaimActionAuthStrategyQuery>.Ignored
                    )
                )
                .Returns(new ResourceClaimActionAuthStrategyListResult.FailureHierarchyNotFound());
        }

        [Test]
        public async Task It_returns_404_for_get_all()
        {
            var client = SetUpClient();
            var response = await client.GetAsync("/v3/resourceClaims");

            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        [Test]
        public async Task It_returns_404_for_get_by_id()
        {
            var client = SetUpClient();
            var response = await client.GetAsync("/v3/resourceClaims/1");

            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        [Test]
        public async Task It_returns_404_for_get_actions()
        {
            var client = SetUpClient();
            var response = await client.GetAsync("/v3/resourceClaimActions");

            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        [Test]
        public async Task It_returns_404_for_get_auth_strategies()
        {
            var client = SetUpClient();
            var response = await client.GetAsync("/v3/resourceClaimActionAuthStrategies");

            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
    }

    [TestFixture]
    public class Given_projection_integrity_failure : ResourceClaimModuleTests
    {
        [SetUp]
        public void SetUp()
        {
            A.CallTo(() => _repository.GetResourceClaims(A<ResourceClaimQuery>.Ignored))
                .Returns(
                    new ResourceClaimListResult.FailureProjectionIntegrity("Projection failed for test.")
                );

            A.CallTo(() => _repository.GetResourceClaim(A<long>.Ignored))
                .Returns(
                    new ResourceClaimGetResult.FailureProjectionIntegrity("Projection failed for test.")
                );

            A.CallTo(() => _repository.GetResourceClaimActions(A<ResourceClaimActionQuery>.Ignored))
                .Returns(
                    new ResourceClaimActionListResult.FailureProjectionIntegrity(
                        "Projection failed for test."
                    )
                );

            A.CallTo(() =>
                    _repository.GetResourceClaimActionAuthStrategies(
                        A<ResourceClaimActionAuthStrategyQuery>.Ignored
                    )
                )
                .Returns(
                    new ResourceClaimActionAuthStrategyListResult.FailureProjectionIntegrity(
                        "Projection failed for test."
                    )
                );
        }

        [Test]
        public async Task It_returns_500_for_get_all()
        {
            var client = SetUpClient();
            var response = await client.GetAsync("/v3/resourceClaims");

            response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        }

        [Test]
        public async Task It_returns_500_for_get_by_id()
        {
            var client = SetUpClient();
            var response = await client.GetAsync("/v3/resourceClaims/1");

            response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        }

        [Test]
        public async Task It_returns_500_for_get_actions()
        {
            var client = SetUpClient();
            var response = await client.GetAsync("/v3/resourceClaimActions");

            response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        }

        [Test]
        public async Task It_returns_500_for_get_auth_strategies()
        {
            var client = SetUpClient();
            var response = await client.GetAsync("/v3/resourceClaimActionAuthStrategies");

            response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        }
    }

    [TestFixture]
    public class Given_query_parameter_binding : ResourceClaimModuleTests
    {
        private static readonly List<ResourceClaimResponse> EmptyClaims = [];
        private static readonly List<ResourceClaimActionResponse> EmptyActions = [];
        private static readonly List<ResourceClaimActionAuthStrategyResponse> EmptyAuthStrategies = [];

        [SetUp]
        public void SetUp()
        {
            A.CallTo(() => _repository.GetResourceClaims(A<ResourceClaimQuery>.Ignored))
                .Returns(new ResourceClaimListResult.Success(EmptyClaims));

            A.CallTo(() => _repository.GetResourceClaimActions(A<ResourceClaimActionQuery>.Ignored))
                .Returns(new ResourceClaimActionListResult.Success(EmptyActions));

            A.CallTo(() =>
                    _repository.GetResourceClaimActionAuthStrategies(
                        A<ResourceClaimActionAuthStrategyQuery>.Ignored
                    )
                )
                .Returns(new ResourceClaimActionAuthStrategyListResult.Success(EmptyAuthStrategies));
        }

        [Test]
        public async Task It_passes_id_filter_to_repository()
        {
            var client = SetUpClient();
            await client.GetAsync("/v3/resourceClaims?id=42");

            A.CallTo(() =>
                    _repository.GetResourceClaims(A<ResourceClaimQuery>.That.Matches(q => q.Id == 42L))
                )
                .MustHaveHappenedOnceExactly();
        }

        [Test]
        public async Task It_passes_name_filter_to_repository()
        {
            var client = SetUpClient();
            await client.GetAsync("/v3/resourceClaims?name=types");

            A.CallTo(() =>
                    _repository.GetResourceClaims(A<ResourceClaimQuery>.That.Matches(q => q.Name == "types"))
                )
                .MustHaveHappenedOnceExactly();
        }

        [Test]
        public async Task It_passes_limit_and_offset_to_repository()
        {
            var client = SetUpClient();
            await client.GetAsync("/v3/resourceClaims?limit=5&offset=10");

            A.CallTo(() =>
                    _repository.GetResourceClaims(
                        A<ResourceClaimQuery>.That.Matches(q => q.Limit == 5 && q.Offset == 10)
                    )
                )
                .MustHaveHappenedOnceExactly();
        }

        [Test]
        public async Task It_passes_orderBy_and_direction_to_repository()
        {
            var client = SetUpClient();
            await client.GetAsync("/v3/resourceClaims?orderBy=name&direction=desc");

            A.CallTo(() =>
                    _repository.GetResourceClaims(
                        A<ResourceClaimQuery>.That.Matches(q => q.OrderBy == "name" && q.Direction == "desc")
                    )
                )
                .MustHaveHappenedOnceExactly();
        }

        [Test]
        public async Task It_passes_resourceName_filter_to_actions_repository()
        {
            var client = SetUpClient();
            await client.GetAsync("/v3/resourceClaimActions?resourceName=types");

            A.CallTo(() =>
                    _repository.GetResourceClaimActions(
                        A<ResourceClaimActionQuery>.That.Matches(q => q.ResourceName == "types")
                    )
                )
                .MustHaveHappenedOnceExactly();
        }

        [Test]
        public async Task It_passes_paging_to_actions_repository()
        {
            var client = SetUpClient();
            await client.GetAsync(
                "/v3/resourceClaimActions?limit=3&offset=6&orderBy=resourceClaimId&direction=asc"
            );

            A.CallTo(() =>
                    _repository.GetResourceClaimActions(
                        A<ResourceClaimActionQuery>.That.Matches(q =>
                            q.Limit == 3
                            && q.Offset == 6
                            && q.OrderBy == "resourceClaimId"
                            && q.Direction == "asc"
                        )
                    )
                )
                .MustHaveHappenedOnceExactly();
        }

        [Test]
        public async Task It_passes_resourceName_filter_to_auth_strategies_repository()
        {
            var client = SetUpClient();
            await client.GetAsync("/v3/resourceClaimActionAuthStrategies?resourceName=types");

            A.CallTo(() =>
                    _repository.GetResourceClaimActionAuthStrategies(
                        A<ResourceClaimActionAuthStrategyQuery>.That.Matches(q => q.ResourceName == "types")
                    )
                )
                .MustHaveHappenedOnceExactly();
        }

        [Test]
        public async Task It_passes_paging_to_auth_strategies_repository()
        {
            var client = SetUpClient();
            await client.GetAsync(
                "/v3/resourceClaimActionAuthStrategies?limit=2&offset=4&orderBy=claimName&direction=desc"
            );

            A.CallTo(() =>
                    _repository.GetResourceClaimActionAuthStrategies(
                        A<ResourceClaimActionAuthStrategyQuery>.That.Matches(q =>
                            q.Limit == 2 && q.Offset == 4 && q.OrderBy == "claimName" && q.Direction == "desc"
                        )
                    )
                )
                .MustHaveHappenedOnceExactly();
        }
    }

    [TestFixture]
    public class Given_The_Claims_Hierarchy_Is_Missing : ResourceClaimModuleTests
    {
        private HttpResponseMessage _getAllResponse = null!;
        private HttpResponseMessage _getByIdResponse = null!;
        private HttpResponseMessage _getActionsResponse = null!;
        private HttpResponseMessage _getAuthStrategiesResponse = null!;

        [SetUp]
        public async Task SetUp()
        {
            A.CallTo(() => _repository.GetResourceClaims(A<ResourceClaimQuery>.Ignored))
                .Returns(new ResourceClaimListResult.FailureHierarchyNotFound());
            A.CallTo(() => _repository.GetResourceClaim(A<long>.Ignored))
                .Returns(new ResourceClaimGetResult.FailureHierarchyNotFound());
            A.CallTo(() => _repository.GetResourceClaimActions(A<ResourceClaimActionQuery>.Ignored))
                .Returns(new ResourceClaimActionListResult.FailureHierarchyNotFound());
            A.CallTo(() =>
                    _repository.GetResourceClaimActionAuthStrategies(
                        A<ResourceClaimActionAuthStrategyQuery>.Ignored
                    )
                )
                .Returns(new ResourceClaimActionAuthStrategyListResult.FailureHierarchyNotFound());

            var client = SetUpClient();
            _getAllResponse = await client.GetAsync("/v3/resourceClaims");
            _getByIdResponse = await client.GetAsync("/v3/resourceClaims/1");
            _getActionsResponse = await client.GetAsync("/v3/resourceClaimActions");
            _getAuthStrategiesResponse = await client.GetAsync("/v3/resourceClaimActionAuthStrategies");
        }

        [TearDown]
        public void TearDown()
        {
            _getAllResponse?.Dispose();
            _getByIdResponse?.Dispose();
            _getActionsResponse?.Dispose();
            _getAuthStrategiesResponse?.Dispose();
        }

        [Test]
        public async Task It_returns_the_not_found_contract_for_get_all() =>
            await _getAllResponse.ShouldBeProblemDetailAsync(
                HttpStatusCode.NotFound,
                "urn:ed-fi:api:not-found",
                "Not Found",
                "The claims hierarchy was not found."
            );

        [Test]
        public async Task It_returns_the_not_found_contract_for_get_by_id() =>
            await _getByIdResponse.ShouldBeProblemDetailAsync(
                HttpStatusCode.NotFound,
                "urn:ed-fi:api:not-found",
                "Not Found",
                "The claims hierarchy was not found."
            );

        [Test]
        public async Task It_returns_the_not_found_contract_for_get_actions() =>
            await _getActionsResponse.ShouldBeProblemDetailAsync(
                HttpStatusCode.NotFound,
                "urn:ed-fi:api:not-found",
                "Not Found",
                "The claims hierarchy was not found."
            );

        [Test]
        public async Task It_returns_the_not_found_contract_for_get_auth_strategies() =>
            await _getAuthStrategiesResponse.ShouldBeProblemDetailAsync(
                HttpStatusCode.NotFound,
                "urn:ed-fi:api:not-found",
                "Not Found",
                "The claims hierarchy was not found."
            );
    }

    [TestFixture]
    public class Given_A_Resource_Claim_That_Does_Not_Exist : ResourceClaimModuleTests
    {
        private HttpResponseMessage _response = null!;

        [SetUp]
        public async Task SetUp()
        {
            A.CallTo(() => _repository.GetResourceClaim(A<long>.Ignored))
                .Returns(new ResourceClaimGetResult.FailureNotFound());

            var client = SetUpClient();
            _response = await client.GetAsync("/v3/resourceClaims/999");
        }

        [TearDown]
        public void TearDown() => _response?.Dispose();

        [Test]
        public async Task It_returns_the_not_found_contract() =>
            await _response.ShouldBeProblemDetailAsync(
                HttpStatusCode.NotFound,
                "urn:ed-fi:api:not-found",
                "Not Found",
                "ResourceClaim 999 not found."
            );
    }
}
