// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EdFi.DmsConfigurationService.Backend;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Configuration;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.ApiClient;
using EdFi.DmsConfigurationService.DataModel.Model.Application;
using EdFi.DmsConfigurationService.DataModel.Model.Authorization;
using EdFi.DmsConfigurationService.DataModel.Model.Vendor;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Configuration;
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
    private readonly IDataStoreRepository _dataStoreRepository = A.Fake<IDataStoreRepository>();
    private readonly IIdentityProviderRepository _identityProviderRepository =
        A.Fake<IIdentityProviderRepository>();

    private HttpClient SetUpClient(int? clientSecretMinimumLength = null)
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(collection =>
            {
                // Use the new test authentication extension that mimics production setup
                collection.AddTestAuthentication();
                if (clientSecretMinimumLength is not null)
                {
                    collection.Configure<ClientSecretValidationOptions>(options =>
                    {
                        options.MinimumLength = clientSecretMinimumLength.Value;
                        options.MaximumLength = clientSecretMinimumLength.Value + 96;
                    });
                    collection.Configure<IdentitySettings>(options =>
                    {
                        options.ClientSecret = ClientSecretValidation.GenerateSecretWithMinimumLength(
                            new ClientSecretValidationOptions
                            {
                                MinimumLength = clientSecretMinimumLength.Value,
                                MaximumLength = clientSecretMinimumLength.Value + 96,
                            }
                        );
                    });
                }

                collection
                    .AddTransient((_) => _apiClientRepository)
                    .AddTransient((_) => _applicationRepository)
                    .AddTransient((_) => _vendorRepository)
                    .AddTransient((_) => _dataStoreRepository)
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
                            DataStoreIds = [1],
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

            A.CallTo(() => _dataStoreRepository.GetExistingDataStoreIds(A<long[]>.Ignored))
                .Returns(new DataStoreIdsExistResult.Success([1L]));

            A.CallTo(() =>
                    _identityProviderRepository.CreateClientAsync(
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<long[]?>.Ignored,
                        A<bool>.Ignored
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

            A.CallTo(() => _apiClientRepository.QueryApiClient(A<ApiClientQuery>.Ignored))
                .Returns(
                    new ApiClientQueryResult.Success([
                        new ApiClientResponse
                        {
                            Id = 1,
                            ApplicationId = 1,
                            ClientId = "test-client-id",
                            ClientUuid = Guid.NewGuid(),
                            Name = "Test API Client",
                            IsApproved = true,
                            DataStoreIds = [1],
                        },
                    ])
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
                            DataStoreIds = [1],
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
                            DataStoreIds = [1],
                        }
                    )
                );

            A.CallTo(() =>
                    _identityProviderRepository.UpdateClientAsync(
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<long[]?>.Ignored,
                        A<bool>.Ignored,
                        A<string>.Ignored
                    )
                )
                .Returns(new ClientUpdateResult.Success(Guid.NewGuid()));

            A.CallTo(() => _apiClientRepository.UpdateApiClient(A<ApiClientUpdateCommand>.Ignored))
                .Returns(new ApiClientUpdateResult.Success());

            A.CallTo(() => _apiClientRepository.DeleteApiClient(A<long>.Ignored))
                .Returns(new ApiClientDeleteResult.Success());

            A.CallTo(() => _identityProviderRepository.DeleteClientAsync(A<string>.Ignored))
                .Returns(new ClientDeleteResult.Success());

            A.CallTo(() => _identityProviderRepository.ResetCredentialsAsync(A<string>.Ignored))
                .Returns(new ClientResetResult.Success("new-secret-12345"));
        }

        [Test]
        public async Task It_returns_success_responses_for_all_operations()
        {
            // Arrange
            using var client = SetUpClient();

            // Act
            var insertResponse = await client.PostAsync(
                "/v3/apiClients",
                new StringContent(
                    """
                    {
                      "applicationId": 1,
                      "name": "Test API Client",
                      "isApproved": true,
                      "dataStoreIds": [1]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );

            var getAllResponse = await client.GetAsync("/v3/apiClients?offset=0&limit=25");
            var getByClientIdResponse = await client.GetAsync("/v3/apiClients/test-client-id");

            var updateResponse = await client.PutAsync(
                "/v3/apiClients/1",
                new StringContent(
                    """
                    {
                      "id": 1,
                      "applicationId": 1,
                      "name": "Updated API Client",
                      "isApproved": false,
                      "dataStoreIds": [1]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );

            var deleteResponse = await client.DeleteAsync("/v3/apiClients/1");

            // Assert
            insertResponse.StatusCode.Should().Be(HttpStatusCode.Created);
            getAllResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            getByClientIdResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            updateResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
            deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }

        [Test]
        public async Task It_generates_api_client_secret_using_the_configured_minimum_length()
        {
            // Arrange
            var configuredMinimumLength = 48;
            string generatedSecret = string.Empty;

            A.CallTo(() =>
                    _identityProviderRepository.CreateClientAsync(
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<long[]?>.Ignored,
                        A<bool>.Ignored
                    )
                )
                .Invokes(call =>
                    generatedSecret =
                        call.GetArgument<string>(1)
                        ?? throw new InvalidOperationException("Generated secret should not be null.")
                )
                .Returns(new ClientCreateResult.Success(Guid.NewGuid()));

            using var client = SetUpClient(configuredMinimumLength);

            // Act
            var insertResponse = await client.PostAsync(
                "/v3/apiClients",
                new StringContent(
                    """
                    {
                      "applicationId": 1,
                      "name": "Test API Client",
                      "isApproved": true,
                      "dataStoreIds": [1]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );

            // Assert
            insertResponse.StatusCode.Should().Be(HttpStatusCode.Created);
            generatedSecret.Should().HaveLength(configuredMinimumLength);
            Regex
                .IsMatch(
                    generatedSecret,
                    ClientSecretValidation.BuildComplexityPattern(
                        new ClientSecretValidationOptions
                        {
                            MinimumLength = configuredMinimumLength,
                            MaximumLength = configuredMinimumLength + 96,
                        }
                    )
                )
                .Should()
                .BeTrue();

            var responseContent = await insertResponse.Content.ReadAsStringAsync();
            var actualResponse = JsonNode.Parse(responseContent);
            actualResponse!["secret"]!.GetValue<string>().Should().HaveLength(configuredMinimumLength);
            actualResponse!["secret"]!.GetValue<string>().Should().Be(generatedSecret);
        }

        [Test]
        public async Task It_returns_name_and_applicationId_in_post_response()
        {
            // Arrange
            using var client = SetUpClient();

            // Act
            var insertResponse = await client.PostAsync(
                "/v3/apiClients",
                new StringContent(
                    """
                    {
                      "applicationId": 1,
                      "name": "Test API Client",
                      "isApproved": true,
                      "dataStoreIds": [1]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );

            // Assert
            insertResponse.StatusCode.Should().Be(HttpStatusCode.Created);
            var responseContent = await insertResponse.Content.ReadAsStringAsync();
            var actualResponse = JsonNode.Parse(responseContent);

            actualResponse!["id"]!.GetValue<long>().Should().Be(1L);
            actualResponse!["name"]!.GetValue<string>().Should().Be("Test API Client");
            actualResponse!["applicationId"]!.GetValue<long>().Should().Be(1L);
            actualResponse!["key"]!.GetValue<string>().Should().NotBeNullOrEmpty();
            actualResponse!["secret"]!.GetValue<string>().Should().NotBeNullOrEmpty();
        }

        [Test]
        public async Task It_disables_the_identity_provider_client_when_api_client_is_unapproved_on_insert()
        {
            // Arrange
            var createdClientUuid = Guid.NewGuid();

            A.CallTo(() =>
                    _identityProviderRepository.CreateClientAsync(
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<long[]?>.Ignored,
                        A<bool>.Ignored
                    )
                )
                .Returns(new ClientCreateResult.Success(createdClientUuid));

            using var client = SetUpClient();

            // Act
            var insertResponse = await client.PostAsync(
                "/v3/apiClients",
                new StringContent(
                    """
                    {
                      "applicationId": 1,
                      "name": "Disabled Client",
                      "isApproved": false,
                      "dataStoreIds": [1]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );

            // Assert
            insertResponse.StatusCode.Should().Be(HttpStatusCode.Created);
            A.CallTo(() =>
                    _identityProviderRepository.CreateClientAsync(
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        "Test Application",
                        "TestClaimSet",
                        "uri://test.org",
                        "1,2",
                        A<long[]>.That.Matches(ids => ids.Length == 1 && ids[0] == 1),
                        false
                    )
                )
                .MustHaveHappenedOnceExactly();
            A.CallTo(() =>
                    _apiClientRepository.InsertApiClient(
                        A<ApiClientInsertCommand>.Ignored,
                        A<ApiClientCommand>.That.Matches(command =>
                            command.ClientUuid == createdClientUuid
                            && command.DataStoreIds.Length == 1
                            && command.DataStoreIds[0] == 1
                        )
                    )
                )
                .MustHaveHappenedOnceExactly();
        }

        [Test]
        public async Task It_returns_success_response_for_reset_credential()
        {
            // Arrange
            using var client = SetUpClient();

            // Act
            var resetResponse = await client.PutAsync(
                "/v3/apiClients/1/reset-credential",
                new StringContent("{}", Encoding.UTF8, "application/json")
            );

            // Assert
            resetResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            string responseContent = await resetResponse.Content.ReadAsStringAsync();
            var actualResponse = JsonNode.Parse(responseContent);

            actualResponse!["id"]!.GetValue<long>().Should().Be(1);
            actualResponse!["applicationId"]!.GetValue<long>().Should().Be(1L);
            actualResponse!["name"]!.GetValue<string>().Should().Be("Test API Client");
            actualResponse!["key"]!.GetValue<string>().Should().NotBeNullOrEmpty();
            actualResponse!["secret"]!.GetValue<string>().Should().Be("new-secret-12345");
        }

        [Test]
        public async Task It_disables_the_identity_provider_client_when_api_client_is_unapproved()
        {
            // Arrange
            using var client = SetUpClient();

            // Act
            var updateResponse = await client.PutAsync(
                "/v3/apiClients/1",
                new StringContent(
                    """
                    {
                      "id": 1,
                      "applicationId": 1,
                      "name": "Updated API Client",
                      "isApproved": false,
                      "dataStoreIds": [1]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );

            // Assert
            updateResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

            A.CallTo(() =>
                    _identityProviderRepository.UpdateClientAsync(
                        A<string>.Ignored,
                        "Updated API Client",
                        "TestClaimSet",
                        "1,2",
                        A<long[]>.That.Matches(ids => ids.Length == 1 && ids[0] == 1),
                        false,
                        A<string>.Ignored
                    )
                )
                .MustHaveHappenedOnceExactly();
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
                   "dataStoreIds": []
                }
                """;

            // Act
            var insertResponse = await client.PostAsync(
                "/v3/apiClients",
                new StringContent(invalidBody, Encoding.UTF8, "application/json")
            );

            // Assert
            insertResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            string responseContent = await insertResponse.Content.ReadAsStringAsync();
            var actualResponse = JsonNode.Parse(responseContent);

            // Verify the validation errors are present
            actualResponse!["validationErrors"]!["ApplicationId"].Should().NotBeNull();
            actualResponse!["validationErrors"]!["Name"].Should().NotBeNull();
            actualResponse!["validationErrors"]!["DataStoreIds"].Should().NotBeNull();
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
                   "dataStoreIds": [1]
                }
                """;

            // Act
            var insertResponse = await client.PostAsync(
                "/v3/apiClients",
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

            A.CallTo(() => _apiClientRepository.DeleteApiClient(A<long>.Ignored))
                .Returns(new ApiClientDeleteResult.FailureNotFound());
        }

        [Test]
        public async Task It_returns_not_found_for_get_by_client_id()
        {
            // Arrange
            using var client = SetUpClient();

            // Act
            var getResponse = await client.GetAsync("/v3/apiClients/nonexistent-client");

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
                "/v3/apiClients/999",
                new StringContent(
                    """
                    {
                      "id": 999,
                      "applicationId": 1,
                      "name": "Updated Name",
                      "isApproved": true,
                      "dataStoreIds": [1]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );

            // Assert
            updateResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        [Test]
        public async Task It_returns_not_found_for_delete()
        {
            // Arrange
            using var client = SetUpClient();

            // Act
            var deleteResponse = await client.DeleteAsync("/v3/apiClients/999");

            // Assert
            deleteResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        [Test]
        public async Task It_returns_not_found_for_reset_credential()
        {
            // Arrange
            using var client = SetUpClient();

            // Act
            var resetResponse = await client.PutAsync(
                "/v3/apiClients/999/reset-credential",
                new StringContent("{}", Encoding.UTF8, "application/json")
            );

            // Assert
            resetResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
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
                            DataStoreIds = [1],
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

            A.CallTo(() => _dataStoreRepository.GetExistingDataStoreIds(A<long[]>.Ignored))
                .Returns(new DataStoreIdsExistResult.Success([1L]));

            A.CallTo(() =>
                    _identityProviderRepository.CreateClientAsync(
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<long[]?>.Ignored,
                        A<bool>.Ignored
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

            A.CallTo(() => _apiClientRepository.QueryApiClient(A<ApiClientQuery>.Ignored))
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
                            DataStoreIds = [1],
                        }
                    )
                );

            A.CallTo(() =>
                    _identityProviderRepository.UpdateClientAsync(
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<long[]?>.Ignored,
                        A<bool>.Ignored,
                        A<string>.Ignored
                    )
                )
                .Returns(new ClientUpdateResult.Success(Guid.NewGuid()));

            A.CallTo(() => _apiClientRepository.UpdateApiClient(A<ApiClientUpdateCommand>.Ignored))
                .Returns(new ApiClientUpdateResult.FailureUnknown("Database error"));

            A.CallTo(() => _apiClientRepository.DeleteApiClient(A<long>.Ignored))
                .Returns(new ApiClientDeleteResult.FailureUnknown("Database error"));

            A.CallTo(() => _identityProviderRepository.DeleteClientAsync(A<string>.Ignored))
                .Returns(new ClientDeleteResult.Success());
        }

        [Test]
        public async Task It_returns_internal_server_error_for_unknown_failures()
        {
            // Arrange
            using var client = SetUpClient();

            // Act
            var insertResponse = await client.PostAsync(
                "/v3/apiClients",
                new StringContent(
                    """
                    {
                      "applicationId": 1,
                      "name": "Test Client",
                      "isApproved": true,
                      "dataStoreIds": [1]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );

            var getAllResponse = await client.GetAsync("/v3/apiClients?offset=0&limit=25");
            var getByIdResponse = await client.GetAsync("/v3/apiClients/test-client");

            var updateResponse = await client.PutAsync(
                "/v3/apiClients/1",
                new StringContent(
                    """
                    {
                      "id": 1,
                      "applicationId": 1,
                      "name": "Updated",
                      "isApproved": true,
                      "dataStoreIds": [1]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );

            var deleteResponse = await client.DeleteAsync("/v3/apiClients/1");

            // Assert
            insertResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            getAllResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            getByIdResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            updateResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            deleteResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        }

        [Test]
        public async Task It_syncs_rollback_client_uuid_when_database_update_fails()
        {
            // Arrange
            var updatedClientUuid = Guid.NewGuid();
            var rollbackClientUuid = Guid.NewGuid();
            List<ApiClientUpdateCommand> updateCommands = [];

            A.CallTo(() =>
                    _identityProviderRepository.UpdateClientAsync(
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<long[]?>.Ignored,
                        A<bool>.Ignored,
                        A<string>.Ignored
                    )
                )
                .ReturnsNextFromSequence(
                    new ClientUpdateResult.Success(updatedClientUuid),
                    new ClientUpdateResult.Success(rollbackClientUuid)
                );

            A.CallTo(() => _apiClientRepository.UpdateApiClient(A<ApiClientUpdateCommand>.Ignored))
                .Invokes(call =>
                {
                    updateCommands.Add(call.GetArgument<ApiClientUpdateCommand>(0)!);
                })
                .ReturnsNextFromSequence(
                    new ApiClientUpdateResult.FailureUnknown("Database error"),
                    new ApiClientUpdateResult.Success()
                );

            using var client = SetUpClient();

            // Act
            var updateResponse = await client.PutAsync(
                "/v3/apiClients/1",
                new StringContent(
                    """
                    {
                      "id": 1,
                      "applicationId": 1,
                      "name": "Updated",
                      "isApproved": true,
                      "dataStoreIds": [1]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );

            // Assert
            updateResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            updateCommands.Should().HaveCount(2);
            updateCommands[0].ClientUuid.Should().Be(updatedClientUuid);
            updateCommands[1].ClientUuid.Should().Be(rollbackClientUuid);
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

            A.CallTo(() => _dataStoreRepository.GetExistingDataStoreIds(A<long[]>.Ignored))
                .Returns(new DataStoreIdsExistResult.Success([1L]));

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
                            DataStoreIds = [1],
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
                "/v3/apiClients",
                new StringContent(
                    """
                    {
                      "applicationId": 999,
                      "name": "Test Client",
                      "isApproved": true,
                      "dataStoreIds": [1]
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
                  "type": "urn:ed-fi:api:bad-request:data",
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
                "/v3/apiClients/1",
                new StringContent(
                    """
                    {
                      "id": 1,
                      "applicationId": 999,
                      "name": "Updated Client",
                      "isApproved": true,
                      "dataStoreIds": [1]
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
                  "type": "urn:ed-fi:api:bad-request:data",
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
    public class Given_Invalid_DataStore_Reference : ApiClientModuleTests
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
                            DataStoreIds = [1],
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

            A.CallTo(() => _dataStoreRepository.GetExistingDataStoreIds(A<long[]>.Ignored))
                .Returns(new DataStoreIdsExistResult.Success([]));

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
                            DataStoreIds = [1],
                        }
                    )
                );
        }

        [Test]
        public async Task It_returns_bad_request_for_nonexistent_data_store_on_insert()
        {
            // Arrange
            using var client = SetUpClient();

            // Act
            var insertResponse = await client.PostAsync(
                "/v3/apiClients",
                new StringContent(
                    """
                    {
                      "applicationId": 1,
                      "name": "Test Client",
                      "isApproved": true,
                      "dataStoreIds": [999, 888]
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
                  "type": "urn:ed-fi:api:bad-request:data",
                  "title": "Data Validation Failed",
                  "status": 400,
                  "correlationId": "{correlationId}",
                  "validationErrors": {
                    "DataStoreIds": [
                      "The following DataStoreIds were not found in database: 999, 888"
                    ]
                  },
                  "errors": []
                }
                """.Replace("{correlationId}", actualResponse!["correlationId"]!.GetValue<string>())
            );
            JsonNode.DeepEquals(actualResponse, expectedResponse).Should().Be(true);
        }

        [Test]
        public async Task It_returns_bad_request_for_nonexistent_data_store_on_update()
        {
            // Arrange
            using var client = SetUpClient();

            // Act
            var updateResponse = await client.PutAsync(
                "/v3/apiClients/1",
                new StringContent(
                    """
                    {
                      "id": 1,
                      "applicationId": 1,
                      "name": "Updated Client",
                      "isApproved": true,
                      "dataStoreIds": [999, 888]
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
                  "type": "urn:ed-fi:api:bad-request:data",
                  "title": "Data Validation Failed",
                  "status": 400,
                  "correlationId": "{correlationId}",
                  "validationErrors": {
                    "DataStoreIds": [
                      "The following DataStoreIds were not found in database: 999, 888"
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
                            DataStoreIds = [1],
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

            A.CallTo(() => _dataStoreRepository.GetExistingDataStoreIds(A<long[]>.Ignored))
                .Returns(new DataStoreIdsExistResult.Success([1L]));

            A.CallTo(() =>
                    _identityProviderRepository.CreateClientAsync(
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<long[]?>.Ignored,
                        A<bool>.Ignored
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
                            DataStoreIds = [1],
                        }
                    )
                );

            A.CallTo(() =>
                    _identityProviderRepository.UpdateClientAsync(
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<long[]?>.Ignored,
                        A<bool>.Ignored,
                        A<string>.Ignored
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
                "/v3/apiClients",
                new StringContent(
                    """
                    {
                      "applicationId": 1,
                      "name": "Test Client",
                      "isApproved": true,
                      "dataStoreIds": [1]
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
                "/v3/apiClients/1",
                new StringContent(
                    """
                    {
                      "id": 1,
                      "applicationId": 1,
                      "name": "Updated Client",
                      "isApproved": true,
                      "dataStoreIds": [1]
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

    [TestFixture]
    public class Given_ResetCredential_Scenarios : ApiClientModuleTests
    {
        [SetUp]
        public void Setup()
        {
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
                            DataStoreIds = [1],
                        }
                    )
                );
        }

        [Test]
        public async Task It_returns_success_when_reset_is_successful()
        {
            // Arrange
            A.CallTo(() => _identityProviderRepository.ResetCredentialsAsync(A<string>.Ignored))
                .Returns(new ClientResetResult.Success("new-secret-67890"));

            using var client = SetUpClient();

            // Act
            var resetResponse = await client.PutAsync(
                "/v3/apiClients/1/reset-credential",
                new StringContent("{}", Encoding.UTF8, "application/json")
            );

            // Assert
            resetResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            string responseContent = await resetResponse.Content.ReadAsStringAsync();
            var actualResponse = JsonNode.Parse(responseContent);

            actualResponse!["id"]!.GetValue<long>().Should().Be(1);
            actualResponse!["applicationId"]!.GetValue<long>().Should().Be(1L);
            actualResponse!["name"]!.GetValue<string>().Should().Be("Test API Client");
            actualResponse!["key"]!.GetValue<string>().Should().Be("test-client-id");
            actualResponse!["secret"]!.GetValue<string>().Should().Be("new-secret-67890");
        }

        [Test]
        public async Task It_returns_not_found_when_client_not_found_in_identity_provider()
        {
            // Arrange
            A.CallTo(() => _identityProviderRepository.ResetCredentialsAsync(A<string>.Ignored))
                .Returns(new ClientResetResult.FailureClientNotFound("Client not found"));

            using var client = SetUpClient();

            // Act
            var resetResponse = await client.PutAsync(
                "/v3/apiClients/1/reset-credential",
                new StringContent("{}", Encoding.UTF8, "application/json")
            );

            // Assert
            resetResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        [Test]
        public async Task It_returns_bad_gateway_when_identity_provider_fails()
        {
            // Arrange
            A.CallTo(() => _identityProviderRepository.ResetCredentialsAsync(A<string>.Ignored))
                .Returns(
                    new ClientResetResult.FailureIdentityProvider(
                        new IdentityProviderError("Identity provider connection failed")
                    )
                );

            using var client = SetUpClient();

            // Act
            var resetResponse = await client.PutAsync(
                "/v3/apiClients/1/reset-credential",
                new StringContent("{}", Encoding.UTF8, "application/json")
            );

            // Assert
            resetResponse.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        }

        [Test]
        public async Task It_returns_internal_server_error_for_unknown_failures()
        {
            // Arrange
            A.CallTo(() => _identityProviderRepository.ResetCredentialsAsync(A<string>.Ignored))
                .Returns(new ClientResetResult.FailureUnknown("Unexpected error"));

            using var client = SetUpClient();

            // Act
            var resetResponse = await client.PutAsync(
                "/v3/apiClients/1/reset-credential",
                new StringContent("{}", Encoding.UTF8, "application/json")
            );

            // Assert
            resetResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        }

        [Test]
        public async Task It_returns_not_found_when_api_client_does_not_exist()
        {
            // Arrange
            A.CallTo(() => _apiClientRepository.GetApiClientById(999L))
                .Returns(new ApiClientGetResult.FailureNotFound());

            using var client = SetUpClient();

            // Act
            var resetResponse = await client.PutAsync(
                "/v3/apiClients/999/reset-credential",
                new StringContent("{}", Encoding.UTF8, "application/json")
            );

            // Assert
            resetResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
    }

    [TestFixture]
    public class Given_Invalid_PagingQuery : ApiClientModuleTests
    {
        [SetUp]
        public void Setup()
        {
            A.CallTo(() => _apiClientRepository.QueryApiClient(A<ApiClientQuery>.Ignored))
                .Returns(new ApiClientQueryResult.Success([]));
        }

        [Test]
        public async Task Should_return_400_when_orderBy_is_invalid()
        {
            using var client = SetUpClient();
            var response = await client.GetAsync("/v3/apiClients?orderBy=invalidField");
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Test]
        public async Task Should_return_400_when_direction_is_invalid()
        {
            using var client = SetUpClient();
            var response = await client.GetAsync("/v3/apiClients?orderBy=id&direction=SIDEWAYS");
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Test]
        public async Task Should_return_400_when_offset_is_negative()
        {
            using var client = SetUpClient();
            var response = await client.GetAsync("/v3/apiClients?offset=-1");
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Test]
        public async Task Should_return_400_when_limit_is_zero()
        {
            using var client = SetUpClient();
            var response = await client.GetAsync("/v3/apiClients?limit=0");
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Test]
        public async Task Should_return_200_with_valid_orderBy_and_direction()
        {
            using var client = SetUpClient();
            var response = await client.GetAsync("/v3/apiClients?orderBy=name&direction=ASC");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Test]
        public async Task Should_return_200_when_filter_applicationId_is_provided()
        {
            using var client = SetUpClient();
            var response = await client.GetAsync("/v3/apiClients?applicationid=1");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Test]
        public async Task Should_return_400_when_offset_is_non_numeric()
        {
            using var client = SetUpClient();
            var response = await client.GetAsync("/v3/apiClients?offset=abc");
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Test]
        public async Task Should_return_400_when_limit_is_non_numeric()
        {
            using var client = SetUpClient();
            var response = await client.GetAsync("/v3/apiClients?limit=xyz");
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Test]
        public async Task Should_return_200_when_orderBy_omitted_with_direction()
        {
            using var client = SetUpClient();
            var response = await client.GetAsync("/v3/apiClients?direction=asc");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }
}
