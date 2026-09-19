// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using FluentAssertions;
using NUnit.Framework;
using static EdFi.DataManagementService.Core.Tests.Unit.OpenApi.OpenApiSnapshotContractAssertions;

namespace EdFi.DataManagementService.Core.Tests.Unit.OpenApi;

/// <summary>
/// Self-tests for the snapshot OpenAPI inspection primitives.
/// </summary>
/// <remarks>
/// The served-document fixtures lean on these to prove a document is self-resolving, so a resolver
/// that quietly found nothing would turn every one of those fixtures into a test that passes because
/// it never looked. These fixtures therefore spend most of their effort on the negative direction:
/// each way a reference can fail gets a document that fails that way, and the resolver has to name it.
/// </remarks>
[TestFixture]
public class OpenApiSnapshotContractAssertionsTests
{
    /// <summary>
    /// A document shaped like a served resource document: a collection, an item, the two tracked-change
    /// feeds, a partitions path, and the standalone Change Queries route, referencing a reusable
    /// parameter and the two snapshot responses that every one of them shares.
    /// </summary>
    private const string SelfResolvingDocument = """
        {
          "openapi": "3.0.1",
          "info": { "title": "Ed-Fi API", "version": "8.0.0" },
          "paths": {
            "/ed-fi/schools": {
              "parameters": [ { "$ref": "#/components/parameters/tenant" } ],
              "get": {
                "parameters": [
                  { "$ref": "#/components/parameters/Use-Snapshot" },
                  { "name": "limit", "in": "query", "schema": { "type": "integer" } }
                ],
                "responses": {
                  "200": { "description": "OK" },
                  "404": { "$ref": "#/components/responses/SnapshotNotFound" }
                }
              },
              "post": {
                "responses": {
                  "201": { "description": "Created" },
                  "405": { "$ref": "#/components/responses/SnapshotMethodNotAllowed" }
                }
              }
            },
            "/ed-fi/schools/{id}": {
              "get": {
                "parameters": [ { "$ref": "#/components/parameters/Use-Snapshot" } ],
                "responses": { "404": { "$ref": "#/components/responses/SnapshotNotFound" } }
              },
              "delete": {
                "responses": { "405": { "$ref": "#/components/responses/SnapshotMethodNotAllowed" } }
              }
            },
            "/ed-fi/schools/deletes": {
              "get": {
                "parameters": [ { "$ref": "#/components/parameters/Use-Snapshot" } ],
                "responses": { "404": { "$ref": "#/components/responses/SnapshotNotFound" } }
              }
            },
            "/ed-fi/schools/keyChanges": {
              "get": {
                "parameters": [ { "$ref": "#/components/parameters/Use-Snapshot" } ],
                "responses": { "404": { "$ref": "#/components/responses/SnapshotNotFound" } }
              }
            },
            "/ed-fi/schools/partitions": {
              "get": { "responses": { "200": { "description": "OK" } } }
            },
            "/availableChangeVersions": {
              "get": {
                "parameters": [ { "$ref": "#/components/parameters/Use-Snapshot" } ],
                "responses": { "404": { "$ref": "#/components/responses/SnapshotNotFound" } }
              }
            }
          },
          "components": {
            "parameters": {
              "tenant": { "name": "tenant", "in": "path", "schema": { "type": "string" } },
              "Use-Snapshot": {
                "name": "Use-Snapshot",
                "in": "header",
                "schema": { "type": "boolean", "default": false }
              }
            },
            "responses": {
              "SnapshotNotFound": {
                "description": "Snapshot not found.",
                "content": {
                  "application/problem+json": {
                    "schema": { "$ref": "#/components/schemas/ProblemDetails" },
                    "example": { "$ref": "this is data, not a reference" }
                  }
                }
              },
              "SnapshotMethodNotAllowed": {
                "description": "Method Not Allowed with Snapshots",
                "headers": { "Allow": { "schema": { "type": "string" }, "example": "GET" } },
                "content": {
                  "application/problem+json": {
                    "schema": { "$ref": "#/components/schemas/ProblemDetails" }
                  }
                }
              }
            },
            "schemas": { "ProblemDetails": { "type": "object" } }
          }
        }
        """;

    private static JsonNode SelfResolving() => Parse(SelfResolvingDocument, "test");

