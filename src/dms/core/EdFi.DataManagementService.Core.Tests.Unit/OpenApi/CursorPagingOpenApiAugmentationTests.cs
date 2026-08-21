// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.OpenApi;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using static EdFi.DataManagementService.Core.Tests.Unit.OpenApi.CursorPagingOpenApiTestBase;

namespace EdFi.DataManagementService.Core.Tests.Unit.OpenApi;

public class CursorPagingOpenApiAugmentationTests
{
    private const string PageTokenReference = "#/components/parameters/pageToken";
    private const string PageSizeReference = "#/components/parameters/pageSize";
    private const string NumberOfPartitionsReference = "#/components/parameters/numberOfPartitions";
    private const string PartitionTokensReference = "#/components/schemas/partitionTokens";
    private const string NextPageTokenHeader = "Next-Page-Token";

    private const string ExpectedPartitionSummary =
        "Retrieves the page tokens that partition this resource for parallel cursor paging.";

    private const string ExpectedPartitionDescription =
        "This GET operation returns a set of opaque page tokens that divide the accessible items of this "
        + "resource into ranges that can be retrieved in parallel using the pageToken parameter of the "
        + "collection GET operation. Boundaries are calculated after the same filters and authorization the "
        + "collection GET applies, so the same filters must be repeated on every request. The response may "
        + "contain fewer tokens than requested and never contains more.";

    private const string ExpectedPartitionResponseDescription =
        "The requested page tokens were successfully retrieved.";

    private const string ExpectedNumberOfPartitionsDescription =
        "The number of evenly distributed partitions to provide for client-side parallel processing. If "
        + "unspecified, the configured default number of partitions for this deployment is used.";

    /// <summary>
    /// The clause the shipped ApiSchema carries, which promises a count derived from the number of
    /// accessible items. Assembly publishes a fixed configured default instead, so this must not survive.
    /// </summary>
    private const string StalePartitionCountClause =
        "a reasonable set of partitions will be determined based on the total number of accessible items";

    private const string ExpectedNextPageTokenDescription =
        "An opaque token that retrieves the next page of results when supplied as the pageToken parameter "
        + "of this operation. Present only when a further page may exist.";

    private static JsonNode Assemble(
        ApiSchemaDocumentNodes apiSchemaNodes,
        OpenApiDocument.OpenApiDocumentType documentType = OpenApiDocument.OpenApiDocumentType.Resource,
        OpenApiPagingSettings? pagingSettings = null,
        string[]? excludedDomains = null
    )
    {
        OpenApiDocument openApiDocument = new(NullLogger.Instance, excludedDomains, pagingSettings);
        return openApiDocument.CreateDocument(apiSchemaNodes, documentType);
    }

    private static JsonObject Paths(JsonNode specification) => specification["paths"]!.AsObject();

    private static JsonObject Operation(JsonNode specification, string pathKey) =>
        Paths(specification)[pathKey]!["get"]!.AsObject();

    private static JsonArray Parameters(JsonNode specification, string pathKey) =>
        Operation(specification, pathKey)["parameters"]?.AsArray() ?? [];

    private static string[] ParameterReferences(JsonNode specification, string pathKey) =>
        [
            .. Parameters(specification, pathKey)
                .Select(parameter => parameter?["$ref"]?.GetValue<string>())
                .Where(reference => reference is not null)
                .Select(reference => reference!),
        ];

    private static JsonObject CoreCollectionGetFragment(ApiSchemaDocumentNodes apiSchemaNodes) =>
        CoreFragmentPaths(apiSchemaNodes)[CoreCollectionPath]!["get"]!.AsObject();

    private static JsonObject CoreFragmentPaths(ApiSchemaDocumentNodes apiSchemaNodes) =>
        apiSchemaNodes.CoreApiSchemaRootNode["projectSchema"]!["resourceSchemas"]!["academicWeeks"]![
            "openApiFragments"
        ]!["resources"]!["paths"]!.AsObject();

    private static JsonObject ResourcesBaseDocument(ApiSchemaDocumentNodes apiSchemaNodes) =>
        apiSchemaNodes.CoreApiSchemaRootNode["projectSchema"]!["openApiBaseDocuments"]![
            "resources"
        ]!.AsObject();

    [TestFixture]
    [Parallelizable]
    public class Given_A_Core_Schema_With_Cursor_Parameter_Components : CursorPagingOpenApiAugmentationTests
    {
        private JsonNode _resources = new JsonObject();
        private JsonNode _descriptors = new JsonObject();

