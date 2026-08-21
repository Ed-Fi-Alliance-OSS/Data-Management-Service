// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
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
    private readonly WebApplicationFactoryTracker<Program> _factoryTracker = new();

    [TearDown]
    public void DisposeWebApplicationFactories() => _factoryTracker.DisposeTrackedFactories();

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
        _factoryTracker.Track(factory);
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
        public async Task Should_return_400_when_offset_is_non_numeric()
        {
            using var client = SetUpClient();
            var response = await client.GetAsync("/v3/dataStoreDerivatives?offset=abc");
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
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
    public class Given_insert_returns_a_foreign_key_violation : DataStoreDerivativeModuleTests
    {
        private HttpResponseMessage _response = null!;
        private JsonNode _body = null!;

        [SetUp]
        public async Task Setup()
        {
            A.CallTo(() => _repository.InsertDataStoreDerivative(A<DataStoreDerivativeInsertCommand>.Ignored))
                .Returns(new DataStoreDerivativeInsertResult.FailureForeignKeyViolation());

            using var client = SetUpClient();
            using var content = new StringContent(
                """{"dataStoreId":1,"derivativeType":"ReadReplica"}""",
                Encoding.UTF8,
                "application/json"
            );
            _response = await client.PostAsync("/v3/dataStoreDerivatives/", content);
            _body = JsonNode.Parse(await _response.Content.ReadAsStringAsync())!;
        }

        [Test]
        public void It_returns_409() => _response.StatusCode.Should().Be(HttpStatusCode.Conflict);

        [Test]
        public void It_uses_the_application_json_content_type() =>
            _response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        [Test]
        public void It_uses_the_unresolved_reference_type() =>
            _body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:conflict:unresolved-reference");

        [Test]
        public void It_has_the_unresolved_reference_title() =>
            _body["title"]!.GetValue<string>().Should().Be("Unresolved Reference");

        [Test]
        public void It_has_the_expected_detail() =>
            _body["detail"]!.GetValue<string>().Should().Be("The specified DataStore does not exist.");

        [Test]
        public void It_has_a_body_status_of_409() => _body["status"]!.GetValue<int>().Should().Be(409);

        [Test]
        public void It_includes_a_non_empty_correlation_id() =>
            _body["correlationId"]!.GetValue<string>().Should().NotBeNullOrEmpty();

        [Test]
        public void It_includes_empty_extension_members()
        {
            _body["validationErrors"]!.AsObject().Count.Should().Be(0);
            _body["errors"]!.AsArray().Count.Should().Be(0);
        }
    }

    [TestFixture]
    public class Given_update_returns_a_foreign_key_violation : DataStoreDerivativeModuleTests
    {
        private HttpResponseMessage _response = null!;
        private JsonNode _body = null!;

        [SetUp]
        public async Task Setup()
        {
            A.CallTo(() => _repository.UpdateDataStoreDerivative(A<DataStoreDerivativeUpdateCommand>.Ignored))
                .Returns(new DataStoreDerivativeUpdateResult.FailureForeignKeyViolation());

            using var client = SetUpClient();
            using var content = new StringContent(
                """{"id":1,"dataStoreId":1,"derivativeType":"ReadReplica"}""",
                Encoding.UTF8,
                "application/json"
            );
            _response = await client.PutAsync("/v3/dataStoreDerivatives/1", content);
            _body = JsonNode.Parse(await _response.Content.ReadAsStringAsync())!;
        }

        [Test]
        public void It_returns_409() => _response.StatusCode.Should().Be(HttpStatusCode.Conflict);

        [Test]
        public void It_uses_the_application_json_content_type() =>
            _response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        [Test]
        public void It_uses_the_unresolved_reference_type() =>
            _body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:conflict:unresolved-reference");

        [Test]
        public void It_has_the_unresolved_reference_title() =>
            _body["title"]!.GetValue<string>().Should().Be("Unresolved Reference");

        [Test]
        public void It_has_the_expected_detail() =>
            _body["detail"]!.GetValue<string>().Should().Be("The specified DataStore does not exist.");

        [Test]
        public void It_has_a_body_status_of_409() => _body["status"]!.GetValue<int>().Should().Be(409);

        [Test]
        public void It_includes_a_non_empty_correlation_id() =>
            _body["correlationId"]!.GetValue<string>().Should().NotBeNullOrEmpty();

        [Test]
        public void It_includes_empty_extension_members()
        {
            _body["validationErrors"]!.AsObject().Count.Should().Be(0);
            _body["errors"]!.AsArray().Count.Should().Be(0);
        }
    }

    [TestFixture]
    public class Given_a_data_store_derivative_update_with_invalid_body_id : DataStoreDerivativeModuleTests
    {
        [Test]
        public async Task It_returns_bad_request_when_body_id_does_not_match_route_id()
        {
            using var client = SetUpClient();
            using var content = new StringContent(
                """{"id":999,"dataStoreId":1,"derivativeType":"ReadReplica"}""",
                Encoding.UTF8,
                "application/json"
            );
            var response = await client.PutAsync("/v3/dataStoreDerivatives/1", content);

            var actualResponse = JsonNode.Parse(await response.Content.ReadAsStringAsync());

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            actualResponse!["validationErrors"]!["Id"]![0]!
                .GetValue<string>()
                .Should()
                .Contain("Request body id must match the id in the url");
        }

        [Test]
        public async Task It_returns_bad_request_when_body_id_is_omitted()
        {
            using var client = SetUpClient();
            var response = await client.PutAsync(
                "/v3/dataStoreDerivatives/1",
                new StringContent(
                    """
                    {
                        "dataStoreId": 1,
                        "derivativeType": "ReadReplica"
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );

            string responseContent = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            responseContent.Should().Contain("Request body id must match the id in the url.");
        }
    }
}
