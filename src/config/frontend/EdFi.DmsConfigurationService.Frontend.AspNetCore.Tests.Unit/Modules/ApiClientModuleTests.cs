// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.ApiClient;
using EdFi.DmsConfigurationService.DataModel.Model.Application;
using EdFi.DmsConfigurationService.DataModel.Model.Authorization;
using EdFi.DmsConfigurationService.DataModel.Model.Vendor;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Modules;

[TestFixture]
public class ApiClientModuleTests
{
    private readonly IApiClientRepository _apiClientRepository = A.Fake<IApiClientRepository>();
    private readonly IApplicationRepository _applicationRepository = A.Fake<IApplicationRepository>();
    private readonly IVendorRepository _vendorRepository = A.Fake<IVendorRepository>();
    private readonly IDmsInstanceRepository _dmsInstanceRepository = A.Fake<IDmsInstanceRepository>();
    private readonly IIdentityProviderRepository _identityProviderRepository =
        A.Fake<IIdentityProviderRepository>();

    private HttpClient SetUpClient()
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(collection =>
            {
                // Use the new test authentication extension that mimics production setup
                collection.AddTestAuthentication();

                collection
                    .AddTransient((_) => _apiClientRepository)
                    .AddTransient((_) => _applicationRepository)
                    .AddTransient((_) => _vendorRepository)
                    .AddTransient((_) => _dmsInstanceRepository)
                    .AddTransient((_) => _identityProviderRepository);
            });
        });
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Scope", AuthorizationScopes.AdminScope.Name);
        return client;
    }

    [TestFixture]
    public class Given_Valid_Requests : ApiClientModuleTests
    {
        [SetUp]
        public void Setup()
        {
            A.CallTo(() => _applicationRepository.GetApplication(A<long>.Ignored))
                .Returns(
                    new ApplicationGetResult.Success(
                        new ApplicationResponse
                        {
                            Id = 1,
                            ApplicationName = "Test Application",
                            ClaimSetName = "TestClaimSet",
                            VendorId = 1,
                            EducationOrganizationIds = [1, 2],
                            DmsInstanceIds = [1],
                        }
                    )
                );

            A.CallTo(() => _vendorRepository.GetVendor(A<long>.Ignored))
                .Returns(
                    new VendorGetResult.Success(
                        new VendorResponse
                        {
                            Id = 1,
                            Company = "Test Vendor",
                            ContactName = "Test Contact",
                            ContactEmailAddress = "test@test.com",
                            NamespacePrefixes = "uri://test.org",
                        }
                    )
                );

            A.CallTo(() => _dmsInstanceRepository.GetExistingDmsInstanceIds(A<long[]>.Ignored))
                .Returns(new DmsInstanceIdsExistResult.Success([1L]));

            A.CallTo(() =>
                    _identityProviderRepository.CreateClientAsync(
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<long[]?>.Ignored
                    )
                )
                .Returns(new ClientCreateResult.Success(Guid.NewGuid()));

            A.CallTo(() =>
                    _apiClientRepository.InsertApiClient(
                        A<ApiClientInsertCommand>.Ignored,
                        A<ApiClientCommand>.Ignored
                    )
                )
                .Returns(new ApiClientInsertResult.Success(1));

            A.CallTo(() => _apiClientRepository.QueryApiClient(A<PagingQuery>.Ignored))
                .Returns(
                    new ApiClientQueryResult.Success(
                        [
                            new ApiClientResponse
                            {
                                Id = 1,
                                ApplicationId = 1,
                                ClientId = "test-client-id",
                                ClientUuid = Guid.NewGuid(),
                                Name = "Test API Client",
                                IsApproved = true,
                                DmsInstanceIds = [1],
                            },
                        ]
                    )
                );

            A.CallTo(() => _apiClientRepository.GetApiClientByClientId(A<string>.Ignored))
                .Returns(
                    new ApiClientGetResult.Success(
                        new ApiClientResponse
                        {
                            Id = 1,
                            ApplicationId = 1,
                            ClientId = "test-client-id",
                            ClientUuid = Guid.NewGuid(),
                            Name = "Test API Client",
                            IsApproved = true,
                            DmsInstanceIds = [1],
                        }
                    )
                );

            A.CallTo(() => _apiClientRepository.GetApiClientById(A<long>.Ignored))
                .Returns(
                    new ApiClientGetResult.Success(
                        new ApiClientResponse
                        {
                            Id = 1,
                            ApplicationId = 1,
                            ClientId = "test-client-id",
                            ClientUuid = Guid.NewGuid(),
                            Name = "Test API Client",
                            IsApproved = true,
                            DmsInstanceIds = [1],
                        }
                    )
                );

            A.CallTo(() =>
                    _identityProviderRepository.UpdateClientAsync(
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<long[]?>.Ignored
                    )
                )
                .Returns(new ClientUpdateResult.Success(Guid.NewGuid()));

            A.CallTo(() => _apiClientRepository.UpdateApiClient(A<ApiClientUpdateCommand>.Ignored))
                .Returns(new ApiClientUpdateResult.Success());
        }

        [Test]
        public async Task It_returns_success_responses_for_all_operations()
        {
            // Arrange
            using var client = SetUpClient();

            // Act
            var insertResponse = await client.PostAsync(
                "/v2/apiClients",
                new StringContent(
                    """
                    {
                      "applicationId": 1,
                      "name": "Test API Client",
                      "isApproved": true,
                      "dmsInstanceIds": [1]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );

            var getAllResponse = await client.GetAsync("/v2/apiClients?offset=0&limit=25");
            var getByClientIdResponse = await client.GetAsync("/v2/apiClients/test-client-id");

            var updateResponse = await client.PutAsync(
                "/v2/apiClients/1",
                new StringContent(
                    """
                    {
                      "id": 1,
                      "applicationId": 1,
                      "name": "Updated API Client",
                      "isApproved": false,
                      "dmsInstanceIds": [1]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );

            // Assert
            insertResponse.StatusCode.Should().Be(HttpStatusCode.Created);
            getAllResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            getByClientIdResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            updateResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }
    }

    [TestFixture]
    public class Given_Invalid_Request_Data : ApiClientModuleTests
    {
        [Test]
        public async Task It_returns_bad_request_for_validation_failures()
        {
            // Arrange
            using var client = SetUpClient();

            string invalidBody = """
                {
                   "applicationId": 0,
                   "name": "",
                   "isApproved": true,
                   "dmsInstanceIds": []
                }
                """;

            // Act
            var insertResponse = await client.PostAsync(
                "/v2/apiClients",
                new StringContent(invalidBody, Encoding.UTF8, "application/json")
            );

            // Assert
            insertResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            string responseContent = await insertResponse.Content.ReadAsStringAsync();
            var actualResponse = JsonNode.Parse(responseContent);

            // Verify the validation errors are present
            actualResponse!["validationErrors"]!["ApplicationId"].Should().NotBeNull();
            actualResponse!["validationErrors"]!["Name"].Should().NotBeNull();
            actualResponse!["validationErrors"]!["DmsInstanceIds"].Should().NotBeNull();
        }

        [Test]
        public async Task It_returns_bad_request_for_name_too_long()
        {
            // Arrange
            using var client = SetUpClient();

            string invalidBody = """
                {
                   "applicationId": 1,
                   "name": "This name is way too long and exceeds the maximum allowed length of fifty characters",
                   "isApproved": true,
                   "dmsInstanceIds": [1]
                }
                """;

            // Act
            var insertResponse = await client.PostAsync(
                "/v2/apiClients",
                new StringContent(invalidBody, Encoding.UTF8, "application/json")
            );

            // Assert
            insertResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            string responseContent = await insertResponse.Content.ReadAsStringAsync();
            var actualResponse = JsonNode.Parse(responseContent);

            actualResponse!["validationErrors"]!["Name"].Should().NotBeNull();
        }
    }

    [TestFixture]
    public class Given_Nonexistent_Resources : ApiClientModuleTests
    {
        [SetUp]
        public void Setup()
        {
            A.CallTo(() => _apiClientRepository.GetApiClientByClientId(A<string>.Ignored))
                .Returns(new ApiClientGetResult.FailureNotFound());

            A.CallTo(() => _apiClientRepository.GetApiClientById(A<long>.Ignored))
                .Returns(new ApiClientGetResult.FailureNotFound());

            A.CallTo(() => _apiClientRepository.UpdateApiClient(A<ApiClientUpdateCommand>.Ignored))
                .Returns(new ApiClientUpdateResult.FailureNotFound());
        }

        [Test]
        public async Task It_returns_not_found_for_get_by_client_id()
        {
            // Arrange
            using var client = SetUpClient();

            // Act
            var getResponse = await client.GetAsync("/v2/apiClients/nonexistent-client");

            // Assert
            getResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        [Test]
        public async Task It_returns_not_found_for_update()
        {
            // Arrange
            using var client = SetUpClient();

            // Act
            var updateResponse = await client.PutAsync(
                "/v2/apiClients/999",
                new StringContent(
                    """
                    {
                      "id": 999,
                      "applicationId": 1,
                      "name": "Updated Name",
                      "isApproved": true,
                      "dmsInstanceIds": [1]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );

            // Assert
            updateResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
    }

    [TestFixture]
    public class Given_Repository_Failures : ApiClientModuleTests
    {
        [SetUp]
        public void Setup()
        {
            A.CallTo(() => _applicationRepository.GetApplication(A<long>.Ignored))
                .Returns(
                    new ApplicationGetResult.Success(
                        new ApplicationResponse
                        {
                            Id = 1,
                            ApplicationName = "Test Application",
                            ClaimSetName = "TestClaimSet",
                            VendorId = 1,
                            EducationOrganizationIds = [1],
                            DmsInstanceIds = [1],
                        }
                    )
                );

            A.CallTo(() => _vendorRepository.GetVendor(A<long>.Ignored))
                .Returns(
                    new VendorGetResult.Success(
                        new VendorResponse
                        {
                            Company = "Test",
                            ContactName = "Test",
                            ContactEmailAddress = "test@test.com",
                            NamespacePrefixes = "uri://test",
                        }
                    )
                );

            A.CallTo(() => _dmsInstanceRepository.GetExistingDmsInstanceIds(A<long[]>.Ignored))
                .Returns(new DmsInstanceIdsExistResult.Success([1L]));

            A.CallTo(() =>
                    _identityProviderRepository.CreateClientAsync(
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<long[]?>.Ignored
                    )
                )
                .Returns(new ClientCreateResult.Success(Guid.NewGuid()));

            A.CallTo(() =>
                    _apiClientRepository.InsertApiClient(
                        A<ApiClientInsertCommand>.Ignored,
                        A<ApiClientCommand>.Ignored
                    )
                )
                .Returns(new ApiClientInsertResult.FailureUnknown("Database error"));

            A.CallTo(() => _apiClientRepository.QueryApiClient(A<PagingQuery>.Ignored))
                .Returns(new ApiClientQueryResult.FailureUnknown("Database error"));

            A.CallTo(() => _apiClientRepository.GetApiClientByClientId(A<string>.Ignored))
                .Returns(new ApiClientGetResult.FailureUnknown("Database error"));

            A.CallTo(() => _apiClientRepository.GetApiClientById(A<long>.Ignored))
                .Returns(
                    new ApiClientGetResult.Success(
                        new ApiClientResponse
                        {
                            Id = 1,
                            ApplicationId = 1,
                            ClientId = "test-client",
                            ClientUuid = Guid.NewGuid(),
                            Name = "Test",
                            IsApproved = true,
                            DmsInstanceIds = [1],
                        }
                    )
                );

            A.CallTo(() =>
                    _identityProviderRepository.UpdateClientAsync(
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<long[]?>.Ignored
                    )
                )
                .Returns(new ClientUpdateResult.Success(Guid.NewGuid()));

            A.CallTo(() => _apiClientRepository.UpdateApiClient(A<ApiClientUpdateCommand>.Ignored))
                .Returns(new ApiClientUpdateResult.FailureUnknown("Database error"));
        }

        [Test]
        public async Task It_returns_internal_server_error_for_unknown_failures()
        {
            // Arrange
            using var client = SetUpClient();

            // Act
            var insertResponse = await client.PostAsync(
                "/v2/apiClients",
                new StringContent(
                    """
                    {
                      "applicationId": 1,
                      "name": "Test Client",
                      "isApproved": true,
                      "dmsInstanceIds": [1]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );

            var getAllResponse = await client.GetAsync("/v2/apiClients?offset=0&limit=25");
            var getByIdResponse = await client.GetAsync("/v2/apiClients/test-client");

            var updateResponse = await client.PutAsync(
                "/v2/apiClients/1",
                new StringContent(
                    """
                    {
                      "id": 1,
                      "applicationId": 1,
                      "name": "Updated",
                      "isApproved": true,
                      "dmsInstanceIds": [1]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );

            // Assert
            insertResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            getAllResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            getByIdResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            updateResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        }
    }

    [TestFixture]
    public class Given_Invalid_Application_Reference : ApiClientModuleTests
    {
        [SetUp]
        public void Setup()
        {
            A.CallTo(() => _applicationRepository.GetApplication(A<long>.Ignored))
                .Returns(new ApplicationGetResult.FailureNotFound());

            A.CallTo(() => _vendorRepository.GetVendor(A<long>.Ignored))
                .Returns(
                    new VendorGetResult.Success(
                        new VendorResponse
                        {
                            Company = "Test",
                            ContactName = "Test",
                            ContactEmailAddress = "test@test.com",
                            NamespacePrefixes = "uri://test",
                        }
                    )
                );

            A.CallTo(() => _dmsInstanceRepository.GetExistingDmsInstanceIds(A<long[]>.Ignored))
                .Returns(new DmsInstanceIdsExistResult.Success([1L]));

            A.CallTo(() => _apiClientRepository.GetApiClientById(A<long>.Ignored))
                .Returns(
                    new ApiClientGetResult.Success(
                        new ApiClientResponse
                        {
                            Id = 1,
                            ApplicationId = 1,
                            ClientId = "test-client",
                            ClientUuid = Guid.NewGuid(),
                            Name = "Test",
                            IsApproved = true,
                            DmsInstanceIds = [1],
                        }
                    )
                );
        }

        [Test]
        public async Task It_returns_bad_request_for_nonexistent_application_on_insert()
        {
            // Arrange
            using var client = SetUpClient();

            // Act
            var insertResponse = await client.PostAsync(
                "/v2/apiClients",
                new StringContent(
                    """
                    {
                      "applicationId": 999,
                      "name": "Test Client",
                      "isApproved": true,
                      "dmsInstanceIds": [1]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );

            // Assert
            insertResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            string responseContent = await insertResponse.Content.ReadAsStringAsync();
            var actualResponse = JsonNode.Parse(responseContent);
            var expectedResponse = JsonNode.Parse(
                """
                {
                  "detail": "Data validation failed. See 'validationErrors' for details.",
                  "type": "urn:ed-fi:api:bad-request:data-validation-failed",
                  "title": "Data Validation Failed",
                  "status": 400,
                  "correlationId": "{correlationId}",
                  "validationErrors": {
                    "ApplicationId": [
                      "Application with ID 999 not found."
                    ]
                  },
                  "errors": []
                }
                """.Replace("{correlationId}", actualResponse!["correlationId"]!.GetValue<string>())
            );
            JsonNode.DeepEquals(actualResponse, expectedResponse).Should().Be(true);
        }

        [Test]
        public async Task It_returns_bad_request_for_nonexistent_application_on_update()
        {
            // Arrange
            using var client = SetUpClient();

            // Act
            var updateResponse = await client.PutAsync(
                "/v2/apiClients/1",
                new StringContent(
                    """
                    {
                      "id": 1,
                      "applicationId": 999,
                      "name": "Updated Client",
                      "isApproved": true,
                      "dmsInstanceIds": [1]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );

            // Assert
            updateResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            string responseContent = await updateResponse.Content.ReadAsStringAsync();
            var actualResponse = JsonNode.Parse(responseContent);
            var expectedResponse = JsonNode.Parse(
                """
                {
                  "detail": "Data validation failed. See 'validationErrors' for details.",
                  "type": "urn:ed-fi:api:bad-request:data-validation-failed",
                  "title": "Data Validation Failed",
                  "status": 400,
                  "correlationId": "{correlationId}",
                  "validationErrors": {
                    "ApplicationId": [
                      "Application with ID 999 not found."
                    ]
                  },
                  "errors": []
                }
                """.Replace("{correlationId}", actualResponse!["correlationId"]!.GetValue<string>())
            );
            JsonNode.DeepEquals(actualResponse, expectedResponse).Should().Be(true);
        }
    }

    [TestFixture]
    public class Given_Invalid_DmsInstance_Reference : ApiClientModuleTests
    {
        [SetUp]
        public void Setup()
        {
            A.CallTo(() => _applicationRepository.GetApplication(A<long>.Ignored))
                .Returns(
                    new ApplicationGetResult.Success(
                        new ApplicationResponse
                        {
                            Id = 1,
                            ApplicationName = "Test Application",
                            ClaimSetName = "TestClaimSet",
                            VendorId = 1,
                            EducationOrganizationIds = [1],
                            DmsInstanceIds = [1],
                        }
                    )
                );

            A.CallTo(() => _vendorRepository.GetVendor(A<long>.Ignored))
                .Returns(
                    new VendorGetResult.Success(
                        new VendorResponse
                        {
                            Company = "Test",
                            ContactName = "Test",
                            ContactEmailAddress = "test@test.com",
                            NamespacePrefixes = "uri://test",
                        }
                    )
                );

            A.CallTo(() => _dmsInstanceRepository.GetExistingDmsInstanceIds(A<long[]>.Ignored))
                .Returns(new DmsInstanceIdsExistResult.Success([]));

            A.CallTo(() => _apiClientRepository.GetApiClientById(A<long>.Ignored))
                .Returns(
                    new ApiClientGetResult.Success(
                        new ApiClientResponse
                        {
                            Id = 1,
                            ApplicationId = 1,
                            ClientId = "test-client",
                            ClientUuid = Guid.NewGuid(),
                            Name = "Test",
                            IsApproved = true,
                            DmsInstanceIds = [1],
                        }
                    )
                );
        }

        [Test]
        public async Task It_returns_bad_request_for_nonexistent_dms_instance_on_insert()
        {
            // Arrange
            using var client = SetUpClient();

            // Act
            var insertResponse = await client.PostAsync(
                "/v2/apiClients",
                new StringContent(
                    """
                    {
                      "applicationId": 1,
                      "name": "Test Client",
                      "isApproved": true,
                      "dmsInstanceIds": [999, 888]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );

            // Assert
            insertResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            string responseContent = await insertResponse.Content.ReadAsStringAsync();
            var actualResponse = JsonNode.Parse(responseContent);
            var expectedResponse = JsonNode.Parse(
                """
                {
                  "detail": "Data validation failed. See 'validationErrors' for details.",
                  "type": "urn:ed-fi:api:bad-request:data-validation-failed",
                  "title": "Data Validation Failed",
                  "status": 400,
                  "correlationId": "{correlationId}",
                  "validationErrors": {
                    "DmsInstanceIds": [
                      "The following DmsInstanceIds were not found in database: 999, 888"
                    ]
                  },
                  "errors": []
                }
                """.Replace("{correlationId}", actualResponse!["correlationId"]!.GetValue<string>())
            );
            JsonNode.DeepEquals(actualResponse, expectedResponse).Should().Be(true);
        }

        [Test]
        public async Task It_returns_bad_request_for_nonexistent_dms_instance_on_update()
        {
            // Arrange
            using var client = SetUpClient();

            // Act
            var updateResponse = await client.PutAsync(
                "/v2/apiClients/1",
                new StringContent(
                    """
                    {
                      "id": 1,
                      "applicationId": 1,
                      "name": "Updated Client",
                      "isApproved": true,
                      "dmsInstanceIds": [999, 888]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );

            // Assert
            updateResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            string responseContent = await updateResponse.Content.ReadAsStringAsync();
            var actualResponse = JsonNode.Parse(responseContent);
            var expectedResponse = JsonNode.Parse(
                """
                {
                  "detail": "Data validation failed. See 'validationErrors' for details.",
                  "type": "urn:ed-fi:api:bad-request:data-validation-failed",
                  "title": "Data Validation Failed",
                  "status": 400,
                  "correlationId": "{correlationId}",
                  "validationErrors": {
                    "DmsInstanceIds": [
                      "The following DmsInstanceIds were not found in database: 999, 888"
                    ]
                  },
                  "errors": []
                }
                """.Replace("{correlationId}", actualResponse!["correlationId"]!.GetValue<string>())
            );
            JsonNode.DeepEquals(actualResponse, expectedResponse).Should().Be(true);
        }
    }

    [TestFixture]
    public class Given_IdentityProvider_Failures : ApiClientModuleTests
    {
        [SetUp]
        public void Setup()
        {
            A.CallTo(() => _applicationRepository.GetApplication(A<long>.Ignored))
                .Returns(
                    new ApplicationGetResult.Success(
                        new ApplicationResponse
                        {
                            Id = 1,
                            ApplicationName = "Test Application",
                            ClaimSetName = "TestClaimSet",
                            VendorId = 1,
                            EducationOrganizationIds = [1],
                            DmsInstanceIds = [1],
                        }
                    )
                );

            A.CallTo(() => _vendorRepository.GetVendor(A<long>.Ignored))
                .Returns(
                    new VendorGetResult.Success(
                        new VendorResponse
                        {
                            Company = "Test",
                            ContactName = "Test",
                            ContactEmailAddress = "test@test.com",
                            NamespacePrefixes = "uri://test",
                        }
                    )
                );

            A.CallTo(() => _dmsInstanceRepository.GetExistingDmsInstanceIds(A<long[]>.Ignored))
                .Returns(new DmsInstanceIdsExistResult.Success([1L]));

            A.CallTo(() =>
                    _identityProviderRepository.CreateClientAsync(
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<long[]?>.Ignored
                    )
                )
                .Returns(
                    new ClientCreateResult.FailureIdentityProvider(
                        new IdentityProviderError("Identity provider error")
                    )
                );

            A.CallTo(() => _apiClientRepository.GetApiClientById(A<long>.Ignored))
                .Returns(
                    new ApiClientGetResult.Success(
                        new ApiClientResponse
                        {
                            Id = 1,
                            ApplicationId = 1,
                            ClientId = "test-client",
                            ClientUuid = Guid.NewGuid(),
                            Name = "Test",
                            IsApproved = true,
                            DmsInstanceIds = [1],
                        }
                    )
                );

            A.CallTo(() =>
                    _identityProviderRepository.UpdateClientAsync(
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<long[]?>.Ignored
                    )
                )
                .Returns(
                    new ClientUpdateResult.FailureIdentityProvider(
                        new IdentityProviderError("Identity provider error")
                    )
                );
        }

        [Test]
        public async Task It_returns_bad_gateway_for_identity_provider_failure_on_insert()
        {
            // Arrange
            using var client = SetUpClient();

            // Act
            var insertResponse = await client.PostAsync(
                "/v2/apiClients",
                new StringContent(
                    """
                    {
                      "applicationId": 1,
                      "name": "Test Client",
                      "isApproved": true,
                      "dmsInstanceIds": [1]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );

            // Assert
            insertResponse.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        }

        [Test]
        public async Task It_returns_bad_gateway_for_identity_provider_failure_on_update()
        {
            // Arrange
            using var client = SetUpClient();

            // Act
            var updateResponse = await client.PutAsync(
                "/v2/apiClients/1",
                new StringContent(
                    """
                    {
                      "id": 1,
                      "applicationId": 1,
                      "name": "Updated Client",
                      "isApproved": true,
                      "dmsInstanceIds": [1]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );

            // Assert
            updateResponse.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        }
    }
}
