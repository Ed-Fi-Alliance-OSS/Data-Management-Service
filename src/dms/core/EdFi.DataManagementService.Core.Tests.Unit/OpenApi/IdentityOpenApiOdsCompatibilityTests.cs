// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using FluentAssertions;
using NUnit.Framework;
using static EdFi.DataManagementService.Core.Tests.Unit.OpenApi.ChangeQueriesOpenApiDocumentTestHelper;

namespace EdFi.DataManagementService.Core.Tests.Unit.OpenApi;

/// <summary>
/// Compares the pinned ODS reference fixture (<c>Fixtures/ods-7.3.2-identity-openapi.json</c>, see
/// <c>Fixtures/PROVENANCE.md</c>) with the served identity OpenAPI document, node by node in both
/// directions: every key either document adds or drops and every value that differs, anywhere in
/// the two documents - paths, operations, parameters, request bodies, responses, headers, media
/// types, component schemas and the properties, types, formats, enums and array items nested inside
/// them. Shared responses and headers are inlined first, so a <c>$ref</c> to one compares as its
/// content; a schema <c>$ref</c> compares as its target name. Every difference must be explained by
/// an entry in <see cref="Ledger" />, or the test fails naming it.
/// </summary>
public class IdentityOpenApiOdsCompatibilityTests
{
    private const string OdsFixturePath = "OpenApi/Fixtures/ods-7.3.2-identity-openapi.json";

    private const string SchemaRefPrefix = "#/components/schemas/";

    /// <summary>
    /// Component schema names the served document adds beyond the six ODS names. D-16 covers the
    /// complete/incomplete result-state split; the rest are DMS's problem-detail schemas, which have no
    /// ODS analog because the pinned fixture's error responses carry no schema at all.
    /// </summary>
    private static readonly HashSet<string> AllowedExtraComponentNames = new(StringComparer.Ordinal)
    {
        "IdentitySearchResponseComplete",
        "IdentitySearchResponseIncomplete",
        "ProblemDetails",
        "IdentityOperationNotSupportedProblemDetails",
        "IdentityNotFoundProblemDetails",
        "NotFoundProblemDetails",
        "IdentityProviderContractViolationProblemDetails",
        "IdentityUpstreamFailureProblemDetails",
        "IdentityJobFailedProblemDetails",
        "IdentityProviderConfigurationProblemDetails",
        "SecurityConfigurationProblemDetails",
    };

    private static readonly HashSet<string> AddedProblemStatusCodes = new(StringComparer.Ordinal)
    {
        "400",
        "401",
        "403",
        "404",
        "415",
        "429",
        "500",
        "503",
    };

    private static readonly HashSet<string> DeclaredResponseHeaders = new(StringComparer.Ordinal)
    {
        "Cache-Control",
        "Location",
    };

    private enum DifferenceKind
    {
        Added,
        Removed,
        Changed,
        ItemAdded,
        ItemRemoved,
    }

    /// <summary>
    /// One difference at <paramref name="Path" />, the segments leading to the node in both documents.
    /// <paramref name="Ods" /> and <paramref name="Dms" /> are the two values there, null on the side
    /// that lacks the node; for an item difference they are the array item itself.
    /// </summary>
    private sealed record Difference(DifferenceKind Kind, string[] Path, JsonNode? Ods, JsonNode? Dms)
    {
        /// <summary>The kind and the JSON Pointer to the node, for example <c>Added /paths/~1identities/post</c>.</summary>
        public override string ToString() =>
            $"{Kind} /{string.Join('/', Path.Select(segment => segment.Replace("~", "~0").Replace("/", "~1")))}";
    }

