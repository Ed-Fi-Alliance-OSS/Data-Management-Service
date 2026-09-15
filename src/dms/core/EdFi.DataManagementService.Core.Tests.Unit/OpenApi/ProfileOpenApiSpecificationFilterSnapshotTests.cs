// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Profile;
using FluentAssertions;
using NUnit.Framework;
using static EdFi.DataManagementService.Core.Tests.Unit.OpenApi.OpenApiSnapshotContractAssertions;

namespace EdFi.DataManagementService.Core.Tests.Unit.OpenApi;

/// <summary>
/// The snapshot OpenAPI contract (DMS-1369) through profile filtering.
/// </summary>
/// <remarks>
/// <para>
/// Profile documents are the half of the snapshot surface DMS owns outright. There is no profile base
/// document for a package to publish: each served profile document is derived by filtering a clone of
/// the assembled resource document, so correct packages alone do not make a correct profile document.
/// </para>
/// <para>
/// These run against the filter directly rather than through assembly, so a regression is attributed to
/// filtering rather than to the packages. They fail in two opposite directions. The filter prunes
/// component parameters nothing references, so an operation that lost its <c>Use-Snapshot</c> reference
/// would take the component down with it and leave a document that still looks internally consistent.
/// It never prunes component responses, so a response can outlive every operation that referenced it and
/// be left pointing at a schema that pruning removed. Asserting that a response survived would therefore
/// prove nothing, and the response-side assertion is that what it points at still resolves.
/// </para>
/// </remarks>
public partial class ProfileOpenApiSpecificationFilterTests
{
    /// <summary>
    /// The change-query base specification plus the snapshot contract, shaped the way the published
    /// packages shape it: one reusable header parameter and two reusable responses, referenced from the
    /// operations rather than inlined, with the shared ProblemDetails envelope behind both responses.
    /// </summary>
    /// <remarks>
    /// Built on the change-query specification on purpose. <c>/deletes</c> and <c>/keyChanges</c> are the
    /// paths the snapshot design calls out as easy to leave behind, and they exist only there.
    /// </remarks>
    private static JsonNode GetBaseSpecWithSnapshotContract()
    {
        JsonNode specification = GetBaseSpecWithChangeQueryPaths();
        JsonObject components = specification["components"]!.AsObject();

        components["parameters"] = new JsonObject
        {
            [UseSnapshotParameterName] = new JsonObject
            {
                ["name"] = UseSnapshotParameterName,
                ["in"] = "header",
                ["description"] = "Indicates whether the request should be served from the Snapshot.",
                ["schema"] = new JsonObject { ["type"] = "boolean", ["default"] = false },
            },
        };

        components["responses"] = new JsonObject
        {
            [SnapshotNotFoundResponseName] = SnapshotResponse("The requested resource was not found."),
            [SnapshotMethodNotAllowedResponseName] = SnapshotMethodNotAllowedResponse(),
        };

        components["schemas"]!.AsObject()[ProblemDetailsSchemaName] = SchemaWithProperties(
            "detail",
            "type",
            "title",
            "status",
            "correlationId"
        );

        foreach (string readPath in _snapshotEligibleReadPaths)
        {
            JsonObject operation = specification["paths"]![readPath]!["get"]!.AsObject();

            AppendParameterReference(operation, UseSnapshotParameterName);
            ResponsesOf(operation)["404"] = new JsonObject { ["$ref"] = SnapshotNotFoundResponseReference };
        }

        foreach ((string mutationPath, string method) in _snapshotRejectedMutations)
        {
            ResponsesOf(specification["paths"]![mutationPath]![method]!.AsObject())["405"] = new JsonObject
            {
                ["$ref"] = SnapshotMethodNotAllowedResponseReference,
            };
        }

        return specification;
    }

    /// <summary>
    /// Every operation the snapshot design treats as eligible in this fixture's base specification:
    /// GET-many, GET-by-id, and the two tracked-change feeds.
    /// </summary>
    private static readonly string[] _snapshotEligibleReadPaths =
    [
        "/ed-fi/students",
        "/ed-fi/students/{id}",
        "/ed-fi/students/deletes",
        "/ed-fi/students/keyChanges",
        "/ed-fi/schools",
    ];

