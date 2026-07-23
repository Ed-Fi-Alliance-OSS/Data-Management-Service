// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.Application;
using EdFi.DmsConfigurationService.DataModel.Model.Authorization;
using EdFi.DmsConfigurationService.DataModel.Model.Vendor;
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

public class VendorModuleTests
{
    private readonly IVendorRepository _vendorRepository = A.Fake<IVendorRepository>();
    private readonly IApplicationRepository _applicationRepository = A.Fake<IApplicationRepository>();
    private readonly IIdentityProviderRepository _clientRepository = A.Fake<IIdentityProviderRepository>();
    private readonly HttpContext _httpContext = A.Fake<HttpContext>();

    private HttpClient SetUpClient()
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
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
                    collection
                        .AddTransient((_) => _httpContext)
                        .AddTransient((_) => _vendorRepository)
                        .AddTransient((_) => _applicationRepository)
                        .AddTransient((_) => _clientRepository);
                }
            );
        });
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Scope", AuthorizationScopes.AdminScope.Name);
        return client;
    }

    [TestFixture]
    public class SuccessTests : VendorModuleTests
    {
        [SetUp]
        public void SetUp()
        {
            A.CallTo(() => _vendorRepository.InsertVendor(A<VendorInsertCommand>.Ignored))
                .Returns(new VendorInsertResult.Success(1, IsNewVendor: true));

            A.CallTo(() => _vendorRepository.QueryVendor(A<VendorQuery>.Ignored))
                .Returns(
                    new VendorQueryResult.Success([
                        new VendorResponse()
                        {
                            Id = 1,
                            Company = "Test Company",
                            ContactName = "Test Contact",
                            ContactEmailAddress = "test@test.com",
                            NamespacePrefixes = "Test Prefix",
                        },
                    ])
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
            A.CallTo(() => _httpContext.Request.Path).Returns("/v3/vendors");

            //Act
            var addResponse = await client.PostAsync(
                "/v3/vendors",
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
            var getResponse = await client.GetAsync("/v3/vendors?offset=0&limit=25");
            var getByIdResponse = await client.GetAsync("/v3/vendors/1");
            var updateResponse = await client.PutAsync(
                "/v3/vendors/1",
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
            var deleteResponse = await client.DeleteAsync("/v3/vendors/1");

            //Assert
            addResponse.StatusCode.Should().Be(HttpStatusCode.Created);
            addResponse.Headers.Location!.IsAbsoluteUri.Should().BeTrue();
            addResponse.Headers.Location!.ToString().Should().EndWith("/v3/vendors/1");
            var addBody = await addResponse.Content.ReadAsStringAsync();
            addBody.Should().BeEmpty();
            getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            getByIdResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            updateResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
            deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }
    }

    [TestFixture]
    public class UpsertTests : VendorModuleTests
    {
        [SetUp]
        public void SetUp()
        {
            A.CallTo(() => _vendorRepository.InsertVendor(A<VendorInsertCommand>.Ignored))
                .Returns(new VendorInsertResult.Success(1, IsNewVendor: false));
        }

        [Test]
        public async Task Should_return_200_with_location_when_vendor_already_exists()
        {
            using var client = SetUpClient();

            var response = await client.PostAsync(
                "/v3/vendors",
                new StringContent(
                    """
                    {
                      "company": "Existing Company",
                      "contactName": "Test",
                      "contactEmailAddress": "test@gmail.com",
                      "namespacePrefixes": "Test"
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Headers.Location!.IsAbsoluteUri.Should().BeTrue();
            response.Headers.Location!.ToString().Should().EndWith("/v3/vendors/1");
            var body = await response.Content.ReadAsStringAsync();
            body.Should().BeEmpty();
        }
    }

    [TestFixture]
    public class FailureDuplicateCompanyNameTests : VendorModuleTests
    {
        [SetUp]
        public void SetUp()
        {
            A.CallTo(() => _vendorRepository.InsertVendor(A<VendorInsertCommand>.Ignored))
                .Returns(new VendorInsertResult.FailureDuplicateCompanyName());
        }

        [Test]
        public async Task It_returns_the_non_unique_identity_conflict()
        {
            using var client = SetUpClient();

            var response = await client.PostAsync(
                "/v3/vendors",
                new StringContent(
                    """
                    {
                      "company": "Existing Company",
                      "contactName": "Test",
                      "contactEmailAddress": "test@gmail.com",
                      "namespacePrefixes": "Test"
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );

            response.StatusCode.Should().Be(HttpStatusCode.Conflict);
            var doc = JsonNode.Parse(await response.Content.ReadAsStringAsync());
            doc!["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:conflict:non-unique-identity");
            doc["title"]!.GetValue<string>().Should().Be("Identifying Values Are Not Unique");
            doc["validationErrors"]!.AsObject().Count.Should().Be(0);
        }
    }

    [TestFixture]
    public class FailureValidationTests : VendorModuleTests
    {
        [Test]
        public async Task Should_return_bad_request()
        {
            // Arrange
            using var client = SetUpClient();

            string invalidPostBody = """
                {
                  "company": "012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789",
                  "contactName": "012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789",
                  "contactEmailAddress": "INVALID",
                  "namespacePrefixes": "0123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789"
                }
                """;

            string invalidPutBody = """
                {
                  "id": 1,
                  "company": "012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789",
                  "contactName": "012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789",
                  "contactEmailAddress": "INVALID",
                  "namespacePrefixes": "0123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789"
                }
                """;

            //Act
            var addResponse = await client.PostAsync(
                "/v3/vendors",
                new StringContent(invalidPostBody, Encoding.UTF8, "application/json")
            );

            var updateResponse = await client.PutAsync(
                "/v3/vendors/1",
                new StringContent(invalidPutBody, Encoding.UTF8, "application/json")
            );

            //Assert
            var actualPostResponse = JsonNode.Parse(await addResponse.Content.ReadAsStringAsync());
            var expectedPostResponse = JsonNode.Parse(
                """
                {
                  "detail": "Data validation failed. See 'validationErrors' for details.",
                  "type": "urn:ed-fi:api:bad-request:data",
                  "title": "Data Validation Failed",
                  "status": 400,
                  "correlationId": "{correlationId}",
                  "validationErrors": {
                    "$.company": [
                      "The length of 'Company' must be 256 characters or fewer. You entered 300 characters."
                    ],
                    "$.contactName": [
                      "The length of 'Contact Name' must be 128 characters or fewer. You entered 300 characters."
                    ],
                    "$.contactEmailAddress": [
                      "'Contact Email Address' is not a valid email address."
                    ],
                    "$.namespacePrefixes": [
                      "Each NamespacePrefix length must be 128 characters or fewer."
                    ]
                  },
                  "errors": []
                }
                """.Replace("{correlationId}", actualPostResponse!["correlationId"]!.GetValue<string>())
            );

            var actualPutResponse = JsonNode.Parse(await updateResponse.Content.ReadAsStringAsync());
            var expectedPutResponse = JsonNode.Parse(
                """
                {
                  "detail": "Data validation failed. See 'validationErrors' for details.",
                  "type": "urn:ed-fi:api:bad-request:data",
                  "title": "Data Validation Failed",
                  "status": 400,
                  "correlationId": "{correlationId}",
                  "validationErrors": {
                    "$.company": [
                      "The length of 'Company' must be 256 characters or fewer. You entered 300 characters."
                    ],
                    "$.contactName": [
                      "The length of 'Contact Name' must be 128 characters or fewer. You entered 300 characters."
                    ],
                    "$.contactEmailAddress": [
                      "'Contact Email Address' is not a valid email address."
                    ],
                    "$.namespacePrefixes": [
                      "Each NamespacePrefix length must be 128 characters or fewer."
                    ]
                  },
                  "errors": []
                }
                """.Replace("{correlationId}", actualPutResponse!["correlationId"]!.GetValue<string>())
            );

            addResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            JsonNode.DeepEquals(actualPostResponse, expectedPostResponse).Should().Be(true);

            updateResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            JsonNode.DeepEquals(actualPutResponse, expectedPutResponse).Should().Be(true);
        }

        [Test]
        public async Task Should_return_bad_request_mismatch_id()
        {
            // Arrange
            using var client = SetUpClient();

            //Act
            var updateResponse = await client.PutAsync(
                "/v3/vendors/1",
                new StringContent(
                    """
                    {
                        "id": 2,
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
            string updateResponseContent = await updateResponse.Content.ReadAsStringAsync();
            updateResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            updateResponseContent.Should().Contain("Request body id must match the id in the url.");
        }
    }

    [TestFixture]
    public class FailureNotFoundTests : VendorModuleTests
    {
        [SetUp]
        public void SetUp()
        {
            A.CallTo(() => _vendorRepository.GetVendor(A<long>.Ignored))
                .Returns(new VendorGetResult.FailureNotFound());

            A.CallTo(() => _vendorRepository.UpdateVendor(A<VendorUpdateCommand>.Ignored))
                .Returns(new VendorUpdateResult.FailureNotExists());

            A.CallTo(() => _vendorRepository.DeleteVendor(A<long>.Ignored))
                .Returns(new VendorDeleteResult.FailureNotExists());
        }

        [Test]
        public async Task Should_return_proper_not_found_responses()
        {
            // Arrange
            using var client = SetUpClient();

            //Act

            var getByIdResponse = await client.GetAsync("/v3/vendors/1");
            var updateResponse = await client.PutAsync(
                "/v3/vendors/1",
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
            var deleteResponse = await client.DeleteAsync("/v3/vendors/1");

            //Assert
            getByIdResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
            updateResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
            deleteResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        [Test]
        public async Task Should_return_bad_request_when_id_not_number()
        {
            // Arrange
            using var client = SetUpClient();

            //Act
            var getByIdResponse = await client.GetAsync("/v3/vendors/a");
            var updateResponse = await client.PutAsync(
                "/v3/vendors/b",
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
            var deleteResponse = await client.DeleteAsync("/v3/vendors/c");

            //Assert
            getByIdResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            updateResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            deleteResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
    }

    [TestFixture]
    public class FailureUnknownTests : VendorModuleTests
    {
        [SetUp]
        public void SetUp()
        {
            A.CallTo(() => _vendorRepository.InsertVendor(A<VendorInsertCommand>.Ignored))
                .Returns(new VendorInsertResult.FailureUnknown(""));

            A.CallTo(() => _vendorRepository.QueryVendor(A<VendorQuery>.Ignored))
                .Returns(new VendorQueryResult.FailureUnknown(""));

            A.CallTo(() => _vendorRepository.GetVendor(A<long>.Ignored))
                .Returns(new VendorGetResult.FailureUnknown(""));

            A.CallTo(() => _vendorRepository.UpdateVendor(A<VendorUpdateCommand>.Ignored))
                .Returns(new VendorUpdateResult.FailureUnknown(""));

            A.CallTo(() => _vendorRepository.DeleteVendor(A<long>.Ignored))
                .Returns(new VendorDeleteResult.FailureUnknown(""));
        }

        [Test]
        public async Task Should_return_internal_server_error_response()
        {
            // Arrange
            using var client = SetUpClient();

            //Act
            var addResponse = await client.PostAsync(
                "/v3/vendors",
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
            var getResponse = await client.GetAsync("/v3/vendors?offset=0&limit=25");
            var getByIdResponse = await client.GetAsync("/v3/vendors/1");
            var updateResponse = await client.PutAsync(
                "/v3/vendors/1",
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
            var deleteResponse = await client.DeleteAsync("/v3/vendors/1");

            //Assert
            addResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            getResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            getByIdResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            updateResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            deleteResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        }
    }

    [TestFixture]
    public class FailureDefaultTests : VendorModuleTests
    {
        [SetUp]
        public void SetUp()
        {
            A.CallTo(() => _vendorRepository.InsertVendor(A<VendorInsertCommand>.Ignored))
                .Returns(new VendorInsertResult());

            A.CallTo(() => _vendorRepository.QueryVendor(A<VendorQuery>.Ignored))
                .Returns(new VendorQueryResult());

            A.CallTo(() => _vendorRepository.GetVendor(A<long>.Ignored)).Returns(new VendorGetResult());

            A.CallTo(() => _vendorRepository.UpdateVendor(A<VendorUpdateCommand>.Ignored))
                .Returns(new VendorUpdateResult());

            A.CallTo(() => _vendorRepository.DeleteVendor(A<long>.Ignored)).Returns(new VendorDeleteResult());
        }

        [Test]
        public async Task Should_return_internal_server_error_response()
        {
            // Arrange
            var client = SetUpClient();

            //Act
            var addResponse = await client.PostAsync(
                "/v3/vendors",
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
            var getResponse = await client.GetAsync("/v3/vendors?offset=0&limit=25");
            var getByIdResponse = await client.GetAsync("/v3/vendors/1");
            var updateResponse = await client.PutAsync(
                "/v3/vendors/1",
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
            var deleteResponse = await client.DeleteAsync("/v3/vendors/1");

            //Assert
            addResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            getResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            getByIdResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            updateResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            deleteResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        }
    }

    [TestFixture]
    public class GetApplicationsByVendorIdTests : VendorModuleTests
    {
        [SetUp]
        public void SetUp()
        {
            A.CallTo(() => _vendorRepository.GetVendorApplications(A<long>.Ignored))
                .Returns(
                    new VendorApplicationsResult.Success([
                        new ApplicationResponse()
                        {
                            Id = 1,
                            ApplicationName = "App 1",
                            ClaimSetName = "Name",
                            VendorId = 1,
                            EducationOrganizationIds = [1],
                        },
                        new ApplicationResponse()
                        {
                            Id = 2,
                            ApplicationName = "App 2",
                            ClaimSetName = "Name",
                            VendorId = 1,
                            EducationOrganizationIds = [1],
                        },
                    ])
                );
        }

        [Test]
        public async Task Should_get_a_list_of_applications_by_vendor_id()
        {
            // Arrange
            using var client = SetUpClient();
            A.CallTo(() => _vendorRepository.GetVendorApplications(A<long>.Ignored))
                .Returns(
                    new VendorApplicationsResult.Success([
                        new ApplicationResponse()
                        {
                            Id = 1,
                            ApplicationName = "App 1",
                            ClaimSetName = "Name",
                            VendorId = 1,
                            EducationOrganizationIds = [1],
                        },
                        new ApplicationResponse()
                        {
                            Id = 2,
                            ApplicationName = "App 2",
                            ClaimSetName = "Name",
                            VendorId = 1,
                            EducationOrganizationIds = [1],
                        },
                    ])
                );

            // Act
            var response = await client.GetAsync("/v3/vendors/1/applications");

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            string responseContent = await response.Content.ReadAsStringAsync();
            responseContent.Should().Contain("App 1");
            responseContent.Should().Contain("App 2");
        }

        [Test]
        public async Task Should_return_an_empty_array_for_a_vendor_with_no_applications()
        {
            // Arrange
            using var client = SetUpClient();

            A.CallTo(() => _vendorRepository.GetVendorApplications(A<long>.Ignored))
                .Returns(new VendorApplicationsResult.Success([]));

            // Act
            var response = await client.GetAsync("/v3/vendors/2/applications");

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            string responseContent = await response.Content.ReadAsStringAsync();
            responseContent.Should().Be("[]");
        }

        [Test]
        public async Task Should_return_not_found_related_to_a_not_found_vendor_id()
        {
            // Arrange
            using var client = SetUpClient();

            A.CallTo(() => _vendorRepository.GetVendorApplications(A<long>.Ignored))
                .Returns(new VendorApplicationsResult.FailureNotExists());

            // Act
            var response = await client.GetAsync("/v3/vendors/99/applications");

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
            string responseContent = await response.Content.ReadAsStringAsync();
            responseContent.Should().Contain("It may have been recently deleted.");
        }
    }

    [TestFixture]
    public class Given_Invalid_PagingQuery : VendorModuleTests
    {
        [SetUp]
        public void SetUp()
        {
            A.CallTo(() => _vendorRepository.QueryVendor(A<VendorQuery>.Ignored))
                .Returns(new VendorQueryResult.Success([]));
        }

        [Test]
        public async Task Should_return_400_when_orderBy_is_invalid()
        {
            using var client = SetUpClient();
            var response = await client.GetAsync("/v3/vendors?orderBy=invalidField");
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Test]
        public async Task Should_return_400_when_direction_is_invalid()
        {
            using var client = SetUpClient();
            var response = await client.GetAsync("/v3/vendors?orderBy=id&direction=SIDEWAYS");
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Test]
        public async Task Should_return_400_when_offset_is_negative()
        {
            using var client = SetUpClient();
            var response = await client.GetAsync("/v3/vendors?offset=-1");
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Test]
        public async Task Should_return_400_when_limit_is_zero()
        {
            using var client = SetUpClient();
            var response = await client.GetAsync("/v3/vendors?limit=0");
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Test]
        public async Task Should_return_parameter_validation_failure_when_limit_is_zero()
        {
            using var client = SetUpClient();
            var response = await client.GetAsync("/v3/vendors?limit=0");
            await response.ShouldBeProblemDetailAsync(
                HttpStatusCode.BadRequest,
                "urn:ed-fi:api:bad-request:parameter",
                "Parameter Validation Failed",
                "One or more query parameters were invalid. See 'errors' for details.",
                errors: ["'limit' must be greater than 0."]
            );
        }

        [Test]
        public async Task Should_return_200_with_valid_orderBy_and_direction()
        {
            using var client = SetUpClient();
            var response = await client.GetAsync("/v3/vendors?orderBy=company&direction=DESC");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Test]
        public async Task Should_return_200_when_direction_is_asc()
        {
            using var client = SetUpClient();
            var response = await client.GetAsync("/v3/vendors?direction=asc");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Test]
        public async Task Should_return_200_when_direction_is_ascending()
        {
            using var client = SetUpClient();
            var response = await client.GetAsync("/v3/vendors?direction=ascending");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Test]
        public async Task Should_return_200_when_direction_is_descending()
        {
            using var client = SetUpClient();
            var response = await client.GetAsync("/v3/vendors?direction=descending");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Test]
        public async Task Should_return_400_with_correct_message_when_direction_is_invalid()
        {
            using var client = SetUpClient();
            var response = await client.GetAsync("/v3/vendors?direction=sideways");
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            var content = await response.Content.ReadAsStringAsync();
            content
                .Should()
                .Contain("The direction query parameter must be one of: asc, ascending, desc, descending.");
        }

        [Test]
        public async Task Should_return_200_when_filter_id_is_provided()
        {
            using var client = SetUpClient();
            var response = await client.GetAsync("/v3/vendors?id=1");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Test]
        public async Task Should_return_200_when_filter_company_is_provided()
        {
            using var client = SetUpClient();
            var response = await client.GetAsync("/v3/vendors?company=Acme");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Test]
        public async Task Should_return_200_when_limit_equals_maximum()
        {
            using var client = SetUpClient();
            var response = await client.GetAsync("/v3/vendors?limit=100");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Test]
        public async Task Should_return_400_when_offset_is_non_numeric()
        {
            using var client = SetUpClient();
            var response = await client.GetAsync("/v3/vendors?offset=abc");
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Test]
        public async Task Should_return_400_when_limit_is_non_numeric()
        {
            using var client = SetUpClient();
            var response = await client.GetAsync("/v3/vendors?limit=xyz");
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
    }

    [TestFixture]
    public class Given_A_Vendor_With_A_Duplicate_Company_Name : VendorModuleTests
    {
        private HttpResponseMessage _response = null!;
        private JsonNode _body = null!;

        [SetUp]
        public async Task SetUp()
        {
            A.CallTo(() => _vendorRepository.InsertVendor(A<VendorInsertCommand>.Ignored))
                .Returns(new VendorInsertResult.FailureDuplicateCompanyName());

            using var client = SetUpClient();
            _response = await client.PostAsync(
                "/v3/vendors",
                new StringContent(
                    """
                    {
                      "company": "Existing Company",
                      "contactName": "Test",
                      "contactEmailAddress": "test@gmail.com",
                      "namespacePrefixes": "Test"
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );
            _body = await _response.ShouldBeProblemDetailAsync(
                HttpStatusCode.Conflict,
                "urn:ed-fi:api:conflict:non-unique-identity",
                "Identifying Values Are Not Unique",
                "The identifying value(s) of the item are the same as another item that already exists.",
                errors: ["A vendor name already exists in the database. Please enter a unique name."]
            );
        }

        [TearDown]
        public void TearDown() => _response?.Dispose();

        [Test]
        public void It_reports_the_duplicate_name_in_errors() =>
            _body["errors"]!
                .ToJsonString()
                .Should()
                .Contain("A vendor name already exists in the database. Please enter a unique name.");
    }

    [TestFixture]
    public class Given_A_Vendor_That_Does_Not_Exist : VendorModuleTests
    {
        private HttpResponseMessage _getResponse = null!;
        private HttpResponseMessage _updateResponse = null!;
        private HttpResponseMessage _deleteResponse = null!;

        [SetUp]
        public async Task SetUp()
        {
            A.CallTo(() => _vendorRepository.GetVendor(A<long>.Ignored))
                .Returns(new VendorGetResult.FailureNotFound());
            A.CallTo(() => _vendorRepository.UpdateVendor(A<VendorUpdateCommand>.Ignored))
                .Returns(new VendorUpdateResult.FailureNotExists());
            A.CallTo(() => _vendorRepository.DeleteVendor(A<long>.Ignored))
                .Returns(new VendorDeleteResult.FailureNotExists());

            using var client = SetUpClient();
            _getResponse = await client.GetAsync("/v3/vendors/1");
            _updateResponse = await client.PutAsync(
                "/v3/vendors/1",
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
            _deleteResponse = await client.DeleteAsync("/v3/vendors/1");
        }

        [TearDown]
        public void TearDown()
        {
            _getResponse?.Dispose();
            _updateResponse?.Dispose();
            _deleteResponse?.Dispose();
        }

        [Test]
        public async Task It_returns_the_not_found_contract_for_get_by_id() =>
            await _getResponse.ShouldBeProblemDetailAsync(
                HttpStatusCode.NotFound,
                "urn:ed-fi:api:not-found",
                "Not Found",
                "Vendor 1 not found. It may have been recently deleted."
            );

        [Test]
        public async Task It_returns_the_not_found_contract_for_update() =>
            await _updateResponse.ShouldBeProblemDetailAsync(
                HttpStatusCode.NotFound,
                "urn:ed-fi:api:not-found",
                "Not Found",
                "Vendor 1 not found. It may have been recently deleted."
            );

        [Test]
        public async Task It_returns_the_not_found_contract_for_delete() =>
            await _deleteResponse.ShouldBeProblemDetailAsync(
                HttpStatusCode.NotFound,
                "urn:ed-fi:api:not-found",
                "Not Found",
                "Vendor 1 not found. It may have been recently deleted."
            );
    }

    /// <summary>
    /// A vendor update succeeds but the follow-up identity-provider client namespace-claim update fails.
    /// The endpoint must return the fixed 502 bad-gateway contract and never surface the raw provider
    /// message (which can carry provider URLs and status detail).
    /// </summary>
    [TestFixture]
    public class Given_A_Vendor_Update_Whose_Client_Namespace_Update_Fails_At_The_Identity_Provider
        : VendorModuleTests
    {
        private const string SensitiveProviderMessage =
            "Keycloak returned 401 from https://idp.internal/realms/edfi/clients: invalid_grant secret=hunter2";

        private HttpResponseMessage _updateResponse = null!;

        [SetUp]
        public async Task SetUp()
        {
            // The vendor row updates successfully and reports one affected client, so the endpoint then
            // calls the identity provider to update that client's namespace claim.
            A.CallTo(() => _vendorRepository.UpdateVendor(A<VendorUpdateCommand>.Ignored))
                .Returns(new VendorUpdateResult.Success([Guid.NewGuid()]));
            A.CallTo(() =>
                    _clientRepository.UpdateClientNamespaceClaimAsync(A<string>.Ignored, A<string>.Ignored)
                )
                .Returns(
                    new ClientUpdateResult.FailureIdentityProvider(
                        new IdentityProviderError.Unreachable(SensitiveProviderMessage)
                    )
                );

            using var client = SetUpClient();
            _updateResponse = await client.PutAsync(
                "/v3/vendors/1",
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
        }

        [TearDown]
        public void TearDown() => _updateResponse?.Dispose();

        [Test]
        public async Task It_returns_the_bad_gateway_contract()
        {
            JsonNode body = await _updateResponse.ShouldBeProblemDetailAsync(
                HttpStatusCode.BadGateway,
                "urn:ed-fi:api:bad-gateway",
                "Bad Gateway",
                "The request could not be processed. See 'errors' for details.",
                errors: ["Identity provider error during client update"]
            );

            // The raw provider message and its embedded detail must never be surfaced to the caller.
            string raw = body.ToJsonString();
            raw.Should().NotContain(SensitiveProviderMessage);
            raw.Should().NotContain("idp.internal");
            raw.Should().NotContain("hunter2");
        }
    }

    /// <summary>
    /// A vendor update succeeds but the identity provider reports no such client for an affected client
    /// that exists in the configuration store. That is an upstream inconsistency, so the endpoint must
    /// return the fixed 502 bad-gateway contract (not an internal 500) and never surface the raw provider
    /// message.
    /// </summary>
    [TestFixture]
    public class Given_A_Vendor_Update_Whose_Client_Is_Missing_At_The_Identity_Provider : VendorModuleTests
    {
        private const string SensitiveProviderMessage = "client 9f3c not found in realm edfi at idp.internal";

        private HttpResponseMessage _updateResponse = null!;

        [SetUp]
        public async Task SetUp()
        {
            // The vendor row updates successfully and reports one affected client, so the endpoint then
            // calls the identity provider to update that client's namespace claim.
            A.CallTo(() => _vendorRepository.UpdateVendor(A<VendorUpdateCommand>.Ignored))
                .Returns(new VendorUpdateResult.Success([Guid.NewGuid()]));
            A.CallTo(() =>
                    _clientRepository.UpdateClientNamespaceClaimAsync(A<string>.Ignored, A<string>.Ignored)
                )
                .Returns(new ClientUpdateResult.FailureNotFound(SensitiveProviderMessage));

            using var client = SetUpClient();
            _updateResponse = await client.PutAsync(
                "/v3/vendors/1",
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
        }

        [TearDown]
        public void TearDown() => _updateResponse?.Dispose();

        [Test]
        public async Task It_returns_the_bad_gateway_contract()
        {
            JsonNode body = await _updateResponse.ShouldBeProblemDetailAsync(
                HttpStatusCode.BadGateway,
                "urn:ed-fi:api:bad-gateway",
                "Bad Gateway",
                "The request could not be processed. See 'errors' for details.",
                errors: ["Identity provider client not found during client update"]
            );

            // The raw provider message must never be surfaced to the caller.
            string raw = body.ToJsonString();
            raw.Should().NotContain(SensitiveProviderMessage);
            raw.Should().NotContain("idp.internal");
        }
    }
}
