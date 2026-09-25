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
/// Story D3/D5: walks the pinned ODS reference fixture (<c>Fixtures/ods-7.3.2-identity-openapi.json</c>,
/// see <c>Fixtures/PROVENANCE.md</c>) against the served identity OpenAPI document.
/// For each of the six ODS component names, every ODS property must exist in the DMS component with
/// an equal <c>type</c> and <c>format</c> (and, for a <c>$ref</c>, the same target name). Every other
/// difference between the two documents - scoped to paths, operations, response codes, headers, media
/// types, <c>required</c>, <c>nullable</c>, <c>additionalProperties</c>, extra components, and
/// <c>x-edfi-*</c> extensions, exactly the dimensions design.md's divergence ledger names - must be
/// explained by an entry in <see cref="AllowedDifferenceCategories" />. An unexplained difference fails
/// the test, which is what a negative control (recorded in the story's Contract round) proves by adding
/// an undeclared difference to a scratch copy of the served document and watching this test fail.
/// </summary>
public class IdentityOpenApiOdsCompatibilityTests
{
    private const string OdsFixturePath = "OpenApi/Fixtures/ods-7.3.2-identity-openapi.json";

    private static readonly string[] SharedComponentNames =
    [
        "IdentityCreateRequest",
        "IdentityResponse",
        "IdentitySearchRequest",
        "IdentitySearchResponse",
        "IdentitySearchResponses",
        "Location",
    ];

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
        "IdentityProviderContractViolationProblemDetails",
        "IdentityUpstreamFailureProblemDetails",
        "IdentityJobFailedProblemDetails",
        "IdentityProviderConfigurationProblemDetails",
    };

    /// <summary>
    /// The divergence ledger, encoded as predicates over the difference descriptors
    /// <see cref="ComputeRemainingDifferences" /> produces. Every computed difference must match at
    /// least one entry here or the test fails; the ledger id is folded into the failure message.
    /// </summary>
    private static readonly List<(
        string LedgerId,
        Func<string, bool> IsAllowed
    )> AllowedDifferenceCategories =
    [
        (
            "D-1: the ODS 501 (not implemented) response is absent because DMS always implements identity",
            diff =>
                diff.StartsWith("response-code-removed:", StringComparison.Ordinal)
                && diff.EndsWith(":501", StringComparison.Ordinal)
        ),
        (
            "D-11/D3: DMS adds 400/404/415/429/500 problem responses the ODS fixture does not declare for that operation",
            diff =>
                diff.StartsWith("response-code-added:", StringComparison.Ordinal)
                && (
                    diff.EndsWith(":400", StringComparison.Ordinal)
                    || diff.EndsWith(":404", StringComparison.Ordinal)
                    || diff.EndsWith(":415", StringComparison.Ordinal)
                    || diff.EndsWith(":429", StringComparison.Ordinal)
                    || diff.EndsWith(":500", StringComparison.Ordinal)
                )
        ),
        (
            "Location/Cache-Control headers: DMS declares response headers everywhere the ODS fixture is silent",
            diff =>
                diff.StartsWith("header-added:", StringComparison.Ordinal)
                && (
                    diff.EndsWith(":Cache-Control", StringComparison.Ordinal)
                    || diff.EndsWith(":Location", StringComparison.Ordinal)
                )
        ),
        (
            "problem schemas: DMS carries application/problem+json bodies where the ODS fixture declares none or an untyped application/json body",
            diff =>
                diff.StartsWith("media-type-added:", StringComparison.Ordinal)
                && diff.EndsWith(":application/problem+json", StringComparison.Ordinal)
        ),
        (
            "response media type narrowing: DMS success bodies drop the ODS fixture's parallel text/json media type",
            diff =>
                diff.StartsWith("media-type-removed:", StringComparison.Ordinal)
                && diff.EndsWith(":text/json", StringComparison.Ordinal)
        ),
        (
            "problem schemas: DMS replaces the ODS fixture's untyped application/json 400 body with a typed ProblemDetails body",
            diff =>
                diff.StartsWith("media-type-removed:", StringComparison.Ordinal)
                && diff.EndsWith(":application/json", StringComparison.Ordinal)
                && diff.Contains(":400:", StringComparison.Ordinal)
        ),
        (
            "D-16: DMS splits the ODS fixture's shared IdentitySearchResponse into Complete/Incomplete result-state variants",
            diff =>
                (
                    diff.StartsWith("response-schema-ref-removed:", StringComparison.Ordinal)
                    && diff.EndsWith(":IdentitySearchResponse", StringComparison.Ordinal)
                )
                || (
                    diff.StartsWith("response-schema-ref-added:", StringComparison.Ordinal)
                    && (
                        diff.EndsWith(":IdentitySearchResponseComplete", StringComparison.Ordinal)
                        || diff.EndsWith(":IdentitySearchResponseIncomplete", StringComparison.Ordinal)
                    )
                )
        ),
        (
            "D-16/problem schemas: DMS adds result-state and problem-detail component schemas the ODS fixture does not declare",
            diff =>
                diff.StartsWith("component-added:", StringComparison.Ordinal)
                && AllowedExtraComponentNames.Contains(diff["component-added:".Length..])
        ),
        (
            "D-14: DMS declares required where the Swagger 2.0-converted ODS fixture declares none",
            diff => diff.StartsWith("required-added:", StringComparison.Ordinal)
        ),
        (
            "D-15: DMS marks nullable where the Swagger 2.0-converted ODS fixture has no nullable keyword",
            diff => diff.StartsWith("nullable-added:", StringComparison.Ordinal)
        ),
        (
            "DMS declares additionalProperties: true; the Swagger 2.0-converted ODS fixture leaves it unset",
            diff => diff.StartsWith("additionalProperties-added:", StringComparison.Ordinal)
        ),
        (
            "x-edfi-identity-contract-version: the serve-time stamp is absent from the static ODS fixture",
            diff => diff == "root-extension-added:x-edfi-identity-contract-version"
        ),
    ];

    [TestFixture]
    public class Given_The_Served_Identity_Document_Compared_To_The_Pinned_Ods_Fixture
    {
        private JsonObject _odsDocument = null!;
        private JsonObject _dmsDocument = null!;

        [SetUp]
        public void Setup()
        {
            string odsJson = File.ReadAllText(OdsFixturePath);
            _odsDocument = JsonNode.Parse(odsJson)!.AsObject();
            _dmsDocument = BuildServedDocument().AsObject();
        }

        [TestCaseSource(typeof(IdentityOpenApiOdsCompatibilityTests), nameof(SharedComponentNames))]
        public void It_has_every_ods_property_present_with_equal_type_and_format(string componentName)
        {
            JsonObject odsProperties =
                _odsDocument["components"]!["schemas"]![componentName]!["properties"]?.AsObject() ?? [];
            JsonObject dmsProperties = _dmsDocument["components"]!["schemas"]![componentName]![
                "properties"
            ]!.AsObject();

            foreach ((string propertyName, JsonNode? odsPropertyNode) in odsProperties)
            {
                dmsProperties
                    .Should()
                    .ContainKey(propertyName, $"{componentName}.{propertyName} must be present in DMS");

                JsonObject odsProperty = odsPropertyNode!.AsObject();
                JsonObject dmsProperty = dmsProperties[propertyName]!.AsObject();

                if (odsProperty["type"] is not null)
                {
                    odsProperty["type"]!
                        .GetValue<string>()
                        .Should()
                        .Be(
                            dmsProperty["type"]?.GetValue<string>(),
                            $"{componentName}.{propertyName} type must match"
                        );
                }

                if (odsProperty["format"] is not null)
                {
                    odsProperty["format"]!
                        .GetValue<string>()
                        .Should()
                        .Be(
                            dmsProperty["format"]?.GetValue<string>(),
                            $"{componentName}.{propertyName} format must match"
                        );
                }

                string? odsRefTarget = ExtractRefTargetName(odsProperty);
                if (odsRefTarget is not null)
                {
                    ExtractRefTargetName(dmsProperty)
                        .Should()
                        .Be(
                            odsRefTarget,
                            $"{componentName}.{propertyName} must reference the same component"
                        );
                }
            }
        }

        [Test]
        public void It_explains_every_remaining_difference_with_the_encoded_ledger()
        {
            List<string> differences = ComputeRemainingDifferences(_odsDocument, _dmsDocument);

            List<string> unexplained = differences
                .Where(difference =>
                    !AllowedDifferenceCategories.Exists(category => category.IsAllowed(difference))
                )
                .ToList();

            unexplained
                .Should()
                .BeEmpty(
                    "every difference between the pinned ODS fixture and the served identity document must "
                        + "be named in the encoded ledger; unexplained: "
                        + string.Join(", ", unexplained)
                );
        }

        private static JsonNode BuildServedDocument()
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

            return apiService.GetIdentityOpenApiSpecification([
                new JsonObject { ["url"] = "http://example.org/identity/v2" },
            ]);
        }

        /// <summary>
        /// Computes every difference between the two documents across the story's named dimensions: paths,
        /// operations, response codes, headers, media types, <c>required</c>, <c>nullable</c>,
        /// <c>additionalProperties</c>, extra components, and root <c>x-*</c> extensions. Each entry is a
        /// terse, stable descriptor string the allow-list matches by prefix/suffix.
        /// </summary>
        private static List<string> ComputeRemainingDifferences(JsonObject ods, JsonObject dms)
        {
            List<string> differences = [];

            JsonObject odsPaths = ods["paths"]!.AsObject();
            JsonObject dmsPaths = dms["paths"]!.AsObject();
            HashSet<string> odsPathKeys = odsPaths.Select(pair => pair.Key).ToHashSet();
            HashSet<string> dmsPathKeys = dmsPaths.Select(pair => pair.Key).ToHashSet();

            differences.AddRange(odsPathKeys.Except(dmsPathKeys).Select(path => $"path-removed:{path}"));
            differences.AddRange(dmsPathKeys.Except(odsPathKeys).Select(path => $"path-added:{path}"));

            foreach (string path in odsPathKeys.Intersect(dmsPathKeys))
            {
                AddOperationDifferences(
                    differences,
                    path,
                    odsPaths[path]!.AsObject(),
                    dmsPaths[path]!.AsObject(),
                    ods,
                    dms
                );
            }

            AddComponentSchemaDifferences(differences, ods, dms);
            AddRootExtensionDifferences(differences, ods, dms);

            return differences;
        }

        private static void AddOperationDifferences(
            List<string> differences,
            string path,
            JsonObject odsOperations,
            JsonObject dmsOperations,
            JsonObject ods,
            JsonObject dms
        )
        {
            HashSet<string> odsMethods = odsOperations.Select(pair => pair.Key).ToHashSet();
            HashSet<string> dmsMethods = dmsOperations.Select(pair => pair.Key).ToHashSet();

            differences.AddRange(
                odsMethods.Except(dmsMethods).Select(method => $"operation-removed:{path}:{method}")
            );
            differences.AddRange(
                dmsMethods.Except(odsMethods).Select(method => $"operation-added:{path}:{method}")
            );

            foreach (string method in odsMethods.Intersect(dmsMethods))
            {
                AddResponseDifferences(
                    differences,
                    path,
                    method,
                    odsOperations[method]!["responses"]!.AsObject(),
                    dmsOperations[method]!["responses"]!.AsObject(),
                    ods,
                    dms
                );
            }
        }

        private static void AddResponseDifferences(
            List<string> differences,
            string path,
            string method,
            JsonObject odsResponses,
            JsonObject dmsResponses,
            JsonObject ods,
            JsonObject dms
        )
        {
            HashSet<string> odsStatuses = odsResponses.Select(pair => pair.Key).ToHashSet();
            HashSet<string> dmsStatuses = dmsResponses.Select(pair => pair.Key).ToHashSet();

            differences.AddRange(
                odsStatuses
                    .Except(dmsStatuses)
                    .Select(status => $"response-code-removed:{path}:{method}:{status}")
            );
            differences.AddRange(
                dmsStatuses
                    .Except(odsStatuses)
                    .Select(status => $"response-code-added:{path}:{method}:{status}")
            );

            foreach (string status in odsStatuses.Intersect(dmsStatuses))
            {
                JsonObject odsResponse = Resolve(odsResponses[status]!, ods).AsObject();
                JsonObject dmsResponse = Resolve(dmsResponses[status]!, dms).AsObject();

                HashSet<string> odsHeaders =
                    (odsResponse["headers"] as JsonObject)?.Select(pair => pair.Key).ToHashSet() ?? [];
                HashSet<string> dmsHeaders =
                    (dmsResponse["headers"] as JsonObject)?.Select(pair => pair.Key).ToHashSet() ?? [];

                differences.AddRange(
                    odsHeaders
                        .Except(dmsHeaders)
                        .Select(header => $"header-removed:{path}:{method}:{status}:{header}")
                );
                differences.AddRange(
                    dmsHeaders
                        .Except(odsHeaders)
                        .Select(header => $"header-added:{path}:{method}:{status}:{header}")
                );

                HashSet<string> odsMedia =
                    (odsResponse["content"] as JsonObject)?.Select(pair => pair.Key).ToHashSet() ?? [];
                HashSet<string> dmsMedia =
                    (dmsResponse["content"] as JsonObject)?.Select(pair => pair.Key).ToHashSet() ?? [];

                differences.AddRange(
                    odsMedia
                        .Except(dmsMedia)
                        .Select(media => $"media-type-removed:{path}:{method}:{status}:{media}")
                );
                differences.AddRange(
                    dmsMedia
                        .Except(odsMedia)
                        .Select(media => $"media-type-added:{path}:{method}:{status}:{media}")
                );

                if (status == "200")
                {
                    AddResponseSchemaRefDifferences(differences, path, method, odsResponse, dmsResponse);
                }
            }
        }

        private static void AddResponseSchemaRefDifferences(
            List<string> differences,
            string path,
            string method,
            JsonObject odsResponse,
            JsonObject dmsResponse
        )
        {
            JsonNode? odsSchema = odsResponse["content"]?["application/json"]?["schema"];
            JsonNode? dmsSchema = dmsResponse["content"]?["application/json"]?["schema"];

            if (odsSchema is null || dmsSchema is null)
            {
                return;
            }

            HashSet<string> odsTargets = ExtractResponseSchemaRefTargets(odsSchema);
            HashSet<string> dmsTargets = ExtractResponseSchemaRefTargets(dmsSchema);

            differences.AddRange(
                odsTargets
                    .Except(dmsTargets)
                    .Select(target => $"response-schema-ref-removed:{path}:{method}:200:{target}")
            );
            differences.AddRange(
                dmsTargets
                    .Except(odsTargets)
                    .Select(target => $"response-schema-ref-added:{path}:{method}:200:{target}")
            );
        }

        private static void AddComponentSchemaDifferences(
            List<string> differences,
            JsonObject ods,
            JsonObject dms
        )
        {
            JsonObject odsSchemas = ods["components"]!["schemas"]!.AsObject();
            JsonObject dmsSchemas = dms["components"]!["schemas"]!.AsObject();
            HashSet<string> odsSchemaNames = odsSchemas.Select(pair => pair.Key).ToHashSet();
            HashSet<string> dmsSchemaNames = dmsSchemas.Select(pair => pair.Key).ToHashSet();

            differences.AddRange(
                odsSchemaNames.Except(dmsSchemaNames).Select(name => $"component-removed:{name}")
            );
            differences.AddRange(
                dmsSchemaNames.Except(odsSchemaNames).Select(name => $"component-added:{name}")
            );

            foreach (string name in SharedComponentNames)
            {
                JsonObject odsSchema = odsSchemas[name]!.AsObject();
                JsonObject dmsSchema = dmsSchemas[name]!.AsObject();

                bool odsAdditionalProperties = odsSchema["additionalProperties"]?.GetValue<bool>() ?? false;
                bool dmsAdditionalProperties = dmsSchema["additionalProperties"]?.GetValue<bool>() ?? false;
                if (dmsAdditionalProperties && !odsAdditionalProperties)
                {
                    differences.Add($"additionalProperties-added:{name}");
                }

                HashSet<string> odsRequired =
                    (odsSchema["required"] as JsonArray)?.Select(node => node!.GetValue<string>()).ToHashSet()
                    ?? [];
                HashSet<string> dmsRequired =
                    (dmsSchema["required"] as JsonArray)?.Select(node => node!.GetValue<string>()).ToHashSet()
                    ?? [];

                differences.AddRange(
                    odsRequired.Except(dmsRequired).Select(property => $"required-removed:{name}:{property}")
                );
                differences.AddRange(
                    dmsRequired.Except(odsRequired).Select(property => $"required-added:{name}:{property}")
                );

                JsonObject dmsProperties = dmsSchema["properties"]!.AsObject();
                foreach ((string propertyName, JsonNode? dmsPropertyNode) in dmsProperties)
                {
                    if (dmsPropertyNode!.AsObject()["nullable"]?.GetValue<bool>() == true)
                    {
                        differences.Add($"nullable-added:{name}:{propertyName}");
                    }
                }
            }
        }

        private static void AddRootExtensionDifferences(
            List<string> differences,
            JsonObject ods,
            JsonObject dms
        )
        {
            HashSet<string> odsExtensionKeys = ods.Select(pair => pair.Key)
                .Where(key => key.StartsWith("x-", StringComparison.Ordinal))
                .ToHashSet();
            HashSet<string> dmsExtensionKeys = dms.Select(pair => pair.Key)
                .Where(key => key.StartsWith("x-", StringComparison.Ordinal))
                .ToHashSet();

            differences.AddRange(
                odsExtensionKeys.Except(dmsExtensionKeys).Select(key => $"root-extension-removed:{key}")
            );
            differences.AddRange(
                dmsExtensionKeys.Except(odsExtensionKeys).Select(key => $"root-extension-added:{key}")
            );
        }

        /// <summary>
        /// Resolves one level of local <c>$ref</c> against the supplied document, or returns the node
        /// unchanged when it is not a reference.
        /// </summary>
        private static JsonNode Resolve(JsonNode node, JsonObject document)
        {
            string? refValue = (node as JsonObject)?["$ref"]?.GetValue<string>();
            if (refValue is null)
            {
                return node;
            }

            JsonNode current = document;
            foreach (string segment in refValue[2..].Split('/'))
            {
                current = current[segment]!;
            }
            return current;
        }

        private static HashSet<string> ExtractResponseSchemaRefTargets(JsonNode schemaNode)
        {
            if (schemaNode is not JsonObject schema)
            {
                return [];
            }

            if (schema["$ref"] is JsonValue refValue && refValue.TryGetValue(out string? reference))
            {
                return [RefTargetName(reference)];
            }

            if (schema["oneOf"] is JsonArray oneOf)
            {
                return oneOf.Select(member => RefTargetName(member!["$ref"]!.GetValue<string>())).ToHashSet();
            }

            return [];
        }

        /// <summary>
        /// Extracts the <c>$ref</c> target name from a property schema, whether the reference is direct
        /// (<c>{"$ref": ...}</c>), wrapped for nullability (<c>{"allOf": [{"$ref": ...}], "nullable": true}</c>),
        /// or nested under an array's <c>items</c>. Returns null for a property with no reference.
        /// </summary>
        private static string? ExtractRefTargetName(JsonObject propertySchema)
        {
            JsonObject candidate =
                propertySchema["type"]?.GetValue<string>() == "array"
                && propertySchema["items"] is JsonObject itemsSchema
                    ? itemsSchema
                    : propertySchema;

            if (
                candidate["$ref"] is JsonValue directRef
                && directRef.TryGetValue(out string? directReference)
            )
            {
                return RefTargetName(directReference);
            }

            if (
                candidate["allOf"] is JsonArray allOf
                && allOf.Count == 1
                && allOf[0] is JsonObject soleMember
                && soleMember["$ref"] is JsonValue soleRefValue
                && soleRefValue.TryGetValue(out string? soleReference)
            )
            {
                return RefTargetName(soleReference);
            }

            return null;
        }

        private static string RefTargetName(string reference)
        {
            const string prefix = "#/components/schemas/";
            reference.Should().StartWith(prefix);
            return reference[prefix.Length..];
        }
    }
}
