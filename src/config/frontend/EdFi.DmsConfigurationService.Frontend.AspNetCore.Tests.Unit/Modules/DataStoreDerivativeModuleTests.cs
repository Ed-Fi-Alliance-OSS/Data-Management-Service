// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
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
using Microsoft.Extensions.Options;
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

    [TestFixture]
    public class Given_insert_returns_a_duplicate_derivative : DataStoreDerivativeModuleTests
    {
        private HttpResponseMessage _response = null!;
        private JsonNode _body = null!;

        [SetUp]
        public async Task Setup()
        {
            A.CallTo(() => _repository.InsertDataStoreDerivative(A<DataStoreDerivativeInsertCommand>.Ignored))
                .Returns(
                    new DataStoreDerivativeInsertResult.FailureDuplicateDataStoreDerivative(1, "ReadReplica")
                );

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
        public void It_uses_the_conflict_type() =>
            _body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:conflict");

        [Test]
        public void It_has_the_conflict_title() => _body["title"]!.GetValue<string>().Should().Be("Conflict");

        [Test]
        public void It_has_the_expected_detail() =>
            _body["detail"]!
                .GetValue<string>()
                .Should()
                .Be("A DataStoreDerivative of type ReadReplica already exists for DataStore 1.");

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
    public class Given_update_returns_a_duplicate_derivative : DataStoreDerivativeModuleTests
    {
        private HttpResponseMessage _response = null!;
        private JsonNode _body = null!;

        [SetUp]
        public async Task Setup()
        {
            A.CallTo(() => _repository.UpdateDataStoreDerivative(A<DataStoreDerivativeUpdateCommand>.Ignored))
                .Returns(
                    new DataStoreDerivativeUpdateResult.FailureDuplicateDataStoreDerivative(2, "Snapshot")
                );

            using var client = SetUpClient();
            using var content = new StringContent(
                """{"id":1,"dataStoreId":2,"derivativeType":"Snapshot"}""",
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
        public void It_uses_the_conflict_type() =>
            _body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:conflict");

        [Test]
        public void It_has_the_conflict_title() => _body["title"]!.GetValue<string>().Should().Be("Conflict");

        [Test]
        public void It_has_the_expected_detail() =>
            _body["detail"]!
                .GetValue<string>()
                .Should()
                .Be("A DataStoreDerivative of type Snapshot already exists for DataStore 2.");

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

    /// <summary>
    /// A get returns the derivative's stored cipher text, so a client that reads it, changes an
    /// unrelated field and writes the object back resubmits that exact value. Encrypting it a second
    /// time leaves a value no reader can turn back into a connection string, so the write path has to
    /// refuse it. These fixtures run against the real validator the host registers.
    /// </summary>
    [TestFixture]
    public class ConnectionStringValidationTests : DataStoreDerivativeModuleTests
    {
        private const string ValidConnectionString = "Server=localhost;Database=ReplicaDb;";

        [SetUp]
        public void SetUpRepository()
        {
            // The fake is shared by the fixture, so recorded calls are cleared to keep each test's
            // "was the repository reached" assertion its own.
            Fake.ClearRecordedCalls(_repository);

            A.CallTo(() => _repository.InsertDataStoreDerivative(A<DataStoreDerivativeInsertCommand>._))
                .Returns(new DataStoreDerivativeInsertResult.Success(1));
            A.CallTo(() => _repository.UpdateDataStoreDerivative(A<DataStoreDerivativeUpdateCommand>._))
                .Returns(new DataStoreDerivativeUpdateResult.Success());
        }

        /// <summary>
        /// What a get returns for this plain text: the stored bytes, Base64 encoded.
        /// </summary>
        private static string StoredValueFor(string plainText) =>
            Convert.ToBase64String(
                new ConnectionStringEncryptionService(
                    Options.Create(
                        new EdFi.DmsConfigurationService.Backend.DatabaseOptions
                        {
                            DatabaseConnection = "Server=test;",
                            EncryptionKey = "TestEncryptionKey123456789012345678901234567890",
                        }
                    )
                ).Encrypt(plainText)!
            );

        private static StringContent InsertBody(string? connectionString) =>
            new(
                JsonSerializer.Serialize(
                    new DataStoreDerivativeInsertCommand
                    {
                        DataStoreId = 1,
                        DerivativeType = "ReadReplica",
                        ConnectionString = connectionString,
                    }
                ),
                Encoding.UTF8,
                "application/json"
            );

        private static StringContent UpdateBody(
            string? connectionString,
            string derivativeType = "ReadReplica"
        ) =>
            new(
                JsonSerializer.Serialize(
                    new DataStoreDerivativeUpdateCommand
                    {
                        Id = 1,
                        DataStoreId = 1,
                        DerivativeType = derivativeType,
                        ConnectionString = connectionString,
                    }
                ),
                Encoding.UTF8,
                "application/json"
            );

        private static async Task ShouldBeDataValidationFailure(HttpResponseMessage response)
        {
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

            JsonNode body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
            body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:bad-request:data");
            body["validationErrors"]!.AsObject().Should().ContainKey("ConnectionString");
        }

        // Plain texts of different lengths, so the stored value covers more than one Base64 padding
        // shape. The exhaustive sweep over lengths lives with the cipher text detector.
        [TestCase("Server=a;Db=b")]
        [TestCase("Server=localhost;Db=b")]
        [TestCase("Server=localhost;Database=ReplicaDb;abc")]
        public async Task It_rejects_resubmitted_cipher_text_on_post(string plainText)
        {
            using var client = SetUpClient();

            var response = await client.PostAsync(
                "/v3/dataStoreDerivatives/",
                InsertBody(StoredValueFor(plainText))
            );

            await ShouldBeDataValidationFailure(response);
            A.CallTo(() => _repository.InsertDataStoreDerivative(A<DataStoreDerivativeInsertCommand>._))
                .MustNotHaveHappened();
        }

        [TestCase("Server=a;Db=b")]
        [TestCase("Server=localhost;Db=b")]
        [TestCase("Server=localhost;Database=ReplicaDb;abc")]
        public async Task It_rejects_resubmitted_cipher_text_on_put(string plainText)
        {
            using var client = SetUpClient();

            var response = await client.PutAsync(
                "/v3/dataStoreDerivatives/1",
                UpdateBody(StoredValueFor(plainText))
            );

            await ShouldBeDataValidationFailure(response);
            A.CallTo(() => _repository.UpdateDataStoreDerivative(A<DataStoreDerivativeUpdateCommand>._))
                .MustNotHaveHappened();
        }

        [TestCase("not-a-connection-string")]
        [TestCase(";;;")]
        [TestCase("host=")]
        [TestCase("")]
        [TestCase("   ")]
        public async Task It_rejects_an_unusable_value_on_post(string connectionString)
        {
            using var client = SetUpClient();

            var response = await client.PostAsync("/v3/dataStoreDerivatives/", InsertBody(connectionString));

            await ShouldBeDataValidationFailure(response);
            A.CallTo(() => _repository.InsertDataStoreDerivative(A<DataStoreDerivativeInsertCommand>._))
                .MustNotHaveHappened();
        }

        [TestCase("not-a-connection-string")]
        [TestCase(";;;")]
        [TestCase("host=")]
        [TestCase("")]
        [TestCase("   ")]
        public async Task It_rejects_an_unusable_value_on_put(string connectionString)
        {
            using var client = SetUpClient();

            var response = await client.PutAsync("/v3/dataStoreDerivatives/1", UpdateBody(connectionString));

            await ShouldBeDataValidationFailure(response);
            A.CallTo(() => _repository.UpdateDataStoreDerivative(A<DataStoreDerivativeUpdateCommand>._))
                .MustNotHaveHappened();
        }

        [Test]
        public async Task It_accepts_a_new_connection_string_on_post()
        {
            using var client = SetUpClient();

            var response = await client.PostAsync(
                "/v3/dataStoreDerivatives/",
                InsertBody(ValidConnectionString)
            );

            response.StatusCode.Should().Be(HttpStatusCode.Created);
            A.CallTo(() =>
                    _repository.InsertDataStoreDerivative(
                        A<DataStoreDerivativeInsertCommand>.That.Matches(command =>
                            command.ConnectionString == ValidConnectionString
                        )
                    )
                )
                .MustHaveHappenedOnceExactly();
        }

        [Test]
        public async Task It_accepts_a_new_connection_string_on_put()
        {
            using var client = SetUpClient();

            var response = await client.PutAsync(
                "/v3/dataStoreDerivatives/1",
                UpdateBody(ValidConnectionString)
            );

            response.StatusCode.Should().Be(HttpStatusCode.NoContent);
            A.CallTo(() =>
                    _repository.UpdateDataStoreDerivative(
                        A<DataStoreDerivativeUpdateCommand>.That.Matches(command =>
                            command.ConnectionString == ValidConnectionString
                        )
                    )
                )
                .MustHaveHappenedOnceExactly();
        }

        /// <summary>
        /// The case this validation must not break: an update that is really about another field.
        /// </summary>
        [Test]
        public async Task It_accepts_an_update_that_changes_another_field()
        {
            using var client = SetUpClient();

            var response = await client.PutAsync(
                "/v3/dataStoreDerivatives/1",
                UpdateBody(ValidConnectionString, derivativeType: "Snapshot")
            );

            response.StatusCode.Should().Be(HttpStatusCode.NoContent);
            A.CallTo(() =>
                    _repository.UpdateDataStoreDerivative(
                        A<DataStoreDerivativeUpdateCommand>.That.Matches(command =>
                            command.DerivativeType == "Snapshot"
                            && command.ConnectionString == ValidConnectionString
                        )
                    )
                )
                .MustHaveHappenedOnceExactly();
        }

        /// <summary>
        /// A provider's own parse failure message repeats the text it could not read, so a rejection
        /// must never carry the submitted value into the response.
        /// </summary>
        [Test]
        public async Task It_does_not_repeat_the_submitted_value_in_the_response()
        {
            using var client = SetUpClient();
            string storedValue = StoredValueFor(ValidConnectionString);

            foreach (string submitted in new[] { storedValue, "not-a-connection-string", "host=" })
            {
                var response = await client.PostAsync("/v3/dataStoreDerivatives/", InsertBody(submitted));

                (await response.Content.ReadAsStringAsync()).Should().NotContain(submitted);
            }
        }

        /// <summary>
        /// The move this validation has to leave open: the client that read a derivative writes back
        /// the fields it changed and leaves the connection string out, and the repository is asked to
        /// keep what is stored. Whether the stored bytes actually survive is a repository concern and
        /// is covered by the backend integration tests.
        /// </summary>
        [Test]
        public async Task It_accepts_an_update_that_leaves_the_connection_string_out()
        {
            using var client = SetUpClient();

            DataStoreDerivativeUpdateCommand? received = null;
            A.CallTo(() => _repository.UpdateDataStoreDerivative(A<DataStoreDerivativeUpdateCommand>._))
                .Invokes((DataStoreDerivativeUpdateCommand command) => received = command)
                .Returns(new DataStoreDerivativeUpdateResult.Success());

            var response = await client.PutAsync(
                "/v3/dataStoreDerivatives/1",
                new StringContent(
                    """
                    {
                        "id": 1,
                        "dataStoreId": 1,
                        "derivativeType": "Snapshot"
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );

            response.StatusCode.Should().Be(HttpStatusCode.NoContent);
            received.Should().NotBeNull();
            received!.ConnectionString.Should().BeNull();
            received.DerivativeType.Should().Be("Snapshot");
        }
    }
}
