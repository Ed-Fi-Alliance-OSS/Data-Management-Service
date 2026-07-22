// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.Authorization;
using EdFi.DmsConfigurationService.DataModel.Model.DataStoreContext;
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

public class DataStoreContextModuleTests
{
    private readonly IDataStoreContextRepository _repository = A.Fake<IDataStoreContextRepository>();

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
    public class Given_Invalid_PagingQuery : DataStoreContextModuleTests
    {
        [SetUp]
        public void SetUp()
        {
            A.CallTo(() => _repository.QueryDataStoreContext(A<PagingQuery>.Ignored))
                .Returns(new DataStoreContextQueryResult.Success([]));
        }

        [Test]
        public async Task Should_return_400_when_orderBy_is_invalid()
        {
            using var client = SetUpClient();
            var response = await client.GetAsync("/v3/dataStoreContexts?orderBy=invalidField");
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Test]
        public async Task Should_return_400_when_direction_is_invalid()
        {
            using var client = SetUpClient();
            var response = await client.GetAsync("/v3/dataStoreContexts?orderBy=id&direction=SIDEWAYS");
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Test]
        public async Task Should_return_400_when_offset_is_negative()
        {
            using var client = SetUpClient();
            var response = await client.GetAsync("/v3/dataStoreContexts?offset=-1");
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Test]
        public async Task Should_return_400_when_limit_is_zero()
        {
            using var client = SetUpClient();
            var response = await client.GetAsync("/v3/dataStoreContexts?limit=0");
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Test]
        public async Task Should_return_400_when_offset_is_non_numeric()
        {
            using var client = SetUpClient();
            var response = await client.GetAsync("/v3/dataStoreContexts?offset=abc");
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Test]
        public async Task Should_return_400_when_limit_is_non_numeric()
        {
            using var client = SetUpClient();
            var response = await client.GetAsync("/v3/dataStoreContexts?limit=xyz");
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
    }

    [TestFixture]
    public class Given_A_Duplicate_DataStoreContext : DataStoreContextModuleTests
    {
        [SetUp]
        public void SetUp()
        {
            A.CallTo(() => _repository.InsertDataStoreContext(A<DataStoreContextInsertCommand>.Ignored))
                .Returns(new DataStoreContextInsertResult.FailureDuplicateDataStoreContext(1, "grade-level"));
        }

        [Test]
        public async Task It_returns_the_non_unique_identity_conflict()
        {
            using var client = SetUpClient();

            var response = await client.PostAsync(
                "/v3/dataStoreContexts",
                new StringContent(
                    """
                    {
                      "dataStoreId": 1,
                      "contextKey": "grade-level",
                      "contextValue": "5"
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );

            await response.ShouldBeProblemDetailAsync(
                HttpStatusCode.Conflict,
                "urn:ed-fi:api:conflict:non-unique-identity",
                "Identifying Values Are Not Unique",
                "The identifying value(s) of the item are the same as another item that already exists.",
                errors:
                [
                    "Data store context with DataStoreId '1' and ContextKey 'grade-level' already exists.",
                ]
            );
        }
    }

    [TestFixture]
    public class Given_A_Duplicate_DataStoreContext_On_Update : DataStoreContextModuleTests
    {
        [SetUp]
        public void SetUp()
        {
            A.CallTo(() => _repository.UpdateDataStoreContext(A<DataStoreContextUpdateCommand>.Ignored))
                .Returns(new DataStoreContextUpdateResult.FailureDuplicateDataStoreContext(1, "grade-level"));
        }

        [Test]
        public async Task It_returns_the_non_unique_identity_conflict()
        {
            using var client = SetUpClient();

            var response = await client.PutAsync(
                "/v3/dataStoreContexts/1",
                new StringContent(
                    """
                    {
                      "id": 1,
                      "dataStoreId": 1,
                      "contextKey": "grade-level",
                      "contextValue": "5"
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );

            await response.ShouldBeProblemDetailAsync(
                HttpStatusCode.Conflict,
                "urn:ed-fi:api:conflict:non-unique-identity",
                "Identifying Values Are Not Unique",
                "The identifying value(s) of the item are the same as another item that already exists.",
                errors:
                [
                    "Data store context with DataStoreId '1' and ContextKey 'grade-level' already exists.",
                ]
            );
        }
    }
}
