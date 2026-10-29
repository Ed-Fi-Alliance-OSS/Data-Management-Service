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
using EdFi.DmsConfigurationService.DataModel.Model.Application;
using EdFi.DmsConfigurationService.DataModel.Model.Authorization;
using EdFi.DmsConfigurationService.DataModel.Model.Profile;
using EdFi.DmsConfigurationService.DataModel.Model.Vendor;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Configuration;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Modules;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Middleware;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Modules;

[TestFixture]
public partial class ApplicationModuleTests
{
    private readonly IApplicationRepository _applicationRepository = A.Fake<IApplicationRepository>();
    private readonly IApiClientRepository _apiClientRepository = A.Fake<IApiClientRepository>();
    private IApplicationLockManager _lockManager = A.Fake<IApplicationLockManager>();
    private readonly IIdentityProviderRepository _clientRepository = A.Fake<IIdentityProviderRepository>();
    private readonly IDataStoreRepository _dataStoreRepository = A.Fake<IDataStoreRepository>();
    private readonly IVendorRepository _vendorRepository = A.Fake<IVendorRepository>();
    private readonly IProfileRepository _profileRepository = A.Fake<IProfileRepository>();
    private readonly WebApplicationFactoryTracker<Program> _factoryTracker = new();

    /// <summary>A saved Application with no client awaiting an identity provider update.</summary>
    private static readonly ApplicationSyncState EmptySyncState = new(
        "Test Application",
        1,
        "Test",
        [],
        [],
        []
    );

    public ApplicationModuleTests()
    {
        A.CallTo(() => _lockManager.AcquireAsync(A<int>.Ignored, A<CancellationToken>.Ignored))
            .ReturnsLazily(_ =>
                Task.FromResult<ApplicationLockResult>(
                    new ApplicationLockResult.Acquired(A.Fake<IAsyncDisposable>())
                )
            );

        A.CallTo(() => _applicationRepository.SaveApplication(A<ApplicationUpdateCommand>.Ignored))
            .Returns(new ApplicationSaveResult.Success(EmptySyncState));
        A.CallTo(() => _applicationRepository.GetApplicationSyncState(A<int>.Ignored))
            .Returns(new ApplicationSyncStateResult.Success(EmptySyncState));
        A.CallTo(() =>
                _apiClientRepository.ClearApiClientIdentityProviderSyncPending(
                    A<int>.Ignored,
                    A<string>.Ignored
                )
            )
            .Returns(new ApiClientSyncPendingResult.Success());

        A.CallTo(() => _apiClientRepository.HasApiClientUuidReference(A<Guid>.Ignored))
            .Returns(new ApiClientUuidReferenceResult.None());

        A.CallTo(() => _dataStoreRepository.GetExistingDataStoreIds(A<int[]>.Ignored))
            .ReturnsLazily(call =>
            {
                int[] ids = call.GetArgument<int[]>(0) ?? [];
                return Task.FromResult<DataStoreIdsExistResult>(
                    new DataStoreIdsExistResult.Success([.. ids])
                );
            });

        A.CallTo(() => _profileRepository.GetProfile(A<int>.Ignored))
            .Returns(
                new ProfileGetResult.Success(
                    new EdFi.DmsConfigurationService.DataModel.Model.Profile.ProfileResponse
                    {
                        Id = 1,
                        Name = "Test Profile",
                        Definition = "<Profile/>",
                    }
                )
            );
    }

    [TearDown]
    public void DisposeWebApplicationFactories() => _factoryTracker.DisposeTrackedFactories();

    private HttpClient SetUpClient(
        int? clientSecretMinimumLength = null,
        Action<IServiceCollection>? configureServices = null
    )
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
                    .AddTransient((_) => _applicationRepository)
                    .AddTransient((_) => _apiClientRepository)
                    .AddTransient((_) => _lockManager)
                    .AddTransient((_) => _clientRepository)
                    .AddTransient((_) => _dataStoreRepository)
                    .AddTransient((_) => _vendorRepository)
                    .AddTransient((_) => _profileRepository);

