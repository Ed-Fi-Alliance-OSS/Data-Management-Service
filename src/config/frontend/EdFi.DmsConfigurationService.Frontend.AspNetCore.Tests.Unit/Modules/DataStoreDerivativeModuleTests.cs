// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.Authorization;
using EdFi.DmsConfigurationService.DataModel.Model.DataStoreDerivative;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Configuration;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure.Authorization;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Modules;

public class DataStoreDerivativeModuleTests
{
    private readonly IDataStoreDerivativeRepository _repository = A.Fake<IDataStoreDerivativeRepository>();

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

                    collection.AddTransient((_) => _repository);
                }
            );
        });
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Scope", AuthorizationScopes.AdminScope.Name);
        return client;
    }

    [TestFixture]
    public class Given_Invalid_PagingQuery : DataStoreDerivativeModuleTests
    {
        [SetUp]
        public void SetUp()
        {
            A.CallTo(() => _repository.QueryDataStoreDerivative(A<PagingQuery>.Ignored))
                .Returns(new DataStoreDerivativeQueryResult.Success([]));
        }

        [Test]
        public async Task Should_return_400_when_orderBy_is_invalid()
        {
            using var client = SetUpClient();
            var response = await client.GetAsync("/v3/dataStoreDerivatives?orderBy=invalidField");
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Test]
        public async Task Should_return_400_when_direction_is_invalid()
        {
            using var client = SetUpClient();
            var response = await client.GetAsync("/v3/dataStoreDerivatives?orderBy=id&direction=SIDEWAYS");
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Test]
        public async Task Should_return_400_when_offset_is_negative()
        {
            using var client = SetUpClient();
            var response = await client.GetAsync("/v3/dataStoreDerivatives?offset=-1");
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Test]
        public async Task Should_return_400_when_limit_is_zero()
        {
            using var client = SetUpClient();
            var response = await client.GetAsync("/v3/dataStoreDerivatives?limit=0");
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Test]
        public async Task Should_return_parameter_validation_failure_when_limit_is_zero()
        {
            using var client = SetUpClient();
            var response = await client.GetAsync("/v3/dataStoreDerivatives?limit=0");
            await response.ShouldBeProblemDetailAsync(
                HttpStatusCode.BadRequest,
                "urn:ed-fi:api:bad-request:parameter",
                "Parameter Validation Failed",
                "One or more query parameters were invalid. See 'errors' for details.",
                errors: ["'limit' must be greater than 0."]
            );
        }

        [Test]
        public async Task Should_return_400_when_offset_is_non_numeric()
        {
            using var client = SetUpClient();
            var response = await client.GetAsync("/v3/dataStoreDerivatives?offset=abc");
            await response.ShouldBeProblemDetailAsync(
                HttpStatusCode.BadRequest,
                "urn:ed-fi:api:bad-request:parameter",
                "Parameter Validation Failed",
                "One or more query parameters were invalid. See 'errors' for details.",
                errors: ["'offset' must be an integer."]
            );
            (await response.Content.ReadAsStringAsync()).Should().NotContain("abc");
        }

        [Test]
        public async Task Should_return_400_when_limit_is_non_numeric()
        {
            using var client = SetUpClient();
            var response = await client.GetAsync("/v3/dataStoreDerivatives?limit=xyz");
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
    }

    [TestFixture]
    public class Given_A_Missing_DataStoreDerivative : DataStoreDerivativeModuleTests
    {
        [SetUp]
        public void SetUp()
        {
            A.CallTo(() => _repository.GetDataStoreDerivative(A<long>._))
                .Returns(new DataStoreDerivativeGetResult.FailureNotFound());

            A.CallTo(() => _repository.UpdateDataStoreDerivative(A<DataStoreDerivativeUpdateCommand>._))
                .Returns(new DataStoreDerivativeUpdateResult.FailureNotFound());

            A.CallTo(() => _repository.DeleteDataStoreDerivative(A<long>._))
                .Returns(new DataStoreDerivativeDeleteResult.FailureNotFound());
        }

        [Test]
        public async Task It_returns_the_problem_details_not_found_contract_for_get_by_id()
        {
            using var client = SetUpClient();

            var response = await client.GetAsync("/v3/dataStoreDerivatives/999");

            JsonNode body = await response.ShouldBeProblemDetailAsync(
                HttpStatusCode.NotFound,
                "urn:ed-fi:api:not-found",
                "Not Found",
                "DataStoreDerivative 999 not found. It may have been recently deleted."
            );
            body["validationErrors"]!.AsObject().Count.Should().Be(0);
            body["errors"]!.AsArray().Count.Should().Be(0);
        }

        [Test]
        public async Task It_returns_the_problem_details_not_found_contract_for_update()
        {
            using var client = SetUpClient();

            var response = await client.PutAsync(
                "/v3/dataStoreDerivatives/999",
                new StringContent(
                    JsonSerializer.Serialize(
                        new DataStoreDerivativeUpdateCommand
                        {
                            Id = 999,
                            DataStoreId = 1,
                            DerivativeType = "ReadReplica",
                            ConnectionString = "Server=localhost;Database=ReplicaDb;",
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
                "DataStoreDerivative 999 not found. It may have been recently deleted."
            );
            body["validationErrors"]!.AsObject().Count.Should().Be(0);
            body["errors"]!.AsArray().Count.Should().Be(0);
        }

        [Test]
        public async Task It_returns_the_problem_details_not_found_contract_for_delete()
        {
            using var client = SetUpClient();

            var response = await client.DeleteAsync("/v3/dataStoreDerivatives/999");

            JsonNode body = await response.ShouldBeProblemDetailAsync(
                HttpStatusCode.NotFound,
                "urn:ed-fi:api:not-found",
                "Not Found",
                "DataStoreDerivative 999 not found. It may have been recently deleted."
            );
            body["validationErrors"]!.AsObject().Count.Should().Be(0);
            body["errors"]!.AsArray().Count.Should().Be(0);
        }
    }

    [TestFixture]
    public class Given_A_DataStoreDerivative_Insert_Referencing_A_Missing_DataStore
        : DataStoreDerivativeModuleTests
    {
        [SetUp]
        public void SetUp() =>
            A.CallTo(() => _repository.InsertDataStoreDerivative(A<DataStoreDerivativeInsertCommand>._))
                .Returns(new DataStoreDerivativeInsertResult.FailureForeignKeyViolation());

        [Test]
        public async Task It_returns_the_unresolved_reference_conflict_contract()
        {
            using var client = SetUpClient();

            var response = await client.PostAsync(
                "/v3/dataStoreDerivatives/",
                new StringContent(
                    JsonSerializer.Serialize(
                        new DataStoreDerivativeInsertCommand
                        {
                            DataStoreId = 1,
                            DerivativeType = "ReadReplica",
                            ConnectionString = "Server=localhost;Database=ReplicaDb;",
                        }
                    ),
                    Encoding.UTF8,
                    "application/json"
                )
            );

            JsonNode body = await response.ShouldBeProblemDetailAsync(
                HttpStatusCode.Conflict,
                "urn:ed-fi:api:conflict:unresolved-reference",
                "Unresolved Reference",
                "One or more referenced items could not be resolved. See 'errors' for details.",
                errors: ["The specified DataStore does not exist."]
            );
            body["validationErrors"]!.AsObject().Count.Should().Be(0);
            body["errors"]!.AsArray().Count.Should().Be(1);
        }
    }

    [TestFixture]
    public class Given_A_DataStoreDerivative_Update_Referencing_A_Missing_DataStore
        : DataStoreDerivativeModuleTests
    {
        [SetUp]
        public void SetUp() =>
            A.CallTo(() => _repository.UpdateDataStoreDerivative(A<DataStoreDerivativeUpdateCommand>._))
                .Returns(new DataStoreDerivativeUpdateResult.FailureForeignKeyViolation());

        [Test]
        public async Task It_returns_the_unresolved_reference_conflict_contract()
        {
            using var client = SetUpClient();

            var response = await client.PutAsync(
                "/v3/dataStoreDerivatives/999",
                new StringContent(
                    JsonSerializer.Serialize(
                        new DataStoreDerivativeUpdateCommand
                        {
                            Id = 999,
                            DataStoreId = 1,
                            DerivativeType = "ReadReplica",
                            ConnectionString = "Server=localhost;Database=ReplicaDb;",
                        }
                    ),
                    Encoding.UTF8,
                    "application/json"
                )
            );

            JsonNode body = await response.ShouldBeProblemDetailAsync(
                HttpStatusCode.Conflict,
                "urn:ed-fi:api:conflict:unresolved-reference",
                "Unresolved Reference",
                "One or more referenced items could not be resolved. See 'errors' for details.",
                errors: ["The specified DataStore does not exist."]
            );
            body["validationErrors"]!.AsObject().Count.Should().Be(0);
            body["errors"]!.AsArray().Count.Should().Be(1);
        }
    }
}