        [SetUp]
        public void Setup()
        {
            ApiSchemaDocumentNodes apiSchemaNodes = ApiSchemaNodes();
            _resources = Assemble(apiSchemaNodes);
            _descriptors = Assemble(apiSchemaNodes, OpenApiDocument.OpenApiDocumentType.Descriptor);
        }

        private static string[] InlineParameterNames(JsonNode specification, string pathKey) =>
            [
                .. Parameters(specification, pathKey)
                    .Where(parameter => parameter?["$ref"] is null)
                    .Select(parameter => parameter?["name"]?.GetValue<string>())
                    .Where(name => name is not null)
                    .Select(name => name!),
            ];

        [Test]
        public void It_should_append_the_cursor_parameter_references_last()
        {
            string[] references = ParameterReferences(_resources, CoreCollectionPath);
            references[^2..].Should().Equal(PageTokenReference, PageSizeReference);
        }

        [Test]
        public void It_should_not_disturb_the_existing_collection_parameter_references()
        {
            ParameterReferences(_resources, CoreCollectionPath)[..^2]
                .Should()
                .Equal(
                    "#/components/parameters/offset",
                    "#/components/parameters/limit",
                    "#/components/parameters/MinChangeVersion",
                    "#/components/parameters/MaxChangeVersion",
                    "#/components/parameters/totalCount",
                    "#/components/parameters/queryExpression",
                    "#/components/parameters/fields"
                );
        }

        [Test]
        public void It_should_document_the_next_page_token_response_header()
        {
            JsonNode header = Operation(_resources, CoreCollectionPath)["responses"]!["200"]!["headers"]![
                NextPageTokenHeader
            ]!;

            header["schema"]!["type"]!.GetValue<string>().Should().Be("string");
            header["description"]!.GetValue<string>().Should().Be(ExpectedNextPageTokenDescription);
        }

        [Test]
        public void It_should_generate_the_sibling_partition_path()
        {
            Paths(_resources).Should().ContainKey(CorePartitionPath);
        }

        [Test]
        public void It_should_append_partitions_to_the_collection_operation_id()
        {
            Operation(_resources, CorePartitionPath)["operationId"]!
                .GetValue<string>()
                .Should()
                .Be(CoreOperationId + "Partitions");
        }

        [Test]
        public void It_should_preserve_the_extension_project_operation_id_prefix()
        {
            Operation(_resources, ExtensionPartitionPath)["operationId"]!
                .GetValue<string>()
                .Should()
                .Be(ExtensionOperationId + "Partitions");
        }

        [Test]
        public void It_should_generate_the_descriptor_partition_operation()
        {
            Operation(_descriptors, DescriptorPartitionPath)["operationId"]!
                .GetValue<string>()
                .Should()
                .Be(DescriptorOperationId + "Partitions");
        }

        [Test]
        public void It_should_give_the_partition_operation_its_own_summary()
        {
            JsonObject partitionOperation = Operation(_resources, CorePartitionPath);
            string summary = partitionOperation["summary"]!.GetValue<string>();

            summary.Should().Be(ExpectedPartitionSummary);
            summary.Should().NotBe(Operation(_resources, CoreCollectionPath)["summary"]!.GetValue<string>());
        }

        [Test]
        public void It_should_give_the_partition_operation_its_own_description()
        {
            JsonObject partitionOperation = Operation(_resources, CorePartitionPath);
            string description = partitionOperation["description"]!.GetValue<string>();

            description.Should().Be(ExpectedPartitionDescription);
            description
                .Should()
                .NotBe(Operation(_resources, CoreCollectionPath)["description"]!.GetValue<string>());
        }

        [Test]
        public void It_should_return_the_shared_page_token_schema_as_application_json()
        {
            JsonNode response = Operation(_resources, CorePartitionPath)["responses"]!["200"]!;

            response["content"]!["application/json"]!["schema"]!["$ref"]!
                .GetValue<string>()
                .Should()
                .Be(PartitionTokensReference);
            response["description"]!.GetValue<string>().Should().Be(ExpectedPartitionResponseDescription);
        }

        [Test]
        public void It_should_declare_the_shared_page_token_schema()
        {
            JsonNode schema = _resources["components"]!["schemas"]!["partitionTokens"]!;

            schema["type"]!.GetValue<string>().Should().Be("object");
            schema["properties"]!["pageTokens"]!["type"]!.GetValue<string>().Should().Be("array");
            schema["properties"]!["pageTokens"]!["items"]!["type"]!.GetValue<string>().Should().Be("string");
        }