    private static readonly (string PathKey, string Method)[] _snapshotRejectedMutations =
    [
        ("/ed-fi/students", "post"),
        ("/ed-fi/students/{id}", "put"),
        ("/ed-fi/students/{id}", "delete"),
        ("/ed-fi/schools", "post"),
    ];

    private static JsonObject SnapshotResponse(string description) =>
        new()
        {
            ["description"] = description,
            ["content"] = new JsonObject
            {
                [ProblemJsonContentType] = new JsonObject
                {
                    ["schema"] = new JsonObject
                    {
                        ["$ref"] = $"#/components/schemas/{ProblemDetailsSchemaName}",
                    },
                },
            },
        };

    private static JsonObject SnapshotMethodNotAllowedResponse()
    {
        JsonObject response = SnapshotResponse("An attempt was made to modify data in a Snapshot.");

        response["headers"] = new JsonObject
        {
            ["Allow"] = new JsonObject
            {
                ["description"] = "The methods the endpoint accepts for a Snapshot request.",
                ["example"] = "GET",
                ["schema"] = new JsonObject { ["type"] = "string" },
            },
        };

        return response;
    }

    private static void AppendParameterReference(JsonObject operation, string componentName)
    {
        if (operation["parameters"] is not JsonArray parameters)
        {
            parameters = [];
            operation["parameters"] = parameters;
        }

        parameters.Add(ParameterReference(componentName));
    }

    private static JsonObject ResponsesOf(JsonObject operation)
    {
        if (operation["responses"] is not JsonObject responses)
        {
            responses = [];
            operation["responses"] = responses;
        }

        return responses;
    }

    private static IReadOnlyList<string> ParameterReferencesOf(
        JsonNode specification,
        string pathKey,
        string method
    )
    {
        JsonNode? operation = specification["paths"]?[pathKey]?[method];

        operation.Should().NotBeNull("the profile document must retain {0} {1}", method, pathKey);

        return operation!["parameters"] is JsonArray parameters
            ?
            [
                .. parameters
                    .OfType<JsonObject>()
                    .Select(parameter => parameter["$ref"]?.GetValue<string>())
                    .Where(reference => reference is not null)
                    .Select(reference => reference!),
            ]
            : [];
    }

    private static string? ResponseReferenceOf(
        JsonNode specification,
        string pathKey,
        string method,
        string statusCode
    )
    {
        JsonNode? operation = specification["paths"]?[pathKey]?[method];

        operation.Should().NotBeNull("the profile document must retain {0} {1}", method, pathKey);

        return operation!["responses"]?[statusCode]?["$ref"]?.GetValue<string>();
    }

    /// <summary>
    /// Requires every entry left in <c>components.responses</c> to still resolve what it points at.
    /// </summary>
    /// <remarks>
    /// Shaped this way deliberately. The filter has no response-pruning step, so asserting that a
    /// response entry survived would pass unconditionally and prove nothing. The failure this guards
    /// against is the opposite one: an entry outliving every operation that referenced it and being left
    /// holding a reference to a schema that schema-pruning removed.
    /// </remarks>
    private static void AssertRetainedResponsesResolve(JsonNode specification)
    {
        if (specification["components"]?["responses"] is not JsonObject responses)
        {
            return;
        }

        responses
            .Should()
            .NotBeEmpty("the filter never prunes responses, so every base-document response is retained");

        foreach ((string responseName, JsonNode? response) in responses)
        {
            if (response is null)
            {
                continue;
            }

            // Only the references inside this entry are reported, so a failure names the response that
            // dangles rather than every response in the document.
            FindUnresolvedReferences(WithComponentsOf(specification, response))
                .Where(reference => reference.Location.StartsWith(ProbeLocation, StringComparison.Ordinal))
                .Should()
                .BeEmpty(
                    "the retained components.responses.{0} entry must still resolve what it references",
                    responseName
                );
        }
    }

    private const string ProbeLocation = "$.probe";

    /// <summary>
    /// Pairs one component with the document's component section, so the reference walk resolves against
    /// the same components the entry was retained alongside.
    /// </summary>
    private static JsonNode WithComponentsOf(JsonNode specification, JsonNode component) =>
        new JsonObject
        {
            ["components"] = specification["components"]!.DeepClone(),
            ["probe"] = component.DeepClone(),
        };

