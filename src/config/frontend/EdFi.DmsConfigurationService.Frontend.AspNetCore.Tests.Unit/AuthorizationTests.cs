// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Net;
using System.Text;
using EdFi.DmsConfigurationService.Backend.AuthorizationMetadata;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.Authorization;
using EdFi.DmsConfigurationService.DataModel.Model.ClaimSets;
using EdFi.DmsConfigurationService.DataModel.Model.Vendor;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Configuration;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure.Authorization;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Action = EdFi.DmsConfigurationService.Backend.AuthorizationMetadata.ClaimSetMetadata.Action;
using Authorization = EdFi.DmsConfigurationService.Backend.AuthorizationMetadata.ClaimSetMetadata.Authorization;
using Claim = EdFi.DmsConfigurationService.Backend.Models.ClaimsHierarchy.Claim;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit;

public class AuthorizationTests
{
    private readonly IVendorRepository _vendorRepository = A.Fake<IVendorRepository>();
    private readonly HttpContext _httpContext = A.Fake<HttpContext>();
    private readonly IClaimSetRepository _claimSetRepository = A.Fake<IClaimSetRepository>();
    private readonly IClaimsHierarchyRepository _claimsHierarchyRepository =
        A.Fake<IClaimsHierarchyRepository>();
    private readonly IAuthorizationMetadataResponseFactory _responseFactory =
        A.Fake<IAuthorizationMetadataResponseFactory>();

    private static void SetScope(string scopeName, HttpClient httpClient)
    {
        httpClient.DefaultRequestHeaders.Add("X-Test-Scope", scopeName);
    }

