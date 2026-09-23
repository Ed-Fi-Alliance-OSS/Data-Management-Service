// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Identity;
using EdFi.DataManagementService.Core.OpenApi;
using EdFi.DataManagementService.Identity;
using FluentAssertions;
using Json.Schema;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.OpenApi;

/// <summary>
/// Story D4/D14: payload-versus-served-schema assertions, evaluated with <c>JsonSchema.Net</c> after
/// <see cref="OpenApiSchemaNormalizer" /> rewrites the OpenAPI 3.0 component schemas into a JSON Schema
/// draft the evaluator accepts.
/// </summary>
[TestFixture]
public class IdentityOpenApiSchemaConformanceTests
{
    /// <summary>
    /// The fixed <see cref="TraceId" /> the served document's four pinned 400 examples were generated
    /// with (their <c>correlationId</c> is <c>0HNOOQ2BHB6VR</c>), so re-running
    /// <see cref="IdentityErrorProjection.Project" /> with the same trace id reproduces the same body.
    /// </summary>
    private static readonly TraceId PinnedTraceId = new("0HNOOQ2BHB6VR");

    private static readonly Dictionary<string, IReadOnlyList<IdentityError>> ExampleErrorsByName = new()
    {
        ["createFieldError"] = [new() { Path = "$.firstName", Message = "First name is required." }],
        ["searchItemError"] =
        [
            new() { Path = "$[2].firstName", Message = "First name is required for item 2." },
        ],
        ["pathlessError"] = [new() { Path = null, Message = "The request could not be evaluated." }],
        ["twoMessagesOneKey"] =
        [
            new() { Path = "$.firstName", Message = "First name is required." },
            new() { Path = "$.firstName", Message = "First name must not exceed 75 characters." },
        ],
    };

    private static JsonNode Document => IdentityOpenApiDocument.Document;

    private static JsonObject Schemas => Document["components"]!["schemas"]!.AsObject();

    private static JsonObject Paths => Document["paths"]!.AsObject();

    private static JsonSchema BuildSchemaFor(string path, string method, string status)
    {
        JsonNode rootSchema = Paths[path]![method]!["responses"]![status]!["content"]!["application/json"]![
            "schema"
        ]!;
        return OpenApiSchemaNormalizer.BuildJsonSchema(Schemas, rootSchema);
    }

    private static JsonNode ExampleValue(string path, string method, string status, string exampleName)
    {
        return Paths[path]![method]!["responses"]![status]!["content"]!["application/json"]!["examples"]![
            exampleName
        ]!["value"]!;
    }

    [Test]
    public void Complete_synchronous_payload_validates_against_the_find_and_search_200_schema()
    {
        JsonSchema schema = BuildSchemaFor("/identities/find", "post", "200");
        JsonNode payload = ExampleValue("/identities/find", "post", "200", "complete");

        schema.Evaluate(payload).IsValid.Should().BeTrue();
    }

    [Test]
    public void Incomplete_synchronous_payload_does_not_validate_against_the_find_and_search_200_schema()
    {
        JsonSchema schema = BuildSchemaFor("/identities/find", "post", "200");
        JsonNode payload = JsonNode.Parse("""{"Status":"Incomplete"}""")!;

        schema.Evaluate(payload).IsValid.Should().BeFalse();
    }

    [Test]
    public void No_match_group_with_empty_responses_validates_against_the_find_and_search_200_schema()
    {
        JsonSchema schema = BuildSchemaFor("/identities/find", "post", "200");
        JsonNode payload = ExampleValue("/identities/find", "post", "200", "noMatch");

        schema.Evaluate(payload).IsValid.Should().BeTrue();
    }

    [Test]
    public void The_complete_results_payload_validates_against_the_results_200_schema()
    {
        JsonSchema schema = BuildSchemaFor("/identities/results/{id}", "get", "200");
        JsonNode payload = ExampleValue("/identities/results/{id}", "get", "200", "complete");

        schema.Evaluate(payload).IsValid.Should().BeTrue();
    }

