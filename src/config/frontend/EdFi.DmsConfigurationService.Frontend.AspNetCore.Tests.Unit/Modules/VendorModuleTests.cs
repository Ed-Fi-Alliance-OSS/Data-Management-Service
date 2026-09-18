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
    private readonly RecordingLockManager _lockManager = new();
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
                        .AddTransient((_) => _identityProviderRepository)
                        .AddSingleton<IApplicationLockManager>(_lockManager);
                }
            );
        });
        _factoryTracker.Track(factory);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Scope", AuthorizationScopes.AdminScope.Name);
        return client;
    }

    private static async Task AssertLockConflictContract(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        JsonNode actualResponse = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        string correlationId = actualResponse["correlationId"]!.GetValue<string>();
        correlationId.Should().NotBeNullOrWhiteSpace();
        JsonNode expectedResponse = JsonNode.Parse(
            """
            {
              "detail": "Unable to process the request due to a concurrent modification. Retry the request.",
              "type": "urn:ed-fi:api:conflict",
              "title": "Conflict",
              "status": 409,
              "correlationId": "{correlationId}",
              "validationErrors": {},
              "errors": []
            }
            """.Replace("{correlationId}", correlationId)
        )!;
        JsonNode.DeepEquals(actualResponse, expectedResponse).Should().Be(true);
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
                .Returns(new VendorUpdateResult.Success());

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
        protected sealed record ProviderCall(string TargetedUuid, Guid ReportedUuid, string Prefixes);

        protected sealed record SyncCall(int ApiClientId, Guid ExpectedUuid, Guid NewUuid);

        private readonly object _stateLock = new();

        protected const string StoredPrefixes = "uri://old.org";
        protected const string RequestedPrefixes = "uri://old.org,uri://new.org";

        protected Dictionary<int, Guid> _storedUuids = [];
        protected List<ProviderCall> _providerCalls = [];
        protected List<SyncCall> _syncCalls = [];
        protected List<string> _deletedClientUuids = [];
        protected VendorApiClient[] _clients = [];

        /// <summary>The claim updates that carry the newly requested prefixes.</summary>
        protected List<ProviderCall> UpdateCalls =>
            [.. _providerCalls.Where(call => call.Prefixes == RequestedPrefixes)];

        /// <summary>The claim updates that put the vendor's stored prefixes back.</summary>
        protected List<ProviderCall> RollbackCalls =>
            [.. _providerCalls.Where(call => call.Prefixes == StoredPrefixes)];

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
            _deletedClientUuids = [];
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
                                StoredPrefixes,
                                _clients
                            )
                        )
                    )
                );

            A.CallTo(() => _vendorRepository.UpdateVendor(A<VendorUpdateCommand>.Ignored))
                .Returns(new VendorUpdateResult.Success());

            A.CallTo(() =>
                    _identityProviderRepository.UpdateClientNamespaceClaimAsync(
                        A<string>.Ignored,
                        A<string>.Ignored
                    )
                )
                .ReturnsLazily(call =>
                    RecordProviderCall(call.GetArgument<string>(0)!, call.GetArgument<string>(1)!)
                );

            A.CallTo(() => _identityProviderRepository.DeleteClientAsync(A<string>.Ignored))
                .ReturnsLazily(call =>
                {
                    lock (_stateLock)
                    {
                        _deletedClientUuids.Add(call.GetArgument<string>(0)!);
                    }

                    return Task.FromResult<ClientDeleteResult>(new ClientDeleteResult.Success());
                });

            A.CallTo(() =>
                    _apiClientRepository.SyncApiClientUuid(A<int>.Ignored, A<Guid>.Ignored, A<Guid>.Ignored)
                )
                .ReturnsLazily(call =>
                    RecordSync(call.GetArgument<int>(0), call.GetArgument<Guid>(1), call.GetArgument<Guid>(2))
                );
        }

        private Task<ClientUpdateResult> RecordProviderCall(string targetedUuid, string prefixes)
        {
            Guid reportedUuid = _rotateClientUuids ? Guid.NewGuid() : Guid.Parse(targetedUuid);
            lock (_stateLock)
            {
                _providerCalls.Add(new ProviderCall(targetedUuid, reportedUuid, prefixes));
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

        /// <summary>
        /// Every client the request changed carries the vendor's stored prefixes again, and no
        /// row was left pointing at a client the rollback replaced.
        /// </summary>
        protected void AssertClientsRestored(params VendorApiClient[] clients)
        {
            foreach (VendorApiClient client in clients)
            {
                RollbackCalls
                    .Should()
                    .Contain(
                        call => call.ReportedUuid == _storedUuids[client.Id],
                        $"ApiClient {client.Id} should have been restored and its row synchronized"
                    );
            }
        }

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
            // Only the claim update fails; putting the stored prefixes back still works, so the
            // failing client's own state is provably restored.
            A.CallTo(() =>
                    _identityProviderRepository.UpdateClientNamespaceClaimAsync(
                        _clients[1].ClientUuid.ToString(),
                        RequestedPrefixes
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
        public void It_stops_before_the_third_client() =>
            UpdateCalls.Should().NotContain(call => call.TargetedUuid == _clients[2].ClientUuid.ToString());

        [Test]
        public void It_restores_the_ambiguous_client_and_the_one_already_changed() =>
            RollbackCalls
                .Select(call => call.TargetedUuid)
                .Should()
                .BeEquivalentTo(_clients[1].ClientUuid.ToString(), _clients[0].ClientUuid.ToString());

        [Test]
        public void It_does_not_update_the_vendor() =>
            A.CallTo(() => _vendorRepository.UpdateVendor(A<VendorUpdateCommand>.Ignored))
                .MustNotHaveHappened();
    }

    [TestFixture]
    public class Given_a_provider_failure_whose_own_rollback_cannot_be_proven : VendorNamespaceUpdateTestBase
    {
        [SetUp]
        public async Task Act()
        {
            // The provider refuses every call for this client, so whether its claim changed is
            // unknown rather than known wrong.
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
        public void It_keeps_the_original_classification() =>
            _response.StatusCode.Should().Be(HttpStatusCode.BadGateway);

        [Test]
        public void It_still_restores_the_client_it_had_already_changed() =>
            RollbackCalls
                .Select(call => call.TargetedUuid)
                .Should()
                .Contain(_clients[0].ClientUuid.ToString());

        [Test]
        public void It_does_not_update_the_vendor() =>
            A.CallTo(() => _vendorRepository.UpdateVendor(A<VendorUpdateCommand>.Ignored))
                .MustNotHaveHappened();
    }

    [TestFixture]
    public class Given_a_failed_rollback_of_a_known_mutated_client : VendorNamespaceUpdateTestBase
    {
        [SetUp]
        public async Task Act()
        {
            // The second client's update fails, and restoring the FIRST client — which this
            // request definitely changed — also fails. That is a known inconsistency, so the
            // upstream classification is replaced by the sanitized server error.
            A.CallTo(() =>
                    _identityProviderRepository.UpdateClientNamespaceClaimAsync(
                        _clients[1].ClientUuid.ToString(),
                        RequestedPrefixes
                    )
                )
                .Returns(
                    new ClientUpdateResult.FailureIdentityProvider(
                        new IdentityProviderError.Unreachable("keycloak is unreachable")
                    )
                );
            A.CallTo(() =>
                    _identityProviderRepository.UpdateClientNamespaceClaimAsync(
                        _clients[0].ClientUuid.ToString(),
                        StoredPrefixes
                    )
                )
                .Returns(new ClientUpdateResult.FailureUnknown("the rollback was rejected"));

            using var client = SetUpClient();
            await ActUpdateAsync(client);
        }

        [Test]
        public void It_downgrades_to_a_sanitized_server_error() =>
            _response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        [Test]
        public async Task It_does_not_leak_the_rollback_message() =>
            (await _response.Content.ReadAsStringAsync()).Should().NotContain("the rollback was rejected");

        [Test]
        public void It_still_restored_the_ambiguous_client() =>
            RollbackCalls
                .Select(call => call.TargetedUuid)
                .Should()
                .Contain(_clients[1].ClientUuid.ToString());
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
        public void It_never_recreates_the_missing_client() =>
            RollbackCalls.Should().NotContain(call => call.TargetedUuid == _clients[1].ClientUuid.ToString());

        [Test]
        public void It_restores_the_client_it_had_already_changed() =>
            RollbackCalls
                .Select(call => call.TargetedUuid)
                .Should()
                .Contain(_clients[0].ClientUuid.ToString());

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
        public void It_stops_before_the_third_client() =>
            UpdateCalls.Should().NotContain(call => call.TargetedUuid == _clients[2].ClientUuid.ToString());

        [Test]
        public void It_restores_the_client_it_mutated_at_the_provider() =>
            RollbackCalls
                .Select(call => call.TargetedUuid)
                .Should()
                .Contain(UpdateCalls[1].ReportedUuid.ToString());

        [Test]
        public void It_does_not_overwrite_the_newer_writer() =>
            _storedUuids[_clients[1].Id].Should().NotBe(UpdateCalls[1].ReportedUuid);

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
        public void It_applied_every_provider_claim() => UpdateCalls.Should().HaveCount(3);

        [Test]
        public void It_restores_every_client_it_changed() => RollbackCalls.Should().HaveCount(3);
    }

    /// <summary>
    /// Records every acquisition in order — each lock set contributes its application ids in the
    /// order the module requested them — and hands out one handle per acquisition that remembers
    /// its disposal, so a fixture can assert the order the aggregate locks were requested in, how
    /// many sessions the workflow took, and that every one was released. Acquisition outcomes are
    /// scripted per acquisition position.
    /// </summary>
    private sealed class RecordingLockManager : IApplicationLockManager
    {
        private int _acquisitions;

        public List<int> AcquiredApplicationIds { get; } = [];

        public List<RecordingLockHandle> Handles { get; } = [];

        /// <summary>
        /// Reads the number of identity-provider calls made so far, captured by each handle when
        /// it is released, so a fixture can prove compensation ran before the locks were let go.
        /// </summary>
        public Func<int>? ProviderCallCount { get; set; }

        /// <summary>
        /// One-based acquisition position whose outcome is replaced, and the result to serve.
        /// </summary>
        public int ScriptedPosition { get; set; }

        public ApplicationLockResult? ScriptedResult { get; set; }

        public Exception? ScriptedException { get; set; }

        public Action? OnAcquire { get; set; }

        public void Reset()
        {
            _acquisitions = 0;
            AcquiredApplicationIds.Clear();
            Handles.Clear();
            ScriptedPosition = 0;
            ScriptedResult = null;
            ScriptedException = null;
            OnAcquire = null;
        }

        public Task<ApplicationLockResult> AcquireAsync(
            int applicationId,
            CancellationToken cancellationToken
        ) => AcquireAllAsync([applicationId], cancellationToken);

        public Task<ApplicationLockResult> AcquireAllAsync(
            IReadOnlyCollection<int> applicationIds,
            CancellationToken cancellationToken
        )
        {
            int position = Interlocked.Increment(ref _acquisitions);
            AcquiredApplicationIds.AddRange(applicationIds);
            OnAcquire?.Invoke();

            if (position == ScriptedPosition)
            {
                if (ScriptedException is not null)
                {
                    throw ScriptedException;
                }

                if (ScriptedResult is not null)
                {
                    return Task.FromResult(ScriptedResult);
                }
            }

            var handle = new RecordingLockHandle(ProviderCallCount);
            Handles.Add(handle);
            return Task.FromResult<ApplicationLockResult>(new ApplicationLockResult.Acquired(handle));
        }
    }

    private sealed class RecordingLockHandle(Func<int>? providerCallCount) : IAsyncDisposable
    {
        public bool Disposed { get; private set; }

        public int ProviderCallsWhenReleased { get; private set; } = -1;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            ProviderCallsWhenReleased = providerCallCount?.Invoke() ?? -1;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// A vendor whose clients are returned in a deliberately non-ascending application order, so
    /// an implementation that locked in the order the clients arrive rather than in ascending
    /// application id order is caught.
    /// </summary>
    public abstract class VendorLockTestBase : VendorNamespaceUpdateTestBase
    {
        protected const int LowerApplicationId = 10;
        protected const int HigherApplicationId = 30;
        protected const int DriftedApplicationId = 40;

        /// <summary>
        /// Enough applications that a per-application connection cost would have been refused
        /// under the fan-out cap this workflow used to carry.
        /// </summary>
        protected const int ManyApplications = 40;

        [SetUp]
        public void SetUpLockDefaults()
        {
            _lockManager.Reset();
            _lockManager.ProviderCallCount = () => _providerCalls.Count;
            _clients =
            [
                new VendorApiClient(53, "client-53", Guid.NewGuid(), HigherApplicationId),
                new VendorApiClient(51, "client-51", Guid.NewGuid(), LowerApplicationId),
                new VendorApiClient(52, "client-52", Guid.NewGuid(), HigherApplicationId),
            ];
            _storedUuids = _clients.ToDictionary(client => client.Id, client => client.ClientUuid);
        }

        /// <summary>
        /// Gives the vendor one client in each of <paramref name="applicationCount" /> distinct
        /// applications.
        /// </summary>
        protected void SpreadClientsAcrossApplications(int applicationCount)
        {
            _clients =
            [
                .. Enumerable
                    .Range(1, applicationCount)
                    .Select(offset => new VendorApiClient(
                        100 + offset,
                        $"client-{100 + offset}",
                        Guid.NewGuid(),
                        100 + offset
                    )),
            ];
            _storedUuids = _clients.ToDictionary(client => client.Id, client => client.ClientUuid);
        }

        protected void AssertEveryLockReleased() =>
            _lockManager.Handles.Should().OnlyContain(handle => handle.Disposed);

        /// <summary>
        /// Every owning application was requested in one lock set, in ascending order, so the
        /// workflow took exactly one lock session however many applications it spans.
        /// </summary>
        protected void AssertOneLockSetOverEveryApplication()
        {
            _lockManager.Handles.Should().HaveCount(1);
            _lockManager
                .AcquiredApplicationIds.Should()
                .Equal(_clients.Select(client => client.ApplicationId).Distinct().Order());
        }

        /// <summary>
        /// A company and contact edit that leaves the namespace prefixes exactly as stored.
        /// </summary>
        protected async Task ActCompanyContactEditAsync(HttpClient client) =>
            _response = await client.PutAsync(
                "/v3/vendors/1",
                new StringContent(
                    $$"""
                    {
                        "id": 1,
                        "company": "Renamed Company",
                        "contactName": "Renamed Contact",
                        "contactEmailAddress": "renamed@test.com",
                        "namespacePrefixes": "{{StoredPrefixes}}"
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );

        /// <summary>
        /// Compensation is only safe while the aggregates are still serialized, so every
        /// identity-provider call must precede the release of the locks.
        /// </summary>
        protected void AssertCompensationRanUnderTheLocks() =>
            _lockManager
                .Handles.Should()
                .OnlyContain(handle => handle.ProviderCallsWhenReleased == _providerCalls.Count);
    }

    [TestFixture]
    public class Given_a_vendor_update_with_clients_across_applications : VendorLockTestBase
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
        public void It_acquires_one_lock_set_in_ascending_application_order()
        {
            _lockManager.Handles.Should().HaveCount(1);
            _lockManager.AcquiredApplicationIds.Should().Equal(LowerApplicationId, HigherApplicationId);
        }

        [Test]
        public void It_releases_every_lock() => AssertEveryLockReleased();

        [Test]
        public void It_updates_every_client() => _providerCalls.Should().HaveCount(3);
    }

    [TestFixture]
    public class Given_a_vendor_update_for_a_vendor_owning_no_clients_at_all : VendorLockTestBase
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
        public void It_acquires_no_lock() => _lockManager.AcquiredApplicationIds.Should().BeEmpty();
    }

    [TestFixture]
    public class Given_a_vendor_update_whose_under_lock_state_differs : VendorLockTestBase
    {
        private Guid[] _underLockUuids = [];

        [SetUp]
        public async Task Act()
        {
            // Another workflow committed new client UUIDs for the same applications while this
            // request waited for the locks. The reread is authoritative, so the provider must be
            // addressed with the committed UUIDs, never the ones read before the locks.
            VendorApiClient[] preReadClients = _clients;
            VendorApiClient[] underLockClients =
            [
                .. preReadClients.Select(client => client with { ClientUuid = Guid.NewGuid() }),
            ];
            _underLockUuids = [.. underLockClients.Select(client => client.ClientUuid)];
            _storedUuids = underLockClients.ToDictionary(client => client.Id, client => client.ClientUuid);

            bool firstRead = true;
            A.CallTo(() => _vendorRepository.GetVendorUpdateState(A<int>.Ignored))
                .ReturnsLazily(_ =>
                {
                    VendorApiClient[] clients = firstRead ? preReadClients : underLockClients;
                    firstRead = false;
                    return Task.FromResult<VendorUpdateStateResult>(
                        new VendorUpdateStateResult.Success(
                            new VendorUpdateState(
                                "Test Company",
                                "Test",
                                "test@test.com",
                                StoredPrefixes,
                                clients
                            )
                        )
                    );
                });

            using var client = SetUpClient();
            await ActUpdateAsync(client);
        }

        [Test]
        public void It_returns_no_content() => _response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        [Test]
        public void It_targets_the_uuids_read_under_the_locks() =>
            _providerCalls
                .Select(call => call.TargetedUuid)
                .Should()
                .BeEquivalentTo(_underLockUuids.Select(uuid => uuid.ToString()));
    }

    [TestFixture]
    public class Given_a_vendor_update_whose_application_set_drifts_once : VendorLockTestBase
    {
        private int _stateReads;

        [SetUp]
        public async Task Act()
        {
            // One fixture instance serves every test in the class, so the read counter is reset
            // for each run rather than carried over from the previous one.
            _stateReads = 0;
            VendorApiClient[] driftedClients =
            [
                .. _clients,
                new VendorApiClient(54, "client-54", Guid.NewGuid(), DriftedApplicationId),
            ];
            _storedUuids = driftedClients.ToDictionary(client => client.Id, client => client.ClientUuid);

            A.CallTo(() => _vendorRepository.GetVendorUpdateState(A<int>.Ignored))
                .ReturnsLazily(_ =>
                {
                    _stateReads++;
                    // The second read — the one under the first set of locks — reports an
                    // application the request never locked, so the attempt must be abandoned.
                    // Every read from then on keeps that application, so the retry has to lock
                    // the new set rather than simply see the drift disappear.
                    VendorApiClient[] clients = _stateReads >= 2 ? driftedClients : _clients;
                    return Task.FromResult<VendorUpdateStateResult>(
                        new VendorUpdateStateResult.Success(
                            new VendorUpdateState(
                                "Test Company",
                                "Test",
                                "test@test.com",
                                StoredPrefixes,
                                clients
                            )
                        )
                    );
                });

            using var client = SetUpClient();
            await ActUpdateAsync(client);
        }

        [Test]
        public void It_returns_no_content() => _response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        [Test]
        public void It_retries_against_the_drifted_application_set() =>
            _lockManager
                .AcquiredApplicationIds.Should()
                .Equal(
                    LowerApplicationId,
                    HigherApplicationId,
                    LowerApplicationId,
                    HigherApplicationId,
                    DriftedApplicationId
                );

        [Test]
        public void It_takes_one_lock_set_per_attempt() => _lockManager.Handles.Should().HaveCount(2);

        [Test]
        public void It_updates_the_client_of_the_newly_locked_application() =>
            UpdateCalls.Should().HaveCount(4);

        [Test]
        public void It_releases_every_lock() => AssertEveryLockReleased();
    }

    [TestFixture]
    public class Given_a_vendor_update_whose_application_set_keeps_drifting : VendorLockTestBase
    {
        private int _stateReads;

        [SetUp]
        public async Task Act()
        {
            _stateReads = 0;
            A.CallTo(() => _vendorRepository.GetVendorUpdateState(A<int>.Ignored))
                .ReturnsLazily(_ =>
                {
                    _stateReads++;
                    // Every reread reports one more application than the pre-read did.
                    VendorApiClient[] clients =
                        _stateReads % 2 == 0
                            ?
                            [
                                .. _clients,
                                new VendorApiClient(
                                    60 + _stateReads,
                                    $"client-{60 + _stateReads}",
                                    Guid.NewGuid(),
                                    40 + _stateReads
                                ),
                            ]
                            : _clients;
                    return Task.FromResult<VendorUpdateStateResult>(
                        new VendorUpdateStateResult.Success(
                            new VendorUpdateState(
                                "Test Company",
                                "Test",
                                "test@test.com",
                                StoredPrefixes,
                                clients
                            )
                        )
                    );
                });

            using var client = SetUpClient();
            await ActUpdateAsync(client);
        }

        [Test]
        public async Task It_returns_the_retriable_conflict_contract() =>
            await AssertLockConflictContract(_response);

        [Test]
        public void It_gives_up_after_three_attempts()
        {
            _lockManager.Handles.Should().HaveCount(3);
            _lockManager.AcquiredApplicationIds.Should().HaveCount(6);
        }

        [Test]
        public void It_releases_every_lock() => AssertEveryLockReleased();

        [Test]
        public void It_mutates_nothing() => AssertNoProviderCallOrVendorUpdate();
    }

    /// <summary>
    /// A vendor whose clients span many applications. The lock set costs one database session
    /// however many applications it spans, so there is no fan-out cap: the request runs the
    /// normal workflow over every client and commits the vendor row once.
    /// </summary>
    [TestFixture]
    public class Given_a_vendor_update_spanning_many_applications : VendorLockTestBase
    {
        [SetUp]
        public async Task Act()
        {
            SpreadClientsAcrossApplications(ManyApplications);
            using var client = SetUpClient();
            await ActUpdateAsync(client);
        }

        [Test]
        public void It_returns_no_content() => _response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        [Test]
        public void It_acquires_every_application_as_one_lock_set() => AssertOneLockSetOverEveryApplication();

        [Test]
        public void It_releases_the_lock_set() => AssertEveryLockReleased();

        [Test]
        public void It_updates_every_client() => UpdateCalls.Should().HaveCount(ManyApplications);

        [Test]
        public void It_persists_every_client_uuid() =>
            _syncCalls.Select(call => call.ApiClientId).Should().BeEquivalentTo(_clients.Select(c => c.Id));

        [Test]
        public void It_commits_the_vendor_row() =>
            A.CallTo(() => _vendorRepository.UpdateVendor(A<VendorUpdateCommand>.Ignored)).MustHaveHappened();
    }

    /// <summary>
    /// A company and contact edit of a vendor spanning many applications, with the prefixes
    /// unchanged. Under the fan-out cap such an edit was refused outright. It now runs the normal
    /// workflow, and the provider is still re-synchronized for every client: an unchanged
    /// prefix value proves nothing about the provider, which may be recovering from a failed
    /// compensation (D-3).
    /// </summary>
    [TestFixture]
    public class Given_a_company_contact_edit_of_a_vendor_spanning_many_applications : VendorLockTestBase
    {
        [SetUp]
        public async Task Act()
        {
            SpreadClientsAcrossApplications(ManyApplications);
            using var client = SetUpClient();
            await ActCompanyContactEditAsync(client);
        }

        [Test]
        public void It_returns_no_content() => _response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        [Test]
        public void It_acquires_every_application_as_one_lock_set() => AssertOneLockSetOverEveryApplication();

        [Test]
        public void It_releases_the_lock_set() => AssertEveryLockReleased();

        [Test]
        public void It_still_resynchronizes_every_client_with_the_stored_prefixes() =>
            _providerCalls
                .Should()
                .HaveCount(ManyApplications)
                .And.OnlyContain(call => call.Prefixes == StoredPrefixes);

        [Test]
        public void It_commits_the_company_and_contact_change() =>
            A.CallTo(() =>
                    _vendorRepository.UpdateVendor(
                        A<VendorUpdateCommand>.That.Matches(command =>
                            command.Company == "Renamed Company"
                            && command.ContactName == "Renamed Contact"
                            && command.NamespacePrefixes == StoredPrefixes
                        )
                    )
                )
                .MustHaveHappened();
    }

    [TestFixture]
    public class Given_a_vendor_update_whose_lock_set_times_out : VendorLockTestBase
    {
        [SetUp]
        public async Task Act()
        {
            _lockManager.ScriptedPosition = 1;
            _lockManager.ScriptedResult = new ApplicationLockResult.FailureTimeout();

            using var client = SetUpClient();
            await ActUpdateAsync(client);
        }

        [Test]
        public async Task It_returns_the_retriable_conflict_contract() =>
            await AssertLockConflictContract(_response);

        [Test]
        public void It_requested_the_whole_set_in_ascending_order() =>
            _lockManager.AcquiredApplicationIds.Should().Equal(LowerApplicationId, HigherApplicationId);

        [Test]
        public void It_holds_no_lock() => _lockManager.Handles.Should().BeEmpty();

        [Test]
        public void It_mutates_nothing() => AssertNoProviderCallOrVendorUpdate();
    }

    [TestFixture]
    public class Given_a_vendor_update_whose_lock_infrastructure_fails : VendorLockTestBase
    {
        [SetUp]
        public async Task Act()
        {
            _lockManager.ScriptedPosition = 1;
            _lockManager.ScriptedResult = new ApplicationLockResult.FailureUnknown(
                "the lock connection dropped"
            );

            using var client = SetUpClient();
            await ActUpdateAsync(client);
        }

        [Test]
        public void It_returns_a_sanitized_server_error() =>
            _response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        [Test]
        public async Task It_does_not_leak_the_failure_message() =>
            (await _response.Content.ReadAsStringAsync()).Should().NotContain("the lock connection dropped");

        [Test]
        public void It_holds_no_lock() => _lockManager.Handles.Should().BeEmpty();

        [Test]
        public void It_mutates_nothing() => AssertNoProviderCallOrVendorUpdate();
    }

    [TestFixture]
    public class Given_a_vendor_update_whose_lock_acquisition_throws : VendorLockTestBase
    {
        [SetUp]
        public async Task Act()
        {
            _lockManager.ScriptedPosition = 1;
            _lockManager.ScriptedException = new OperationCanceledException(
                "the application lock acquisition was cancelled"
            );

            using var client = SetUpClient();
            await ActUpdateAsync(client);
        }

        [Test]
        public void It_does_not_return_success() =>
            _response.StatusCode.Should().NotBe(HttpStatusCode.NoContent);

        [Test]
        public void It_holds_no_lock() => _lockManager.Handles.Should().BeEmpty();

        [Test]
        public void It_mutates_nothing() => AssertNoProviderCallOrVendorUpdate();
    }

    [TestFixture]
    public class Given_a_vendor_update_whose_under_lock_reread_fails : VendorLockTestBase
    {
        [SetUp]
        public async Task Act()
        {
            bool firstRead = true;
            A.CallTo(() => _vendorRepository.GetVendorUpdateState(A<int>.Ignored))
                .ReturnsLazily(_ =>
                {
                    if (firstRead)
                    {
                        firstRead = false;
                        return Task.FromResult<VendorUpdateStateResult>(
                            new VendorUpdateStateResult.Success(
                                new VendorUpdateState(
                                    "Test Company",
                                    "Test",
                                    "test@test.com",
                                    StoredPrefixes,
                                    _clients
                                )
                            )
                        );
                    }

                    return Task.FromResult<VendorUpdateStateResult>(
                        new VendorUpdateStateResult.FailureNotExists()
                    );
                });

            using var client = SetUpClient();
            await ActUpdateAsync(client);
        }

        [Test]
        public void It_returns_not_found() => _response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        [Test]
        public void It_releases_every_lock() => AssertEveryLockReleased();

        [Test]
        public void It_mutates_nothing() => AssertNoProviderCallOrVendorUpdate();
    }

    [TestFixture]
    public class Given_a_vendor_update_that_fails_at_the_provider_under_lock : VendorLockTestBase
    {
        [SetUp]
        public async Task Act()
        {
            A.CallTo(() =>
                    _identityProviderRepository.UpdateClientNamespaceClaimAsync(
                        A<string>.Ignored,
                        A<string>.Ignored
                    )
                )
                .Returns(new ClientUpdateResult.FailureUnknown("the provider rejected the update"));

            using var client = SetUpClient();
            await ActUpdateAsync(client);
        }

        [Test]
        public void It_returns_a_sanitized_server_error() =>
            _response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        [Test]
        public void It_still_releases_every_lock() => AssertEveryLockReleased();
    }

    [TestFixture]
    public class Given_a_safe_to_delete_sync_for_a_rotated_uuid : VendorNamespaceUpdateTestBase
    {
        [SetUp]
        public async Task Act()
        {
            _rotateClientUuids = true;
            // The row vanished and nothing references the client the provider created for it.
            A.CallTo(() =>
                    _apiClientRepository.SyncApiClientUuid(_clients[1].Id, A<Guid>.Ignored, A<Guid>.Ignored)
                )
                .Returns(new ApiClientUuidSyncResult.FailureNotExistsSafeToDelete());

            using var client = SetUpClient();
            await ActUpdateAsync(client);
        }

        [Test]
        public void It_returns_a_sanitized_server_error() =>
            _response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        [Test]
        public void It_deletes_the_replacement_client_rather_than_orphaning_it() =>
            _deletedClientUuids.Should().Equal(UpdateCalls[1].ReportedUuid.ToString());

        [Test]
        public void It_restores_the_client_it_had_already_changed() =>
            RollbackCalls
                .Select(call => call.TargetedUuid)
                .Should()
                .Contain(UpdateCalls[0].ReportedUuid.ToString());
    }

    [TestFixture]
    public class Given_a_safe_to_delete_sync_for_a_stable_uuid : VendorNamespaceUpdateTestBase
    {
        [SetUp]
        public async Task Act()
        {
            // The provider preserved the client's identity, so there is no replacement to
            // delete: deleting here would destroy the client the row used to point at.
            A.CallTo(() =>
                    _apiClientRepository.SyncApiClientUuid(_clients[1].Id, A<Guid>.Ignored, A<Guid>.Ignored)
                )
                .Returns(new ApiClientUuidSyncResult.FailureNotExistsSafeToDelete());

            using var client = SetUpClient();
            await ActUpdateAsync(client);
        }

        [Test]
        public void It_returns_a_sanitized_server_error() =>
            _response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        [Test]
        public void It_deletes_nothing() => _deletedClientUuids.Should().BeEmpty();

        [Test]
        public void It_restores_the_claim_of_the_client_whose_row_vanished() =>
            RollbackCalls
                .Select(call => call.TargetedUuid)
                .Should()
                .Contain(_clients[1].ClientUuid.ToString());
    }

    [TestFixture]
    public class Given_a_referenced_missing_row_sync : VendorNamespaceUpdateTestBase
    {
        [SetUp]
        public async Task Act()
        {
            _rotateClientUuids = true;
            // The row is gone but another row still references the reported client, so deleting
            // it would destroy a client that is in use.
            A.CallTo(() =>
                    _apiClientRepository.SyncApiClientUuid(_clients[1].Id, A<Guid>.Ignored, A<Guid>.Ignored)
                )
                .Returns(new ApiClientUuidSyncResult.FailureNotExists());

            using var client = SetUpClient();
            await ActUpdateAsync(client);
        }

        [Test]
        public void It_returns_a_sanitized_server_error() =>
            _response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        [Test]
        public void It_deletes_nothing() => _deletedClientUuids.Should().BeEmpty();
    }

    [TestFixture]
    public class Given_an_unknown_sync_failure : VendorNamespaceUpdateTestBase
    {
        [SetUp]
        public async Task Act()
        {
            A.CallTo(() =>
                    _apiClientRepository.SyncApiClientUuid(_clients[1].Id, A<Guid>.Ignored, A<Guid>.Ignored)
                )
                .Returns(new ApiClientUuidSyncResult.FailureUnknown("the row could not be written"));

            using var client = SetUpClient();
            await ActUpdateAsync(client);
        }

        [Test]
        public void It_returns_a_sanitized_server_error() =>
            _response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        [Test]
        public async Task It_does_not_leak_the_failure_message() =>
            (await _response.Content.ReadAsStringAsync()).Should().NotContain("the row could not be written");

        [Test]
        public void It_restores_both_touched_clients() =>
            RollbackCalls
                .Select(call => call.TargetedUuid)
                .Should()
                .BeEquivalentTo(_clients[1].ClientUuid.ToString(), _clients[0].ClientUuid.ToString());
    }

    /// <summary>
    /// A provider that replaces the client reports the replacement before anything has re-pointed
    /// the row at it. When that forward sync fails, the client the provider now serves is the
    /// replacement while the row still holds the UUID from the snapshot, so the rollback has to
    /// address the replacement and guard against the stored UUID. Addressing the stored UUID
    /// would restore a client this request never mutated; guarding with the replacement would
    /// classify a provider restore that worked as a stale-state inconsistency.
    /// </summary>
    [TestFixture]
    public class Given_a_rotated_client_whose_forward_sync_fails : VendorNamespaceUpdateTestBase
    {
        [SetUp]
        public async Task Act()
        {
            _rotateClientUuids = true;
            // Only the forward sync is scripted; the rollback's own sync falls back to the
            // fixture's guarded store, so whether the compensation is accepted is that store's
            // verdict rather than a scripted one.
            A.CallTo(() =>
                    _apiClientRepository.SyncApiClientUuid(_clients[0].Id, A<Guid>.Ignored, A<Guid>.Ignored)
                )
                .Returns(new ApiClientUuidSyncResult.FailureUnknown("the row could not be written"))
                .Once();

            using var client = SetUpClient();
            await ActUpdateAsync(client);
        }

        [Test]
        public void It_returns_a_sanitized_server_error() =>
            _response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        [Test]
        public void It_restores_the_replacement_client_the_request_actually_mutated() =>
            RollbackCalls
                .Select(call => call.TargetedUuid)
                .Should()
                .Equal(UpdateCalls[0].ReportedUuid.ToString());

        [Test]
        public void It_guards_the_rollback_sync_with_the_uuid_the_row_still_holds() =>
            _syncCalls
                .Should()
                .Equal(new SyncCall(_clients[0].Id, _clients[0].ClientUuid, RollbackCalls[0].ReportedUuid));

        [Test]
        public void It_leaves_the_row_pointing_at_the_client_that_carries_the_stored_prefixes() =>
            _storedUuids[_clients[0].Id].Should().Be(RollbackCalls[0].ReportedUuid);

        [Test]
        public void It_stops_before_the_remaining_clients() => UpdateCalls.Should().HaveCount(1);

        [Test]
        public void It_deletes_nothing() => _deletedClientUuids.Should().BeEmpty();

        [Test]
        public void It_does_not_update_the_vendor() =>
            A.CallTo(() => _vendorRepository.UpdateVendor(A<VendorUpdateCommand>.Ignored))
                .MustNotHaveHappened();
    }

    [TestFixture]
    public class Given_an_unrecognized_provider_result : VendorNamespaceUpdateTestBase
    {
        [SetUp]
        public async Task Act()
        {
            // A future result variant this workflow has never seen must not fall through to
            // success.
            A.CallTo(() =>
                    _identityProviderRepository.UpdateClientNamespaceClaimAsync(
                        _clients[1].ClientUuid.ToString(),
                        RequestedPrefixes
                    )
                )
                .Returns(new ClientUpdateResult());

            using var client = SetUpClient();
            await ActUpdateAsync(client);
        }

        [Test]
        public void It_returns_a_sanitized_server_error() =>
            _response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        [Test]
        public void It_restores_the_client_it_had_already_changed() =>
            RollbackCalls
                .Select(call => call.TargetedUuid)
                .Should()
                .Contain(_clients[0].ClientUuid.ToString());

        [Test]
        public void It_does_not_update_the_vendor() =>
            A.CallTo(() => _vendorRepository.UpdateVendor(A<VendorUpdateCommand>.Ignored))
                .MustNotHaveHappened();
    }

    [TestFixture]
    public class Given_a_thrown_provider_call : VendorNamespaceUpdateTestBase
    {
        [SetUp]
        public async Task Act()
        {
            A.CallTo(() =>
                    _identityProviderRepository.UpdateClientNamespaceClaimAsync(
                        _clients[1].ClientUuid.ToString(),
                        RequestedPrefixes
                    )
                )
                .Throws(new InvalidOperationException("the provider connection dropped"));

            using var client = SetUpClient();
            await ActUpdateAsync(client);
        }

        [Test]
        public void It_returns_a_sanitized_server_error() =>
            _response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        [Test]
        public void It_restores_the_ambiguous_client_and_the_one_already_changed() =>
            RollbackCalls
                .Select(call => call.TargetedUuid)
                .Should()
                .BeEquivalentTo(_clients[1].ClientUuid.ToString(), _clients[0].ClientUuid.ToString());
    }

    [TestFixture]
    public class Given_a_vanished_vendor_whose_rollback_fails : VendorNamespaceUpdateTestBase
    {
        [SetUp]
        public async Task Act()
        {
            A.CallTo(() => _vendorRepository.UpdateVendor(A<VendorUpdateCommand>.Ignored))
                .Returns(new VendorUpdateResult.FailureNotExists());
            A.CallTo(() =>
                    _identityProviderRepository.UpdateClientNamespaceClaimAsync(
                        _clients[0].ClientUuid.ToString(),
                        StoredPrefixes
                    )
                )
                .Returns(new ClientUpdateResult.FailureUnknown("the rollback was rejected"));

            using var client = SetUpClient();
            await ActUpdateAsync(client);
        }

        [Test]
        public void It_replaces_the_not_found_with_a_sanitized_server_error() =>
            _response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        // The rejected rollback is stubbed ahead of the recorder, so it never reaches the
        // recorded list; the two that did reach it prove the loop kept going.
        [Test]
        public void It_continues_restoring_after_the_rejection() =>
            RollbackCalls
                .Select(call => call.TargetedUuid)
                .Should()
                .BeEquivalentTo(_clients[2].ClientUuid.ToString(), _clients[1].ClientUuid.ToString());

        [Test]
        public void It_attempted_the_client_whose_rollback_was_rejected() =>
            A.CallTo(() =>
                    _identityProviderRepository.UpdateClientNamespaceClaimAsync(
                        _clients[0].ClientUuid.ToString(),
                        StoredPrefixes
                    )
                )
                .MustHaveHappened();
    }

    [TestFixture]
    public class Given_a_vanished_vendor_whose_provider_clients_are_already_gone
        : VendorNamespaceUpdateTestBase
    {
        [SetUp]
        public async Task Act()
        {
            // The vendor and its applications cascaded away, so every provider client this
            // request updated has already been removed with them. That is the expected end
            // state, not a failure.
            A.CallTo(() => _vendorRepository.UpdateVendor(A<VendorUpdateCommand>.Ignored))
                .Returns(new VendorUpdateResult.FailureNotExists());
            A.CallTo(() =>
                    _identityProviderRepository.UpdateClientNamespaceClaimAsync(
                        A<string>.Ignored,
                        StoredPrefixes
                    )
                )
                .Returns(new ClientUpdateResult.FailureNotFound("Client not found"));

            using var client = SetUpClient();
            await ActUpdateAsync(client);
        }

        [Test]
        public void It_returns_not_found() => _response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [TestFixture]
    public class Given_a_compensating_vendor_update : VendorLockTestBase
    {
        [SetUp]
        public async Task Act()
        {
            A.CallTo(() =>
                    _identityProviderRepository.UpdateClientNamespaceClaimAsync(
                        _clients[2].ClientUuid.ToString(),
                        RequestedPrefixes
                    )
                )
                .Returns(new ClientUpdateResult.FailureUnknown("the provider rejected the update"));

            using var client = SetUpClient();
            await ActUpdateAsync(client);
        }

        [Test]
        public void It_returns_a_sanitized_server_error() =>
            _response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        [Test]
        public void It_compensates_before_releasing_the_locks() => AssertCompensationRanUnderTheLocks();

        [Test]
        public void It_releases_every_lock_afterwards() => AssertEveryLockReleased();
    }

    [TestFixture]
    public class Given_a_vendor_update_whose_under_lock_reread_throws : VendorLockTestBase
    {
        [SetUp]
        public async Task Act()
        {
            bool firstRead = true;
            A.CallTo(() => _vendorRepository.GetVendorUpdateState(A<int>.Ignored))
                .ReturnsLazily(_ =>
                {
                    if (!firstRead)
                    {
                        throw new InvalidOperationException("the state read connection dropped");
                    }

                    firstRead = false;
                    return Task.FromResult<VendorUpdateStateResult>(
                        new VendorUpdateStateResult.Success(
                            new VendorUpdateState(
                                "Test Company",
                                "Test",
                                "test@test.com",
                                StoredPrefixes,
                                _clients
                            )
                        )
                    );
                });

            using var client = SetUpClient();
            await ActUpdateAsync(client);
        }

        [Test]
        public void It_returns_a_sanitized_server_error() =>
            _response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        [Test]
        public async Task It_does_not_leak_the_failure_message() =>
            (await _response.Content.ReadAsStringAsync())
                .Should()
                .NotContain("the state read connection dropped");

        [Test]
        public void It_releases_every_lock_it_held() => AssertEveryLockReleased();

        [Test]
        public void It_mutates_nothing() => AssertNoProviderCallOrVendorUpdate();
    }

    /// <summary>
    /// The vendor vanished under the locks and the provider replaces the client on every call,
    /// so each rollback produces a replacement whose row no longer exists. Whether that
    /// replacement can be removed decides whether the outcome is clean.
    /// </summary>
    public abstract class VanishedVendorWithReplacementTestBase : VendorNamespaceUpdateTestBase
    {
        [SetUp]
        public void SetUpVanishedVendorWithReplacement()
        {
            _rotateClientUuids = true;

            A.CallTo(() => _vendorRepository.UpdateVendor(A<VendorUpdateCommand>.Ignored))
                .Returns(new VendorUpdateResult.FailureNotExists());

            // The forward pass persists normally; every rollback then finds the row gone, with
            // its replacement provably unreferenced.
            A.CallTo(() =>
                    _apiClientRepository.SyncApiClientUuid(A<int>.Ignored, A<Guid>.Ignored, A<Guid>.Ignored)
                )
                .ReturnsLazily(call =>
                {
                    Guid expectedUuid = call.GetArgument<Guid>(1);
                    bool isRollback = Array.TrueForAll(
                        _clients,
                        candidate => candidate.ClientUuid != expectedUuid
                    );
                    return Task.FromResult<ApiClientUuidSyncResult>(
                        isRollback
                            ? new ApiClientUuidSyncResult.FailureNotExistsSafeToDelete()
                            : new ApiClientUuidSyncResult.Success()
                    );
                });
        }
    }

    [TestFixture]
    public class Given_a_vanished_vendor_whose_replacement_client_cannot_be_deleted
        : VanishedVendorWithReplacementTestBase
    {
        [SetUp]
        public async Task Act()
        {
            A.CallTo(() => _identityProviderRepository.DeleteClientAsync(A<string>.Ignored))
                .ReturnsLazily(call =>
                {
                    _deletedClientUuids.Add(call.GetArgument<string>(0)!);
                    return Task.FromResult<ClientDeleteResult>(
                        new ClientDeleteResult.FailureUnknown("the delete was rejected")
                    );
                });

            using var client = SetUpClient();
            await ActUpdateAsync(client);
        }

        [Test]
        public void It_refuses_the_clean_not_found() =>
            _response.StatusCode.Should().NotBe(HttpStatusCode.NotFound);

        [Test]
        public void It_returns_a_sanitized_server_error() =>
            _response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        [Test]
        public async Task It_does_not_leak_the_delete_message() =>
            (await _response.Content.ReadAsStringAsync()).Should().NotContain("the delete was rejected");

        [Test]
        public void It_attempted_to_remove_every_replacement() => _deletedClientUuids.Should().HaveCount(3);
    }

    [TestFixture]
    public class Given_a_vanished_vendor_whose_replacement_client_delete_throws
        : VanishedVendorWithReplacementTestBase
    {
        [SetUp]
        public async Task Act()
        {
            A.CallTo(() => _identityProviderRepository.DeleteClientAsync(A<string>.Ignored))
                .Throws(new InvalidOperationException("the provider connection dropped"));

            using var client = SetUpClient();
            await ActUpdateAsync(client);
        }

        [Test]
        public void It_returns_a_sanitized_server_error() =>
            _response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
    }

    [TestFixture]
    public class Given_a_vanished_vendor_whose_replacement_client_is_deleted
        : VanishedVendorWithReplacementTestBase
    {
        [SetUp]
        public async Task Act()
        {
            using var client = SetUpClient();
            await ActUpdateAsync(client);
        }

        [Test]
        public void It_returns_not_found() => _response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        [Test]
        public void It_removes_every_replacement_client() =>
            _deletedClientUuids
                .Should()
                .BeEquivalentTo(RollbackCalls.Select(call => call.ReportedUuid.ToString()));
    }

    /// <summary>
    /// The repository update returns an unknown failure or throws, so whether it committed is
    /// unknown and only the authoritative reread can classify it. The state read is scripted:
    /// the first two reads are the pre-lock read and the reread under the locks, and the third
    /// is the resolution.
    /// </summary>
    public abstract class AmbiguousVendorUpdateTestBase : VendorNamespaceUpdateTestBase
    {
        /// <summary>The scalars the request body carries, as <see cref="ActUpdateAsync"/> sends them.</summary>
        protected const string RequestedCompany = "Test 11";
        protected const string RequestedContactEmail = "test@gmail.com";

        protected void ScriptStateReads(params Func<VendorUpdateStateResult>[] reads)
        {
            int read = 0;
            A.CallTo(() => _vendorRepository.GetVendorUpdateState(A<int>.Ignored))
                .ReturnsLazily(_ =>
                {
                    Func<VendorUpdateStateResult> next = reads[Math.Min(read, reads.Length - 1)];
                    read++;
                    return Task.FromResult(next());
                });
        }

        protected VendorUpdateStateResult OriginalState() =>
            new VendorUpdateStateResult.Success(
                new VendorUpdateState("Test Company", "Test", "test@test.com", StoredPrefixes, _clients)
            );

        protected VendorUpdateStateResult CommittedState() =>
            new VendorUpdateStateResult.Success(
                new VendorUpdateState(
                    RequestedCompany,
                    "Test",
                    RequestedContactEmail,
                    // The same set the request asked for, in the other order, because the stored
                    // order carries no meaning.
                    "uri://new.org,uri://old.org",
                    _clients
                )
            );

        protected void FailTheRepositoryUpdate() =>
            A.CallTo(() => _vendorRepository.UpdateVendor(A<VendorUpdateCommand>.Ignored))
                .Returns(new VendorUpdateResult.FailureUnknown("the commit outcome is unknown"));
    }

    [TestFixture]
    public class Given_an_ambiguous_repository_update_that_committed : AmbiguousVendorUpdateTestBase
    {
        [SetUp]
        public async Task Act()
        {
            FailTheRepositoryUpdate();
            ScriptStateReads(OriginalState, OriginalState, CommittedState);

            using var client = SetUpClient();
            await ActUpdateAsync(client);
        }

        [Test]
        public void It_recovers_the_success() => _response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        [Test]
        public void It_leaves_the_updated_clients_alone() => RollbackCalls.Should().BeEmpty();
    }

    [TestFixture]
    public class Given_an_ambiguous_repository_update_that_did_not_commit : AmbiguousVendorUpdateTestBase
    {
        [SetUp]
        public async Task Act()
        {
            FailTheRepositoryUpdate();
            ScriptStateReads(OriginalState);

            using var client = SetUpClient();
            await ActUpdateAsync(client);
        }

        [Test]
        public void It_returns_a_sanitized_server_error() =>
            _response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        [Test]
        public async Task It_does_not_leak_the_failure_message() =>
            (await _response.Content.ReadAsStringAsync())
                .Should()
                .NotContain("the commit outcome is unknown");

        [Test]
        public void It_restores_every_client_it_changed() => RollbackCalls.Should().HaveCount(3);
    }

    [TestFixture]
    public class Given_a_thrown_repository_update : AmbiguousVendorUpdateTestBase
    {
        [SetUp]
        public async Task Act()
        {
            A.CallTo(() => _vendorRepository.UpdateVendor(A<VendorUpdateCommand>.Ignored))
                .Throws(new InvalidOperationException("the commit connection dropped"));
            ScriptStateReads(OriginalState);

            using var client = SetUpClient();
            await ActUpdateAsync(client);
        }

        [Test]
        public void It_enters_the_same_resolution_as_a_returned_failure() =>
            _response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        [Test]
        public void It_restores_every_client_it_changed() => RollbackCalls.Should().HaveCount(3);
    }

    [TestFixture]
    public class Given_a_thrown_repository_update_that_committed : AmbiguousVendorUpdateTestBase
    {
        [SetUp]
        public async Task Act()
        {
            A.CallTo(() => _vendorRepository.UpdateVendor(A<VendorUpdateCommand>.Ignored))
                .Throws(new InvalidOperationException("the commit connection dropped"));
            ScriptStateReads(OriginalState, OriginalState, CommittedState);

            using var client = SetUpClient();
            await ActUpdateAsync(client);
        }

        [Test]
        public void It_recovers_the_success() => _response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        [Test]
        public void It_leaves_the_updated_clients_alone() => RollbackCalls.Should().BeEmpty();
    }

    [TestFixture]
    public class Given_an_unrecognized_repository_update_result : AmbiguousVendorUpdateTestBase
    {
        [SetUp]
        public async Task Act()
        {
            // A future result variant this workflow has never seen must not fall through to
            // success without the reread proving the commit landed.
            A.CallTo(() => _vendorRepository.UpdateVendor(A<VendorUpdateCommand>.Ignored))
                .Returns(new VendorUpdateResult());
            ScriptStateReads(OriginalState);

            using var client = SetUpClient();
            await ActUpdateAsync(client);
        }

        [Test]
        public void It_returns_a_sanitized_server_error() =>
            _response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        [Test]
        public void It_restores_every_client_it_changed() => RollbackCalls.Should().HaveCount(3);
    }

    [TestFixture]
    public class Given_an_ambiguous_update_with_a_partially_matching_state : AmbiguousVendorUpdateTestBase
    {
        [SetUp]
        public async Task Act()
        {
            FailTheRepositoryUpdate();
            // The company was written but the prefixes were not, which matches neither the
            // command nor the original: nothing can be concluded, so nothing is compensated.
            ScriptStateReads(
                OriginalState,
                OriginalState,
                () =>
                    new VendorUpdateStateResult.Success(
                        new VendorUpdateState(
                            RequestedCompany,
                            "Test",
                            RequestedContactEmail,
                            StoredPrefixes,
                            _clients
                        )
                    )
            );

            using var client = SetUpClient();
            await ActUpdateAsync(client);
        }

        [Test]
        public void It_returns_a_sanitized_server_error() =>
            _response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        [Test]
        public void It_does_not_guess_at_compensation() => RollbackCalls.Should().BeEmpty();
    }

    [TestFixture]
    public class Given_an_ambiguous_update_whose_vendor_vanished : AmbiguousVendorUpdateTestBase
    {
        [SetUp]
        public async Task Act()
        {
            FailTheRepositoryUpdate();
            ScriptStateReads(
                OriginalState,
                OriginalState,
                () => new VendorUpdateStateResult.FailureNotExists()
            );

            using var client = SetUpClient();
            await ActUpdateAsync(client);
        }

        [Test]
        public void It_returns_a_sanitized_server_error() =>
            _response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        [Test]
        public void It_still_restores_every_client_it_changed() => RollbackCalls.Should().HaveCount(3);
    }

    [TestFixture]
    public class Given_an_ambiguous_update_that_cannot_be_resolved : AmbiguousVendorUpdateTestBase
    {
        [SetUp]
        public async Task Act()
        {
            FailTheRepositoryUpdate();
            ScriptStateReads(
                OriginalState,
                OriginalState,
                () => new VendorUpdateStateResult.FailureUnknown("the resolution read failed")
            );

            using var client = SetUpClient();
            await ActUpdateAsync(client);
        }

        [Test]
        public void It_returns_a_sanitized_server_error() =>
            _response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        [Test]
        public async Task It_does_not_leak_the_resolution_message() =>
            (await _response.Content.ReadAsStringAsync()).Should().NotContain("the resolution read failed");

        [Test]
        public void It_does_not_guess_at_compensation() => RollbackCalls.Should().BeEmpty();
    }

    [TestFixture]
    public class Given_an_ambiguous_update_whose_resolution_throws : AmbiguousVendorUpdateTestBase
    {
        [SetUp]
        public async Task Act()
        {
            FailTheRepositoryUpdate();

            int read = 0;
            A.CallTo(() => _vendorRepository.GetVendorUpdateState(A<int>.Ignored))
                .ReturnsLazily(_ =>
                {
                    read++;
                    if (read > 2)
                    {
                        throw new InvalidOperationException("the resolution connection dropped");
                    }

                    return Task.FromResult(OriginalState());
                });

            using var client = SetUpClient();
            await ActUpdateAsync(client);
        }

        [Test]
        public void It_returns_a_sanitized_server_error() =>
            _response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        [Test]
        public async Task It_does_not_leak_the_failure_message() =>
            (await _response.Content.ReadAsStringAsync())
                .Should()
                .NotContain("the resolution connection dropped");

        [Test]
        public void It_does_not_guess_at_compensation() => RollbackCalls.Should().BeEmpty();
    }

    [TestFixture]
    public class Given_an_ambiguous_update_resolved_under_the_locks : VendorLockTestBase
    {
        [SetUp]
        public async Task Act()
        {
            A.CallTo(() => _vendorRepository.UpdateVendor(A<VendorUpdateCommand>.Ignored))
                .Returns(new VendorUpdateResult.FailureUnknown("the commit outcome is unknown"));

            using var client = SetUpClient();
            await ActUpdateAsync(client);
        }

        [Test]
        public void It_returns_a_sanitized_server_error() =>
            _response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        [Test]
        public void It_resolves_and_compensates_before_releasing_the_locks() =>
            AssertCompensationRanUnderTheLocks();

        [Test]
        public void It_releases_every_lock_afterwards() => AssertEveryLockReleased();
    }
}
