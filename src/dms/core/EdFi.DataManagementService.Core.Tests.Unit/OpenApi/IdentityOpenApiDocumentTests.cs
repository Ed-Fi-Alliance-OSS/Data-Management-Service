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
public class IdentityOpenApiDocumentTests
{
    private static JsonNode Document => IdentityOpenApiDocument.Document;

    private static JsonObject Schemas => Document["components"]!["schemas"]!.AsObject();

    private static JsonObject Paths => Document["paths"]!.AsObject();

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

    [TestFixture]
    [Parallelizable]
    public class Given_The_Served_Identity_Document
    {
        [Test]
        public void It_has_no_servers()
        {
            Document["servers"].Should().BeNull();
        }

        [Test]
        public void It_has_no_security()
        {
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
        public void It_stamps_a_non_blank_identity_contract_version()
        {
            Document["x-edfi-identity-contract-version"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
        }

        [Test]
        public void It_strips_the_plus_build_metadata_from_the_contract_version_stamp()
        {
            Document["x-edfi-identity-contract-version"]!.GetValue<string>().Should().NotContain("+");
        }

        [Test]
        public void It_declares_the_six_ods_component_names()
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
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_Post_Operation_Request_Bodies
    {
        private static readonly string[] PostOperationPaths =
        [
            "/identities",
            "/identities/find",
            "/identities/search",
        ];

        [TestCaseSource(nameof(PostOperationPaths))]
        public void It_accepts_application_json_and_text_json(string path)
        {
            JsonObject requestBodyContent = Paths[path]!["post"]!["requestBody"]!["content"]!.AsObject();
            requestBodyContent
                .Select(pair => pair.Key)
                .Should()
                .BeEquivalentTo("application/json", "text/json");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_Create_Request_Body_Schema
    {
        private string _schemaName = null!;

        [SetUp]
        public void Setup()
        {
            JsonNode schemaRef = Paths["/identities"]!["post"]!["requestBody"]!["content"]![
                "application/json"
            ]!["schema"]!;
            _schemaName = ExtractRefName(schemaRef);
        }

        [Test]
        public void It_is_an_object()
        {
            Schemas[_schemaName]!["type"]!.GetValue<string>().Should().Be("object");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_Find_Request_Body_Schema
    {
        private JsonNode _schema = null!;

        [SetUp]
        public void Setup()
        {
            _schema = Paths["/identities/find"]!["post"]!["requestBody"]!["content"]!["application/json"]![
                "schema"
            ]!;
        }

        [Test]
        public void It_is_an_array()
        {
            _schema["type"]!.GetValue<string>().Should().Be("array");
        }

        [Test]
        public void It_has_string_items()
        {
            _schema["items"]!["type"]!.GetValue<string>().Should().Be("string");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_Search_Request_Body_Schema
    {
        private JsonNode _schema = null!;
        private string _itemSchemaName = null!;

        [SetUp]
        public void Setup()
        {
            _schema = Paths["/identities/search"]!["post"]!["requestBody"]!["content"]!["application/json"]![
                "schema"
            ]!;
            _itemSchemaName = ExtractRefName(_schema["items"]!);
        }

        [Test]
        public void It_is_an_array()
        {
            _schema["type"]!.GetValue<string>().Should().Be("array");
        }

        [Test]
        public void It_has_object_items()
        {
            Schemas[_itemSchemaName]!["type"]!.GetValue<string>().Should().Be("object");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_IdentityResponse_UniqueId_Property
    {
        private JsonObject _identityResponse = null!;

        [SetUp]
        public void Setup()
        {
            _identityResponse = Schemas["IdentityResponse"]!.AsObject();
        }

        [Test]
        public void It_is_a_string()
        {
            _identityResponse["properties"]!["UniqueId"]!["type"]!.GetValue<string>().Should().Be("string");
        }

        [Test]
        public void It_has_minLength_1()
        {
            _identityResponse["properties"]!["UniqueId"]!["minLength"]!.GetValue<int>().Should().Be(1);
        }

        [Test]
        public void It_is_required()
        {
            _identityResponse["required"]!
                .AsArray()
                .Select(n => n!.GetValue<string>())
                .Should()
                .Contain("UniqueId");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_IdentityResponse_Standard_Attributes
    {
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

        private JsonObject _properties = null!;
        private string[] _required = null!;

        [SetUp]
        public void Setup()
        {
            JsonObject identityResponse = Schemas["IdentityResponse"]!.AsObject();
            _properties = identityResponse["properties"]!.AsObject();
            _required = identityResponse["required"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
        }

        [TestCaseSource(nameof(StandardAttributeNames))]
        public void It_is_nullable(string attribute)
        {
            _properties[attribute]!["nullable"]!.GetValue<bool>().Should().BeTrue();
        }

        [TestCaseSource(nameof(StandardAttributeNames))]
        public void It_is_required(string attribute)
        {
            _required.Should().Contain(attribute);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_IdentityResponse_Additional_Required_Properties
    {
        private string[] _required = null!;

        [SetUp]
        public void Setup()
        {
            JsonObject identityResponse = Schemas["IdentityResponse"]!.AsObject();
            _required = identityResponse["required"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
        }

        [TestCase("BirthLocation")]
        [TestCase("Score")]
        public void It_is_required(string property)
        {
            _required.Should().Contain(property);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_IdentityResponse_BirthDate_Property
    {
        private JsonObject _properties = null!;

        [SetUp]
        public void Setup()
        {
            _properties = Schemas["IdentityResponse"]!["properties"]!.AsObject();
        }

        [Test]
        public void It_is_a_string()
        {
            _properties["BirthDate"]!["type"]!.GetValue<string>().Should().Be("string");
        }

        [Test]
        public void It_has_the_date_time_format()
        {
            _properties["BirthDate"]!["format"]!.GetValue<string>().Should().Be("date-time");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_IdentityResponse_BirthOrder_Property
    {
        private JsonObject _properties = null!;

        [SetUp]
        public void Setup()
        {
            _properties = Schemas["IdentityResponse"]!["properties"]!.AsObject();
        }

        [Test]
        public void It_is_an_integer()
        {
            _properties["BirthOrder"]!["type"]!.GetValue<string>().Should().Be("integer");
        }

        [Test]
        public void It_has_the_int32_format()
        {
            _properties["BirthOrder"]!["format"]!.GetValue<string>().Should().Be("int32");
        }

        [Test]
        public void It_is_nullable()
        {
            _properties["BirthOrder"]!["nullable"]!.GetValue<bool>().Should().BeTrue();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_IdentityResponse_Score_Property
    {
        private JsonObject _properties = null!;

        [SetUp]
        public void Setup()
        {
            _properties = Schemas["IdentityResponse"]!["properties"]!.AsObject();
        }

        [Test]
        public void It_is_a_number()
        {
            _properties["Score"]!["type"]!.GetValue<string>().Should().Be("number");
        }

        [Test]
        public void It_has_the_double_format()
        {
            _properties["Score"]!["format"]!.GetValue<string>().Should().Be("double");
        }

        [Test]
        public void It_is_nullable()
        {
            _properties["Score"]!["nullable"]!.GetValue<bool>().Should().BeTrue();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_IdentityResponse_BirthLocation_Property
    {
        private JsonObject _identityResponse = null!;

        [SetUp]
        public void Setup()
        {
            _identityResponse = Schemas["IdentityResponse"]!.AsObject();
        }

        [Test]
        public void It_references_Location()
        {
            string refName = ExtractRefName(_identityResponse["properties"]!["BirthLocation"]!);
            refName.Should().Be("Location");
        }

        [Test]
        public void It_is_required()
        {
            _identityResponse["required"]!
                .AsArray()
                .Select(n => n!.GetValue<string>())
                .Should()
                .Contain("BirthLocation");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_Location_Schema_Children
    {
        private static readonly string[] LocationChildNames =
        [
            "City",
            "StateAbbreviation",
            "InternationalProvince",
            "Country",
        ];

        private JsonObject _properties = null!;

        [SetUp]
        public void Setup()
        {
            _properties = Schemas["Location"]!["properties"]!.AsObject();
        }

        [TestCaseSource(nameof(LocationChildNames))]
        public void It_is_a_string(string child)
        {
            _properties[child]!["type"]!.GetValue<string>().Should().Be("string");
        }

        [TestCaseSource(nameof(LocationChildNames))]
        public void It_is_nullable(string child)
        {
            _properties[child]!["nullable"]!.GetValue<bool>().Should().BeTrue();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_Request_And_Response_Schemas
    {
        [TestCase("IdentityCreateRequest")]
        [TestCase("IdentitySearchRequest")]
        [TestCase("Location")]
        [TestCase("IdentityResponse")]
        [TestCase("IdentitySearchResponses")]
        [TestCase("IdentitySearchResponse")]
        [TestCase("IdentitySearchResponseComplete")]
        [TestCase("IdentitySearchResponseIncomplete")]
        public void It_declares_additionalProperties_true(string schemaName)
        {
            Schemas[schemaName]!["additionalProperties"]!.GetValue<bool>().Should().BeTrue();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_Search_Response_Result_State_Schemas
    {
        private JsonObject _complete = null!;
        private JsonObject _incomplete = null!;

        [SetUp]
        public void Setup()
        {
            _complete = Schemas["IdentitySearchResponseComplete"]!.AsObject();
            _incomplete = Schemas["IdentitySearchResponseIncomplete"]!.AsObject();
        }

        [Test]
        public void It_the_complete_schema_has_a_single_value_status_enum()
        {
            _complete["properties"]!["Status"]!["enum"]!
                .AsArray()
                .Select(n => n!.GetValue<string>())
                .Should()
                .Equal("Complete");
        }

        [Test]
        public void It_the_complete_schema_requires_SearchResponses()
        {
            _complete["required"]!
                .AsArray()
                .Select(n => n!.GetValue<string>())
                .Should()
                .Contain("SearchResponses");
        }

        [Test]
        public void It_the_incomplete_schema_has_a_single_value_status_enum()
        {
            _incomplete["properties"]!["Status"]!["enum"]!
                .AsArray()
                .Select(n => n!.GetValue<string>())
                .Should()
                .Equal("Incomplete");
        }

        [Test]
        public void It_the_incomplete_schema_does_not_require_SearchResponses()
        {
            (_incomplete["required"]?.AsArray().Select(n => n!.GetValue<string>()) ?? [])
                .Should()
                .NotContain("SearchResponses");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_Find_And_Search_200_Response_Schema
    {
        [TestCase("/identities/find")]
        [TestCase("/identities/search")]
        public void It_references_only_the_complete_search_response_schema(string path)
        {
            JsonNode schemaNode = Paths[path]!["post"]!["responses"]!["200"]!["content"]![
                "application/json"
            ]!["schema"]!;
            ExtractRefName(schemaNode).Should().Be("IdentitySearchResponseComplete");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_Results_200_Response_Schema
    {
        private JsonArray _oneOf = null!;

        [SetUp]
        public void Setup()
        {
            JsonNode schema = Paths["/identities/results/{id}"]!["get"]!["responses"]!["200"]!["content"]![
                "application/json"
            ]!["schema"]!;
            _oneOf = schema["oneOf"]!.AsArray();
        }

        [Test]
        public void It_permits_both_the_complete_and_incomplete_search_response_schemas()
        {
            _oneOf
                .Select(ExtractRefName)
                .Should()
                .BeEquivalentTo("IdentitySearchResponseComplete", "IdentitySearchResponseIncomplete");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Every_Response_In_The_Document
    {
        [Test]
        public void It_declares_a_no_store_cache_control_header()
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
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_Create_Response_Matrix
    {
        private JsonObject _responses = null!;

        [SetUp]
        public void Setup()
        {
            _responses = Paths["/identities"]!["post"]!["responses"]!.AsObject();
        }

        [Test]
        public void It_has_no_Location_header_on_200()
        {
            _responses["200"]!["headers"]!["Location"].Should().BeNull();
        }

        [Test]
        public void It_returns_a_string_body_on_200()
        {
            _responses["200"]!["content"]!["application/json"]!["schema"]!["type"]!
                .GetValue<string>()
                .Should()
                .Be("string");
        }

        [Test]
        public void It_declares_the_common_error_responses_including_415()
        {
            AssertCommonErrorResponses(_responses, expects415: true, expectsJobFailed: false);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_GetById_Response_Matrix
    {
        private JsonObject _responses = null!;

        [SetUp]
        public void Setup()
        {
            _responses = Paths["/identities/{id}"]!["get"]!["responses"]!.AsObject();
        }

        [Test]
        public void It_does_not_declare_415()
        {
            _responses.Should().NotContainKey("415");
        }

        [Test]
        public void It_declares_the_common_error_responses_without_415()
        {
            AssertCommonErrorResponses(_responses, expects415: false, expectsJobFailed: false);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_Find_And_Search_Response_Matrix
    {
        [TestCase("/identities/find")]
        [TestCase("/identities/search")]
        public void It_has_a_202_with_a_required_Location_header(string path)
        {
            JsonObject responses = Paths[path]!["post"]!["responses"]!.AsObject();
            JsonObject accepted = responses["202"]!.AsObject();
            accepted["headers"]!["Location"]!["required"]!.GetValue<bool>().Should().BeTrue();
        }

        [TestCase("/identities/find")]
        [TestCase("/identities/search")]
        public void It_declares_the_common_error_responses_including_415(string path)
        {
            JsonObject responses = Paths[path]!["post"]!["responses"]!.AsObject();
            AssertCommonErrorResponses(responses, expects415: true, expectsJobFailed: false);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_Results_Response_Matrix
    {
        private JsonObject _responses = null!;

        [SetUp]
        public void Setup()
        {
            _responses = Paths["/identities/results/{id}"]!["get"]!["responses"]!.AsObject();
        }

        [Test]
        public void It_does_not_declare_415()
        {
            _responses.Should().NotContainKey("415");
        }

        [Test]
        public void It_declares_the_common_error_responses_permitting_job_failed_on_502()
        {
            AssertCommonErrorResponses(_responses, expects415: false, expectsJobFailed: true);
        }

        [Test]
        public void It_declares_a_Location_header_on_200()
        {
            JsonObject accepted200 = _responses["200"]!.AsObject();
            accepted200["headers"]!["Location"].Should().NotBeNull();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_404_Response_Problem_Types
    {
        private List<string> _types = null!;

        [SetUp]
        public void Setup()
        {
            JsonNode fourOhFour = Resolve(Paths["/identities"]!["post"]!["responses"]!["404"]!);
            JsonArray oneOf = fourOhFour["content"]!["application/problem+json"]!["schema"]![
                "oneOf"
            ]!.AsArray();
            _types = oneOf
                .Select(ExtractRefName)
                .Select(name =>
                    Schemas[name]!["allOf"]![1]!["properties"]!["type"]!["enum"]![0]!.GetValue<string>()
                )
                .ToList();
        }

        [Test]
        public void It_documents_two_distinct_problem_types()
        {
            _types
                .Should()
                .BeEquivalentTo(
                    "urn:ed-fi:api:identities:operation-not-supported",
                    "urn:ed-fi:api:identities:not-found"
                );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_502_Response_Problem_Types
    {
        private List<string> _types = null!;

        [SetUp]
        public void Setup()
        {
            JsonNode fiveOhTwo = Resolve(Paths["/identities"]!["post"]!["responses"]!["502"]!);
            JsonArray oneOf = fiveOhTwo["content"]!["application/problem+json"]!["schema"]![
                "oneOf"
            ]!.AsArray();
            _types = oneOf
                .Select(ExtractRefName)
                .Select(name =>
                    Schemas[name]!["allOf"]![1]!["properties"]!["type"]!["enum"]![0]!.GetValue<string>()
                )
                .ToList();
        }

        [Test]
        public void It_documents_contract_violation_and_upstream_failure_as_distinct_types()
        {
            _types
                .Should()
                .BeEquivalentTo(
                    "urn:ed-fi:api:identities:provider-contract-violation",
                    "urn:ed-fi:api:identities:upstream-failure"
                );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_Results_502_Response_Problem_Types
    {
        private List<string> _types = null!;

        [SetUp]
        public void Setup()
        {
            JsonNode fiveOhTwo = Resolve(Paths["/identities/results/{id}"]!["get"]!["responses"]!["502"]!);
            JsonArray oneOf = fiveOhTwo["content"]!["application/problem+json"]!["schema"]![
                "oneOf"
            ]!.AsArray();
            _types = oneOf
                .Select(ExtractRefName)
                .Select(name =>
                    Schemas[name]!["allOf"]![1]!["properties"]!["type"]!["enum"]![0]!.GetValue<string>()
                )
                .ToList();
        }

        [Test]
        public void It_adds_the_job_failed_type()
        {
            _types.Should().Contain("urn:ed-fi:api:identities:job-failed");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_429_Response_Retry_After_Header
    {
        private JsonNode _retryAfterHeader = null!;

        [SetUp]
        public void Setup()
        {
            JsonNode fourTwoNine = Resolve(Paths["/identities"]!["post"]!["responses"]!["429"]!);
            _retryAfterHeader = Resolve(fourTwoNine["headers"]!["Retry-After"]!);
        }

        [Test]
        public void It_declares_an_optional_retry_after_header()
        {
            _retryAfterHeader["schema"]!["type"]!.GetValue<string>().Should().Be("integer");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_Find_No_Match_Example
    {
        private JsonObject _noMatchExample = null!;

        [SetUp]
        public void Setup()
        {
            JsonNode findSuccess = Paths["/identities/find"]!["post"]!["responses"]!["200"]!["content"]![
                "application/json"
            ]!;
            _noMatchExample = findSuccess["examples"]!["noMatch"]!["value"]!.AsObject();
        }

        [Test]
        public void It_has_empty_responses_for_every_search_response()
        {
            foreach (JsonNode? searchResponse in _noMatchExample["SearchResponses"]!.AsArray())
            {
                searchResponse!["Responses"]!.AsArray().Should().BeEmpty();
            }
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_Results_Examples
    {
        private JsonObject _examples = null!;

        [SetUp]
        public void Setup()
        {
            JsonNode resultsSuccess = Paths["/identities/results/{id}"]!["get"]!["responses"]!["200"]![
                "content"
            ]!["application/json"]!;
            _examples = resultsSuccess["examples"]!.AsObject();
        }

        [Test]
        public void It_the_complete_example_has_status_Complete()
        {
            _examples["complete"]!["value"]!["Status"]!.GetValue<string>().Should().Be("Complete");
        }

        [Test]
        public void It_the_incomplete_example_has_status_Incomplete()
        {
            _examples["incomplete"]!["value"]!["Status"]!.GetValue<string>().Should().Be("Incomplete");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_400_Response_Pinned_Examples
    {
        private JsonObject _examples = null!;

        [SetUp]
        public void Setup()
        {
            JsonNode badRequest = Resolve(Paths["/identities"]!["post"]!["responses"]!["400"]!);
            _examples = badRequest["content"]!["application/problem+json"]!["examples"]!.AsObject();
        }

        [Test]
        public void It_the_createFieldError_example_has_a_firstName_validation_error()
        {
            _examples["createFieldError"]!["value"]!["validationErrors"]!["$.firstName"].Should().NotBeNull();
        }

        [Test]
        public void It_the_searchItemError_example_has_an_indexed_firstName_validation_error()
        {
            _examples["searchItemError"]!["value"]!["validationErrors"]!
                ["$[2].firstName"]
                .Should()
                .NotBeNull();
        }

        [Test]
        public void It_the_pathlessError_example_has_a_non_empty_errors_array()
        {
            JsonArray pathlessErrors = _examples["pathlessError"]!["value"]!["errors"]!.AsArray();
            pathlessErrors.Should().NotBeEmpty();
        }

        [Test]
        public void It_the_twoMessagesOneKey_example_has_two_messages_for_firstName()
        {
            JsonArray twoMessages = _examples["twoMessagesOneKey"]!["value"]!["validationErrors"]![
                "$.firstName"
            ]!.AsArray();
            twoMessages.Should().HaveCount(2);
        }
    }
}
