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
    private readonly IApiClientRepository _apiClientRepository = A.Fake<IApiClientRepository>();
    private readonly IIdentityProviderRepository _identityProviderRepository =
        A.Fake<IIdentityProviderRepository>();
    private readonly HttpContext _httpContext = A.Fake<HttpContext>();
    private readonly WebApplicationFactoryTracker<Program> _factoryTracker = new();

    protected VendorModuleTests()
    {
        // A vendor with no clients keeps every pre-existing fixture exercising exactly the
        // repository result it arranges; the namespace-claim fixtures below supply their own.
        A.CallTo(() => _vendorRepository.GetVendorUpdateState(A<int>.Ignored))
            .Returns(
                new VendorUpdateStateResult.Success(
                    new VendorUpdateState("Test Company", "Test", "test@test.com", "Test Prefix", [])
                )
            );
    }

    [TearDown]
    public void DisposeWebApplicationFactories() => _factoryTracker.DisposeTrackedFactories();

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
                        .AddTransient((_) => _apiClientRepository)
                        .AddTransient((_) => _identityProviderRepository);
                }
            );
        });
        _factoryTracker.Track(factory);
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

            A.CallTo(() => _vendorRepository.GetVendor(A<int>.Ignored))
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

            A.CallTo(() => _vendorRepository.DeleteVendor(A<int>.Ignored))
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
        public async Task Should_return_bad_request_with_Name_field_key()
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

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            var doc = JsonNode.Parse(await response.Content.ReadAsStringAsync());
            doc!["validationErrors"]!
                ["Name"]
                .Should()
                .NotBeNull("field key must be 'Name' per existing contract");
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
                  "detail": "Data validation failed. See 'validationErrors' for details.",
                  "type": "urn:ed-fi:api:bad-request:data",
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

        [Test]
        public async Task Should_return_bad_request_when_vendor_body_id_is_omitted()
        {
            // Arrange
            using var client = SetUpClient();

            //Act: PUT with route id=1, body omits "id" (defaults to 0)
            var updateResponse = await client.PutAsync(
                "/v3/vendors/1",
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
            A.CallTo(() => _vendorRepository.GetVendor(A<int>.Ignored))
                .Returns(new VendorGetResult.FailureNotFound());

            A.CallTo(() => _vendorRepository.UpdateVendor(A<VendorUpdateCommand>.Ignored))
                .Returns(new VendorUpdateResult.FailureNotExists());

            A.CallTo(() => _vendorRepository.DeleteVendor(A<int>.Ignored))
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

            A.CallTo(() => _vendorRepository.GetVendor(A<int>.Ignored))
                .Returns(new VendorGetResult.FailureUnknown(""));

            A.CallTo(() => _vendorRepository.UpdateVendor(A<VendorUpdateCommand>.Ignored))
                .Returns(new VendorUpdateResult.FailureUnknown(""));

            A.CallTo(() => _vendorRepository.DeleteVendor(A<int>.Ignored))
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

            A.CallTo(() => _vendorRepository.GetVendor(A<int>.Ignored)).Returns(new VendorGetResult());

            A.CallTo(() => _vendorRepository.UpdateVendor(A<VendorUpdateCommand>.Ignored))
                .Returns(new VendorUpdateResult());

            A.CallTo(() => _vendorRepository.DeleteVendor(A<int>.Ignored)).Returns(new VendorDeleteResult());
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
    public class GetApplicationsByVendorIdTests : VendorModuleTests
    {
        [SetUp]
        public void SetUp()
        {
            A.CallTo(() => _vendorRepository.GetVendorApplications(A<int>.Ignored))
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
            A.CallTo(() => _vendorRepository.GetVendorApplications(A<int>.Ignored))
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

            A.CallTo(() => _vendorRepository.GetVendorApplications(A<int>.Ignored))
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

            A.CallTo(() => _vendorRepository.GetVendorApplications(A<int>.Ignored))
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

    /// <summary>
    /// Route binding at the edges of the int32 resource identifier. The upper bound must be usable,
    /// not merely parseable, and a value beyond it must be rejected before the repository is reached.
    /// There is deliberately no negative-id test: -1 binds to int perfectly well and VendorModule
    /// passes it straight through to the repository, so it 404s exactly as it did when the id was a
    /// long. Adding positive-id route validation would be a separate behavior change.
    /// </summary>
    // Instance per test case so each test gets its own repository fake: NUnit otherwise shares one
    // fixture instance across the fixture, and the accepted call below would leak into the
    // MustNotHaveHappened assertion of the rejection test.
    [TestFixture]
    [FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
    public class Given_an_id_at_the_int32_boundary : VendorModuleTests
    {
        [Test]
        public async Task It_accepts_an_id_at_int_MaxValue()
        {
            // Arrange
            A.CallTo(() => _vendorRepository.GetVendor(int.MaxValue))
                .Returns(
                    new VendorGetResult.Success(
                        new VendorResponse()
                        {
                            Id = int.MaxValue,
                            Company = "Test Company",
                            ContactEmailAddress = "test@test.com",
                            ContactName = "Test Contact",
                            NamespacePrefixes = "Test Prefix",
                        }
                    )
                );
            using var client = SetUpClient();

            // Act
            var response = await client.GetAsync($"/v3/vendors/{int.MaxValue}");

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            A.CallTo(() => _vendorRepository.GetVendor(int.MaxValue)).MustHaveHappenedOnceExactly();
        }

        [Test]
        public async Task It_rejects_an_id_above_int_MaxValue_with_400()
        {
            // Arrange
            using var client = SetUpClient();

            // Act
            // Before the narrowing this id bound successfully and answered 404. Rejecting it at the
            // binding layer is the observable behavior change the int32 contract introduces.
            var response = await client.GetAsync($"/v3/vendors/{(long)int.MaxValue + 1}");

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            A.CallTo(() => _vendorRepository.GetVendor(A<int>.Ignored)).MustNotHaveHappened();

            // The status is only half the change. No module runs, so the 400 originates in framework
            // route binding as a BadHttpRequestException; GlobalExceptionHandler.MapBadRequest is what
            // classifies an unbindable route value as parameter-level and gives it an Ed-Fi envelope.
            // Asserting the type keeps an out-of-range id from regressing to a bodiless framework 400
            // or to the generic bad-request classification.
            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
            body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:bad-request:parameter");
            body["status"]!.GetValue<int>().Should().Be(400);
        }
    }

    /// <summary>
    /// Arranges a vendor that owns three clients across two applications, with the identity
    /// provider and the guarded UUID synchronization served from one mutable client state, so a
    /// fixture can observe exactly which provider client each call targeted and which UUID was
    /// persisted to which row.
    /// </summary>
    public abstract class VendorNamespaceUpdateTestBase : VendorModuleTests
    {
        protected sealed record ProviderCall(string TargetedUuid, Guid ReportedUuid);

        protected sealed record SyncCall(int ApiClientId, Guid ExpectedUuid, Guid NewUuid);

        private readonly object _stateLock = new();

        protected Dictionary<int, Guid> _storedUuids = [];
        protected List<ProviderCall> _providerCalls = [];
        protected List<SyncCall> _syncCalls = [];
        protected VendorApiClient[] _clients = [];

        /// <summary>
        /// Models a provider that replaces the client on every namespace update, which is what
        /// Keycloak did before this ticket and what any future recreating provider would do.
        /// The workflow must persist whatever UUID comes back either way.
        /// </summary>
        protected bool _rotateClientUuids;

        protected HttpResponseMessage _response = null!;

        [SetUp]
        public void SetUpNamespaceUpdateDefaults()
        {
            _providerCalls = [];
            _syncCalls = [];
            _rotateClientUuids = false;

            _clients =
            [
                new VendorApiClient(51, "client-51", Guid.NewGuid(), 10),
                new VendorApiClient(52, "client-52", Guid.NewGuid(), 10),
                new VendorApiClient(53, "client-53", Guid.NewGuid(), 30),
            ];
            _storedUuids = _clients.ToDictionary(client => client.Id, client => client.ClientUuid);

            A.CallTo(() => _vendorRepository.GetVendorUpdateState(A<int>.Ignored))
                .ReturnsLazily(_ =>
                    Task.FromResult<VendorUpdateStateResult>(
                        new VendorUpdateStateResult.Success(
                            new VendorUpdateState(
                                "Test Company",
                                "Test",
                                "test@test.com",
                                "uri://old.org",
                                _clients
                            )
                        )
                    )
                );

            A.CallTo(() => _vendorRepository.UpdateVendor(A<VendorUpdateCommand>.Ignored))
                .Returns(new VendorUpdateResult.Success([]));

            A.CallTo(() =>
                    _identityProviderRepository.UpdateClientNamespaceClaimAsync(
                        A<string>.Ignored,
                        A<string>.Ignored
                    )
                )
                .ReturnsLazily(call => RecordProviderCall(call.GetArgument<string>(0)!));

            A.CallTo(() =>
                    _apiClientRepository.SyncApiClientUuid(A<int>.Ignored, A<Guid>.Ignored, A<Guid>.Ignored)
                )
                .ReturnsLazily(call =>
                    RecordSync(call.GetArgument<int>(0), call.GetArgument<Guid>(1), call.GetArgument<Guid>(2))
                );
        }

        private Task<ClientUpdateResult> RecordProviderCall(string targetedUuid)
        {
            Guid reportedUuid = _rotateClientUuids ? Guid.NewGuid() : Guid.Parse(targetedUuid);
            lock (_stateLock)
            {
                _providerCalls.Add(new ProviderCall(targetedUuid, reportedUuid));
            }

            return Task.FromResult<ClientUpdateResult>(new ClientUpdateResult.Success(reportedUuid));
        }

        private Task<ApiClientUuidSyncResult> RecordSync(int apiClientId, Guid expectedUuid, Guid newUuid)
        {
            lock (_stateLock)
            {
                _syncCalls.Add(new SyncCall(apiClientId, expectedUuid, newUuid));
                if (!_storedUuids.TryGetValue(apiClientId, out Guid storedUuid))
                {
                    return Task.FromResult<ApiClientUuidSyncResult>(
                        new ApiClientUuidSyncResult.FailureNotExistsSafeToDelete()
                    );
                }

                if (storedUuid == newUuid)
                {
                    return Task.FromResult<ApiClientUuidSyncResult>(
                        new ApiClientUuidSyncResult.AlreadyApplied()
                    );
                }

                if (storedUuid != expectedUuid)
                {
                    return Task.FromResult<ApiClientUuidSyncResult>(
                        new ApiClientUuidSyncResult.FailureStaleState()
                    );
                }

                _storedUuids[apiClientId] = newUuid;
                return Task.FromResult<ApiClientUuidSyncResult>(new ApiClientUuidSyncResult.Success());
            }
        }

        protected async Task ActUpdateAsync(HttpClient client) =>
            _response = await client.PutAsync(
                "/v3/vendors/1",
                new StringContent(
                    """
                    {
                        "id": 1,
                        "company": "Test 11",
                        "contactName": "Test",
                        "contactEmailAddress": "test@gmail.com",
                        "namespacePrefixes": "uri://old.org,uri://new.org"
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );

        protected void AssertNoProviderCallOrVendorUpdate()
        {
            _providerCalls.Should().BeEmpty();
            A.CallTo(() => _vendorRepository.UpdateVendor(A<VendorUpdateCommand>.Ignored))
                .MustNotHaveHappened();
        }
    }

    [TestFixture]
    public class Given_a_vendor_update_whose_provider_preserves_the_client_uuid
        : VendorNamespaceUpdateTestBase
    {
        [SetUp]
        public async Task Act()
        {
            using var client = SetUpClient();
            await ActUpdateAsync(client);
        }

        [Test]
        public void It_returns_no_content() => _response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        [Test]
        public void It_targets_every_resolved_client_exactly_once() =>
            _providerCalls
                .Select(call => call.TargetedUuid)
                .Should()
                .Equal(_clients.Select(client => client.ClientUuid.ToString()));

        [Test]
        public void It_persists_the_reported_uuid_for_every_client() =>
            _syncCalls
                .Should()
                .Equal(
                    _clients.Select(client => new SyncCall(client.Id, client.ClientUuid, client.ClientUuid))
                );

        [Test]
        public void It_leaves_the_stored_uuids_unchanged() =>
            _storedUuids
                .Should()
                .Equal(_clients.ToDictionary(client => client.Id, client => client.ClientUuid));
    }

    [TestFixture]
    public class Given_a_vendor_update_whose_provider_rotates_the_client_uuid : VendorNamespaceUpdateTestBase
    {
        [SetUp]
        public async Task Act()
        {
            _rotateClientUuids = true;
            using var client = SetUpClient();
            await ActUpdateAsync(client);
        }

        [Test]
        public void It_returns_no_content() => _response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        [Test]
        public void It_persists_every_rotated_uuid_against_its_resolved_predecessor() =>
            _syncCalls
                .Should()
                .Equal(
                    _clients.Select(
                        (client, index) =>
                            new SyncCall(client.Id, client.ClientUuid, _providerCalls[index].ReportedUuid)
                    )
                );

        [Test]
        public void It_stores_the_rotated_uuid_on_every_row() =>
            _storedUuids
                .Should()
                .Equal(
                    _clients
                        .Select((client, index) => (client.Id, _providerCalls[index].ReportedUuid))
                        .ToDictionary(pair => pair.Id, pair => pair.ReportedUuid)
                );

        [Test]
        public void It_never_leaves_a_row_pointing_at_a_replaced_client() =>
            _storedUuids.Values.Should().NotIntersectWith(_clients.Select(client => client.ClientUuid));
    }

    [TestFixture]
    public class Given_a_vendor_update_for_a_vendor_with_no_clients : VendorNamespaceUpdateTestBase
    {
        [SetUp]
        public async Task Act()
        {
            _clients = [];
            _storedUuids = [];
            using var client = SetUpClient();
            await ActUpdateAsync(client);
        }

        [Test]
        public void It_returns_no_content() => _response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        [Test]
        public void It_calls_the_identity_provider_for_nothing() => _providerCalls.Should().BeEmpty();

        // One fixture instance serves every test in the class, so the fakes accumulate calls
        // across them. The assertion is therefore that the vendor update was reached at all.
        [Test]
        public void It_still_updates_the_vendor() =>
            A.CallTo(() => _vendorRepository.UpdateVendor(A<VendorUpdateCommand>.Ignored)).MustHaveHappened();
    }

    [TestFixture]
    public class Given_a_vendor_update_for_a_missing_vendor : VendorNamespaceUpdateTestBase
    {
        [SetUp]
        public async Task Act()
        {
            A.CallTo(() => _vendorRepository.GetVendorUpdateState(A<int>.Ignored))
                .Returns(new VendorUpdateStateResult.FailureNotExists());

            using var client = SetUpClient();
            await ActUpdateAsync(client);
        }

        [Test]
        public void It_returns_not_found() => _response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        [Test]
        public void It_mutates_nothing() => AssertNoProviderCallOrVendorUpdate();
    }

    [TestFixture]
    public class Given_a_vendor_update_whose_state_read_fails : VendorNamespaceUpdateTestBase
    {
        [SetUp]
        public async Task Act()
        {
            A.CallTo(() => _vendorRepository.GetVendorUpdateState(A<int>.Ignored))
                .Returns(new VendorUpdateStateResult.FailureUnknown("connection reset by peer"));

            using var client = SetUpClient();
            await ActUpdateAsync(client);
        }

        [Test]
        public void It_returns_a_sanitized_server_error() =>
            _response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        [Test]
        public async Task It_does_not_leak_the_failure_message() =>
            (await _response.Content.ReadAsStringAsync()).Should().NotContain("connection reset by peer");

        [Test]
        public void It_mutates_nothing() => AssertNoProviderCallOrVendorUpdate();
    }

    [TestFixture]
    public class Given_a_vendor_update_with_an_invalid_body : VendorNamespaceUpdateTestBase
    {
        [SetUp]
        public async Task Act()
        {
            using var client = SetUpClient();
            _response = await client.PutAsync(
                "/v3/vendors/1",
                new StringContent(
                    """
                    {
                        "id": 1,
                        "company": "",
                        "contactName": "Test",
                        "contactEmailAddress": "test@gmail.com",
                        "namespacePrefixes": "uri://new.org"
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );
        }

        [Test]
        public void It_returns_bad_request() => _response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        [Test]
        public void It_never_reads_the_update_state() =>
            A.CallTo(() => _vendorRepository.GetVendorUpdateState(A<int>.Ignored)).MustNotHaveHappened();

        [Test]
        public void It_mutates_nothing() => AssertNoProviderCallOrVendorUpdate();
    }

    [TestFixture]
    public class Given_a_provider_failure_on_the_second_client : VendorNamespaceUpdateTestBase
    {
        [SetUp]
        public async Task Act()
        {
            A.CallTo(() =>
                    _identityProviderRepository.UpdateClientNamespaceClaimAsync(
                        _clients[1].ClientUuid.ToString(),
                        A<string>.Ignored
                    )
                )
                .Returns(
                    new ClientUpdateResult.FailureIdentityProvider(
                        new IdentityProviderError.Unreachable("keycloak is unreachable")
                    )
                );

            using var client = SetUpClient();
            await ActUpdateAsync(client);
        }

        [Test]
        public void It_returns_bad_gateway() => _response.StatusCode.Should().Be(HttpStatusCode.BadGateway);

        [Test]
        public async Task It_does_not_leak_the_provider_message() =>
            (await _response.Content.ReadAsStringAsync()).Should().NotContain("keycloak is unreachable");

        [Test]
        public void It_stops_before_the_third_client() => _providerCalls.Should().HaveCount(1);

        [Test]
        public void It_does_not_update_the_vendor() =>
            A.CallTo(() => _vendorRepository.UpdateVendor(A<VendorUpdateCommand>.Ignored))
                .MustNotHaveHappened();
    }

    [TestFixture]
    public class Given_a_missing_stored_client_on_the_second_client : VendorNamespaceUpdateTestBase
    {
        [SetUp]
        public async Task Act()
        {
            A.CallTo(() =>
                    _identityProviderRepository.UpdateClientNamespaceClaimAsync(
                        _clients[1].ClientUuid.ToString(),
                        A<string>.Ignored
                    )
                )
                .Returns(new ClientUpdateResult.FailureNotFound("Client not found"));

            using var client = SetUpClient();
            await ActUpdateAsync(client);
        }

        [Test]
        public void It_returns_a_sanitized_server_error() =>
            _response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        [Test]
        public void It_does_not_update_the_vendor() =>
            A.CallTo(() => _vendorRepository.UpdateVendor(A<VendorUpdateCommand>.Ignored))
                .MustNotHaveHappened();
    }

    [TestFixture]
    public class Given_a_stale_uuid_sync_on_the_second_client : VendorNamespaceUpdateTestBase
    {
        [SetUp]
        public async Task Act()
        {
            _rotateClientUuids = true;
            // A non-participating writer re-pointed the row between the snapshot and the sync.
            _storedUuids[_clients[1].Id] = Guid.NewGuid();

            using var client = SetUpClient();
            await ActUpdateAsync(client);
        }

        [Test]
        public void It_returns_a_sanitized_server_error() =>
            _response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        [Test]
        public void It_stops_before_the_third_client() => _providerCalls.Should().HaveCount(2);

        [Test]
        public void It_does_not_overwrite_the_newer_writer() =>
            _storedUuids[_clients[1].Id].Should().NotBe(_providerCalls[1].ReportedUuid);

        [Test]
        public void It_does_not_update_the_vendor() =>
            A.CallTo(() => _vendorRepository.UpdateVendor(A<VendorUpdateCommand>.Ignored))
                .MustNotHaveHappened();
    }

    [TestFixture]
    public class Given_a_vendor_that_vanishes_before_the_repository_update : VendorNamespaceUpdateTestBase
    {
        [SetUp]
        public async Task Act()
        {
            A.CallTo(() => _vendorRepository.UpdateVendor(A<VendorUpdateCommand>.Ignored))
                .Returns(new VendorUpdateResult.FailureNotExists());

            using var client = SetUpClient();
            await ActUpdateAsync(client);
        }

        [Test]
        public void It_returns_not_found() => _response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        [Test]
        public void It_still_applied_every_provider_claim() => _providerCalls.Should().HaveCount(3);
    }
}