    /// <summary>
    /// The snapshot contract through profile filtering for a readable profile.
    /// </summary>
    [TestFixture]
    public class Given_Readable_Profile_With_Snapshot_Contract : ProfileOpenApiSpecificationFilterTests
    {
        private static readonly string[] _profiledStudentReadPaths =
        [
            "/ed-fi/students",
            "/ed-fi/students/{id}",
            "/ed-fi/students/deletes",
            "/ed-fi/students/keyChanges",
        ];

        private JsonNode _result = null!;

        [SetUp]
        public void Setup()
        {
            _result = CreateFilter()
                .CreateProfileSpecification(
                    GetBaseSpecWithSnapshotContract(),
                    Given_Profile_With_Change_Query_Paths.CreateReadableStudentProfile()
                );
        }

        [Test]
        public void It_preserves_the_snapshot_parameter_on_every_surviving_read()
        {
            // Enumerated by path rather than swept, so a profile document that kept /deletes and
            // /keyChanges without the parameter fails here instead of passing on the live reads alone.
            foreach (string pathKey in _profiledStudentReadPaths)
            {
                ParameterReferencesOf(_result, pathKey, "get")
                    .Should()
                    .Contain(UseSnapshotParameterReference, "{0} survives the profile as a read", pathKey);
            }
        }

        [Test]
        public void It_preserves_the_snapshot_not_found_response_on_every_surviving_read()
        {
            foreach (string pathKey in _profiledStudentReadPaths)
            {
                ResponseReferenceOf(_result, pathKey, "get", "404")
                    .Should()
                    .Be(SnapshotNotFoundResponseReference, "{0} survives the profile as a read", pathKey);
            }
        }

        [Test]
        public void It_retains_the_parameter_component_the_surviving_reads_resolve_to()
        {
            Component(_result, "parameters", UseSnapshotParameterName)
                .Should()
                .NotBeNull(
                    "the filter prunes component parameters nothing references, so a retained reference "
                        + "must keep its component"
                );
        }

        [Test]
        public void It_leaves_every_retained_response_resolvable()
        {
            AssertRetainedResponsesResolve(_result);
        }

        [Test]
        public void It_produces_a_self_resolving_profile_document()
        {
            FindUnresolvedReferences(_result).Should().BeEmpty();
        }
    }

    /// <summary>
    /// The snapshot contract through profile filtering for a writable profile.
    /// </summary>
    [TestFixture]
    public class Given_Writable_Profile_With_Snapshot_Contract : ProfileOpenApiSpecificationFilterTests
    {
        private static readonly (string PathKey, string Method)[] _profiledStudentMutations =
        [
            ("/ed-fi/students", "post"),
            ("/ed-fi/students/{id}", "put"),
            ("/ed-fi/students/{id}", "delete"),
        ];

        private JsonNode _result = null!;

        [SetUp]
        public void Setup()
        {
            _result = CreateFilter()
                .CreateProfileSpecification(
                    GetBaseSpecWithSnapshotContract(),
                    new ProfileDefinition(
                        "WritableStudentProfile",
                        [
                            new ResourceProfile(
                                "Student",
                                null,
                                null,
                                new ContentTypeDefinition(MemberSelection.IncludeAll, [], [], [], [])
                            ),
                        ]
                    )
                );
        }

        [Test]
        public void It_preserves_the_snapshot_405_on_every_surviving_mutation()
        {
            foreach ((string pathKey, string method) in _profiledStudentMutations)
            {
                ResponseReferenceOf(_result, pathKey, method, "405")
                    .Should()
                    .Be(
                        SnapshotMethodNotAllowedResponseReference,
                        "{0} {1} survives the profile as a mutation",
                        method,
                        pathKey
                    );
            }
        }

