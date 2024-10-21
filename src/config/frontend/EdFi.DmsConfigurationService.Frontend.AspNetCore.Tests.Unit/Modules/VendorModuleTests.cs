// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Security.Claims;
using System.Text;
using EdFi.DmsConfigurationService.Backend;
using EdFi.DmsConfigurationService.DataModel;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Modules;

public class VendorModuleTests
{
    private readonly IRepository<Vendor> _repository = A.Fake<IRepository<Vendor>>();

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

                    collection.AddTransient((_) => _repository);
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
            A.CallTo(() => _repository.AddAsync(A<Vendor>.Ignored))
                .Returns(new InsertResult.InsertSuccess(1));

            A.CallTo(() => _repository.GetAllAsync())
                .Returns(
                    new GetResult<Vendor>.GetSuccess(
                        [
                            new Vendor()
                            {
                                Id = 1,
                                Company = "Test Company",
                                NamespacePrefixes = ["Test Prefix"],
                            },
                        ]
                    )
                );

            A.CallTo(() => _repository.GetByIdAsync(A<long>.Ignored))
                .Returns(
                    new GetResult<Vendor>.GetByIdSuccess(
                        new Vendor()
                        {
                            Id = 1,
                            Company = "Test Company",
                            NamespacePrefixes = ["Test Prefix"],
                        }
                    )
                );

            A.CallTo(() => _repository.UpdateAsync(A<Vendor>.Ignored))
                .Returns(new UpdateResult.UpdateSuccess(1));

