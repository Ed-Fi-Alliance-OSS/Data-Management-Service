// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.OpenApi;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.OpenApi;

/// <summary>
/// Walks the embedded identity OpenAPI document (design.md D2) and pins the paths, media types,
/// request/response shapes, required/nullable sets, enums, additionalProperties, response
/// declarations, headers, and examples the story's D2 and D3 acceptance criteria name.
/// </summary>
[TestFixture]
public class IdentityOpenApiDocumentTests
{
    private static JsonNode Document => IdentityOpenApiDocument.Document;

    private static JsonObject Schemas => Document["components"]!["schemas"]!.AsObject();

    private static JsonObject Paths => Document["paths"]!.AsObject();

    [Test]
    public void It_has_no_servers_or_security_because_those_are_injected_at_serve_time()
    {
        Document["servers"].Should().BeNull();
        Document["security"].Should().BeNull();
    }

    [Test]
    public void It_declares_the_exact_five_paths_relative_to_the_identity_v2_base()
    {
        Paths
            .Select(pair => pair.Key)
            .Should()
            .BeEquivalentTo(
                "/identities",
                "/identities/{id}",
                "/identities/find",
                "/identities/search",
                "/identities/results/{id}"
            );
    }

    [Test]
    public void It_stamps_the_identity_contract_version()
    {
        Document["x-edfi-identity-contract-version"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
        Document["x-edfi-identity-contract-version"]!.GetValue<string>().Should().NotContain("+");
    }

    private static readonly string[] PostOperationPaths =
    [
        "/identities",
        "/identities/find",
        "/identities/search",
    ];

    [TestCaseSource(nameof(PostOperationPaths))]
    public void Each_post_operation_accepts_application_json_and_text_json(string path)
    {
        JsonObject requestBodyContent = Paths[path]!["post"]!["requestBody"]!["content"]!.AsObject();
        requestBodyContent.Select(pair => pair.Key).Should().BeEquivalentTo("application/json", "text/json");
    }

    [Test]
    public void Create_request_body_schema_is_an_object()
    {
        JsonNode schemaRef = Paths["/identities"]!["post"]!["requestBody"]!["content"]!["application/json"]![
            "schema"
        ]!;
        string schemaName = ExtractRefName(schemaRef);
        Schemas[schemaName]!["type"]!.GetValue<string>().Should().Be("object");
    }

    [Test]
    public void Find_request_body_schema_is_an_array_of_strings()
    {
        JsonNode schema = Paths["/identities/find"]!["post"]!["requestBody"]!["content"]![
            "application/json"
        ]!["schema"]!;
        schema["type"]!.GetValue<string>().Should().Be("array");
        schema["items"]!["type"]!.GetValue<string>().Should().Be("string");
    }

    [Test]
    public void Search_request_body_schema_is_an_array_of_objects()
    {
        JsonNode schema = Paths["/identities/search"]!["post"]!["requestBody"]!["content"]![
            "application/json"
        ]!["schema"]!;
        schema["type"]!.GetValue<string>().Should().Be("array");
        string itemSchemaName = ExtractRefName(schema["items"]!);
        Schemas[itemSchemaName]!["type"]!.GetValue<string>().Should().Be("object");
    }

    [Test]
    public void The_six_ODS_component_names_are_present()
    {
        Schemas
            .Select(pair => pair.Key)
            .Should()
            .Contain([
                "IdentityCreateRequest",
                "IdentityResponse",
                "IdentitySearchRequest",
                "IdentitySearchResponse",
                "IdentitySearchResponses",
                "Location",
            ]);
    }

    [Test]
    public void IdentityResponse_requires_UniqueId_as_a_non_empty_string()
    {
        JsonObject identityResponse = Schemas["IdentityResponse"]!.AsObject();
        identityResponse["properties"]!["UniqueId"]!["type"]!.GetValue<string>().Should().Be("string");
        identityResponse["properties"]!["UniqueId"]!["minLength"]!.GetValue<int>().Should().Be(1);
        identityResponse["required"]!
            .AsArray()
            .Select(n => n!.GetValue<string>())
            .Should()
            .Contain("UniqueId");
    }

    private static readonly string[] StandardAttributeNames =
    [
        "LastSurname",
        "FirstName",
        "MiddleName",
        "GenerationCodeSuffix",
        "SexType",
        "BirthDate",
        "BirthOrder",
    ];

    [Test]
    public void IdentityResponse_marks_every_standard_attribute_required_and_nullable()
    {
        JsonObject identityResponse = Schemas["IdentityResponse"]!.AsObject();
        JsonObject properties = identityResponse["properties"]!.AsObject();
        string[] required = identityResponse["required"]!
            .AsArray()
            .Select(n => n!.GetValue<string>())
            .ToArray();

        foreach (string attribute in StandardAttributeNames)
        {
            properties[attribute]!["nullable"]!.GetValue<bool>().Should().BeTrue();
            required.Should().Contain(attribute);
        }

        required.Should().Contain("BirthLocation");
        required.Should().Contain("Score");
    }

    [Test]
    public void IdentityResponse_declares_BirthDate_as_a_date_time_string()
    {
        JsonObject properties = Schemas["IdentityResponse"]!["properties"]!.AsObject();
        properties["BirthDate"]!["type"]!.GetValue<string>().Should().Be("string");
        properties["BirthDate"]!["format"]!.GetValue<string>().Should().Be("date-time");
    }

    [Test]
    public void IdentityResponse_declares_BirthOrder_as_an_integer()
    {
        JsonObject properties = Schemas["IdentityResponse"]!["properties"]!.AsObject();
        properties["BirthOrder"]!["type"]!.GetValue<string>().Should().Be("integer");
        properties["BirthOrder"]!["format"]!.GetValue<string>().Should().Be("int32");
        properties["BirthOrder"]!["nullable"]!.GetValue<bool>().Should().BeTrue();
    }

    [Test]
    public void IdentityResponse_declares_Score_as_a_nullable_double()
    {
        JsonObject properties = Schemas["IdentityResponse"]!["properties"]!.AsObject();
        properties["Score"]!["type"]!.GetValue<string>().Should().Be("number");
        properties["Score"]!["format"]!.GetValue<string>().Should().Be("double");
        properties["Score"]!["nullable"]!.GetValue<bool>().Should().BeTrue();
    }

    [Test]
    public void IdentityResponse_BirthLocation_references_Location_as_a_required_object()
    {
        JsonObject identityResponse = Schemas["IdentityResponse"]!.AsObject();
        string refName = ExtractRefName(identityResponse["properties"]!["BirthLocation"]!);
        refName.Should().Be("Location");
        identityResponse["required"]!
            .AsArray()
            .Select(n => n!.GetValue<string>())
            .Should()
            .Contain("BirthLocation");
    }

    private static readonly string[] LocationChildNames =
    [
        "City",
        "StateAbbreviation",
        "InternationalProvince",
        "Country",
    ];

    [Test]
    public void Location_declares_four_nullable_string_children()
    {
        JsonObject properties = Schemas["Location"]!["properties"]!.AsObject();
        foreach (string child in LocationChildNames)
        {
            properties[child]!["type"]!.GetValue<string>().Should().Be("string");
            properties[child]!["nullable"]!.GetValue<bool>().Should().BeTrue();
        }
    }

    [TestCase("IdentityCreateRequest")]
    [TestCase("IdentitySearchRequest")]
    [TestCase("Location")]
    [TestCase("IdentityResponse")]
    [TestCase("IdentitySearchResponses")]
    [TestCase("IdentitySearchResponse")]
    [TestCase("IdentitySearchResponseComplete")]
    [TestCase("IdentitySearchResponseIncomplete")]
    public void Request_and_response_objects_declare_additionalProperties_true(string schemaName)
    {
        Schemas[schemaName]!["additionalProperties"]!.GetValue<bool>().Should().BeTrue();
    }

    [Test]
    public void DMS_adds_a_complete_and_an_incomplete_search_response_schema_with_single_value_status_enums()
    {
        JsonObject complete = Schemas["IdentitySearchResponseComplete"]!.AsObject();
        complete["properties"]!["Status"]!["enum"]!
            .AsArray()
            .Select(n => n!.GetValue<string>())
            .Should()
            .Equal("Complete");
        complete["required"]!
            .AsArray()
            .Select(n => n!.GetValue<string>())
            .Should()
            .Contain("SearchResponses");

        JsonObject incomplete = Schemas["IdentitySearchResponseIncomplete"]!.AsObject();
        incomplete["properties"]!["Status"]!["enum"]!
            .AsArray()
            .Select(n => n!.GetValue<string>())
            .Should()
            .Equal("Incomplete");
        (incomplete["required"]?.AsArray().Select(n => n!.GetValue<string>()) ?? [])
            .Should()
            .NotContain("SearchResponses");
    }

    [Test]
    public void Find_and_search_200_reference_only_the_complete_search_response_schema()
    {
        foreach (string path in new[] { "/identities/find", "/identities/search" })
        {
            JsonNode schemaNode = Paths[path]!["post"]!["responses"]!["200"]!["content"]![
                "application/json"
            ]!["schema"]!;
            ExtractRefName(schemaNode).Should().Be("IdentitySearchResponseComplete");
        }
    }

    [Test]
    public void Results_200_permits_both_the_complete_and_incomplete_search_response_schemas()
    {
        JsonNode schema = Paths["/identities/results/{id}"]!["get"]!["responses"]!["200"]!["content"]![
            "application/json"
        ]!["schema"]!;
        JsonArray oneOf = schema["oneOf"]!.AsArray();
        oneOf
            .Select(ExtractRefName)
            .Should()
            .BeEquivalentTo("IdentitySearchResponseComplete", "IdentitySearchResponseIncomplete");
    }

    [Test]
    public void Every_response_across_the_document_declares_a_no_store_cache_control_header()
    {
        foreach ((string path, JsonNode? pathItemNode) in Paths)
        {
            JsonObject pathItem = pathItemNode!.AsObject();
            foreach ((string method, JsonNode? operationNode) in pathItem)
            {
                JsonObject responses = operationNode!["responses"]!.AsObject();
                foreach ((string statusCode, JsonNode? responseNode) in responses)
                {
                    JsonNode resolvedResponse = Resolve(responseNode!);
                    JsonNode? cacheControlHeader = resolvedResponse["headers"]?["Cache-Control"];
                    cacheControlHeader
                        .Should()
                        .NotBeNull($"{path} {method} {statusCode} must declare a Cache-Control header");

                    JsonNode resolvedHeader = Resolve(cacheControlHeader!);
                    resolvedHeader["schema"]!["enum"]!
                        .AsArray()
                        .Select(n => n!.GetValue<string>())
                        .Should()
                        .Equal("no-store");
                }
            }
        }
    }

    [Test]
    public void Create_response_matrix_has_no_415_and_no_location_and_a_string_body()
    {
        JsonObject responses = Paths["/identities"]!["post"]!["responses"]!.AsObject();
        responses["200"]!["headers"]!["Location"].Should().BeNull();
        responses["200"]!["content"]!["application/json"]!["schema"]!["type"]!
            .GetValue<string>()
            .Should()
            .Be("string");

        AssertCommonErrorResponses(responses, expects415: true, expectsJobFailed: false);
    }

    [Test]
    public void GetById_response_matrix_has_no_415()
    {
        JsonObject responses = Paths["/identities/{id}"]!["get"]!["responses"]!.AsObject();
        responses.Should().NotContainKey("415");
        AssertCommonErrorResponses(responses, expects415: false, expectsJobFailed: false);
    }

    [Test]
    public void Find_and_search_response_matrix_has_202_with_a_required_location_header()
    {
        foreach (string path in new[] { "/identities/find", "/identities/search" })
        {
            JsonObject responses = Paths[path]!["post"]!["responses"]!.AsObject();
            JsonObject accepted = responses["202"]!.AsObject();
            accepted["headers"]!["Location"]!["required"]!.GetValue<bool>().Should().BeTrue();
            AssertCommonErrorResponses(responses, expects415: true, expectsJobFailed: false);
        }
    }

    [Test]
    public void Results_response_matrix_has_no_415_and_permits_job_failed_on_502()
    {
        JsonObject responses = Paths["/identities/results/{id}"]!["get"]!["responses"]!.AsObject();
        responses.Should().NotContainKey("415");
        AssertCommonErrorResponses(responses, expects415: false, expectsJobFailed: true);

        JsonObject accepted200 = responses["200"]!.AsObject();
        accepted200["headers"]!["Location"].Should().NotBeNull();
    }

    [Test]
    public void The_404_response_documents_two_distinct_problem_types()
    {
        JsonNode fourOhFour = Resolve(Paths["/identities"]!["post"]!["responses"]!["404"]!);
        JsonArray oneOf = fourOhFour["content"]!["application/problem+json"]!["schema"]!["oneOf"]!.AsArray();
        List<string> types = oneOf
            .Select(ExtractRefName)
            .Select(name =>
                Schemas[name]!["allOf"]![1]!["properties"]!["type"]!["enum"]![0]!.GetValue<string>()
            )
            .ToList();

        types
            .Should()
            .BeEquivalentTo(
                "urn:ed-fi:api:identities:operation-not-supported",
                "urn:ed-fi:api:identities:not-found"
            );
    }

    [Test]
    public void The_502_response_documents_contract_violation_and_upstream_failure_as_distinct_types()
    {
        JsonNode fiveOhTwo = Resolve(Paths["/identities"]!["post"]!["responses"]!["502"]!);
        JsonArray oneOf = fiveOhTwo["content"]!["application/problem+json"]!["schema"]!["oneOf"]!.AsArray();
        List<string> types = oneOf
            .Select(ExtractRefName)
            .Select(name =>
                Schemas[name]!["allOf"]![1]!["properties"]!["type"]!["enum"]![0]!.GetValue<string>()
            )
            .ToList();

        types
            .Should()
            .BeEquivalentTo(
                "urn:ed-fi:api:identities:provider-contract-violation",
                "urn:ed-fi:api:identities:upstream-failure"
            );
    }

    [Test]
    public void The_results_502_response_adds_the_job_failed_type()
    {
        JsonNode fiveOhTwo = Resolve(Paths["/identities/results/{id}"]!["get"]!["responses"]!["502"]!);
        JsonArray oneOf = fiveOhTwo["content"]!["application/problem+json"]!["schema"]!["oneOf"]!.AsArray();
        List<string> types = oneOf
            .Select(ExtractRefName)
            .Select(name =>
                Schemas[name]!["allOf"]![1]!["properties"]!["type"]!["enum"]![0]!.GetValue<string>()
            )
            .ToList();

        types.Should().Contain("urn:ed-fi:api:identities:job-failed");
    }

    [Test]
    public void The_429_response_declares_an_optional_retry_after_header()
    {
        JsonNode fourTwoNine = Resolve(Paths["/identities"]!["post"]!["responses"]!["429"]!);
        JsonNode retryAfterHeader = fourTwoNine["headers"]!["Retry-After"]!;
        Resolve(retryAfterHeader)["schema"]!["type"]!.GetValue<string>().Should().Be("integer");
    }

    [Test]
    public void Examples_include_a_no_match_group_with_empty_responses()
    {
        JsonNode findSuccess = Paths["/identities/find"]!["post"]!["responses"]!["200"]!["content"]![
            "application/json"
        ]!;
        JsonObject noMatchExample = findSuccess["examples"]!["noMatch"]!["value"]!.AsObject();

        foreach (JsonNode? searchResponse in noMatchExample["SearchResponses"]!.AsArray())
        {
            searchResponse!["Responses"]!.AsArray().Should().BeEmpty();
        }
    }

    [Test]
    public void Examples_include_both_the_complete_and_incomplete_results_states()
    {
        JsonNode resultsSuccess = Paths["/identities/results/{id}"]!["get"]!["responses"]!["200"]![
            "content"
        ]!["application/json"]!;
        JsonObject examples = resultsSuccess["examples"]!.AsObject();

        examples["complete"]!["value"]!["Status"]!.GetValue<string>().Should().Be("Complete");
        examples["incomplete"]!["value"]!["Status"]!.GetValue<string>().Should().Be("Incomplete");
    }

    [Test]
    public void The_400_response_pins_the_four_projection_row_examples()
    {
        JsonNode badRequest = Resolve(Paths["/identities"]!["post"]!["responses"]!["400"]!);
        JsonObject examples = badRequest["content"]!["application/problem+json"]!["examples"]!.AsObject();

        examples["createFieldError"]!["value"]!["validationErrors"]!["$.firstName"].Should().NotBeNull();
        examples["searchItemError"]!["value"]!["validationErrors"]!["$[2].firstName"].Should().NotBeNull();

        JsonArray pathlessErrors = examples["pathlessError"]!["value"]!["errors"]!.AsArray();
        pathlessErrors.Should().NotBeEmpty();

        JsonArray twoMessages = examples["twoMessagesOneKey"]!["value"]!["validationErrors"]![
            "$.firstName"
        ]!.AsArray();
        twoMessages.Should().HaveCount(2);
    }

    private static void AssertCommonErrorResponses(
        JsonObject responses,
        bool expects415,
        bool expectsJobFailed
    )
    {
        responses.Should().ContainKey("400");
        responses.Should().ContainKey("404");
        responses.Should().ContainKey("502");
        responses.Should().ContainKey("500");
        responses.Should().ContainKey("429");

        if (expects415)
        {
            responses.Should().ContainKey("415");
        }

        JsonNode fiveOhTwo = Resolve(responses["502"]!);
        JsonArray oneOf = fiveOhTwo["content"]!["application/problem+json"]!["schema"]!["oneOf"]!.AsArray();
        bool hasJobFailed = oneOf
            .Select(ExtractRefName)
            .Any(name => name == "IdentityJobFailedProblemDetails");
        hasJobFailed.Should().Be(expectsJobFailed);
    }

    /// <summary>
    /// Resolves one level of local <c>$ref</c> against the loaded document, or returns the node
    /// unchanged when it is not a reference.
    /// </summary>
    private static JsonNode Resolve(JsonNode node)
    {
        string? refValue = node["$ref"]?.GetValue<string>();
        if (refValue is null)
        {
            return node;
        }

        return ResolvePath(refValue);
    }

    private static JsonNode ResolvePath(string reference)
    {
        reference.Should().StartWith("#/");
        JsonNode current = Document;
        foreach (string segment in reference[2..].Split('/'))
        {
            current = current[segment]!;
        }
        return current;
    }

    private static string ExtractRefName(JsonNode? node)
    {
        string reference =
            node?["$ref"]?.GetValue<string>() ?? throw new InvalidOperationException("Expected a $ref node.");
        const string prefix = "#/components/schemas/";
        reference.Should().StartWith(prefix);
        return reference[prefix.Length..];
    }
}
