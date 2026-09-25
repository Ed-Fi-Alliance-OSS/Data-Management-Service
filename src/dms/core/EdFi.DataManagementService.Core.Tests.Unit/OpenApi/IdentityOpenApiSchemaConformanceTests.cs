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
public class IdentityOpenApiSchemaConformanceTests
{
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

    [TestFixture]
    [Parallelizable]
    public class Given_The_Find_And_Search_200_Schema
    {
        private EvaluationResults _completeResult = null!;
        private EvaluationResults _incompleteResult = null!;
        private EvaluationResults _noMatchResult = null!;

        [SetUp]
        public void Setup()
        {
            JsonSchema schema = BuildSchemaFor("/identities/find", "post", "200");

            _completeResult = schema.Evaluate(ExampleValue("/identities/find", "post", "200", "complete"));
            _incompleteResult = schema.Evaluate(JsonNode.Parse("""{"Status":"Incomplete"}""")!);
            _noMatchResult = schema.Evaluate(ExampleValue("/identities/find", "post", "200", "noMatch"));
        }

        [Test]
        public void It_accepts_the_complete_synchronous_payload()
        {
            _completeResult.IsValid.Should().BeTrue();
        }

        [Test]
        public void It_rejects_an_incomplete_synchronous_payload()
        {
            _incompleteResult.IsValid.Should().BeFalse();
        }

        [Test]
        public void It_accepts_a_no_match_group_with_empty_responses()
        {
            _noMatchResult.IsValid.Should().BeTrue();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_Results_200_Schema
    {
        private EvaluationResults _completeResult = null!;
        private EvaluationResults _incompleteResult = null!;

        [SetUp]
        public void Setup()
        {
            JsonSchema schema = BuildSchemaFor("/identities/results/{id}", "get", "200");

            _completeResult = schema.Evaluate(
                ExampleValue("/identities/results/{id}", "get", "200", "complete")
            );
            _incompleteResult = schema.Evaluate(
                ExampleValue("/identities/results/{id}", "get", "200", "incomplete")
            );
        }

        [Test]
        public void It_accepts_the_complete_results_payload()
        {
            _completeResult.IsValid.Should().BeTrue();
        }

        [Test]
        public void It_accepts_the_pending_incomplete_results_payload()
        {
            _incompleteResult.IsValid.Should().BeTrue();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_Pinned_400_Examples
    {
        /// <summary>
        /// The fixed <see cref="TraceId" /> the served document's four pinned 400 examples were
        /// generated with (their <c>correlationId</c> is <c>0HNOOQ2BHB6VR</c>), so re-running
        /// <see cref="IdentityErrorProjection.Project" /> with the same trace id reproduces the same
        /// body.
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

        [TestCase("createFieldError")]
        [TestCase("searchItemError")]
        [TestCase("pathlessError")]
        [TestCase("twoMessagesOneKey")]
        public void It_equals_the_projection_output_after_canonical_serialization(string exampleName)
        {
            JsonNode pinnedExample = Resolve(Paths["/identities"]!["post"]!["responses"]!["400"]!)[
                "content"
            ]!["application/problem+json"]!["examples"]![exampleName]!["value"]!;

            JsonNode projected = IdentityErrorProjection.Project(
                ExampleErrorsByName[exampleName],
                PinnedTraceId
            );

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
        /// Serializes a node with object keys sorted recursively and no extraneous whitespace, so two
        /// JSON documents that differ only in property order or formatting compare equal.
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
    }

    /// <summary>
    /// Story D14's negative control: a <c>nullable</c> string schema rejects a JSON <c>null</c> before
    /// <see cref="OpenApiSchemaNormalizer" /> runs (OpenAPI's <c>nullable</c> keyword is meaningless to
    /// a JSON Schema evaluator) and accepts it after normalization converts <c>type</c> into an array
    /// that includes <c>"null"</c>.
    /// </summary>
    public class OpenApiSchemaNormalizerTests
    {
        [TestFixture]
        [Parallelizable]
        public class Given_A_Nullable_String_Schema
        {
            /// <summary>
            /// Kept as a single test so the before/after contrast stays atomic: splitting it into two
            /// <c>It_</c> methods would let one half regress without the other half's evaluation ever
            /// running against the same schema instance.
            /// </summary>
            [Test]
            public void It_rejects_null_before_normalization_and_accepts_it_after()
            {
                JsonObject nullableStringSchema = new() { ["type"] = "string", ["nullable"] = true };

                JsonSchema beforeNormalization = JsonSchema.FromText(nullableStringSchema.ToJsonString());
                beforeNormalization.Evaluate(null).IsValid.Should().BeFalse();

                JsonNode normalized = OpenApiSchemaNormalizer.Normalize(nullableStringSchema.DeepClone());
                JsonSchema afterNormalization = JsonSchema.FromText(normalized.ToJsonString());
                afterNormalization.Evaluate(null).IsValid.Should().BeTrue();
            }
        }

        [TestFixture]
        [Parallelizable]
        public class Given_A_Nullable_Ref_Wrapped_In_AllOf
        {
            private JsonNode _normalized = null!;

            [SetUp]
            public void Setup()
            {
                JsonObject nullableRefSchema = new()
                {
                    ["allOf"] = new JsonArray(new JsonObject { ["$ref"] = "#/components/schemas/Location" }),
                    ["nullable"] = true,
                };

                _normalized = OpenApiSchemaNormalizer.Normalize(nullableRefSchema);
            }

            [Test]
            public void It_removes_allOf()
            {
                _normalized["allOf"].Should().BeNull();
            }

            [Test]
            public void It_removes_nullable()
            {
                _normalized["nullable"].Should().BeNull();
            }

            [Test]
            public void It_rewrites_the_ref_into_the_first_anyOf_member()
            {
                _normalized["anyOf"]![0]!["$ref"]!.GetValue<string>().Should().Be("#/$defs/Location");
            }

            [Test]
            public void It_adds_a_null_type_as_the_second_anyOf_member()
            {
                _normalized["anyOf"]![1]!["type"]!.GetValue<string>().Should().Be("null");
            }
        }
    }
}
