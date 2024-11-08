// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel;
using EdFi.DmsConfigurationService.DataModel.Application;
using EdFi.DmsConfigurationService.DataModel.Vendor;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Modules;

public class VendorModuleTests
{
    private readonly IVendorRepository _vendorRepository = A.Fake<IVendorRepository>();
    private readonly IApplicationRepository _applicationRepository = A.Fake<IApplicationRepository>();
    private readonly HttpContext _httpContext = A.Fake<HttpContext>();

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
                        options.AddPolicy(
                            SecurityConstants.ServicePolicy,
                            policy => policy.RequireClaim(ClaimTypes.Role, AuthenticationConstants.Role)
                        )
                    );
                    collection
                        .AddTransient((_) => _httpContext)
                        .AddTransient((_) => _vendorRepository)
                        .AddTransient((_) => _applicationRepository);
                }
            );
        });
        return factory.CreateClient();
    }

    [TestFixture]
    public class SuccessTests : VendorModuleTests
    {
        [SetUp]
        public void SetUp()
        {
            A.CallTo(() => _vendorRepository.InsertVendor(A<VendorInsertCommand>.Ignored))
                .Returns(new VendorInsertResult.Success(1));

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
                .Returns(new VendorUpdateResult.Success());

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
            var getResponse = await client.GetAsync("/v2/vendors");
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
                "/v2/vendors",
                new StringContent(invalidPostBody, Encoding.UTF8, "application/json")
            );

            var updateResponse = await client.PutAsync(
                "/v2/vendors/1",
                new StringContent(invalidPutBody, Encoding.UTF8, "application/json")
            );

            //Assert
            var actualPostResponse = JsonNode.Parse(await addResponse.Content.ReadAsStringAsync());
            var expectedPostResponse = JsonNode.Parse(
                """
                {
                  "detail": "",
                  "type": "urn:ed-fi:api:bad-request:data-validation-failed",
                  "title": "Data Validation Failed",
                  "status": 400,
                  "correlationId": "{correlationId}",
                  "validationErrors": {
                    "Company": [
                      "The length of 'Company' must be 256 characters or fewer. You entered 300 characters."
                    ],
                    "ContactName": [
                      "The length of 'Contact Name' must be 128 characters or fewer. You entered 300 characters."
                    ],
                    "ContactEmailAddress": [
                      "'Contact Email Address' is not a valid email address."
                    ],
                    "NamespacePrefixes": [
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
                  "detail": "",
                  "type": "urn:ed-fi:api:bad-request:data-validation-failed",
                  "title": "Data Validation Failed",
                  "status": 400,
                  "correlationId": "{correlationId}",
                  "validationErrors": {
                    "Company": [
                      "The length of 'Company' must be 256 characters or fewer. You entered 300 characters."
                    ],
                    "ContactName": [
                      "The length of 'Contact Name' must be 128 characters or fewer. You entered 300 characters."
                    ],
                    "ContactEmailAddress": [
                      "'Contact Email Address' is not a valid email address."
                    ],
                    "NamespacePrefixes": [
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
                "/v2/vendors/1",
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
            var getByIdResponse = await client.GetAsync("/v2/vendors/a");
            var updateResponse = await client.PutAsync(
                "/v2/vendors/b",
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
            var deleteResponse = await client.DeleteAsync("/v2/vendors/c");

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

            A.CallTo(() => _vendorRepository.QueryVendor(A<PagingQuery>.Ignored))
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
            var getResponse = await client.GetAsync("/v2/vendors");
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

            A.CallTo(() => _vendorRepository.QueryVendor(A<PagingQuery>.Ignored))
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
            var getResponse = await client.GetAsync("/v2/vendors");
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
                    new VendorApplicationsResult.Success(
                        [
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
                        ]
                    )
                );
        }

        [Test]
        public async Task Should_get_a_list_of_applications_by_vendor_id()
        {
            // Arrange
            using var client = SetUpClient();
            A.CallTo(() => _vendorRepository.GetVendorApplications(A<long>.Ignored))
                .Returns(
                    new VendorApplicationsResult.Success(
                        [
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
                        ]
                    )
                );

            // Act
            var response = await client.GetAsync("/v2/vendors/1/applications");

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
            var response = await client.GetAsync("/v2/vendors/2/applications");

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
            var response = await client.GetAsync("/v2/vendors/99/applications");

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
            string responseContent = await response.Content.ReadAsStringAsync();
            responseContent.Should().Contain("It may have been recently deleted.");
        }
    }
}