        /// <summary>
        /// The handler emits this member on every 200, so publishing it as optional would tell a client
        /// generator to model a value that is never absent as nullable.
        /// </summary>
        [Test]
        public void It_should_publish_the_page_tokens_member_as_required()
        {
            RequiredMembers(_resources).Should().Equal("pageTokens");
        }

        [Test]
        public void It_should_publish_the_page_tokens_member_as_required_for_descriptors()
        {
            RequiredMembers(_descriptors).Should().Equal("pageTokens");
        }

        private static string[] RequiredMembers(JsonNode specification) =>
            [
                .. specification["components"]!["schemas"]!["partitionTokens"]!["required"]!
                    .AsArray()
                    .Select(member => member!.GetValue<string>()),
            ];

        [Test]
        public void It_should_reference_the_partition_count_parameter_first()
        {
            Parameters(_resources, CorePartitionPath)[0]!["$ref"]!
                .GetValue<string>()
                .Should()
                .Be(NumberOfPartitionsReference);
        }

        [Test]
        public void It_should_copy_only_the_change_version_parameter_references()
        {
            ParameterReferences(_resources, CorePartitionPath)
                .Should()
                .Equal(
                    NumberOfPartitionsReference,
                    "#/components/parameters/MinChangeVersion",
                    "#/components/parameters/MaxChangeVersion"
                );
        }

        [Test]
        public void It_should_copy_resource_filters_but_not_the_partition_count_name_or_headers()
        {
            InlineParameterNames(_resources, CorePartitionPath).Should().Equal(CopiedFilterName);
        }

        [Test]
        public void It_should_copy_the_collection_security()
        {
            JsonNode
                .DeepEquals(
                    Operation(_resources, CorePartitionPath)["security"],
                    Operation(_resources, CoreCollectionPath)["security"]
                )
                .Should()
                .BeTrue();
        }

        [Test]
        public void It_should_copy_the_collection_tags()
        {
            JsonNode
                .DeepEquals(
                    Operation(_resources, CorePartitionPath)["tags"],
                    Operation(_resources, CoreCollectionPath)["tags"]
                )
                .Should()
                .BeTrue();
        }

        [Test]
        public void It_should_copy_the_path_level_domain_metadata()
        {
            JsonNode
                .DeepEquals(
                    Paths(_resources)[ExcludedDomainPartitionPath]!["x-Ed-Fi-domains"],
                    Paths(_resources)[ExcludedDomainCollectionPath]!["x-Ed-Fi-domains"]
                )
                .Should()
                .BeTrue();
        }

        [Test]
        public void It_should_not_alias_the_collection_operation_nodes()
        {
            Operation(_resources, CorePartitionPath)["tags"]!.AsArray().Add("mutated");

            Operation(_resources, CoreCollectionPath)["tags"]!.AsArray().Count.Should().Be(1);
        }

        [Test]
        public void It_should_publish_the_runtime_maximum_page_size_on_limit()
        {
            JsonNode schema = _resources["components"]!["parameters"]!["limit"]!["schema"]!;

            schema["default"]!.GetValue<int>().Should().Be(500);
            schema["maximum"]!.GetValue<int>().Should().Be(500);
            schema["minimum"]!.GetValue<int>().Should().Be(0);
            schema["format"]!.GetValue<string>().Should().Be("int32");
        }

        [Test]
        public void It_should_publish_the_runtime_maximum_page_size_on_page_size()
        {
            JsonNode schema = _resources["components"]!["parameters"]!["pageSize"]!["schema"]!;

            schema["default"]!.GetValue<int>().Should().Be(500);
            schema["maximum"]!.GetValue<int>().Should().Be(500);
        }

        [Test]
        public void It_should_publish_the_runtime_partition_count_default()
        {
            JsonNode schema = _resources["components"]!["parameters"]!["numberOfPartitions"]!["schema"]!;

            schema["default"]!.GetValue<int>().Should().Be(10);
            schema["minimum"]!.GetValue<int>().Should().Be(1);
            schema["maximum"]!.GetValue<int>().Should().Be(200);
        }

        [Test]
        public void It_should_describe_omission_as_the_configured_default()
        {
            _resources["components"]!["parameters"]!["numberOfPartitions"]!["description"]!
                .GetValue<string>()
                .Should()
                .Be(ExpectedNumberOfPartitionsDescription);
        }

