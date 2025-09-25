// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.DataModel;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.Authorization;
using EdFi.DmsConfigurationService.DataModel.Model.DmsInstance;
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

public class DmsInstanceModuleTests
{
    private readonly IDmsInstanceRepository _dmsInstanceRepository = A.Fake<IDmsInstanceRepository>();
    private readonly IConnectionStringEncryptionService _encryptionService =
        A.Fake<IConnectionStringEncryptionService>();

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

                    collection.AddTransient((_) => _dmsInstanceRepository);
                    collection.AddTransient((_) => _encryptionService);
                }
            );
        });
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Scope", AuthorizationScopes.AdminScope.Name);
        return client;
    }

    [TestFixture]
    public class SuccessTests : DmsInstanceModuleTests
    {
        [SetUp]
        public void SetUp()
        {
            A.CallTo(() => _dmsInstanceRepository.InsertDmsInstance(A<DmsInstanceInsertCommand>._))
                .Returns(new DmsInstanceInsertResult.Success(1));

            A.CallTo(() => _dmsInstanceRepository.QueryDmsInstance(A<PagingQuery>._))
                .Returns(
                    new DmsInstanceQueryResult.Success(
                        [
                            new DmsInstanceResponse
                            {
                                Id = 1,
                                InstanceType = "Production",
                                InstanceName = "Test Instance",
                                ConnectionString = "Server=localhost;Database=TestDb;",
                            },
                        ]
                    )
                );

            A.CallTo(() => _dmsInstanceRepository.GetDmsInstance(A<long>._))
                .Returns(
                    new DmsInstanceGetResult.Success(
                        new DmsInstanceResponse
                        {
                            Id = 1,
                            InstanceType = "Production",
                            InstanceName = "Test Instance",
                            ConnectionString = "Server=localhost;Database=TestDb;",
                        }
                    )
                );

            A.CallTo(() => _dmsInstanceRepository.UpdateDmsInstance(A<DmsInstanceUpdateCommand>._))
                .Returns(new DmsInstanceUpdateResult.Success());

            A.CallTo(() => _dmsInstanceRepository.DeleteDmsInstance(A<long>._))
                .Returns(new DmsInstanceDeleteResult.Success());
        }

        [Test]
        public async Task Should_return_proper_success_responses()
        {
            using var client = SetUpClient();

            var insertResponse = await client.PostAsync(
                "/v2/dmsInstances/",
                new StringContent(
                    JsonSerializer.Serialize(
                        new DmsInstanceInsertCommand
                        {
                            InstanceType = "Production",
                            InstanceName = "Test Instance",
                            ConnectionString = "Server=localhost;Database=TestDb;",
                        }
                    ),
                    Encoding.UTF8,
                    "application/json"
                )
            );

            var queryResponse = await client.GetAsync("/v2/dmsInstances/?offset=0&limit=25");
            var getResponse = await client.GetAsync("/v2/dmsInstances/1");
            var updateResponse = await client.PutAsync(
                "/v2/dmsInstances/1",
                new StringContent(
                    JsonSerializer.Serialize(
                        new DmsInstanceUpdateCommand
                        {
                            Id = 1,
                            InstanceType = "Production",
                            InstanceName = "Updated Instance",
                            ConnectionString = "Server=updated;Database=UpdatedDb;",
                        }
                    ),
                    Encoding.UTF8,
                    "application/json"
                )
            );
            var deleteResponse = await client.DeleteAsync("/v2/dmsInstances/1");

            insertResponse.StatusCode.Should().Be(HttpStatusCode.Created);
            queryResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            updateResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
            deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }
    }

    [TestFixture]
    public class FailureValidationTests : DmsInstanceModuleTests
    {
        [Test]
        public async Task Should_return_bad_request()
        {
            using var client = SetUpClient();

            var invalidPostBody = JsonSerializer.Serialize(
                new DmsInstanceInsertCommand
                {
                    InstanceType = "", // Invalid - empty
                    InstanceName = "", // Invalid - empty
                    ConnectionString = new string('x', 1001), // Invalid - too long
                }
            );

            var invalidPutBody = JsonSerializer.Serialize(
                new DmsInstanceUpdateCommand
                {
                    Id = 1,
                    InstanceType = "", // Invalid - empty
                    InstanceName = "", // Invalid - empty
                    ConnectionString = new string('x', 1001), // Invalid - too long
                }
            );

            var postResponse = await client.PostAsync(
                "/v2/dmsInstances/",
                new StringContent(invalidPostBody, Encoding.UTF8, "application/json")
            );

            var putResponse = await client.PutAsync(
                "/v2/dmsInstances/1",
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
                "/v2/dmsInstances/1",
                new StringContent(
                    JsonSerializer.Serialize(
                        new DmsInstanceUpdateCommand
                        {
                            Id = 2, // Mismatch with URL
                            InstanceType = "Production",
                            InstanceName = "Test Instance",
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
    public class FailureNotFoundTests : DmsInstanceModuleTests
    {
        [SetUp]
        public void SetUp()
        {
            A.CallTo(() => _dmsInstanceRepository.GetDmsInstance(A<long>._))
                .Returns(new DmsInstanceGetResult.FailureNotFound());

            A.CallTo(() => _dmsInstanceRepository.UpdateDmsInstance(A<DmsInstanceUpdateCommand>._))
                .Returns(new DmsInstanceUpdateResult.FailureNotExists());

            A.CallTo(() => _dmsInstanceRepository.DeleteDmsInstance(A<long>._))
                .Returns(new DmsInstanceDeleteResult.FailureNotExists());
        }

        [Test]
        public async Task Should_return_proper_not_found_responses()
        {
            using var client = SetUpClient();

            var getResponse = await client.GetAsync("/v2/dmsInstances/999");
            var updateResponse = await client.PutAsync(
                "/v2/dmsInstances/999",
                new StringContent(
                    JsonSerializer.Serialize(
                        new DmsInstanceUpdateCommand
                        {
                            Id = 999,
                            InstanceType = "Production",
                            InstanceName = "Test Instance",
                            ConnectionString = "Server=localhost;Database=TestDb;",
                        }
                    ),
                    Encoding.UTF8,
                    "application/json"
                )
            );
            var deleteResponse = await client.DeleteAsync("/v2/dmsInstances/999");

            getResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
            updateResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
            deleteResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
    }
}
