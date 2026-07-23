// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Net;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend.AuthorizationMetadata;
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

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Modules;

public class AuthorizationMetadataModuleTests
{
    private readonly IClaimsHierarchyRepository _claimsHierarchyRepository =
        A.Fake<IClaimsHierarchyRepository>();
    private readonly IAuthorizationMetadataResponseFactory _responseFactory =
        A.Fake<IAuthorizationMetadataResponseFactory>();

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

                    collection.AddTransient(_ => _claimsHierarchyRepository);
                    collection.AddTransient(_ => _responseFactory);
                }
            );
        });
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Scope", AuthorizationScopes.AdminScope.Name);
        return client;
    }

    [TestFixture]
    public class Given_The_Claims_Hierarchy_Is_Not_Found : AuthorizationMetadataModuleTests
    {
        [SetUp]
        public void SetUp() =>
            A.CallTo(() => _claimsHierarchyRepository.GetClaimsHierarchy(A<DbTransaction>._))
                .Returns(new ClaimsHierarchyGetResult.FailureHierarchyNotFound());

        [Test]
        public async Task It_returns_the_problem_details_not_found_contract()
        {
            using var client = SetUpClient();

            var response = await client.GetAsync("/v3/authorizationMetadata?claimSetName=ClaimSet1");

            JsonNode body = await response.ShouldBeProblemDetailAsync(
                HttpStatusCode.NotFound,
                "urn:ed-fi:api:not-found",
                "Not Found",
                "Authorization metadata for claim set 'ClaimSet1' not found."
            );
            body["validationErrors"]!.AsObject().Count.Should().Be(0);
            body["errors"]!.AsArray().Count.Should().Be(0);
        }
    }
}
