// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend.Repositories;
using FakeItEasy;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Modules;

public partial class ApplicationModuleTests
{
    /// <summary>
    /// Asserts the Application contract against the document the application actually serves, and
    /// checks it against what the same pipeline returns: every published POST request example is
    /// sent, and the responses are matched to the published schemas.
    /// </summary>
    [TestFixture]
    public class Given_the_served_openapi_document_for_applications : CreateDefaultCredentialsTestBase
    {
        private const string DefaultCredentialsExample = "defaultCredentials";
        private const string WithoutDefaultCredentialsExample = "withoutDefaultCredentials";

        private JsonNode _document = JsonNode.Parse("{}")!;
        private readonly Dictionary<string, (HttpStatusCode Status, JsonObject Body)> _insertResponses = [];
        private HttpStatusCode _resetStatus;
        private JsonObject _resetBody = [];

        [SetUp]
        public async Task Setup()
        {
            ArrangeVendorAndInsert(new ApplicationInsertResult.Success(11));
            A.CallTo(() => _applicationRepository.GetApplicationApiClients(7))
                .Returns(new ApplicationApiClientsResult.Success([new("client-key", Guid.NewGuid(), true)]));
            A.CallTo(() => _clientRepository.ResetCredentialsAsync(A<string>.Ignored))
                .Returns(new ClientResetResult.Success("NEW_SECRET"));

            using var client = SetUpClient();
            using var documentResponse = await client.GetAsync("/openapi/v1.json");
            _document = JsonNode.Parse(await documentResponse.Content.ReadAsStringAsync())!;

            _insertResponses.Clear();
            // A document without examples posts nothing, so each test reports its own failure.
            JsonObject requestExamples =
                InsertOperation["requestBody"]?["content"]?["application/json"]?["examples"]?.AsObject()
                ?? [];
            foreach ((string name, JsonNode? example) in requestExamples)
            {
                using var response = await client.PostAsync(
                    "/v3/applications",
                    new StringContent(example!["value"]!.ToJsonString(), Encoding.UTF8, "application/json")
                );
                _insertResponses[name] = (
                    response.StatusCode,
                    JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject()
                );
            }

            using var resetResponse = await client.PutAsync("/v3/applications/7/reset-credential", null);
            _resetStatus = resetResponse.StatusCode;
            _resetBody = JsonNode.Parse(await resetResponse.Content.ReadAsStringAsync())!.AsObject();
        }

        private JsonNode Operation(string path, string method) => _document["paths"]![path]![method]!;

        private JsonNode Schema(string schemaName) => _document["components"]!["schemas"]![schemaName]!;

        private JsonNode InsertOperation => Operation("/v3/applications", "post");

        private JsonObject RequestExamples =>
            InsertOperation["requestBody"]!["content"]!["application/json"]!["examples"]!.AsObject();

        private JsonNode InsertCreatedContent =>
            InsertOperation["responses"]!["201"]!["content"]!["application/json"]!;

        private JsonNode Resolve(JsonNode schema) =>
            schema["$ref"] is JsonNode reference
                ? Schema(reference.GetValue<string>()["#/components/schemas/".Length..])
                : schema;

        private JsonNode InsertResponseSchema => Resolve(InsertCreatedContent["schema"]!);

        private JsonNode ResetResponseSchema =>
            Resolve(
                Operation("/v3/applications/{id}/reset-credential", "put")["responses"]!["200"]!["content"]![
                    "application/json"
                ]!["schema"]!
            );

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

        [Test]
        public void It_publishes_create_default_credentials_as_an_optional_boolean_defaulting_to_true()
        {
            JsonNode schema = Schema("ApplicationInsertCommand");
            JsonNode property = schema["properties"]!["createDefaultCredentials"]!;

            TypesOf(property).Should().Equal("boolean");
            property["default"]!.GetValue<bool>().Should().BeTrue();
            (schema["required"]?.AsArray().Select(entry => entry!.GetValue<string>()) ?? [])
                .Should()
                .NotContain("createDefaultCredentials");
        }

        [Test]
        public void It_documents_that_enabled_and_data_store_ids_are_rejected_with_any_value()
        {
            string description = Schema("ApplicationInsertCommand")["properties"]![
                "createDefaultCredentials"
            ]!["description"]!.GetValue<string>();

            description.Should().Contain("omit enabled and dataStoreIds");
            description.Should().Contain("whatever its value, including enabled: false and dataStoreIds: []");
            InsertOperation["description"]!
                .GetValue<string>()
                .Should()
                .Contain("with any value, is rejected");
            InsertOperation["responses"]!["400"]!["description"]!
                .GetValue<string>()
                .Should()
                .Contain("enabled or dataStoreIds sent with createDefaultCredentials false");
        }