    [TestFixture]
    public class Given_a_self_resolving_document : OpenApiSnapshotContractAssertionsTests
    {
        private JsonNode _document = null!;

        [SetUp]
        public void Setup() => _document = SelfResolving();

        [Test]
        public void It_reports_no_unresolved_references()
        {
            FindUnresolvedReferences(_document).Should().BeEmpty();
        }

        [Test]
        public void It_passes_the_self_resolution_assertion()
        {
            Action assert = () => AssertSelfResolves(_document, "resources");

            assert.Should().NotThrow();
        }

        [Test]
        public void It_treats_a_ref_shaped_value_inside_an_example_as_data()
        {
            // The example carries a "$ref" whose value resolves to nothing. It is a response body
            // sample rather than a reference, so reporting it would be a false positive.
            FindUnresolvedReferences(_document)
                .Should()
                .NotContain(reference => reference.Reference.Contains("this is data"));
        }

        [Test]
        public void It_enumerates_every_operation_across_every_path()
        {
            EnumerateOperations(_document)
                .Select(operation => operation.ToString())
                .Should()
                .BeEquivalentTo(
                    "GET /ed-fi/schools",
                    "POST /ed-fi/schools",
                    "GET /ed-fi/schools/{id}",
                    "DELETE /ed-fi/schools/{id}",
                    "GET /ed-fi/schools/deletes",
                    "GET /ed-fi/schools/keyChanges",
                    "GET /ed-fi/schools/partitions",
                    "GET /availableChangeVersions"
                );
        }

        [Test]
        public void It_classifies_each_path_by_its_suffix()
        {
            ClassifyPath("/ed-fi/schools").Should().Be(OperationPathKind.Collection);
            ClassifyPath("/ed-fi/schools/{id}").Should().Be(OperationPathKind.Item);
            ClassifyPath("/ed-fi/schools/deletes").Should().Be(OperationPathKind.Deletes);
            ClassifyPath("/ed-fi/schools/keyChanges").Should().Be(OperationPathKind.KeyChanges);
            ClassifyPath("/ed-fi/schools/partitions").Should().Be(OperationPathKind.Partitions);
            ClassifyPath("/availableChangeVersions").Should().Be(OperationPathKind.AvailableChangeVersions);
        }

        [Test]
        public void It_merges_path_level_parameters_into_every_operation_beneath_them()
        {
            // The tenant parameter is declared once on the path item. Both operations must see it,
            // otherwise a fixture would fail merely because upstream hoisted a shared parameter.
            EnumerateOperations(_document)
                .Where(operation => operation.PathKey == "/ed-fi/schools")
                .Should()
                .OnlyContain(operation =>
                    operation.ParameterReferences.Contains("#/components/parameters/tenant")
                );
        }

        [Test]
        public void It_collects_inline_parameter_names_alongside_referenced_ones()
        {
            Operation collectionGet = OperationFor(_document, "GET /ed-fi/schools");

            collectionGet.InlineParameterNames.Should().Contain("limit");
            collectionGet.ParameterReferences.Should().Contain(UseSnapshotParameterReference);
        }

        [Test]
        public void It_recognizes_the_snapshot_parameter_on_every_snapshot_eligible_read()
        {
            EnumerateOperations(_document)
                .Where(operation => operation.ReferencesUseSnapshotParameter)
                .Select(operation => operation.ToString())
                .Should()
                .BeEquivalentTo(
                    "GET /ed-fi/schools",
                    "GET /ed-fi/schools/{id}",
                    "GET /ed-fi/schools/deletes",
                    "GET /ed-fi/schools/keyChanges",
                    "GET /availableChangeVersions"
                );
        }

        [Test]
        public void It_recognizes_the_snapshot_not_found_response_on_every_snapshot_eligible_read()
        {
            EnumerateOperations(_document)
                .Where(operation => operation.ReferencesSnapshotNotFound)
                .Select(operation => operation.ToString())
                .Should()
                .BeEquivalentTo(
                    "GET /ed-fi/schools",
                    "GET /ed-fi/schools/{id}",
                    "GET /ed-fi/schools/deletes",
                    "GET /ed-fi/schools/keyChanges",
                    "GET /availableChangeVersions"
                );
        }

