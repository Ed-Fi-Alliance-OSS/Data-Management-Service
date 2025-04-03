// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json;
using EdFi.DmsConfigurationService.Backend.AuthorizationMetadata;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel;
using EdFi.DmsConfigurationService.DataModel.Model.Authorization;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure.Authorization;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Modules;

[TestFixture]
public class ClaimsHierarchyModuleTests
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
                (collection) =>
                {
                    collection
                        .AddAuthentication(AuthenticationConstants.AuthenticationSchema)
                        .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                            AuthenticationConstants.AuthenticationSchema,
                            _ => { }
                        );

                    collection.AddAuthorization(options =>
                    {
                        options.AddPolicy(
                            SecurityConstants.ServicePolicy,
                            policy =>
                                policy.RequireClaim(
                                    System.Security.Claims.ClaimTypes.Role,
                                    AuthenticationConstants.Role
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

    [Test]
    public async Task Get_should_return_success_response()
    {
        // Arrange
        using var client = SetUpClient();

        Claim[] claims =
        [
            new Claim
            {
                Name = "Claim1",
                Claims = [new Claim { Name = "Claim-1a" }, new Claim { Name = "Claim-1b" }],
            },
            new Claim
            {
                Name = "Claim2",
                Claims = [new Claim { Name = "Claim-2a" }, new Claim { Name = "Claim-2b" }],
            },
        ];

        A.CallTo(() => _claimsHierarchyRepository.GetClaimsHierarchy())
            .Returns(new ClaimsHierarchyResult.Success(claims));

        var suppliedAuthorizationMetadataResponse = new AuthorizationMetadataResponse(
            Claims: [new("ClaimOne", 1)],
            Authorizations:
            [
                new AuthorizationMetadataResponse.Authorization(
                    1,
                    [
                        new AuthorizationMetadataResponse.Action(
                            "Create",
                            [new AuthorizationMetadataResponse.AuthorizationStrategy("Strategy1")]
                        ),
                    ]
                ),
            ]
        );

        A.CallTo(() => _responseFactory.Create("ClaimSet1", claims))
            .Returns(suppliedAuthorizationMetadataResponse);

        var responseMessage = await client.GetAsync("/authorizationMetadata?claimSetName=ClaimSet1");
        responseMessage.EnsureSuccessStatusCode();
        string responseContent = await responseMessage.Content.ReadAsStringAsync();

        var responseModel = JsonSerializer.Deserialize<AuthorizationMetadataResponse>(
            responseContent,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
        );

        // Assert
        responseMessage.StatusCode.Should().Be(HttpStatusCode.OK);
        responseModel.Should().BeEquivalentTo(suppliedAuthorizationMetadataResponse);
    }
}
