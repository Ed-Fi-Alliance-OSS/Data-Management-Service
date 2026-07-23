// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.DataModel;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.Application;
using EdFi.DmsConfigurationService.DataModel.Model.Authorization;
using EdFi.DmsConfigurationService.DataModel.Model.DataStore;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Configuration;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure.Authorization;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;
using FakeItEasy;
using FluentAssertions;
using FluentValidation;
using FluentValidation.AspNetCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Modules;

public class DataStoreModuleTests
{
    private readonly IDataStoreRepository _dataStoreRepository = A.Fake<IDataStoreRepository>();
    private readonly IConnectionStringEncryptionService _encryptionService =
        A.Fake<IConnectionStringEncryptionService>();

    private static readonly string FakeEncryptedConnection1 = Convert.ToBase64String(
        new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17 }
    );

    private static readonly string FakeEncryptedReplica1 = Convert.ToBase64String(
        new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 18 }
    );

    private static readonly string FakeEncryptedSnapshot1 = Convert.ToBase64String(
        new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 19 }
    );

    private HttpClient SetUpClient()
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(
                (ctx, collection) =>
                {
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

                    // Add FluentValidation services
                    var executingAssembly = Assembly.GetExecutingAssembly();
                    collection
                        .AddValidatorsFromAssembly(executingAssembly)
                        .AddValidatorsFromAssembly(
                            Assembly.Load("EdFi.DmsConfigurationService.DataModel"),
                            ServiceLifetime.Transient
                        )
                        .AddFluentValidationAutoValidation();

                    collection.AddTransient((_) => _dataStoreRepository);
                    collection.AddTransient((_) => _encryptionService);
                }
            );
        });
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Scope", AuthorizationScopes.AdminScope.Name);
        return client;
    }

    [TestFixture]
    public class SuccessTests : DataStoreModuleTests
    {
        [SetUp]
        public void SetUp()
        {
            A.CallTo(() => _dataStoreRepository.InsertDataStore(A<DataStoreInsertCommand>._))
                .Returns(new DataStoreInsertResult.Success(1));

            A.CallTo(() => _dataStoreRepository.QueryDataStore(A<DataStoreQuery>._))
                .Returns(
                    new DataStoreQueryResult.Success([
                        new DataStoreResponse
                        {
                            Id = 1,
                            DataStoreType = "Production",
                            Name = "Test Instance",
                            ConnectionString = FakeEncryptedConnection1,
                            DataStoreDerivatives = [new(1, 1, "ReadReplica", FakeEncryptedReplica1)],
                        },
                    ])
                );

            A.CallTo(() => _dataStoreRepository.GetDataStore(A<long>._))
                .Returns(
                    new DataStoreGetResult.Success(
                        new DataStoreResponse
                        {
                            Id = 1,
                            DataStoreType = "Production",
                            Name = "Test Instance",
                            ConnectionString = FakeEncryptedConnection1,
                            DataStoreContexts =
                            [
                                new(1, 1, "contextKey1", "contextValue1"),
                                new(2, 1, "contextKey2", "contextValue2"),
                            ],
                            DataStoreDerivatives =
                            [
                                new(1, 1, "ReadReplica", FakeEncryptedReplica1),
                                new(2, 1, "Snapshot", FakeEncryptedSnapshot1),
                            ],
                        }
                    )
                );

            A.CallTo(() => _dataStoreRepository.UpdateDataStore(A<DataStoreUpdateCommand>._))
                .Returns(new DataStoreUpdateResult.Success());

            A.CallTo(() => _dataStoreRepository.DeleteDataStore(A<long>._))
                .Returns(new DataStoreDeleteResult.Success());

            A.CallTo(() => _dataStoreRepository.QueryApplicationByDataStore(A<long>._, A<PagingQuery>._))
                .Returns(
                    new ApplicationByDataStoreQueryResult.Success([
                        new ApplicationResponse
                        {
                            Id = 1,
                            ApplicationName = "Test Application 1",
                            VendorId = 1,
                            ClaimSetName = "TestClaimSet1",
                            EducationOrganizationIds = [1, 2, 3],
                            DataStoreIds = [1],
                        },
                    ])
                );
        }

        [Test]
        public async Task Should_return_proper_success_responses()
        {
            using var client = SetUpClient();

            var insertResponse = await client.PostAsync(
                "/v3/dataStores/",
                new StringContent(
                    JsonSerializer.Serialize(
                        new DataStoreInsertCommand
                        {
                            DataStoreType = "Production",
                            Name = "Test Instance",
                            ConnectionString = "Server=localhost;Database=TestDb;",
                        }
                    ),
                    Encoding.UTF8,
                    "application/json"
                )
            );

            var queryResponse = await client.GetAsync("/v3/dataStores/?offset=0&limit=25");
            var getResponse = await client.GetAsync("/v3/dataStores/1");
            var updateResponse = await client.PutAsync(
                "/v3/dataStores/1",
                new StringContent(
                    JsonSerializer.Serialize(
                        new DataStoreUpdateCommand
                        {
                            Id = 1,
                            DataStoreType = "Production",
                            Name = "Updated Instance",
                            ConnectionString = "Server=updated;Database=UpdatedDb;",
                        }
                    ),
                    Encoding.UTF8,
                    "application/json"
                )
            );
            var deleteResponse = await client.DeleteAsync("/v3/dataStores/1");
            var queryApplicationsByDataStoreResponse = await client.GetAsync(
                "/v3/dataStores/1/applications/?offset=0&limit=25"
            );

            insertResponse.StatusCode.Should().Be(HttpStatusCode.Created);
            queryResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            updateResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
            deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
            queryApplicationsByDataStoreResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Test]
        public async Task Should_return_data_store_with_route_contexts()
        {
            using var client = SetUpClient();

            var getResponse = await client.GetAsync("/v3/dataStores/1");
            getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

            var responseContent = await getResponse.Content.ReadAsStringAsync();
            var instance = JsonSerializer.Deserialize<DataStoreResponse>(
                responseContent,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            );

            instance.Should().NotBeNull();
            instance!.DataStoreContexts.Should().HaveCount(2);
            instance
                .DataStoreContexts.Should()
                .Contain(c => c.ContextKey == "contextKey1" && c.ContextValue == "contextValue1");
            instance
                .DataStoreContexts.Should()
                .Contain(c => c.ContextKey == "contextKey2" && c.ContextValue == "contextValue2");
        }

        [Test]
        public async Task Should_return_data_store_with_derivatives()
        {
            using var client = SetUpClient();

            var getResponse = await client.GetAsync("/v3/dataStores/1");
            getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

            var responseContent = await getResponse.Content.ReadAsStringAsync();
            var instance = JsonSerializer.Deserialize<DataStoreResponse>(
                responseContent,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            );

            instance.Should().NotBeNull();
            instance!.DataStoreDerivatives.Should().HaveCount(2);
            instance
                .DataStoreDerivatives.Should()
                .Contain(d =>
                    d.Id == 1
                    && d.DataStoreId == 1
                    && d.DerivativeType == "ReadReplica"
                    && d.ConnectionString == FakeEncryptedReplica1
                );
            instance
                .DataStoreDerivatives.Should()
                .Contain(d =>
                    d.Id == 2
                    && d.DataStoreId == 1
                    && d.DerivativeType == "Snapshot"
                    && d.ConnectionString == FakeEncryptedSnapshot1
                );
        }
    }

    [TestFixture]
    public class FailureValidationTests : DataStoreModuleTests
    {
        [Test]
        public async Task Should_return_bad_request()
        {
            using var client = SetUpClient();

            var invalidPostBody = JsonSerializer.Serialize(
                new DataStoreInsertCommand
                {
                    DataStoreType = "", // Invalid - empty
                    Name = "", // Invalid - empty
                    ConnectionString = new string('x', 1001), // Invalid - too long
                }
            );

            var invalidPutBody = JsonSerializer.Serialize(
                new DataStoreUpdateCommand
                {
                    Id = 1,
                    DataStoreType = "", // Invalid - empty
                    Name = "", // Invalid - empty
                    ConnectionString = new string('x', 1001), // Invalid - too long
                }
            );

            var postResponse = await client.PostAsync(
                "/v3/dataStores/",
                new StringContent(invalidPostBody, Encoding.UTF8, "application/json")
            );

            var putResponse = await client.PutAsync(
                "/v3/dataStores/1",
                new StringContent(invalidPutBody, Encoding.UTF8, "application/json")
            );

            postResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            putResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Test]
        public async Task Should_return_bad_request_when_id_mismatch()
        {
            using var client = SetUpClient();

            var response = await client.PutAsync(
                "/v3/dataStores/1",
                new StringContent(
                    JsonSerializer.Serialize(
                        new DataStoreUpdateCommand
                        {
                            Id = 2, // Mismatch with URL
                            DataStoreType = "Production",
                            Name = "Test Instance",
                            ConnectionString = "Server=localhost;Database=TestDb;",
                        }
                    ),
                    Encoding.UTF8,
                    "application/json"
                )
            );

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
    }

    [TestFixture]
    public class FailureNotFoundTests : DataStoreModuleTests
    {
        [SetUp]
        public void SetUp()
        {
            A.CallTo(() => _dataStoreRepository.GetDataStore(A<long>._))
                .Returns(new DataStoreGetResult.FailureNotFound());

            A.CallTo(() => _dataStoreRepository.UpdateDataStore(A<DataStoreUpdateCommand>._))
                .Returns(new DataStoreUpdateResult.FailureNotExists());

            A.CallTo(() => _dataStoreRepository.DeleteDataStore(A<long>._))
                .Returns(new DataStoreDeleteResult.FailureNotExists());

            A.CallTo(() => _dataStoreRepository.QueryApplicationByDataStore(A<long>._, A<PagingQuery>._))
                .Returns(new ApplicationByDataStoreQueryResult.FailureNotExists());
        }

        [Test]
        public async Task Should_return_proper_not_found_responses()
        {
            using var client = SetUpClient();

            var getResponse = await client.GetAsync("/v3/dataStores/999");
            var updateResponse = await client.PutAsync(
                "/v3/dataStores/999",
                new StringContent(
                    JsonSerializer.Serialize(
                        new DataStoreUpdateCommand
                        {
                            Id = 999,
                            DataStoreType = "Production",
                            Name = "Test Instance",
                            ConnectionString = "Server=localhost;Database=TestDb;",
                        }
                    ),
                    Encoding.UTF8,
                    "application/json"
                )
            );
            var deleteResponse = await client.DeleteAsync("/v3/dataStores/999");
            var queryApplicationsByDataStoreResponse = await client.GetAsync(
                "/v3/dataStores/0/applications/?offset=0&limit=25"
            );

            getResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
            updateResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
            deleteResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
            queryApplicationsByDataStoreResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        [Test]
        public async Task It_returns_the_problem_details_not_found_contract()
        {
            using var client = SetUpClient();

            var response = await client.GetAsync("/v3/dataStores/999");

            JsonNode body = await response.ShouldBeProblemDetailAsync(
                HttpStatusCode.NotFound,
                "urn:ed-fi:api:not-found",
                "Not Found",
                "DataStore 999 not found. It may have been recently deleted."
            );
            body["validationErrors"]!.AsObject().Count.Should().Be(0);
            body["errors"]!.AsArray().Count.Should().Be(0);
        }

        [Test]
        public async Task It_returns_the_problem_details_not_found_contract_for_update()
        {
            using var client = SetUpClient();

            var response = await client.PutAsync(
                "/v3/dataStores/999",
                new StringContent(
                    JsonSerializer.Serialize(
                        new DataStoreUpdateCommand
                        {
                            Id = 999,
                            DataStoreType = "Production",
                            Name = "Test Instance",
                            ConnectionString = "Server=localhost;Database=TestDb;",
                        }
                    ),
                    Encoding.UTF8,
                    "application/json"
                )
            );

            JsonNode body = await response.ShouldBeProblemDetailAsync(
                HttpStatusCode.NotFound,
                "urn:ed-fi:api:not-found",
                "Not Found",
                "DataStore 999 not found. It may have been recently deleted."
            );
            body["validationErrors"]!.AsObject().Count.Should().Be(0);
            body["errors"]!.AsArray().Count.Should().Be(0);
        }

        [Test]
        public async Task It_returns_the_problem_details_not_found_contract_for_delete()
        {
            using var client = SetUpClient();

            var response = await client.DeleteAsync("/v3/dataStores/999");

            JsonNode body = await response.ShouldBeProblemDetailAsync(
                HttpStatusCode.NotFound,
                "urn:ed-fi:api:not-found",
                "Not Found",
                "DataStore 999 not found. It may have been recently deleted."
            );
            body["validationErrors"]!.AsObject().Count.Should().Be(0);
            body["errors"]!.AsArray().Count.Should().Be(0);
        }

        [Test]
        public async Task It_returns_the_problem_details_not_found_contract_for_applications_query()
        {
            using var client = SetUpClient();

            var response = await client.GetAsync("/v3/dataStores/0/applications/?offset=0&limit=25");

            JsonNode body = await response.ShouldBeProblemDetailAsync(
                HttpStatusCode.NotFound,
                "urn:ed-fi:api:not-found",
                "Not Found",
                "DataStore 0 not found. It may have been recently deleted."
            );
            body["validationErrors"]!.AsObject().Count.Should().Be(0);
            body["errors"]!.AsArray().Count.Should().Be(0);
        }
    }

    [TestFixture]
    public class Given_Invalid_PagingQuery : DataStoreModuleTests
    {
        [SetUp]
        public void SetUp()
        {
            A.CallTo(() => _dataStoreRepository.QueryDataStore(A<DataStoreQuery>.Ignored))
                .Returns(new DataStoreQueryResult.Success([]));
        }

        [Test]
        public async Task Should_return_400_when_orderBy_is_invalid()
        {
            using var client = SetUpClient();
            var response = await client.GetAsync("/v3/dataStores?orderBy=invalidField");
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Test]
        public async Task Should_return_400_when_direction_is_invalid()
        {
            using var client = SetUpClient();
            var response = await client.GetAsync("/v3/dataStores?orderBy=id&direction=SIDEWAYS");
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Test]
        public async Task Should_return_400_when_offset_is_negative()
        {
            using var client = SetUpClient();
            var response = await client.GetAsync("/v3/dataStores?offset=-1");
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Test]
        public async Task Should_return_400_when_limit_is_zero()
        {
            using var client = SetUpClient();
            var response = await client.GetAsync("/v3/dataStores?limit=0");
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Test]
        public async Task Should_return_parameter_validation_failure_when_limit_is_zero()
        {
            using var client = SetUpClient();
            var response = await client.GetAsync("/v3/dataStores?limit=0");
            await response.ShouldBeProblemDetailAsync(
                HttpStatusCode.BadRequest,
                "urn:ed-fi:api:bad-request:parameter",
                "Parameter Validation Failed",
                "One or more query parameters were invalid. See 'errors' for details.",
                errors: ["'limit' must be greater than 0."]
            );
        }

        [Test]
        public async Task Should_return_200_with_valid_orderBy_and_direction()
        {
            using var client = SetUpClient();
            var response = await client.GetAsync("/v3/dataStores?orderBy=name&direction=ASC");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Test]
        public async Task Should_return_200_when_filter_name_is_provided()
        {
            using var client = SetUpClient();
            var response = await client.GetAsync("/v3/dataStores?name=MyInstance");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Test]
        public async Task Should_return_200_when_filter_dataStoreType_is_provided()
        {
            using var client = SetUpClient();
            var response = await client.GetAsync("/v3/dataStores?dataStoreType=SQL");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Test]
        public async Task Should_return_400_when_offset_is_non_numeric()
        {
            using var client = SetUpClient();
            var response = await client.GetAsync("/v3/dataStores?offset=abc");
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Test]
        public async Task Should_return_400_when_limit_is_non_numeric()
        {
            using var client = SetUpClient();
            var response = await client.GetAsync("/v3/dataStores?limit=xyz");
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Test]
        public async Task Should_return_200_when_orderBy_omitted_with_direction()
        {
            using var client = SetUpClient();
            var response = await client.GetAsync("/v3/dataStores?direction=asc");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }
}