        [Test]
        public void It_recognizes_the_snapshot_method_not_allowed_response_on_every_mutation()
        {
            EnumerateOperations(_document)
                .Where(operation => operation.ReferencesSnapshotMethodNotAllowed)
                .Select(operation => operation.ToString())
                .Should()
                .BeEquivalentTo("POST /ed-fi/schools", "DELETE /ed-fi/schools/{id}");
        }

        [Test]
        public void It_records_the_status_codes_an_operation_declares()
        {
            OperationFor(_document, "GET /ed-fi/schools")
                .ResponseStatusCodes.Should()
                .BeEquivalentTo("200", "404");
        }

        [Test]
        public void It_accepts_the_published_snapshot_parameter_shape()
        {
            Action assert = () => AssertUseSnapshotParameterShape(_document, "resources");

            assert.Should().NotThrow();
        }

        private static Operation OperationFor(JsonNode document, string description) =>
            EnumerateOperations(document).Single(operation => operation.ToString() == description);
    }

    [TestFixture]
    public class Given_a_document_missing_a_referenced_component : OpenApiSnapshotContractAssertionsTests
    {
        private JsonNode _document = null!;

        [SetUp]
        public void Setup()
        {
            _document = SelfResolving();
            _document["components"]!["parameters"]!.AsObject().Remove(UseSnapshotParameterName);
        }

        [Test]
        public void It_reports_one_unresolved_reference_per_operation_that_referenced_it()
        {
            FindUnresolvedReferences(_document)
                .Should()
                .HaveCount(5)
                .And.OnlyContain(reference => reference.Reference == UseSnapshotParameterReference);
        }

        [Test]
        public void It_names_the_operation_the_broken_reference_sits_in()
        {
            FindUnresolvedReferences(_document)
                .Select(reference => reference.Location)
                .Should()
                .Contain(location => location.Contains("['/ed-fi/schools/deletes'].get"));
        }

        [Test]
        public void It_explains_which_entry_is_missing()
        {
            FindUnresolvedReferences(_document)
                .Should()
                .OnlyContain(reference => reference.Reason.Contains(UseSnapshotParameterName));
        }

        [Test]
        public void It_fails_the_self_resolution_assertion()
        {
            Action assert = () => AssertSelfResolves(_document, "resources");

            assert.Should().Throw<Exception>().WithMessage("*resources*");
        }
    }

    [TestFixture]
    public class Given_documents_with_other_broken_references : OpenApiSnapshotContractAssertionsTests
    {
        [Test]
        public void It_reports_a_reference_into_another_document()
        {
            JsonNode document = SelfResolving();
            document["paths"]!["/ed-fi/schools"]!["get"]!["responses"]!["404"] = new JsonObject
            {
                ["$ref"] = "resources-spec.json#/components/responses/SnapshotNotFound",
            };

            FindUnresolvedReferences(document)
                .Should()
                .ContainSingle()
                .Which.Reason.Should()
                .Contain("not a local reference");
        }

        [Test]
        public void It_reports_a_reference_that_is_not_a_string()
        {
            JsonNode document = SelfResolving();
            document["paths"]!["/ed-fi/schools"]!["get"]!["responses"]!["404"] = new JsonObject
            {
                ["$ref"] = 42,
            };

            FindUnresolvedReferences(document)
                .Should()
                .ContainSingle()
                .Which.Reason.Should()
                .Be("not a string");
        }

        [Test]
        public void It_reports_a_reference_that_resolves_to_a_null_entry()
        {
            JsonNode document = SelfResolving();
            document["components"]!["schemas"]!.AsObject()["ProblemDetails"] = null;

            FindUnresolvedReferences(document)
                .Should()
                .HaveCount(2)
                .And.OnlyContain(reference => reference.Reason == "resolves to null");
        }

        [Test]
        public void It_reports_a_reference_that_cannot_be_traversed()
        {
            JsonNode document = SelfResolving();
            document["paths"]!["/ed-fi/schools"]!["get"]!["responses"]!["404"] = new JsonObject
            {
                ["$ref"] = "#/components/responses/SnapshotNotFound/description/deeper",
            };

            FindUnresolvedReferences(document)
                .Should()
                .ContainSingle()
                .Which.Reason.Should()
                .Contain("cannot traverse");
        }

