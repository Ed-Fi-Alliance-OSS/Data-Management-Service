// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.ApiClient;
using EdFi.DmsConfigurationService.DataModel.Model.Application;
using EdFi.DmsConfigurationService.DataModel.Model.Vendor;
using FakeItEasy;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Modules;

public partial class ApiClientModuleTests
{
    /// <summary>
    /// Asserts the API client education organization contract against the document the
    /// application actually serves, and checks it against what the same pipeline does: every
    /// published POST and PUT request example is sent, its scope and claim are captured, and the
    /// responses are matched to the published schemas and status codes. Application 1 holds
    /// 255901 and 255902, as the examples assume; Application 2 holds only 255901; Application 99
    /// does not exist; and Application 3's identity provider fails. Client 1 is narrowed to 255902.
    /// </summary>
    [TestFixture]
    public class Given_the_served_openapi_document_for_api_client_scope : ApiClientModuleTests
    {
        private const string FailingProviderApplication = "Failing Provider Application";

        private JsonNode _document = JsonNode.Parse("{}")!;
        private readonly Dictionary<string, (HttpStatusCode Status, JsonNode Body)> _inserts = [];
        private readonly Dictionary<
            string,
            (HttpStatusCode Status, string Claim, ApiClientEducationOrganizationScope? Scope)
        > _updates = [];
        private readonly List<string> _createdClaims = [];
        private readonly List<string> _updatedClaims = [];
        private readonly List<ApiClientInsertCommand> _insertCommands = [];
        private readonly List<ApiClientUpdateCommand> _updateCommands = [];
        private (HttpStatusCode Status, JsonNode Body) _emptyList;
        private (HttpStatusCode Status, JsonNode Body) _unresolvedApplication;
        private (HttpStatusCode Status, JsonNode Body) _providerFailure;
        private (HttpStatusCode Status, JsonNode Body) _nonFittingMove;
        private (HttpStatusCode Status, JsonNode Body) _roundTrip;
        private (HttpStatusCode Status, JsonNode Body) _readItem;
        private (HttpStatusCode Status, JsonNode Body) _readEmptyItem;
        private (HttpStatusCode Status, JsonNode Body) _readCollection;
        private (HttpStatusCode Status, JsonNode Body) _invalidDeleteId;
        private (HttpStatusCode Status, JsonNode Body) _invalidResetId;
        private (HttpStatusCode Status, JsonNode Body) _insertUuidCollision;
        private (HttpStatusCode Status, JsonNode Body) _updateUuidCollision;
        private (HttpStatusCode Status, JsonNode Body) _reset;
        private const string Collision = "Collision";

        private static ApiClientResponse Client(int id, bool inherits, List<long> educationOrganizationIds) =>
            new()
            {
                Id = id,
                ApplicationId = 1,
                ClientId = $"client-{id}",
                ClientUuid = Guid.NewGuid(),
                Name = $"Client {id}",
                IsApproved = true,
                DataStoreIds = [1],
                InheritsApplicationEducationOrganizations = inherits,
                EducationOrganizationIds = educationOrganizationIds,
            };

        private static readonly Dictionary<int, ApiClientResponse> Clients = new()
        {
            [1] = Client(1, true, [255901, 255902]),
            [2] = Client(2, false, [255902]),
            [3] = Client(3, false, []),
        };

