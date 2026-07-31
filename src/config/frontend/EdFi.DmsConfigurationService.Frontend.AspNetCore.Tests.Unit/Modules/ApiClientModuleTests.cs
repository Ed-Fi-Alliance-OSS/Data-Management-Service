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
    private IApplicationLockManager _lockManager = A.Fake<IApplicationLockManager>();
    private readonly IApplicationRepository _applicationRepository = A.Fake<IApplicationRepository>();
    private readonly IVendorRepository _vendorRepository = A.Fake<IVendorRepository>();
    private readonly IDataStoreRepository _dataStoreRepository = A.Fake<IDataStoreRepository>();
    private readonly IIdentityProviderRepository _identityProviderRepository =
        A.Fake<IIdentityProviderRepository>();
    private readonly WebApplicationFactoryTracker<Program> _factoryTracker = new();

    public ApiClientModuleTests()
    {
        A.CallTo(() => _lockManager.AcquireAsync(A<long>.Ignored, A<CancellationToken>.Ignored))
            .ReturnsLazily(_ =>
                Task.FromResult<ApplicationLockResult>(
                    new ApplicationLockResult.Acquired(A.Fake<IAsyncDisposable>())
                )
            );

        A.CallTo(() =>
                _apiClientRepository.SyncApiClientUuid(A<long>.Ignored, A<Guid>.Ignored, A<Guid>.Ignored)
            )
            .Returns(new ApiClientUuidSyncResult.Success());

        A.CallTo(() => _apiClientRepository.HasApiClientUuidReference(A<Guid>.Ignored))
            .Returns(new ApiClientUuidReferenceResult.None());
    }

    [TearDown]
    public void DisposeWebApplicationFactories() => _factoryTracker.DisposeTrackedFactories();

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
                    .AddTransient((_) => _lockManager)
                    .AddTransient((_) => _applicationRepository)
                    .AddTransient((_) => _vendorRepository)
                    .AddTransient((_) => _dataStoreRepository)
                    .AddTransient((_) => _identityProviderRepository);
            });
        });
        _factoryTracker.Track(factory);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Scope", AuthorizationScopes.AdminScope.Name);
        return client;
    }

    private static async Task AssertContract(
        HttpResponseMessage response,
        HttpStatusCode status,
        string type,
        string title,
        string detail
    )
    {
        response.StatusCode.Should().Be(status);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        string content = await response.Content.ReadAsStringAsync();
        JsonNode actualResponse = JsonNode.Parse(content)!;
        string correlationId = actualResponse["correlationId"]!.GetValue<string>();
        correlationId.Should().NotBeNullOrEmpty();

        JsonNode expectedResponse = JsonNode.Parse(
            $$"""
            {
              "detail": "{{detail}}",
              "type": "{{type}}",
              "title": "{{title}}",
              "status": {{(int)status}},
              "correlationId": "{{correlationId}}",
              "validationErrors": {},
              "errors": []
            }
            """
        )!;
        JsonNode.DeepEquals(actualResponse, expectedResponse).Should().Be(true);
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
    public class Given_ApiClient_Lookup_Failures : ApiClientModuleTests
    {
        private const string Sentinel = "SENTINEL_APICLIENT_LOOKUP_must_not_leak";

        [SetUp]
        public void Setup()
        {
            A.CallTo(() => _apiClientRepository.GetApiClientById(A<long>.Ignored))
                .Returns(new ApiClientGetResult.FailureUnknown(Sentinel));
        }

        [Test]
        public async Task It_returns_internal_server_error_when_lookup_fails_on_update()
        {
            using var client = SetUpClient();

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

            await AssertContract(
                updateResponse,
                HttpStatusCode.InternalServerError,
                "urn:ed-fi:api:internal-server-error",
                "Internal Server Error",
                ""
            );
            (await updateResponse.Content.ReadAsStringAsync()).Should().NotContain(Sentinel);
        }

        [Test]
        public async Task It_returns_internal_server_error_when_lookup_fails_on_delete()
        {
            using var client = SetUpClient();

            var deleteResponse = await client.DeleteAsync("/v3/apiClients/1");

            await AssertContract(
                deleteResponse,
                HttpStatusCode.InternalServerError,
                "urn:ed-fi:api:internal-server-error",
                "Internal Server Error",
                ""
            );
            (await deleteResponse.Content.ReadAsStringAsync()).Should().NotContain(Sentinel);
        }

        [Test]
        public async Task It_returns_internal_server_error_when_lookup_fails_on_reset_credential()
        {
            using var client = SetUpClient();

            var resetResponse = await client.PutAsync(
                "/v3/apiClients/1/reset-credential",
                new StringContent("{}", Encoding.UTF8, "application/json")
            );

            await AssertContract(
                resetResponse,
                HttpStatusCode.InternalServerError,
                "urn:ed-fi:api:internal-server-error",
                "Internal Server Error",
                ""
            );
            (await resetResponse.Content.ReadAsStringAsync()).Should().NotContain(Sentinel);
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
            var existingUuid = Guid.NewGuid();
            var updatedClientUuid = Guid.NewGuid();
            var rollbackClientUuid = Guid.NewGuid();

            A.CallTo(() => _apiClientRepository.GetApiClientById(A<long>.Ignored))
                .Returns(
                    new ApiClientGetResult.Success(
                        new ApiClientResponse
                        {
                            Id = 1,
                            ApplicationId = 1,
                            ClientId = "test-client",
                            ClientUuid = existingUuid,
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
                .ReturnsNextFromSequence(
                    new ClientUpdateResult.Success(updatedClientUuid),
                    new ClientUpdateResult.Success(rollbackClientUuid)
                );

            A.CallTo(() => _apiClientRepository.UpdateApiClient(A<ApiClientUpdateCommand>.Ignored))
                .Returns(new ApiClientUpdateResult.FailureUnknown("Database error"));

            // The ambiguous outcome resolves to the exact original state, so the update
            // provably did not commit and compensation runs.
            A.CallTo(() => _apiClientRepository.GetApiClientResolutionState(A<long>.Ignored))
                .Returns(
                    new ApiClientResolutionResult.Success(
                        new ApiClientResolutionState(1, "Test", true, "test-client", existingUuid, [1])
                    )
                );

            List<(long Id, Guid ExpectedUuid, Guid NewUuid)> syncCalls = [];
            A.CallTo(() =>
                    _apiClientRepository.SyncApiClientUuid(A<long>.Ignored, A<Guid>.Ignored, A<Guid>.Ignored)
                )
                .Invokes(call =>
                    syncCalls.Add(
                        (call.GetArgument<long>(0), call.GetArgument<Guid>(1), call.GetArgument<Guid>(2))
                    )
                )
                .Returns(new ApiClientUuidSyncResult.Success());

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
            syncCalls.Should().Equal((1L, existingUuid, rollbackClientUuid));
        }

        [Test]
        public async Task It_returns_conflict_and_cleans_up_when_insert_application_not_found_at_repository()
        {
            List<string> deletedClientUuids = [];
            A.CallTo(() => _identityProviderRepository.DeleteClientAsync(A<string>.Ignored))
                .Invokes(call => deletedClientUuids.Add(call.GetArgument<string>(0)!))
                .Returns(new ClientDeleteResult.Success());

            A.CallTo(() =>
                    _apiClientRepository.InsertApiClient(
                        A<ApiClientInsertCommand>.Ignored,
                        A<ApiClientCommand>.Ignored
                    )
                )
                .Returns(new ApiClientInsertResult.FailureApplicationNotFound());

            using var client = SetUpClient();

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

            await AssertContract(
                insertResponse,
                HttpStatusCode.Conflict,
                "urn:ed-fi:api:conflict:unresolved-reference",
                "Unresolved Reference",
                "Application with ID 1 not found."
            );
            deletedClientUuids.Should().HaveCount(1);
        }

        [Test]
        public async Task It_returns_conflict_and_cleans_up_when_insert_data_store_not_found_at_repository()
        {
            List<string> deletedClientUuids = [];
            A.CallTo(() => _identityProviderRepository.DeleteClientAsync(A<string>.Ignored))
                .Invokes(call => deletedClientUuids.Add(call.GetArgument<string>(0)!))
                .Returns(new ClientDeleteResult.Success());

            A.CallTo(() =>
                    _apiClientRepository.InsertApiClient(
                        A<ApiClientInsertCommand>.Ignored,
                        A<ApiClientCommand>.Ignored
                    )
                )
                .Returns(new ApiClientInsertResult.FailureDataStoreNotFound());

            using var client = SetUpClient();

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

            await AssertContract(
                insertResponse,
                HttpStatusCode.Conflict,
                "urn:ed-fi:api:conflict:unresolved-reference",
                "Unresolved Reference",
                "Data store does not exist."
            );
            deletedClientUuids.Should().HaveCount(1);
        }

        [Test]
        public async Task It_returns_not_found_and_deletes_the_recreated_client_when_update_target_vanishes_at_repository()
        {
            var updatedClientUuid = Guid.NewGuid();
            List<string> updateClientUuidsCalled = [];
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
                .Invokes(call => updateClientUuidsCalled.Add(call.GetArgument<string>(0)!))
                .Returns(new ClientUpdateResult.Success(updatedClientUuid));

            A.CallTo(() => _apiClientRepository.UpdateApiClient(A<ApiClientUpdateCommand>.Ignored))
                .Returns(new ApiClientUpdateResult.FailureNotFound());

            List<string> deletedClientUuids = [];
            A.CallTo(() => _identityProviderRepository.DeleteClientAsync(A<string>.Ignored))
                .Invokes(call => deletedClientUuids.Add(call.GetArgument<string>(0)!))
                .Returns(new ClientDeleteResult.Success());

            using var client = SetUpClient();

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

            await AssertContract(
                updateResponse,
                HttpStatusCode.NotFound,
                "urn:ed-fi:api:not-found",
                "Not Found",
                "ApiClient with ID 1 not found."
            );
            updateClientUuidsCalled.Should().HaveCount(1);
            deletedClientUuids.Should().Equal(updatedClientUuid.ToString());
        }

        [Test]
        public async Task It_returns_conflict_and_rolls_back_when_update_application_not_found_at_repository()
        {
            List<string> updateClientUuidsCalled = [];
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
                .Invokes(call => updateClientUuidsCalled.Add(call.GetArgument<string>(0)!))
                .Returns(new ClientUpdateResult.Success(Guid.NewGuid()));

            A.CallTo(() => _apiClientRepository.UpdateApiClient(A<ApiClientUpdateCommand>.Ignored))
                .Returns(new ApiClientUpdateResult.FailureApplicationNotFound());

            using var client = SetUpClient();

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

            await AssertContract(
                updateResponse,
                HttpStatusCode.Conflict,
                "urn:ed-fi:api:conflict:unresolved-reference",
                "Unresolved Reference",
                "Application with ID 1 not found."
            );
            updateClientUuidsCalled.Should().HaveCount(2);
        }

        [Test]
        public async Task It_returns_conflict_and_rolls_back_when_update_data_store_not_found_at_repository()
        {
            List<string> updateClientUuidsCalled = [];
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
                .Invokes(call => updateClientUuidsCalled.Add(call.GetArgument<string>(0)!))
                .Returns(new ClientUpdateResult.Success(Guid.NewGuid()));

            A.CallTo(() => _apiClientRepository.UpdateApiClient(A<ApiClientUpdateCommand>.Ignored))
                .Returns(new ApiClientUpdateResult.FailureDataStoreNotFound());

            using var client = SetUpClient();

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

            await AssertContract(
                updateResponse,
                HttpStatusCode.Conflict,
                "urn:ed-fi:api:conflict:unresolved-reference",
                "Unresolved Reference",
                "Data store does not exist."
            );
            updateClientUuidsCalled.Should().HaveCount(2);
        }
    }

    [TestFixture]
    public class Given_Invalid_Vendor_Reference : ApiClientModuleTests
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
                            VendorId = 999,
                            EducationOrganizationIds = [1],
                            DataStoreIds = [1],
                        }
                    )
                );

            A.CallTo(() => _vendorRepository.GetVendor(A<long>.Ignored))
                .Returns(new VendorGetResult.FailureNotFound());

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
        public async Task It_returns_internal_server_error_on_insert_when_inherited_vendor_is_unresolvable()
        {
            using var client = SetUpClient();

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

            await AssertContract(
                insertResponse,
                HttpStatusCode.InternalServerError,
                "urn:ed-fi:api:internal-server-error",
                "Internal Server Error",
                ""
            );
        }

        [Test]
        public async Task It_returns_internal_server_error_on_update_when_inherited_vendor_is_unresolvable()
        {
            using var client = SetUpClient();

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

            await AssertContract(
                updateResponse,
                HttpStatusCode.InternalServerError,
                "urn:ed-fi:api:internal-server-error",
                "Internal Server Error",
                ""
            );
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
        public async Task It_returns_conflict_for_nonexistent_application_on_insert()
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
            await AssertContract(
                insertResponse,
                HttpStatusCode.Conflict,
                "urn:ed-fi:api:conflict:unresolved-reference",
                "Unresolved Reference",
                "Application with ID 999 not found."
            );
        }

        [Test]
        public async Task It_returns_conflict_for_nonexistent_application_on_update()
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
            await AssertContract(
                updateResponse,
                HttpStatusCode.Conflict,
                "urn:ed-fi:api:conflict:unresolved-reference",
                "Unresolved Reference",
                "Application with ID 999 not found."
            );
        }

        [Test]
        public async Task It_returns_internal_server_error_when_application_lookup_fails_on_insert()
        {
            const string Sentinel = "SENTINEL_APPLICATION_LOOKUP_INSERT_must_not_leak";
            A.CallTo(() => _applicationRepository.GetApplication(A<long>.Ignored))
                .Returns(new ApplicationGetResult.FailureUnknown(Sentinel));

            using var client = SetUpClient();

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

            await AssertContract(
                insertResponse,
                HttpStatusCode.InternalServerError,
                "urn:ed-fi:api:internal-server-error",
                "Internal Server Error",
                ""
            );
            (await insertResponse.Content.ReadAsStringAsync()).Should().NotContain(Sentinel);
        }

        [Test]
        public async Task It_returns_internal_server_error_when_application_lookup_fails_on_update()
        {
            const string Sentinel = "SENTINEL_APPLICATION_LOOKUP_UPDATE_must_not_leak";
            A.CallTo(() => _applicationRepository.GetApplication(A<long>.Ignored))
                .Returns(new ApplicationGetResult.FailureUnknown(Sentinel));

            using var client = SetUpClient();

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

            await AssertContract(
                updateResponse,
                HttpStatusCode.InternalServerError,
                "urn:ed-fi:api:internal-server-error",
                "Internal Server Error",
                ""
            );
            (await updateResponse.Content.ReadAsStringAsync()).Should().NotContain(Sentinel);
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
        public async Task It_returns_conflict_for_nonexistent_data_store_on_insert()
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
            await AssertContract(
                insertResponse,
                HttpStatusCode.Conflict,
                "urn:ed-fi:api:conflict:unresolved-reference",
                "Unresolved Reference",
                "The following DataStoreIds were not found in database: 999, 888"
            );
        }

        [Test]
        public async Task It_returns_conflict_for_nonexistent_data_store_on_update()
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
            await AssertContract(
                updateResponse,
                HttpStatusCode.Conflict,
                "urn:ed-fi:api:conflict:unresolved-reference",
                "Unresolved Reference",
                "The following DataStoreIds were not found in database: 999, 888"
            );
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

        [Test]
        public async Task It_returns_internal_server_error_when_stored_client_is_missing_on_update()
        {
            const string Sentinel = "SENTINEL_IDP_CLIENT_MISSING_UPDATE_must_not_leak";
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
                .Returns(new ClientUpdateResult.FailureNotFound(Sentinel));

            using var client = SetUpClient();

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

            await AssertContract(
                updateResponse,
                HttpStatusCode.InternalServerError,
                "urn:ed-fi:api:internal-server-error",
                "Internal Server Error",
                ""
            );
            (await updateResponse.Content.ReadAsStringAsync()).Should().NotContain(Sentinel);
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
        public async Task It_returns_internal_server_error_when_stored_client_is_missing_in_identity_provider()
        {
            // Arrange
            const string Sentinel = "SENTINEL_RESET_CLIENT_MISSING_must_not_leak";
            A.CallTo(() => _identityProviderRepository.ResetCredentialsAsync(A<string>.Ignored))
                .Returns(new ClientResetResult.FailureClientNotFound(Sentinel));

            using var client = SetUpClient();

            // Act
            var resetResponse = await client.PutAsync(
                "/v3/apiClients/1/reset-credential",
                new StringContent("{}", Encoding.UTF8, "application/json")
            );

            // Assert
            await AssertContract(
                resetResponse,
                HttpStatusCode.InternalServerError,
                "urn:ed-fi:api:internal-server-error",
                "Internal Server Error",
                ""
            );
            (await resetResponse.Content.ReadAsStringAsync()).Should().NotContain(Sentinel);
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

    [TestFixture]
    public class Given_an_api_client_update_whose_stored_provider_client_is_missing : ApiClientModuleTests
    {
        private const string Sentinel = "SENTINEL_APICLIENT_STORED_CLIENT_MISSING_must_not_leak";

        private RecordingLockManager _recordingLockManager = null!;
        private List<string> _repositoryUpdates = null!;
        private HttpResponseMessage _updateResponse = null!;

        [SetUp]
        public async Task Act()
        {
            _repositoryUpdates = [];
            _recordingLockManager = new RecordingLockManager();
            _lockManager = _recordingLockManager;

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
                .Returns(new ClientUpdateResult.FailureNotFound(Sentinel));

            A.CallTo(() => _apiClientRepository.UpdateApiClient(A<ApiClientUpdateCommand>.Ignored))
                .Invokes(_ => _repositoryUpdates.Add("update"))
                .Returns(new ApiClientUpdateResult.Success());

            using var client = SetUpClient();
            _updateResponse = await client.PutAsync(
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
        }

        [TearDown]
        public void TearDownResponse() => _updateResponse?.Dispose();

        [Test]
        public async Task It_returns_a_sanitized_internal_server_error()
        {
            await AssertContract(
                _updateResponse,
                HttpStatusCode.InternalServerError,
                "urn:ed-fi:api:internal-server-error",
                "Internal Server Error",
                ""
            );
            (await _updateResponse.Content.ReadAsStringAsync()).Should().NotContain(Sentinel);
        }

        [Test]
        public void It_does_not_update_the_database_api_client() => _repositoryUpdates.Should().BeEmpty();

        [Test]
        public void It_does_not_synchronize_the_client_uuid() =>
            A.CallTo(() =>
                    _apiClientRepository.SyncApiClientUuid(A<long>.Ignored, A<Guid>.Ignored, A<Guid>.Ignored)
                )
                .MustNotHaveHappened();

        [Test]
        public void It_acquires_and_releases_the_aggregate_lock()
        {
            _recordingLockManager.AcquiredApplicationIds.Should().Equal(1L);
            _recordingLockManager.Handles.Should().OnlyContain(handle => handle.Disposed);
        }
    }

    [TestFixture]
    public class Given_an_api_client_update_with_mismatched_route_and_body_ids : ApiClientModuleTests
    {
        private List<string> _dependencyCalls = null!;
        private HttpResponseMessage _updateResponse = null!;

        [SetUp]
        public async Task Act()
        {
            _dependencyCalls = [];
            A.CallTo(_apiClientRepository).Invokes(call => _dependencyCalls.Add(call.Method.Name));
            A.CallTo(_applicationRepository).Invokes(call => _dependencyCalls.Add(call.Method.Name));
            A.CallTo(_vendorRepository).Invokes(call => _dependencyCalls.Add(call.Method.Name));
            A.CallTo(_dataStoreRepository).Invokes(call => _dependencyCalls.Add(call.Method.Name));
            A.CallTo(_identityProviderRepository).Invokes(call => _dependencyCalls.Add(call.Method.Name));

            using var client = SetUpClient();
            _updateResponse = await client.PutAsync(
                "/v3/apiClients/1",
                new StringContent(
                    """
                    {
                      "id": 9999,
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
        }

        [TearDown]
        public void TearDownResponse() => _updateResponse?.Dispose();

        [Test]
        public async Task It_returns_the_id_mismatch_validation_contract()
        {
            _updateResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            _updateResponse.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
            string responseBody = await _updateResponse.Content.ReadAsStringAsync();
            JsonNode actualResponse = JsonNode.Parse(responseBody)!;
            string correlationId = actualResponse["correlationId"]!.GetValue<string>();
            correlationId.Should().NotBeNullOrWhiteSpace();
            JsonNode expectedResponse = JsonNode.Parse(
                """
                {
                  "detail": "Data validation failed. See 'validationErrors' for details.",
                  "type": "urn:ed-fi:api:bad-request:data",
                  "title": "Data Validation Failed",
                  "status": 400,
                  "correlationId": "{correlationId}",
                  "validationErrors": {
                    "Id": [
                      "Request body id must match the id in the url."
                    ]
                  },
                  "errors": []
                }
                """.Replace("{correlationId}", correlationId)
            )!;
            JsonNode.DeepEquals(actualResponse, expectedResponse).Should().Be(true);
        }

        [Test]
        public void It_calls_no_repository_or_identity_provider_dependency() =>
            _dependencyCalls.Should().BeEmpty();
    }

    public abstract class DeleteWorkflowTestBase : ApiClientModuleTests
    {
        protected Guid _providerClientUuid;
        protected HttpResponseMessage _deleteResponse = null!;
        protected List<string> _providerDeletes = null!;
        protected List<long> _databaseDeletes = null!;
        protected List<string> _recreatedClientIds = null!;

        [SetUp]
        public void SetUpDeleteDefaults()
        {
            _providerClientUuid = Guid.NewGuid();
            _providerDeletes = [];
            _databaseDeletes = [];
            _recreatedClientIds = [];

            A.CallTo(() => _apiClientRepository.GetApiClientById(A<long>.Ignored))
                .Returns(
                    new ApiClientGetResult.Success(
                        new ApiClientResponse
                        {
                            Id = 1,
                            ApplicationId = 1,
                            ClientId = "test-client",
                            ClientUuid = _providerClientUuid,
                            Name = "Test",
                            IsApproved = true,
                            DataStoreIds = [1],
                        }
                    )
                );

            ArrangeProviderDelete(new ClientDeleteResult.FailureClientNotFound("Client not found"));

            // Tripwire: the delete workflow must never create a provider client, so every
            // fixture can assert this recorder stays empty.
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
                .Invokes(call => _recreatedClientIds.Add(call.GetArgument<string>(0)!))
                .Returns(new ClientCreateResult.Success(Guid.NewGuid()));
        }

        [TearDown]
        public void TearDownResponse() => _deleteResponse?.Dispose();

        protected void ArrangeProviderDelete(ClientDeleteResult result)
        {
            A.CallTo(() => _identityProviderRepository.DeleteClientAsync(A<string>.Ignored))
                .Invokes(call => _providerDeletes.Add(call.GetArgument<string>(0)!))
                .Returns(result);
        }

        protected void ArrangeDatabaseDelete(ApiClientDeleteResult result)
        {
            A.CallTo(() => _apiClientRepository.DeleteApiClient(A<long>.Ignored))
                .Invokes(call => _databaseDeletes.Add(call.GetArgument<long>(0)))
                .Returns(result);
        }

        protected void ArrangeResolutionState(ApiClientResolutionResult result)
        {
            A.CallTo(() => _apiClientRepository.GetApiClientResolutionState(A<long>.Ignored)).Returns(result);
        }

        protected ApiClientResolutionState SurvivingRowState() =>
            new(1, "Test", true, "test-client", _providerClientUuid, [1]);

        protected async Task ActDeleteAsync()
        {
            using var client = SetUpClient();
            _deleteResponse = await client.DeleteAsync("/v3/apiClients/1");
        }
    }

    [TestFixture]
    public class Given_an_api_client_delete_whose_identity_provider_client_is_already_missing
        : DeleteWorkflowTestBase
    {
        [SetUp]
        public async Task Act()
        {
            ArrangeDatabaseDelete(new ApiClientDeleteResult.Success());
            await ActDeleteAsync();
        }

        [Test]
        public void It_returns_no_content() =>
            _deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        [Test]
        public void It_invokes_the_provider_delete_for_the_stored_client() =>
            _providerDeletes.Should().Equal(_providerClientUuid.ToString());

        [Test]
        public void It_invokes_the_database_delete() => _databaseDeletes.Should().Equal(1L);

        [Test]
        public void It_does_not_recreate_a_provider_client() => _recreatedClientIds.Should().BeEmpty();
    }

    [TestFixture]
    public class Given_an_api_client_delete_whose_provider_client_and_database_row_are_already_gone
        : DeleteWorkflowTestBase
    {
        [SetUp]
        public async Task Act()
        {
            ArrangeDatabaseDelete(new ApiClientDeleteResult.FailureNotFound());
            await ActDeleteAsync();
        }

        [Test]
        public async Task It_returns_the_not_found_contract() =>
            await AssertContract(
                _deleteResponse,
                HttpStatusCode.NotFound,
                "urn:ed-fi:api:not-found",
                "Not Found",
                "ApiClient not found"
            );

        [Test]
        public void It_invokes_the_provider_delete_for_the_stored_client() =>
            _providerDeletes.Should().Equal(_providerClientUuid.ToString());

        [Test]
        public void It_invokes_the_database_delete() => _databaseDeletes.Should().Equal(1L);

        [Test]
        public void It_does_not_recreate_a_provider_client() => _recreatedClientIds.Should().BeEmpty();
    }

    [TestFixture]
    public class Given_an_api_client_delete_whose_provider_client_is_missing_and_the_database_delete_fails
        : DeleteWorkflowTestBase
    {
        private const string Sentinel = "SENTINEL_DB_DELETE_5c8a_must_not_leak";

        [SetUp]
        public async Task Act()
        {
            ArrangeDatabaseDelete(new ApiClientDeleteResult.FailureUnknown(Sentinel));
            ArrangeResolutionState(new ApiClientResolutionResult.Success(SurvivingRowState()));
            await ActDeleteAsync();
        }

        [Test]
        public async Task It_returns_a_sanitized_internal_server_error()
        {
            await AssertContract(
                _deleteResponse,
                HttpStatusCode.InternalServerError,
                "urn:ed-fi:api:internal-server-error",
                "Internal Server Error",
                ""
            );
            string responseBody = await _deleteResponse.Content.ReadAsStringAsync();
            responseBody.Should().NotContain(Sentinel);
        }

        [Test]
        public void It_invokes_the_provider_delete_for_the_stored_client() =>
            _providerDeletes.Should().Equal(_providerClientUuid.ToString());

        [Test]
        public void It_invokes_the_database_delete() => _databaseDeletes.Should().Equal(1L);

        [Test]
        public void It_does_not_recreate_a_provider_client() => _recreatedClientIds.Should().BeEmpty();
    }

    [TestFixture]
    public class Given_an_api_client_delete_whose_provider_deletion_fails_at_the_identity_provider
        : DeleteWorkflowTestBase
    {
        private const string Sentinel = "SENTINEL_APICLIENT_PROVIDER_DELETE_502_must_not_leak";

        [SetUp]
        public async Task Act()
        {
            ArrangeDatabaseDelete(new ApiClientDeleteResult.Success());
            ArrangeProviderDelete(
                new ClientDeleteResult.FailureIdentityProvider(new IdentityProviderError(Sentinel))
            );
            await ActDeleteAsync();
        }

        [Test]
        public async Task It_returns_the_bad_gateway_contract()
        {
            _deleteResponse.StatusCode.Should().Be(HttpStatusCode.BadGateway);
            _deleteResponse.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
            string responseBody = await _deleteResponse.Content.ReadAsStringAsync();
            responseBody.Should().NotContain(Sentinel);
            JsonNode actualResponse = JsonNode.Parse(responseBody)!;
            JsonNode expectedResponse = JsonNode.Parse(
                """
                {
                  "detail": "The request could not be processed. See 'errors' for details.",
                  "type": "urn:ed-fi:api:bad-gateway",
                  "title": "Bad Gateway",
                  "status": 502,
                  "correlationId": "{correlationId}",
                  "validationErrors": {},
                  "errors": ["The identity provider returned an unexpected response."]
                }
                """.Replace("{correlationId}", actualResponse["correlationId"]!.GetValue<string>())
            )!;
            JsonNode.DeepEquals(actualResponse, expectedResponse).Should().Be(true);
        }

        [Test]
        public void It_does_not_delete_the_database_row() => _databaseDeletes.Should().BeEmpty();

        [Test]
        public void It_does_not_create_a_provider_client() => _recreatedClientIds.Should().BeEmpty();
    }

    [TestFixture]
    public class Given_an_api_client_delete_whose_provider_deletion_fails_unknown : DeleteWorkflowTestBase
    {
        private const string Sentinel = "SENTINEL_APICLIENT_PROVIDER_DELETE_500_must_not_leak";

        [SetUp]
        public async Task Act()
        {
            ArrangeDatabaseDelete(new ApiClientDeleteResult.Success());
            ArrangeProviderDelete(new ClientDeleteResult.FailureUnknown(Sentinel));
            await ActDeleteAsync();
        }

        [Test]
        public async Task It_returns_a_sanitized_internal_server_error()
        {
            await AssertContract(
                _deleteResponse,
                HttpStatusCode.InternalServerError,
                "urn:ed-fi:api:internal-server-error",
                "Internal Server Error",
                ""
            );
            (await _deleteResponse.Content.ReadAsStringAsync()).Should().NotContain(Sentinel);
        }

        [Test]
        public void It_does_not_delete_the_database_row() => _databaseDeletes.Should().BeEmpty();

        [Test]
        public void It_does_not_create_a_provider_client() => _recreatedClientIds.Should().BeEmpty();
    }

    [TestFixture]
    public class Given_an_api_client_delete_whose_provider_deletion_throws : DeleteWorkflowTestBase
    {
        private const string Sentinel = "SENTINEL_APICLIENT_PROVIDER_THROWN_must_not_leak";

        [SetUp]
        public async Task Act()
        {
            ArrangeDatabaseDelete(new ApiClientDeleteResult.Success());
            A.CallTo(() => _identityProviderRepository.DeleteClientAsync(A<string>.Ignored))
                .Throws(new InvalidOperationException(Sentinel));
            await ActDeleteAsync();
        }

        [Test]
        public async Task It_returns_a_sanitized_internal_server_error()
        {
            await AssertContract(
                _deleteResponse,
                HttpStatusCode.InternalServerError,
                "urn:ed-fi:api:internal-server-error",
                "Internal Server Error",
                ""
            );
            (await _deleteResponse.Content.ReadAsStringAsync()).Should().NotContain(Sentinel);
        }

        [Test]
        public void It_does_not_delete_the_database_row() => _databaseDeletes.Should().BeEmpty();

        [Test]
        public void It_does_not_create_a_provider_client() => _recreatedClientIds.Should().BeEmpty();
    }

    [TestFixture]
    public class Given_an_api_client_delete_whose_provider_deletion_returns_an_unrecognized_result
        : DeleteWorkflowTestBase
    {
        private sealed record UnrecognizedClientDeleteResult() : ClientDeleteResult;

        [SetUp]
        public async Task Act()
        {
            ArrangeDatabaseDelete(new ApiClientDeleteResult.Success());
            ArrangeProviderDelete(new UnrecognizedClientDeleteResult());
            await ActDeleteAsync();
        }

        [Test]
        public async Task It_returns_a_sanitized_internal_server_error() =>
            await AssertContract(
                _deleteResponse,
                HttpStatusCode.InternalServerError,
                "urn:ed-fi:api:internal-server-error",
                "Internal Server Error",
                ""
            );

        [Test]
        public void It_does_not_delete_the_database_row() => _databaseDeletes.Should().BeEmpty();

        [Test]
        public void It_does_not_create_a_provider_client() => _recreatedClientIds.Should().BeEmpty();
    }

    [TestFixture]
    public class Given_an_api_client_delete_whose_database_row_was_already_absent : DeleteWorkflowTestBase
    {
        [SetUp]
        public async Task Act()
        {
            ArrangeProviderDelete(new ClientDeleteResult.Success());
            ArrangeDatabaseDelete(new ApiClientDeleteResult.FailureNotFound());
            await ActDeleteAsync();
        }

        [Test]
        public async Task It_returns_the_not_found_contract() =>
            await AssertContract(
                _deleteResponse,
                HttpStatusCode.NotFound,
                "urn:ed-fi:api:not-found",
                "Not Found",
                "ApiClient not found"
            );

        [Test]
        public void It_deletes_the_provider_client() =>
            _providerDeletes.Should().Equal(_providerClientUuid.ToString());

        [Test]
        public void It_does_not_create_a_provider_client() => _recreatedClientIds.Should().BeEmpty();

        [Test]
        public void It_does_not_synchronize_a_uuid() =>
            A.CallTo(() =>
                    _apiClientRepository.SyncApiClientUuid(A<long>.Ignored, A<Guid>.Ignored, A<Guid>.Ignored)
                )
                .MustNotHaveHappened();
    }

    [TestFixture]
    public class Given_an_api_client_delete_whose_ambiguous_database_delete_committed : DeleteWorkflowTestBase
    {
        [SetUp]
        public async Task Act()
        {
            ArrangeProviderDelete(new ClientDeleteResult.Success());
            ArrangeDatabaseDelete(new ApiClientDeleteResult.FailureUnknown("connection dropped"));
            ArrangeResolutionState(new ApiClientResolutionResult.FailureNotExists());
            await ActDeleteAsync();
        }

        [Test]
        public void It_returns_no_content() =>
            _deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        [Test]
        public void It_does_not_create_a_provider_client() => _recreatedClientIds.Should().BeEmpty();
    }

    [TestFixture]
    public class Given_an_api_client_delete_whose_database_delete_fails_with_a_surviving_row
        : DeleteWorkflowTestBase
    {
        private const string Sentinel = "SENTINEL_APICLIENT_DB_DELETE_500_must_not_leak";
        private RecordingLockManager _recordingLockManager = null!;
        private bool _lockDisposedDuringResolution;

        [SetUp]
        public async Task Act()
        {
            _recordingLockManager = new RecordingLockManager();
            _lockManager = _recordingLockManager;

            ArrangeProviderDelete(new ClientDeleteResult.Success());
            ArrangeDatabaseDelete(new ApiClientDeleteResult.FailureUnknown(Sentinel));
            A.CallTo(() => _apiClientRepository.GetApiClientResolutionState(A<long>.Ignored))
                .ReturnsLazily(_ =>
                {
                    _lockDisposedDuringResolution = _recordingLockManager.Handles.Exists(handle =>
                        handle.Disposed
                    );
                    return Task.FromResult<ApiClientResolutionResult>(
                        new ApiClientResolutionResult.Success(SurvivingRowState())
                    );
                });
            await ActDeleteAsync();
        }

        [Test]
        public async Task It_returns_a_sanitized_internal_server_error()
        {
            await AssertContract(
                _deleteResponse,
                HttpStatusCode.InternalServerError,
                "urn:ed-fi:api:internal-server-error",
                "Internal Server Error",
                ""
            );
            (await _deleteResponse.Content.ReadAsStringAsync()).Should().NotContain(Sentinel);
        }

        [Test]
        public void It_does_not_create_a_provider_client() => _recreatedClientIds.Should().BeEmpty();

        [Test]
        public void It_holds_the_lock_through_the_outcome_resolution()
        {
            _recordingLockManager.AcquiredApplicationIds.Should().Equal(1L);
            _lockDisposedDuringResolution.Should().BeFalse();
            _recordingLockManager.Handles.Should().OnlyContain(handle => handle.Disposed);
        }
    }

    [TestFixture]
    public class Given_an_api_client_delete_whose_database_delete_throws : DeleteWorkflowTestBase
    {
        private const string Sentinel = "SENTINEL_APICLIENT_DB_DELETE_THROWN_must_not_leak";
        private List<long> _resolutionReads = null!;

        [SetUp]
        public async Task Act()
        {
            _resolutionReads = [];
            ArrangeProviderDelete(new ClientDeleteResult.Success());
            A.CallTo(() => _apiClientRepository.DeleteApiClient(A<long>.Ignored))
                .Throws(new InvalidOperationException(Sentinel));
            A.CallTo(() => _apiClientRepository.GetApiClientResolutionState(A<long>.Ignored))
                .Invokes(call => _resolutionReads.Add(call.GetArgument<long>(0)))
                .Returns(new ApiClientResolutionResult.Success(SurvivingRowState()));
            await ActDeleteAsync();
        }

        [Test]
        public async Task It_returns_a_sanitized_internal_server_error()
        {
            await AssertContract(
                _deleteResponse,
                HttpStatusCode.InternalServerError,
                "urn:ed-fi:api:internal-server-error",
                "Internal Server Error",
                ""
            );
            (await _deleteResponse.Content.ReadAsStringAsync()).Should().NotContain(Sentinel);
        }

        [Test]
        public void It_resolves_the_outcome_before_answering() => _resolutionReads.Should().Equal(1L);

        [Test]
        public void It_does_not_create_a_provider_client() => _recreatedClientIds.Should().BeEmpty();
    }

    [TestFixture]
    public class Given_an_api_client_delete_whose_outcome_resolution_fails : DeleteWorkflowTestBase
    {
        private const string DeleteSentinel = "SENTINEL_APICLIENT_DB_DELETE_must_not_leak";
        private const string ResolutionSentinel = "SENTINEL_APICLIENT_RESOLUTION_must_not_leak";

        [SetUp]
        public async Task Act()
        {
            ArrangeProviderDelete(new ClientDeleteResult.Success());
            ArrangeDatabaseDelete(new ApiClientDeleteResult.FailureUnknown(DeleteSentinel));
            ArrangeResolutionState(new ApiClientResolutionResult.FailureUnknown(ResolutionSentinel));
            await ActDeleteAsync();
        }

        [Test]
        public async Task It_returns_a_sanitized_internal_server_error()
        {
            await AssertContract(
                _deleteResponse,
                HttpStatusCode.InternalServerError,
                "urn:ed-fi:api:internal-server-error",
                "Internal Server Error",
                ""
            );
            string responseBody = await _deleteResponse.Content.ReadAsStringAsync();
            responseBody.Should().NotContain(DeleteSentinel);
            responseBody.Should().NotContain(ResolutionSentinel);
        }

        [Test]
        public void It_does_not_create_a_provider_client() => _recreatedClientIds.Should().BeEmpty();
    }

    [TestFixture]
    public class Given_an_api_client_delete_whose_outcome_resolution_throws : DeleteWorkflowTestBase
    {
        private const string Sentinel = "SENTINEL_APICLIENT_RESOLUTION_THROWN_must_not_leak";
        private RecordingLockManager _recordingLockManager = null!;

        [SetUp]
        public async Task Act()
        {
            _recordingLockManager = new RecordingLockManager();
            _lockManager = _recordingLockManager;

            ArrangeProviderDelete(new ClientDeleteResult.Success());
            ArrangeDatabaseDelete(new ApiClientDeleteResult.FailureUnknown("database delete failed"));
            A.CallTo(() => _apiClientRepository.GetApiClientResolutionState(A<long>.Ignored))
                .Throws(new InvalidOperationException(Sentinel));
            await ActDeleteAsync();
        }

        [Test]
        public async Task It_returns_a_sanitized_internal_server_error()
        {
            await AssertContract(
                _deleteResponse,
                HttpStatusCode.InternalServerError,
                "urn:ed-fi:api:internal-server-error",
                "Internal Server Error",
                ""
            );
            (await _deleteResponse.Content.ReadAsStringAsync()).Should().NotContain(Sentinel);
        }

        [Test]
        public void It_releases_the_aggregate_lock()
        {
            _recordingLockManager.AcquiredApplicationIds.Should().Equal(1L);
            _recordingLockManager.Handles.Should().OnlyContain(handle => handle.Disposed);
        }

        [Test]
        public void It_does_not_create_a_provider_client() => _recreatedClientIds.Should().BeEmpty();
    }

    private sealed class RecordingLockHandle : IAsyncDisposable
    {
        public bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingLockManager : IApplicationLockManager
    {
        public List<RecordingLockHandle> Handles { get; } = [];
        public List<long> AcquiredApplicationIds { get; } = [];

        public Task<ApplicationLockResult> AcquireAsync(
            long applicationId,
            CancellationToken cancellationToken
        )
        {
            AcquiredApplicationIds.Add(applicationId);
            var handle = new RecordingLockHandle();
            Handles.Add(handle);
            return Task.FromResult<ApplicationLockResult>(new ApplicationLockResult.Acquired(handle));
        }
    }

    private static async Task AssertSanitizedInternalServerError(
        HttpResponseMessage response,
        params string[] sentinels
    )
    {
        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        string responseBody = await response.Content.ReadAsStringAsync();
        foreach (string sentinel in sentinels)
        {
            responseBody.Should().NotContain(sentinel);
        }
    }

    private static async Task AssertLockConflictContract(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        string responseBody = await response.Content.ReadAsStringAsync();
        JsonNode actualResponse = JsonNode.Parse(responseBody)!;
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

    public abstract class UpdateUnderLockTestBase : ApiClientModuleTests
    {
        protected Guid _existingUuid;
        protected HttpResponseMessage _updateResponse = null!;

        [SetUp]
        public void SetUpUpdateDefaults()
        {
            _existingUuid = Guid.NewGuid();

            A.CallTo(() => _apiClientRepository.GetApiClientById(A<long>.Ignored))
                .Returns(
                    new ApiClientGetResult.Success(
                        new ApiClientResponse
                        {
                            Id = 1,
                            ApplicationId = 1,
                            ClientId = "test-client",
                            ClientUuid = _existingUuid,
                            Name = "Test",
                            IsApproved = true,
                            DataStoreIds = [1],
                        }
                    )
                );

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
                .ReturnsLazily(call =>
                {
                    long[] ids = call.GetArgument<long[]>(0) ?? [];
                    return Task.FromResult<DataStoreIdsExistResult>(
                        new DataStoreIdsExistResult.Success([.. ids])
                    );
                });

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
        }

        [TearDown]
        public void TearDownResponse() => _updateResponse?.Dispose();

        protected async Task ActUpdateAsync(long applicationId = 1)
        {
            using var client = SetUpClient();
            _updateResponse = await client.PutAsync(
                "/v3/apiClients/1",
                new StringContent(
                    $$"""
                    {
                      "id": 1,
                      "applicationId": {{applicationId}},
                      "name": "Updated",
                      "isApproved": true,
                      "dataStoreIds": [1]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );
        }
    }

    [TestFixture]
    public class Given_an_api_client_update_when_the_aggregate_lock_times_out : UpdateUnderLockTestBase
    {
        private List<string> _dependencyCalls = null!;
        private List<string> _databaseUpdates = null!;

        [SetUp]
        public async Task Act()
        {
            _dependencyCalls = [];
            _databaseUpdates = [];
            A.CallTo(() => _lockManager.AcquireAsync(A<long>.Ignored, A<CancellationToken>.Ignored))
                .Returns(new ApplicationLockResult.FailureTimeout());
            A.CallTo(_identityProviderRepository).Invokes(call => _dependencyCalls.Add(call.Method.Name));
            A.CallTo(_applicationRepository).Invokes(call => _dependencyCalls.Add(call.Method.Name));
            A.CallTo(_vendorRepository).Invokes(call => _dependencyCalls.Add(call.Method.Name));
            A.CallTo(_dataStoreRepository).Invokes(call => _dependencyCalls.Add(call.Method.Name));
            A.CallTo(() => _apiClientRepository.UpdateApiClient(A<ApiClientUpdateCommand>.Ignored))
                .Invokes(_ => _databaseUpdates.Add("UpdateApiClient"))
                .Returns(new ApiClientUpdateResult.Success());

            await ActUpdateAsync();
        }

        [Test]
        public async Task It_returns_the_retriable_conflict_contract() =>
            await AssertLockConflictContract(_updateResponse);

        [Test]
        public void It_calls_nothing_beyond_the_pre_read()
        {
            _dependencyCalls.Should().BeEmpty();
            _databaseUpdates.Should().BeEmpty();
        }
    }

    [TestFixture]
    public class Given_an_api_client_update_moving_to_a_higher_application_id : UpdateUnderLockTestBase
    {
        private RecordingLockManager _recordingLockManager = null!;

        [SetUp]
        public async Task Act()
        {
            _recordingLockManager = new RecordingLockManager();
            _lockManager = _recordingLockManager;

            await ActUpdateAsync(applicationId: 2);
        }

        [Test]
        public void It_returns_no_content() =>
            _updateResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        [Test]
        public void It_acquires_both_locks_in_ascending_order() =>
            _recordingLockManager.AcquiredApplicationIds.Should().Equal(1L, 2L);

        [Test]
        public void It_releases_every_lock() =>
            _recordingLockManager.Handles.Should().OnlyContain(handle => handle.Disposed);
    }

    [TestFixture]
    public class Given_an_api_client_update_moving_to_a_lower_application_id : UpdateUnderLockTestBase
    {
        private RecordingLockManager _recordingLockManager = null!;

        [SetUp]
        public async Task Act()
        {
            _recordingLockManager = new RecordingLockManager();
            _lockManager = _recordingLockManager;

            A.CallTo(() => _apiClientRepository.GetApiClientById(A<long>.Ignored))
                .Returns(
                    new ApiClientGetResult.Success(
                        new ApiClientResponse
                        {
                            Id = 1,
                            ApplicationId = 5,
                            ClientId = "test-client",
                            ClientUuid = _existingUuid,
                            Name = "Test",
                            IsApproved = true,
                            DataStoreIds = [1],
                        }
                    )
                );

            await ActUpdateAsync(applicationId: 2);
        }

        [Test]
        public void It_returns_no_content() =>
            _updateResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        [Test]
        public void It_acquires_both_locks_in_ascending_order() =>
            _recordingLockManager.AcquiredApplicationIds.Should().Equal(2L, 5L);

        [Test]
        public void It_releases_every_lock() =>
            _recordingLockManager.Handles.Should().OnlyContain(handle => handle.Disposed);
    }

    [TestFixture]
    public class Given_an_api_client_update_whose_second_lock_times_out : UpdateUnderLockTestBase
    {
        private RecordingLockHandle _firstLockHandle = null!;

        [SetUp]
        public async Task Act()
        {
            _firstLockHandle = new RecordingLockHandle();
            A.CallTo(() => _lockManager.AcquireAsync(1, A<CancellationToken>.Ignored))
                .Returns(new ApplicationLockResult.Acquired(_firstLockHandle));
            A.CallTo(() => _lockManager.AcquireAsync(2, A<CancellationToken>.Ignored))
                .Returns(new ApplicationLockResult.FailureTimeout());

            await ActUpdateAsync(applicationId: 2);
        }

        [TearDown]
        public async Task TearDownHandle() => await _firstLockHandle.DisposeAsync();

        [Test]
        public async Task It_returns_the_retriable_conflict_contract() =>
            await AssertLockConflictContract(_updateResponse);

        [Test]
        public void It_releases_the_first_lock() => _firstLockHandle.Disposed.Should().BeTrue();
    }

    [TestFixture]
    public class Given_an_api_client_update_whose_parent_keeps_moving : UpdateUnderLockTestBase
    {
        private RecordingLockManager _recordingLockManager = null!;

        [SetUp]
        public async Task Act()
        {
            _recordingLockManager = new RecordingLockManager();
            _lockManager = _recordingLockManager;

            int reads = 0;
            A.CallTo(() => _apiClientRepository.GetApiClientById(A<long>.Ignored))
                .ReturnsLazily(_ =>
                {
                    reads++;
                    long applicationId = reads % 2 == 1 ? 1 : 2;
                    return Task.FromResult<ApiClientGetResult>(
                        new ApiClientGetResult.Success(
                            new ApiClientResponse
                            {
                                Id = 1,
                                ApplicationId = applicationId,
                                ClientId = "test-client",
                                ClientUuid = _existingUuid,
                                Name = "Test",
                                IsApproved = true,
                                DataStoreIds = [1],
                            }
                        )
                    );
                });

            await ActUpdateAsync();
        }

        [Test]
        public async Task It_returns_the_retriable_conflict_contract() =>
            await AssertLockConflictContract(_updateResponse);

        [Test]
        public void It_retries_the_bounded_number_of_times() =>
            _recordingLockManager.AcquiredApplicationIds.Should().Equal(1L, 1L, 1L);

        [Test]
        public void It_releases_every_lock() =>
            _recordingLockManager.Handles.Should().OnlyContain(handle => handle.Disposed);
    }

    [TestFixture]
    public class Given_an_api_client_update_whose_original_application_is_missing : UpdateUnderLockTestBase
    {
        private List<string> _providerCalls = null!;

        [SetUp]
        public async Task Act()
        {
            _providerCalls = [];
            A.CallTo(_identityProviderRepository).Invokes(call => _providerCalls.Add(call.Method.Name));

            A.CallTo(() => _apiClientRepository.GetApiClientById(A<long>.Ignored))
                .Returns(
                    new ApiClientGetResult.Success(
                        new ApiClientResponse
                        {
                            Id = 1,
                            ApplicationId = 3,
                            ClientId = "test-client",
                            ClientUuid = _existingUuid,
                            Name = "Test",
                            IsApproved = true,
                            DataStoreIds = [1],
                        }
                    )
                );

            A.CallTo(() => _applicationRepository.GetApplication(3))
                .Returns(new ApplicationGetResult.FailureNotFound());

            await ActUpdateAsync();
        }

        [Test]
        public void It_returns_a_sanitized_internal_server_error() =>
            _updateResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        [Test]
        public void It_never_mutates_the_identity_provider() => _providerCalls.Should().BeEmpty();
    }

    [TestFixture]
    public class Given_an_api_client_update_whose_original_application_read_fails : UpdateUnderLockTestBase
    {
        private const string Sentinel = "SENTINEL_ORIGINAL_APP_must_not_leak";
        private List<string> _providerCalls = null!;

        [SetUp]
        public async Task Act()
        {
            _providerCalls = [];
            A.CallTo(_identityProviderRepository).Invokes(call => _providerCalls.Add(call.Method.Name));

            A.CallTo(() => _apiClientRepository.GetApiClientById(A<long>.Ignored))
                .Returns(
                    new ApiClientGetResult.Success(
                        new ApiClientResponse
                        {
                            Id = 1,
                            ApplicationId = 3,
                            ClientId = "test-client",
                            ClientUuid = _existingUuid,
                            Name = "Test",
                            IsApproved = true,
                            DataStoreIds = [1],
                        }
                    )
                );

            A.CallTo(() => _applicationRepository.GetApplication(3))
                .Returns(new ApplicationGetResult.FailureUnknown(Sentinel));

            await ActUpdateAsync();
        }

        [Test]
        public async Task It_returns_a_sanitized_internal_server_error()
        {
            _updateResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            string responseBody = await _updateResponse.Content.ReadAsStringAsync();
            responseBody.Should().NotContain(Sentinel);
        }

        [Test]
        public void It_never_mutates_the_identity_provider() => _providerCalls.Should().BeEmpty();
    }

    public abstract class ThrownApiClientRepositoryExceptionTestBase : UpdateUnderLockTestBase
    {
        protected const string Sentinel = "SENTINEL_THROWN_APICLIENT_REPO_must_not_leak";

        protected Guid _updatedUuid;
        protected Guid _rollbackUuid;
        protected List<string> _updatedClientUuids = null!;
        protected List<string> _deletedClientIds = null!;
        protected List<(long Id, Guid ExpectedUuid, Guid NewUuid)> _syncCalls = null!;

        [SetUp]
        public void SetUpThrownDefaults()
        {
            _updatedUuid = Guid.NewGuid();
            _rollbackUuid = Guid.NewGuid();
            _updatedClientUuids = [];
            _deletedClientIds = [];
            _syncCalls = [];

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
                .Invokes(call => _updatedClientUuids.Add(call.GetArgument<string>(0)!))
                .ReturnsNextFromSequence(
                    new ClientUpdateResult.Success(_updatedUuid),
                    new ClientUpdateResult.Success(_rollbackUuid)
                );

            A.CallTo(() => _identityProviderRepository.DeleteClientAsync(A<string>.Ignored))
                .Invokes(call => _deletedClientIds.Add(call.GetArgument<string>(0)!))
                .Returns(new ClientDeleteResult.Success());

            A.CallTo(() =>
                    _apiClientRepository.SyncApiClientUuid(A<long>.Ignored, A<Guid>.Ignored, A<Guid>.Ignored)
                )
                .Invokes(call =>
                    _syncCalls.Add(
                        (call.GetArgument<long>(0), call.GetArgument<Guid>(1), call.GetArgument<Guid>(2))
                    )
                )
                .Returns(new ApiClientUuidSyncResult.Success());

            A.CallTo(() => _apiClientRepository.UpdateApiClient(A<ApiClientUpdateCommand>.Ignored))
                .Throws(new InvalidOperationException(Sentinel));
        }

        protected ApiClientResolutionState CommandMatchingState() =>
            new(1, "Updated", true, "test-client", _updatedUuid, [1]);

        protected ApiClientResolutionState OriginalState() =>
            new(1, "Test", true, "test-client", _existingUuid, [1]);
    }

    [TestFixture]
    public class Given_a_thrown_api_client_repository_exception_whose_transaction_committed
        : ThrownApiClientRepositoryExceptionTestBase
    {
        [SetUp]
        public async Task Act()
        {
            A.CallTo(() => _apiClientRepository.GetApiClientResolutionState(A<long>.Ignored))
                .Returns(new ApiClientResolutionResult.Success(CommandMatchingState()));

            await ActUpdateAsync();
        }

        [Test]
        public async Task It_returns_the_recovered_success()
        {
            _updateResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
            string responseBody = await _updateResponse.Content.ReadAsStringAsync();
            responseBody.Should().NotContain(Sentinel);
        }

        [Test]
        public void It_performs_no_compensation_or_cleanup()
        {
            _updatedClientUuids.Should().HaveCount(1);
            _syncCalls.Should().BeEmpty();
            _deletedClientIds.Should().BeEmpty();
        }
    }

    [TestFixture]
    public class Given_a_thrown_api_client_repository_exception_whose_transaction_did_not_commit
        : ThrownApiClientRepositoryExceptionTestBase
    {
        [SetUp]
        public async Task Act()
        {
            A.CallTo(() => _apiClientRepository.GetApiClientResolutionState(A<long>.Ignored))
                .Returns(new ApiClientResolutionResult.Success(OriginalState()));

            await ActUpdateAsync();
        }

        [Test]
        public async Task It_returns_a_sanitized_internal_server_error() =>
            await AssertSanitizedInternalServerError(_updateResponse, Sentinel);

        [Test]
        public void It_compensates_the_identity_provider()
        {
            _updatedClientUuids.Should().HaveCount(2);
            _updatedClientUuids[1].Should().Be(_updatedUuid.ToString());
            _syncCalls.Should().Equal((1L, _existingUuid, _rollbackUuid));
        }
    }

    [TestFixture]
    public class Given_a_thrown_api_client_repository_exception_whose_outcome_resolution_fails
        : ThrownApiClientRepositoryExceptionTestBase
    {
        private const string ResolutionSentinel = "SENTINEL_APICLIENT_RESOLUTION_must_not_leak";

        [SetUp]
        public async Task Act()
        {
            A.CallTo(() => _apiClientRepository.GetApiClientResolutionState(A<long>.Ignored))
                .Returns(new ApiClientResolutionResult.FailureUnknown(ResolutionSentinel));

            await ActUpdateAsync();
        }

        [Test]
        public async Task It_returns_a_sanitized_internal_server_error() =>
            await AssertSanitizedInternalServerError(_updateResponse, Sentinel, ResolutionSentinel);

        [Test]
        public void It_performs_no_compensation_or_cleanup()
        {
            _updatedClientUuids.Should().HaveCount(1);
            _syncCalls.Should().BeEmpty();
            _deletedClientIds.Should().BeEmpty();
        }
    }

    [TestFixture]
    public class Given_a_thrown_api_client_repository_exception_resolving_to_a_partial_state
        : ThrownApiClientRepositoryExceptionTestBase
    {
        [SetUp]
        public async Task Act()
        {
            A.CallTo(() => _apiClientRepository.GetApiClientResolutionState(A<long>.Ignored))
                .Returns(
                    new ApiClientResolutionResult.Success(
                        CommandMatchingState() with
                        {
                            ClientUuid = Guid.NewGuid(),
                        }
                    )
                );

            await ActUpdateAsync();
        }

        [Test]
        public async Task It_returns_a_sanitized_internal_server_error() =>
            await AssertSanitizedInternalServerError(_updateResponse, Sentinel);

        [Test]
        public void It_performs_no_compensation_or_cleanup()
        {
            _updatedClientUuids.Should().HaveCount(1);
            _syncCalls.Should().BeEmpty();
            _deletedClientIds.Should().BeEmpty();
        }
    }

    [TestFixture]
    public class Given_a_vanished_api_client_whose_recreated_client_is_still_referenced
        : UpdateUnderLockTestBase
    {
        private List<string> _deletedClientIds = null!;

        [SetUp]
        public async Task Act()
        {
            _deletedClientIds = [];
            A.CallTo(() => _identityProviderRepository.DeleteClientAsync(A<string>.Ignored))
                .Invokes(call => _deletedClientIds.Add(call.GetArgument<string>(0)!))
                .Returns(new ClientDeleteResult.Success());

            A.CallTo(() => _apiClientRepository.UpdateApiClient(A<ApiClientUpdateCommand>.Ignored))
                .Returns(new ApiClientUpdateResult.FailureNotFound());

            A.CallTo(() => _apiClientRepository.HasApiClientUuidReference(A<Guid>.Ignored))
                .Returns(new ApiClientUuidReferenceResult.Referenced());

            await ActUpdateAsync();
        }

        [Test]
        public void It_returns_a_sanitized_internal_server_error() =>
            _updateResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        [Test]
        public void It_deletes_nothing() => _deletedClientIds.Should().BeEmpty();
    }

    [TestFixture]
    public class Given_an_api_client_delete_using_the_uuid_read_under_the_lock : ApiClientModuleTests
    {
        private Guid _staleUuid;
        private Guid _freshUuid;
        private List<string> _deletedClientUuids = null!;
        private HttpResponseMessage _deleteResponse = null!;

        [SetUp]
        public async Task Act()
        {
            _staleUuid = Guid.NewGuid();
            _freshUuid = Guid.NewGuid();
            _deletedClientUuids = [];

            int reads = 0;
            A.CallTo(() => _apiClientRepository.GetApiClientById(A<long>.Ignored))
                .ReturnsLazily(_ =>
                {
                    reads++;
                    return Task.FromResult<ApiClientGetResult>(
                        new ApiClientGetResult.Success(
                            new ApiClientResponse
                            {
                                Id = 1,
                                ApplicationId = 1,
                                ClientId = "test-client",
                                ClientUuid = reads == 1 ? _staleUuid : _freshUuid,
                                Name = "Test",
                                IsApproved = true,
                                DataStoreIds = [1],
                            }
                        )
                    );
                });

            A.CallTo(() => _identityProviderRepository.DeleteClientAsync(A<string>.Ignored))
                .Invokes(call => _deletedClientUuids.Add(call.GetArgument<string>(0)!))
                .Returns(new ClientDeleteResult.Success());

            A.CallTo(() => _apiClientRepository.DeleteApiClient(A<long>.Ignored))
                .Returns(new ApiClientDeleteResult.Success());

            using var client = SetUpClient();
            _deleteResponse = await client.DeleteAsync("/v3/apiClients/1");
        }

        [TearDown]
        public void TearDownResponse() => _deleteResponse?.Dispose();

        [Test]
        public void It_returns_no_content() =>
            _deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        [Test]
        public void It_targets_the_uuid_read_under_the_lock() =>
            _deletedClientUuids.Should().Equal(_freshUuid.ToString());
    }

    [TestFixture]
    public class Given_an_api_client_reset_credential_using_the_uuid_read_under_the_lock
        : ApiClientModuleTests
    {
        private Guid _staleUuid;
        private Guid _freshUuid;
        private List<string> _resetClientUuids = null!;
        private HttpResponseMessage _resetResponse = null!;

        [SetUp]
        public async Task Act()
        {
            _staleUuid = Guid.NewGuid();
            _freshUuid = Guid.NewGuid();
            _resetClientUuids = [];

            int reads = 0;
            A.CallTo(() => _apiClientRepository.GetApiClientById(A<long>.Ignored))
                .ReturnsLazily(_ =>
                {
                    reads++;
                    return Task.FromResult<ApiClientGetResult>(
                        new ApiClientGetResult.Success(
                            new ApiClientResponse
                            {
                                Id = 1,
                                ApplicationId = 1,
                                ClientId = "test-client",
                                ClientUuid = reads == 1 ? _staleUuid : _freshUuid,
                                Name = "Test",
                                IsApproved = true,
                                DataStoreIds = [1],
                            }
                        )
                    );
                });

            A.CallTo(() => _identityProviderRepository.ResetCredentialsAsync(A<string>.Ignored))
                .Invokes(call => _resetClientUuids.Add(call.GetArgument<string>(0)!))
                .Returns(new ClientResetResult.Success("NEW_SECRET"));

            using var client = SetUpClient();
            _resetResponse = await client.PutAsync("/v3/apiClients/1/reset-credential", null);
        }

        [TearDown]
        public void TearDownResponse() => _resetResponse?.Dispose();

        [Test]
        public void It_returns_the_new_credentials() =>
            _resetResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        [Test]
        public void It_targets_the_uuid_read_under_the_lock() =>
            _resetClientUuids.Should().Equal(_freshUuid.ToString());
    }

    [TestFixture]
    public class Given_an_api_client_delete_when_the_aggregate_lock_times_out : ApiClientModuleTests
    {
        private List<string> _dependencyCalls = null!;
        private HttpResponseMessage _deleteResponse = null!;

        [SetUp]
        public async Task Act()
        {
            _dependencyCalls = [];
            A.CallTo(() => _lockManager.AcquireAsync(A<long>.Ignored, A<CancellationToken>.Ignored))
                .Returns(new ApplicationLockResult.FailureTimeout());
            A.CallTo(_identityProviderRepository).Invokes(call => _dependencyCalls.Add(call.Method.Name));
            A.CallTo(_applicationRepository).Invokes(call => _dependencyCalls.Add(call.Method.Name));

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

            using var client = SetUpClient();
            _deleteResponse = await client.DeleteAsync("/v3/apiClients/1");
        }

        [TearDown]
        public void TearDownResponse() => _deleteResponse?.Dispose();

        [Test]
        public async Task It_returns_the_retriable_conflict_contract() =>
            await AssertLockConflictContract(_deleteResponse);

        [Test]
        public void It_calls_nothing_beyond_the_pre_read() => _dependencyCalls.Should().BeEmpty();
    }

    [TestFixture]
    public class Given_an_api_client_update_whose_second_lock_acquisition_is_cancelled
        : UpdateUnderLockTestBase
    {
        private RecordingLockHandle _firstLockHandle = null!;

        [SetUp]
        public async Task Act()
        {
            _firstLockHandle = new RecordingLockHandle();
            A.CallTo(() => _lockManager.AcquireAsync(1, A<CancellationToken>.Ignored))
                .Returns(new ApplicationLockResult.Acquired(_firstLockHandle));
            A.CallTo(() => _lockManager.AcquireAsync(2, A<CancellationToken>.Ignored))
                .Throws(new OperationCanceledException());

            await ActUpdateAsync(applicationId: 2);
        }

        [TearDown]
        public async Task TearDownHandle() => await _firstLockHandle.DisposeAsync();

        [Test]
        public void It_returns_a_server_error() =>
            _updateResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        [Test]
        public void It_releases_the_first_lock() => _firstLockHandle.Disposed.Should().BeTrue();
    }

    [TestFixture]
    public class Given_an_api_client_update_whose_under_lock_reread_throws : UpdateUnderLockTestBase
    {
        private const string Sentinel = "SENTINEL_REREAD_THROW_must_not_leak";
        private RecordingLockManager _recordingLockManager = null!;

        [SetUp]
        public async Task Act()
        {
            _recordingLockManager = new RecordingLockManager();
            _lockManager = _recordingLockManager;

            int reads = 0;
            A.CallTo(() => _apiClientRepository.GetApiClientById(A<long>.Ignored))
                .ReturnsLazily(_ =>
                {
                    reads++;
                    if (reads > 1)
                    {
                        throw new InvalidOperationException(Sentinel);
                    }

                    return Task.FromResult<ApiClientGetResult>(
                        new ApiClientGetResult.Success(
                            new ApiClientResponse
                            {
                                Id = 1,
                                ApplicationId = 1,
                                ClientId = "test-client",
                                ClientUuid = _existingUuid,
                                Name = "Test",
                                IsApproved = true,
                                DataStoreIds = [1],
                            }
                        )
                    );
                });

            await ActUpdateAsync();
        }

        [Test]
        public async Task It_returns_a_sanitized_internal_server_error() =>
            await AssertSanitizedInternalServerError(_updateResponse, Sentinel);

        [Test]
        public void It_releases_every_lock()
        {
            _recordingLockManager.Handles.Should().NotBeEmpty();
            _recordingLockManager.Handles.Should().OnlyContain(handle => handle.Disposed);
        }
    }

    public abstract class CompensationSyncTestBase : UpdateUnderLockTestBase
    {
        protected Guid _updatedUuid;
        protected Guid _rollbackUuid;
        protected List<string> _deletedClientIds = null!;

        [SetUp]
        public void SetUpCompensationDefaults()
        {
            _updatedUuid = Guid.NewGuid();
            _rollbackUuid = Guid.NewGuid();
            _deletedClientIds = [];

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
                    new ClientUpdateResult.Success(_updatedUuid),
                    new ClientUpdateResult.Success(_rollbackUuid)
                );

            A.CallTo(() => _identityProviderRepository.DeleteClientAsync(A<string>.Ignored))
                .Invokes(call => _deletedClientIds.Add(call.GetArgument<string>(0)!))
                .Returns(new ClientDeleteResult.Success());

            A.CallTo(() => _apiClientRepository.UpdateApiClient(A<ApiClientUpdateCommand>.Ignored))
                .Returns(new ApiClientUpdateResult.FailureApplicationNotFound());
        }

        protected void ArrangeSyncResult(ApiClientUuidSyncResult result) =>
            A.CallTo(() =>
                    _apiClientRepository.SyncApiClientUuid(A<long>.Ignored, A<Guid>.Ignored, A<Guid>.Ignored)
                )
                .Returns(result);
    }

    /// <summary>
    /// An identity-preserving provider update returns the stored UUID unchanged, so guarded
    /// synchronization is asked to replace a UUID with itself. It must recognize that as applied
    /// rather than as stale state, and compensation must still return the domain contract.
    /// </summary>
    [TestFixture]
    public class Given_a_failed_api_client_update_whose_provider_preserves_the_uuid : UpdateUnderLockTestBase
    {
        private List<(Guid Expected, Guid New)> _syncCalls = null!;
        private List<string> _deletedClientIds = null!;

        [SetUp]
        public async Task Act()
        {
            _syncCalls = [];
            _deletedClientIds = [];

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
                .Returns(new ClientUpdateResult.Success(_existingUuid));

            A.CallTo(() => _identityProviderRepository.DeleteClientAsync(A<string>.Ignored))
                .Invokes(call => _deletedClientIds.Add(call.GetArgument<string>(0)!))
                .Returns(new ClientDeleteResult.Success());

            A.CallTo(() => _apiClientRepository.UpdateApiClient(A<ApiClientUpdateCommand>.Ignored))
                .Returns(new ApiClientUpdateResult.FailureApplicationNotFound());

            A.CallTo(() =>
                    _apiClientRepository.SyncApiClientUuid(A<long>.Ignored, A<Guid>.Ignored, A<Guid>.Ignored)
                )
                .Invokes(call => _syncCalls.Add((call.GetArgument<Guid>(1), call.GetArgument<Guid>(2))))
                // The relational repositories answer AlreadyApplied when the stored UUID already
                // equals the new one, which is always the case for a stable-identity provider.
                .Returns(new ApiClientUuidSyncResult.AlreadyApplied());

            await ActUpdateAsync();
        }

        [Test]
        public void It_returns_the_unresolved_reference_conflict() =>
            _updateResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);

        [Test]
        public void It_synchronizes_the_unchanged_uuid_against_itself() =>
            _syncCalls.Should().Equal((_existingUuid, _existingUuid));

        [Test]
        public void It_deletes_no_provider_client() => _deletedClientIds.Should().BeEmpty();
    }

    [TestFixture]
    public class Given_a_failed_api_client_update_whose_rollback_was_already_synchronized
        : CompensationSyncTestBase
    {
        [SetUp]
        public async Task Act()
        {
            ArrangeSyncResult(new ApiClientUuidSyncResult.AlreadyApplied());
            await ActUpdateAsync();
        }

        [Test]
        public void It_returns_the_unresolved_reference_conflict() =>
            _updateResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);

        [Test]
        public void It_deletes_nothing() => _deletedClientIds.Should().BeEmpty();
    }

    [TestFixture]
    public class Given_a_failed_api_client_update_that_vanishes_during_rollback : CompensationSyncTestBase
    {
        [SetUp]
        public async Task Act()
        {
            ArrangeSyncResult(new ApiClientUuidSyncResult.FailureNotExistsSafeToDelete());
            await ActUpdateAsync();
        }

        [Test]
        public void It_returns_a_sanitized_internal_server_error() =>
            _updateResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        [Test]
        public void It_deletes_the_client_recreated_by_the_rollback() =>
            _deletedClientIds.Should().Equal(_rollbackUuid.ToString());
    }

    [TestFixture]
    public class Given_a_failed_api_client_update_with_a_stale_stored_client : CompensationSyncTestBase
    {
        [SetUp]
        public async Task Act()
        {
            ArrangeSyncResult(new ApiClientUuidSyncResult.FailureStaleState());
            await ActUpdateAsync();
        }

        [Test]
        public void It_returns_a_sanitized_internal_server_error() =>
            _updateResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        [Test]
        public void It_deletes_nothing() => _deletedClientIds.Should().BeEmpty();
    }

    [TestFixture]
    public class Given_a_failed_api_client_update_whose_rollback_client_is_still_referenced
        : CompensationSyncTestBase
    {
        [SetUp]
        public async Task Act()
        {
            ArrangeSyncResult(new ApiClientUuidSyncResult.FailureNotExists());
            await ActUpdateAsync();
        }

        [Test]
        public void It_returns_a_sanitized_internal_server_error() =>
            _updateResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        [Test]
        public void It_deletes_nothing() => _deletedClientIds.Should().BeEmpty();
    }

    [TestFixture]
    public class Given_a_failed_api_client_update_whose_rollback_sync_fails : CompensationSyncTestBase
    {
        private const string Sentinel = "SENTINEL_APICLIENT_SYNC_must_not_leak";

        [SetUp]
        public async Task Act()
        {
            ArrangeSyncResult(new ApiClientUuidSyncResult.FailureUnknown(Sentinel));
            await ActUpdateAsync();
        }

        [Test]
        public async Task It_returns_a_sanitized_internal_server_error() =>
            await AssertSanitizedInternalServerError(_updateResponse, Sentinel);

        [Test]
        public void It_deletes_nothing() => _deletedClientIds.Should().BeEmpty();
    }

    [TestFixture]
    public class Given_a_vanished_api_client_whose_reference_check_fails : CompensationSyncTestBase
    {
        private const string Sentinel = "SENTINEL_APICLIENT_REFERENCE_must_not_leak";

        [SetUp]
        public async Task Act()
        {
            A.CallTo(() => _apiClientRepository.UpdateApiClient(A<ApiClientUpdateCommand>.Ignored))
                .Returns(new ApiClientUpdateResult.FailureNotFound());

            A.CallTo(() => _apiClientRepository.HasApiClientUuidReference(A<Guid>.Ignored))
                .Returns(new ApiClientUuidReferenceResult.FailureUnknown(Sentinel));

            await ActUpdateAsync();
        }

        [Test]
        public async Task It_returns_a_sanitized_internal_server_error() =>
            await AssertSanitizedInternalServerError(_updateResponse, Sentinel);

        [Test]
        public void It_deletes_nothing() => _deletedClientIds.Should().BeEmpty();
    }

    [TestFixture]
    public class Given_a_failed_api_client_update_whose_provider_rollback_throws : CompensationSyncTestBase
    {
        private const string Sentinel = "SENTINEL_APICLIENT_ROLLBACK_THROW_must_not_leak";
        private RecordingLockManager _recordingLockManager = null!;

        [SetUp]
        public async Task Act()
        {
            _recordingLockManager = new RecordingLockManager();
            _lockManager = _recordingLockManager;

            int providerCalls = 0;
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
                .ReturnsLazily(_ =>
                {
                    providerCalls++;
                    if (providerCalls == 1)
                    {
                        return Task.FromResult<ClientUpdateResult>(
                            new ClientUpdateResult.Success(_updatedUuid)
                        );
                    }

                    throw new InvalidOperationException(Sentinel);
                });

            await ActUpdateAsync();
        }

        [Test]
        public async Task It_returns_a_sanitized_internal_server_error() =>
            await AssertSanitizedInternalServerError(_updateResponse, Sentinel);

        [Test]
        public void It_releases_every_lock() =>
            _recordingLockManager.Handles.Should().OnlyContain(handle => handle.Disposed);
    }

    [TestFixture]
    public class Given_a_vanished_api_client_whose_client_cleanup_throws : CompensationSyncTestBase
    {
        private const string Sentinel = "SENTINEL_APICLIENT_CLEANUP_THROW_must_not_leak";
        private RecordingLockManager _recordingLockManager = null!;

        [SetUp]
        public async Task Act()
        {
            _recordingLockManager = new RecordingLockManager();
            _lockManager = _recordingLockManager;

            A.CallTo(() => _apiClientRepository.UpdateApiClient(A<ApiClientUpdateCommand>.Ignored))
                .Returns(new ApiClientUpdateResult.FailureNotFound());

            A.CallTo(() => _identityProviderRepository.DeleteClientAsync(A<string>.Ignored))
                .Throws(new InvalidOperationException(Sentinel));

            await ActUpdateAsync();
        }

        [Test]
        public async Task It_returns_a_sanitized_internal_server_error() =>
            await AssertSanitizedInternalServerError(_updateResponse, Sentinel);

        [Test]
        public void It_releases_every_lock() =>
            _recordingLockManager.Handles.Should().OnlyContain(handle => handle.Disposed);
    }

    [TestFixture]
    public class Given_a_failed_api_client_update_whose_resolution_read_throws : CompensationSyncTestBase
    {
        private const string Sentinel = "SENTINEL_APICLIENT_RESOLUTION_THROW_must_not_leak";
        private RecordingLockManager _recordingLockManager = null!;
        private List<string> _providerUpdateUuids = null!;

        [SetUp]
        public async Task Act()
        {
            _recordingLockManager = new RecordingLockManager();
            _lockManager = _recordingLockManager;

            _providerUpdateUuids = [];
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
                .Invokes(call => _providerUpdateUuids.Add(call.GetArgument<string>(0)!))
                .Returns(new ClientUpdateResult.Success(_updatedUuid));

            A.CallTo(() => _apiClientRepository.UpdateApiClient(A<ApiClientUpdateCommand>.Ignored))
                .Returns(new ApiClientUpdateResult.FailureUnknown("Database error"));

            A.CallTo(() => _apiClientRepository.GetApiClientResolutionState(A<long>.Ignored))
                .Throws(new InvalidOperationException(Sentinel));

            await ActUpdateAsync();
        }

        [Test]
        public async Task It_returns_a_sanitized_internal_server_error() =>
            await AssertSanitizedInternalServerError(_updateResponse, Sentinel);

        [Test]
        public void It_performs_no_speculative_action()
        {
            _providerUpdateUuids.Should().HaveCount(1);
            _deletedClientIds.Should().BeEmpty();
        }

        [Test]
        public void It_releases_every_lock() =>
            _recordingLockManager.Handles.Should().OnlyContain(handle => handle.Disposed);
    }
}