            A.CallTo(() => _repository.DeleteAsync(A<long>.Ignored))
                .Returns(new DeleteResult.DeleteSuccess(1));
        }

        [Test]
        public async Task Should_return_proper_success_responses()
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
                      "namespacePrefixes": [
                          "Test"
                      ]
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
                        "namespacePrefixes": [
                            "Test"
                        ]
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

            string invalidBody = """
                {
                  "id": 1,
                  "company": "012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789",
                  "contactName": "012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789",
                  "contactEmailAddress": "INVALID",
                  "namespacePrefixes": [
                      "0123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789"
                  ]
                }
                """;

            //Act
            var addResponse = await client.PostAsync(
                "/v2/vendors",
                new StringContent(invalidBody, Encoding.UTF8, "application/json")
            );

            var updateResponse = await client.PutAsync(
                "/v2/vendors/1",
                new StringContent(invalidBody, Encoding.UTF8, "application/json")
            );

            //Assert
            string expectedResponse =
                @"{""title"":""Validation failed"",""errors"":{""Company"":[""The length of \u0027Company\u0027 must be 256 characters or fewer. You entered 300 characters.""],""ContactName"":[""The length of \u0027Contact Name\u0027 must be 128 characters or fewer. You entered 300 characters.""],""ContactEmailAddress"":[""\u0027Contact Email Address\u0027 is not a valid email address.""],""NamespacePrefixes[0]"":[""The length of \u0027Namespace Prefixes\u0027 must be 128 characters or fewer. You entered 130 characters.""]}}";
            string addResponseContent = await addResponse.Content.ReadAsStringAsync();
            addResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            addResponseContent.Should().Contain(expectedResponse);

            string updateResponseContent = await updateResponse.Content.ReadAsStringAsync();
            updateResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            updateResponseContent.Should().Contain(expectedResponse);
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
                        "namespacePrefixes": [
                            "Test"
                        ]
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
            A.CallTo(() => _repository.GetByIdAsync(A<long>.Ignored))
                .Returns(new GetResult<Vendor>.GetByIdFailureNotExists());

            A.CallTo(() => _repository.UpdateAsync(A<Vendor>.Ignored))
                .Returns(new UpdateResult.UpdateFailureNotExists());

            A.CallTo(() => _repository.DeleteAsync(A<long>.Ignored))
                .Returns(new DeleteResult.DeleteFailureNotExists());
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
                        "namespacePrefixes": [
                            "Test"
                        ]
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
        public async Task Should_return_not_found_when_id_not_number()
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
                        "namespacePrefixes": [
                            "Test"
                        ]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );
            var deleteResponse = await client.DeleteAsync("/v2/vendors/c");

            //Assert
            getByIdResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
            updateResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
            deleteResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
    }

    [TestFixture]
    public class FailureUnknownTests : VendorModuleTests
    {
        [SetUp]
        public void SetUp()
        {
            A.CallTo(() => _repository.AddAsync(A<Vendor>.Ignored))
                .Returns(new InsertResult.UnknownFailure(""));

            A.CallTo(() => _repository.GetAllAsync()).Returns(new GetResult<Vendor>.UnknownFailure(""));

            A.CallTo(() => _repository.GetByIdAsync(A<long>.Ignored))
                .Returns(new GetResult<Vendor>.UnknownFailure(""));

            A.CallTo(() => _repository.UpdateAsync(A<Vendor>.Ignored))
                .Returns(new UpdateResult.UnknownFailure(""));

            A.CallTo(() => _repository.DeleteAsync(A<long>.Ignored))
                .Returns(new DeleteResult.UnknownFailure(""));
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
                      "namespacePrefixes": [
                          "Test"
                      ]
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
                        "namespacePrefixes": [
                            "Test"
                        ]
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
            A.CallTo(() => _repository.AddAsync(A<Vendor>.Ignored)).Returns(new InsertResult());

            A.CallTo(() => _repository.GetAllAsync()).Returns(new GetResult<Vendor>());

            A.CallTo(() => _repository.GetByIdAsync(A<long>.Ignored)).Returns(new GetResult<Vendor>());

            A.CallTo(() => _repository.UpdateAsync(A<Vendor>.Ignored)).Returns(new UpdateResult());

            A.CallTo(() => _repository.DeleteAsync(A<long>.Ignored)).Returns(new DeleteResult());
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
                      "namespacePrefixes": [
                          "Test"
                      ]
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
                        "namespacePrefixes": [
                            "Test"
                        ]
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
        private readonly IVendorRepository _vendorRepository = A.Fake<IVendorRepository>();

        private HttpClient SetUpIVendorClient()
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

                        collection.AddTransient<IVendorRepository>((_) => _vendorRepository);
                    }
                );
            });
            return factory.CreateClient();
        }

        [SetUp]
        public void SetUp()
        {
            A.CallTo(() => _vendorRepository.GetApplicationsByVendorIdAsync(A<long>.Ignored))
                .Returns(
                    new GetResult<Application>.GetSuccess(
                        new List<Application>
                        {
                            new()
                            {
                                Id = 1,
                                ApplicationName = "App 1",
                                ClaimSetName = "Name",
                                VendorId = 1,
                                EducationOrganizationIds = [1]
                            },
                            new()
                            {
                                Id = 2,
                                ApplicationName = "App 2",
                                ClaimSetName = "Name",
                                VendorId = 1,
                                EducationOrganizationIds = [1]
                            }
                        }
                    )
                );
        }

        [Test]
        public async Task Should_get_a_list_of_applications_by_vendor_id()
        {
            // Arrange
            using var client = SetUpIVendorClient();

            // Act
            var response = await client.GetAsync("/v2/vendors/1/applications");

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            string responseContent = await response.Content.ReadAsStringAsync();
            responseContent.Should().Contain("App 1");
            responseContent.Should().Contain("App 2");
        }

        [Test]
        public async Task Should_return_not_found_if_there_are_no_applications_related_to_vendor()
        {
            // Arrange
            using var client = SetUpIVendorClient();

            A.CallTo(() => _vendorRepository.GetApplicationsByVendorIdAsync(A<long>.Ignored))
                .Returns(new GetResult<Application>.GetSuccess(new List<Application>()));

            // Act
            var response = await client.GetAsync("/v2/vendors/2/applications");

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
    }
}
