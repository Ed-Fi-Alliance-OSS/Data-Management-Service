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
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Modules;

[TestFixture]
public class ApplicationModuleTests
{
    private readonly IApplicationRepository _applicationRepository = A.Fake<IApplicationRepository>();
    private readonly IApiClientRepository _apiClientRepository = A.Fake<IApiClientRepository>();
    private IApplicationLockManager _lockManager = A.Fake<IApplicationLockManager>();
    private readonly IIdentityProviderRepository _clientRepository = A.Fake<IIdentityProviderRepository>();
    private readonly IDataStoreRepository _dataStoreRepository = A.Fake<IDataStoreRepository>();
    private readonly IVendorRepository _vendorRepository = A.Fake<IVendorRepository>();
    private readonly IProfileRepository _profileRepository = A.Fake<IProfileRepository>();
    private readonly WebApplicationFactoryTracker<Program> _factoryTracker = new();

    public ApplicationModuleTests()
    {
        A.CallTo(() => _lockManager.AcquireAsync(A<int>.Ignored, A<CancellationToken>.Ignored))
            .ReturnsLazily(_ =>
                Task.FromResult<ApplicationLockResult>(
                    new ApplicationLockResult.Acquired(A.Fake<IAsyncDisposable>())
                )
            );

        A.CallTo(() => _applicationRepository.GetApplicationUpdateState(A<int>.Ignored, A<string>.Ignored))
            .Returns(
                new ApplicationUpdateStateResult.Success(
                    new ApplicationUpdateState(
                        "Original Application",
                        7,
                        "OriginalClaim",
                        [9],
                        [],
                        "clientId",
                        Guid.NewGuid(),
                        true,
                        [1]
                    )
                )
            );

        A.CallTo(() =>
                _applicationRepository.SyncApplicationApiClientUuid(
                    A<int>.Ignored,
                    A<string>.Ignored,
                    A<Guid>.Ignored,
                    A<Guid>.Ignored
                )
            )
            .Returns(new ApiClientUuidSyncResult.Success());

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

    private HttpClient SetUpClient(int? clientSecretMinimumLength = null)
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(collection =>
            {
                // Use the new test authentication extension that mimics production setup
                collection.AddTestAuthentication();
                collection.Configure<AppSettings>(options =>
                {
                    options.EnableApplicationResetEndpoint = true;
                });
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

            A.CallTo(() =>
                    _applicationRepository.UpdateApplication(
                        A<ApplicationUpdateCommand>.Ignored,
                        A<ApiClientCommand>.Ignored
                    )
                )
                .Returns(new ApplicationUpdateResult.Success());

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
                  "type": "urn:ed-fi:api:bad-request:data",
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

            string addResponseContentForInvalidClaimSetName =
                await addResponseForInvalidClaimSetName.Content.ReadAsStringAsync();
            var actualResponseForInvalidClaimSetName = JsonNode.Parse(
                addResponseContentForInvalidClaimSetName
            );
            var expectedResponseForInvalidClaimSetName = JsonNode.Parse(
                """
                {
                  "detail": "Data validation failed. See 'validationErrors' for details.",
                  "type": "urn:ed-fi:api:bad-request:data",
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
                  "type": "urn:ed-fi:api:bad-request:data",
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

    [TestFixture]
    public class FailureNotFoundTest : ApplicationModuleTests
    {
        [SetUp]
        public void SetUp()
        {
            A.CallTo(() => _vendorRepository.InsertVendor(A<VendorInsertCommand>.Ignored))
                .Returns(new VendorInsertResult.Success(1, IsNewVendor: true));

            A.CallTo(() => _applicationRepository.GetApplication(A<int>.Ignored))
                .Returns(new ApplicationGetResult.FailureNotFound());

            A.CallTo(() =>
                    _applicationRepository.UpdateApplication(
                        A<ApplicationUpdateCommand>.Ignored,
                        A<ApiClientCommand>.Ignored
                    )
                )
                .Returns(new ApplicationUpdateResult.FailureNotExists());

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

            A.CallTo(() =>
                    _applicationRepository.UpdateApplication(
                        A<ApplicationUpdateCommand>.Ignored,
                        A<ApiClientCommand>.Ignored
                    )
                )
                .Returns(new ApplicationUpdateResult.FailureUnknown(""));

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

            A.CallTo(() =>
                    _applicationRepository.UpdateApplication(
                        A<ApplicationUpdateCommand>.Ignored,
                        A<ApiClientCommand>.Ignored
                    )
                )
                .Returns(new ApplicationUpdateResult());

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

            A.CallTo(() =>
                    _applicationRepository.UpdateApplication(
                        A<ApplicationUpdateCommand>.Ignored,
                        A<ApiClientCommand>.Ignored
                    )
                )
                .Returns(new ApplicationUpdateResult.FailureVendorNotFound());

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
                  "type": "urn:ed-fi:api:conflict:unresolved-reference",
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
            // Arrange
            var originalUuid = Guid.NewGuid();
            A.CallTo(() =>
                    _applicationRepository.GetApplicationUpdateState(A<int>.Ignored, A<string>.Ignored)
                )
                .Returns(
                    new ApplicationUpdateStateResult.Success(
                        new ApplicationUpdateState(
                            "Original Application",
                            7,
                            "OriginalClaim",
                            [9],
                            [],
                            "clientId",
                            originalUuid,
                            true,
                            [1]
                        )
                    )
                );

            var updatedUuid = Guid.NewGuid();
            var rollbackUuid = Guid.NewGuid();
            List<string> clientUpdateNames = [];
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
                .Invokes(call => clientUpdateNames.Add(call.GetArgument<string>(1)!))
                .ReturnsNextFromSequence(
                    new ClientUpdateResult.Success(updatedUuid),
                    new ClientUpdateResult.Success(rollbackUuid)
                );

            List<ApplicationUpdateCommand> updateCommands = [];
            List<ApiClientCommand> apiClientCommands = [];
            A.CallTo(() =>
                    _applicationRepository.UpdateApplication(
                        A<ApplicationUpdateCommand>.Ignored,
                        A<ApiClientCommand>.Ignored
                    )
                )
                .Invokes(call =>
                {
                    updateCommands.Add(call.GetArgument<ApplicationUpdateCommand>(0)!);
                    apiClientCommands.Add(call.GetArgument<ApiClientCommand>(1)!);
                })
                .Returns(new ApplicationUpdateResult.FailureVendorNotFound());

            List<(Guid ExpectedUuid, Guid NewUuid)> syncCalls = [];
            A.CallTo(() =>
                    _applicationRepository.SyncApplicationApiClientUuid(
                        A<int>.Ignored,
                        A<string>.Ignored,
                        A<Guid>.Ignored,
                        A<Guid>.Ignored
                    )
                )
                .Invokes(call => syncCalls.Add((call.GetArgument<Guid>(2), call.GetArgument<Guid>(3))))
                .Returns(new ApiClientUuidSyncResult.Success());

            using var client = SetUpClient();

            //Act
            var updateResponse = await client.PutAsync(
                "/v3/applications/1",
                new StringContent(
                    """
                    {
                        "id": 1,
                       "ApplicationName": "Application 101",
                        "ClaimSetName": "Test",
                        "VendorId":1,
                        "EducationOrganizationIds": [1],
                        "DataStoreIds": [1]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );

            //Assert
            updateResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
            updateResponse.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
            string responseBody = await updateResponse.Content.ReadAsStringAsync();
            var actualResponse = JsonNode.Parse(responseBody);
            var expectedResponse = JsonNode.Parse(
                """
                {
                  "detail": "Reference 'VendorId' does not exist.",
                  "type": "urn:ed-fi:api:conflict:unresolved-reference",
                  "title": "Unresolved Reference",
                  "status": 409,
                  "correlationId": "{correlationId}",
                  "validationErrors": {},
                  "errors": []
                }
                """.Replace("{correlationId}", actualResponse!["correlationId"]!.GetValue<string>())
            );
            JsonNode.DeepEquals(actualResponse, expectedResponse).Should().Be(true);

            clientUpdateNames.Should().Equal("Application 101", "Original Application");
            updateCommands.Should().HaveCount(1);
            apiClientCommands[0].ClientUuid.Should().Be(updatedUuid);
            syncCalls.Should().Equal((originalUuid, rollbackUuid));
        }

        [Test]
        public async Task Should_not_update_identity_provider_when_update_vendor_id_is_invalid()
        {
            // Arrange
            A.CallTo(() => _vendorRepository.GetVendor(A<int>.Ignored))
                .Returns(new VendorGetResult.FailureNotFound());

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
                        "vendorId": 999,
                        "educationOrganizationIds": [1],
                        "dataStoreIds": [1]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );

            // Assert
            updateResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
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
            A.CallTo(() =>
                    _applicationRepository.UpdateApplication(
                        A<ApplicationUpdateCommand>.Ignored,
                        A<ApiClientCommand>.Ignored
                    )
                )
                .MustNotHaveHappened();
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
                  "type": "urn:ed-fi:api:internal-server-error",
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
        public async Task It_returns_internal_server_error_when_vendor_lookup_fails_on_update()
        {
            const string Sentinel = "SENTINEL_VENDOR_LOOKUP_UPDATE_must_not_leak";
            A.CallTo(() => _vendorRepository.GetVendor(A<int>.Ignored))
                .Returns(new VendorGetResult.FailureUnknown(Sentinel));

            using var client = SetUpClient();

            var updateResponse = await client.PutAsync(
                "/v3/applications/1",
                new StringContent(
                    """
                    {
                        "id": 1,
                       "ApplicationName": "Application 101",
                        "ClaimSetName": "Test",
                        "VendorId":1,
                        "EducationOrganizationIds": [1],
                        "DataStoreIds": [1]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );

            updateResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            updateResponse.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
            string responseBody = await updateResponse.Content.ReadAsStringAsync();
            responseBody.Should().NotContain(Sentinel);
            JsonNode actualResponse = JsonNode.Parse(responseBody)!;
            JsonNode expectedResponse = JsonNode.Parse(
                """
                {
                  "detail": "",
                  "type": "urn:ed-fi:api:internal-server-error",
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

            A.CallTo(() =>
                    _applicationRepository.UpdateApplication(
                        A<ApplicationUpdateCommand>.Ignored,
                        A<ApiClientCommand>.Ignored
                    )
                )
                .Returns(new ApplicationUpdateResult.FailureDuplicateApplication("Test Application"));

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
                  "type": "urn:ed-fi:api:bad-request:data",
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
            // Arrange
            var originalUuid = Guid.NewGuid();
            var updatedUuid = Guid.NewGuid();
            var rollbackUuid = Guid.NewGuid();
            var originalState = new ApplicationUpdateState(
                "Original Application",
                7,
                "OriginalClaim",
                [9],
                [],
                "clientId",
                originalUuid,
                true,
                [42]
            );

            A.CallTo(() =>
                    _applicationRepository.GetApplicationUpdateState(A<int>.Ignored, A<string>.Ignored)
                )
                .Returns(new ApplicationUpdateStateResult.Success(originalState));

            List<(
                string ClientUuid,
                string DisplayName,
                string Scope,
                string EducationOrganizationIds,
                int[]? DataStoreIds
            )> providerUpdates = [];
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
                .Invokes(call =>
                    providerUpdates.Add(
                        (
                            call.GetArgument<string>(0)!,
                            call.GetArgument<string>(1)!,
                            call.GetArgument<string>(2)!,
                            call.GetArgument<string>(3)!,
                            call.GetArgument<int[]?>(4)
                        )
                    )
                )
                .ReturnsNextFromSequence(
                    new ClientUpdateResult.Success(updatedUuid),
                    new ClientUpdateResult.Success(rollbackUuid)
                );

            List<(int ApplicationId, string ClientId, Guid ExpectedUuid, Guid NewUuid)> syncCalls = [];
            A.CallTo(() =>
                    _applicationRepository.SyncApplicationApiClientUuid(
                        A<int>.Ignored,
                        A<string>.Ignored,
                        A<Guid>.Ignored,
                        A<Guid>.Ignored
                    )
                )
                .Invokes(call =>
                    syncCalls.Add(
                        (
                            call.GetArgument<int>(0),
                            call.GetArgument<string>(1)!,
                            call.GetArgument<Guid>(2),
                            call.GetArgument<Guid>(3)
                        )
                    )
                )
                .Returns(new ApiClientUuidSyncResult.Success());

            A.CallTo(() =>
                    _applicationRepository.UpdateApplication(
                        A<ApplicationUpdateCommand>.Ignored,
                        A<ApiClientCommand>.Ignored
                    )
                )
                .Returns(new ApplicationUpdateResult.FailureDuplicateApplication("Test Application"));

            using var client = SetUpClient();

            // Act
            var updateResponse = await client.PutAsync(
                "/v3/applications/1",
                new StringContent(
                    """
                    {
                        "Id": 1,
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

            // Assert - the compensation restored the recreated client to the exact original
            // state and synchronized its UUID before the duplicate-name response was returned.
            // Removing the compensation fails these capture assertions.
            providerUpdates.Should().HaveCount(2);
            providerUpdates[1].ClientUuid.Should().Be(updatedUuid.ToString());
            providerUpdates[1].DisplayName.Should().Be("Original Application");
            providerUpdates[1].Scope.Should().Be("OriginalClaim");
            providerUpdates[1].EducationOrganizationIds.Should().Be("9");
            providerUpdates[1].DataStoreIds.Should().Equal(42);

            syncCalls.Should().HaveCount(1);
            syncCalls[0].ApplicationId.Should().Be(1);
            syncCalls[0].ClientId.Should().Be("clientId");
            syncCalls[0].ExpectedUuid.Should().Be(originalUuid);
            syncCalls[0].NewUuid.Should().Be(rollbackUuid);

            updateResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            string responseBody = await updateResponse.Content.ReadAsStringAsync();
            var actualResponse = JsonNode.Parse(responseBody);
            var expectedResponse = JsonNode.Parse(
                """
                {
                  "detail": "Data validation failed. See 'validationErrors' for details.",
                  "type": "urn:ed-fi:api:bad-request:data",
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

            A.CallTo(() =>
                    _applicationRepository.UpdateApplication(
                        A<ApplicationUpdateCommand>.Ignored,
                        A<ApiClientCommand>.Ignored
                    )
                )
                .Returns(new ApplicationUpdateResult.FailureDataStoreNotFound());

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
                  "type": "urn:ed-fi:api:conflict:unresolved-reference",
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
        public async Task Should_return_conflict_when_data_store_not_found_at_repository_on_update()
        {
            // Arrange
            var originalUuid = Guid.NewGuid();
            A.CallTo(() =>
                    _applicationRepository.GetApplicationUpdateState(A<int>.Ignored, A<string>.Ignored)
                )
                .Returns(
                    new ApplicationUpdateStateResult.Success(
                        new ApplicationUpdateState(
                            "Original Application",
                            7,
                            "OriginalClaim",
                            [9],
                            [],
                            "clientId",
                            originalUuid,
                            true,
                            [1]
                        )
                    )
                );

            var updatedUuid = Guid.NewGuid();
            var rollbackUuid = Guid.NewGuid();
            List<string> clientUpdateNames = [];
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
                .Invokes(call => clientUpdateNames.Add(call.GetArgument<string>(1)!))
                .ReturnsNextFromSequence(
                    new ClientUpdateResult.Success(updatedUuid),
                    new ClientUpdateResult.Success(rollbackUuid)
                );

            List<ApplicationUpdateCommand> updateCommands = [];
            List<ApiClientCommand> apiClientCommands = [];
            A.CallTo(() =>
                    _applicationRepository.UpdateApplication(
                        A<ApplicationUpdateCommand>.Ignored,
                        A<ApiClientCommand>.Ignored
                    )
                )
                .Invokes(call =>
                {
                    updateCommands.Add(call.GetArgument<ApplicationUpdateCommand>(0)!);
                    apiClientCommands.Add(call.GetArgument<ApiClientCommand>(1)!);
                })
                .Returns(new ApplicationUpdateResult.FailureDataStoreNotFound());

            List<(Guid ExpectedUuid, Guid NewUuid)> syncCalls = [];
            A.CallTo(() =>
                    _applicationRepository.SyncApplicationApiClientUuid(
                        A<int>.Ignored,
                        A<string>.Ignored,
                        A<Guid>.Ignored,
                        A<Guid>.Ignored
                    )
                )
                .Invokes(call => syncCalls.Add((call.GetArgument<Guid>(2), call.GetArgument<Guid>(3))))
                .Returns(new ApiClientUuidSyncResult.Success());

            using var client = SetUpClient();

            // Act
            var updateResponse = await client.PutAsync(
                "/v3/applications/1",
                new StringContent(
                    """
                    {
                        "Id": 1,
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
            updateResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
            updateResponse.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
            string responseBody = await updateResponse.Content.ReadAsStringAsync();
            var actualResponse = JsonNode.Parse(responseBody);
            var expectedResponse = JsonNode.Parse(
                """
                {
                  "detail": "Data store does not exist.",
                  "type": "urn:ed-fi:api:conflict:unresolved-reference",
                  "title": "Unresolved Reference",
                  "status": 409,
                  "correlationId": "{correlationId}",
                  "validationErrors": {},
                  "errors": []
                }
                """.Replace("{correlationId}", actualResponse!["correlationId"]!.GetValue<string>())
            );
            JsonNode.DeepEquals(actualResponse, expectedResponse).Should().Be(true);

            clientUpdateNames.Should().Equal("Test Application", "Original Application");
            updateCommands.Should().HaveCount(1);
            apiClientCommands[0].ClientUuid.Should().Be(updatedUuid);
            syncCalls.Should().Equal((originalUuid, rollbackUuid));
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

        [Test]
        public async Task Should_not_update_identity_provider_when_update_data_store_id_is_invalid()
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
            var updateResponse = await client.PutAsync(
                "/v3/applications/1",
                new StringContent(
                    """
                    {
                        "Id": 1,
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
            updateResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
            updateResponse.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
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
            A.CallTo(() =>
                    _applicationRepository.UpdateApplication(
                        A<ApplicationUpdateCommand>.Ignored,
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

            A.CallTo(() =>
                    _applicationRepository.UpdateApplication(
                        A<ApplicationUpdateCommand>.Ignored,
                        A<ApiClientCommand>.Ignored
                    )
                )
                .Returns(new ApplicationUpdateResult.FailureProfileNotFound());

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
                  "type": "urn:ed-fi:api:conflict:unresolved-reference",
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
        public async Task Should_return_conflict_when_profile_not_found_at_pre_check_on_update()
        {
            // Arrange
            A.CallTo(() => _profileRepository.GetProfile(A<int>.Ignored))
                .Returns(new ProfileGetResult.FailureNotFound());

            using var client = SetUpClient();

            // Act
            var updateResponse = await client.PutAsync(
                "/v3/applications/1",
                new StringContent(
                    """
                    {
                        "Id": 1,
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
            updateResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
            updateResponse.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
            string responseBody = await updateResponse.Content.ReadAsStringAsync();
            var actualResponse = JsonNode.Parse(responseBody);
            var expectedResponse = JsonNode.Parse(
                """
                {
                  "detail": "Profile does not exist.",
                  "type": "urn:ed-fi:api:conflict:unresolved-reference",
                  "title": "Unresolved Reference",
                  "status": 409,
                  "correlationId": "{correlationId}",
                  "validationErrors": {},
                  "errors": []
                }
                """.Replace("{correlationId}", actualResponse!["correlationId"]!.GetValue<string>())
            );
            JsonNode.DeepEquals(actualResponse, expectedResponse).Should().Be(true);
        }

        [Test]
        public async Task It_returns_conflict_when_profile_not_found_at_repository_on_update()
        {
            A.CallTo(() => _profileRepository.GetProfile(A<int>.Ignored))
                .Returns(
                    new ProfileGetResult.Success(
                        new ProfileResponse
                        {
                            Id = 999,
                            Name = "TestProfile",
                            Definition = "<Profile name=\"TestProfile\"></Profile>",
                        }
                    )
                );

            var originalUuid = Guid.NewGuid();
            A.CallTo(() =>
                    _applicationRepository.GetApplicationUpdateState(A<int>.Ignored, A<string>.Ignored)
                )
                .Returns(
                    new ApplicationUpdateStateResult.Success(
                        new ApplicationUpdateState(
                            "Original Application",
                            7,
                            "OriginalClaim",
                            [9],
                            [],
                            "clientId",
                            originalUuid,
                            true,
                            [1]
                        )
                    )
                );

            var updatedUuid = Guid.NewGuid();
            var rollbackUuid = Guid.NewGuid();
            List<string> clientUpdateNames = [];
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
                .Invokes(call => clientUpdateNames.Add(call.GetArgument<string>(1)!))
                .ReturnsNextFromSequence(
                    new ClientUpdateResult.Success(updatedUuid),
                    new ClientUpdateResult.Success(rollbackUuid)
                );

            List<ApplicationUpdateCommand> updateCommands = [];
            List<ApiClientCommand> apiClientCommands = [];
            A.CallTo(() =>
                    _applicationRepository.UpdateApplication(
                        A<ApplicationUpdateCommand>.Ignored,
                        A<ApiClientCommand>.Ignored
                    )
                )
                .Invokes(call =>
                {
                    updateCommands.Add(call.GetArgument<ApplicationUpdateCommand>(0)!);
                    apiClientCommands.Add(call.GetArgument<ApiClientCommand>(1)!);
                })
                .Returns(new ApplicationUpdateResult.FailureProfileNotFound());

            List<(Guid ExpectedUuid, Guid NewUuid)> syncCalls = [];
            A.CallTo(() =>
                    _applicationRepository.SyncApplicationApiClientUuid(
                        A<int>.Ignored,
                        A<string>.Ignored,
                        A<Guid>.Ignored,
                        A<Guid>.Ignored
                    )
                )
                .Invokes(call => syncCalls.Add((call.GetArgument<Guid>(2), call.GetArgument<Guid>(3))))
                .Returns(new ApiClientUuidSyncResult.Success());

            using var client = SetUpClient();

            var updateResponse = await client.PutAsync(
                "/v3/applications/1",
                new StringContent(
                    """
                    {
                        "Id": 1,
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

            updateResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
            updateResponse.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
            string responseBody = await updateResponse.Content.ReadAsStringAsync();
            JsonNode actualResponse = JsonNode.Parse(responseBody)!;
            JsonNode expectedResponse = JsonNode.Parse(
                """
                {
                  "detail": "Profile does not exist.",
                  "type": "urn:ed-fi:api:conflict:unresolved-reference",
                  "title": "Unresolved Reference",
                  "status": 409,
                  "correlationId": "{correlationId}",
                  "validationErrors": {},
                  "errors": []
                }
                """.Replace("{correlationId}", actualResponse["correlationId"]!.GetValue<string>())
            )!;
            JsonNode.DeepEquals(actualResponse, expectedResponse).Should().Be(true);

            clientUpdateNames.Should().Equal("Test Application", "Original Application");
            updateCommands.Should().HaveCount(1);
            apiClientCommands[0].ClientUuid.Should().Be(updatedUuid);
            syncCalls.Should().Equal((originalUuid, rollbackUuid));
        }

        [Test]
        public async Task Should_not_update_identity_provider_when_update_profile_id_is_invalid()
        {
            // Arrange
            A.CallTo(() => _profileRepository.GetProfile(A<int>.Ignored))
                .Returns(new ProfileGetResult.FailureNotFound());

            // Local capture lists rather than MustNotHaveHappened: the shared fixture instance
            // accumulates call history from sibling tests that legitimately update the client.
            List<string> updatedClientUuids = [];
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
                .Invokes(call => updatedClientUuids.Add(call.GetArgument<string>(0)!))
                .Returns(new ClientUpdateResult.Success(Guid.NewGuid()));

            List<ApplicationUpdateCommand> applicationUpdates = [];
            A.CallTo(() =>
                    _applicationRepository.UpdateApplication(
                        A<ApplicationUpdateCommand>.Ignored,
                        A<ApiClientCommand>.Ignored
                    )
                )
                .Invokes(call => applicationUpdates.Add(call.GetArgument<ApplicationUpdateCommand>(0)!))
                .Returns(new ApplicationUpdateResult.FailureProfileNotFound());

            using var client = SetUpClient();

            // Act
            var updateResponse = await client.PutAsync(
                "/v3/applications/1",
                new StringContent(
                    """
                    {
                        "Id": 1,
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
            updateResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);

            // Assert - the identity provider client was never mutated for an invalid
            // profile reference, so a rejected update cannot leave the client out of sync
            updatedClientUuids.Should().BeEmpty();
            applicationUpdates.Should().BeEmpty();
        }
    }

    public abstract class UpdateRollbackTestBase : ApplicationModuleTests
    {
        private const string UpdateRequestBody = """
            {
                "Id": 1,
                "ApplicationName": "Test Application",
                "ClaimSetName": "TestClaimSet",
                "VendorId": 1,
                "EducationOrganizationIds": [1],
                "DataStoreIds": [1]
            }
            """;

        private HttpClient _client = null!;
        protected HttpResponseMessage _updateResponse = null!;
        protected Guid _originalClientUuid;
        protected ApplicationUpdateState _originalState = null!;

        [SetUp]
        public void SetUpUpdateDefaults()
        {
            _originalClientUuid = Guid.NewGuid();
            _originalState = new ApplicationUpdateState(
                "Original Application",
                7,
                "OriginalClaim",
                [9],
                [],
                "clientId",
                _originalClientUuid,
                true,
                [1]
            );

            A.CallTo(() =>
                    _applicationRepository.GetApplicationUpdateState(A<int>.Ignored, A<string>.Ignored)
                )
                .Returns(new ApplicationUpdateStateResult.Success(_originalState));

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

            A.CallTo(() => _applicationRepository.GetApplicationApiClients(A<int>.Ignored))
                .Returns(
                    new ApplicationApiClientsResult.Success([
                        new ApiClient("clientId", _originalClientUuid, true),
                    ])
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

        [TearDown]
        public void TearDownUpdateClient()
        {
            _updateResponse?.Dispose();
            _client?.Dispose();
        }

        protected async Task ActUpdateAsync()
        {
            _client = SetUpClient();
            _updateResponse = await _client.PutAsync(
                "/v3/applications/1",
                new StringContent(UpdateRequestBody, Encoding.UTF8, "application/json")
            );
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
              "type": "urn:ed-fi:api:internal-server-error",
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
    public class Given_an_application_update_where_the_original_application_is_missing
        : UpdateRollbackTestBase
    {
        private List<string> _updatedClientUuids = null!;

        [SetUp]
        public async Task Act()
        {
            A.CallTo(() =>
                    _applicationRepository.GetApplicationUpdateState(A<int>.Ignored, A<string>.Ignored)
                )
                .Returns(new ApplicationUpdateStateResult.FailureNotExists());

            _updatedClientUuids = [];
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
                .Invokes(call => _updatedClientUuids.Add(call.GetArgument<string>(0)!))
                .Returns(new ClientUpdateResult.Success(Guid.NewGuid()));

            await ActUpdateAsync();
        }

        [Test]
        public void It_returns_not_found() => _updateResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);

        [Test]
        public void It_does_not_update_the_identity_provider() => _updatedClientUuids.Should().BeEmpty();
    }

    [TestFixture]
    public class Given_an_application_update_where_the_original_state_cannot_be_read : UpdateRollbackTestBase
    {
        private const string Sentinel = "SENTINEL_ORIGINAL_STATE_must_not_leak";
        private List<string> _updatedClientUuids = null!;

        [SetUp]
        public async Task Act()
        {
            A.CallTo(() =>
                    _applicationRepository.GetApplicationUpdateState(A<int>.Ignored, A<string>.Ignored)
                )
                .Returns(new ApplicationUpdateStateResult.FailureUnknown(Sentinel));

            _updatedClientUuids = [];
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
                .Invokes(call => _updatedClientUuids.Add(call.GetArgument<string>(0)!))
                .Returns(new ClientUpdateResult.Success(Guid.NewGuid()));

            await ActUpdateAsync();
        }

        [Test]
        public async Task It_returns_a_sanitized_internal_server_error() =>
            await AssertSanitizedInternalServerError(_updateResponse, Sentinel);

        [Test]
        public void It_does_not_update_the_identity_provider() => _updatedClientUuids.Should().BeEmpty();
    }

    [TestFixture]
    public class Given_a_failed_application_update_where_the_identity_provider_rollback_fails
        : UpdateRollbackTestBase
    {
        private const string Sentinel = "SENTINEL_ROLLBACK_must_not_leak";
        private Guid _updatedUuid;
        private List<string> _updatedClientUuids = null!;

        [SetUp]
        public async Task Act()
        {
            _updatedUuid = Guid.NewGuid();
            _updatedClientUuids = [];
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
                .Invokes(call => _updatedClientUuids.Add(call.GetArgument<string>(0)!))
                .ReturnsNextFromSequence(
                    new ClientUpdateResult.Success(_updatedUuid),
                    new ClientUpdateResult.FailureUnknown(Sentinel)
                );

            A.CallTo(() =>
                    _applicationRepository.UpdateApplication(
                        A<ApplicationUpdateCommand>.Ignored,
                        A<ApiClientCommand>.Ignored
                    )
                )
                .Returns(new ApplicationUpdateResult.FailureVendorNotFound());

            await ActUpdateAsync();
        }

        [Test]
        public async Task It_returns_a_sanitized_internal_server_error() =>
            await AssertSanitizedInternalServerError(_updateResponse, Sentinel);

        [Test]
        public void It_attempted_the_rollback_against_the_recreated_client()
        {
            _updatedClientUuids.Should().HaveCount(2);
            _updatedClientUuids[1].Should().Be(_updatedUuid.ToString());
        }
    }

    [TestFixture]
    public class Given_a_failed_application_update_where_the_rollback_state_cannot_be_persisted
        : UpdateRollbackTestBase
    {
        private const string Sentinel = "SENTINEL_ROLLBACK_SYNC_must_not_leak";
        private List<string> _deletedClientIds = null!;

        [SetUp]
        public async Task Act()
        {
            _deletedClientIds = [];
            A.CallTo(() => _clientRepository.DeleteClientAsync(A<string>.Ignored))
                .Invokes(call => _deletedClientIds.Add(call.GetArgument<string>(0)!))
                .Returns(new ClientDeleteResult.Success());

            A.CallTo(() =>
                    _applicationRepository.UpdateApplication(
                        A<ApplicationUpdateCommand>.Ignored,
                        A<ApiClientCommand>.Ignored
                    )
                )
                .Returns(new ApplicationUpdateResult.FailureVendorNotFound());

            A.CallTo(() =>
                    _applicationRepository.SyncApplicationApiClientUuid(
                        A<int>.Ignored,
                        A<string>.Ignored,
                        A<Guid>.Ignored,
                        A<Guid>.Ignored
                    )
                )
                .Returns(new ApiClientUuidSyncResult.FailureUnknown(Sentinel));

            await ActUpdateAsync();
        }

        [Test]
        public async Task It_returns_a_sanitized_internal_server_error() =>
            await AssertSanitizedInternalServerError(_updateResponse, Sentinel);

        [Test]
        public void It_deletes_nothing() => _deletedClientIds.Should().BeEmpty();
    }

    [TestFixture]
    public class Given_a_failed_application_update_where_the_application_vanishes_during_rollback
        : UpdateRollbackTestBase
    {
        private Guid _rollbackUuid;
        private List<string> _deletedClientIds = null!;

        [SetUp]
        public async Task Act()
        {
            _rollbackUuid = Guid.NewGuid();
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
                .ReturnsNextFromSequence(
                    new ClientUpdateResult.Success(Guid.NewGuid()),
                    new ClientUpdateResult.Success(_rollbackUuid)
                );

            A.CallTo(() =>
                    _applicationRepository.UpdateApplication(
                        A<ApplicationUpdateCommand>.Ignored,
                        A<ApiClientCommand>.Ignored
                    )
                )
                .Returns(new ApplicationUpdateResult.FailureVendorNotFound());

            A.CallTo(() =>
                    _applicationRepository.SyncApplicationApiClientUuid(
                        A<int>.Ignored,
                        A<string>.Ignored,
                        A<Guid>.Ignored,
                        A<Guid>.Ignored
                    )
                )
                .Returns(new ApiClientUuidSyncResult.FailureNotExistsSafeToDelete());

            _deletedClientIds = [];
            A.CallTo(() => _clientRepository.DeleteClientAsync(A<string>.Ignored))
                .Invokes(call => _deletedClientIds.Add(call.GetArgument<string>(0)!))
                .Returns(new ClientDeleteResult.Success());

            await ActUpdateAsync();
        }

        [Test]
        public async Task It_returns_a_sanitized_internal_server_error() =>
            await AssertSanitizedInternalServerError(_updateResponse);

        [Test]
        public void It_deletes_the_client_recreated_by_the_rollback() =>
            _deletedClientIds.Should().Equal(_rollbackUuid.ToString());
    }

    [TestFixture]
    public class Given_an_application_update_where_the_application_vanished_from_the_repository
        : UpdateRollbackTestBase
    {
        private Guid _updatedUuid;
        private List<string> _updatedClientUuids = null!;
        private List<string> _deletedClientIds = null!;

        [SetUp]
        public async Task Act()
        {
            _updatedUuid = Guid.NewGuid();
            _updatedClientUuids = [];
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
                .Invokes(call => _updatedClientUuids.Add(call.GetArgument<string>(0)!))
                .Returns(new ClientUpdateResult.Success(_updatedUuid));

            A.CallTo(() =>
                    _applicationRepository.UpdateApplication(
                        A<ApplicationUpdateCommand>.Ignored,
                        A<ApiClientCommand>.Ignored
                    )
                )
                .Returns(new ApplicationUpdateResult.FailureNotExists());

            _deletedClientIds = [];
            A.CallTo(() => _clientRepository.DeleteClientAsync(A<string>.Ignored))
                .Invokes(call => _deletedClientIds.Add(call.GetArgument<string>(0)!))
                .Returns(new ClientDeleteResult.Success());

            await ActUpdateAsync();
        }

        [Test]
        public void It_returns_not_found() => _updateResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);

        [Test]
        public void It_deletes_the_recreated_client_instead_of_restoring_it()
        {
            _deletedClientIds.Should().Equal(_updatedUuid.ToString());
            _updatedClientUuids.Should().HaveCount(1);
        }
    }

    [TestFixture]
    public class Given_a_vanished_application_whose_identity_provider_client_was_already_deleted
        : UpdateRollbackTestBase
    {
        [SetUp]
        public async Task Act()
        {
            A.CallTo(() =>
                    _applicationRepository.UpdateApplication(
                        A<ApplicationUpdateCommand>.Ignored,
                        A<ApiClientCommand>.Ignored
                    )
                )
                .Returns(new ApplicationUpdateResult.FailureNotExists());

            A.CallTo(() => _clientRepository.DeleteClientAsync(A<string>.Ignored))
                .Returns(new ClientDeleteResult.FailureClientNotFound("Client not found"));

            await ActUpdateAsync();
        }

        [Test]
        public void It_returns_not_found() => _updateResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [TestFixture]
    public class Given_a_vanished_application_whose_identity_provider_client_cleanup_fails
        : UpdateRollbackTestBase
    {
        private const string Sentinel = "SENTINEL_CLEANUP_must_not_leak";

        [SetUp]
        public async Task Act()
        {
            A.CallTo(() =>
                    _applicationRepository.UpdateApplication(
                        A<ApplicationUpdateCommand>.Ignored,
                        A<ApiClientCommand>.Ignored
                    )
                )
                .Returns(new ApplicationUpdateResult.FailureNotExists());

            A.CallTo(() => _clientRepository.DeleteClientAsync(A<string>.Ignored))
                .Returns(new ClientDeleteResult.FailureUnknown(Sentinel));

            await ActUpdateAsync();
        }

        [Test]
        public async Task It_returns_a_sanitized_internal_server_error() =>
            await AssertSanitizedInternalServerError(_updateResponse, Sentinel);
    }

    /// <summary>
    /// An identity-preserving provider update returns the stored UUID unchanged, so guarded
    /// synchronization is asked to replace a UUID with itself. It must recognize that as applied
    /// rather than as stale state, and compensation must still return the domain contract.
    /// </summary>
    [TestFixture]
    public class Given_a_failed_application_update_whose_provider_preserves_the_uuid : UpdateRollbackTestBase
    {
        private List<(Guid Expected, Guid New)> _syncCalls = null!;
        private List<string> _deletedClientIds = null!;

        [SetUp]
        public async Task Act()
        {
            _syncCalls = [];
            _deletedClientIds = [];

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
                .Returns(new ClientUpdateResult.Success(_originalClientUuid));

            A.CallTo(() => _clientRepository.DeleteClientAsync(A<string>.Ignored))
                .Invokes(call => _deletedClientIds.Add(call.GetArgument<string>(0)!))
                .Returns(new ClientDeleteResult.Success());

            A.CallTo(() =>
                    _applicationRepository.UpdateApplication(
                        A<ApplicationUpdateCommand>.Ignored,
                        A<ApiClientCommand>.Ignored
                    )
                )
                .Returns(new ApplicationUpdateResult.FailureVendorNotFound());

            A.CallTo(() =>
                    _applicationRepository.SyncApplicationApiClientUuid(
                        A<int>.Ignored,
                        A<string>.Ignored,
                        A<Guid>.Ignored,
                        A<Guid>.Ignored
                    )
                )
                .Invokes(call => _syncCalls.Add((call.GetArgument<Guid>(2), call.GetArgument<Guid>(3))))
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
            _syncCalls.Should().Equal((_originalClientUuid, _originalClientUuid));

        [Test]
        public void It_deletes_no_provider_client() => _deletedClientIds.Should().BeEmpty();
    }

    [TestFixture]
    public class Given_an_application_update_that_succeeds_with_a_stable_provider_uuid
        : UpdateRollbackTestBase
    {
        private List<ApiClientCommand> _writtenClientCommands = null!;

        [SetUp]
        public async Task Act()
        {
            _writtenClientCommands = [];

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
                .Returns(new ClientUpdateResult.Success(_originalClientUuid));

            A.CallTo(() =>
                    _applicationRepository.UpdateApplication(
                        A<ApplicationUpdateCommand>.Ignored,
                        A<ApiClientCommand>.Ignored
                    )
                )
                .Invokes(call => _writtenClientCommands.Add(call.GetArgument<ApiClientCommand>(1)!))
                .Returns(new ApplicationUpdateResult.Success());

            await ActUpdateAsync();
        }

        [Test]
        public void It_returns_no_content() =>
            _updateResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        [Test]
        public void It_persists_the_unchanged_client_uuid() =>
            _writtenClientCommands.Should().ContainSingle().Which.ClientUuid.Should().Be(_originalClientUuid);
    }

    [TestFixture]
    public class Given_an_application_update_whose_stored_provider_client_is_missing : UpdateRollbackTestBase
    {
        private const string Sentinel = "SENTINEL_APPLICATION_STORED_CLIENT_MISSING_must_not_leak";

        private RecordingLockManager _recordingLockManager = null!;
        private List<string> _repositoryUpdates = null!;

        [SetUp]
        public async Task Act()
        {
            _repositoryUpdates = [];
            _recordingLockManager = new RecordingLockManager();
            _lockManager = _recordingLockManager;

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
                .Returns(new ClientUpdateResult.FailureNotFound(Sentinel));

            A.CallTo(() =>
                    _applicationRepository.UpdateApplication(
                        A<ApplicationUpdateCommand>.Ignored,
                        A<ApiClientCommand>.Ignored
                    )
                )
                .Invokes(_ => _repositoryUpdates.Add("update"))
                .Returns(new ApplicationUpdateResult.Success());

            await ActUpdateAsync();
        }

        [Test]
        public async Task It_returns_a_sanitized_internal_server_error() =>
            await AssertSanitizedInternalServerError(_updateResponse, Sentinel);

        [Test]
        public void It_does_not_update_the_database_application() => _repositoryUpdates.Should().BeEmpty();

        [Test]
        public void It_does_not_synchronize_the_client_uuid() =>
            A.CallTo(() =>
                    _applicationRepository.SyncApplicationApiClientUuid(
                        A<int>.Ignored,
                        A<string>.Ignored,
                        A<Guid>.Ignored,
                        A<Guid>.Ignored
                    )
                )
                .MustNotHaveHappened();

        [Test]
        public void It_acquires_and_releases_the_aggregate_lock()
        {
            _recordingLockManager.AcquiredApplicationIds.Should().Equal(1);
            _recordingLockManager.Handle.Disposed.Should().BeTrue();
        }
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
        public void It_does_not_call_application_repository_update()
        {
            A.CallTo(() =>
                    _applicationRepository.UpdateApplication(
                        A<ApplicationUpdateCommand>.Ignored,
                        A<ApiClientCommand>.Ignored
                    )
                )
                .MustNotHaveHappened();
        }
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
        public void It_does_not_call_application_repository_update()
        {
            A.CallTo(() =>
                    _applicationRepository.UpdateApplication(
                        A<ApplicationUpdateCommand>.Ignored,
                        A<ApiClientCommand>.Ignored
                    )
                )
                .MustNotHaveHappened();
        }
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
                  "type": "urn:ed-fi:api:conflict:unresolved-reference",
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

    [TestFixture]
    public class ResetCredentialEndpointEnabledTests : ApplicationModuleTests
    {
        /// <summary>
        /// Tests that verify the reset-credential endpoint is available when
        /// EnableApplicationResetEndpoint is true.
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
        public async Task Should_successfully_reset_credentials_when_endpoint_enabled()
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
        public async Task Should_return_not_found_when_application_has_no_api_clients()
        {
            // Arrange
            using var client = SetUpClient();
            A.CallTo(() => _applicationRepository.GetApplicationApiClients(A<int>.Ignored))
                .Returns(new ApplicationApiClientsResult.Success([]));

            // Act
            var resetResponse = await client.PutAsync("/v3/applications/1/reset-credential", null);

            // Assert
            resetResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
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
                  "type": "urn:ed-fi:api:internal-server-error",
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
        /// Asserts the full urn:ed-fi:api:bad-request:parameter contract (real pipeline, not a
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
            body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:bad-request:parameter");
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
    public class ResetCredentialEndpointDisabledTests : ApplicationModuleTests
    {
        /// <summary>
        /// Tests that verify the reset-credential endpoint returns 404 when
        /// EnableApplicationResetEndpoint is false. This scenario is typical when
        /// using multiple API clients per application to avoid credential confusion.
        /// </summary>
        private HttpClient SetUpClientWithEndpointDisabled()
        {
            var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureServices(collection =>
                {
                    collection.AddTestAuthentication();

                    // Override AppSettings to disable the reset endpoint
                    collection.Configure<EdFi.DmsConfigurationService.Frontend.AspNetCore.Configuration.AppSettings>(
                        options =>
                        {
                            options.EnableApplicationResetEndpoint = false;
                        }
                    );

                    collection
                        .AddTransient((_) => _applicationRepository)
                        .AddTransient((_) => _clientRepository)
                        .AddTransient((_) => _dataStoreRepository)
                        .AddTransient((_) => _vendorRepository)
                        .AddTransient((_) => _profileRepository);
                });
            });
            _factoryTracker.Track(factory);
            var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-Test-Scope", AuthorizationScopes.AdminScope.Name);
            return client;
        }

        [Test]
        public async Task Should_return_not_found_when_endpoint_disabled()
        {
            // Arrange
            using var client = SetUpClientWithEndpointDisabled();

            // Act
            var resetResponse = await client.PutAsync("/v3/applications/1/reset-credential", null);

            // Assert
            resetResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        [Test]
        public async Task Should_still_allow_other_application_endpoints_when_reset_disabled()
        {
            // Arrange
            using var client = SetUpClientWithEndpointDisabled();

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
                            ProfileIds = [],
                        }
                    )
                );

            // Act - Verify GET still works
            var getResponse = await client.GetAsync("/v3/applications/1");

            // Assert
            getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
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
        )
        {
            AcquiredApplicationIds.Add(applicationId);
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
    public class Given_an_application_update_when_the_aggregate_lock_times_out : UpdateRollbackTestBase
    {
        private List<string> _dependencyCalls = null!;

        [SetUp]
        public async Task Act()
        {
            _dependencyCalls = [];
            A.CallTo(() => _lockManager.AcquireAsync(A<int>.Ignored, A<CancellationToken>.Ignored))
                .Returns(new ApplicationLockResult.FailureTimeout());
            A.CallTo(_applicationRepository).Invokes(call => _dependencyCalls.Add(call.Method.Name));
            A.CallTo(_apiClientRepository).Invokes(call => _dependencyCalls.Add(call.Method.Name));
            A.CallTo(_clientRepository).Invokes(call => _dependencyCalls.Add(call.Method.Name));
            A.CallTo(_vendorRepository).Invokes(call => _dependencyCalls.Add(call.Method.Name));
            A.CallTo(_dataStoreRepository).Invokes(call => _dependencyCalls.Add(call.Method.Name));
            A.CallTo(_profileRepository).Invokes(call => _dependencyCalls.Add(call.Method.Name));

            await ActUpdateAsync();
        }

        [Test]
        public async Task It_returns_the_retriable_conflict_contract() =>
            await AssertLockConflictContract(_updateResponse);

        [Test]
        public void It_calls_no_repository_or_identity_provider_dependency() =>
            _dependencyCalls.Should().BeEmpty();
    }

    [TestFixture]
    public class Given_an_application_update_when_the_aggregate_lock_cannot_be_acquired
        : UpdateRollbackTestBase
    {
        private const string Sentinel = "SENTINEL_LOCK_ACQUIRE_must_not_leak";

        [SetUp]
        public async Task Act()
        {
            A.CallTo(() => _lockManager.AcquireAsync(A<int>.Ignored, A<CancellationToken>.Ignored))
                .Returns(new ApplicationLockResult.FailureUnknown(Sentinel));

            await ActUpdateAsync();
        }

        [Test]
        public async Task It_returns_a_sanitized_internal_server_error() =>
            await AssertSanitizedInternalServerError(_updateResponse, Sentinel);
    }

    [TestFixture]
    public class Given_an_application_update_that_succeeds_under_the_lock : UpdateRollbackTestBase
    {
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
                        new ApiClient("clientId", _originalClientUuid, true),
                    ])
                );

            A.CallTo(() =>
                    _applicationRepository.UpdateApplication(
                        A<ApplicationUpdateCommand>.Ignored,
                        A<ApiClientCommand>.Ignored
                    )
                )
                .Returns(new ApplicationUpdateResult.Success());

            await ActUpdateAsync();
        }

        [Test]
        public void It_returns_no_content() =>
            _updateResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        [Test]
        public void It_acquires_the_lock_before_the_first_read()
        {
            _recordingLockManager.AcquiredApplicationIds.Should().Equal(1);
            _callOrder.Take(2).Should().Equal("lock", "clients-read");
        }

        [Test]
        public void It_releases_the_lock() => _recordingLockManager.Handle.Disposed.Should().BeTrue();
    }

    public abstract class ThrownRepositoryExceptionTestBase : UpdateRollbackTestBase
    {
        protected const string Sentinel = "SENTINEL_THROWN_REPO_must_not_leak";

        protected Guid _updatedUuid;
        protected Guid _rollbackUuid;
        protected List<string> _updatedClientUuids = null!;
        protected List<string> _deletedClientIds = null!;
        protected List<(Guid ExpectedUuid, Guid NewUuid)> _syncCalls = null!;

        [SetUp]
        public void SetUpThrownDefaults()
        {
            _updatedUuid = Guid.NewGuid();
            _rollbackUuid = Guid.NewGuid();
            _updatedClientUuids = [];
            _deletedClientIds = [];
            _syncCalls = [];

            A.CallTo(() =>
                    _applicationRepository.SyncApplicationApiClientUuid(
                        A<int>.Ignored,
                        A<string>.Ignored,
                        A<Guid>.Ignored,
                        A<Guid>.Ignored
                    )
                )
                .Invokes(call => _syncCalls.Add((call.GetArgument<Guid>(2), call.GetArgument<Guid>(3))))
                .Returns(new ApiClientUuidSyncResult.Success());

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
                .Invokes(call => _updatedClientUuids.Add(call.GetArgument<string>(0)!))
                .ReturnsNextFromSequence(
                    new ClientUpdateResult.Success(Guid.Empty),
                    new ClientUpdateResult.Success(Guid.Empty)
                );

            A.CallTo(() => _clientRepository.DeleteClientAsync(A<string>.Ignored))
                .Invokes(call => _deletedClientIds.Add(call.GetArgument<string>(0)!))
                .Returns(new ClientDeleteResult.Success());

            A.CallTo(() =>
                    _applicationRepository.UpdateApplication(
                        A<ApplicationUpdateCommand>.Ignored,
                        A<ApiClientCommand>.Ignored
                    )
                )
                .Throws(new InvalidOperationException(Sentinel));
        }

        protected void ArrangeProviderUpdates()
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
                .Invokes(call => _updatedClientUuids.Add(call.GetArgument<string>(0)!))
                .ReturnsNextFromSequence(
                    new ClientUpdateResult.Success(_updatedUuid),
                    new ClientUpdateResult.Success(_rollbackUuid)
                );
        }

        protected ApplicationUpdateState CommandMatchingState() =>
            new("Test Application", 1, "TestClaimSet", [1], [], "clientId", _updatedUuid, true, [1]);
    }

    [TestFixture]
    public class Given_a_thrown_repository_exception_whose_transaction_committed
        : ThrownRepositoryExceptionTestBase
    {
        [SetUp]
        public async Task Act()
        {
            ArrangeProviderUpdates();
            A.CallTo(() =>
                    _applicationRepository.GetApplicationUpdateState(A<int>.Ignored, A<string>.Ignored)
                )
                .ReturnsNextFromSequence(
                    new ApplicationUpdateStateResult.Success(_originalState),
                    new ApplicationUpdateStateResult.Success(CommandMatchingState())
                );

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
        public void It_performs_no_rollback_or_deletion()
        {
            _updatedClientUuids.Should().HaveCount(1);
            _deletedClientIds.Should().BeEmpty();
        }

        [Test]
        public void It_does_not_synchronize_the_uuid() => _syncCalls.Should().BeEmpty();
    }

    [TestFixture]
    public class Given_a_thrown_repository_exception_whose_transaction_did_not_commit
        : ThrownRepositoryExceptionTestBase
    {
        [SetUp]
        public async Task Act()
        {
            ArrangeProviderUpdates();
            A.CallTo(() =>
                    _applicationRepository.GetApplicationUpdateState(A<int>.Ignored, A<string>.Ignored)
                )
                .ReturnsNextFromSequence(
                    new ApplicationUpdateStateResult.Success(_originalState),
                    new ApplicationUpdateStateResult.Success(_originalState)
                );

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
            _syncCalls.Should().Equal((_originalClientUuid, _rollbackUuid));
        }
    }

    [TestFixture]
    public class Given_a_thrown_repository_exception_whose_outcome_resolution_fails
        : ThrownRepositoryExceptionTestBase
    {
        private const string ResolutionSentinel = "SENTINEL_RESOLUTION_must_not_leak";

        [SetUp]
        public async Task Act()
        {
            ArrangeProviderUpdates();
            A.CallTo(() =>
                    _applicationRepository.GetApplicationUpdateState(A<int>.Ignored, A<string>.Ignored)
                )
                .ReturnsNextFromSequence(
                    new ApplicationUpdateStateResult.Success(_originalState),
                    new ApplicationUpdateStateResult.FailureUnknown(ResolutionSentinel)
                );

            await ActUpdateAsync();
        }

        [Test]
        public async Task It_returns_a_sanitized_internal_server_error()
        {
            await AssertSanitizedInternalServerError(_updateResponse, Sentinel);
            string responseBody = await _updateResponse.Content.ReadAsStringAsync();
            responseBody.Should().NotContain(ResolutionSentinel);
        }

        [Test]
        public void It_performs_no_rollback_or_deletion()
        {
            _updatedClientUuids.Should().HaveCount(1);
            _deletedClientIds.Should().BeEmpty();
        }
    }

    [TestFixture]
    public class Given_a_failed_application_update_whose_provider_compensation_throws : UpdateRollbackTestBase
    {
        private const string Sentinel = "SENTINEL_COMPENSATION_THROW_must_not_leak";
        private RecordingLockManager _recordingLockManager = null!;

        [SetUp]
        public async Task Act()
        {
            _recordingLockManager = new RecordingLockManager();
            _lockManager = _recordingLockManager;

            int providerCalls = 0;
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
                .ReturnsLazily(_ =>
                {
                    providerCalls++;
                    if (providerCalls == 1)
                    {
                        return Task.FromResult<ClientUpdateResult>(
                            new ClientUpdateResult.Success(Guid.NewGuid())
                        );
                    }

                    throw new InvalidOperationException(Sentinel);
                });

            A.CallTo(() =>
                    _applicationRepository.UpdateApplication(
                        A<ApplicationUpdateCommand>.Ignored,
                        A<ApiClientCommand>.Ignored
                    )
                )
                .Returns(new ApplicationUpdateResult.FailureVendorNotFound());

            await ActUpdateAsync();
        }

        [Test]
        public async Task It_returns_a_sanitized_internal_server_error() =>
            await AssertSanitizedInternalServerError(_updateResponse, Sentinel);

        [Test]
        public void It_releases_the_lock() => _recordingLockManager.Handle.Disposed.Should().BeTrue();
    }

    [TestFixture]
    public class Given_a_failed_application_update_with_disjoint_client_data_stores : UpdateRollbackTestBase
    {
        private List<int[]?> _providerDataStoreArguments = null!;

        [SetUp]
        public async Task Act()
        {
            // The aggregate read unions data stores across clients; compensation must restore
            // the selected client's exact set instead.
            A.CallTo(() => _applicationRepository.GetApplication(A<int>.Ignored))
                .Returns(
                    new ApplicationGetResult.Success(
                        new ApplicationResponse
                        {
                            Id = 1,
                            ApplicationName = "Original Application",
                            ClaimSetName = "OriginalClaim",
                            VendorId = 7,
                            EducationOrganizationIds = [9],
                            DataStoreIds = [1, 2],
                            ProfileIds = [],
                        }
                    )
                );

            _originalState = new ApplicationUpdateState(
                "Original Application",
                7,
                "OriginalClaim",
                [9],
                [],
                "clientId",
                _originalClientUuid,
                true,
                [1]
            );
            A.CallTo(() =>
                    _applicationRepository.GetApplicationUpdateState(A<int>.Ignored, A<string>.Ignored)
                )
                .Returns(new ApplicationUpdateStateResult.Success(_originalState));

            _providerDataStoreArguments = [];
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
                .Invokes(call => _providerDataStoreArguments.Add(call.GetArgument<int[]?>(4)))
                .Returns(new ClientUpdateResult.Success(Guid.NewGuid()));

            A.CallTo(() =>
                    _applicationRepository.UpdateApplication(
                        A<ApplicationUpdateCommand>.Ignored,
                        A<ApiClientCommand>.Ignored
                    )
                )
                .Returns(new ApplicationUpdateResult.FailureVendorNotFound());

            await ActUpdateAsync();
        }

        [Test]
        public void It_returns_the_unresolved_reference_conflict() =>
            _updateResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);

        [Test]
        public void It_restores_only_the_selected_clients_data_stores()
        {
            _providerDataStoreArguments.Should().HaveCount(2);
            _providerDataStoreArguments[1].Should().Equal(1);
        }
    }

    [TestFixture]
    public class Given_a_failed_application_update_with_a_stale_stored_client : UpdateRollbackTestBase
    {
        private List<string> _deletedClientIds = null!;

        [SetUp]
        public async Task Act()
        {
            _deletedClientIds = [];
            A.CallTo(() => _clientRepository.DeleteClientAsync(A<string>.Ignored))
                .Invokes(call => _deletedClientIds.Add(call.GetArgument<string>(0)!))
                .Returns(new ClientDeleteResult.Success());

            A.CallTo(() =>
                    _applicationRepository.UpdateApplication(
                        A<ApplicationUpdateCommand>.Ignored,
                        A<ApiClientCommand>.Ignored
                    )
                )
                .Returns(new ApplicationUpdateResult.FailureVendorNotFound());

            A.CallTo(() =>
                    _applicationRepository.SyncApplicationApiClientUuid(
                        A<int>.Ignored,
                        A<string>.Ignored,
                        A<Guid>.Ignored,
                        A<Guid>.Ignored
                    )
                )
                .Returns(new ApiClientUuidSyncResult.FailureStaleState());

            await ActUpdateAsync();
        }

        [Test]
        public async Task It_returns_a_sanitized_internal_server_error() =>
            await AssertSanitizedInternalServerError(_updateResponse);

        [Test]
        public void It_deletes_nothing() => _deletedClientIds.Should().BeEmpty();
    }

    [TestFixture]
    public class Given_a_failed_application_update_whose_rollback_client_is_still_referenced
        : UpdateRollbackTestBase
    {
        private List<string> _deletedClientIds = null!;

        [SetUp]
        public async Task Act()
        {
            _deletedClientIds = [];
            A.CallTo(() => _clientRepository.DeleteClientAsync(A<string>.Ignored))
                .Invokes(call => _deletedClientIds.Add(call.GetArgument<string>(0)!))
                .Returns(new ClientDeleteResult.Success());

            A.CallTo(() =>
                    _applicationRepository.UpdateApplication(
                        A<ApplicationUpdateCommand>.Ignored,
                        A<ApiClientCommand>.Ignored
                    )
                )
                .Returns(new ApplicationUpdateResult.FailureVendorNotFound());

            A.CallTo(() =>
                    _applicationRepository.SyncApplicationApiClientUuid(
                        A<int>.Ignored,
                        A<string>.Ignored,
                        A<Guid>.Ignored,
                        A<Guid>.Ignored
                    )
                )
                .Returns(new ApiClientUuidSyncResult.FailureNotExists());

            await ActUpdateAsync();
        }

        [Test]
        public async Task It_returns_a_sanitized_internal_server_error() =>
            await AssertSanitizedInternalServerError(_updateResponse);

        [Test]
        public void It_deletes_nothing() => _deletedClientIds.Should().BeEmpty();
    }

    [TestFixture]
    public class Given_an_ambiguous_update_for_a_vanished_application : ThrownRepositoryExceptionTestBase
    {
        [SetUp]
        public async Task Act()
        {
            ArrangeProviderUpdates();
            A.CallTo(() =>
                    _applicationRepository.GetApplicationUpdateState(A<int>.Ignored, A<string>.Ignored)
                )
                .ReturnsNextFromSequence(
                    new ApplicationUpdateStateResult.Success(_originalState),
                    new ApplicationUpdateStateResult.FailureNotExists()
                );

            await ActUpdateAsync();
        }

        [Test]
        public async Task It_returns_a_sanitized_internal_server_error() =>
            await AssertSanitizedInternalServerError(_updateResponse, Sentinel);

        [Test]
        public void It_deletes_the_recreated_client_after_the_reference_check() =>
            _deletedClientIds.Should().Equal(_updatedUuid.ToString());
    }

    [TestFixture]
    public class Given_a_vanished_application_whose_recreated_client_is_still_referenced
        : UpdateRollbackTestBase
    {
        private List<string> _deletedClientIds = null!;

        [SetUp]
        public async Task Act()
        {
            _deletedClientIds = [];
            A.CallTo(() => _clientRepository.DeleteClientAsync(A<string>.Ignored))
                .Invokes(call => _deletedClientIds.Add(call.GetArgument<string>(0)!))
                .Returns(new ClientDeleteResult.Success());

            A.CallTo(() =>
                    _applicationRepository.UpdateApplication(
                        A<ApplicationUpdateCommand>.Ignored,
                        A<ApiClientCommand>.Ignored
                    )
                )
                .Returns(new ApplicationUpdateResult.FailureNotExists());

            A.CallTo(() => _apiClientRepository.HasApiClientUuidReference(A<Guid>.Ignored))
                .Returns(new ApiClientUuidReferenceResult.Referenced());

            await ActUpdateAsync();
        }

        [Test]
        public async Task It_returns_a_sanitized_internal_server_error() =>
            await AssertSanitizedInternalServerError(_updateResponse);

        [Test]
        public void It_deletes_nothing() => _deletedClientIds.Should().BeEmpty();
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
    public class Given_an_ambiguous_update_resolving_to_a_command_like_partial_state
        : ThrownRepositoryExceptionTestBase
    {
        [SetUp]
        public async Task Act()
        {
            ArrangeProviderUpdates();
            A.CallTo(() =>
                    _applicationRepository.GetApplicationUpdateState(A<int>.Ignored, A<string>.Ignored)
                )
                .ReturnsNextFromSequence(
                    new ApplicationUpdateStateResult.Success(_originalState),
                    new ApplicationUpdateStateResult.Success(
                        CommandMatchingState() with
                        {
                            IsApproved = false,
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
    public class Given_an_ambiguous_update_resolving_to_an_original_like_partial_state
        : ThrownRepositoryExceptionTestBase
    {
        [SetUp]
        public async Task Act()
        {
            ArrangeProviderUpdates();
            A.CallTo(() =>
                    _applicationRepository.GetApplicationUpdateState(A<int>.Ignored, A<string>.Ignored)
                )
                .ReturnsNextFromSequence(
                    new ApplicationUpdateStateResult.Success(_originalState),
                    new ApplicationUpdateStateResult.Success(_originalState with { IsApproved = false })
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
    public class Given_a_failed_application_update_whose_rollback_was_already_synchronized
        : UpdateRollbackTestBase
    {
        private List<string> _deletedClientIds = null!;

        [SetUp]
        public async Task Act()
        {
            _deletedClientIds = [];
            A.CallTo(() => _clientRepository.DeleteClientAsync(A<string>.Ignored))
                .Invokes(call => _deletedClientIds.Add(call.GetArgument<string>(0)!))
                .Returns(new ClientDeleteResult.Success());

            A.CallTo(() =>
                    _applicationRepository.UpdateApplication(
                        A<ApplicationUpdateCommand>.Ignored,
                        A<ApiClientCommand>.Ignored
                    )
                )
                .Returns(new ApplicationUpdateResult.FailureVendorNotFound());

            A.CallTo(() =>
                    _applicationRepository.SyncApplicationApiClientUuid(
                        A<int>.Ignored,
                        A<string>.Ignored,
                        A<Guid>.Ignored,
                        A<Guid>.Ignored
                    )
                )
                .Returns(new ApiClientUuidSyncResult.AlreadyApplied());

            await ActUpdateAsync();
        }

        [Test]
        public void It_returns_the_unresolved_reference_conflict() =>
            _updateResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);

        [Test]
        public void It_deletes_nothing() => _deletedClientIds.Should().BeEmpty();
    }

    [TestFixture]
    public class Given_a_vanished_application_whose_reference_check_fails : UpdateRollbackTestBase
    {
        private const string Sentinel = "SENTINEL_REFERENCE_CHECK_must_not_leak";
        private List<string> _deletedClientIds = null!;

        [SetUp]
        public async Task Act()
        {
            _deletedClientIds = [];
            A.CallTo(() => _clientRepository.DeleteClientAsync(A<string>.Ignored))
                .Invokes(call => _deletedClientIds.Add(call.GetArgument<string>(0)!))
                .Returns(new ClientDeleteResult.Success());

            A.CallTo(() =>
                    _applicationRepository.UpdateApplication(
                        A<ApplicationUpdateCommand>.Ignored,
                        A<ApiClientCommand>.Ignored
                    )
                )
                .Returns(new ApplicationUpdateResult.FailureNotExists());

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
    public class Given_a_vanished_application_whose_client_cleanup_throws : UpdateRollbackTestBase
    {
        private const string Sentinel = "SENTINEL_CLEANUP_THROW_must_not_leak";
        private RecordingLockManager _recordingLockManager = null!;

        [SetUp]
        public async Task Act()
        {
            _recordingLockManager = new RecordingLockManager();
            _lockManager = _recordingLockManager;

            A.CallTo(() =>
                    _applicationRepository.UpdateApplication(
                        A<ApplicationUpdateCommand>.Ignored,
                        A<ApiClientCommand>.Ignored
                    )
                )
                .Returns(new ApplicationUpdateResult.FailureNotExists());

            A.CallTo(() => _clientRepository.DeleteClientAsync(A<string>.Ignored))
                .Throws(new InvalidOperationException(Sentinel));

            await ActUpdateAsync();
        }

        [Test]
        public async Task It_returns_a_sanitized_internal_server_error() =>
            await AssertSanitizedInternalServerError(_updateResponse, Sentinel);

        [Test]
        public void It_releases_the_lock() => _recordingLockManager.Handle.Disposed.Should().BeTrue();
    }
}