        [Test]
        public void It_reports_a_fragment_that_is_not_a_json_pointer()
        {
            JsonNode document = SelfResolving();
            document["paths"]!["/ed-fi/schools"]!["get"]!["responses"]!["404"] = new JsonObject
            {
                ["$ref"] = "#SnapshotNotFound",
            };

            FindUnresolvedReferences(document)
                .Should()
                .ContainSingle()
                .Which.Reason.Should()
                .Be("not a JSON Pointer fragment");
        }
    }

    [TestFixture]
    public class Given_a_document_using_json_pointer_escapes : OpenApiSnapshotContractAssertionsTests
    {
        [Test]
        public void It_resolves_a_component_name_containing_a_slash_or_a_tilde()
        {
            JsonNode document = SelfResolving();
            document["components"]!["schemas"]!.AsObject()["Problem/Details~Extended"] = new JsonObject
            {
                ["type"] = "object",
            };
            document["paths"]!["/ed-fi/schools"]!["get"]!["responses"]!["200"] = new JsonObject
            {
                ["$ref"] = "#/components/schemas/Problem~1Details~0Extended",
            };

            FindUnresolvedReferences(document).Should().BeEmpty();
        }

        [Test]
        public void It_resolves_a_pointer_into_an_array()
        {
            JsonNode document = SelfResolving();
            document["paths"]!["/ed-fi/schools"]!["get"]!["responses"]!["200"] = new JsonObject
            {
                ["$ref"] = "#/paths/~1ed-fi~1schools/parameters/0",
            };

            FindUnresolvedReferences(document).Should().BeEmpty();
        }
    }

    [TestFixture]
    public class Given_a_snapshot_parameter_with_the_wrong_shape : OpenApiSnapshotContractAssertionsTests
    {
        [Test]
        public void It_fails_when_the_parameter_defaults_to_true()
        {
            JsonNode document = SelfResolving();
            document["components"]!["parameters"]![UseSnapshotParameterName]!["schema"]!["default"] = true;

            Action assert = () => AssertUseSnapshotParameterShape(document, "resources");

            assert.Should().Throw<Exception>();
        }

        [Test]
        public void It_fails_when_the_parameter_is_not_a_header()
        {
            JsonNode document = SelfResolving();
            document["components"]!["parameters"]![UseSnapshotParameterName]!["in"] = "query";

            Action assert = () => AssertUseSnapshotParameterShape(document, "resources");

            assert.Should().Throw<Exception>();
        }

        [Test]
        public void It_fails_when_the_parameter_is_absent()
        {
            JsonNode document = SelfResolving();
            document["components"]!["parameters"]!.AsObject().Remove(UseSnapshotParameterName);

            Action assert = () => AssertUseSnapshotParameterShape(document, "resources");

            assert.Should().Throw<Exception>();
        }

        // The three absences below are the ones a null-conditional walk reaches past: with nothing to
        // read, there is nothing to compare, and an assertion that never runs is an assertion that
        // never fails. A parameter that declares no type or no default advertises no contract at all,
        // so each has to be rejected as firmly as a wrong one.
        [Test]
        public void It_fails_when_the_parameter_declares_no_schema()
        {
            JsonNode document = SelfResolving();
            document["components"]!["parameters"]![UseSnapshotParameterName]!.AsObject().Remove("schema");

            Action assert = () => AssertUseSnapshotParameterShape(document, "resources");

            assert.Should().Throw<Exception>();
        }

        [Test]
        public void It_fails_when_the_parameter_schema_declares_no_type()
        {
            JsonNode document = SelfResolving();
            document["components"]!["parameters"]![UseSnapshotParameterName]!["schema"]!
                .AsObject()
                .Remove("type");

            Action assert = () => AssertUseSnapshotParameterShape(document, "resources");

            assert.Should().Throw<Exception>();
        }

        [Test]
        public void It_fails_when_the_parameter_schema_declares_no_default()
        {
            JsonNode document = SelfResolving();
            document["components"]!["parameters"]![UseSnapshotParameterName]!["schema"]!
                .AsObject()
                .Remove("default");

            Action assert = () => AssertUseSnapshotParameterShape(document, "resources");

            assert.Should().Throw<Exception>();
        }
    }
}