        [Test]
        public void It_keeps_presence_tracking_members_out_of_the_request_schema()
        {
            JsonNode schema = Schema("ApplicationInsertCommand");

            schema["properties"]!
                .AsObject()
                .Select(member => member.Key)
                .Should()
                .BeEquivalentTo(
                    "applicationName",
                    "vendorId",
                    "claimSetName",
                    "educationOrganizationIds",
                    "dataStoreIds",
                    "profileIds",
                    "createDefaultCredentials"
                );
            // Unknown members are accepted at runtime, so the schema must not forbid them.
            schema["additionalProperties"].Should().BeNull();
        }

        [Test]
        public void It_publishes_a_request_example_for_each_credentials_mode() =>
            RequestExamples
                .Select(member => member.Key)
                .Should()
                .BeEquivalentTo(DefaultCredentialsExample, WithoutDefaultCredentialsExample);

        [Test]
        public void It_omits_the_default_client_fields_from_the_example_without_default_credentials()
        {
            JsonObject example = RequestExamples[WithoutDefaultCredentialsExample]!["value"]!.AsObject();

            example["createDefaultCredentials"]!.GetValue<bool>().Should().BeFalse();
            example.Should().NotContainKey("enabled");
            example.Should().NotContainKey("dataStoreIds");
        }

        [Test]
        public void It_does_not_disable_default_credentials_in_the_default_example() =>
            RequestExamples[DefaultCredentialsExample]!["value"]!
                .AsObject()
                .Should()
                .NotContainKey("createDefaultCredentials");

        [TestCase(DefaultCredentialsExample)]
        [TestCase(WithoutDefaultCredentialsExample)]
        public void It_creates_the_application_from_the_published_request_example(string name) =>
            _insertResponses[name].Status.Should().Be(HttpStatusCode.Created);

        [TestCase(DefaultCredentialsExample)]
        [TestCase(WithoutDefaultCredentialsExample)]
        public void It_returns_a_body_matching_the_published_created_schema(string name) =>
            AssertMatchesSchema(_insertResponses[name].Body, InsertResponseSchema);

        [Test]
        public void It_returns_present_null_credentials_without_default_credentials()
        {
            JsonObject body = _insertResponses[WithoutDefaultCredentialsExample].Body;

            body.Should().ContainKey("key");
            body.Should().ContainKey("secret");
            body["key"].Should().BeNull();
            body["secret"].Should().BeNull();
        }

        [Test]
        public void It_returns_string_credentials_with_default_credentials()
        {
            JsonObject body = _insertResponses[DefaultCredentialsExample].Body;

            JsonTypeOf(body["key"]).Should().Be("string");
            JsonTypeOf(body["secret"]).Should().Be("string");
        }

        [Test]
        public void It_publishes_the_credentials_as_required_and_nullable_in_the_created_schema()
        {
            Required(InsertResponseSchema).Should().BeEquivalentTo("id", "key", "secret");
            TypesOf(InsertResponseSchema["properties"]!["key"]!).Should().BeEquivalentTo("null", "string");
            TypesOf(InsertResponseSchema["properties"]!["secret"]!).Should().BeEquivalentTo("null", "string");
        }

        [TestCase(DefaultCredentialsExample)]
        [TestCase(WithoutDefaultCredentialsExample)]
        public void It_publishes_created_examples_matching_the_created_schema(string name) =>
            AssertMatchesSchema(
                InsertCreatedContent["examples"]![name]!["value"]!.AsObject(),
                InsertResponseSchema
            );

        [Test]
        public void It_publishes_null_credentials_in_the_created_example_without_default_credentials()
        {
            JsonObject example = InsertCreatedContent["examples"]![WithoutDefaultCredentialsExample]![
                "value"
            ]!.AsObject();

            JsonTypeOf(example["key"]).Should().Be("null");
            JsonTypeOf(example["secret"]).Should().Be("null");
        }

        [TestCase("/v3/applications", "get")]
        [TestCase("/v3/applications/{id}", "get")]
        [TestCase("/v3/vendors/{id}/applications", "get")]
        public void It_documents_the_zero_client_state_on_application_reads(string path, string method)
        {
            string description = Operation(path, method)["description"]!.GetValue<string>();

            description
                .Should()
                .Contain("enabled is true only when the Application has at least one API client");
            description.Should().Contain("every one is approved");
            description.Should().Contain("no API clients reports false");
            description.Should().Contain("[] for an Application with no API clients");
        }