        [Test]
        public void It_retains_the_allow_header_and_problem_json_on_the_retained_405()
        {
            JsonNode? methodNotAllowed = Component(
                _result,
                "responses",
                SnapshotMethodNotAllowedResponseName
            );

            methodNotAllowed.Should().NotBeNull();
            methodNotAllowed!["headers"]?["Allow"]?["example"]?.GetValue<string>().Should().Be("GET");
            methodNotAllowed["content"]
                ?[ProblemJsonContentType]?["schema"]?["$ref"]?.GetValue<string>()
                .Should()
                .Be($"#/components/schemas/{ProblemDetailsSchemaName}");
        }

        [Test]
        public void It_prunes_the_parameter_component_no_surviving_operation_references()
        {
            // Correct behavior, pinned so it is not mistaken for a regression later. A write-only profile
            // serves no read, so nothing references the header and the filter is right to drop it. The
            // document stays correct precisely because no surviving operation names it.
            Component(_result, "parameters", UseSnapshotParameterName).Should().BeNull();
            ParameterReferencesOf(_result, "/ed-fi/students", "post")
                .Should()
                .NotContain(UseSnapshotParameterReference);
        }

        [Test]
        public void It_leaves_every_retained_response_resolvable()
        {
            // The Snapshot Not Found entry outlives every operation that referenced it here, because
            // responses are never pruned. What matters is that the schema behind it survived too.
            AssertRetainedResponsesResolve(_result);
        }

        [Test]
        public void It_produces_a_self_resolving_profile_document()
        {
            FindUnresolvedReferences(_result).Should().BeEmpty();
        }
    }

    /// <summary>
    /// The snapshot contract through profile filtering for a profile that both reads and writes, where
    /// both halves have to survive the same pass.
    /// </summary>
    [TestFixture]
    public class Given_ReadWrite_Profile_With_Snapshot_Contract : ProfileOpenApiSpecificationFilterTests
    {
        private JsonNode _result = null!;

        [SetUp]
        public void Setup()
        {
            _result = CreateFilter()
                .CreateProfileSpecification(
                    GetBaseSpecWithSnapshotContract(),
                    new ProfileDefinition(
                        "ReadWriteStudentProfile",
                        [
                            new ResourceProfile(
                                "Student",
                                null,
                                new ContentTypeDefinition(MemberSelection.IncludeAll, [], [], [], []),
                                new ContentTypeDefinition(MemberSelection.IncludeAll, [], [], [], [])
                            ),
                        ]
                    )
                );
        }

        [Test]
        public void It_preserves_the_read_half_of_the_contract()
        {
            foreach (
                string pathKey in new[]
                {
                    "/ed-fi/students",
                    "/ed-fi/students/{id}",
                    "/ed-fi/students/deletes",
                    "/ed-fi/students/keyChanges",
                }
            )
            {
                ParameterReferencesOf(_result, pathKey, "get")
                    .Should()
                    .Contain(UseSnapshotParameterReference, "{0} survives the profile as a read", pathKey);
                ResponseReferenceOf(_result, pathKey, "get", "404")
                    .Should()
                    .Be(SnapshotNotFoundResponseReference, "{0} survives the profile as a read", pathKey);
            }
        }

        [Test]
        public void It_preserves_the_write_half_of_the_contract()
        {
            foreach (
                (string pathKey, string method) in new[]
                {
                    ("/ed-fi/students", "post"),
                    ("/ed-fi/students/{id}", "put"),
                    ("/ed-fi/students/{id}", "delete"),
                }
            )
            {
                ResponseReferenceOf(_result, pathKey, method, "405")
                    .Should()
                    .Be(
                        SnapshotMethodNotAllowedResponseReference,
                        "{0} {1} survives the profile as a mutation",
                        method,
                        pathKey
                    );
            }
        }

        [Test]
        public void It_retains_the_parameter_component_and_both_response_components()
        {
            Component(_result, "parameters", UseSnapshotParameterName).Should().NotBeNull();
            Component(_result, "responses", SnapshotNotFoundResponseName).Should().NotBeNull();
            Component(_result, "responses", SnapshotMethodNotAllowedResponseName).Should().NotBeNull();
        }

        [Test]
        public void It_leaves_every_retained_response_resolvable()
        {
            AssertRetainedResponsesResolve(_result);
        }

        [Test]
        public void It_produces_a_self_resolving_profile_document()
        {
            FindUnresolvedReferences(_result).Should().BeEmpty();
        }
    }
}