        [SetUp]
        public async Task Setup()
        {
            ArrangeRepositories();

            using var client = SetUpClient();
            using var documentResponse = await client.GetAsync("/openapi/v1.json");
            _document = JsonNode.Parse(await documentResponse.Content.ReadAsStringAsync())!;

            _inserts.Clear();
            _updates.Clear();
            // A document without examples sends nothing, so each test reports its own failure.
            foreach ((string name, JsonNode? example) in ExamplesOf(Operation("/v3/apiClients", "post")))
            {
                _inserts[name] = await SendAsync(
                    client,
                    HttpMethod.Post,
                    "/v3/apiClients",
                    example!["value"]!
                );
            }

            foreach ((string name, JsonNode? example) in ExamplesOf(Operation("/v3/apiClients/{id}", "put")))
            {
                int claimsBefore = _updatedClaims.Count;
                var (status, _) = await SendAsync(
                    client,
                    HttpMethod.Put,
                    "/v3/apiClients/1",
                    example!["value"]!
                );
                _updates[name] = (
                    status,
                    _updatedClaims.Count > claimsBefore ? _updatedClaims[^1] : "(none)",
                    _updateCommands.Count > 0 ? _updateCommands[^1].EducationOrganizationScope : null
                );
            }

            _emptyList = await SendAsync(
                client,
                HttpMethod.Post,
                "/v3/apiClients",
                Body(
                    """{ "applicationId": 1, "name": "Empty", "isApproved": true, "educationOrganizationIds": [] }"""
                )
            );
            _unresolvedApplication = await SendAsync(
                client,
                HttpMethod.Post,
                "/v3/apiClients",
                Body("""{ "applicationId": 99, "name": "Orphan", "isApproved": true }""")
            );
            _providerFailure = await SendAsync(
                client,
                HttpMethod.Post,
                "/v3/apiClients",
                Body("""{ "applicationId": 3, "name": "Unreachable", "isApproved": true }""")
            );
            _nonFittingMove = await SendAsync(
                client,
                HttpMethod.Put,
                "/v3/apiClients/1",
                Body("""{ "id": 1, "applicationId": 2, "name": "Moved", "isApproved": true }""")
            );

            _readItem = await SendAsync(client, HttpMethod.Get, "/v3/apiClients/1");
            _readEmptyItem = await SendAsync(client, HttpMethod.Get, "/v3/apiClients/3");
            _readCollection = await SendAsync(client, HttpMethod.Get, "/v3/apiClients/?offset=0&limit=25");

            _invalidDeleteId = await SendAsync(client, HttpMethod.Delete, "/v3/apiClients/not-an-int");
            _invalidResetId = await SendAsync(
                client,
                HttpMethod.Put,
                "/v3/apiClients/not-an-int/reset-credential"
            );
            _insertUuidCollision = await SendAsync(
                client,
                HttpMethod.Post,
                "/v3/apiClients",
                Body($$"""{ "applicationId": 1, "name": "{{Collision}}", "isApproved": true }""")
            );
            _updateUuidCollision = await SendAsync(
                client,
                HttpMethod.Put,
                "/v3/apiClients/1",
                Body($$"""{ "id": 1, "applicationId": 1, "name": "{{Collision}}", "isApproved": true }""")
            );
            _reset = await SendAsync(client, HttpMethod.Put, "/v3/apiClients/1/reset-credential");

            // GET-then-PUT: the inheriting client sent back as read stays inheriting, so client 1's
            // authoritative scope now matches the read
            A.CallTo(() => _apiClientRepository.GetApiClientEducationOrganizationState(1))
                .Returns(
                    new ApiClientEducationOrganizationStateResult.Success(
                        new ApiClientEducationOrganizationState(
                            1,
                            true,
                            [255901, 255902],
                            [255901, 255902],
                            null
                        )
                    )
                );
            int updatesBefore = _updateCommands.Count;
            JsonObject readBack = _readItem.Body.DeepClone().AsObject();
            foreach (
                string readOnly in new[]
                {
                    "clientId",
                    "clientUuid",
                    "creatorOwnershipTokenId",
                    "ownershipTokenIds",
                }
            )
            {
                readBack.Remove(readOnly);
            }

            _roundTrip = await SendAsync(client, HttpMethod.Put, "/v3/apiClients/1", readBack);
            _updateCommands.Count.Should().Be(updatesBefore + 1);
        }