        [Test]
        public void It_should_describe_omission_as_the_configured_default_for_descriptors()
        {
            _descriptors["components"]!["parameters"]!["numberOfPartitions"]!["description"]!
                .GetValue<string>()
                .Should()
                .Be(ExpectedNumberOfPartitionsDescription);
        }

        [Test]
        public void It_should_not_publish_the_accessible_item_count_promise()
        {
            _resources.ToJsonString().Should().NotContain(StalePartitionCountClause);
            _descriptors.ToJsonString().Should().NotContain(StalePartitionCountClause);
        }

        [Test]
        public void It_should_publish_the_same_values_in_the_descriptor_document()
        {
            _descriptors["components"]!["parameters"]!["limit"]!["schema"]!["maximum"]!
                .GetValue<int>()
                .Should()
                .Be(500);
        }

        [Test]
        public void It_should_resolve_extension_partition_parameters_against_the_core_components()
        {
            ParameterReferences(_resources, ExtensionPartitionPath)
                .Should()
                .Contain(NumberOfPartitionsReference);
            _resources["components"]!["parameters"]!.AsObject().Should().ContainKey("numberOfPartitions");
        }

        [Test]
        public void It_should_keep_descriptor_partitions_out_of_the_resource_document()
        {
            Paths(_resources).Should().NotContainKey(DescriptorPartitionPath);
        }