    private HttpClient SetUpClient(string scopeName = "edfi_admin_api/full_access")
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(
                (ctx, collection) =>
                {
                    collection
                        .AddAuthentication(AuthenticationConstants.AuthenticationSchema)
                        .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                            AuthenticationConstants.AuthenticationSchema,
                            _ => { }
                        );

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

                    collection
                        .AddTransient(_ => _httpContext)
                        .AddTransient(_ => _vendorRepository)
                        .AddTransient(_ => _claimsHierarchyRepository)
                        .AddTransient(_ => _responseFactory)
                        .AddTransient(_ => _claimSetRepository);
                }
            );
        });
        var httpClient = factory.CreateClient();
        SetScope(scopeName, httpClient);
        return httpClient;
    }

    [TestFixture]
    public class SuccessTests : AuthorizationTests
    {
        [SetUp]
        public void SetUp()
        {
            A.CallTo(() => _vendorRepository.InsertVendor(A<VendorInsertCommand>.Ignored))
                .Returns(new VendorInsertResult.Success(1, true));

            A.CallTo(() => _vendorRepository.QueryVendor(A<PagingQuery>.Ignored))
                .Returns(
                    new VendorQueryResult.Success(
                        [
                            new VendorResponse()
                            {
                                Id = 1,
                                Company = "Test Company",
                                ContactName = "Test Contact",
                                ContactEmailAddress = "test@test.com",
                                NamespacePrefixes = "Test Prefix",
                            },
                        ]
                    )
                );

            A.CallTo(() => _vendorRepository.GetVendor(A<long>.Ignored))
                .Returns(
                    new VendorGetResult.Success(
                        new VendorResponse()
                        {
                            Id = 1,
                            Company = "Test Company",
                            ContactEmailAddress = "Test",
                            ContactName = "Test",
                            NamespacePrefixes = "Test Prefix",
                        }
                    )
                );

            A.CallTo(() => _vendorRepository.UpdateVendor(A<VendorUpdateCommand>.Ignored))
                .Returns(new VendorUpdateResult.Success(new List<Guid>()));

            A.CallTo(() => _vendorRepository.DeleteVendor(A<long>.Ignored))
                .Returns(new VendorDeleteResult.Success());
        }

        [Test]
        public async Task Should_return_proper_success_responses()
        {
            // Arrange
            using var client = SetUpClient();
            A.CallTo(() => _httpContext.Request.Path).Returns("/v2/vendors");

            //Act
            var addResponse = await client.PostAsync(
                "/v2/vendors",
                new StringContent(
                    """
                    {
                      "company": "Test 11",
                      "contactName": "Test",
                      "contactEmailAddress": "test@gmail.com",
                      "namespacePrefixes": "Test"
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );
            var getResponse = await client.GetAsync("/v2/vendors?offset=0&limit=25");
            var getByIdResponse = await client.GetAsync("/v2/vendors/1");
            var updateResponse = await client.PutAsync(
                "/v2/vendors/1",
                new StringContent(
                    """
                    {
                        "id": 1,
                        "company": "Test 11",
                        "contactName": "Test",
                        "contactEmailAddress": "test@gmail.com",
                        "namespacePrefixes": "Test"
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );
            var deleteResponse = await client.DeleteAsync("/v2/vendors/1");

            //Assert
            addResponse.StatusCode.Should().Be(HttpStatusCode.Created);
            addResponse.Headers.Location!.ToString().Should().EndWith("/v2/vendors/1");
            getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            getByIdResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            updateResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
            deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }
    }

    [TestFixture]
    public class ReadOnly_Authorization_Scope_Tests : AuthorizationTests
    {
        [SetUp]
        public void SetUp()
        {
            A.CallTo(() => _vendorRepository.InsertVendor(A<VendorInsertCommand>.Ignored))
                .Returns(new VendorInsertResult.Success(1, false));

            A.CallTo(() => _vendorRepository.QueryVendor(A<PagingQuery>.Ignored))
                .Returns(
                    new VendorQueryResult.Success(
                        [
                            new VendorResponse()
                            {
                                Id = 1,
                                Company = "Test Company",
                                ContactName = "Test Contact",
                                ContactEmailAddress = "test@test.com",
                                NamespacePrefixes = "Test Prefix",
                            },
                        ]
                    )
                );

            A.CallTo(() => _vendorRepository.GetVendor(A<long>.Ignored))
                .Returns(
                    new VendorGetResult.Success(
                        new VendorResponse()
                        {
                            Id = 1,
                            Company = "Test Company",
                            ContactEmailAddress = "Test",
                            ContactName = "Test",
                            NamespacePrefixes = "Test Prefix",
                        }
                    )
                );
        }

        [Test]
        public async Task Should_return_proper_success_responses_for_read_end_points()
        {
            // Arrange
            using var client = SetUpClient(AuthorizationScopes.ReadOnlyScope.Name);
            A.CallTo(() => _httpContext.Request.Path).Returns("/v2/vendors");

            //Act
            var getResponse = await client.GetAsync("/v2/vendors?offset=0&limit=25");
            var getByIdResponse = await client.GetAsync("/v2/vendors/1");

            //Assert
            getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            getByIdResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Test]
        public async Task Should_return_forbidden_response_for_post_end_point()
        {
            // Arrange
            using var client = SetUpClient(AuthorizationScopes.ReadOnlyScope.Name);
            A.CallTo(() => _httpContext.Request.Path).Returns("/v2/vendors");

            //Act
            var addResponse = await client.PostAsync(
                "/v2/vendors",
                new StringContent(
                    """
                    {
                      "company": "Test 11",
                      "contactName": "Test",
                      "contactEmailAddress": "test@gmail.com",
                      "namespacePrefixes": "Test"
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );

            //Assert
            addResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        [Test]
        public async Task Should_return_forbidden_response_for_put_end_point()
        {
            // Arrange
            using var client = SetUpClient(AuthorizationScopes.ReadOnlyScope.Name);
            A.CallTo(() => _httpContext.Request.Path).Returns("/v2/vendors");

            //Act
            var updateResponse = await client.PutAsync(
                "/v2/vendors/1",
                new StringContent(
                    """
                    {
                        "id": 1,
                        "company": "Test 11",
                        "contactName": "Test",
                        "contactEmailAddress": "test@gmail.com",
                        "namespacePrefixes": "Test"
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );

            //Assert
            updateResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        [Test]
        public async Task Should_return_forbidden_response_for_delete_end_point()
        {
            // Arrange
            using var client = SetUpClient(AuthorizationScopes.ReadOnlyScope.Name);
            A.CallTo(() => _httpContext.Request.Path).Returns("/v2/vendors");

            //Act
            var deleteResponse = await client.DeleteAsync("/v2/vendors/1");

            //Assert
            deleteResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
    }

    [TestFixture]
    public class Access_To_Authorization_End_Points_Only_Tests : AuthorizationTests
    {
        [SetUp]
        public void SetUp()
        {
            A.CallTo(() => _claimSetRepository.QueryClaimSet(A<PagingQuery>.Ignored))
                .Returns(
                    new ClaimSetQueryResult.Success(
                        [new ClaimSetResponse() { Name = "Test ClaimSet", IsSystemReserved = false }]
                    )
                );

            List<Claim> claims =
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

            A.CallTo(() => _claimsHierarchyRepository.GetClaimsHierarchy(A<DbTransaction>.Ignored))
                .Returns(new ClaimsHierarchyGetResult.Success(claims, DateTime.Now, 1));
            var suppliedAuthorizationMetadataResponse = new AuthorizationMetadataResponse(
                [
                    new ClaimSetMetadata(
                        ClaimSetName: "ClaimSet1",
                        Claims: [new("ClaimOne", 1)],
                        Authorizations:
                        [
                            new Authorization(
                                1,
                                [
                                    new Action(
                                        "Create",
                                        [new ClaimSetMetadata.AuthorizationStrategy("Strategy1")]
                                    ),
                                ]
                            ),
                        ]
                    ),
                ]
            );

            A.CallTo(() => _responseFactory.Create("ClaimSet1", claims))
                .Returns(suppliedAuthorizationMetadataResponse);
        }

        [Test]
        public async Task Should_return_proper_success_responses_for_read_end_points()
        {
            // Arrange
            using var client = SetUpClient(AuthorizationScopes.AuthMetadataReadOnlyAccessScope.Name);

            //Act
            var getAuthorizationMetaDataResponse = await client.GetAsync(
                "/authorizationMetadata?claimSetName=ClaimSet1"
            );
            var getAllClaimSetsResponse = await client.GetAsync("/v2/claimSets");

            //Assert
            getAuthorizationMetaDataResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            getAllClaimSetsResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Test]
        public async Task Should_return_forbidden_response_for_get_end_point_with_no_access()
        {
            // Arrange
            using var client = SetUpClient(AuthorizationScopes.AuthMetadataReadOnlyAccessScope.Name);

            // Act
            var getResponse = await client.GetAsync("/v2/vendors?offset=0&limit=25");

            //Assert
            getResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        [Test]
        public async Task Should_return_forbidden_response_for_post_end_point()
        {
            // Arrange
            using var client = SetUpClient(AuthorizationScopes.AuthMetadataReadOnlyAccessScope.Name);
            A.CallTo(() => _httpContext.Request.Path).Returns("/v2/vendors");

            //Act
            var addResponse = await client.PostAsync(
                "/v2/vendors",
                new StringContent(
                    """
                    {
                      "company": "Test 11",
                      "contactName": "Test",
                      "contactEmailAddress": "test@gmail.com",
                      "namespacePrefixes": "Test"
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );

            //Assert
            addResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        [Test]
        public async Task Should_return_forbidden_response_for_put_end_point()
        {
            // Arrange
            using var client = SetUpClient(AuthorizationScopes.AuthMetadataReadOnlyAccessScope.Name);
            A.CallTo(() => _httpContext.Request.Path).Returns("/v2/vendors");

            //Act
            var updateResponse = await client.PutAsync(
                "/v2/vendors/1",
                new StringContent(
                    """
                    {
                        "id": 1,
                        "company": "Test 11",
                        "contactName": "Test",
                        "contactEmailAddress": "test@gmail.com",
                        "namespacePrefixes": "Test"
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );

            //Assert
            updateResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        [Test]
        public async Task Should_return_forbidden_response_for_delete_end_point()
        {
            // Arrange
            using var client = SetUpClient(AuthorizationScopes.AuthMetadataReadOnlyAccessScope.Name);
            A.CallTo(() => _httpContext.Request.Path).Returns("/v2/vendors");

            //Act
            var deleteResponse = await client.DeleteAsync("/v2/vendors/1");

            //Assert
            deleteResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
    }
}
