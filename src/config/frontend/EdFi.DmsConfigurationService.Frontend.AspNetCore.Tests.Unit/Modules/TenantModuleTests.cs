// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.Authorization;
using EdFi.DmsConfigurationService.DataModel.Model.Tenant;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Configuration;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure.Authorization;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Modules;

public class TenantModuleTests
{
    private readonly ITenantRepository _tenantRepository = A.Fake<ITenantRepository>();
    private readonly HttpContext _httpContext = A.Fake<HttpContext>();

    private HttpClient SetUpClient(bool multiTenancyEnabled = true)
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");

            // Set MultiTenancy in configuration (used by WebApplicationExtensions.MapRouteEndpoints)
            builder.ConfigureAppConfiguration(
                (_, config) =>
                {
                    config.AddInMemoryCollection(
                        new Dictionary<string, string?>
                        {
                            ["AppSettings:MultiTenancy"] = multiTenancyEnabled.ToString(),
                        }
                    );
                }
            );

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

                    // Override AppSettings to control MultiTenancy flag for IOptions<AppSettings>
                    collection.Configure<AppSettings>(settings =>
                    {
                        settings.MultiTenancy = multiTenancyEnabled;
                    });

                    collection.AddTransient((_) => _httpContext).AddTransient((_) => _tenantRepository);