        [Test]
        public void It_should_keep_resource_partitions_out_of_the_descriptor_document()
        {
            Paths(_descriptors).Should().NotContainKey(CorePartitionPath);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Paths_That_Must_Not_Be_Augmented : CursorPagingOpenApiAugmentationTests
    {
        private JsonNode _resources = new JsonObject();

        [SetUp]
        public void Setup()
        {
            _resources = Assemble(ApiSchemaNodes());
        }

        [TestCase(CoreItemPath)]
        [TestCase(CoreDeletesPath)]
        [TestCase(CoreKeyChangesPath)]
        [TestCase(CoreCompositePath)]
        [TestCase(ManagementPath)]
        [TestCase(MetadataPath)]
        [TestCase(ResourceExtensionCollectionPath)]
        public void It_should_not_add_cursor_parameters(string pathKey)
        {
            ParameterReferences(_resources, pathKey)
                .Should()
                .NotContain(reference => reference == PageTokenReference || reference == PageSizeReference);
        }

        [TestCase(CoreItemPath)]
        [TestCase(CoreDeletesPath)]
        [TestCase(CoreKeyChangesPath)]
        [TestCase(CoreCompositePath)]
        [TestCase(ManagementPath)]
        [TestCase(MetadataPath)]
        [TestCase(ResourceExtensionCollectionPath)]
        public void It_should_not_add_a_response_header(string pathKey)
        {
            Operation(_resources, pathKey)["responses"]?["200"]?["headers"].Should().BeNull();
        }

        [TestCase(CoreItemPath)]
        [TestCase(CoreDeletesPath)]
        [TestCase(CoreKeyChangesPath)]
        [TestCase(CoreCompositePath)]
        [TestCase(ManagementPath)]
        [TestCase(MetadataPath)]
        [TestCase(ResourceExtensionCollectionPath)]
        public void It_should_not_generate_a_partition_sibling(string pathKey)
        {
            Paths(_resources).Should().NotContainKey(pathKey + "/partitions");
        }

        [Test]
        public void It_should_still_include_the_paths_themselves()
        {
            Paths(_resources)
                .Should()
                .ContainKeys(
                    CoreItemPath,
                    CoreDeletesPath,
                    CoreKeyChangesPath,
                    CoreCompositePath,
                    ManagementPath,
                    MetadataPath,
                    ResourceExtensionCollectionPath
                );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_An_Excluded_Domain : CursorPagingOpenApiAugmentationTests
    {
        private JsonNode _resources = new JsonObject();

        [SetUp]
        public void Setup()
        {
            _resources = Assemble(ApiSchemaNodes(), excludedDomains: [ExcludedDomainName]);
        }

        [Test]
        public void It_should_remove_the_collection_path()
        {
            Paths(_resources).Should().NotContainKey(ExcludedDomainCollectionPath);
        }

        [Test]
        public void It_should_remove_the_generated_partition_path()
        {
            Paths(_resources).Should().NotContainKey(ExcludedDomainPartitionPath);
        }

        [Test]
        public void It_should_keep_the_partition_path_of_an_included_domain()
        {
            Paths(_resources).Should().ContainKey(CorePartitionPath);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Custom_Paging_Settings : CursorPagingOpenApiAugmentationTests
    {
        private JsonNode _resources = new JsonObject();

        [SetUp]
        public void Setup()
        {
            _resources = Assemble(ApiSchemaNodes(), pagingSettings: new OpenApiPagingSettings(250, 7));
        }

        [Test]
        public void It_should_publish_the_configured_maximum_page_size()
        {
            JsonNode parameters = _resources["components"]!["parameters"]!;

            parameters["limit"]!["schema"]!["default"]!.GetValue<int>().Should().Be(250);
            parameters["limit"]!["schema"]!["maximum"]!.GetValue<int>().Should().Be(250);
            parameters["pageSize"]!["schema"]!["default"]!.GetValue<int>().Should().Be(250);
            parameters["pageSize"]!["schema"]!["maximum"]!.GetValue<int>().Should().Be(250);
        }

        [Test]
        public void It_should_publish_the_configured_partition_count()
        {
            _resources["components"]!["parameters"]!["numberOfPartitions"]!["schema"]!["default"]!
                .GetValue<int>()
                .Should()
                .Be(7);
        }

        [Test]
        public void It_should_still_describe_omission_as_the_configured_default()
        {
            _resources["components"]!["parameters"]!["numberOfPartitions"]!["description"]!
                .GetValue<string>()
                .Should()
                .Be(ExpectedNumberOfPartitionsDescription);
            _resources.ToJsonString().Should().NotContain(StalePartitionCountClause);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Exactly_Already_Augmented_Input : CursorPagingOpenApiAugmentationTests
    {
        private JsonNode _resources = new JsonObject();

        [SetUp]
        public void Setup()
        {
            ApiSchemaDocumentNodes apiSchemaNodes = ApiSchemaNodes();
            JsonObject collectionGet = CoreCollectionGetFragment(apiSchemaNodes);

            collectionGet["parameters"]!.AsArray().Add(ParameterReference("pageToken"));
            collectionGet["parameters"]!.AsArray().Add(ParameterReference("pageSize"));
            collectionGet["responses"]!["200"]!.AsObject()["headers"] = new JsonObject
            {
                [NextPageTokenHeader] = new JsonObject
                {
                    ["description"] = ExpectedNextPageTokenDescription,
                    ["schema"] = new JsonObject { ["type"] = "string" },
                },
            };

            _resources = Assemble(apiSchemaNodes);
        }

        [Test]
        public void It_should_not_duplicate_the_cursor_parameter_references()
        {
            string[] references = ParameterReferences(_resources, CoreCollectionPath);

            references.Count(reference => reference == PageTokenReference).Should().Be(1);
            references.Count(reference => reference == PageSizeReference).Should().Be(1);
        }

        [Test]
        public void It_should_keep_the_matching_response_header()
        {
            Operation(_resources, CoreCollectionPath)["responses"]!["200"]!["headers"]![NextPageTokenHeader]![
                "description"
            ]!
                .GetValue<string>()
                .Should()
                .Be(ExpectedNextPageTokenDescription);
        }

        [Test]
        public void It_should_still_generate_the_partition_path()
        {
            Paths(_resources).Should().ContainKey(CorePartitionPath);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Conflicting_Existing_Metadata : CursorPagingOpenApiAugmentationTests
    {
        [Test]
        public void It_should_refuse_a_conflicting_cursor_parameter()
        {
            ApiSchemaDocumentNodes apiSchemaNodes = ApiSchemaNodes();
            CoreCollectionGetFragment(apiSchemaNodes)["parameters"]!.AsArray().Add(InlineFilter("pageToken"));

            Action assemble = () => Assemble(apiSchemaNodes);

            assemble
                .Should()
                .Throw<InvalidOperationException>()
                .WithMessage("*already declares a 'pageToken' parameter*");
        }

        [Test]
        public void It_should_refuse_a_conflicting_response_header()
        {
            ApiSchemaDocumentNodes apiSchemaNodes = ApiSchemaNodes();
            CoreCollectionGetFragment(apiSchemaNodes)["responses"]!["200"]!.AsObject()["headers"] =
                new JsonObject
                {
                    [NextPageTokenHeader] = new JsonObject
                    {
                        ["schema"] = new JsonObject { ["type"] = "integer" },
                    },
                };

            Action assemble = () => Assemble(apiSchemaNodes);

            assemble
                .Should()
                .Throw<InvalidOperationException>()
                .WithMessage("*already declares a 'Next-Page-Token' response header*");
        }

        [Test]
        public void It_should_refuse_an_existing_partition_path()
        {
            ApiSchemaDocumentNodes apiSchemaNodes = ApiSchemaNodes();
            CoreFragmentPaths(apiSchemaNodes)[CorePartitionPath] = new JsonObject
            {
                ["get"] = new JsonObject
                {
                    ["description"] = "hand authored partitions",
                    ["operationId"] = "getAcademicWeeksPartitions",
                },
            };

            Action assemble = () => Assemble(apiSchemaNodes);

            assemble
                .Should()
                .Throw<InvalidOperationException>()
                .WithMessage("*is already present in the OpenAPI specification*");
        }

        [Test]
        public void It_should_refuse_a_conflicting_page_token_schema()
        {
            ApiSchemaDocumentNodes apiSchemaNodes = ApiSchemaNodes();
            ResourcesBaseDocument(apiSchemaNodes)["components"]!["schemas"]!.AsObject()["partitionTokens"] =
                new JsonObject { ["type"] = "string" };

            Action assemble = () => Assemble(apiSchemaNodes);

            assemble
                .Should()
                .Throw<InvalidOperationException>()
                .WithMessage("*Schema 'partitionTokens' is already present*");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Missing_Required_Document_Nodes : CursorPagingOpenApiAugmentationTests
    {
        [TestCase("limit")]
        [TestCase("pageToken")]
        [TestCase("pageSize")]
        [TestCase("numberOfPartitions")]
        public void It_should_refuse_a_missing_parameter_component(string componentName)
        {
            ApiSchemaDocumentNodes apiSchemaNodes = ApiSchemaNodes();
            ResourcesBaseDocument(apiSchemaNodes)["components"]!["parameters"]!
                .AsObject()
                .Remove(componentName);

            Action assemble = () => Assemble(apiSchemaNodes);

            assemble
                .Should()
                .Throw<InvalidOperationException>()
                .WithMessage($"*$.components.parameters.{componentName}' not found*");
        }

        /// <summary>
        /// The CLI generator builds this same object with no ApiSchema validation in front of it, so
        /// assembly has to reject a parameter contract the request pipeline would not honor rather than
        /// publish it.
        /// </summary>
        [TestCase("limit", "limit")]
        [TestCase("pageToken", "pageToken")]
        [TestCase("pageSize", "pageSize")]
        [TestCase("numberOfPartitions", "number")]
        public void It_should_refuse_a_parameter_component_with_the_wrong_query_name(
            string componentName,
            string publishedName
        )
        {
            ApiSchemaDocumentNodes apiSchemaNodes = ApiSchemaNodes();
            ResourcesBaseDocument(apiSchemaNodes)["components"]!["parameters"]![componentName]!["name"] =
                publishedName + "Renamed";

            Action assemble = () => Assemble(apiSchemaNodes);

            assemble
                .Should()
                .Throw<InvalidOperationException>()
                .WithMessage($"*publishes the query name*but the request pipeline reads '{publishedName}'*");
        }

        /// <summary>
        /// The partition count component is keyed numberOfPartitions and published as number, so naming
        /// it after its own key is the plausible authoring mistake.
        /// </summary>
        [Test]
        public void It_should_refuse_the_partition_count_named_after_its_component_key()
        {
            ApiSchemaDocumentNodes apiSchemaNodes = ApiSchemaNodes();
            ResourcesBaseDocument(apiSchemaNodes)["components"]!["parameters"]!["numberOfPartitions"]![
                "name"
            ] = "numberOfPartitions";

            Action assemble = () => Assemble(apiSchemaNodes);

            assemble
                .Should()
                .Throw<InvalidOperationException>()
                .WithMessage("*but the request pipeline reads 'number'*");
        }

        [TestCase("limit")]
        [TestCase("pageToken")]
        [TestCase("pageSize")]
        [TestCase("numberOfPartitions")]
        public void It_should_refuse_a_parameter_component_without_a_location(string componentName)
        {
            ApiSchemaDocumentNodes apiSchemaNodes = ApiSchemaNodes();
            ResourcesBaseDocument(apiSchemaNodes)["components"]!["parameters"]![componentName]!
                .AsObject()
                .Remove("in");

            Action assemble = () => Assemble(apiSchemaNodes);

            assemble
                .Should()
                .Throw<InvalidOperationException>()
                .WithMessage($"*$.components.parameters.{componentName}' is carried in*");
        }

        [Test]
        public void It_should_refuse_a_parameter_component_carried_outside_the_query()
        {
            ApiSchemaDocumentNodes apiSchemaNodes = ApiSchemaNodes();
            ResourcesBaseDocument(apiSchemaNodes)["components"]!["parameters"]!["pageToken"]!["in"] =
                "header";

            Action assemble = () => Assemble(apiSchemaNodes);

            assemble
                .Should()
                .Throw<InvalidOperationException>()
                .WithMessage("*is carried in 'header'*reads it from the 'query' location*");
        }

        [Test]
        public void It_should_refuse_a_parameter_component_without_a_schema()
        {
            ApiSchemaDocumentNodes apiSchemaNodes = ApiSchemaNodes();
            ResourcesBaseDocument(apiSchemaNodes)["components"]!["parameters"]!["pageSize"]!
                .AsObject()
                .Remove("schema");

            Action assemble = () => Assemble(apiSchemaNodes);

            assemble
                .Should()
                .Throw<InvalidOperationException>()
                .WithMessage("*$.components.parameters.pageSize.schema' not found*");
        }

        [Test]
        public void It_should_refuse_missing_parameter_components()
        {
            ApiSchemaDocumentNodes apiSchemaNodes = ApiSchemaNodes();
            ResourcesBaseDocument(apiSchemaNodes)["components"]!.AsObject().Remove("parameters");

            Action assemble = () => Assemble(apiSchemaNodes);

            assemble
                .Should()
                .Throw<InvalidOperationException>()
                .WithMessage("*$.components.parameters' not found*");
        }

        [Test]
        public void It_should_refuse_missing_component_schemas()
        {
            // A project with no resource fragments is the only way a base document reaches assembly with
            // its schemas container still absent: merging any fragment establishes it first.
            ApiSchemaDocumentNodes apiSchemaNodes = ResourcelessApiSchemaNodes(resourcesDocument =>
                resourcesDocument["components"]!.AsObject().Remove("schemas")
            );

            Action assemble = () => Assemble(apiSchemaNodes);

            assemble
                .Should()
                .Throw<InvalidOperationException>()
                .WithMessage("*$.components.schemas' not found*");
        }

        [Test]
        public void It_should_refuse_missing_components()
        {
            ApiSchemaDocumentNodes apiSchemaNodes = ResourcelessApiSchemaNodes(resourcesDocument =>
                resourcesDocument.Remove("components")
            );

            Action assemble = () => Assemble(apiSchemaNodes);

            assemble.Should().Throw<InvalidOperationException>().WithMessage("*$.components' not found*");
        }

        private static ApiSchemaDocumentNodes ResourcelessApiSchemaNodes(
            Action<JsonObject> mutateResourcesDocument
        )
        {
            ApiSchemaDocumentNodes apiSchemaNodes = new ApiSchemaBuilder()
                .WithStartProject("ed-fi", "5.0.0")
                .WithOpenApiBaseDocuments()
                .WithEndProject()
                .AsApiSchemaNodes();

            mutateResourcesDocument(ResourcesBaseDocument(apiSchemaNodes));
            return apiSchemaNodes;
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Malformed_Eligible_Collection_Get : CursorPagingOpenApiAugmentationTests
    {
        [Test]
        public void It_should_refuse_a_missing_operation_id()
        {
            ApiSchemaDocumentNodes apiSchemaNodes = ApiSchemaNodes();
            CoreCollectionGetFragment(apiSchemaNodes).Remove("operationId");

            Action assemble = () => Assemble(apiSchemaNodes);

            assemble.Should().Throw<InvalidOperationException>().WithMessage("*has no operationId*");
        }

        [Test]
        public void It_should_refuse_a_blank_operation_id()
        {
            ApiSchemaDocumentNodes apiSchemaNodes = ApiSchemaNodes();
            CoreCollectionGetFragment(apiSchemaNodes)["operationId"] = "   ";

            Action assemble = () => Assemble(apiSchemaNodes);

            assemble.Should().Throw<InvalidOperationException>().WithMessage("*has no operationId*");
        }

        [Test]
        public void It_should_refuse_a_non_string_operation_id()
        {
            ApiSchemaDocumentNodes apiSchemaNodes = ApiSchemaNodes();
            CoreCollectionGetFragment(apiSchemaNodes)["operationId"] = 7;

            Action assemble = () => Assemble(apiSchemaNodes);

            assemble.Should().Throw<InvalidOperationException>().WithMessage("*has no operationId*");
        }

        [Test]
        public void It_should_refuse_a_missing_success_response()
        {
            ApiSchemaDocumentNodes apiSchemaNodes = ApiSchemaNodes();
            CoreCollectionGetFragment(apiSchemaNodes)["responses"]!.AsObject().Remove("200");

            Action assemble = () => Assemble(apiSchemaNodes);

            assemble
                .Should()
                .Throw<InvalidOperationException>()
                .WithMessage("*.get.responses.200' not found*");
        }

        [Test]
        public void It_should_refuse_a_success_response_that_is_not_an_object()
        {
            ApiSchemaDocumentNodes apiSchemaNodes = ApiSchemaNodes();
            CoreCollectionGetFragment(apiSchemaNodes)["responses"]!.AsObject()["200"] = "OK";

            Action assemble = () => Assemble(apiSchemaNodes);

            assemble
                .Should()
                .Throw<InvalidOperationException>()
                .WithMessage("*.get.responses.200' is not a JSON object*");
        }

        [Test]
        public void It_should_refuse_parameters_that_are_not_an_array()
        {
            ApiSchemaDocumentNodes apiSchemaNodes = ApiSchemaNodes();
            CoreCollectionGetFragment(apiSchemaNodes)["parameters"] = "none";

            Action assemble = () => Assemble(apiSchemaNodes);

            assemble
                .Should()
                .Throw<InvalidOperationException>()
                .WithMessage("*.get.parameters' is not a JSON array*");
        }

        [Test]
        public void It_should_refuse_an_unresolvable_parameter_reference()
        {
            ApiSchemaDocumentNodes apiSchemaNodes = ApiSchemaNodes();
            CoreCollectionGetFragment(apiSchemaNodes)["parameters"]!
                .AsArray()
                .Add(ParameterReference("notAComponent"));

            Action assemble = () => Assemble(apiSchemaNodes);

            assemble
                .Should()
                .Throw<InvalidOperationException>()
                .WithMessage("*$.components.parameters.notAComponent' not found*");
        }

        [Test]
        public void It_should_refuse_a_reference_that_is_not_a_parameter_component()
        {
            ApiSchemaDocumentNodes apiSchemaNodes = ApiSchemaNodes();
            CoreCollectionGetFragment(apiSchemaNodes)["parameters"]!
                .AsArray()
                .Add(new JsonObject { ["$ref"] = "#/components/schemas/EdFi_AcademicWeek" });

            Action assemble = () => Assemble(apiSchemaNodes);

            assemble
                .Should()
                .Throw<InvalidOperationException>()
                .WithMessage("*is not a parameter component reference*");
        }

        [Test]
        public void It_should_refuse_an_inline_parameter_without_a_name()
        {
            ApiSchemaDocumentNodes apiSchemaNodes = ApiSchemaNodes();
            CoreCollectionGetFragment(apiSchemaNodes)["parameters"]!
                .AsArray()
                .Add(new JsonObject { ["in"] = "query" });

            Action assemble = () => Assemble(apiSchemaNodes);

            assemble.Should().Throw<InvalidOperationException>().WithMessage("*has no name*");
        }

        [Test]
        public void It_should_refuse_a_collection_path_without_a_get_operation()
        {
            ApiSchemaDocumentNodes apiSchemaNodes = ApiSchemaNodes();
            CoreFragmentPaths(apiSchemaNodes)[CoreCollectionPath]!.AsObject().Remove("get");

            Action assemble = () => Assemble(apiSchemaNodes);

            assemble
                .Should()
                .Throw<InvalidOperationException>()
                .WithMessage($"*{CoreCollectionPath}'].get' not found*");
        }

        [Test]
        public void It_should_not_partially_augment_a_malformed_collection()
        {
            ApiSchemaDocumentNodes apiSchemaNodes = ApiSchemaNodes();
            CoreCollectionGetFragment(apiSchemaNodes).Remove("operationId");

            Action assemble = () => Assemble(apiSchemaNodes);

            assemble.Should().Throw<InvalidOperationException>();
            ParameterReferences(
                    new JsonObject { ["paths"] = CoreFragmentPaths(apiSchemaNodes).DeepClone() },
                    CoreCollectionPath
                )
                .Should()
                .NotContain(reference => reference == PageTokenReference);
        }
    }
}