                // Registered last so a fixture can replace a closed-generic service the host
                // already provides, such as the module's logger.
                configureServices?.Invoke(collection);
            });
        });
        _factoryTracker.Track(factory);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Scope", AuthorizationScopes.AdminScope.Name);
        return client;
    }

    [TestFixture]
    public class SuccessTests : ApplicationModuleTests
    {
        [SetUp]
        public void Setup()
        {
            A.CallTo(() => _vendorRepository.GetVendor(A<int>.Ignored))
                .Returns(
                    new VendorGetResult.Success(
                        new VendorResponse
                        {
                            Company = "any",
                            ContactName = "any",
                            ContactEmailAddress = "any",
                            NamespacePrefixes = "any",
                        }
                    )
                );

            A.CallTo(() =>
                    _applicationRepository.InsertApplication(
                        A<ApplicationInsertCommand>.Ignored,
                        A<ApiClientCommand>.Ignored
                    )
                )
                .Returns(new ApplicationInsertResult.Success(1));

            A.CallTo(() => _applicationRepository.QueryApplication(A<ApplicationQuery>.Ignored))
                .Returns(
                    new ApplicationQueryResult.Success([
                        new ApplicationResponse()
                        {
                            Id = 1,
                            ApplicationName = "Test Application",
                            ClaimSetName = "ClaimSet",
                            VendorId = 1,
                            EducationOrganizationIds = [1],
                            DataStoreIds = [1],
                            ProfileIds = [1],
                        },
                    ])
                );

            A.CallTo(() => _applicationRepository.GetApplication(A<int>.Ignored))
                .Returns(
                    new ApplicationGetResult.Success(
                        new ApplicationResponse()
                        {
                            Id = 1,
                            ApplicationName = "Test Application",
                            ClaimSetName = "ClaimSet",
                            VendorId = 1,
                            EducationOrganizationIds = [1],
                            DataStoreIds = [1],
                            ProfileIds = [1],
                        }
                    )
                );

            A.CallTo(() => _applicationRepository.SaveApplication(A<ApplicationUpdateCommand>.Ignored))
                .Returns(new ApplicationSaveResult.Success(EmptySyncState));

            A.CallTo(() => _applicationRepository.DeleteApplication(A<int>.Ignored))
                .Returns(new ApplicationDeleteResult.Success());

            A.CallTo(() => _applicationRepository.GetApplicationApiClients(A<int>.Ignored))
                .Returns(new ApplicationApiClientsResult.Success([new("1", Guid.NewGuid(), true)]));

            A.CallTo(() =>
                    _clientRepository.CreateClientAsync(
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<int[]?>.Ignored,
                        A<bool>.Ignored
                    )
                )
                .Returns(new ClientCreateResult.Success(Guid.NewGuid()));

            A.CallTo(() => _clientRepository.ResetCredentialsAsync(A<string>.Ignored))
                .Returns(new ClientResetResult.Success("SECRET"));

            A.CallTo(() => _clientRepository.DeleteClientAsync(A<string>.Ignored))
                .Returns(new ClientDeleteResult.Success());

            A.CallTo(() =>
                    _clientRepository.UpdateClientAsync(
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<int[]?>.Ignored,
                        A<bool>.Ignored,
                        A<string>.Ignored
                    )
                )
                .Returns(new ClientUpdateResult.Success(Guid.NewGuid()));
        }

        [Test]
        public async Task Should_return_success_response()
        {
            // Arrange
            using var client = SetUpClient();

            var addResponse = await client.PostAsync(
                "/v3/applications",
                new StringContent(
                    """
                    {
                      "ApplicationName": "Application 11",
                      "ClaimSetName": "Test",
                      "VendorId": 1,
                      "EducationOrganizationIds": [1],
                      "DataStoreIds": [1]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );

            var getResponse = await client.GetAsync("/v3/applications?offset=0&limit=25");
            var getByIdResponse = await client.GetAsync("/v3/applications/1");
            var updateResponse = await client.PutAsync(
                "/v3/applications/1",
                new StringContent(
                    """
                    {
                       "id": 1,
                       "ApplicationName": "Application 11",
                        "ClaimSetName": "Test",
                        "VendorId": 1,
                        "EducationOrganizationIds": [1],
                        "DataStoreIds": [1]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );
            var deleteResponse = await client.DeleteAsync("/v3/applications/1");
            var resetCredentialsResponse = await client.PutAsync("/v3/applications/1/reset-credential", null);

            //Assert
            addResponse.StatusCode.Should().Be(HttpStatusCode.Created);
            getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            getByIdResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            updateResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
            deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
            resetCredentialsResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Test]
        public async Task Should_generate_application_secret_using_the_configured_minimum_length()
        {
            // Arrange
            var configuredMinimumLength = 40;
            string generatedSecret = string.Empty;

            A.CallTo(() =>
                    _clientRepository.CreateClientAsync(
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<int[]?>.Ignored,
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
            var addResponse = await client.PostAsync(
                "/v3/applications",
                new StringContent(
                    """
                    {
                      "ApplicationName": "Application 11",
                      "ClaimSetName": "Test",
                      "VendorId": 1,
                      "EducationOrganizationIds": [1],
                      "DataStoreIds": [1]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );

            // Assert
            addResponse.StatusCode.Should().Be(HttpStatusCode.Created);
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

            var responseBody = await addResponse.Content.ReadAsStringAsync();
            var actualResponse = JsonNode.Parse(responseBody);
            actualResponse!["secret"]!.GetValue<string>().Should().HaveLength(configuredMinimumLength);
            actualResponse!["secret"]!.GetValue<string>().Should().Be(generatedSecret);
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
                    "EducationOrganizationIds": [0],
                    "DataStoreIds": []
                }
                """;

            string invalidClaimSetName = """
                {
                   "ApplicationName": "Application101",
                    "ClaimSetName": "ClaimSet name with white space",
                    "VendorId":1,
                    "EducationOrganizationIds": [255901],
                    "DataStoreIds": [1]
                }
                """;

            //Act
            var addResponse = await client.PostAsync(
                "/v3/applications",
                new StringContent(invalidBody, Encoding.UTF8, "application/json")
            );

            var addResponseForInvalidClaimSetName = await client.PostAsync(
                "/v3/applications",
                new StringContent(invalidClaimSetName, Encoding.UTF8, "application/json")
            );

            //Assert
            string addResponseContent = await addResponse.Content.ReadAsStringAsync();
            var actualResponse = JsonNode.Parse(addResponseContent);
            var expectedResponse = JsonNode.Parse(
                """
                {
                  "detail": "Data validation failed. See 'validationErrors' for details.",
                  "type": "urn:ed-fi:management-api:bad-request:data",
                  "title": "Data Validation Failed",
                  "status": 400,
                  "correlationId": "{correlationId}",
                  "validationErrors": {
                    "ApplicationName": [
                      "The length of 'Application Name' must be 50 characters or fewer. You entered 266 characters."
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

            string addResponseContentForInvalidClaimSetName =
                await addResponseForInvalidClaimSetName.Content.ReadAsStringAsync();
            var actualResponseForInvalidClaimSetName = JsonNode.Parse(
                addResponseContentForInvalidClaimSetName
            );
            var expectedResponseForInvalidClaimSetName = JsonNode.Parse(
                """
                {
                  "detail": "Data validation failed. See 'validationErrors' for details.",
                  "type": "urn:ed-fi:management-api:bad-request:data",
                  "title": "Data Validation Failed",
                  "status": 400,
                  "correlationId": "{correlationId}",
                  "validationErrors": {
                    "ClaimSetName": [
                      "Claim set name must not contain white spaces."
                    ]
                  },
                  "errors": []
                }
                """.Replace(
                    "{correlationId}",
                    actualResponseForInvalidClaimSetName!["correlationId"]!.GetValue<string>()
                )
            );
            addResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            JsonNode
                .DeepEquals(actualResponseForInvalidClaimSetName, expectedResponseForInvalidClaimSetName)
                .Should()
                .Be(true);
        }

        [Test]
        public async Task Should_return_bad_request_for_invalid_profile_id_value()
        {
            // Arrange
            using var client = SetUpClient();

            string invalidProfileId = """
                {
                   "ApplicationName": "Application101",
                    "ClaimSetName": "TestClaimSet",
                    "VendorId":1,
                    "EducationOrganizationIds": [255901],
                    "DataStoreIds": [1],
                    "ProfileIds": [0]
                }
                """;

            //Act
            var addResponse = await client.PostAsync(
                "/v3/applications",
                new StringContent(invalidProfileId, Encoding.UTF8, "application/json")
            );

            //Assert
            string addResponseContent = await addResponse.Content.ReadAsStringAsync();
            var actualResponse = JsonNode.Parse(addResponseContent);
            var expectedResponse = JsonNode.Parse(
                """
                {
                  "detail": "Data validation failed. See 'validationErrors' for details.",
                  "type": "urn:ed-fi:management-api:bad-request:data",
                  "title": "Data Validation Failed",
                  "status": 400,
                  "correlationId": "{correlationId}",
                  "validationErrors": {
                    "ProfileIds[0]": [
                      "'Profile Ids' must be greater than '0'."
                    ]
                  },
                  "errors": []
                }
                """.Replace("{correlationId}", actualResponse!["correlationId"]!.GetValue<string>())
            );
            addResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            JsonNode.DeepEquals(actualResponse, expectedResponse).Should().Be(true);
        }
    }

    /// <summary>
    /// DMS reads an application's profile assignments by id with the service-account scope that covers
    /// its other Configuration Service reads. That scope must reach the by-id read and nothing else here.
    /// </summary>
    [TestFixture]
    public class Given_a_token_with_only_the_auth_metadata_scope : ApplicationModuleTests
    {
        private HttpResponseMessage _getByIdResponse = null!;
        private HttpResponseMessage _getAllResponse = null!;
        private HttpResponseMessage _deleteResponse = null!;

        [SetUp]
        public async Task Setup()
        {
            A.CallTo(() => _applicationRepository.GetApplication(A<int>.Ignored))
                .Returns(
                    new ApplicationGetResult.Success(
                        new ApplicationResponse()
                        {
                            Id = 1,
                            ApplicationName = "Test Application",
                            ClaimSetName = "ClaimSet",
                            VendorId = 1,
                            EducationOrganizationIds = [1],
                            DataStoreIds = [1],
                            ProfileIds = [1],
                        }
                    )
                );

            using var client = SetUpClient();
            client.DefaultRequestHeaders.Remove("X-Test-Scope");
            client.DefaultRequestHeaders.Add(
                "X-Test-Scope",
                AuthorizationScopes.AuthMetadataReadOnlyAccessScope.Name
            );

            _getByIdResponse = await client.GetAsync("/v3/applications/1");
            _getAllResponse = await client.GetAsync("/v3/applications");
            _deleteResponse = await client.DeleteAsync("/v3/applications/1");
        }

        [Test]
        public void It_allows_reading_an_application_by_id() =>
            _getByIdResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        [Test]
        public void It_still_forbids_listing_applications() =>
            _getAllResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        [Test]
        public void It_still_forbids_deleting_an_application() =>
            _deleteResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [TestFixture]
    public class FailureNotFoundTest : ApplicationModuleTests
    {
        [SetUp]
        public void SetUp()
        {
            A.CallTo(() => _vendorRepository.InsertVendor(A<VendorInsertCommand>.Ignored))
                .Returns(new VendorInsertResult.Success(1));

            A.CallTo(() => _applicationRepository.GetApplication(A<int>.Ignored))
                .Returns(new ApplicationGetResult.FailureNotFound());

            A.CallTo(() => _applicationRepository.SaveApplication(A<ApplicationUpdateCommand>.Ignored))
                .Returns(new ApplicationSaveResult.FailureNotExists());

            A.CallTo(() => _applicationRepository.DeleteApplication(A<int>.Ignored))
                .Returns(new ApplicationDeleteResult.FailureNotExists());

            A.CallTo(() => _applicationRepository.GetApplicationApiClients(A<int>.Ignored))
                .Returns(new ApplicationApiClientsResult.Success([]));

            A.CallTo(() => _applicationRepository.GetApplicationApiClients(A<int>.Ignored))
                .Returns(new ApplicationApiClientsResult.Success([]));

            A.CallTo(() => _applicationRepository.GetApplicationApiClients(A<int>.Ignored))
                .Returns(new ApplicationApiClientsResult.Success([]));
        }

        [Test]
        public async Task Should_return_proper_not_found_responses()
        {
            // Arrange
            using var client = SetUpClient();

            //Act
            var getByIdResponse = await client.GetAsync("/v3/applications/1");
            var updateResponse = await client.PutAsync(
                "/v3/applications/1",
                new StringContent(
                    """
                    {
                        "id": 1,
                       "applicationName": "Application 101",
                        "claimSetName": "Test",
                        "vendorId":1,
                        "educationOrganizationIds": [1],
                        "dataStoreIds": [1]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );
            var deleteResponse = await client.DeleteAsync("/v3/applications/1");
            var resetCredentialsResponse = await client.PutAsync("/v3/applications/1/reset-credential", null);

            //Assert
            getByIdResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
            updateResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
            deleteResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
            resetCredentialsResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        [Test]
        public async Task Should_return_not_found_before_validating_references_on_update()
        {
            // Arrange - the requested data store ids do not exist for this tenant either
            A.CallTo(() => _dataStoreRepository.GetExistingDataStoreIds(A<int[]>.Ignored))
                .Returns(new DataStoreIdsExistResult.Success([]));

            using var client = SetUpClient();

            // Act
            var updateResponse = await client.PutAsync(
                "/v3/applications/1",
                new StringContent(
                    """
                    {
                        "id": 1,
                        "applicationName": "Application 101",
                        "claimSetName": "Test",
                        "vendorId": 1,
                        "educationOrganizationIds": [1],
                        "dataStoreIds": [999]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );

            // Assert - a missing or foreign-tenant application responds 404, not 400
            updateResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
            string responseBody = await updateResponse.Content.ReadAsStringAsync();
            responseBody.Should().Contain("Application not found");

            // Assert - the identity provider client was never touched for an application
            // that does not resolve for this tenant
            A.CallTo(() =>
                    _clientRepository.UpdateClientAsync(
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<int[]?>.Ignored,
                        A<bool>.Ignored,
                        A<string>.Ignored
                    )
                )
                .MustNotHaveHappened();
        }

        [Test]
        public async Task Should_not_delete_identity_provider_client_when_application_does_not_resolve()
        {
            // Arrange
            using var client = SetUpClient();

            // Act
            var deleteResponse = await client.DeleteAsync("/v3/applications/1");

            // Assert - a missing or foreign-tenant application responds 404
            deleteResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);

            // Assert - the identity provider client was never deleted for an application
            // that does not resolve for this tenant
            A.CallTo(() => _clientRepository.DeleteClientAsync(A<string>.Ignored)).MustNotHaveHappened();
        }
    }

    [TestFixture]
    public class FailureUnknownTests : ApplicationModuleTests
    {
        [SetUp]
        public void SetUp()
        {
            A.CallTo(() =>
                    _clientRepository.CreateClientAsync(
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<int[]?>.Ignored,
                        A<bool>.Ignored
                    )
                )
                .Returns(new ClientCreateResult.Success(Guid.NewGuid()));

            A.CallTo(() => _vendorRepository.GetVendor(A<int>.Ignored))
                .Returns(
                    new VendorGetResult.Success(
                        new VendorResponse
                        {
                            Company = "any",
                            ContactName = "any",
                            ContactEmailAddress = "any",
                            NamespacePrefixes = "any",
                        }
                    )
                );

            A.CallTo(() =>
                    _applicationRepository.InsertApplication(
                        A<ApplicationInsertCommand>.Ignored,
                        A<ApiClientCommand>.Ignored
                    )
                )
                .Returns(new ApplicationInsertResult.FailureUnknown(""));

            A.CallTo(() => _clientRepository.ResetCredentialsAsync(A<string>.Ignored))
                .Returns(new ClientResetResult.FailureUnknown(""));

            A.CallTo(() => _applicationRepository.QueryApplication(A<ApplicationQuery>.Ignored))
                .Returns(new ApplicationQueryResult.FailureUnknown(""));

            A.CallTo(() => _applicationRepository.GetApplication(A<int>.Ignored))
                .Returns(new ApplicationGetResult.FailureUnknown(""));

            A.CallTo(() => _applicationRepository.SaveApplication(A<ApplicationUpdateCommand>.Ignored))
                .Returns(new ApplicationSaveResult.FailureUnknown(""));

            A.CallTo(() => _applicationRepository.DeleteApplication(A<int>.Ignored))
                .Returns(new ApplicationDeleteResult.FailureUnknown(""));

            A.CallTo(() => _applicationRepository.GetApplicationApiClients(A<int>.Ignored))
                .Returns(new ApplicationApiClientsResult.FailureUnknown(""));
        }

        [Test]
        public async Task Should_return_internal_server_error_response()
        {
            // Arrange
            using var client = SetUpClient();

            //Act
            var addResponse = await client.PostAsync(
                "/v3/applications",
                new StringContent(
                    """
                    {
                        "ApplicationName": "Application 102",
                        "ClaimSetName": "Test",
                        "VendorId": 1,
                        "EducationOrganizationIds": [1],
                        "DataStoreIds": [1]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );
            var getResponse = await client.GetAsync("/v3/applications?offset=0&limit=25");
            var getByIdResponse = await client.GetAsync("/v3/applications/1");
            var updateResponse = await client.PutAsync(
                "/v3/applications/1",
                new StringContent(
                    """
                    {
                        "id": 1,
                        "ApplicationName": "Application 102",
                        "ClaimSetName": "Test",
                        "VendorId": 1,
                        "EducationOrganizationIds": [1],
                        "DataStoreIds": [1]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );
            var deleteResponse = await client.DeleteAsync("/v3/applications/1");
            var resetCredentialsResponse = await client.PutAsync("/v3/applications/1/reset-credential", null);

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
            A.CallTo(() =>
                    _applicationRepository.InsertApplication(
                        A<ApplicationInsertCommand>.Ignored,
                        A<ApiClientCommand>.Ignored
                    )
                )
                .Returns(new ApplicationInsertResult());

            A.CallTo(() => _vendorRepository.GetVendor(A<int>.Ignored))
                .Returns(
                    new VendorGetResult.Success(
                        new VendorResponse
                        {
                            Company = "any",
                            ContactName = "any",
                            ContactEmailAddress = "any",
                            NamespacePrefixes = "any",
                        }
                    )
                );

            A.CallTo(() => _applicationRepository.QueryApplication(A<ApplicationQuery>.Ignored))
                .Returns(new ApplicationQueryResult());

            A.CallTo(() => _applicationRepository.GetApplication(A<int>.Ignored))
                .Returns(new ApplicationGetResult());

            A.CallTo(() => _applicationRepository.SaveApplication(A<ApplicationUpdateCommand>.Ignored))
                .Returns(new ApplicationSaveResult());

            A.CallTo(() => _applicationRepository.DeleteApplication(A<int>.Ignored))
                .Returns(new ApplicationDeleteResult());

            A.CallTo(() => _applicationRepository.GetApplicationApiClients(A<int>.Ignored))
                .Returns(new ApplicationApiClientsResult());
        }

        [Test]
        public async Task Should_return_internal_server_error_response()
        {
            // Arrange
            using var client = SetUpClient();

            //Act
            var addResponse = await client.PostAsync(
                "/v3/applications",
                new StringContent(
                    """
                    {
                      "ApplicationName": "Application 11",
                      "ClaimSetName": "Test",
                      "VendorId": 1,
                      "EducationOrganizationIds": [1],
                      "DataStoreIds": [1]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );

            var getResponse = await client.GetAsync("/v3/applications?offset=0&limit=25");
            var getByIdResponse = await client.GetAsync("/v3/applications/1");
            var updateResponse = await client.PostAsync(
                "/v3/applications",
                new StringContent(
                    """
                    {
                      "ApplicationName": "Application 11",
                      "ClaimSetName": "Test",
                      "VendorId": 1,
                      "EducationOrganizationIds": [1],
                      "DataStoreIds": [1]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );
            var deleteResponse = await client.DeleteAsync("/v3/applications/1");

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
            A.CallTo(() =>
                    _clientRepository.CreateClientAsync(
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<int[]?>.Ignored,
                        A<bool>.Ignored
                    )
                )
                .Returns(new ClientCreateResult.Success(Guid.NewGuid()));

            // Keep the pre-check successful so repository-result tests reach their target branch.
            A.CallTo(() => _vendorRepository.GetVendor(A<int>.Ignored))
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

            A.CallTo(() =>
                    _applicationRepository.InsertApplication(
                        A<ApplicationInsertCommand>.Ignored,
                        A<ApiClientCommand>.Ignored
                    )
                )
                .Returns(new ApplicationInsertResult.FailureVendorNotFound());

            A.CallTo(() => _applicationRepository.SaveApplication(A<ApplicationUpdateCommand>.Ignored))
                .Returns(new ApplicationSaveResult.FailureVendorNotFound());

            A.CallTo(() => _applicationRepository.GetApplicationApiClients(A<int>.Ignored))
                .Returns(
                    new ApplicationApiClientsResult.Success([new ApiClient("111", Guid.NewGuid(), true)])
                );

            A.CallTo(() =>
                    _clientRepository.UpdateClientAsync(
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<int[]?>.Ignored,
                        A<bool>.Ignored,
                        A<string>.Ignored
                    )
                )
                .Returns(new ClientUpdateResult.Success(Guid.NewGuid()));
        }

        [Test]
        public async Task Should_return_conflict_and_clean_up_when_vendor_not_found_at_repository_on_insert()
        {
            // Arrange
            List<string> deletedClientIds = [];
            A.CallTo(() => _clientRepository.DeleteClientAsync(A<string>.Ignored))
                .Invokes(call => deletedClientIds.Add(call.GetArgument<string>(0)!))
                .Returns(new ClientDeleteResult.Success());

            using var client = SetUpClient();

            //Act
            var addResponse = await client.PostAsync(
                "/v3/applications",
                new StringContent(
                    """
                    {
                        "ApplicationName": "Application 102",
                        "ClaimSetName": "Test",
                        "VendorId": 1,
                        "EducationOrganizationIds": [1],
                        "DataStoreIds": [1]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );

            addResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
            addResponse.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
            string responseBody = await addResponse.Content.ReadAsStringAsync();
            var actualResponse = JsonNode.Parse(responseBody);
            var expectedResponse = JsonNode.Parse(
                """
                {
                  "detail": "Reference 'VendorId' does not exist.",
                  "type": "urn:ed-fi:management-api:conflict:unresolved-reference",
                  "title": "Unresolved Reference",
                  "status": 409,
                  "correlationId": "{correlationId}",
                  "validationErrors": {},
                  "errors": []
                }
                """.Replace("{correlationId}", actualResponse!["correlationId"]!.GetValue<string>())
            );
            JsonNode.DeepEquals(actualResponse, expectedResponse).Should().Be(true);
            deletedClientIds.Should().HaveCount(1);
        }

        [Test]
        public async Task Should_return_conflict_when_vendor_not_found_at_repository_on_update()
        {
            A.CallTo(() => _applicationRepository.SaveApplication(A<ApplicationUpdateCommand>.Ignored))
                .Returns(new ApplicationSaveResult.FailureVendorNotFound());
            using var client = SetUpClient();

            var updateResponse = await client.PutAsync("/v3/applications/1", UpdateContent());

            updateResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
            updateResponse.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
            var actualResponse = JsonNode.Parse(await updateResponse.Content.ReadAsStringAsync());
            var expectedResponse = JsonNode.Parse(
                """
                {
                  "detail": "Reference 'VendorId' does not exist.",
                  "type": "urn:ed-fi:management-api:conflict:unresolved-reference",
                  "title": "Unresolved Reference",
                  "status": 409,
                  "correlationId": "{correlationId}",
                  "validationErrors": {},
                  "errors": []
                }
                """.Replace("{correlationId}", actualResponse!["correlationId"]!.GetValue<string>())
            );
            JsonNode.DeepEquals(actualResponse, expectedResponse).Should().Be(true);
            AssertNoProviderUpdate();
        }

        [Test]
        public async Task It_returns_internal_server_error_when_vendor_lookup_fails_on_insert()
        {
            const string Sentinel = "SENTINEL_VENDOR_LOOKUP_INSERT_must_not_leak";
            A.CallTo(() => _vendorRepository.GetVendor(A<int>.Ignored))
                .Returns(new VendorGetResult.FailureUnknown(Sentinel));

            using var client = SetUpClient();

            var addResponse = await client.PostAsync(
                "/v3/applications",
                new StringContent(
                    """
                    {
                        "ApplicationName": "Application 102",
                        "ClaimSetName": "Test",
                        "VendorId": 1,
                        "EducationOrganizationIds": [1],
                        "DataStoreIds": [1]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );

            addResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            addResponse.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
            string responseBody = await addResponse.Content.ReadAsStringAsync();
            responseBody.Should().NotContain(Sentinel);
            JsonNode actualResponse = JsonNode.Parse(responseBody)!;
            JsonNode expectedResponse = JsonNode.Parse(
                """
                {
                  "detail": "",
                  "type": "urn:ed-fi:management-api:internal-server-error",
                  "title": "Internal Server Error",
                  "status": 500,
                  "correlationId": "{correlationId}",
                  "validationErrors": {},
                  "errors": []
                }
                """.Replace("{correlationId}", actualResponse["correlationId"]!.GetValue<string>())
            )!;
            JsonNode.DeepEquals(actualResponse, expectedResponse).Should().Be(true);
        }
    }

    [TestFixture]
    public class FailureDuplicateApplicationNameTests : ApplicationModuleTests
    {
        [SetUp]
        public void SetUp()
        {
            A.CallTo(() =>
                    _clientRepository.CreateClientAsync(
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<int[]?>.Ignored,
                        A<bool>.Ignored
                    )
                )
                .Returns(new ClientCreateResult.Success(Guid.NewGuid()));

            A.CallTo(() => _vendorRepository.GetVendor(A<int>.Ignored))
                .Returns(
                    new VendorGetResult.Success(
                        new VendorResponse
                        {
                            Company = "Test Company",
                            ContactName = "Test Contact",
                            ContactEmailAddress = "test@test.com",
                            NamespacePrefixes = "Test Prefix",
                        }
                    )
                );

            A.CallTo(() =>
                    _applicationRepository.InsertApplication(
                        A<ApplicationInsertCommand>.Ignored,
                        A<ApiClientCommand>.Ignored
                    )
                )
                .Returns(new ApplicationInsertResult.FailureDuplicateApplication("Test Application"));

            A.CallTo(() => _applicationRepository.SaveApplication(A<ApplicationUpdateCommand>.Ignored))
                .Returns(new ApplicationSaveResult.FailureDuplicateApplication("Test Application"));

            A.CallTo(() => _applicationRepository.GetApplicationApiClients(A<int>.Ignored))
                .Returns(
                    new ApplicationApiClientsResult.Success([new ApiClient("clientId", Guid.NewGuid(), true)])
                );

            A.CallTo(() =>
                    _clientRepository.UpdateClientAsync(
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<int[]?>.Ignored,
                        A<bool>.Ignored,
                        A<string>.Ignored
                    )
                )
                .Returns(new ClientUpdateResult.Success(Guid.NewGuid()));
        }

        [Test]
        public async Task Should_return_bad_request_for_duplicate_application_name_on_insert()
        {
            // Arrange
            using var client = SetUpClient();

            // Act
            var insertResponse = await client.PostAsync(
                "/v3/applications",
                new StringContent(
                    """
                    {
                        "ApplicationName": "Test Application",
                        "ClaimSetName": "TestClaimSet",
                        "VendorId": 1,
                        "EducationOrganizationIds": [1],
                        "DataStoreIds": [1]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );

            // Assert
            insertResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            string responseBody = await insertResponse.Content.ReadAsStringAsync();
            var actualResponse = JsonNode.Parse(responseBody);
            var expectedResponse = JsonNode.Parse(
                """
                {
                  "detail": "Data validation failed. See 'validationErrors' for details.",
                  "type": "urn:ed-fi:management-api:bad-request:data",
                  "title": "Data Validation Failed",
                  "status": 400,
                  "correlationId": "{correlationId}",
                  "validationErrors": {
                    "ApplicationName": [
                      "Application 'Test Application' already exists for vendor."
                    ]
                  },
                  "errors": []
                }
                """.Replace("{correlationId}", actualResponse!["correlationId"]!.GetValue<string>())
            );
            JsonNode.DeepEquals(actualResponse, expectedResponse).Should().Be(true);
        }

        [Test]
        public async Task Should_return_bad_request_for_duplicate_application_name_on_update()
        {
            A.CallTo(() => _applicationRepository.SaveApplication(A<ApplicationUpdateCommand>.Ignored))
                .Returns(new ApplicationSaveResult.FailureDuplicateApplication("Test Application"));
            using var client = SetUpClient();

            var updateResponse = await client.PutAsync("/v3/applications/1", UpdateContent());

            updateResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            var actualResponse = JsonNode.Parse(await updateResponse.Content.ReadAsStringAsync());
            var expectedResponse = JsonNode.Parse(
                """
                {
                  "detail": "Data validation failed. See 'validationErrors' for details.",
                  "type": "urn:ed-fi:management-api:bad-request:data",
                  "title": "Data Validation Failed",
                  "status": 400,
                  "correlationId": "{correlationId}",
                  "validationErrors": {
                    "ApplicationName": [
                      "Application 'Test Application' already exists for vendor."
                    ]
                  },
                  "errors": []
                }
                """.Replace("{correlationId}", actualResponse!["correlationId"]!.GetValue<string>())
            );
            JsonNode.DeepEquals(actualResponse, expectedResponse).Should().Be(true);
            AssertNoProviderUpdate();
        }
    }

    [TestFixture]
    public class FailureDuplicateClaimSetNameTests : ApplicationModuleTests
    {
        [SetUp]
        public void SetUp()
        {
            A.CallTo(() =>
                    _clientRepository.CreateClientAsync(
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<int[]?>.Ignored,
                        A<bool>.Ignored
                    )
                )
                .Returns(new ClientCreateResult.Success(Guid.NewGuid()));
        }
    }

    [TestFixture]
    public class FailureDataStoreNotFoundTests : ApplicationModuleTests
    {
        [SetUp]
        public void SetUp()
        {
            A.CallTo(() =>
                    _clientRepository.CreateClientAsync(
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<int[]?>.Ignored,
                        A<bool>.Ignored
                    )
                )
                .Returns(new ClientCreateResult.Success(Guid.NewGuid()));

            A.CallTo(() => _vendorRepository.GetVendor(A<int>.Ignored))
                .Returns(
                    new VendorGetResult.Success(
                        new VendorResponse
                        {
                            Company = "Test Company",
                            ContactName = "Test Contact",
                            ContactEmailAddress = "test@test.com",
                            NamespacePrefixes = "Test Prefix",
                        }
                    )
                );

            // Reset the default because sibling tests override this fake.
            A.CallTo(() => _dataStoreRepository.GetExistingDataStoreIds(A<int[]>.Ignored!))
                .Returns(new DataStoreIdsExistResult.Success([999]));

            A.CallTo(() =>
                    _applicationRepository.InsertApplication(
                        A<ApplicationInsertCommand>.Ignored,
                        A<ApiClientCommand>.Ignored
                    )
                )
                .Returns(new ApplicationInsertResult.FailureDataStoreNotFound());
        }

        [Test]
        public async Task Should_return_conflict_and_clean_up_when_data_store_not_found_at_repository_on_insert()
        {
            // Arrange
            List<string> deletedClientIds = [];
            A.CallTo(() => _clientRepository.DeleteClientAsync(A<string>.Ignored))
                .Invokes(call => deletedClientIds.Add(call.GetArgument<string>(0)!))
                .Returns(new ClientDeleteResult.Success());

            using var client = SetUpClient();

            // Act
            var insertResponse = await client.PostAsync(
                "/v3/applications",
                new StringContent(
                    """
                    {
                        "ApplicationName": "Test Application",
                        "ClaimSetName": "TestClaimSet",
                        "VendorId": 1,
                        "EducationOrganizationIds": [1],
                        "DataStoreIds": [999]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );

            // Assert
            insertResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
            insertResponse.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
            string responseBody = await insertResponse.Content.ReadAsStringAsync();
            var actualResponse = JsonNode.Parse(responseBody);
            var expectedResponse = JsonNode.Parse(
                """
                {
                  "detail": "Data store does not exist.",
                  "type": "urn:ed-fi:management-api:conflict:unresolved-reference",
                  "title": "Unresolved Reference",
                  "status": 409,
                  "correlationId": "{correlationId}",
                  "validationErrors": {},
                  "errors": []
                }
                """.Replace("{correlationId}", actualResponse!["correlationId"]!.GetValue<string>())
            );
            JsonNode.DeepEquals(actualResponse, expectedResponse).Should().Be(true);
            deletedClientIds.Should().HaveCount(1);
        }

        [Test]
        public async Task Should_not_create_identity_provider_client_when_insert_data_store_id_is_invalid()
        {
            // Arrange
            A.CallTo(() =>
                    _dataStoreRepository.GetExistingDataStoreIds(
                        A<int[]>.That.Matches(ids => ids.Length == 1 && ids[0] == 999)
                    )
                )
                .Returns(new DataStoreIdsExistResult.Success([]));

            using var client = SetUpClient();

            // Act
            var insertResponse = await client.PostAsync(
                "/v3/applications",
                new StringContent(
                    """
                    {
                        "ApplicationName": "Test Application",
                        "ClaimSetName": "TestClaimSet",
                        "VendorId": 1,
                        "EducationOrganizationIds": [1],
                        "DataStoreIds": [999]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );

            // Assert
            insertResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
            insertResponse.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
            A.CallTo(() =>
                    _clientRepository.CreateClientAsync(
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<int[]?>.Ignored,
                        A<bool>.Ignored
                    )
                )
                .MustNotHaveHappened();
            A.CallTo(() =>
                    _applicationRepository.InsertApplication(
                        A<ApplicationInsertCommand>.Ignored,
                        A<ApiClientCommand>.Ignored
                    )
                )
                .MustNotHaveHappened();
        }
    }

    [TestFixture]
    public class FailureProfileNotFoundTests : ApplicationModuleTests
    {
        [SetUp]
        public void SetUp()
        {
            A.CallTo(() =>
                    _clientRepository.CreateClientAsync(
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<int[]?>.Ignored,
                        A<bool>.Ignored
                    )
                )
                .Returns(new ClientCreateResult.Success(Guid.NewGuid()));

            A.CallTo(() => _vendorRepository.GetVendor(A<int>.Ignored))
                .Returns(
                    new VendorGetResult.Success(
                        new VendorResponse
                        {
                            Company = "Test Company",
                            ContactName = "Test Contact",
                            ContactEmailAddress = "test@test.com",
                            NamespacePrefixes = "Test Prefix",
                        }
                    )
                );

            A.CallTo(() =>
                    _applicationRepository.InsertApplication(
                        A<ApplicationInsertCommand>.Ignored,
                        A<ApiClientCommand>.Ignored
                    )
                )
                .Returns(new ApplicationInsertResult.FailureProfileNotFound());

            A.CallTo(() => _applicationRepository.SaveApplication(A<ApplicationUpdateCommand>.Ignored))
                .Returns(new ApplicationSaveResult.FailureProfileNotFound());

            A.CallTo(() => _applicationRepository.GetApplicationApiClients(A<int>.Ignored))
                .Returns(
                    new ApplicationApiClientsResult.Success([new ApiClient("clientId", Guid.NewGuid(), true)])
                );

            A.CallTo(() =>
                    _clientRepository.UpdateClientAsync(
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<int[]?>.Ignored,
                        A<bool>.Ignored,
                        A<string>.Ignored
                    )
                )
                .Returns(new ClientUpdateResult.Success(Guid.NewGuid()));
        }

        [Test]
        public async Task Should_return_conflict_and_clean_up_when_profile_not_found_at_repository_on_insert()
        {
            // Arrange
            List<string> deletedClientIds = [];
            A.CallTo(() => _clientRepository.DeleteClientAsync(A<string>.Ignored))
                .Invokes(call => deletedClientIds.Add(call.GetArgument<string>(0)!))
                .Returns(new ClientDeleteResult.Success());

            using var client = SetUpClient();

            // Act
            var insertResponse = await client.PostAsync(
                "/v3/applications",
                new StringContent(
                    """
                    {
                        "ApplicationName": "Test Application",
                        "ClaimSetName": "TestClaimSet",
                        "VendorId": 1,
                        "EducationOrganizationIds": [1],
                        "DataStoreIds": [1],
                        "ProfileIds": [999]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );

            // Assert
            insertResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
            insertResponse.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
            string responseBody = await insertResponse.Content.ReadAsStringAsync();
            var actualResponse = JsonNode.Parse(responseBody);
            var expectedResponse = JsonNode.Parse(
                """
                {
                  "detail": "Profile does not exist.",
                  "type": "urn:ed-fi:management-api:conflict:unresolved-reference",
                  "title": "Unresolved Reference",
                  "status": 409,
                  "correlationId": "{correlationId}",
                  "validationErrors": {},
                  "errors": []
                }
                """.Replace("{correlationId}", actualResponse!["correlationId"]!.GetValue<string>())
            );
            JsonNode.DeepEquals(actualResponse, expectedResponse).Should().Be(true);
            deletedClientIds.Should().HaveCount(1);
        }

        [Test]
        public async Task It_returns_conflict_when_profile_not_found_at_repository_on_update()
        {
            A.CallTo(() => _applicationRepository.SaveApplication(A<ApplicationUpdateCommand>.Ignored))
                .Returns(new ApplicationSaveResult.FailureProfileNotFound());
            using var client = SetUpClient();

            var updateResponse = await client.PutAsync("/v3/applications/1", UpdateContent());

            updateResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
            updateResponse.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
            var actualResponse = JsonNode.Parse(await updateResponse.Content.ReadAsStringAsync());
            var expectedResponse = JsonNode.Parse(
                """
                {
                  "detail": "Profile does not exist.",
                  "type": "urn:ed-fi:management-api:conflict:unresolved-reference",
                  "title": "Unresolved Reference",
                  "status": 409,
                  "correlationId": "{correlationId}",
                  "validationErrors": {},
                  "errors": []
                }
                """.Replace("{correlationId}", actualResponse!["correlationId"]!.GetValue<string>())
            );
            JsonNode.DeepEquals(actualResponse, expectedResponse).Should().Be(true);
            AssertNoProviderUpdate();
        }
    }

    protected static async Task AssertSanitizedInternalServerError(
        HttpResponseMessage response,
        string? sentinel = null
    )
    {
        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        string responseBody = await response.Content.ReadAsStringAsync();
        if (sentinel is not null)
        {
            responseBody.Should().NotContain(sentinel);
        }
        JsonNode actualResponse = JsonNode.Parse(responseBody)!;
        JsonNode expectedResponse = JsonNode.Parse(
            """
            {
              "detail": "",
              "type": "urn:ed-fi:management-api:internal-server-error",
              "title": "Internal Server Error",
              "status": 500,
              "correlationId": "{correlationId}",
              "validationErrors": {},
              "errors": []
            }
            """.Replace("{correlationId}", actualResponse["correlationId"]!.GetValue<string>())
        )!;
        JsonNode.DeepEquals(actualResponse, expectedResponse).Should().Be(true);
    }

    [TestFixture]
    public class Given_an_application_update_with_mismatched_route_and_body_ids : ApplicationModuleTests
    {
        private List<string> _dependencyCalls = null!;
        private HttpResponseMessage _updateResponse = null!;

        [SetUp]
        public async Task Act()
        {
            _dependencyCalls = [];
            A.CallTo(_lockManager).Invokes(call => _dependencyCalls.Add(call.Method.Name));
            A.CallTo(_applicationRepository).Invokes(call => _dependencyCalls.Add(call.Method.Name));
            A.CallTo(_apiClientRepository).Invokes(call => _dependencyCalls.Add(call.Method.Name));
            A.CallTo(_clientRepository).Invokes(call => _dependencyCalls.Add(call.Method.Name));
            A.CallTo(_vendorRepository).Invokes(call => _dependencyCalls.Add(call.Method.Name));
            A.CallTo(_dataStoreRepository).Invokes(call => _dependencyCalls.Add(call.Method.Name));
            A.CallTo(_profileRepository).Invokes(call => _dependencyCalls.Add(call.Method.Name));

            using var client = SetUpClient();
            _updateResponse = await client.PutAsync(
                "/v3/applications/1",
                new StringContent(
                    """
                    {
                        "Id": 9999,
                        "ApplicationName": "Test Application",
                        "ClaimSetName": "TestClaimSet",
                        "VendorId": 1,
                        "EducationOrganizationIds": [1],
                        "DataStoreIds": [1]
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
                  "type": "urn:ed-fi:management-api:bad-request:data",
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

        [Test]
        public void It_does_not_call_identity_provider_update_async()
        {
            A.CallTo(() =>
                    _clientRepository.UpdateClientAsync(
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<int[]?>.Ignored,
                        A<bool>.Ignored,
                        A<string>.Ignored
                    )
                )
                .MustNotHaveHappened();
        }

        [Test]
        public void It_does_not_save_the_application() =>
            A.CallTo(() => _applicationRepository.SaveApplication(A<ApplicationUpdateCommand>.Ignored))
                .MustNotHaveHappened();
    }

    [TestFixture]
    public class Given_an_application_update_with_omitted_body_id : ApplicationModuleTests
    {
        private HttpResponseMessage _updateResponse = null!;

        [SetUp]
        public async Task Act()
        {
            using var client = SetUpClient();
            var json = """
                {
                    "ApplicationName": "Test Application",
                    "ClaimSetName": "TestClaimSet",
                    "VendorId": 1,
                    "EducationOrganizationIds": [1],
                    "DataStoreIds": [1]
                }
                """;
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            _updateResponse = await client.PutAsync("/v3/applications/1", content);
        }

        [TearDown]
        public void TearDownResponse() => _updateResponse?.Dispose();

        [Test]
        public async Task It_returns_the_id_mismatch_validation_contract()
        {
            _updateResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            string responseBody = await _updateResponse.Content.ReadAsStringAsync();
            JsonNode actualResponse = JsonNode.Parse(responseBody)!;
            actualResponse["validationErrors"]!["Id"]![0]!
                .GetValue<string>()
                .Should()
                .Be("Request body id must match the id in the url.");
        }

        [Test]
        public void It_does_not_call_identity_provider_update_async()
        {
            A.CallTo(() =>
                    _clientRepository.UpdateClientAsync(
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<int[]?>.Ignored,
                        A<bool>.Ignored,
                        A<string>.Ignored
                    )
                )
                .MustNotHaveHappened();
        }

        [Test]
        public void It_does_not_save_the_application() =>
            A.CallTo(() => _applicationRepository.SaveApplication(A<ApplicationUpdateCommand>.Ignored))
                .MustNotHaveHappened();
    }

    [TestFixture]
    public class Given_an_application_delete_whose_identity_provider_client_is_already_missing
        : ApplicationModuleTests
    {
        private Guid _providerClientUuid;
        private List<string> _providerDeletes = null!;
        private List<int> _databaseDeletes = null!;
        private HttpResponseMessage _deleteResponse = null!;

        [SetUp]
        public async Task Act()
        {
            _providerClientUuid = Guid.NewGuid();
            _providerDeletes = [];
            _databaseDeletes = [];

            A.CallTo(() => _applicationRepository.GetApplicationApiClients(A<int>.Ignored))
                .Returns(
                    new ApplicationApiClientsResult.Success([
                        new ApiClient("clientId", _providerClientUuid, true),
                    ])
                );

            A.CallTo(() => _clientRepository.DeleteClientAsync(A<string>.Ignored))
                .Invokes(call => _providerDeletes.Add(call.GetArgument<string>(0)!))
                .Returns(new ClientDeleteResult.FailureClientNotFound("Client not found"));

            A.CallTo(() => _applicationRepository.DeleteApplication(A<int>.Ignored))
                .Invokes(call => _databaseDeletes.Add(call.GetArgument<int>(0)))
                .Returns(new ApplicationDeleteResult.Success());

            using var client = SetUpClient();
            _deleteResponse = await client.DeleteAsync("/v3/applications/1");
        }

        [TearDown]
        public void TearDownResponse() => _deleteResponse?.Dispose();

        [Test]
        public void It_returns_no_content() =>
            _deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        [Test]
        public void It_invokes_the_provider_delete_for_the_stored_client() =>
            _providerDeletes.Should().Equal(_providerClientUuid.ToString());

        [Test]
        public void It_invokes_the_database_delete() => _databaseDeletes.Should().Equal(1);
    }

    public abstract class DeleteProviderFailureTestBase : ApplicationModuleTests
    {
        protected Guid _providerClientUuid;
        protected List<string> _providerDeletes = null!;
        protected List<int> _databaseDeletes = null!;
        protected HttpResponseMessage _deleteResponse = null!;

        [SetUp]
        public void SetUpDeleteDefaults()
        {
            _providerClientUuid = Guid.NewGuid();
            _providerDeletes = [];
            _databaseDeletes = [];

            A.CallTo(() => _applicationRepository.GetApplicationApiClients(A<int>.Ignored))
                .Returns(
                    new ApplicationApiClientsResult.Success([
                        new ApiClient("clientId", _providerClientUuid, true),
                    ])
                );

            A.CallTo(() => _applicationRepository.DeleteApplication(A<int>.Ignored))
                .Invokes(call => _databaseDeletes.Add(call.GetArgument<int>(0)))
                .Returns(new ApplicationDeleteResult.Success());
        }

        [TearDown]
        public void TearDownResponse() => _deleteResponse?.Dispose();

        protected void ArrangeProviderDelete(ClientDeleteResult result)
        {
            A.CallTo(() => _clientRepository.DeleteClientAsync(A<string>.Ignored))
                .Invokes(call => _providerDeletes.Add(call.GetArgument<string>(0)!))
                .Returns(result);
        }

        protected async Task ActDeleteAsync()
        {
            using var client = SetUpClient();
            _deleteResponse = await client.DeleteAsync("/v3/applications/1");
        }
    }

    [TestFixture]
    public class Given_an_application_delete_whose_provider_deletion_fails_at_the_identity_provider
        : DeleteProviderFailureTestBase
    {
        private const string Sentinel = "SENTINEL_PROVIDER_DELETE_502_must_not_leak";

        private List<string> _callOrder = null!;
        private RecordingLockManager _recordingLockManager = null!;

        [SetUp]
        public async Task Act()
        {
            _callOrder = [];
            _recordingLockManager = new RecordingLockManager(() => _callOrder.Add("lock"));
            _lockManager = _recordingLockManager;

            A.CallTo(() => _applicationRepository.GetApplicationApiClients(A<int>.Ignored))
                .Invokes(_ => _callOrder.Add("clients-read"))
                .Returns(
                    new ApplicationApiClientsResult.Success([
                        new ApiClient("clientId", _providerClientUuid, true),
                    ])
                );

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
                  "type": "urn:ed-fi:management-api:bad-gateway",
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
        public void It_invokes_the_provider_delete_for_the_stored_client() =>
            _providerDeletes.Should().Equal(_providerClientUuid.ToString());

        [Test]
        public void It_does_not_delete_the_database_application() => _databaseDeletes.Should().BeEmpty();

        [Test]
        public void It_acquires_the_lock_before_the_clients_read()
        {
            _recordingLockManager.AcquiredApplicationIds.Should().Equal(1);
            _callOrder.Take(2).Should().Equal("lock", "clients-read");
        }

        [Test]
        public void It_releases_the_lock() => _recordingLockManager.Handle.Disposed.Should().BeTrue();
    }

    [TestFixture]
    public class Given_an_application_delete_whose_provider_deletion_fails_unknown
        : DeleteProviderFailureTestBase
    {
        private const string Sentinel = "SENTINEL_PROVIDER_DELETE_500_must_not_leak";

        [SetUp]
        public async Task Act()
        {
            ArrangeProviderDelete(new ClientDeleteResult.FailureUnknown(Sentinel));
            await ActDeleteAsync();
        }

        [Test]
        public async Task It_returns_a_sanitized_internal_server_error() =>
            await AssertSanitizedInternalServerError(_deleteResponse, Sentinel);

        [Test]
        public void It_invokes_the_provider_delete_for_the_stored_client() =>
            _providerDeletes.Should().Equal(_providerClientUuid.ToString());

        [Test]
        public void It_does_not_delete_the_database_application() => _databaseDeletes.Should().BeEmpty();
    }

    [TestFixture]
    public class Given_an_application_delete_whose_provider_deletion_throws : DeleteProviderFailureTestBase
    {
        private const string Sentinel = "SENTINEL_PROVIDER_DELETE_THROWN_must_not_leak";

        [SetUp]
        public async Task Act()
        {
            A.CallTo(() => _clientRepository.DeleteClientAsync(A<string>.Ignored))
                .Invokes(call => _providerDeletes.Add(call.GetArgument<string>(0)!))
                .Throws(new InvalidOperationException(Sentinel));
            await ActDeleteAsync();
        }

        [Test]
        public async Task It_returns_a_sanitized_internal_server_error() =>
            await AssertSanitizedInternalServerError(_deleteResponse, Sentinel);

        [Test]
        public void It_does_not_delete_the_database_application() => _databaseDeletes.Should().BeEmpty();
    }

    [TestFixture]
    public class Given_an_application_delete_whose_provider_deletion_returns_an_unrecognized_result
        : DeleteProviderFailureTestBase
    {
        private sealed record UnrecognizedClientDeleteResult : ClientDeleteResult;

        [SetUp]
        public async Task Act()
        {
            ArrangeProviderDelete(new UnrecognizedClientDeleteResult());
            await ActDeleteAsync();
        }

        [Test]
        public async Task It_returns_a_sanitized_internal_server_error() =>
            await AssertSanitizedInternalServerError(_deleteResponse);

        [Test]
        public void It_does_not_delete_the_database_application() => _databaseDeletes.Should().BeEmpty();
    }

    [TestFixture]
    public class Given_an_application_delete_failing_after_an_earlier_client_deletion
        : DeleteProviderFailureTestBase
    {
        private Guid _secondClientUuid;
        private Guid _thirdClientUuid;
        private List<int> _databaseDeletesAfterFailedAttempt = null!;
        private HttpResponseMessage _retryResponse = null!;

        [SetUp]
        public async Task Act()
        {
            _secondClientUuid = Guid.NewGuid();
            _thirdClientUuid = Guid.NewGuid();

            A.CallTo(() => _applicationRepository.GetApplicationApiClients(A<int>.Ignored))
                .Returns(
                    new ApplicationApiClientsResult.Success([
                        new ApiClient("clientOne", _providerClientUuid, true),
                        new ApiClient("clientTwo", _secondClientUuid, true),
                        new ApiClient("clientThree", _thirdClientUuid, true),
                    ])
                );

            // First attempt: the first client is deleted, the second fails at the provider,
            // and the third must never be attempted.
            ArrangeProviderDeletePerClient(
                new ClientDeleteResult.Success(),
                new ClientDeleteResult.FailureIdentityProvider(
                    new IdentityProviderError("provider unavailable")
                ),
                new ClientDeleteResult.Success()
            );

            await ActDeleteAsync();
            _databaseDeletesAfterFailedAttempt = [.. _databaseDeletes];

            // Retry: the client deleted by the failed attempt is already absent (idempotent
            // cleanup success) and the remaining deletions succeed, so the delete converges.
            ArrangeProviderDeletePerClient(
                new ClientDeleteResult.FailureClientNotFound("Client not found"),
                new ClientDeleteResult.Success(),
                new ClientDeleteResult.Success()
            );

            using var client = SetUpClient();
            _retryResponse = await client.DeleteAsync("/v3/applications/1");
        }

        [TearDown]
        public void TearDownRetryResponse() => _retryResponse?.Dispose();

        private void ArrangeProviderDeletePerClient(
            ClientDeleteResult first,
            ClientDeleteResult second,
            ClientDeleteResult third
        )
        {
            A.CallTo(() => _clientRepository.DeleteClientAsync(_providerClientUuid.ToString()))
                .Invokes(call => _providerDeletes.Add(call.GetArgument<string>(0)!))
                .Returns(first);
            A.CallTo(() => _clientRepository.DeleteClientAsync(_secondClientUuid.ToString()))
                .Invokes(call => _providerDeletes.Add(call.GetArgument<string>(0)!))
                .Returns(second);
            A.CallTo(() => _clientRepository.DeleteClientAsync(_thirdClientUuid.ToString()))
                .Invokes(call => _providerDeletes.Add(call.GetArgument<string>(0)!))
                .Returns(third);
        }

        [Test]
        public void It_returns_bad_gateway_for_the_failed_attempt() =>
            _deleteResponse.StatusCode.Should().Be(HttpStatusCode.BadGateway);

        [Test]
        public void It_stops_at_the_first_failed_client() =>
            _providerDeletes
                .Take(2)
                .Should()
                .Equal(_providerClientUuid.ToString(), _secondClientUuid.ToString());

        [Test]
        public void It_does_not_delete_the_database_application_on_the_failed_attempt() =>
            _databaseDeletesAfterFailedAttempt.Should().BeEmpty();

        [Test]
        public void It_converges_on_retry()
        {
            _retryResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
            _databaseDeletes.Should().Equal(1);
        }

        [Test]
        public void It_resumes_the_retry_across_every_client() =>
            _providerDeletes
                .Skip(2)
                .Should()
                .Equal(
                    _providerClientUuid.ToString(),
                    _secondClientUuid.ToString(),
                    _thirdClientUuid.ToString()
                );
    }

    [TestFixture]
    public class Given_an_application_insert_whose_cleanup_client_is_already_missing : ApplicationModuleTests
    {
        private Guid _createdClientUuid;
        private List<string> _deletedClientUuids = null!;
        private HttpResponseMessage _insertResponse = null!;

        [SetUp]
        public async Task Act()
        {
            _createdClientUuid = Guid.NewGuid();
            _deletedClientUuids = [];

            A.CallTo(() => _vendorRepository.GetVendor(A<int>.Ignored))
                .Returns(
                    new VendorGetResult.Success(
                        new VendorResponse
                        {
                            Company = "Test Company",
                            ContactName = "Test Contact",
                            ContactEmailAddress = "test@test.com",
                            NamespacePrefixes = "Test Prefix",
                        }
                    )
                );

            A.CallTo(() =>
                    _clientRepository.CreateClientAsync(
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<int[]?>.Ignored,
                        A<bool>.Ignored
                    )
                )
                .Returns(new ClientCreateResult.Success(_createdClientUuid));

            A.CallTo(() =>
                    _applicationRepository.InsertApplication(
                        A<ApplicationInsertCommand>.Ignored,
                        A<ApiClientCommand>.Ignored
                    )
                )
                .Returns(new ApplicationInsertResult.FailureVendorNotFound());

            A.CallTo(() => _clientRepository.DeleteClientAsync(A<string>.Ignored))
                .Invokes(call => _deletedClientUuids.Add(call.GetArgument<string>(0)!))
                .Returns(new ClientDeleteResult.FailureClientNotFound("Client not found"));

            using var client = SetUpClient();
            _insertResponse = await client.PostAsync(
                "/v3/applications",
                new StringContent(
                    """
                    {
                        "ApplicationName": "Test Application",
                        "ClaimSetName": "TestClaimSet",
                        "VendorId": 1,
                        "EducationOrganizationIds": [1],
                        "DataStoreIds": [1]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );
        }

        [TearDown]
        public void TearDownResponse() => _insertResponse?.Dispose();

        [Test]
        public async Task It_keeps_the_unresolved_reference_conflict_response()
        {
            _insertResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
            _insertResponse.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
            string responseBody = await _insertResponse.Content.ReadAsStringAsync();
            JsonNode actualResponse = JsonNode.Parse(responseBody)!;
            string correlationId = actualResponse["correlationId"]!.GetValue<string>();
            correlationId.Should().NotBeNullOrWhiteSpace();
            JsonNode expectedResponse = JsonNode.Parse(
                """
                {
                  "detail": "Reference 'VendorId' does not exist.",
                  "type": "urn:ed-fi:management-api:conflict:unresolved-reference",
                  "title": "Unresolved Reference",
                  "status": 409,
                  "correlationId": "{correlationId}",
                  "validationErrors": {},
                  "errors": []
                }
                """.Replace("{correlationId}", correlationId)
            )!;
            JsonNode.DeepEquals(actualResponse, expectedResponse).Should().Be(true);
        }

        [Test]
        public void It_deletes_the_client_it_created() =>
            _deletedClientUuids.Should().Equal(_createdClientUuid.ToString());
    }

    /// <summary>
    /// Shared arrangement for POST /v3/applications: a resolvable vendor, a provider that creates
    /// a client, and a recording module logger. Fixtures override the one outcome under test.
    /// </summary>
    public abstract class InsertWorkflowTestBase : ApplicationModuleTests
    {
        /// <summary>
        /// Appended to a sentinel so the sanitizer has something to remove. Only the sanitized
        /// form may reach a log, and neither form may reach a response.
        /// </summary>
        protected const string RawSuffix = "\r\n<b>{raw}</b>";

        protected const string SanitizedSuffix = "braw/b";

        private const string RequestBody = """
            {
                "ApplicationName": "Test Application",
                "ClaimSetName": "TestClaimSet",
                "VendorId": 1,
                "EducationOrganizationIds": [1],
                "DataStoreIds": [1]
            }
            """;

        protected Guid _createdClientUuid;
        protected string _createdClientId = null!;
        protected string _createdClientSecret = null!;
        protected string? _forwardedClientRole;
        protected List<string> _deletedClientUuids = null!;

        // Internal because the recording logger type is: every fixture lives in this assembly.
        internal TestLogger<ApplicationModule> _moduleLogger = null!;
        protected HttpResponseMessage _insertResponse = null!;

        [SetUp]
        public void SetUpInsertWorkflow()
        {
            _createdClientUuid = Guid.NewGuid();
            _deletedClientUuids = [];
            _moduleLogger = new TestLogger<ApplicationModule>();

            A.CallTo(() => _vendorRepository.GetVendor(A<int>.Ignored))
                .Returns(
                    new VendorGetResult.Success(
                        new VendorResponse
                        {
                            Company = "Test Company",
                            ContactName = "Test Contact",
                            ContactEmailAddress = "test@test.com",
                            NamespacePrefixes = "uri://ed-fi.org",
                        }
                    )
                );

            A.CallTo(() =>
                    _clientRepository.CreateClientAsync(
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<int[]?>.Ignored,
                        A<bool>.Ignored
                    )
                )
                .Invokes(call =>
                {
                    _createdClientId = call.GetArgument<string>(0)!;
                    _createdClientSecret = call.GetArgument<string>(1)!;
                    _forwardedClientRole = call.GetArgument<string>(2);
                })
                .Returns(new ClientCreateResult.Success(_createdClientUuid));

            A.CallTo(() => _clientRepository.DeleteClientAsync(A<string>.Ignored))
                .Invokes(call => _deletedClientUuids.Add(call.GetArgument<string>(0)!))
                .Returns(new ClientDeleteResult.Success());
        }

        [TearDown]
        public void TearDownInsertResponse() => _insertResponse?.Dispose();

        protected void ArrangeDatabaseInsert(ApplicationInsertResult result) =>
            A.CallTo(() =>
                    _applicationRepository.InsertApplication(
                        A<ApplicationInsertCommand>.Ignored,
                        A<ApiClientCommand>.Ignored
                    )
                )
                .Returns(result);

        protected void ArrangeProviderCreate(ClientCreateResult result) =>
            A.CallTo(() =>
                    _clientRepository.CreateClientAsync(
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<int[]?>.Ignored,
                        A<bool>.Ignored
                    )
                )
                .Invokes(call =>
                {
                    _createdClientId = call.GetArgument<string>(0)!;
                    _createdClientSecret = call.GetArgument<string>(1)!;
                    _forwardedClientRole = call.GetArgument<string>(2);
                })
                .Returns(result);

        protected void ArrangeProviderCleanup(ClientDeleteResult result) =>
            A.CallTo(() => _clientRepository.DeleteClientAsync(A<string>.Ignored))
                .Invokes(call => _deletedClientUuids.Add(call.GetArgument<string>(0)!))
                .Returns(result);

        protected async Task ActInsertAsync(string? clientRole = null)
        {
            using var client = SetUpClient(configureServices: collection =>
            {
                collection.AddSingleton<ILogger<ApplicationModule>>(_moduleLogger);
                if (clientRole is not null)
                {
                    collection.Configure<IdentitySettings>(options => options.ClientRole = clientRole);
                }
            });

            _insertResponse = await client.PostAsync(
                "/v3/applications",
                new StringContent(RequestBody, Encoding.UTF8, "application/json")
            );
        }

        /// <summary>
        /// Each recorded entry at this level, as its formatted message together with the text of
        /// any exception logged beside it.
        /// </summary>
        protected IReadOnlyList<string> LoggedAt(LogLevel level) =>
            [
                .. _moduleLogger
                    .Entries.Where(entry => entry.Level == level)
                    .Select(entry => $"{entry.State} {entry.Exception}"),
            ];

        protected void AssertNoDatabaseInsert() =>
            A.CallTo(() =>
                    _applicationRepository.InsertApplication(
                        A<ApplicationInsertCommand>.Ignored,
                        A<ApiClientCommand>.Ignored
                    )
                )
                .MustNotHaveHappened();

        protected void AssertNoProviderCleanup() =>
            A.CallTo(() => _clientRepository.DeleteClientAsync(A<string>.Ignored)).MustNotHaveHappened();

        /// <summary>
        /// A failed request returns no credentials: neither member is present, and the secret the
        /// module generated appears nowhere in the body.
        /// </summary>
        protected async Task AssertNoCredentialsInResponse()
        {
            string responseBody = await _insertResponse.Content.ReadAsStringAsync();
            JsonNode actualResponse = JsonNode.Parse(responseBody)!;
            actualResponse["key"].Should().BeNull();
            actualResponse["secret"].Should().BeNull();
            responseBody.Should().NotContain(_createdClientSecret);
        }

        protected void AssertCleanupReportedAsUnconfirmed(string outcome)
        {
            LoggedAt(LogLevel.Error)
                .Should()
                .Contain(entry =>
                    entry.Contains("Could not confirm deletion of provider client")
                    && entry.Contains(_createdClientUuid.ToString())
                    && entry.Contains(_createdClientId)
                    && entry.Contains(outcome)
                );
            AssertNoRawCharactersLogged();
        }

        /// <summary>
        /// Inherited by every insert fixture: no module log entry may carry the secret this
        /// request generated, in its message or in the text of an exception logged beside it. The
        /// captured secret is asserted non-empty first, so an arrangement that never reached the
        /// provider cannot make the check vacuous.
        /// </summary>
        [Test]
        public void It_logs_no_secret()
        {
            _createdClientSecret
                .Should()
                .NotBeNullOrEmpty("the module should have generated a secret for the provider");
            _moduleLogger
                .Entries.Select(entry => $"{entry.State} {entry.Exception}")
                .Should()
                .OnlyContain(entry => !entry.Contains(_createdClientSecret));
        }

        protected void AssertNoRawCharactersLogged() =>
            _moduleLogger
                .Entries.Select(entry => $"{entry.State}")
                .Should()
                .OnlyContain(message =>
                    !message.Contains('\r')
                    && !message.Contains('\n')
                    && !message.Contains('<')
                    && !message.Contains('>')
                );
    }

    /// <summary>
    /// A provider failure during creation is answered with the structured bad-gateway contract,
    /// and nothing is persisted. The module attempts no cleanup because a creation failure carries
    /// no client identifier: whether a client was left behind, and its removal, belong to the
    /// repository's own compensation.
    /// </summary>
    [TestFixture]
    public class Given_an_application_insert_whose_provider_creation_fails_at_the_identity_provider
        : InsertWorkflowTestBase
    {
        private const string Sentinel = "SENTINEL_INSERT_IDP_CREATE_must_not_leak";

        [SetUp]
        public async Task Act()
        {
            ArrangeProviderCreate(
                new ClientCreateResult.FailureIdentityProvider(new IdentityProviderError(Sentinel))
            );

            await ActInsertAsync();
        }

        [Test]
        public async Task It_returns_the_bad_gateway_contract()
        {
            _insertResponse.StatusCode.Should().Be(HttpStatusCode.BadGateway);
            _insertResponse.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
            string responseBody = await _insertResponse.Content.ReadAsStringAsync();
            responseBody.Should().NotContain(Sentinel);
            JsonNode actualResponse = JsonNode.Parse(responseBody)!;
            string correlationId = actualResponse["correlationId"]!.GetValue<string>();
            correlationId.Should().NotBeNullOrWhiteSpace();
            JsonNode expectedResponse = JsonNode.Parse(
                """
                {
                  "detail": "The request could not be processed. See 'errors' for details.",
                  "type": "urn:ed-fi:management-api:bad-gateway",
                  "title": "Bad Gateway",
                  "status": 502,
                  "correlationId": "{correlationId}",
                  "validationErrors": {},
                  "errors": ["The identity provider returned an unexpected response."]
                }
                """.Replace("{correlationId}", correlationId)
            )!;
            JsonNode.DeepEquals(actualResponse, expectedResponse).Should().Be(true);
        }

        [Test]
        public async Task It_returns_no_credentials() => await AssertNoCredentialsInResponse();

        [Test]
        public void It_persists_no_application() => AssertNoDatabaseInsert();

        [Test]
        public void It_attempts_no_cleanup() => AssertNoProviderCleanup();
    }

    [TestFixture]
    public class Given_an_application_insert_whose_provider_creation_fails_unknown : InsertWorkflowTestBase
    {
        private const string Sentinel = "SENTINEL_INSERT_CREATE_UNKNOWN_must_not_leak";

        [SetUp]
        public async Task Act()
        {
            ArrangeProviderCreate(new ClientCreateResult.FailureUnknown(Sentinel + RawSuffix));

            await ActInsertAsync();
        }

        [Test]
        public async Task It_returns_the_sanitized_internal_server_error() =>
            await AssertSanitizedInternalServerError(_insertResponse, Sentinel);

        [Test]
        public async Task It_returns_no_credentials() => await AssertNoCredentialsInResponse();

        [Test]
        public void It_persists_no_application() => AssertNoDatabaseInsert();

        [Test]
        public void It_attempts_no_cleanup() => AssertNoProviderCleanup();

        [Test]
        public void It_logs_the_sanitized_failure_message_and_not_the_result_record()
        {
            LoggedAt(LogLevel.Error).Should().Contain(entry => entry.Contains(Sentinel + SanitizedSuffix));
            LoggedAt(LogLevel.Error).Should().NotContain(entry => entry.Contains("FailureUnknown {"));
            AssertNoRawCharactersLogged();
        }
    }

    [TestFixture]
    public class Given_an_application_insert_whose_cleanup_succeeds : InsertWorkflowTestBase
    {
        [SetUp]
        public async Task Act()
        {
            ArrangeDatabaseInsert(new ApplicationInsertResult.FailureVendorNotFound());

            await ActInsertAsync();
        }

        [Test]
        public void It_keeps_the_unresolved_reference_conflict() =>
            _insertResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);

        [Test]
        public void It_deletes_the_client_it_created() =>
            _deletedClientUuids.Should().Equal(_createdClientUuid.ToString());

        [Test]
        public void It_records_the_deletion() =>
            LoggedAt(LogLevel.Debug)
                .Should()
                .Contain(entry =>
                    entry.Contains("Deleted provider client")
                    && entry.Contains(_createdClientUuid.ToString())
                    && entry.Contains(_createdClientId)
                );

        [Test]
        public void It_reports_no_unconfirmed_cleanup() =>
            LoggedAt(LogLevel.Error)
                .Should()
                .NotContain(entry => entry.Contains("Could not confirm deletion"));
    }

    [TestFixture]
    public class Given_an_application_insert_whose_cleanup_client_is_already_absent : InsertWorkflowTestBase
    {
        [SetUp]
        public async Task Act()
        {
            ArrangeDatabaseInsert(new ApplicationInsertResult.FailureVendorNotFound());
            ArrangeProviderCleanup(new ClientDeleteResult.FailureClientNotFound("Client not found"));

            await ActInsertAsync();
        }

        [Test]
        public void It_keeps_the_unresolved_reference_conflict() =>
            _insertResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);

        [Test]
        public void It_records_the_absent_client_as_a_warning() =>
            LoggedAt(LogLevel.Warning)
                .Should()
                .Contain(entry =>
                    entry.Contains("was already absent")
                    && entry.Contains(_createdClientUuid.ToString())
                    && entry.Contains(_createdClientId)
                );

        [Test]
        public void It_reports_no_unconfirmed_cleanup() =>
            LoggedAt(LogLevel.Error)
                .Should()
                .NotContain(entry => entry.Contains("Could not confirm deletion"));
    }

    /// <summary>
    /// A cleanup the provider refuses leaves a client behind, which an operator has to remove.
    /// The caller still receives the conflict their request has to fix.
    /// </summary>
    [TestFixture]
    public class Given_an_application_insert_whose_cleanup_fails_at_the_identity_provider
        : InsertWorkflowTestBase
    {
        private const string Sentinel = "SENTINEL_INSERT_CLEANUP_IDP_must_not_leak";

        [SetUp]
        public async Task Act()
        {
            ArrangeDatabaseInsert(new ApplicationInsertResult.FailureVendorNotFound());
            ArrangeProviderCleanup(
                new ClientDeleteResult.FailureIdentityProvider(
                    new IdentityProviderError(Sentinel + RawSuffix)
                )
            );

            await ActInsertAsync();
        }

        [Test]
        public async Task It_keeps_the_unresolved_reference_conflict_response()
        {
            _insertResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
            _insertResponse.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
            string responseBody = await _insertResponse.Content.ReadAsStringAsync();
            responseBody.Should().NotContain(Sentinel);
            JsonNode actualResponse = JsonNode.Parse(responseBody)!;
            string correlationId = actualResponse["correlationId"]!.GetValue<string>();
            JsonNode expectedResponse = JsonNode.Parse(
                """
                {
                  "detail": "Reference 'VendorId' does not exist.",
                  "type": "urn:ed-fi:management-api:conflict:unresolved-reference",
                  "title": "Unresolved Reference",
                  "status": 409,
                  "correlationId": "{correlationId}",
                  "validationErrors": {},
                  "errors": []
                }
                """.Replace("{correlationId}", correlationId)
            )!;
            JsonNode.DeepEquals(actualResponse, expectedResponse).Should().Be(true);
        }

        [Test]
        public void It_deletes_the_client_it_created() =>
            _deletedClientUuids.Should().Equal(_createdClientUuid.ToString());

        [Test]
        public void It_reports_the_client_as_possibly_remaining()
        {
            AssertCleanupReportedAsUnconfirmed(nameof(ClientDeleteResult.FailureIdentityProvider));
            LoggedAt(LogLevel.Error).Should().Contain(entry => entry.Contains(Sentinel + SanitizedSuffix));
        }
    }

    [TestFixture]
    public class Given_an_application_insert_whose_cleanup_fails_unknown : InsertWorkflowTestBase
    {
        private const string DatabaseSentinel = "SENTINEL_INSERT_DB_UNKNOWN_must_not_leak";
        private const string CleanupSentinel = "SENTINEL_INSERT_CLEANUP_UNKNOWN_must_not_leak";

        [SetUp]
        public async Task Act()
        {
            ArrangeDatabaseInsert(new ApplicationInsertResult.FailureUnknown(DatabaseSentinel + RawSuffix));
            ArrangeProviderCleanup(new ClientDeleteResult.FailureUnknown(CleanupSentinel + RawSuffix));

            await ActInsertAsync();
        }

        [Test]
        public async Task It_returns_the_sanitized_internal_server_error()
        {
            await AssertSanitizedInternalServerError(_insertResponse, DatabaseSentinel);
            (await _insertResponse.Content.ReadAsStringAsync()).Should().NotContain(CleanupSentinel);
        }

        [Test]
        public async Task It_returns_no_credentials() => await AssertNoCredentialsInResponse();

        [Test]
        public void It_deletes_the_client_it_created() =>
            _deletedClientUuids.Should().Equal(_createdClientUuid.ToString());

        [Test]
        public void It_reports_both_failures_sanitized()
        {
            AssertCleanupReportedAsUnconfirmed(nameof(ClientDeleteResult.FailureUnknown));
            LoggedAt(LogLevel.Error)
                .Should()
                .Contain(entry => entry.Contains(DatabaseSentinel + SanitizedSuffix))
                .And.Contain(entry => entry.Contains(CleanupSentinel + SanitizedSuffix));
        }
    }

    [TestFixture]
    public class Given_an_application_insert_whose_cleanup_returns_an_unrecognized_result
        : InsertWorkflowTestBase
    {
        private sealed record UnrecognizedClientDeleteResult : ClientDeleteResult;

        [SetUp]
        public async Task Act()
        {
            ArrangeDatabaseInsert(new ApplicationInsertResult.FailureVendorNotFound());
            ArrangeProviderCleanup(new UnrecognizedClientDeleteResult());

            await ActInsertAsync();
        }

        [Test]
        public void It_keeps_the_unresolved_reference_conflict() =>
            _insertResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);

        [Test]
        public void It_reports_the_unrecognized_outcome() =>
            AssertCleanupReportedAsUnconfirmed(nameof(UnrecognizedClientDeleteResult));
    }

    /// <summary>
    /// A cleanup that throws used to reach the global handler and replace the caller's validation
    /// failure with a generic server error. The correction the caller has to make survives it.
    /// </summary>
    [TestFixture]
    public class Given_an_application_insert_whose_cleanup_throws : InsertWorkflowTestBase
    {
        private const string Sentinel = "SENTINEL_INSERT_CLEANUP_THROWN_must_not_leak";

        [SetUp]
        public async Task Act()
        {
            ArrangeDatabaseInsert(
                new ApplicationInsertResult.FailureDuplicateApplication("Test Application")
            );
            A.CallTo(() => _clientRepository.DeleteClientAsync(A<string>.Ignored))
                .Invokes(call => _deletedClientUuids.Add(call.GetArgument<string>(0)!))
                .Throws(new InvalidOperationException(Sentinel));

            await ActInsertAsync();
        }

        [Test]
        public async Task It_keeps_the_duplicate_name_validation_response()
        {
            _insertResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            _insertResponse.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
            string responseBody = await _insertResponse.Content.ReadAsStringAsync();
            responseBody.Should().NotContain(Sentinel);
            JsonNode actualResponse = JsonNode.Parse(responseBody)!;
            string correlationId = actualResponse["correlationId"]!.GetValue<string>();
            JsonNode expectedResponse = JsonNode.Parse(
                """
                {
                  "detail": "Data validation failed. See 'validationErrors' for details.",
                  "type": "urn:ed-fi:management-api:bad-request:data",
                  "title": "Data Validation Failed",
                  "status": 400,
                  "correlationId": "{correlationId}",
                  "validationErrors": {
                    "ApplicationName": [
                      "Application 'Test Application' already exists for vendor."
                    ]
                  },
                  "errors": []
                }
                """.Replace("{correlationId}", correlationId)
            )!;
            JsonNode.DeepEquals(actualResponse, expectedResponse).Should().Be(true);
        }

        [Test]
        public void It_attempted_the_deletion() =>
            _deletedClientUuids.Should().Equal(_createdClientUuid.ToString());

        [Test]
        public void It_reports_the_thrown_cleanup_with_its_exception()
        {
            AssertCleanupReportedAsUnconfirmed(nameof(InvalidOperationException));
            _moduleLogger
                .Entries.Should()
                .Contain(entry =>
                    entry.Level == LogLevel.Error && entry.Exception is InvalidOperationException
                );
        }
    }

    /// <summary>
    /// Shared arrangement for the insert's identifier-conflict arms: the database rejects the
    /// initial client's row under one of the two identifier constraints, and the fixture decides
    /// what the UUID reference check reports. Every fixture asserts the exact sanitized 409 body,
    /// the absence of credentials, and whether the provisioned provider client was deleted.
    /// </summary>
    public abstract class IdentifierConflictInsertTestBase : InsertWorkflowTestBase
    {
        protected const string DuplicateClientIdDetail =
            "The generated client key is already in use. Retry the request.";

        protected const string DuplicateClientUuidDetail =
            "The identity provider client is already associated with another API client.";

        protected const string LeftInPlaceMarker = "Could not prove provider client";

        protected void ArrangeReferenceCheck(ApiClientUuidReferenceResult result) =>
            A.CallTo(() => _apiClientRepository.HasApiClientUuidReference(A<Guid>.Ignored)).Returns(result);

        protected async Task AssertIdentifierConflictContract(string detail)
        {
            _insertResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
            _insertResponse.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
            string responseBody = await _insertResponse.Content.ReadAsStringAsync();
            JsonNode actualResponse = JsonNode.Parse(responseBody)!;
            string correlationId = actualResponse["correlationId"]!.GetValue<string>();
            correlationId.Should().NotBeNullOrWhiteSpace();
            JsonNode expectedResponse = JsonNode.Parse(
                $$"""
                {
                  "detail": "{{detail}}",
                  "type": "urn:ed-fi:management-api:conflict",
                  "title": "Conflict",
                  "status": 409,
                  "correlationId": "{{correlationId}}",
                  "validationErrors": {},
                  "errors": []
                }
                """
            )!;
            JsonNode.DeepEquals(actualResponse, expectedResponse).Should().Be(true);
            responseBody.Should().NotContain(_createdClientUuid.ToString());
            responseBody.Should().NotContain(_createdClientId);
        }

        protected void AssertReferenceCheckedOnceForTheCreatedUuid() =>
            A.CallTo(() => _apiClientRepository.HasApiClientUuidReference(_createdClientUuid))
                .MustHaveHappenedOnceExactly();

        protected void AssertLeftInPlace(string outcome)
        {
            LoggedAt(LogLevel.Error)
                .Should()
                .ContainSingle(entry =>
                    entry.Contains(LeftInPlaceMarker)
                    && entry.Contains(_createdClientUuid.ToString())
                    && entry.Contains(_createdClientId)
                    && entry.Contains(outcome)
                    && entry.Contains("left in place")
                );
            AssertNoRawCharactersLogged();
        }
    }

    /// <summary>
    /// The generated key collided and no row holds the provisioned client's UUID, so the client is
    /// deleted exactly once and the caller is told to retry.
    /// </summary>
    [TestFixture]
    public class Given_an_application_insert_whose_generated_key_is_already_in_use
        : IdentifierConflictInsertTestBase
    {
        [SetUp]
        public async Task Act()
        {
            ArrangeDatabaseInsert(new ApplicationInsertResult.FailureDuplicateClientId());
            ArrangeReferenceCheck(new ApiClientUuidReferenceResult.None());

            await ActInsertAsync();
        }

        [Test]
        public async Task It_returns_the_duplicate_key_conflict_contract() =>
            await AssertIdentifierConflictContract(DuplicateClientIdDetail);

        [Test]
        public async Task It_returns_no_credentials() => await AssertNoCredentialsInResponse();

        [Test]
        public void It_checks_the_created_uuid_for_references_before_deleting() =>
            AssertReferenceCheckedOnceForTheCreatedUuid();

        [Test]
        public void It_deletes_the_provisioned_client_exactly_once() =>
            _deletedClientUuids.Should().Equal(_createdClientUuid.ToString());

        [Test]
        public void It_records_the_collision_as_a_warning_with_the_client_id() =>
            LoggedAt(LogLevel.Warning)
                .Should()
                .Contain(entry => entry.Contains("already in use") && entry.Contains(_createdClientId));

        [Test]
        public void It_logs_nothing_at_error() => LoggedAt(LogLevel.Error).Should().BeEmpty();
    }

    /// <summary>
    /// The both-identifier arrangement: the database reports the key collision, and the reference
    /// check finds another row already holding the provisioned client's UUID. The client is in
    /// use, so it stays, and the key conflict is still what the caller is told.
    /// </summary>
    [TestFixture]
    public class Given_an_application_insert_whose_generated_key_collides_and_whose_uuid_is_referenced
        : IdentifierConflictInsertTestBase
    {
        [SetUp]
        public async Task Act()
        {
            ArrangeDatabaseInsert(new ApplicationInsertResult.FailureDuplicateClientId());
            ArrangeReferenceCheck(new ApiClientUuidReferenceResult.Referenced());

            await ActInsertAsync();
        }

        [Test]
        public async Task It_returns_the_duplicate_key_conflict_contract() =>
            await AssertIdentifierConflictContract(DuplicateClientIdDetail);

        [Test]
        public async Task It_returns_no_credentials() => await AssertNoCredentialsInResponse();

        [Test]
        public void It_checks_the_created_uuid_for_references() =>
            AssertReferenceCheckedOnceForTheCreatedUuid();

        [Test]
        public void It_deletes_nothing() => AssertNoProviderCleanup();

        [Test]
        public void It_reports_the_client_left_in_place() =>
            AssertLeftInPlace(nameof(ApiClientUuidReferenceResult.Referenced));
    }

    [TestFixture]
    public class Given_an_application_insert_whose_generated_key_collides_and_whose_reference_check_fails
        : IdentifierConflictInsertTestBase
    {
        private const string Sentinel = "SENTINEL_APPLICATION_REFERENCE_CHECK_UNKNOWN_must_not_leak";

        [SetUp]
        public async Task Act()
        {
            ArrangeDatabaseInsert(new ApplicationInsertResult.FailureDuplicateClientId());
            ArrangeReferenceCheck(new ApiClientUuidReferenceResult.FailureUnknown(Sentinel + RawSuffix));

            await ActInsertAsync();
        }

        [Test]
        public async Task It_returns_the_duplicate_key_conflict_contract()
        {
            await AssertIdentifierConflictContract(DuplicateClientIdDetail);
            (await _insertResponse.Content.ReadAsStringAsync()).Should().NotContain(Sentinel);
        }

        [Test]
        public async Task It_returns_no_credentials() => await AssertNoCredentialsInResponse();

        [Test]
        public void It_deletes_nothing() => AssertNoProviderCleanup();

        [Test]
        public void It_reports_the_client_left_in_place_with_the_sanitized_failure() =>
            AssertLeftInPlace(
                $"{nameof(ApiClientUuidReferenceResult.FailureUnknown)}: {Sentinel}{SanitizedSuffix}"
            );
    }

    [TestFixture]
    public class Given_an_application_insert_whose_generated_key_collides_and_whose_reference_check_is_unrecognized
        : IdentifierConflictInsertTestBase
    {
        private sealed record UnrecognizedReferenceResult : ApiClientUuidReferenceResult;

        [SetUp]
        public async Task Act()
        {
            ArrangeDatabaseInsert(new ApplicationInsertResult.FailureDuplicateClientId());
            ArrangeReferenceCheck(new UnrecognizedReferenceResult());

            await ActInsertAsync();
        }

        [Test]
        public async Task It_returns_the_duplicate_key_conflict_contract() =>
            await AssertIdentifierConflictContract(DuplicateClientIdDetail);

        [Test]
        public void It_deletes_nothing() => AssertNoProviderCleanup();

        [Test]
        public void It_reports_the_client_left_in_place_naming_the_outcome() =>
            AssertLeftInPlace(nameof(UnrecognizedReferenceResult));
    }

    /// <summary>
    /// A reference check that throws must not reach the global handler: the 409 stands, nothing is
    /// deleted, and the thrown check is reported by its exception type alone. The exception's
    /// message is never sanitized, so attaching it would log whatever text it carries; the
    /// sentinel therefore carries the raw suffix and must appear nowhere in the log in either form.
    /// </summary>
    [TestFixture]
    public class Given_an_application_insert_whose_generated_key_collides_and_whose_reference_check_throws
        : IdentifierConflictInsertTestBase
    {
        private const string Sentinel = "SENTINEL_APPLICATION_REFERENCE_CHECK_THROWN_must_not_leak";

        [SetUp]
        public async Task Act()
        {
            ArrangeDatabaseInsert(new ApplicationInsertResult.FailureDuplicateClientId());
            A.CallTo(() => _apiClientRepository.HasApiClientUuidReference(A<Guid>.Ignored))
                .Throws(new InvalidOperationException(Sentinel + RawSuffix));

            await ActInsertAsync();
        }

        [Test]
        public async Task It_returns_the_duplicate_key_conflict_contract()
        {
            await AssertIdentifierConflictContract(DuplicateClientIdDetail);
            (await _insertResponse.Content.ReadAsStringAsync()).Should().NotContain(Sentinel);
        }

        [Test]
        public async Task It_returns_no_credentials() => await AssertNoCredentialsInResponse();

        [Test]
        public void It_deletes_nothing() => AssertNoProviderCleanup();

        [Test]
        public void It_reports_the_thrown_check_by_type_only() =>
            AssertLeftInPlace(nameof(InvalidOperationException));

        [Test]
        public void It_attaches_no_exception_to_any_log_entry() =>
            _moduleLogger.Entries.Should().AllSatisfy(entry => entry.Exception.Should().BeNull());

        [Test]
        public void It_logs_the_exception_message_in_neither_form() =>
            _moduleLogger
                .Entries.Select(entry => $"{entry.State}")
                .Should()
                .OnlyContain(message => !message.Contains(Sentinel));
    }

    /// <summary>
    /// The reference check clears the deletion, and the deletion itself then fails. The existing
    /// cleanup helper reports the client as possibly remaining; the caller's 409 is unchanged.
    /// </summary>
    [TestFixture]
    public class Given_an_application_insert_whose_generated_key_collides_and_whose_cleanup_throws
        : IdentifierConflictInsertTestBase
    {
        private const string Sentinel = "SENTINEL_APPLICATION_KEY_CLEANUP_THROWN_must_not_leak";

        [SetUp]
        public async Task Act()
        {
            ArrangeDatabaseInsert(new ApplicationInsertResult.FailureDuplicateClientId());
            ArrangeReferenceCheck(new ApiClientUuidReferenceResult.None());
            A.CallTo(() => _clientRepository.DeleteClientAsync(A<string>.Ignored))
                .Invokes(call => _deletedClientUuids.Add(call.GetArgument<string>(0)!))
                .Throws(new InvalidOperationException(Sentinel));

            await ActInsertAsync();
        }

        [Test]
        public async Task It_keeps_the_duplicate_key_conflict_contract()
        {
            await AssertIdentifierConflictContract(DuplicateClientIdDetail);
            (await _insertResponse.Content.ReadAsStringAsync()).Should().NotContain(Sentinel);
        }

        [Test]
        public void It_attempted_the_deletion() =>
            _deletedClientUuids.Should().Equal(_createdClientUuid.ToString());

        [Test]
        public void It_reports_the_thrown_cleanup_as_unconfirmed() =>
            AssertCleanupReportedAsUnconfirmed(nameof(InvalidOperationException));
    }

    /// <summary>
    /// The Application row is inserted before the ApiClient row, so a name conflict is reported
    /// before the UUID constraint can judge the UUID the provider reported. Another row holds that
    /// UUID, so the client stays, and the caller still gets the duplicate-name validation failure.
    /// </summary>
    [TestFixture]
    public class Given_an_application_insert_with_a_duplicate_name_whose_uuid_is_referenced
        : IdentifierConflictInsertTestBase
    {
        [SetUp]
        public async Task Act()
        {
            ArrangeDatabaseInsert(
                new ApplicationInsertResult.FailureDuplicateApplication("Test Application")
            );
            ArrangeReferenceCheck(new ApiClientUuidReferenceResult.Referenced());

            await ActInsertAsync();
        }

        [Test]
        public async Task It_keeps_the_duplicate_name_validation_response()
        {
            _insertResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            _insertResponse.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
            JsonNode actualResponse = JsonNode.Parse(await _insertResponse.Content.ReadAsStringAsync())!;
            string correlationId = actualResponse["correlationId"]!.GetValue<string>();
            JsonNode expectedResponse = JsonNode.Parse(
                """
                {
                  "detail": "Data validation failed. See 'validationErrors' for details.",
                  "type": "urn:ed-fi:management-api:bad-request:data",
                  "title": "Data Validation Failed",
                  "status": 400,
                  "correlationId": "{correlationId}",
                  "validationErrors": {
                    "ApplicationName": [
                      "Application 'Test Application' already exists for vendor."
                    ]
                  },
                  "errors": []
                }
                """.Replace("{correlationId}", correlationId)
            )!;
            JsonNode.DeepEquals(actualResponse, expectedResponse).Should().Be(true);
        }

        [Test]
        public void It_checks_the_created_uuid_for_references() =>
            AssertReferenceCheckedOnceForTheCreatedUuid();

        [Test]
        public void It_deletes_nothing() => AssertNoProviderCleanup();

        [Test]
        public void It_reports_the_client_left_in_place() =>
            AssertLeftInPlace(nameof(ApiClientUuidReferenceResult.Referenced));
    }

    /// <summary>
    /// The same name conflict with a UUID no row holds: the reference check clears the deletion and
    /// the provisioned client is removed exactly once.
    /// </summary>
    [TestFixture]
    public class Given_an_application_insert_with_a_duplicate_name_whose_uuid_is_unreferenced
        : IdentifierConflictInsertTestBase
    {
        [SetUp]
        public async Task Act()
        {
            ArrangeDatabaseInsert(
                new ApplicationInsertResult.FailureDuplicateApplication("Test Application")
            );
            ArrangeReferenceCheck(new ApiClientUuidReferenceResult.None());

            await ActInsertAsync();
        }

        [Test]
        public void It_keeps_the_duplicate_name_validation_failure() =>
            _insertResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        [Test]
        public void It_checks_the_created_uuid_for_references_before_deleting() =>
            AssertReferenceCheckedOnceForTheCreatedUuid();

        [Test]
        public void It_deletes_the_provisioned_client_exactly_once() =>
            _deletedClientUuids.Should().Equal(_createdClientUuid.ToString());

        [Test]
        public void It_logs_nothing_at_error() => LoggedAt(LogLevel.Error).Should().BeEmpty();
    }

    /// <summary>
    /// The vendor was not found, so no row was written and no constraint judged the reported UUID.
    /// Another row holds it, so the client stays and the unresolved reference still answers.
    /// </summary>
    [TestFixture]
    public class Given_an_application_insert_for_a_missing_vendor_whose_uuid_is_referenced
        : IdentifierConflictInsertTestBase
    {
        [SetUp]
        public async Task Act()
        {
            ArrangeDatabaseInsert(new ApplicationInsertResult.FailureVendorNotFound());
            ArrangeReferenceCheck(new ApiClientUuidReferenceResult.Referenced());

            await ActInsertAsync();
        }

        [Test]
        public void It_keeps_the_unresolved_reference_conflict() =>
            _insertResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);

        [Test]
        public void It_deletes_nothing() => AssertNoProviderCleanup();

        [Test]
        public void It_reports_the_client_left_in_place() =>
            AssertLeftInPlace(nameof(ApiClientUuidReferenceResult.Referenced));
    }

    /// <summary>
    /// An unclassified insert failure says nothing about the reported UUID. Another row holds it,
    /// so the client stays and the server error still answers.
    /// </summary>
    [TestFixture]
    public class Given_an_application_insert_failing_unknown_whose_uuid_is_referenced
        : IdentifierConflictInsertTestBase
    {
        [SetUp]
        public async Task Act()
        {
            ArrangeDatabaseInsert(new ApplicationInsertResult.FailureUnknown("insert failed"));
            ArrangeReferenceCheck(new ApiClientUuidReferenceResult.Referenced());

            await ActInsertAsync();
        }

        [Test]
        public void It_keeps_the_internal_server_error() =>
            _insertResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        [Test]
        public void It_deletes_nothing() => AssertNoProviderCleanup();

        [Test]
        public void It_reports_the_client_left_in_place() =>
            AssertLeftInPlace(nameof(ApiClientUuidReferenceResult.Referenced));
    }

    /// <summary>
    /// Another row already references the UUID the provider reported for this request's client.
    /// The client is provably in use, so no reference check and no deletion are attempted.
    /// </summary>
    [TestFixture]
    public class Given_an_application_insert_whose_provider_client_is_already_associated
        : IdentifierConflictInsertTestBase
    {
        [SetUp]
        public async Task Act()
        {
            ArrangeDatabaseInsert(new ApplicationInsertResult.FailureDuplicateClientUuid());

            await ActInsertAsync();
        }

        [Test]
        public async Task It_returns_the_duplicate_uuid_conflict_contract() =>
            await AssertIdentifierConflictContract(DuplicateClientUuidDetail);

        [Test]
        public async Task It_returns_no_credentials() => await AssertNoCredentialsInResponse();

        [Test]
        public void It_deletes_nothing() => AssertNoProviderCleanup();

        [Test]
        public void It_runs_no_reference_check() =>
            A.CallTo(() => _apiClientRepository.HasApiClientUuidReference(A<Guid>.Ignored))
                .MustNotHaveHappened();

        [Test]
        public void It_reports_the_client_left_in_place_at_error()
        {
            LoggedAt(LogLevel.Error)
                .Should()
                .ContainSingle(entry =>
                    entry.Contains("already associated with another API client")
                    && entry.Contains(_createdClientUuid.ToString())
                    && entry.Contains(_createdClientId)
                    && entry.Contains("left in place")
                );
            AssertNoRawCharactersLogged();
        }
    }

    /// <summary>
    /// The identity provider refused the generated key as already registered and created nothing,
    /// so the caller is told to retry with the same 409 the database collision produces, and no
    /// row, reference check, or deletion follows. Fails against the implementation whose create
    /// switch let the variant fall through to the 500 default.
    /// </summary>
    [TestFixture]
    public class Given_an_application_insert_whose_provider_reports_the_generated_key_already_registered
        : IdentifierConflictInsertTestBase
    {
        [SetUp]
        public async Task Act()
        {
            ArrangeProviderCreate(new ClientCreateResult.FailureDuplicateClientId());

            await ActInsertAsync();
        }

        [Test]
        public async Task It_returns_the_duplicate_key_conflict_contract() =>
            await AssertIdentifierConflictContract(DuplicateClientIdDetail);

        [Test]
        public async Task It_returns_no_credentials() => await AssertNoCredentialsInResponse();

        [Test]
        public void It_persists_no_application() => AssertNoDatabaseInsert();

        [Test]
        public void It_deletes_nothing() => AssertNoProviderCleanup();

        [Test]
        public void It_runs_no_reference_check() =>
            A.CallTo(() => _apiClientRepository.HasApiClientUuidReference(A<Guid>.Ignored))
                .MustNotHaveHappened();

        [Test]
        public void It_records_the_conflict_as_a_warning_with_the_client_id()
        {
            LoggedAt(LogLevel.Warning)
                .Should()
                .ContainSingle(entry =>
                    entry.Contains("already registered at the identity provider")
                    && entry.Contains("no client was created")
                    && entry.Contains(_createdClientId)
                );
            AssertNoRawCharactersLogged();
        }

        [Test]
        public void It_logs_nothing_at_error() => LoggedAt(LogLevel.Error).Should().BeEmpty();
    }

    [TestFixture]
    public class Given_a_successful_application_insert : InsertWorkflowTestBase
    {
        [SetUp]
        public async Task Act()
        {
            ArrangeDatabaseInsert(new ApplicationInsertResult.Success(7));

            await ActInsertAsync(clientRole: "role-under-test");
        }

        [Test]
        public void It_forwards_the_configured_client_role() =>
            _forwardedClientRole.Should().Be("role-under-test");

        [Test]
        public async Task It_returns_the_created_credentials()
        {
            _insertResponse.StatusCode.Should().Be(HttpStatusCode.Created);
            JsonNode actualResponse = JsonNode.Parse(await _insertResponse.Content.ReadAsStringAsync())!;
            actualResponse["id"]!.GetValue<int>().Should().Be(7);
            actualResponse["key"]!.GetValue<string>().Should().Be(_createdClientId);
            actualResponse["secret"]!.GetValue<string>().Should().Be(_createdClientSecret);
        }

        [Test]
        public void It_reports_the_created_location() =>
            _insertResponse.Headers.Location!.ToString().Should().EndWith("/v3/applications/7");

        [Test]
        public void It_performs_no_cleanup() => AssertNoProviderCleanup();
    }

    [TestFixture]
    public class ResetCredentialTests : ApplicationModuleTests
    {
        /// <summary>
        /// Tests that verify the application-level credential reset for a single-client application.
        /// </summary>
        [SetUp]
        public void SetUp()
        {
            A.CallTo(() => _applicationRepository.GetApplicationApiClients(A<int>.Ignored))
                .Returns(new ApplicationApiClientsResult.Success([new ApiClient("1", Guid.NewGuid(), true)]));

            A.CallTo(() => _clientRepository.ResetCredentialsAsync(A<string>.Ignored))
                .Returns(new ClientResetResult.Success("NEW_SECRET"));
        }

        [Test]
        public async Task Should_successfully_reset_credentials_for_a_single_client_application()
        {
            // Arrange
            using var client = SetUpClient();

            // Act
            var resetResponse = await client.PutAsync("/v3/applications/1/reset-credential", null);

            // Assert
            resetResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            var responseBody = await resetResponse.Content.ReadAsStringAsync();
            var actualResponse = JsonNode.Parse(responseBody);
            actualResponse.Should().NotBeNull();
            actualResponse!["id"]!.GetValue<int>().Should().Be(1);
            actualResponse!["key"]!.GetValue<string>().Should().Be("1");
            actualResponse!["secret"]!.GetValue<string>().Should().Be("NEW_SECRET");
        }

        [Test]
        public async Task Should_return_not_found_when_the_application_does_not_exist()
        {
            // Arrange - a missing application has no clients and no application row
            Fake.ClearRecordedCalls(_clientRepository);
            using var client = SetUpClient();
            A.CallTo(() => _applicationRepository.GetApplicationApiClients(A<int>.Ignored))
                .Returns(new ApplicationApiClientsResult.Success([]));
            A.CallTo(() => _applicationRepository.GetApplication(A<int>.Ignored))
                .Returns(new ApplicationGetResult.FailureNotFound());

            // Act
            var resetResponse = await client.PutAsync("/v3/applications/1/reset-credential", null);

            // Assert
            resetResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
            A.CallTo(() => _clientRepository.ResetCredentialsAsync(A<string>.Ignored)).MustNotHaveHappened();
        }

        [Test]
        public async Task Should_return_internal_server_error_when_application_client_is_missing_in_identity_provider()
        {
            const string Sentinel = "SENTINEL_APPLICATION_RESET_CLIENT_MISSING_must_not_leak";
            using var client = SetUpClient();
            A.CallTo(() => _clientRepository.ResetCredentialsAsync(A<string>.Ignored))
                .Returns(new ClientResetResult.FailureClientNotFound(Sentinel));

            var resetResponse = await client.PutAsync("/v3/applications/1/reset-credential", null);

            resetResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            resetResponse.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
            string responseBody = await resetResponse.Content.ReadAsStringAsync();
            responseBody.Should().NotContain(Sentinel);
            JsonNode actualResponse = JsonNode.Parse(responseBody)!;
            JsonNode expectedResponse = JsonNode.Parse(
                """
                {
                  "detail": "",
                  "type": "urn:ed-fi:management-api:internal-server-error",
                  "title": "Internal Server Error",
                  "status": 500,
                  "correlationId": "{correlationId}",
                  "validationErrors": {},
                  "errors": []
                }
                """.Replace("{correlationId}", actualResponse["correlationId"]!.GetValue<string>())
            )!;
            JsonNode.DeepEquals(actualResponse, expectedResponse).Should().Be(true);
        }

        [Test]
        public async Task Should_return_bad_gateway_when_identity_provider_reset_fails()
        {
            using var client = SetUpClient();
            A.CallTo(() => _clientRepository.ResetCredentialsAsync(A<string>.Ignored))
                .Returns(
                    new ClientResetResult.FailureIdentityProvider(
                        new IdentityProviderError("Identity provider connection failed")
                    )
                );

            var resetResponse = await client.PutAsync("/v3/applications/1/reset-credential", null);

            resetResponse.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        }
    }

    [TestFixture]
    public class Given_Invalid_PagingQuery : ApplicationModuleTests
    {
        [SetUp]
        public void SetUp()
        {
            A.CallTo(() => _applicationRepository.QueryApplication(A<ApplicationQuery>.Ignored))
                .Returns(new ApplicationQueryResult.Success([]));
        }

        /// <summary>
        /// Asserts the full urn:ed-fi:management-api:bad-request:parameter contract (real pipeline, not a
        /// unit-level construction) for both binding-level (offset/limit non-numeric) and
        /// validation-rule-level (limit=0, negative offset, invalid orderBy) query-parameter failures,
        /// including a fixed <c>detail</c> and exactly one <c>errors</c> entry. <paramref name="exactMatch"/>
        /// is false only for the orderBy cases, whose message includes the allowed-fields list and so is
        /// matched as a substring rather than depending on set-enumeration order.
        /// </summary>
        private static async Task AssertParameterValidationContract(
            HttpResponseMessage response,
            string expectedError,
            bool exactMatch = true
        )
        {
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

            string content = await response.Content.ReadAsStringAsync();
            JsonNode body = JsonNode.Parse(content)!;
            body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:management-api:bad-request:parameter");
            body["title"]!.GetValue<string>().Should().Be("Parameter Validation Failed");
            body["detail"]!
                .GetValue<string>()
                .Should()
                .Be("Parameter validation failed. See 'errors' for details.");
            body["status"]!.GetValue<int>().Should().Be(400);
            body["correlationId"]!.GetValue<string>().Should().NotBeNullOrEmpty();
            body["validationErrors"]!.AsObject().Count.Should().Be(0);

            var errors = body["errors"]!.AsArray().Select(node => node!.GetValue<string>()).ToList();
            errors.Should().HaveCount(1);
            if (exactMatch)
            {
                errors[0].Should().Be(expectedError);
            }
            else
            {
                errors[0].Should().Contain(expectedError);
            }
        }

        [Test]
        public async Task Should_return_400_when_orderBy_is_invalid()
        {
            using var client = SetUpClient();
            var response = await client.GetAsync("/v3/applications?orderBy=invalidField");
            await AssertParameterValidationContract(
                response,
                "'orderBy' is not a valid field.",
                exactMatch: false
            );
        }

        [Test]
        public async Task Should_not_reflect_an_injection_style_orderBy_value_in_the_response()
        {
            using var client = SetUpClient();
            const string sentinel = "<script>SENTINEL_ORDERBY_APP_9f2c</script>";
            var response = await client.GetAsync(
                $"/v3/applications?orderBy={Uri.EscapeDataString(sentinel)}"
            );

            await AssertParameterValidationContract(
                response,
                "'orderBy' is not a valid field.",
                exactMatch: false
            );
            string content = await response.Content.ReadAsStringAsync();
            content.Should().NotContain(sentinel);
        }

        [Test]
        public async Task Should_return_400_when_direction_is_invalid()
        {
            using var client = SetUpClient();
            var response = await client.GetAsync("/v3/applications?orderBy=id&direction=SIDEWAYS");
            await AssertParameterValidationContract(
                response,
                "The direction query parameter must be one of: asc, ascending, desc, descending."
            );
        }

        [Test]
        public async Task Should_return_400_when_offset_is_negative()
        {
            using var client = SetUpClient();
            var response = await client.GetAsync("/v3/applications?offset=-1");
            await AssertParameterValidationContract(response, "'offset' must be greater than or equal to 0.");
        }

        [Test]
        public async Task Should_return_400_when_limit_is_zero()
        {
            using var client = SetUpClient();
            var response = await client.GetAsync("/v3/applications?limit=0");
            await AssertParameterValidationContract(response, "'limit' must be greater than 0.");
        }

        [Test]
        public async Task Should_return_200_with_valid_orderBy_and_direction()
        {
            using var client = SetUpClient();
            var response = await client.GetAsync("/v3/applications?orderBy=applicationName&direction=ASC");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Test]
        public async Task Should_return_200_when_ids_is_valid_list()
        {
            using var client = SetUpClient();
            var response = await client.GetAsync("/v3/applications?ids=1,2,3");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Test]
        public async Task Should_return_200_when_ids_has_whitespace()
        {
            using var client = SetUpClient();
            var response = await client.GetAsync("/v3/applications?ids=1%2C+2+%2C+3");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Test]
        public async Task Should_return_400_when_ids_contains_non_integer()
        {
            using var client = SetUpClient();
            var response = await client.GetAsync("/v3/applications?ids=1%2Cabc%2C3");
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            var content = await response.Content.ReadAsStringAsync();
            content.Should().Contain("The 'ids' query parameter must be a comma-separated list of integers.");
        }

        [Test]
        public async Task Should_return_200_when_ids_is_single_value()
        {
            using var client = SetUpClient();
            var response = await client.GetAsync("/v3/applications?ids=42");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Test]
        public async Task Should_return_200_when_filter_applicationName_is_provided()
        {
            using var client = SetUpClient();
            var response = await client.GetAsync("/v3/applications?applicationName=MyApp");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Test]
        public async Task Should_return_400_when_id_and_ids_are_used_together()
        {
            using var client = SetUpClient();
            var response = await client.GetAsync("/v3/applications?id=5&ids=1,2,3");
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            var content = await response.Content.ReadAsStringAsync();
            content.Should().Contain("'id' and 'ids' cannot be used together.");
        }

        [Test]
        public async Task Should_return_400_when_offset_is_non_numeric()
        {
            using var client = SetUpClient();
            var response = await client.GetAsync("/v3/applications?offset=abc");
            await AssertParameterValidationContract(
                response,
                "The request contains one or more invalid parameters."
            );
        }

        [Test]
        public async Task Should_return_400_when_limit_is_non_numeric()
        {
            using var client = SetUpClient();
            var response = await client.GetAsync("/v3/applications?limit=xyz");
            await AssertParameterValidationContract(
                response,
                "The request contains one or more invalid parameters."
            );
        }

        [Test]
        public async Task Should_return_200_when_orderBy_omitted_with_direction()
        {
            using var client = SetUpClient();
            var response = await client.GetAsync("/v3/applications?direction=asc");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }

    [TestFixture]
    public class Given_GetApplications_WithEnabledFlag : ApplicationModuleTests
    {
        [Test]
        public async Task It_returns_enabled_true_when_application_is_enabled()
        {
            // Arrange
            A.CallTo(() => _applicationRepository.QueryApplication(A<ApplicationQuery>.Ignored))
                .Returns(
                    new ApplicationQueryResult.Success([
                        new ApplicationResponse()
                        {
                            Id = 1,
                            ApplicationName = "Test Application",
                            ClaimSetName = "ClaimSet",
                            VendorId = 1,
                            EducationOrganizationIds = [1],
                            DataStoreIds = [],
                            ProfileIds = [],
                            Enabled = true,
                        },
                    ])
                );

            using var client = SetUpClient();

            // Act
            var response = await client.GetAsync("/v3/applications?offset=0&limit=25");
            var body = await response.Content.ReadAsStringAsync();
            var json = JsonNode.Parse(body)!.AsArray();

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            json[0]!["enabled"]!.GetValue<bool>().Should().BeTrue();
        }

        [Test]
        public async Task It_returns_enabled_false_when_application_is_disabled()
        {
            // Arrange
            A.CallTo(() => _applicationRepository.QueryApplication(A<ApplicationQuery>.Ignored))
                .Returns(
                    new ApplicationQueryResult.Success([
                        new ApplicationResponse()
                        {
                            Id = 2,
                            ApplicationName = "Disabled Application",
                            ClaimSetName = "ClaimSet",
                            VendorId = 1,
                            EducationOrganizationIds = [],
                            DataStoreIds = [],
                            ProfileIds = [],
                            Enabled = false,
                        },
                    ])
                );

            using var client = SetUpClient();

            // Act
            var response = await client.GetAsync("/v3/applications?offset=0&limit=25");
            var body = await response.Content.ReadAsStringAsync();
            var json = JsonNode.Parse(body)!.AsArray();

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            json[0]!["enabled"]!.GetValue<bool>().Should().BeFalse();
        }

        [Test]
        public async Task It_returns_enabled_false_on_get_by_id_when_application_is_disabled()
        {
            // Arrange
            A.CallTo(() => _applicationRepository.GetApplication(A<int>.Ignored))
                .Returns(
                    new ApplicationGetResult.Success(
                        new ApplicationResponse()
                        {
                            Id = 3,
                            ApplicationName = "Disabled Application",
                            ClaimSetName = "ClaimSet",
                            VendorId = 1,
                            EducationOrganizationIds = [],
                            DataStoreIds = [],
                            ProfileIds = [],
                            Enabled = false,
                        }
                    )
                );

            using var client = SetUpClient();

            // Act
            var response = await client.GetAsync("/v3/applications/3");
            var body = await response.Content.ReadAsStringAsync();
            var json = JsonNode.Parse(body)!;

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            json["enabled"]!.GetValue<bool>().Should().BeFalse();
        }
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

    private sealed class RecordingLockManager(Action? onAcquire = null) : IApplicationLockManager
    {
        public RecordingLockHandle Handle { get; } = new();
        public List<int> AcquiredApplicationIds { get; } = [];

        public Task<ApplicationLockResult> AcquireAsync(
            int applicationId,
            CancellationToken cancellationToken
        ) => AcquireAllAsync([applicationId], cancellationToken);

        public Task<ApplicationLockResult> AcquireAllAsync(
            IReadOnlyCollection<int> applicationIds,
            CancellationToken cancellationToken
        )
        {
            AcquiredApplicationIds.AddRange(applicationIds);
            onAcquire?.Invoke();
            return Task.FromResult<ApplicationLockResult>(new ApplicationLockResult.Acquired(Handle));
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
              "type": "urn:ed-fi:management-api:conflict",
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
    public class Given_an_application_delete_when_the_aggregate_lock_times_out : ApplicationModuleTests
    {
        private List<string> _dependencyCalls = null!;
        private HttpResponseMessage _deleteResponse = null!;

        [SetUp]
        public async Task Act()
        {
            _dependencyCalls = [];
            A.CallTo(() => _lockManager.AcquireAsync(A<int>.Ignored, A<CancellationToken>.Ignored))
                .Returns(new ApplicationLockResult.FailureTimeout());
            A.CallTo(_applicationRepository).Invokes(call => _dependencyCalls.Add(call.Method.Name));
            A.CallTo(_clientRepository).Invokes(call => _dependencyCalls.Add(call.Method.Name));

            using var client = SetUpClient();
            _deleteResponse = await client.DeleteAsync("/v3/applications/1");
        }

        [TearDown]
        public void TearDownResponse() => _deleteResponse?.Dispose();

        [Test]
        public async Task It_returns_the_retriable_conflict_contract() =>
            await AssertLockConflictContract(_deleteResponse);

        [Test]
        public void It_calls_no_repository_or_identity_provider_dependency() =>
            _dependencyCalls.Should().BeEmpty();
    }

    [TestFixture]
    public class Given_an_application_delete_in_progress : ApplicationModuleTests
    {
        private RecordingLockManager _recordingLockManager = null!;
        private bool _handleDisposedDuringDatabaseDelete;
        private HttpResponseMessage _deleteResponse = null!;

        [SetUp]
        public async Task Act()
        {
            _recordingLockManager = new RecordingLockManager();
            _lockManager = _recordingLockManager;

            A.CallTo(() => _applicationRepository.GetApplicationApiClients(A<int>.Ignored))
                .Returns(new ApplicationApiClientsResult.Success([]));

            var deleteEntered = new TaskCompletionSource();
            var releaseDelete = new TaskCompletionSource();
            A.CallTo(() => _applicationRepository.DeleteApplication(A<int>.Ignored))
                .ReturnsLazily(async _ =>
                {
                    deleteEntered.TrySetResult();
                    await releaseDelete.Task;
                    return (ApplicationDeleteResult)new ApplicationDeleteResult.Success();
                });

            using var client = SetUpClient();
            Task<HttpResponseMessage> deleting = client.DeleteAsync("/v3/applications/1");

            await deleteEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            _handleDisposedDuringDatabaseDelete = _recordingLockManager.Handle.Disposed;

            releaseDelete.SetResult();
            _deleteResponse = await deleting;
        }

        [TearDown]
        public void TearDownResponse() => _deleteResponse?.Dispose();

        [Test]
        public void It_returns_no_content() =>
            _deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        [Test]
        public void It_holds_the_lock_until_the_database_delete_completes()
        {
            _handleDisposedDuringDatabaseDelete.Should().BeFalse();
            _recordingLockManager.Handle.Disposed.Should().BeTrue();
        }
    }

    [TestFixture]
    public class Given_an_application_reset_credential_when_the_aggregate_lock_times_out
        : ApplicationModuleTests
    {
        private List<string> _dependencyCalls = null!;
        private HttpResponseMessage _resetResponse = null!;

        [SetUp]
        public async Task Act()
        {
            _dependencyCalls = [];
            A.CallTo(() => _lockManager.AcquireAsync(A<int>.Ignored, A<CancellationToken>.Ignored))
                .Returns(new ApplicationLockResult.FailureTimeout());
            A.CallTo(_applicationRepository).Invokes(call => _dependencyCalls.Add(call.Method.Name));
            A.CallTo(_clientRepository).Invokes(call => _dependencyCalls.Add(call.Method.Name));

            using var client = SetUpClient();
            _resetResponse = await client.PutAsync("/v3/applications/1/reset-credential", null);
        }

        [TearDown]
        public void TearDownResponse() => _resetResponse?.Dispose();

        [Test]
        public async Task It_returns_the_retriable_conflict_contract() =>
            await AssertLockConflictContract(_resetResponse);

        [Test]
        public void It_calls_no_repository_or_identity_provider_dependency() =>
            _dependencyCalls.Should().BeEmpty();
    }

    [TestFixture]
    public class Given_an_application_credential_reset_for_a_multi_client_application : ApplicationModuleTests
    {
        private HttpResponseMessage _resetResponse = null!;

        [SetUp]
        public async Task Act()
        {
            A.CallTo(() => _applicationRepository.GetApplicationApiClients(A<int>.Ignored))
                .Returns(
                    new ApplicationApiClientsResult.Success([
                        new ApiClient("firstClientId", Guid.NewGuid(), true),
                        new ApiClient("secondClientId", Guid.NewGuid(), true),
                    ])
                );

            // Arranged to succeed so that a handler resetting one of the clients would answer 200.
            A.CallTo(() => _clientRepository.ResetCredentialsAsync(A<string>.Ignored))
                .Returns(new ClientResetResult.Success("NEW_SECRET"));

            using var client = SetUpClient();
            _resetResponse = await client.PutAsync(
                "/v3/applications/1/reset-credential",
                new StringContent("{}", Encoding.UTF8, "application/json")
            );
        }

        [TearDown]
        public void TearDownResponse() => _resetResponse?.Dispose();

        [Test]
        public async Task It_returns_409_with_the_conflict_problem_details()
        {
            _resetResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
            _resetResponse.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
            string responseBody = await _resetResponse.Content.ReadAsStringAsync();
            JsonNode actualResponse = JsonNode.Parse(responseBody)!;
            JsonNode expectedResponse = JsonNode.Parse(
                """
                {
                  "detail": "The application has more than one API client. Reset a specific client's credentials with PUT /v3/apiClients/{id}/reset-credential.",
                  "type": "urn:ed-fi:management-api:conflict",
                  "title": "Conflict",
                  "status": 409,
                  "correlationId": "{correlationId}",
                  "validationErrors": {},
                  "errors": []
                }
                """.Replace("{correlationId}", actualResponse["correlationId"]!.GetValue<string>())
            )!;
            JsonNode.DeepEquals(actualResponse, expectedResponse).Should().Be(true);
        }

        [Test]
        public void It_resets_no_client() =>
            A.CallTo(() => _clientRepository.ResetCredentialsAsync(A<string>.Ignored)).MustNotHaveHappened();
    }

    public abstract class CreateDefaultCredentialsTestBase : ApplicationModuleTests
    {
        protected readonly List<ApiClientCommand> InsertedClientCommands = [];
        protected readonly List<string> ProvisionedSecrets = [];
        protected readonly List<ApplicationInsertCommand> InsertedCommands = [];

        // NUnit reuses the fixture instance across its tests, so each test starts from no
        // recorded calls; base-class SetUp runs before the derived fixture's arrange and act.
        [SetUp]
        public void ClearRecordedRequests()
        {
            InsertedClientCommands.Clear();
            InsertedCommands.Clear();
            ProvisionedSecrets.Clear();
            Fake.ClearRecordedCalls(_applicationRepository);
            Fake.ClearRecordedCalls(_clientRepository);
        }

        protected void ArrangeVendorAndInsert(ApplicationInsertResult insertResult)
        {
            A.CallTo(() => _vendorRepository.GetVendor(A<int>.Ignored))
                .Returns(
                    new VendorGetResult.Success(
                        new VendorResponse
                        {
                            Company = "any",
                            ContactName = "any",
                            ContactEmailAddress = "any",
                            NamespacePrefixes = "any",
                        }
                    )
                );

            A.CallTo(() =>
                    _applicationRepository.InsertApplication(
                        A<ApplicationInsertCommand>.Ignored,
                        A<ApiClientCommand>.Ignored
                    )
                )
                .Invokes(
                    (ApplicationInsertCommand command, ApiClientCommand clientCommand) =>
                    {
                        InsertedCommands.Add(command);
                        InsertedClientCommands.Add(clientCommand);
                    }
                )
                .Returns(insertResult);

            A.CallTo(() =>
                    _clientRepository.CreateClientAsync(
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<int[]?>.Ignored,
                        A<bool>.Ignored
                    )
                )
                .Invokes(call => ProvisionedSecrets.Add(call.GetArgument<string>(1)!))
                .Returns(new ClientCreateResult.Success(Guid.NewGuid()));
        }

        protected async Task<HttpResponseMessage> PostApplicationAsync(string additionalFields)
        {
            using var client = SetUpClient();
            return await client.PostAsync(
                "/v3/applications",
                new StringContent(
                    $$"""
                    {
                      {{additionalFields}}
                      "applicationName": "Application 11",
                      "claimSetName": "Test",
                      "vendorId": 1,
                      "educationOrganizationIds": [1, 2],
                      "profileIds": [3]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );
        }

        protected void AssertNoProviderClientWasCreatedOrDeleted()
        {
            A.CallTo(() =>
                    _clientRepository.CreateClientAsync(
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<int[]?>.Ignored,
                        A<bool>.Ignored
                    )
                )
                .MustNotHaveHappened();
            A.CallTo(() => _clientRepository.DeleteClientAsync(A<string>.Ignored)).MustNotHaveHappened();
        }

        protected void AssertProviderClientWasCreatedOnce() =>
            A.CallTo(() =>
                    _clientRepository.CreateClientAsync(
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<int[]?>.Ignored,
                        A<bool>.Ignored
                    )
                )
                .MustHaveHappenedOnceExactly();
    }

    [TestFixture]
    public class Given_an_application_created_without_default_credentials : CreateDefaultCredentialsTestBase
    {
        private HttpResponseMessage _response = null!;
        private JsonObject _body = null!;

        [SetUp]
        public async Task Setup()
        {
            ArrangeVendorAndInsert(new ApplicationInsertResult.Success(5));
            _response = await PostApplicationAsync("\"createDefaultCredentials\": false,");
            _body = JsonNode.Parse(await _response.Content.ReadAsStringAsync())!.AsObject();
        }

        [Test]
        public void It_returns_created() => _response.StatusCode.Should().Be(HttpStatusCode.Created);

        [Test]
        public void It_returns_the_application_id() => _body["id"]!.GetValue<int>().Should().Be(5);

        [Test]
        public void It_returns_an_explicit_null_key()
        {
            _body.ContainsKey("key").Should().BeTrue();
            _body["key"].Should().BeNull();
        }

        [Test]
        public void It_returns_an_explicit_null_secret()
        {
            _body.ContainsKey("secret").Should().BeTrue();
            _body["secret"].Should().BeNull();
        }

        [Test]
        public void It_creates_no_identity_provider_client() => AssertNoProviderClientWasCreatedOrDeleted();

        [Test]
        public void It_inserts_the_application_without_default_credentials() =>
            InsertedCommands.Should().ContainSingle().Which.CreateDefaultCredentials.Should().BeFalse();

        [Test]
        public void It_inserts_the_requested_education_organizations_and_profiles()
        {
            InsertedCommands.Should().ContainSingle();
            InsertedCommands[0].EducationOrganizationIds.Should().Equal(1, 2);
            InsertedCommands[0].ProfileIds.Should().Equal(3);
        }
    }

    [TestFixture("")]
    [TestFixture("\"createDefaultCredentials\": true,")]
    [TestFixture("\"enabled\": false,")]
    public class Given_an_application_created_with_default_credentials(string additionalFields)
        : CreateDefaultCredentialsTestBase
    {
        private HttpResponseMessage _response = null!;
        private JsonObject _body = null!;

        [SetUp]
        public async Task Setup()
        {
            ArrangeVendorAndInsert(new ApplicationInsertResult.Success(5));
            _response = await PostApplicationAsync(additionalFields);
            _body = JsonNode.Parse(await _response.Content.ReadAsStringAsync())!.AsObject();
        }

        [Test]
        public void It_returns_created() => _response.StatusCode.Should().Be(HttpStatusCode.Created);

        [Test]
        public void It_creates_one_identity_provider_client() => AssertProviderClientWasCreatedOnce();

        [Test]
        public void It_inserts_the_application_with_default_credentials() =>
            InsertedCommands.Should().ContainSingle().Which.CreateDefaultCredentials.Should().BeTrue();

        [Test]
        public void It_returns_the_stored_client_key() =>
            _body["key"]!
                .GetValue<string>()
                .Should()
                .Be(InsertedClientCommands.Should().ContainSingle().Subject.ClientId);

        [Test]
        public void It_returns_the_provisioned_secret() =>
            _body["secret"]!
                .GetValue<string>()
                .Should()
                .Be(ProvisionedSecrets.Should().ContainSingle().Subject);
    }

    [TestFixture("\"enabled\": false,", "Enabled")]
    [TestFixture("\"enabled\": true,", "Enabled")]
    [TestFixture("\"enabled\": null,", "Enabled")]
    [TestFixture("\"Enabled\": true,", "Enabled")]
    [TestFixture("\"dataStoreIds\": [],", "DataStoreIds")]
    [TestFixture("\"dataStoreIds\": null,", "DataStoreIds")]
    [TestFixture("\"dataStoreIds\": [1],", "DataStoreIds")]
    [TestFixture("\"enabled\": true, \"dataStoreIds\": [1],", "Enabled,DataStoreIds")]
    public class Given_an_application_created_without_default_credentials_and_default_client_fields(
        string defaultClientFields,
        string expectedFields
    ) : CreateDefaultCredentialsTestBase
    {
        private HttpResponseMessage _response = null!;
        private JsonObject _body = null!;

        [SetUp]
        public async Task Setup()
        {
            ArrangeVendorAndInsert(new ApplicationInsertResult.Success(5));
            _response = await PostApplicationAsync(
                $"\"createDefaultCredentials\": false, {defaultClientFields}"
            );
            _body = JsonNode.Parse(await _response.Content.ReadAsStringAsync())!.AsObject();
        }

        [Test]
        public void It_returns_bad_request() => _response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        [Test]
        public void It_returns_a_data_validation_problem() =>
            _body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:management-api:bad-request:data");

        [Test]
        public void It_reports_each_supplied_default_client_field() =>
            _body["validationErrors"]!
                .AsObject()
                .Select(e => e.Key)
                .Should()
                .BeEquivalentTo(expectedFields.Split(','));

        [Test]
        public void It_writes_nothing() =>
            A.CallTo(() =>
                    _applicationRepository.InsertApplication(
                        A<ApplicationInsertCommand>.Ignored,
                        A<ApiClientCommand>.Ignored
                    )
                )
                .MustNotHaveHappened();

        [Test]
        public void It_creates_no_identity_provider_client() => AssertNoProviderClientWasCreatedOrDeleted();
    }

    [TestFixture]
    public class Given_an_application_created_without_default_credentials_for_a_missing_vendor
        : CreateDefaultCredentialsTestBase
    {
        private HttpResponseMessage _response = null!;

        [SetUp]
        public async Task Setup()
        {
            ArrangeVendorAndInsert(new ApplicationInsertResult.Success(5));
            A.CallTo(() => _vendorRepository.GetVendor(A<int>.Ignored))
                .Returns(new VendorGetResult.FailureNotFound());
            _response = await PostApplicationAsync("\"createDefaultCredentials\": false,");
        }

        [Test]
        public void It_returns_conflict() => _response.StatusCode.Should().Be(HttpStatusCode.Conflict);

        [Test]
        public void It_writes_nothing() =>
            A.CallTo(() =>
                    _applicationRepository.InsertApplication(
                        A<ApplicationInsertCommand>.Ignored,
                        A<ApiClientCommand>.Ignored
                    )
                )
                .MustNotHaveHappened();

        [Test]
        public void It_creates_no_identity_provider_client() => AssertNoProviderClientWasCreatedOrDeleted();
    }

    [TestFixture]
    public class Given_an_application_created_without_default_credentials_whose_insert_fails
        : CreateDefaultCredentialsTestBase
    {
        private HttpResponseMessage _profileResponse = null!;
        private HttpResponseMessage _vendorResponse = null!;
        private HttpResponseMessage _duplicateResponse = null!;
        private HttpResponseMessage _unknownResponse = null!;

        [SetUp]
        public async Task Setup()
        {
            ArrangeVendorAndInsert(new ApplicationInsertResult.FailureProfileNotFound());
            const string Flag = "\"createDefaultCredentials\": false,";
            _profileResponse = await PostApplicationAsync(Flag);

            ArrangeVendorAndInsert(new ApplicationInsertResult.FailureVendorNotFound());
            _vendorResponse = await PostApplicationAsync(Flag);

            ArrangeVendorAndInsert(new ApplicationInsertResult.FailureDuplicateApplication("Application 11"));
            _duplicateResponse = await PostApplicationAsync(Flag);

            ArrangeVendorAndInsert(new ApplicationInsertResult.FailureUnknown("boom"));
            _unknownResponse = await PostApplicationAsync(Flag);
        }

        [Test]
        public void It_returns_conflict_for_a_missing_profile() =>
            _profileResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);

        [Test]
        public void It_returns_conflict_for_a_vendor_removed_before_the_insert() =>
            _vendorResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);

        [Test]
        public async Task It_returns_the_duplicate_name_validation_failure()
        {
            _duplicateResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            var body = JsonNode.Parse(await _duplicateResponse.Content.ReadAsStringAsync())!;
            body["validationErrors"]!["ApplicationName"].Should().NotBeNull();
        }

        [Test]
        public void It_returns_internal_server_error_for_an_unknown_failure() =>
            _unknownResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        [Test]
        public void It_never_calls_the_identity_provider() => AssertNoProviderClientWasCreatedOrDeleted();
    }

    [TestFixture]
    public class Given_a_credential_reset_for_an_application_with_no_api_clients : ApplicationModuleTests
    {
        private HttpResponseMessage _response = null!;
        private JsonNode _body = null!;

        [SetUp]
        public async Task Setup()
        {
            A.CallTo(() => _applicationRepository.GetApplicationApiClients(7))
                .Returns(new ApplicationApiClientsResult.Success([]));
            A.CallTo(() => _applicationRepository.GetApplication(7))
                .Returns(
                    new ApplicationGetResult.Success(
                        new ApplicationResponse
                        {
                            Id = 7,
                            ApplicationName = "Zero Client Application",
                            ClaimSetName = "ClaimSet",
                            VendorId = 1,
                        }
                    )
                );

            using var client = SetUpClient();
            _response = await client.PutAsync("/v3/applications/7/reset-credential", null);
            _body = JsonNode.Parse(await _response.Content.ReadAsStringAsync())!;
        }

        [Test]
        public void It_returns_conflict() => _response.StatusCode.Should().Be(HttpStatusCode.Conflict);

        [Test]
        public void It_returns_the_conflict_problem_type() =>
            _body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:management-api:conflict");

        [Test]
        public void It_explains_that_the_application_has_no_api_clients() =>
            _body["detail"]!
                .GetValue<string>()
                .Should()
                .Be("The application has no API clients. Create one with POST /v3/apiClients.");

        [Test]
        public void It_resets_no_identity_provider_client() =>
            A.CallTo(() => _clientRepository.ResetCredentialsAsync(A<string>.Ignored)).MustNotHaveHappened();
    }

    [TestFixture]
    public class Given_a_credential_reset_for_an_application_that_cannot_be_read : ApplicationModuleTests
    {
        private HttpResponseMessage _response = null!;

        [SetUp]
        public async Task Setup()
        {
            A.CallTo(() => _applicationRepository.GetApplicationApiClients(7))
                .Returns(new ApplicationApiClientsResult.Success([]));
            A.CallTo(() => _applicationRepository.GetApplication(7))
                .Returns(new ApplicationGetResult.FailureUnknown("read failed"));

            using var client = SetUpClient();
            _response = await client.PutAsync("/v3/applications/7/reset-credential", null);
        }

        [Test]
        public void It_returns_internal_server_error() =>
            _response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        [Test]
        public void It_resets_no_identity_provider_client() =>
            A.CallTo(() => _clientRepository.ResetCredentialsAsync(A<string>.Ignored)).MustNotHaveHappened();
    }
}
