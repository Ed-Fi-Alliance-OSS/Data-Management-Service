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
    private readonly IIdentityProviderRepository _clientRepository = A.Fake<IIdentityProviderRepository>();
    private readonly IDataStoreRepository _dataStoreRepository = A.Fake<IDataStoreRepository>();
    private readonly IVendorRepository _vendorRepository = A.Fake<IVendorRepository>();

    public ApplicationModuleTests()
    {
        A.CallTo(() => _dataStoreRepository.GetExistingDataStoreIds(A<long[]>.Ignored))
            .ReturnsLazily(call =>
            {
                long[] ids = call.GetArgument<long[]>(0) ?? [];
                return Task.FromResult<DataStoreIdsExistResult>(
                    new DataStoreIdsExistResult.Success([.. ids])
                );
            });
    }

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
                    .AddTransient((_) => _clientRepository)
                    .AddTransient((_) => _dataStoreRepository)
                    .AddTransient((_) => _vendorRepository);
            });
        });
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
            A.CallTo(() => _vendorRepository.GetVendor(A<long>.Ignored))
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

            A.CallTo(() => _applicationRepository.DeleteApplication(A<long>.Ignored))
                .Returns(new ApplicationDeleteResult.Success());

            A.CallTo(() => _applicationRepository.GetApplicationApiClients(A<long>.Ignored))
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
                        A<long[]?>.Ignored,
                        A<bool>.Ignored
                    )
                )
                .Returns(new ClientCreateResult.Success(Guid.NewGuid()));

            A.CallTo(() => _clientRepository.ResetCredentialsAsync(A<string>.Ignored))
                .Returns(new ClientResetResult.Success("SECRET"));

            A.CallTo(() =>
                    _clientRepository.UpdateClientAsync(
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

            A.CallTo(() => _applicationRepository.GetApplication(A<long>.Ignored))
                .Returns(new ApplicationGetResult.FailureNotFound());

            A.CallTo(() =>
                    _applicationRepository.UpdateApplication(
                        A<ApplicationUpdateCommand>.Ignored,
                        A<ApiClientCommand>.Ignored
                    )
                )
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
            A.CallTo(() => _dataStoreRepository.GetExistingDataStoreIds(A<long[]>.Ignored))
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
                        A<long[]?>.Ignored,
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
                        A<long[]?>.Ignored,
                        A<bool>.Ignored
                    )
                )
                .Returns(new ClientCreateResult.Success(Guid.NewGuid()));

            A.CallTo(() => _vendorRepository.GetVendor(A<long>.Ignored))
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

            A.CallTo(() => _applicationRepository.GetApplication(A<long>.Ignored))
                .Returns(new ApplicationGetResult.FailureUnknown(""));

            A.CallTo(() =>
                    _applicationRepository.UpdateApplication(
                        A<ApplicationUpdateCommand>.Ignored,
                        A<ApiClientCommand>.Ignored
                    )
                )
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

            A.CallTo(() => _vendorRepository.GetVendor(A<long>.Ignored))
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

            A.CallTo(() => _applicationRepository.GetApplication(A<long>.Ignored))
                .Returns(new ApplicationGetResult());

            A.CallTo(() =>
                    _applicationRepository.UpdateApplication(
                        A<ApplicationUpdateCommand>.Ignored,
                        A<ApiClientCommand>.Ignored
                    )
                )
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
                        A<long[]?>.Ignored,
                        A<bool>.Ignored
                    )
                )
                .Returns(new ClientCreateResult.Success(Guid.NewGuid()));

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

            A.CallTo(() => _applicationRepository.GetApplicationApiClients(A<long>.Ignored))
                .Returns(
                    new ApplicationApiClientsResult.Success([new ApiClient("111", Guid.NewGuid(), true)])
                );

            A.CallTo(() =>
                    _clientRepository.UpdateClientAsync(
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
        }

        [Test]
        public async Task Should_return_bad_request_due_to_invalid_vendor_id_on_insert()
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

            addResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            string responseBody = await addResponse.Content.ReadAsStringAsync();
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
        public async Task Should_not_update_identity_provider_when_update_vendor_id_is_invalid()
        {
            // Arrange
            A.CallTo(() => _vendorRepository.GetVendor(A<long>.Ignored))
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
            updateResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            A.CallTo(() =>
                    _clientRepository.UpdateClientAsync(
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<long[]?>.Ignored,
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
                        A<long[]?>.Ignored,
                        A<bool>.Ignored
                    )
                )
                .Returns(new ClientCreateResult.Success(Guid.NewGuid()));

            A.CallTo(() => _vendorRepository.GetVendor(A<long>.Ignored))
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

            A.CallTo(() => _applicationRepository.GetApplicationApiClients(A<long>.Ignored))
                .Returns(
                    new ApplicationApiClientsResult.Success([new ApiClient("clientId", Guid.NewGuid(), true)])
                );

            A.CallTo(() =>
                    _clientRepository.UpdateClientAsync(
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

            // Assert
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
                        A<long[]?>.Ignored,
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
                        A<long[]?>.Ignored,
                        A<bool>.Ignored
                    )
                )
                .Returns(new ClientCreateResult.Success(Guid.NewGuid()));

            A.CallTo(() => _vendorRepository.GetVendor(A<long>.Ignored))
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
                .Returns(new ApplicationInsertResult.FailureDataStoreNotFound());

            A.CallTo(() =>
                    _applicationRepository.UpdateApplication(
                        A<ApplicationUpdateCommand>.Ignored,
                        A<ApiClientCommand>.Ignored
                    )
                )
                .Returns(new ApplicationUpdateResult.FailureDataStoreNotFound());

            A.CallTo(() => _applicationRepository.GetApplicationApiClients(A<long>.Ignored))
                .Returns(
                    new ApplicationApiClientsResult.Success([new ApiClient("clientId", Guid.NewGuid(), true)])
                );

            A.CallTo(() =>
                    _clientRepository.UpdateClientAsync(
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
        }

        [Test]
        public async Task Should_return_bad_request_for_invalid_data_store_id_on_insert()
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
                        "DataStoreIds": [999]
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
                    "DataStoreId": [
                      "Data store does not exist."
                    ]
                  },
                  "errors": []
                }
                """.Replace("{correlationId}", actualResponse!["correlationId"]!.GetValue<string>())
            );
            JsonNode.DeepEquals(actualResponse, expectedResponse).Should().Be(true);
        }

        [Test]
        public async Task Should_return_bad_request_for_invalid_data_store_id_on_update()
        {
            // Arrange
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
                    "DataStoreId": [
                      "Data store does not exist."
                    ]
                  },
                  "errors": []
                }
                """.Replace("{correlationId}", actualResponse!["correlationId"]!.GetValue<string>())
            );
            JsonNode.DeepEquals(actualResponse, expectedResponse).Should().Be(true);
        }

        [Test]
        public async Task Should_not_create_identity_provider_client_when_insert_data_store_id_is_invalid()
        {
            // Arrange
            A.CallTo(() =>
                    _dataStoreRepository.GetExistingDataStoreIds(
                        A<long[]>.That.Matches(ids => ids.Length == 1 && ids[0] == 999L)
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
            insertResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            A.CallTo(() =>
                    _clientRepository.CreateClientAsync(
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
                        A<long[]>.That.Matches(ids => ids.Length == 1 && ids[0] == 999L)
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
            updateResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            A.CallTo(() =>
                    _clientRepository.UpdateClientAsync(
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<long[]?>.Ignored,
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
                        A<long[]?>.Ignored,
                        A<bool>.Ignored
                    )
                )
                .Returns(new ClientCreateResult.Success(Guid.NewGuid()));

            A.CallTo(() => _vendorRepository.GetVendor(A<long>.Ignored))
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

            A.CallTo(() => _applicationRepository.GetApplicationApiClients(A<long>.Ignored))
                .Returns(
                    new ApplicationApiClientsResult.Success([new ApiClient("clientId", Guid.NewGuid(), true)])
                );

            A.CallTo(() =>
                    _clientRepository.UpdateClientAsync(
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
        }

        [Test]
        public async Task Should_return_bad_request_for_invalid_profile_id_on_insert()
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
                        "DataStoreIds": [1],
                        "ProfileIds": [999]
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
                    "ProfileId": [
                      "Profile does not exist."
                    ]
                  },
                  "errors": []
                }
                """.Replace("{correlationId}", actualResponse!["correlationId"]!.GetValue<string>())
            );
            JsonNode.DeepEquals(actualResponse, expectedResponse).Should().Be(true);
        }

        [Test]
        public async Task Should_return_bad_request_for_invalid_profile_id_on_update()
        {
            // Arrange
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
                    "ProfileId": [
                      "Profile does not exist."
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
    public class ResetCredentialEndpointEnabledTests : ApplicationModuleTests
    {
        /// <summary>
        /// Tests that verify the reset-credential endpoint is available when
        /// EnableApplicationResetEndpoint is true.
        /// </summary>
        [SetUp]
        public void SetUp()
        {
            A.CallTo(() => _applicationRepository.GetApplicationApiClients(A<long>.Ignored))
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
            actualResponse!["id"]!.GetValue<long>().Should().Be(1);
            actualResponse!["key"]!.GetValue<string>().Should().Be("1");
            actualResponse!["secret"]!.GetValue<string>().Should().Be("NEW_SECRET");
        }

        [Test]
        public async Task Should_return_not_found_when_application_has_no_api_clients()
        {
            // Arrange
            using var client = SetUpClient();
            A.CallTo(() => _applicationRepository.GetApplicationApiClients(A<long>.Ignored))
                .Returns(new ApplicationApiClientsResult.Success([]));

            // Act
            var resetResponse = await client.PutAsync("/v3/applications/1/reset-credential", null);

            // Assert
            resetResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        [Test]
        public async Task Should_return_not_found_when_application_client_is_missing_in_identity_provider()
        {
            using var client = SetUpClient();
            A.CallTo(() => _clientRepository.ResetCredentialsAsync(A<string>.Ignored))
                .Returns(new ClientResetResult.FailureClientNotFound("Client not found"));

            var resetResponse = await client.PutAsync("/v3/applications/1/reset-credential", null);

            resetResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
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

        [Test]
        public async Task Should_return_400_when_orderBy_is_invalid()
        {
            using var client = SetUpClient();
            var response = await client.GetAsync("/v3/applications?orderBy=invalidField");
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Test]
        public async Task Should_return_400_when_direction_is_invalid()
        {
            using var client = SetUpClient();
            var response = await client.GetAsync("/v3/applications?orderBy=id&direction=SIDEWAYS");
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Test]
        public async Task Should_return_400_when_offset_is_negative()
        {
            using var client = SetUpClient();
            var response = await client.GetAsync("/v3/applications?offset=-1");
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Test]
        public async Task Should_return_400_when_limit_is_zero()
        {
            using var client = SetUpClient();
            var response = await client.GetAsync("/v3/applications?limit=0");
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
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
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Test]
        public async Task Should_return_400_when_limit_is_non_numeric()
        {
            using var client = SetUpClient();
            var response = await client.GetAsync("/v3/applications?limit=xyz");
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
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
                        .AddTransient((_) => _vendorRepository);
                });
            });
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
            A.CallTo(() => _applicationRepository.GetApplication(A<long>.Ignored))
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
}