        [Test]
        public void It_documents_the_zero_client_state_on_the_application_response_schema()
        {
            JsonNode properties = Schema("ApplicationResponse")["properties"]!;

            string enabled = properties["enabled"]!["description"]!.GetValue<string>();
            enabled.Should().Contain("True only when there is at least one API client");
            enabled.Should().Contain("is approved");
            enabled.Should().Contain("no API clients reports false");
            properties["dataStoreIds"]!["description"]!
                .GetValue<string>()
                .Should()
                .Contain("no API clients reports an empty array");
        }

        [Test]
        public void It_publishes_the_reset_success_with_non_null_credentials()
        {
            Required(ResetResponseSchema).Should().Contain(["key", "secret"]);
            TypesOf(ResetResponseSchema["properties"]!["key"]!).Should().Equal("string");
            TypesOf(ResetResponseSchema["properties"]!["secret"]!).Should().Equal("string");
        }

        [Test]
        public void It_returns_a_reset_body_matching_the_published_success_schema()
        {
            _resetStatus.Should().Be(HttpStatusCode.OK);
            AssertMatchesSchema(_resetBody, ResetResponseSchema);
        }

        [TestCase("404")]
        [TestCase("409")]
        public void It_publishes_the_reset_failure_as_problem_json(string status) =>
            Operation("/v3/applications/{id}/reset-credential", "put")["responses"]![status]!["content"]!
                ["application/problem+json"]
                .Should()
                .NotBeNull();

        [Test]
        public void It_documents_the_reset_conflict_for_zero_or_multiple_clients()
        {
            JsonNode operation = Operation("/v3/applications/{id}/reset-credential", "put");

            operation["responses"]!["409"]!["description"]!
                .GetValue<string>()
                .Should()
                .StartWith("The Application has no API clients or more than one");
            operation["responses"]!["404"]!["description"]!
                .GetValue<string>()
                .Should()
                .Be("The Application does not exist.");
            operation["description"]!
                .GetValue<string>()
                .Should()
                .Contain("PUT /v3/apiClients/{id}/reset-credential");
        }

        [Test]
        public void It_publishes_the_api_client_delete_success_as_no_content()
        {
            JsonObject responses = Operation("/v3/apiClients/{id}", "delete")["responses"]!.AsObject();

            responses.Should().ContainKey("204");
            responses.Should().NotContainKey("200");
            responses["204"]!["content"].Should().BeNull();
        }

        [Test]
        public void It_documents_that_deleting_the_last_api_client_keeps_the_application() =>
            Operation("/v3/apiClients/{id}", "delete")["description"]!
                .GetValue<string>()
                .Should()
                .Contain("Deleting an Application's last API client keeps the Application");

        private JsonNode UpdateOperation => Operation("/v3/applications/{id}", "put");

        [Test]
        public void It_documents_the_update_as_saved_first_then_synchronized_with_every_affected_client() =>
            UpdateOperation["description"]!
                .GetValue<string>()
                .Should()
                .Contain("The change is saved first")
                .And.Contain("marks every API client it affects as pending")
                .And.Contain("narrowed clients that held a removed one")
                .And.Contain("including any left pending by an earlier request")
                .And.Contain("send the same request again to retry only the clients still pending")
                .And.Contain("tokens already issued keep their claims until they expire")
                .And.Contain(
                    "An Application with no API clients is updated without an identity provider call"
                );

        [Test]
        public void It_documents_each_update_status() =>
            UpdateOperation["responses"]!
                .AsObject()
                .Select(response => response.Key)
                .Should()
                .Contain(["204", "400", "404", "409", "500", "502"]);

        [Test]
        public void It_documents_the_partial_sync_502()
        {
            JsonNode response = UpdateOperation["responses"]!["502"]!;
            response["description"]!
                .GetValue<string>()
                .Should()
                .Contain("The Application change is saved")
                .And.Contain("urn:ed-fi:management-api:bad-gateway:partial-sync")
                .And.Contain("apiClientIds lists their numeric ids");

            JsonNode example = response["content"]!["application/problem+json"]!["example"]!;
            example["status"]!.GetValue<int>().Should().Be(502);
            example["type"]!
                .GetValue<string>()
                .Should()
                .Be("urn:ed-fi:management-api:bad-gateway:partial-sync");
            example["apiClientIds"]!.AsArray().Select(id => id!.GetValue<int>()).Should().Equal(12, 15);
        }
    }
}
