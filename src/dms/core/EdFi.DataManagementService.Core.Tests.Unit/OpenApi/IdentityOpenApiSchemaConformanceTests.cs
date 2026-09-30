// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Identity;
using EdFi.DataManagementService.Core.OpenApi;
using EdFi.DataManagementService.Core.Response;
using EdFi.DataManagementService.Identity;
using FluentAssertions;
using Json.Schema;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.OpenApi;

/// <summary>
/// Payload-versus-served-schema assertions, evaluated with <c>JsonSchema.Net</c> after
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

    /// <summary>
    /// Follows a <c>#/components/...</c> reference to the node it names within the served document,
    /// or returns the node unchanged when it is not a reference.
    /// </summary>
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
    /// The shared authorization stages every identity operation runs (tenant existence, client
    /// binding, JWT authentication, and service-claim authorization) answer with DMS's generic
    /// problem bodies rather than identity-specific ones, so each of those actual bodies must
    /// validate against the schema the served document declares for that status on every operation.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_The_Shared_Authorization_Stage_Problem_Bodies
    {
        private static readonly TraceId TraceId = new("0HNOOQ2BHB6VR");

        private static readonly (string Path, string Method)[] Operations =
        [
            ("/identities", "post"),
            ("/identities/{id}", "get"),
            ("/identities/find", "post"),
            ("/identities/search", "post"),
            ("/identities/results/{id}", "get"),
        ];

        private static IEnumerable<TestCaseData> BodiesByOperation()
        {
            (string Status, string Name, JsonNode Body)[] bodies =
            [
                (
                    "401",
                    "authentication failure",
                    FailureResponse.ForAuthenticationFailure(TraceId, ["Authorization header is missing."])
                ),
                ("403", "forbidden", FailureResponse.ForForbidden(TraceId, [])),
                (
                    "404",
                    "tenant not found",
                    FailureResponse.ForNotFound("The specified tenant could not be found.", TraceId)
                ),
                (
                    "500",
                    "security configuration",
                    FailureResponse.ForSecurityConfiguration(
                        TraceId,
                        [
                            "The identity service claim's authorization strategies for claim set 'SIS-Vendor' and action 'Create' must be exactly ['NoFurtherAuthorizationRequired'].",
                        ]
                    )
                ),
                ("503", "service unavailable", FailureResponse.ForServiceUnavailable(TraceId)),
            ];

            foreach ((string path, string method) in Operations)
            {
                foreach ((string status, string name, JsonNode body) in bodies)
                {
                    string operation = new([.. $"{method}{path}".Select(c => char.IsLetter(c) ? c : '_')]);
                    yield return new TestCaseData(path, method, status, body).SetName(
                        $"It_accepts_the_actual_{status}_{name.Replace(' ', '_')}_body_on_{operation}"
                    );
                }
            }
        }

        /// <summary>
        /// Builds the evaluator schema for an operation's <c>application/problem+json</c> response,
        /// following the response's component reference.
        /// </summary>
        private static JsonSchema BuildProblemSchemaFor(string path, string method, string status)
        {
            JsonNode response = Resolve(Paths[path]![method]!["responses"]![status]!);
            JsonNode rootSchema = response["content"]!["application/problem+json"]!["schema"]!;
            return OpenApiSchemaNormalizer.BuildJsonSchema(Schemas, rootSchema);
        }

        [TestCaseSource(nameof(BodiesByOperation))]
        public void It_accepts_the_actual_body(string path, string method, string status, JsonNode body)
        {
            EvaluationResults result = BuildProblemSchemaFor(path, method, status).Evaluate(body);

            result.IsValid.Should().BeTrue();
        }
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

    /// <summary>
    /// Requests use <c>null</c> or omission for an unknown standard attribute, <c>BirthLocation</c>
    /// included, so both request schemas must admit <c>BirthLocation: null</c> under OpenAPI 3.0.3's
    /// own <c>nullable</c> rules while still requiring an object otherwise.
    /// </summary>
    [TestFixture("IdentityCreateRequest")]
    [TestFixture("IdentitySearchRequest")]
    [Parallelizable]
    public class Given_A_Request_Schema_BirthLocation(string schemaName)
    {
        private JsonSchema _schema = null!;

        [SetUp]
        public void Setup()
        {
            _schema = OpenApiSchemaNormalizer.BuildJsonSchema(
                Schemas,
                new JsonObject { ["$ref"] = $"#/components/schemas/{schemaName}" }
            );
        }

        [Test]
        public void It_accepts_null()
        {
            _schema.Evaluate(JsonNode.Parse("""{"BirthLocation":null}""")).IsValid.Should().BeTrue();
        }

        [Test]
        public void It_accepts_an_object_with_null_children()
        {
            _schema
                .Evaluate(
                    JsonNode.Parse(
                        """{"BirthLocation":{"City":null,"StateAbbreviation":null,"InternationalProvince":null,"Country":null}}"""
                    )
                )
                .IsValid.Should()
                .BeTrue();
        }

        [Test]
        public void It_rejects_a_string()
        {
            _schema.Evaluate(JsonNode.Parse("""{"BirthLocation":"Austin"}""")).IsValid.Should().BeFalse();
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
    /// Negative control: a <c>nullable</c> string schema rejects a JSON <c>null</c> before
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

        /// <summary>
        /// OpenAPI 3.0.3 ignores <c>nullable</c> without a sibling <c>type</c>, so wrapping a reference
        /// as <c>{"allOf": [{"$ref": ...}], "nullable": true}</c> does not admit <c>null</c> for a strict
        /// validator. The normalizer must reject it too, or it would hide that pattern from every
        /// conformance test above.
        /// </summary>
        [TestFixture]
        [Parallelizable]
        public class Given_A_Nullable_Keyword_Beside_An_AllOf_Reference
        {
            private EvaluationResults _nullResult = null!;
            private EvaluationResults _objectResult = null!;

            [SetUp]
            public void Setup()
            {
                JsonObject componentSchemas = new()
                {
                    ["Location"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject { ["City"] = new JsonObject { ["type"] = "string" } },
                    },
                };
                JsonObject nullableAllOfSchema = new()
                {
                    ["allOf"] = new JsonArray(new JsonObject { ["$ref"] = "#/components/schemas/Location" }),
                    ["nullable"] = true,
                };

                JsonSchema schema = OpenApiSchemaNormalizer.BuildJsonSchema(
                    componentSchemas,
                    nullableAllOfSchema
                );

                _nullResult = schema.Evaluate(null);
                _objectResult = schema.Evaluate(JsonNode.Parse("""{"City":"Austin"}"""));
            }

            [Test]
            public void It_rejects_null()
            {
                _nullResult.IsValid.Should().BeFalse();
            }

            [Test]
            public void It_accepts_the_referenced_object()
            {
                _objectResult.IsValid.Should().BeTrue();
            }
        }
    }
}