    [Test]
    public void The_pending_incomplete_results_payload_validates_against_the_results_200_schema()
    {
        JsonSchema schema = BuildSchemaFor("/identities/results/{id}", "get", "200");
        JsonNode payload = ExampleValue("/identities/results/{id}", "get", "200", "incomplete");

        schema.Evaluate(payload).IsValid.Should().BeTrue();
    }

    [TestCase("createFieldError")]
    [TestCase("searchItemError")]
    [TestCase("pathlessError")]
    [TestCase("twoMessagesOneKey")]
    public void The_pinned_400_example_equals_the_projection_output_after_canonical_serialization(
        string exampleName
    )
    {
        JsonNode pinnedExample = Resolve(Paths["/identities"]!["post"]!["responses"]!["400"]!)["content"]![
            "application/problem+json"
        ]!["examples"]![exampleName]!["value"]!;

        JsonNode projected = IdentityErrorProjection.Project(ExampleErrorsByName[exampleName], PinnedTraceId);

        CanonicalJson(projected).Should().Be(CanonicalJson(pinnedExample));
    }

    private static JsonNode Resolve(JsonNode node)
    {
        string? refValue = node["$ref"]?.GetValue<string>();
        if (refValue is null)
        {
            return node;
        }

        JsonNode current = Document;
        foreach (string segment in refValue[2..].Split('/'))
        {
            current = current[segment]!;
        }
        return current;
    }

    /// <summary>
    /// Serializes a node with object keys sorted recursively and no extraneous whitespace, so two JSON
    /// documents that differ only in property order or formatting compare equal.
    /// </summary>
    private static string CanonicalJson(JsonNode node)
    {
        return SortKeys(node).ToJsonString();
    }

    private static JsonNode SortKeys(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                JsonObject sorted = [];
                foreach (
                    string key in obj.Select(pair => pair.Key).OrderBy(key => key, StringComparer.Ordinal)
                )
                {
                    JsonNode? value = obj[key];
                    sorted[key] = value is null ? null : SortKeys(value.DeepClone());
                }
                return sorted;
            case JsonArray array:
                JsonArray result = [];
                foreach (JsonNode? item in array)
                {
                    result.Add(item is null ? null : SortKeys(item.DeepClone()));
                }
                return result;
            default:
                return node.DeepClone();
        }
    }

    /// <summary>
    /// Story D14's negative control: a <c>nullable</c> string schema rejects a JSON <c>null</c> before
    /// <see cref="OpenApiSchemaNormalizer" /> runs (OpenAPI's <c>nullable</c> keyword is meaningless to
    /// a JSON Schema evaluator) and accepts it after normalization converts <c>type</c> into an array
    /// that includes <c>"null"</c>.
    /// </summary>
    [TestFixture]
    public class OpenApiSchemaNormalizerTests
    {
        [Test]
        public void A_nullable_string_schema_rejects_null_before_normalization_and_accepts_it_after()
        {
            JsonObject nullableStringSchema = new() { ["type"] = "string", ["nullable"] = true };

            JsonSchema beforeNormalization = JsonSchema.FromText(nullableStringSchema.ToJsonString());
            beforeNormalization.Evaluate(null).IsValid.Should().BeFalse();

            JsonNode normalized = OpenApiSchemaNormalizer.Normalize(nullableStringSchema.DeepClone());
            JsonSchema afterNormalization = JsonSchema.FromText(normalized.ToJsonString());
            afterNormalization.Evaluate(null).IsValid.Should().BeTrue();
        }

        [Test]
        public void A_nullable_ref_wrapped_in_allOf_becomes_an_anyOf_of_the_ref_and_null()
        {
            JsonObject nullableRefSchema = new()
            {
                ["allOf"] = new JsonArray(new JsonObject { ["$ref"] = "#/components/schemas/Location" }),
                ["nullable"] = true,
            };

            JsonNode normalized = OpenApiSchemaNormalizer.Normalize(nullableRefSchema);

            normalized["allOf"].Should().BeNull();
            normalized["nullable"].Should().BeNull();
            normalized["anyOf"]![0]!["$ref"]!.GetValue<string>().Should().Be("#/$defs/Location");
            normalized["anyOf"]![1]!["type"]!.GetValue<string>().Should().Be("null");
        }
    }
}