    /// <summary>
    /// The divergence ledger: every computed difference must match at least one entry, or the test
    /// fails naming it. D-numbers refer to the identity design's divergence ledger.
    /// </summary>
    private static readonly List<(string LedgerId, Func<Difference, bool> IsAllowed)> Ledger =
    [
        (
            "D-1: the ODS 501 (not implemented) response is absent because DMS always implements identity",
            difference =>
                difference is { Kind: DifferenceKind.Removed, Path: ["paths", _, _, "responses", "501"] }
        ),
        (
            "D-1/D-11/D-12: DMS declares its host problem responses where the ODS fixture declares none for that operation",
            difference =>
                difference is { Kind: DifferenceKind.Added, Path: ["paths", _, _, "responses", var status] }
                && AddedProblemStatusCodes.Contains(status)
        ),
        (
            "D-10 and no-store: DMS declares Location and Cache-Control response headers where the ODS fixture declares none",
            difference =>
                difference
                    is {
                        Kind: DifferenceKind.Added,
                        Path: ["paths", _, _, "responses", _, "headers"],
                        Dms: JsonObject headers,
                    }
                && headers.All(header => DeclaredResponseHeaders.Contains(header.Key))
        ),
        (
            "D-4/D-5: DMS problem responses carry typed application/problem+json bodies where the ODS fixture declares none or an untyped JSON body",
            difference =>
                difference
                    is {
                        Kind: DifferenceKind.Added,
                        Path: ["paths", _, _, "responses", _, "content", "application/problem+json"],
                    }
                || (
                    difference
                        is {
                            Kind: DifferenceKind.Added,
                            Path: ["paths", _, _, "responses", _, "content"],
                            Dms: JsonObject content,
                        }
                    && content.All(mediaType => mediaType.Key == "application/problem+json")
                )
                || difference
                    is {
                        Kind: DifferenceKind.Removed,
                        Path: [
                            "paths",
                            _,
                            _,
                            "responses",
                            "400",
                            "content",
                            "application/json"
                            or "text/json",
                        ],
                        Ods: JsonObject { Count: 0 },
                    }
        ),
        (
            "response media type narrowing: DMS success bodies drop the ODS fixture's parallel text/json media type",
            difference =>
                difference
                    is {
                        Kind: DifferenceKind.Removed,
                        Path: ["paths", _, _, "responses", "200", "content", "text/json"],
                    }
        ),
        (
            "D-16: find/search 200 references only the complete shape and results 200 either result-state shape",
            difference =>
                (
                    difference
                        is {
                            Kind: DifferenceKind.Changed,
                            Path: [
                                "paths",
                                "/identities/find"
                                or "/identities/search",
                                "post",
                                "responses",
                                "200",
                                "content",
                                "application/json",
                                "schema",
                                "$ref",
                            ],
                        }
                    && StringValue(difference.Ods) == SchemaRefPrefix + "IdentitySearchResponse"
                    && StringValue(difference.Dms) == SchemaRefPrefix + "IdentitySearchResponseComplete"
                )
                || (
                    difference
                        is {
                            Kind: DifferenceKind.Removed,
                            Path: [
                                "paths",
                                "/identities/results/{id}",
                                "get",
                                "responses",
                                "200",
                                "content",
                                "application/json",
                                "schema",
                                "$ref",
                            ],
                        }
                    && StringValue(difference.Ods) == SchemaRefPrefix + "IdentitySearchResponse"
                )
                || (
                    difference
                        is {
                            Kind: DifferenceKind.Added,
                            Path: [
                                "paths",
                                "/identities/results/{id}",
                                "get",
                                "responses",
                                "200",
                                "content",
                                "application/json",
                                "schema",
                                "oneOf",
                            ],
                            Dms: JsonArray members,
                        }
                    && members
                        .Select(member => StringValue(member?["$ref"]))
                        .Order(StringComparer.Ordinal)
                        .SequenceEqual([
                            SchemaRefPrefix + "IdentitySearchResponseComplete",
                            SchemaRefPrefix + "IdentitySearchResponseIncomplete",
                        ])
                )
        ),
        (
            "D-16/problem schemas: DMS adds result-state and problem-detail component schemas the ODS fixture does not declare",
            difference =>
                difference is { Kind: DifferenceKind.Added, Path: ["components", "schemas", var name] }
                && AllowedExtraComponentNames.Contains(name)
        ),
        (
            "D-14: DMS declares required where the Swagger 2.0-converted ODS fixture declares none",
            difference =>
                difference is { Kind: DifferenceKind.Added, Path: ["components", "schemas", _, "required"] }
        ),
        (
            "D-14: IdentityResponse.UniqueId is a required non-empty string",
            difference =>
                difference
                    is {
                        Kind: DifferenceKind.Added,
                        Path: [
                            "components",
                            "schemas",
                            "IdentityResponse",
                            "properties",
                            "UniqueId",
                            "minLength",
                        ],
                        Dms: JsonValue minLength,
                    }
                && minLength.TryGetValue(out int length)
                && length == 1
        ),
        (
            "D-15: DMS marks nullable where the Swagger 2.0-converted ODS fixture has no nullable keyword",
            difference =>
                difference
                    is {
                        Kind: DifferenceKind.Added,
                        Path: ["components", "schemas", _, "properties", _, "nullable"],
                    }
                && IsTrue(difference.Dms)
        ),
        (
            "D-15: each request BirthLocation is an inline nullable object with Location's properties, because OpenAPI 3.0.3 ignores nullable beside a $ref",
            difference =>
                difference.Path
                    is [
                        "components",
                        "schemas",
                        "IdentityCreateRequest"
                        or "IdentitySearchRequest",
                        "properties",
                        "BirthLocation",
                        var keyword,
                    ]
                && (
                    (
                        difference.Kind == DifferenceKind.Removed
                        && keyword == "$ref"
                        && StringValue(difference.Ods) == SchemaRefPrefix + "Location"
                    )
                    || (
                        difference.Kind == DifferenceKind.Added
                        && keyword is "type" or "nullable" or "properties" or "additionalProperties"
                    )
                )
        ),
        (
            "DMS declares additionalProperties: true; the Swagger 2.0-converted ODS fixture leaves it unset",
            difference =>
                difference
                    is {
                        Kind: DifferenceKind.Added,
                        Path: ["components", "schemas", _, "additionalProperties"],
                    }
                && IsTrue(difference.Dms)
        ),
        (
            "x-edfi-identity-contract-version: the serve-time stamp is absent from the static ODS fixture",
            difference =>
                difference is { Kind: DifferenceKind.Added, Path: ["x-edfi-identity-contract-version"] }
        ),
        (
            "documentation: DMS writes its own summary and description prose and examples, none of which reaches the wire",
            difference =>
                (difference.Path is [.., var parent, "summary" or "description"] && parent != "properties")
                || difference
                    is { Kind: DifferenceKind.Added, Path: [.., "content", _, "example" or "examples"] }
        ),
        (
            "code generation metadata: DMS names operations in its own operationId convention, declares no tags, and drops the Swagger 2.0 x-bodyName hint",
            difference =>
                difference is { Kind: DifferenceKind.Changed, Path: ["paths", _, _, "operationId"] }
                || difference is { Kind: DifferenceKind.Removed, Path: ["tags"] or ["paths", _, _, "tags"] }
                || difference
                    is { Kind: DifferenceKind.Removed, Path: ["paths", _, _, "requestBody", "x-bodyName"] }
        ),
        (
            "deployment URLs: the served server URL and OAuth token URL come from the running host",
            difference =>
                difference is { Kind: DifferenceKind.Changed, Path: ["servers", _, "url"] }
                || difference
                    is {
                        Kind: DifferenceKind.Changed,
                        Path: ["components", "securitySchemes", _, "flows", _, "tokenUrl"],
                    }
        ),
    ];