                    // If multitenancy is enabled, mock a successful tenant resolution
                    if (multiTenancyEnabled)
                    {
                        var fakeTenantResponse =
                            new EdFi.DmsConfigurationService.DataModel.Model.Tenant.TenantResponse
                            {
                                Id = 1,
                                Name = "test-tenant",
                                CreatedAt = DateTime.UtcNow,
                            };
                        A.CallTo(() => _tenantRepository.GetTenantByName("test-tenant"))
                            .Returns(new TenantGetByNameResult.Success(fakeTenantResponse));
                    }
                }
            );
        });
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Scope", AuthorizationScopes.AdminScope.Name);
        if (multiTenancyEnabled)
        {
            client.DefaultRequestHeaders.Add("Tenant", "test-tenant");
        }
        return client;
    }

    [TestFixture]
    public class Given_MultiTenancy_Is_Enabled : TenantModuleTests
    {
        [TestFixture]
        public class SuccessTests : Given_MultiTenancy_Is_Enabled
        {
            [SetUp]
            public void SetUp()
            {
                A.CallTo(() => _tenantRepository.InsertTenant(A<TenantInsertCommand>.Ignored))
                    .Returns(new TenantInsertResult.Success(1));

                A.CallTo(() => _tenantRepository.QueryTenant(A<PagingQuery>.Ignored))
                    .Returns(
                        new TenantQueryResult.Success(
                            [
                                new TenantResponse()
                                {
                                    Id = 1,
                                    Name = "Test Tenant",
                                    CreatedAt = DateTime.UtcNow,
                                },
                            ]
                        )
                    );

                A.CallTo(() => _tenantRepository.GetTenant(A<long>.Ignored))
                    .Returns(
                        new TenantGetResult.Success(
                            new TenantResponse()
                            {
                                Id = 1,
                                Name = "Test Tenant",
                                CreatedAt = DateTime.UtcNow,
                            }
                        )
                    );
            }

            [Test]
            public async Task It_returns_proper_success_responses()
            {
                // Arrange
                using var client = SetUpClient(multiTenancyEnabled: true);
                A.CallTo(() => _httpContext.Request.Path).Returns("/v2/tenants");

                //Act
                var addResponse = await client.PostAsync(
                    "/v2/tenants/",
                    new StringContent(
                        """
                        {
                          "name": "Test Tenant"
                        }
                        """,
                        Encoding.UTF8,
                        "application/json"
                    )
                );
                var getResponse = await client.GetAsync("/v2/tenants/?offset=0&limit=25");
                var getByIdResponse = await client.GetAsync("/v2/tenants/1");

                //Assert
                addResponse.StatusCode.Should().Be(HttpStatusCode.Created);
                addResponse.Headers.Location!.ToString().Should().EndWith("/v2/tenants/1");
                getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
                getByIdResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            }
        }

        [TestFixture]
        public class FailureValidationTests : Given_MultiTenancy_Is_Enabled
        {
            [Test]
            public async Task It_returns_bad_request_for_invalid_input()
            {
                // Arrange
                using var client = SetUpClient(multiTenancyEnabled: true);

                string invalidPostBody = """
                    {
                      "name": "0123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789"
                    }
                    """;

                //Act
                var addResponse = await client.PostAsync(
                    "/v2/tenants/",
                    new StringContent(invalidPostBody, Encoding.UTF8, "application/json")
                );

                //Assert
                var actualPostResponse = JsonNode.Parse(await addResponse.Content.ReadAsStringAsync());

                addResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
                actualPostResponse!["detail"]!
                    .GetValue<string>()
                    .Should()
                    .Be("Data validation failed. See 'validationErrors' for details.");
                actualPostResponse["type"]!
                    .GetValue<string>()
                    .Should()
                    .Be("urn:ed-fi:api:bad-request:data-validation-failed");
                actualPostResponse["title"]!.GetValue<string>().Should().Be("Data Validation Failed");
                actualPostResponse["status"]!.GetValue<int>().Should().Be(400);
                actualPostResponse["correlationId"]!.GetValue<string>().Should().NotBeNullOrEmpty();

                var validationErrors = actualPostResponse["validationErrors"]!.AsObject();
                validationErrors.Should().ContainKey("Name");
                var nameErrors = validationErrors["Name"]!.AsArray();
                nameErrors.Should().HaveCount(1);
                nameErrors[0]!.GetValue<string>().Should().Contain("256 characters or fewer");
                nameErrors[0]!.GetValue<string>().Should().Contain("280 characters");

                actualPostResponse["errors"]!.AsArray().Should().BeEmpty();
            }

            [Test]
            public async Task It_returns_bad_request_for_empty_name()
            {
                // Arrange
                using var client = SetUpClient(multiTenancyEnabled: true);

                string invalidPostBody = """
                    {
                      "name": ""
                    }
                    """;

                //Act
                var addResponse = await client.PostAsync(
                    "/v2/tenants/",
                    new StringContent(invalidPostBody, Encoding.UTF8, "application/json")
                );

                //Assert
                addResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
                var responseContent = await addResponse.Content.ReadAsStringAsync();
                responseContent.Should().Contain("'Name' must not be empty.");
            }
        }

        [TestFixture]
        public class FailureDuplicateNameTests : Given_MultiTenancy_Is_Enabled
        {
            [SetUp]
            public void SetUp()
            {
                A.CallTo(() => _tenantRepository.InsertTenant(A<TenantInsertCommand>.Ignored))
                    .Returns(new TenantInsertResult.FailureDuplicateName());
            }

            [Test]
            public async Task It_returns_bad_request_for_duplicate_name()
            {
                // Arrange
                using var client = SetUpClient(multiTenancyEnabled: true);

                //Act
                var addResponse = await client.PostAsync(
                    "/v2/tenants/",
                    new StringContent(
                        """
                        {
                          "name": "Duplicate Tenant"
                        }
                        """,
                        Encoding.UTF8,
                        "application/json"
                    )
                );

                //Assert
                addResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
                var responseContent = await addResponse.Content.ReadAsStringAsync();
                responseContent
                    .Should()
                    .Contain("A tenant name already exists in the database. Please enter a unique name.");
            }
        }

        [TestFixture]
        public class FailureNotFoundTests : Given_MultiTenancy_Is_Enabled
        {
            [SetUp]
            public void SetUp()
            {
                A.CallTo(() => _tenantRepository.GetTenant(A<long>.Ignored))
                    .Returns(new TenantGetResult.FailureNotFound());
            }

            [Test]
            public async Task It_returns_proper_not_found_responses()
            {
                // Arrange
                using var client = SetUpClient(multiTenancyEnabled: true);

                //Act
                var getByIdResponse = await client.GetAsync("/v2/tenants/1");

                //Assert
                getByIdResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
                var responseContent = await getByIdResponse.Content.ReadAsStringAsync();
                responseContent.Should().Contain("Tenant 1 not found. It may have been recently deleted.");
            }

            [Test]
            public async Task It_returns_bad_request_when_id_not_number()
            {
                // Arrange
                using var client = SetUpClient(multiTenancyEnabled: true);

                //Act
                var getByIdResponse = await client.GetAsync("/v2/tenants/a");

                //Assert
                getByIdResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            }
        }

        [TestFixture]
        public class FailureUnknownTests : Given_MultiTenancy_Is_Enabled
        {
            [SetUp]
            public void SetUp()
            {
                A.CallTo(() => _tenantRepository.InsertTenant(A<TenantInsertCommand>.Ignored))
                    .Returns(new TenantInsertResult.FailureUnknown("Database error"));

                A.CallTo(() => _tenantRepository.QueryTenant(A<PagingQuery>.Ignored))
                    .Returns(new TenantQueryResult.FailureUnknown("Database error"));

                A.CallTo(() => _tenantRepository.GetTenant(A<long>.Ignored))
                    .Returns(new TenantGetResult.FailureUnknown("Database error"));
            }

            [Test]
            public async Task It_returns_internal_server_error_response()
            {
                // Arrange
                using var client = SetUpClient(multiTenancyEnabled: true);

                //Act
                var addResponse = await client.PostAsync(
                    "/v2/tenants/",
                    new StringContent(
                        """
                        {
                          "name": "Test Tenant"
                        }
                        """,
                        Encoding.UTF8,
                        "application/json"
                    )
                );
                var getResponse = await client.GetAsync("/v2/tenants/?offset=0&limit=25");
                var getByIdResponse = await client.GetAsync("/v2/tenants/1");

                //Assert
                addResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
                getResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
                getByIdResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            }
        }

        [TestFixture]
        public class FailureDefaultTests : Given_MultiTenancy_Is_Enabled
        {
            [SetUp]
            public void SetUp()
            {
                A.CallTo(() => _tenantRepository.InsertTenant(A<TenantInsertCommand>.Ignored))
                    .Returns(new TenantInsertResult());

                A.CallTo(() => _tenantRepository.QueryTenant(A<PagingQuery>.Ignored))
                    .Returns(new TenantQueryResult());

                A.CallTo(() => _tenantRepository.GetTenant(A<long>.Ignored)).Returns(new TenantGetResult());
            }

            [Test]
            public async Task It_returns_internal_server_error_response()
            {
                // Arrange
                using var client = SetUpClient(multiTenancyEnabled: true);

                //Act
                var addResponse = await client.PostAsync(
                    "/v2/tenants/",
                    new StringContent(
                        """
                        {
                          "name": "Test Tenant"
                        }
                        """,
                        Encoding.UTF8,
                        "application/json"
                    )
                );
                var getResponse = await client.GetAsync("/v2/tenants/?offset=0&limit=25");
                var getByIdResponse = await client.GetAsync("/v2/tenants/1");

                //Assert
                addResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
                getResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
                getByIdResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            }
        }
    }

    [TestFixture]
    public class Given_MultiTenancy_Is_Disabled : TenantModuleTests
    {
        [Test]
        public async Task It_returns_404_for_post_endpoint()
        {
            // Arrange
            using var client = SetUpClient(multiTenancyEnabled: false);

            //Act
            var response = await client.PostAsync(
                "/v2/tenants/",
                new StringContent(
                    """
                    {
                      "name": "Test Tenant"
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );

            //Assert
            // When multi-tenancy is disabled, the TenantModule is not registered at all
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        [Test]
        public async Task It_returns_404_for_get_all_endpoint()
        {
            // Arrange
            using var client = SetUpClient(multiTenancyEnabled: false);

            //Act
            var response = await client.GetAsync("/v2/tenants/?offset=0&limit=25");

            //Assert
            // When multi-tenancy is disabled, the TenantModule is not registered at all
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        [Test]
        public async Task It_returns_404_for_get_by_id_endpoint()
        {
            // Arrange
            using var client = SetUpClient(multiTenancyEnabled: false);

            //Act
            var response = await client.GetAsync("/v2/tenants/1");

            //Assert
            // When multi-tenancy is disabled, the TenantModule is not registered at all
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        [Test]
        public async Task It_never_calls_repository_when_disabled()
        {
            // Arrange
            using var client = SetUpClient(multiTenancyEnabled: false);

            //Act
            await client.PostAsync(
                "/v2/tenants/",
                new StringContent(
                    """
                    {
                      "name": "Test Tenant"
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );
            await client.GetAsync("/v2/tenants/?offset=0&limit=25");
            await client.GetAsync("/v2/tenants/1");

            //Assert
            A.CallTo(() => _tenantRepository.InsertTenant(A<TenantInsertCommand>.Ignored))
                .MustNotHaveHappened();
            A.CallTo(() => _tenantRepository.QueryTenant(A<PagingQuery>.Ignored)).MustNotHaveHappened();
            A.CallTo(() => _tenantRepository.GetTenant(A<long>.Ignored)).MustNotHaveHappened();
        }
    }
}
