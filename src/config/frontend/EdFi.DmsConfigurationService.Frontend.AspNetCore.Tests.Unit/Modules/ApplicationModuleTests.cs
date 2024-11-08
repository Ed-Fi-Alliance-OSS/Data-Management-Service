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
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Modules;

[TestFixture]
public class ApplicationModuleTests
{
    private readonly IApplicationRepository _applicationRepository = A.Fake<IApplicationRepository>();
    private readonly IClientRepository _clientRepository = A.Fake<IClientRepository>();
    private readonly IVendorRepository _vendorRepository = A.Fake<IVendorRepository>();

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
                        .AddTransient((_) => _applicationRepository)
                        .AddTransient((_) => _clientRepository)
                        .AddTransient((_) => _vendorRepository);
                }
            );
        });
        return factory.CreateClient();
    }

    [TestFixture]
    public class SuccessTests : ApplicationModuleTests
    {
        [SetUp]
        public void Setup()
        {
            A.CallTo(
                    () =>
                        _applicationRepository.InsertApplication(
                            A<ApplicationInsertCommand>.Ignored,
                            A<ApiClientInsertCommand>.Ignored
                        )
                )
                .Returns(new ApplicationInsertResult.Success(1));

            A.CallTo(() => _applicationRepository.QueryApplication(A<PagingQuery>.Ignored))
                .Returns(
                    new ApplicationQueryResult.Success(
                        [
                            new ApplicationResponse()
                            {
                                Id = 1,
                                ApplicationName = "Test Application",
                                ClaimSetName = "ClaimSet",
                                VendorId = 1,
                                EducationOrganizationIds = [1],
                            },
                        ]
                    )
                );

            A.CallTo(() => _applicationRepository.GetApplication(A<long>.Ignored))
                .Returns(
                    new ApplicationGetResult.Success(
                        new ApplicationResponse()
                        {
                            Id = 1,
                            ApplicationName = "Test Application",
                            ClaimSetName = "ClaimSet",
                            VendorId = 1,
                            EducationOrganizationIds = [1],
                        }
                    )
                );

            A.CallTo(() => _applicationRepository.UpdateApplication(A<ApplicationUpdateCommand>.Ignored))
                .Returns(new ApplicationUpdateResult.Success());

            A.CallTo(() => _applicationRepository.DeleteApplication(A<long>.Ignored))
                .Returns(new ApplicationDeleteResult.Success());

            A.CallTo(() => _applicationRepository.GetApplicationApiClients(A<long>.Ignored))
                .Returns(new ApplicationApiClientsResult.Success([new("1", Guid.NewGuid())]));

            A.CallTo(
                    () =>
                        _clientRepository.CreateClientAsync(
                            A<string>.Ignored,
                            A<string>.Ignored,
                            A<string>.Ignored
                        )
                )
                .Returns(new ClientCreateResult.Success(Guid.NewGuid()));

            A.CallTo(() => _clientRepository.ResetCredentialsAsync(A<string>.Ignored))
                .Returns(new ClientResetResult.Success("SECRET"));
        }

        [Test]
        public async Task Should_return_success_response()
        {
            // Arrange
            using var client = SetUpClient();

            var addResponse = await client.PostAsync(
                "/v2/applications",
                new StringContent(
                    """
                    {
                      "ApplicationName": "Application 11",
                      "ClaimSetName": "Test",
                      "VendorId": 1,
                      "EducationOrganizationIds": [1]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );

            var getResponse = await client.GetAsync("/v2/applications");
            var getByIdResponse = await client.GetAsync("/v2/applications/1");
            var updateResponse = await client.PutAsync(
                "/v2/applications/1",
                new StringContent(
                    """
                    {
                       "id": 1,
                       "ApplicationName": "Application 11",
                        "ClaimSetName": "Test",
                        "VendorId": 1,
                        "EducationOrganizationIds": [1]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );
            var deleteResponse = await client.DeleteAsync("/v2/applications/1");
            var resetCredentialsResponse = await client.PutAsync("/v2/applications/1/reset-credential", null);

            //Assert
            addResponse.StatusCode.Should().Be(HttpStatusCode.Created);
            getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            getByIdResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            updateResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
            deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
            resetCredentialsResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }

    [TestFixture]
    public class FailureValidationTests : ApplicationModuleTests
    {
        [Test]
        public async Task Should_return_bad_request()
        {
            // Arrange
            using var client = SetUpClient();

            string invalidBody = """
                {
                   "ApplicationName": "Application101Application101Application101Application101Application101Application101Application101Application101Application101Application101Application101Application101Application101Application101Application101Application101Application101Application101Application101",
                    "ClaimSetName": "",
                    "VendorId":1,
                    "EducationOrganizationIds": [0]
                }
                """;

            //Act
            var addResponse = await client.PostAsync(
                "/v2/applications",
                new StringContent(invalidBody, Encoding.UTF8, "application/json")
            );

            //Assert
            string addResponseContent = await addResponse.Content.ReadAsStringAsync();
            var actualResponse = JsonNode.Parse(addResponseContent);
            var expectedResponse = JsonNode.Parse(
                """
                {
                  "detail": "",
                  "type": "urn:ed-fi:api:bad-request:data-validation-failed",
                  "title": "Data Validation Failed",
                  "status": 400,
                  "correlationId": "{correlationId}",
                  "validationErrors": {
                    "ApplicationName": [
                      "The length of 'Application Name' must be 256 characters or fewer. You entered 266 characters."
                    ],
                    "ClaimSetName": [
                      "'Claim Set Name' must not be empty."
                    ],
                    "EducationOrganizationIds[0]": [
                      "'Education Organization Ids' must be greater than '0'."
                    ]
                  },
                  "errors": []
                }
                """.Replace("{correlationId}", actualResponse!["correlationId"]!.GetValue<string>())
            );
            addResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            JsonNode.DeepEquals(JsonNode.Parse(addResponseContent), expectedResponse).Should().Be(true);
        }
    }

    [TestFixture]
    public class FailureNotFoundTest : ApplicationModuleTests
    {
        [SetUp]
        public void SetUp()
        {
            A.CallTo(() => _vendorRepository.InsertVendor(A<VendorInsertCommand>.Ignored))
                .Returns(new VendorInsertResult.Success(1));

            A.CallTo(() => _applicationRepository.GetApplication(A<long>.Ignored))
                .Returns(new ApplicationGetResult.FailureNotFound());

            A.CallTo(() => _applicationRepository.UpdateApplication(A<ApplicationUpdateCommand>.Ignored))
                .Returns(new ApplicationUpdateResult.FailureNotExists());

            A.CallTo(() => _applicationRepository.DeleteApplication(A<long>.Ignored))
                .Returns(new ApplicationDeleteResult.FailureNotExists());

            A.CallTo(() => _applicationRepository.GetApplicationApiClients(A<long>.Ignored))
                .Returns(new ApplicationApiClientsResult.Success([]));

            A.CallTo(() => _applicationRepository.GetApplicationApiClients(A<long>.Ignored))
                .Returns(new ApplicationApiClientsResult.Success([]));

            A.CallTo(() => _applicationRepository.GetApplicationApiClients(A<long>.Ignored))
                .Returns(new ApplicationApiClientsResult.Success([]));
        }

        [Test]
        public async Task Should_return_proper_not_found_responses()
        {
            // Arrange
            using var client = SetUpClient();

            //Act
            var getByIdResponse = await client.GetAsync("/v2/applications/1");
            var updateResponse = await client.PutAsync(
                "/v2/applications/1",
                new StringContent(
                    """
                    {
                        "id": 1,
                       "applicationName": "Application 101",
                        "claimSetName": "Test",
                        "vendorId":1,
                        "educationOrganizationIds": [1]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );
            var deleteResponse = await client.DeleteAsync("/v2/applications/1");
            var resetCredentialsResponse = await client.PutAsync("/v2/applications/1/reset-credential", null);

            //Assert
            getByIdResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
            updateResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
            deleteResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
            resetCredentialsResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
    }

    [TestFixture]
    public class FailureUnknownTests : ApplicationModuleTests
    {
        [SetUp]
        public void SetUp()
        {
            A.CallTo(
                    () =>
                        _clientRepository.CreateClientAsync(
                            A<string>.Ignored,
                            A<string>.Ignored,
                            A<string>.Ignored
                        )
                )
                .Returns(new ClientCreateResult.Success(Guid.NewGuid()));

            A.CallTo(
                    () =>
                        _applicationRepository.InsertApplication(
                            A<ApplicationInsertCommand>.Ignored,
                            A<ApiClientInsertCommand>.Ignored
                        )
                )
                .Returns(new ApplicationInsertResult.FailureUnknown(""));

            A.CallTo(() => _clientRepository.ResetCredentialsAsync(A<string>.Ignored))
                .Returns(new ClientResetResult.FailureUnknown(""));

            A.CallTo(() => _applicationRepository.QueryApplication(A<PagingQuery>.Ignored))
                .Returns(new ApplicationQueryResult.FailureUnknown(""));

            A.CallTo(() => _applicationRepository.GetApplication(A<long>.Ignored))
                .Returns(new ApplicationGetResult.FailureUnknown(""));

            A.CallTo(() => _applicationRepository.UpdateApplication(A<ApplicationUpdateCommand>.Ignored))
                .Returns(new ApplicationUpdateResult.FailureUnknown(""));

            A.CallTo(() => _applicationRepository.DeleteApplication(A<long>.Ignored))
                .Returns(new ApplicationDeleteResult.FailureUnknown(""));

            A.CallTo(() => _applicationRepository.GetApplicationApiClients(A<long>.Ignored))
                .Returns(new ApplicationApiClientsResult.FailureUnknown(""));
        }

        [Test]
        public async Task Should_return_internal_server_error_response()
        {
            // Arrange
            using var client = SetUpClient();

            //Act
            var addResponse = await client.PostAsync(
                "/v2/applications",
                new StringContent(
                    """
                    {
                        "ApplicationName": "Application 102",
                        "ClaimSetName": "Test",
                        "VendorId": 1,
                        "EducationOrganizationIds": [1]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );
            var getResponse = await client.GetAsync("/v2/applications");
            var getByIdResponse = await client.GetAsync("/v2/applications/1");
            var updateResponse = await client.PutAsync(
                "/v2/applications/1",
                new StringContent(
                    """
                    {
                        "id": 1,
                        "ApplicationName": "Application 102",
                        "ClaimSetName": "Test",
                        "VendorId": 1,
                        "EducationOrganizationIds": [1]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );
            var deleteResponse = await client.DeleteAsync("/v2/applications/1");
            var resetCredentialsResponse = await client.PutAsync("/v2/applications/1/reset-credential", null);

            //Assert
            addResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            getResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            getByIdResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            updateResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            deleteResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            resetCredentialsResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        }
    }

    [TestFixture]
    public class FailureDefaultTests : ApplicationModuleTests
    {
        [SetUp]
        public void SetUp()
        {
            A.CallTo(
                    () =>
                        _applicationRepository.InsertApplication(
                            A<ApplicationInsertCommand>.Ignored,
                            A<ApiClientInsertCommand>.Ignored
                        )
                )
                .Returns(new ApplicationInsertResult());

            A.CallTo(() => _applicationRepository.QueryApplication(A<PagingQuery>.Ignored))
                .Returns(new ApplicationQueryResult());

            A.CallTo(() => _applicationRepository.GetApplication(A<long>.Ignored))
                .Returns(new ApplicationGetResult());

            A.CallTo(() => _applicationRepository.UpdateApplication(A<ApplicationUpdateCommand>.Ignored))
                .Returns(new ApplicationUpdateResult());

            A.CallTo(() => _applicationRepository.DeleteApplication(A<long>.Ignored))
                .Returns(new ApplicationDeleteResult());

            A.CallTo(() => _applicationRepository.GetApplicationApiClients(A<long>.Ignored))
                .Returns(new ApplicationApiClientsResult());
        }

        [Test]
        public async Task Should_return_internal_server_error_response()
        {
            // Arrange
            using var client = SetUpClient();

            //Act
            var addResponse = await client.PostAsync(
                "/v2/applications",
                new StringContent(
                    """
                    {
                      "ApplicationName": "Application 11",
                      "ClaimSetName": "Test",
                      "VendorId": 1,
                      "EducationOrganizationIds": [1]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );

            var getResponse = await client.GetAsync("/v2/applications");
            var getByIdResponse = await client.GetAsync("/v2/applications/1");
            var updateResponse = await client.PostAsync(
                "/v2/applications",
                new StringContent(
                    """
                    {
                      "ApplicationName": "Application 11",
                      "ClaimSetName": "Test",
                      "VendorId": 1,
                      "EducationOrganizationIds": [1]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );
            var deleteResponse = await client.DeleteAsync("/v2/applications/1");

            //Assert
            addResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            getResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            getByIdResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            updateResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            deleteResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        }
    }

    [TestFixture]
    public class FailureReferenceValidationTests : ApplicationModuleTests
    {
        [SetUp]
        public void SetUp()
        {
            A.CallTo(
                    () =>
                        _clientRepository.CreateClientAsync(
                            A<string>.Ignored,
                            A<string>.Ignored,
                            A<string>.Ignored
                        )
                )
                .Returns(new ClientCreateResult.Success(Guid.NewGuid()));

            A.CallTo(
                    () =>
                        _applicationRepository.InsertApplication(
                            A<ApplicationInsertCommand>.Ignored,
                            A<ApiClientInsertCommand>.Ignored
                        )
                )
                .Returns(new ApplicationInsertResult.FailureVendorNotFound());

            A.CallTo(() => _applicationRepository.UpdateApplication(A<ApplicationUpdateCommand>.Ignored))
                .Returns(new ApplicationUpdateResult.FailureVendorNotFound());

            A.CallTo(() => _applicationRepository.GetApplicationApiClients(A<long>.Ignored))
                .Returns(new ApplicationApiClientsResult.Success([]));
        }

        [Test]
        public async Task Should_return_bad_request_due_to_invalid_vendor_id_on_insert()
        {
            // Arrange
            using var client = SetUpClient();

            //Act
            var addResponse = await client.PostAsync(
                "/v2/applications",
                new StringContent(
                    """
                    {
                        "ApplicationName": "Application 102",
                        "ClaimSetName": "Test",
                        "VendorId": 1,
                        "EducationOrganizationIds": [1]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );

            addResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            string responseBody = await addResponse.Content.ReadAsStringAsync();
            var actualResponse = JsonNode.Parse(responseBody);
            var expectedResponse = JsonNode.Parse(
                """
                {
                  "detail": "",
                  "type": "urn:ed-fi:api:bad-request:data-validation-failed",
                  "title": "Data Validation Failed",
                  "status": 400,
                  "correlationId": "{correlationId}",
                  "validationErrors": {
                    "VendorId": [
                      "Reference 'VendorId' does not exist."
                    ]
                  },
                  "errors": []
                }
                """.Replace("{correlationId}", actualResponse!["correlationId"]!.GetValue<string>())
            );
            JsonNode.DeepEquals(actualResponse, expectedResponse).Should().Be(true);
        }

        [Test]
        public async Task Should_return_bad_request_due_to_invalid_vendor_id_on_update()
        {
            // Arrange
            using var client = SetUpClient();

            //Act
            var updateResponse = await client.PutAsync(
                "/v2/applications/1",
                new StringContent(
                    """
                    {
                        "id": 1,
                       "ApplicationName": "Application 101",
                        "ClaimSetName": "Test",
                        "VendorId":1,
                        "EducationOrganizationIds": [1]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );

            //Assert
            updateResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            string responseBody = await updateResponse.Content.ReadAsStringAsync();
            var actualResponse = JsonNode.Parse(responseBody);
            var expectedResponse = JsonNode.Parse(
                """
                {
                  "detail": "",
                  "type": "urn:ed-fi:api:bad-request:data-validation-failed",
                  "title": "Data Validation Failed",
                  "status": 400,
                  "correlationId": "{correlationId}",
                  "validationErrors": {
                    "VendorId": [
                      "Reference 'VendorId' does not exist."
                    ]
                  },
                  "errors": []
                }
                """.Replace("{correlationId}", actualResponse!["correlationId"]!.GetValue<string>())
            );
            JsonNode.DeepEquals(actualResponse, expectedResponse).Should().Be(true);
        }
    }
}