    private static string? StringValue(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    private static bool IsTrue(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out bool flag) && flag;

    private static JsonObject LoadOdsDocument() =>
        JsonNode.Parse(File.ReadAllText(OdsFixturePath))!.AsObject();

    private static JsonObject BuildServedDocument()
    {
        ApiSchemaDocumentNodes apiSchemaDocumentNodes = new ApiSchemaBuilder()
            .WithStartProject("ed-fi", "5.0.0")
            .WithOpenApiBaseDocuments(
                resourcesDoc: MinimalOpenApiDocument("Ed-Fi Resources API"),
                descriptorsDoc: MinimalOpenApiDocument("Ed-Fi Descriptors API")
            )
            .WithEndProject()
            .AsApiSchemaNodes();

        ApiService apiService = ApiServiceOpenApiTests.CreateApiService(apiSchemaDocumentNodes);

        return apiService
            .GetIdentityOpenApiSpecification([new JsonObject { ["url"] = "http://example.org/identity/v2" }])
            .AsObject();
    }

    /// <summary>The differences between the two documents that no ledger entry explains.</summary>
    private static List<Difference> ComputeUnexplainedDifferences(JsonObject ods, JsonObject dms)
    {
        List<Difference> differences = [];
        CompareNodes(InlineSharedComponents(ods), InlineSharedComponents(dms), [], differences);

        return differences.Where(difference => !Ledger.Exists(entry => entry.IsAllowed(difference))).ToList();
    }

    /// <summary>
    /// Returns a copy with every <c>$ref</c> to a shared response, header, parameter, or request body
    /// replaced by the content it names, and those component maps dropped, so a path that references
    /// a shared response compares with the other document's inline one. Schema references are kept.
    /// </summary>
    private static JsonNode InlineSharedComponents(JsonObject document)
    {
        JsonNode Inline(JsonNode node) =>
            node switch
            {
                JsonObject obj
                    when StringValue(obj["$ref"]) is { } reference
                        && reference.StartsWith("#/components/", StringComparison.Ordinal)
                        && !reference.StartsWith(SchemaRefPrefix, StringComparison.Ordinal) => Inline(
                    Resolve(reference, document)
                ),
                JsonObject obj => new JsonObject(
                    obj.Select(pair =>
                        KeyValuePair.Create(pair.Key, pair.Value is null ? null : Inline(pair.Value))
                    )
                ),
                JsonArray array => new JsonArray([
                    .. array.Select(item => item is null ? null : Inline(item)),
                ]),
                _ => node.DeepClone(),
            };

        JsonObject inlined = Inline(document).AsObject();
        if (inlined["components"] is JsonObject components)
        {
            foreach (string sharedMap in new[] { "responses", "headers", "parameters", "requestBodies" })
            {
                components.Remove(sharedMap);
            }
        }
        return inlined;
    }

    private static JsonNode Resolve(string reference, JsonObject document)
    {
        JsonNode current = document;
        foreach (string segment in reference[2..].Split('/'))
        {
            current = current[segment.Replace("~1", "/").Replace("~0", "~")]!;
        }
        return current;
    }

    /// <summary>
    /// Records every difference between <paramref name="ods" /> and <paramref name="dms" />. Arrays of
    /// scalars (<c>required</c>, <c>enum</c>) compare as sets, arrays of named objects
    /// (<c>parameters</c>) by name, and any other array position by position.
    /// </summary>
    private static void CompareNodes(
        JsonNode? ods,
        JsonNode? dms,
        string[] path,
        List<Difference> differences
    )
    {
        switch (ods, dms)
        {
            case (JsonObject odsObject, JsonObject dmsObject):
                CompareMaps(ToMap(odsObject), ToMap(dmsObject), path, differences);
                break;

            case (JsonArray odsArray, JsonArray dmsArray)
                when odsArray.Concat(dmsArray).All(item => item is JsonValue):
                differences.AddRange(
                    odsArray
                        .Where(item => !dmsArray.Any(other => JsonNode.DeepEquals(item, other)))
                        .Select(item => new Difference(DifferenceKind.ItemRemoved, path, item, null))
                );
                differences.AddRange(
                    dmsArray
                        .Where(item => !odsArray.Any(other => JsonNode.DeepEquals(item, other)))
                        .Select(item => new Difference(DifferenceKind.ItemAdded, path, null, item))
                );
                break;

            case (JsonArray odsArray, JsonArray dmsArray)
                when odsArray.Concat(dmsArray).All(item => StringValue(item?["name"]) is not null):
                CompareMaps(
                    odsArray.ToDictionary(item => StringValue(item!["name"])!, item => item),
                    dmsArray.ToDictionary(item => StringValue(item!["name"])!, item => item),
                    path,
                    differences
                );
                break;

            case (JsonArray odsArray, JsonArray dmsArray) when odsArray.Count == dmsArray.Count:
                for (int index = 0; index < odsArray.Count; index++)
                {
                    CompareNodes(odsArray[index], dmsArray[index], [.. path, $"{index}"], differences);
                }
                break;

            default:
                if (!JsonNode.DeepEquals(ods, dms))
                {
                    differences.Add(new Difference(DifferenceKind.Changed, path, ods, dms));
                }
                break;
        }
    }

    private static Dictionary<string, JsonNode?> ToMap(JsonObject obj) =>
        obj.ToDictionary(pair => pair.Key, pair => pair.Value);

    private static void CompareMaps(
        Dictionary<string, JsonNode?> ods,
        Dictionary<string, JsonNode?> dms,
        string[] path,
        List<Difference> differences
    )
    {
        foreach ((string key, JsonNode? odsValue) in ods)
        {
            if (dms.TryGetValue(key, out JsonNode? dmsValue))
            {
                CompareNodes(odsValue, dmsValue, [.. path, key], differences);
            }
            else
            {
                differences.Add(new Difference(DifferenceKind.Removed, [.. path, key], odsValue, null));
            }
        }

        differences.AddRange(
            dms.Where(pair => !ods.ContainsKey(pair.Key))
                .Select(pair => new Difference(DifferenceKind.Added, [.. path, pair.Key], null, pair.Value))
        );
    }

    [TestFixture]
    public class Given_The_Served_Identity_Document_Compared_To_The_Pinned_Ods_Fixture
    {
        private List<Difference> _unexplained = null!;

        [SetUp]
        public void Setup()
        {
            _unexplained = ComputeUnexplainedDifferences(LoadOdsDocument(), BuildServedDocument());
        }

        [Test]
        public void It_explains_every_difference_with_the_encoded_ledger()
        {
            _unexplained
                .Select(difference => difference.ToString())
                .Should()
                .BeEmpty("every difference from the pinned ODS fixture must be named in the encoded ledger");
        }
    }

    /// <summary>
    /// Negative controls: each case adds one undeclared difference to the served document, of a kind
    /// the ledger names nowhere, and the comparison must report exactly that difference.
    /// </summary>
    [TestFixture]
    public class Given_An_Undeclared_Difference_In_The_Served_Document
    {
        private static IEnumerable<TestCaseData> Mutations()
        {
            yield return new TestCaseData(
                (Action<JsonObject>)(
                    dms =>
                        Schema(dms, "IdentityResponse")["properties"]!.AsObject()["Nickname"] = new JsonObject
                        {
                            ["type"] = "string",
                        }
                ),
                new[] { "Added /components/schemas/IdentityResponse/properties/Nickname" }
            ).SetName("It_reports_a_property_only_DMS_declares");

            yield return new TestCaseData(
                (Action<JsonObject>)(
                    dms => Schema(dms, "IdentityResponse")["properties"]!.AsObject().Remove("Score")
                ),
                new[] { "Removed /components/schemas/IdentityResponse/properties/Score" }
            ).SetName("It_reports_a_property_only_ODS_declares");

            yield return new TestCaseData(
                (Action<JsonObject>)(
                    dms => Schema(dms, "Location")["properties"]!["City"]!["type"] = "integer"
                ),
                new[] { "Changed /components/schemas/Location/properties/City/type" }
            ).SetName("It_reports_a_nested_type_change");

            yield return new TestCaseData(
                (Action<JsonObject>)(
                    dms => Schema(dms, "IdentityResponse")["properties"]!["BirthDate"]!["format"] = "date"
                ),
                new[] { "Changed /components/schemas/IdentityResponse/properties/BirthDate/format" }
            ).SetName("It_reports_a_format_change");

            yield return new TestCaseData(
                (Action<JsonObject>)(
                    dms =>
                        Schema(dms, "IdentitySearchResponses")["properties"]!["Responses"]!["items"] =
                            new JsonObject { ["type"] = "string" }
                ),
                new[]
                {
                    "Removed /components/schemas/IdentitySearchResponses/properties/Responses/items/$ref",
                    "Added /components/schemas/IdentitySearchResponses/properties/Responses/items/type",
                }
            ).SetName("It_reports_an_array_item_change");

            yield return new TestCaseData(
                (Action<JsonObject>)(
                    dms =>
                        Schema(dms, "IdentitySearchResponse")["properties"]!["Status"]!["enum"]!
                            .AsArray()
                            .Add("Pending")
                ),
                new[] { "ItemAdded /components/schemas/IdentitySearchResponse/properties/Status/enum" }
            ).SetName("It_reports_a_widened_enum");

            yield return new TestCaseData(
                (Action<JsonObject>)(
                    dms =>
                        dms["paths"]!["/identities"]!["post"]!["requestBody"]!["content"]!.AsObject()[
                            "application/xml"
                        ] = new JsonObject()
                ),
                new[] { "Added /paths/~1identities/post/requestBody/content/application~1xml" }
            ).SetName("It_reports_a_request_media_type");

            yield return new TestCaseData(
                (Action<JsonObject>)(
                    dms => dms["paths"]!["/identities/{id}"]!["get"]!["parameters"]![0]!["required"] = false
                ),
                new[] { "Changed /paths/~1identities~1{id}/get/parameters/id/required" }
            ).SetName("It_reports_a_parameter_change");
        }

        private static JsonObject Schema(JsonObject document, string name) =>
            document["components"]!["schemas"]![name]!.AsObject();

        [TestCaseSource(nameof(Mutations))]
        public void It_reports_the_difference(Action<JsonObject> mutate, string[] expectedDifferences)
        {
            JsonObject dms = BuildServedDocument();
            mutate(dms);

            ComputeUnexplainedDifferences(LoadOdsDocument(), dms)
                .Select(difference => difference.ToString())
                .Should()
                .BeEquivalentTo(expectedDifferences);
        }
    }
}