        private void ArrangeRepositories()
        {
            A.CallTo(() => _applicationRepository.GetApplication(A<int>.Ignored))
                .ReturnsLazily(call =>
                {
                    int applicationId = call.GetArgument<int>(0);
                    return Task.FromResult<ApplicationGetResult>(
                        applicationId switch
                        {
                            99 => new ApplicationGetResult.FailureNotFound(),
                            _ => new ApplicationGetResult.Success(
                                new ApplicationResponse
                                {
                                    Id = applicationId,
                                    ApplicationName =
                                        applicationId == 3
                                            ? FailingProviderApplication
                                            : $"Application {applicationId}",
                                    ClaimSetName = "ClaimSet",
                                    VendorId = 1,
                                    EducationOrganizationIds =
                                        applicationId == 2 ? [255901] : [255901, 255902],
                                    DataStoreIds = [],
                                }
                            ),
                        }
                    );
                });
            A.CallTo(() => _vendorRepository.GetVendor(A<int>.Ignored))
                .Returns(
                    new VendorGetResult.Success(
                        new VendorResponse
                        {
                            Company = "Vendor",
                            ContactName = "Contact",
                            ContactEmailAddress = "contact@test.test",
                            NamespacePrefixes = "uri://test",
                        }
                    )
                );
            A.CallTo(() => _dataStoreRepository.GetExistingDataStoreIds(A<int[]>.Ignored))
                .ReturnsLazily(call =>
                    Task.FromResult<DataStoreIdsExistResult>(
                        new DataStoreIdsExistResult.Success([.. call.GetArgument<int[]>(0) ?? []])
                    )
                );

            A.CallTo(() =>
                    _identityProviderRepository.CreateClientAsync(
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
                .ReturnsLazily(call =>
                {
                    if (call.GetArgument<string>(3) == FailingProviderApplication)
                    {
                        return Task.FromResult<ClientCreateResult>(
                            new ClientCreateResult.FailureIdentityProvider(new IdentityProviderError("down"))
                        );
                    }

                    _createdClaims.Add(call.GetArgument<string>(6)!);
                    return Task.FromResult<ClientCreateResult>(
                        new ClientCreateResult.Success(Guid.NewGuid())
                    );
                });
            A.CallTo(() =>
                    _identityProviderRepository.UpdateClientAsync(
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<int[]?>.Ignored,
                        A<bool>.Ignored,
                        A<string>.Ignored
                    )
                )
                .ReturnsLazily(call =>
                {
                    _updatedClaims.Add(call.GetArgument<string>(3)!);
                    return Task.FromResult<ClientUpdateResult>(
                        new ClientUpdateResult.Success(Guid.Parse(call.GetArgument<string>(0)!))
                    );
                });

            A.CallTo(() =>
                    _apiClientRepository.InsertApiClient(
                        A<ApiClientInsertCommand>.Ignored,
                        A<ApiClientCommand>.Ignored
                    )
                )
                .ReturnsLazily(call =>
                {
                    ApiClientInsertCommand command = call.GetArgument<ApiClientInsertCommand>(0)!;
                    if (command.Name == Collision)
                    {
                        return Task.FromResult<ApiClientInsertResult>(
                            new ApiClientInsertResult.FailureDuplicateClientUuid()
                        );
                    }

                    _insertCommands.Add(command);
                    return Task.FromResult<ApiClientInsertResult>(new ApiClientInsertResult.Success(10));
                });
            A.CallTo(() => _apiClientRepository.UpdateApiClient(A<ApiClientUpdateCommand>.Ignored))
                .ReturnsLazily(call =>
                {
                    ApiClientUpdateCommand command = call.GetArgument<ApiClientUpdateCommand>(0)!;
                    if (command.Name == Collision)
                    {
                        return Task.FromResult<ApiClientUpdateResult>(
                            new ApiClientUpdateResult.FailureDuplicateClientUuid()
                        );
                    }

                    _updateCommands.Add(command);
                    return Task.FromResult<ApiClientUpdateResult>(new ApiClientUpdateResult.Success());
                });
            A.CallTo(() => _apiClientRepository.GetApiClientById(A<int>.Ignored))
                .ReturnsLazily(call =>
                    Task.FromResult<ApiClientGetResult>(
                        Clients.TryGetValue(call.GetArgument<int>(0), out ApiClientResponse? found)
                            ? new ApiClientGetResult.Success(found)
                            : new ApiClientGetResult.FailureNotFound()
                    )
                );
            A.CallTo(() => _apiClientRepository.GetApiClientByClientId(A<string>.Ignored))
                .Returns(new ApiClientGetResult.FailureNotFound());
            A.CallTo(() =>
                    _apiClientRepository.QueryApiClient(
                        A<ApiClientQuery>.That.Matches(q => !q.ApplicationId.HasValue)
                    )
                )
                .Returns(new ApiClientQueryResult.Success([.. Clients.Values]));

            A.CallTo(() => _identityProviderRepository.ResetCredentialsAsync(A<string>.Ignored))
                .Returns(new ClientResetResult.Success("NEW-secret-Example-Value-Of-Minimum-Length-32!"));

            // Client 1 is narrowed to 255902 for the examples; the round trip rearranges it
            A.CallTo(() => _apiClientRepository.GetApiClientEducationOrganizationState(1))
                .Returns(
                    new ApiClientEducationOrganizationStateResult.Success(
                        new ApiClientEducationOrganizationState(1, false, [255902], [255902], null)
                    )
                );
        }

        private static JsonNode Body(string json) => JsonNode.Parse(json)!;

        private static async Task<(HttpStatusCode Status, JsonNode Body)> SendAsync(
            HttpClient client,
            HttpMethod method,
            string path,
            JsonNode? body = null
        )
        {
            using HttpRequestMessage request = new(method, path);
            if (body is not null)
            {
                request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            }

            using HttpResponseMessage response = await client.SendAsync(request);
            string content = await response.Content.ReadAsStringAsync();
            return (response.StatusCode, content.Length == 0 ? new JsonObject() : JsonNode.Parse(content)!);
        }

        private JsonNode Operation(string path, string method) => _document["paths"]![path]![method]!;

        private JsonNode Schema(string schemaName) => _document["components"]!["schemas"]![schemaName]!;

        private static JsonObject ExamplesOf(JsonNode operation) =>
            operation["requestBody"]?["content"]?["application/json"]?["examples"]?.AsObject() ?? [];

        private JsonNode Response(string path, string method, string status) =>
            Operation(path, method)["responses"]![status]!;

        private static JsonObject ResponseExamples(JsonNode response) =>
            response["content"]!["application/json"]!["examples"]!.AsObject();

        private JsonNode Resolve(JsonNode schema) =>
            schema["$ref"] is JsonNode reference
                ? Schema(reference.GetValue<string>()["#/components/schemas/".Length..])
                : schema;

        /// <summary>The schema's types: OpenAPI 3.1 writes a nullable one as a type array that includes "null".</summary>
        private static string[] TypesOf(JsonNode schema) =>
            schema["type"] switch
            {
                JsonArray types => [.. types.Select(type => type!.GetValue<string>())],
                JsonNode type => [type.GetValue<string>()],
                null => [],
            };

        private static string[] Required(JsonNode schema) =>
            [.. schema["required"]!.AsArray().Select(entry => entry!.GetValue<string>())];

        private static string JsonTypeOf(JsonNode? value) =>
            value?.GetValueKind() switch
            {
                null or JsonValueKind.Null => "null",
                JsonValueKind.Number => "integer",
                JsonValueKind.String => "string",
                JsonValueKind.True or JsonValueKind.False => "boolean",
                JsonValueKind.Array => "array",
                _ => "object",
            };

        /// <summary>
        /// The body carries exactly the schema's properties, every required one is present, and
        /// each value's JSON type is one the schema allows.
        /// </summary>
        private static void AssertMatchesSchema(JsonObject body, JsonNode schema)
        {
            JsonObject properties = schema["properties"]!.AsObject();
            body.Select(member => member.Key)
                .Should()
                .BeEquivalentTo(properties.Select(member => member.Key));
            body.Select(member => member.Key).Should().Contain(Required(schema));

            foreach ((string name, JsonNode? value) in body)
            {
                TypesOf(properties[name]!).Should().Contain(JsonTypeOf(value), $"'{name}' is {value}");
            }
        }

        private static string[] Statuses(JsonNode operation) =>
            [.. operation["responses"]!.AsObject().Select(response => response.Key)];

        private static string Description(JsonNode node) => node["description"]!.GetValue<string>();

        private static string ProblemType((HttpStatusCode Status, JsonNode Body) response) =>
            response.Body["type"]!.GetValue<string>();

        [TestCase("ApiClientInsertCommand")]
        [TestCase("ApiClientUpdateCommand")]
        public void It_publishes_the_scope_fields_as_optional_and_non_nullable(string schemaName)
        {
            JsonNode schema = Schema(schemaName);
            JsonNode properties = schema["properties"]!;

            Required(schema)
                .Should()
                .NotContain(["educationOrganizationIds", "inheritsApplicationEducationOrganizations"]);
            TypesOf(properties["inheritsApplicationEducationOrganizations"]!).Should().Equal("boolean");
            TypesOf(properties["educationOrganizationIds"]!).Should().Equal("array");
            properties["educationOrganizationIds"]!["minItems"]!.GetValue<int>().Should().Be(1);
            properties["educationOrganizationIds"]!["items"]!["format"]!
                .GetValue<string>()
                .Should()
                .Be("int64");
        }

        [TestCase("ApiClientInsertCommand")]
        [TestCase("ApiClientUpdateCommand")]
        public void It_keeps_presence_tracking_and_resolved_members_out_of_the_request_schema(
            string schemaName
        ) =>
            Schema(schemaName)["properties"]!
                .AsObject()
                .Select(property => property.Key)
                .Should()
                .NotContain([
                    "educationOrganizationIdsSupplied",
                    "inheritsApplicationEducationOrganizationsSupplied",
                    "educationOrganizationScope",
                    "clientUuid",
                ]);

        [TestCase("ApiClientInsertCommand")]
        [TestCase("ApiClientUpdateCommand")]
        public void It_documents_omission_without_a_fixed_flag_default(string schemaName)
        {
            JsonNode flag = Schema(schemaName)["properties"]!["inheritsApplicationEducationOrganizations"]!;

            flag["default"].Should().BeNull();
            Description(flag)
                .Should()
                .Contain("no fixed default")
                .And.Contain("a create inherits")
                .And.Contain("an update keeps the client's flag and list");
        }

        [Test]
        public void It_publishes_the_response_scope_fields_as_required_and_non_nullable()
        {
            JsonNode schema = Schema("ApiClientResponse");

            Required(schema).Should().BeEquivalentTo(schema["properties"]!.AsObject().Select(p => p.Key));
            TypesOf(schema["properties"]!["inheritsApplicationEducationOrganizations"]!)
                .Should()
                .Equal("boolean");
            TypesOf(schema["properties"]!["educationOrganizationIds"]!).Should().Equal("array");
            Description(schema["properties"]!["educationOrganizationIds"]!).Should().Contain("Never null");
        }

        [TestCase("/v3/apiClients", "post", new[] { "201", "400", "401", "403", "409", "500", "502" })]
        [TestCase(
            "/v3/apiClients/{id}",
            "put",
            new[] { "204", "400", "401", "403", "404", "409", "500", "502" }
        )]
        [TestCase(
            "/v3/apiClients/{id}",
            "delete",
            new[] { "204", "400", "401", "403", "404", "409", "500", "502" }
        )]
        [TestCase(
            "/v3/apiClients/{id}/reset-credential",
            "put",
            new[] { "200", "400", "401", "403", "404", "409", "500", "502" }
        )]
        [TestCase("/v3/apiClients", "get", new[] { "200", "400", "401", "403", "500" })]
        [TestCase("/v3/apiClients/{id}", "get", new[] { "200", "401", "403", "404", "500" })]
        public void It_publishes_the_status_codes_the_endpoint_returns(
            string path,
            string method,
            string[] statuses
        ) => Statuses(Operation(path, method)).Should().BeEquivalentTo(statuses);

        [Test]
        public void It_documents_the_scope_rules_on_create_and_update()
        {
            foreach (
                JsonNode operation in new[]
                {
                    Operation("/v3/apiClients", "post"),
                    Operation("/v3/apiClients/{id}", "put"),
                }
            )
            {
                Description(operation)
                    .Should()
                    .Contain("even when it equals the Application's list")
                    .And.Contain("duplicate ids are removed")
                    .And.Contain("in any order")
                    .And.Contain("false without a list")
                    .And.Contain("urn:ed-fi:management-api:bad-request:data");
            }

            Description(Operation("/v3/apiClients", "post"))
                .Should()
                .Contain("With neither field the client inherits");
            Description(Operation("/v3/apiClients/{id}", "put"))
                .Should()
                .Contain("With neither field the client keeps its flag and list")
                .And.Contain("urn:ed-fi:management-api:conflict:education-organization-scope");
        }

        [Test]
        public void It_documents_the_duplicate_name_as_a_400() =>
            Description(Response("/v3/apiClients", "post", "400")).Should().Contain("already has the name");

        [Test]
        public void It_documents_reset_as_secret_only() =>
            Description(Operation("/v3/apiClients/{id}/reset-credential", "put"))
                .Should()
                .Contain("Only the secret changes")
                .And.Contain("narrowed list");

        [TestCase("inherit", "255901,255902", null)]
        [TestCase("narrowed", "255901", typeof(ApiClientEducationOrganizationScope.Narrow))]
        [TestCase("explicitInherit", "255901,255902", typeof(ApiClientEducationOrganizationScope.Inherit))]
        public void It_creates_the_client_each_post_example_describes(
            string example,
            string claim,
            Type? scopeType
        )
        {
            _inserts[example].Status.Should().Be(HttpStatusCode.Created);
            _createdClaims[_inserts.Keys.ToList().IndexOf(example)].Should().Be(claim);
            ApiClientEducationOrganizationScope? scope = _insertCommands[
                _inserts.Keys.ToList().IndexOf(example)
            ].EducationOrganizationScope;
            (scope?.GetType()).Should().Be(scopeType);
        }

        [Test]
        public void It_keeps_the_identity_only_post_example_without_data_stores() =>
            ExamplesOf(Operation("/v3/apiClients", "post"))["inherit"]!["value"]!["dataStoreIds"]!
                .AsArray()
                .Should()
                .BeEmpty();

        [Test]
        public void It_returns_credentials_matching_the_published_created_schema_and_example()
        {
            JsonNode created = Response("/v3/apiClients", "post", "201");
            JsonNode schema = Resolve(created["content"]!["application/json"]!["schema"]!);

            AssertMatchesSchema(_inserts["narrowed"].Body.AsObject(), schema);
            AssertMatchesSchema(ResponseExamples(created)["credentials"]!["value"]!.AsObject(), schema);
        }

        [TestCase("keepScope", "255902", null)]
        [TestCase("narrow", "255901", typeof(ApiClientEducationOrganizationScope.Narrow))]
        [TestCase("inherit", "255901,255902", typeof(ApiClientEducationOrganizationScope.Inherit))]
        [TestCase("roundTrip", "255901,255902", typeof(ApiClientEducationOrganizationScope.Inherit))]
        public void It_updates_the_client_as_each_put_example_describes(
            string example,
            string claim,
            Type? scopeType
        )
        {
            _updates[example].Status.Should().Be(HttpStatusCode.NoContent);
            _updates[example].Claim.Should().Be(claim);
            (_updates[example].Scope?.GetType()).Should().Be(scopeType);
        }

        [Test]
        public void It_publishes_exactly_the_request_examples_these_tests_send()
        {
            ExamplesOf(Operation("/v3/apiClients", "post"))
                .Select(example => example.Key)
                .Should()
                .BeEquivalentTo("inherit", "narrowed", "explicitInherit");
            ExamplesOf(Operation("/v3/apiClients/{id}", "put"))
                .Select(example => example.Key)
                .Should()
                .BeEquivalentTo("keepScope", "narrow", "inherit", "roundTrip");
        }

        [TestCase("delete", "/v3/apiClients/{id}")]
        [TestCase("reset", "/v3/apiClients/{id}/reset-credential")]
        public void It_rejects_a_non_integer_id_as_the_documented_400(string operation, string path)
        {
            var (response, method) =
                operation == "delete" ? (_invalidDeleteId, "delete") : (_invalidResetId, "put");

            response.Status.Should().Be(HttpStatusCode.BadRequest);
            ProblemType(response).Should().Be("urn:ed-fi:management-api:bad-request:parameter");
            Description(Response(path, method, "400")).Should().Contain(ProblemType(response));
        }

        [Test]
        public void It_reports_an_insert_uuid_collision_as_the_documented_409()
        {
            _insertUuidCollision.Status.Should().Be(HttpStatusCode.Conflict);
            ProblemType(_insertUuidCollision).Should().Be("urn:ed-fi:management-api:conflict");
            Description(Response("/v3/apiClients", "post", "409"))
                .Should()
                .Contain("already associated with another API client")
                .And.Contain(ProblemType(_insertUuidCollision));
        }

        [Test]
        public void It_reports_an_update_uuid_collision_as_the_documented_500()
        {
            _updateUuidCollision.Status.Should().Be(HttpStatusCode.InternalServerError);
            Description(Response("/v3/apiClients/{id}", "put", "500"))
                .Should()
                .Contain("already associated with another API client");
            Description(Response("/v3/apiClients/{id}", "put", "409"))
                .Should()
                .NotContain("already associated with another API client");
        }

        [Test]
        public void It_returns_reset_credentials_matching_the_published_schema()
        {
            JsonNode schema = Resolve(
                Response("/v3/apiClients/{id}/reset-credential", "put", "200")["content"]![
                    "application/json"
                ]!["schema"]!
            );

            _reset.Status.Should().Be(HttpStatusCode.OK);
            AssertMatchesSchema(_reset.Body.AsObject(), schema);
            TypesOf(schema["properties"]!["key"]!).Should().Equal("string");
            TypesOf(schema["properties"]!["secret"]!).Should().Equal("string");
            _reset.Body["secret"]!
                .GetValue<string>()
                .Should()
                .Be("NEW-secret-Example-Value-Of-Minimum-Length-32!");
        }

        [Test]
        public void It_publishes_the_response_ids_as_non_nullable_int64()
        {
            JsonNode items = Schema("ApiClientResponse")["properties"]!["educationOrganizationIds"]![
                "items"
            ]!;

            items["format"]!.GetValue<string>().Should().Be("int64");
            TypesOf(items).Should().NotContain("null");
        }

        [Test]
        public void It_qualifies_the_provider_failure_guarantees()
        {
            Description(Response("/v3/apiClients", "post", "502"))
                .Should()
                .Contain("no API client record was saved");
            Description(Response("/v3/apiClients/{id}", "put", "502")).Should().Contain("not confirmed");
            Description(Response("/v3/apiClients/{id}/reset-credential", "put", "502"))
                .Should()
                .Contain("not confirmed");
            Description(Response("/v3/apiClients/{id}", "delete", "502")).Should().Contain("record is kept");
            Description(Response("/v3/apiClients", "post", "403")).Should().Contain("service role");
        }

        [Test]
        public void It_keeps_an_inheriting_client_inheriting_through_a_get_then_put_round_trip()
        {
            _roundTrip.Status.Should().Be(HttpStatusCode.NoContent);
            _updateCommands[^1]
                .EducationOrganizationScope.Should()
                .BeOfType<ApiClientEducationOrganizationScope.Inherit>();
            _updatedClaims[^1].Should().Be("255901,255902");
        }

        [Test]
        public void It_rejects_an_empty_list_as_the_documented_400()
        {
            _emptyList.Status.Should().Be(HttpStatusCode.BadRequest);
            ProblemType(_emptyList).Should().Be("urn:ed-fi:management-api:bad-request:data");
            Statuses(Operation("/v3/apiClients", "post")).Should().Contain("400");
            Description(Response("/v3/apiClients", "post", "400")).Should().Contain(ProblemType(_emptyList));
        }

        [Test]
        public void It_rejects_an_unresolved_application_as_the_documented_409()
        {
            _unresolvedApplication.Status.Should().Be(HttpStatusCode.Conflict);
            Description(Response("/v3/apiClients", "post", "409"))
                .Should()
                .Contain(ProblemType(_unresolvedApplication));
        }

        [Test]
        public void It_reports_a_provider_failure_as_the_documented_502()
        {
            _providerFailure.Status.Should().Be(HttpStatusCode.BadGateway);
            Statuses(Operation("/v3/apiClients", "post")).Should().Contain("502");
        }

        [Test]
        public void It_rejects_a_non_fitting_move_as_the_documented_scope_conflict()
        {
            _nonFittingMove.Status.Should().Be(HttpStatusCode.Conflict);
            ProblemType(_nonFittingMove)
                .Should()
                .Be("urn:ed-fi:management-api:conflict:education-organization-scope");
            Description(Response("/v3/apiClients/{id}", "put", "409"))
                .Should()
                .Contain(ProblemType(_nonFittingMove));
        }

        [Test]
        public void It_returns_reads_matching_the_schemas_their_operations_publish()
        {
            JsonNode itemResponse = Response("/v3/apiClients/{id}", "get", "200");
            JsonNode itemSchema = Resolve(itemResponse["content"]!["application/json"]!["schema"]!);
            JsonNode collectionResponse = Response("/v3/apiClients", "get", "200");
            JsonNode collectionSchema = collectionResponse["content"]!["application/json"]!["schema"]!;
            JsonNode collectionItemSchema = Resolve(collectionSchema["items"]!);

            TypesOf(collectionSchema).Should().Equal("array");
            AssertMatchesSchema(_readItem.Body.AsObject(), itemSchema);
            AssertMatchesSchema(_readEmptyItem.Body.AsObject(), itemSchema);
            _readCollection.Body.Should().BeOfType<JsonArray>().Which.Should().HaveCount(3);
            foreach (JsonNode? item in _readCollection.Body.AsArray())
            {
                AssertMatchesSchema(item!.AsObject(), collectionItemSchema);
            }

            foreach ((_, JsonNode? example) in ResponseExamples(itemResponse))
            {
                AssertMatchesSchema(example!["value"]!.AsObject(), itemSchema);
            }

            JsonNode collectionExample = ResponseExamples(collectionResponse)["clients"]!["value"]!;
            collectionExample.Should().BeOfType<JsonArray>();
            foreach (JsonNode? item in collectionExample.AsArray())
            {
                AssertMatchesSchema(item!.AsObject(), collectionItemSchema);
            }
        }

        [Test]
        public void It_returns_an_emptied_narrowed_client_with_an_empty_list()
        {
            _readEmptyItem.Body["inheritsApplicationEducationOrganizations"]!
                .GetValue<bool>()
                .Should()
                .BeFalse();
            _readEmptyItem
                .Body["educationOrganizationIds"]
                .Should()
                .BeOfType<JsonArray>()
                .Which.Should()
                .BeEmpty();
            ResponseExamples(Response("/v3/apiClients/{id}", "get", "200"))["narrowedToNothing"]!["value"]![
                "educationOrganizationIds"
            ]!
                .AsArray()
                .Should()
                .BeEmpty();
        }
    }
}
