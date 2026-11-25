// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.ApiSchema.Model;
using EdFi.DataManagementService.Core.Startup;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.ApiSchema;

/// <summary>
/// Query fields carry the maxLength that the insert schema declares at each document path, so a filter
/// is bounded by the same declared maxLength as the request-body property.
/// </summary>
[TestFixture]
public class ResourceSchemaQueryFieldTests
{
    private static JsonPathAndType PathOf(ResourceSchema resourceSchema, string queryFieldName) =>
        resourceSchema
            .QueryFields.Single(field => field.QueryFieldName == queryFieldName)
            .DocumentPathsWithType[0];

    [TestFixture]
    [Parallelizable]
    public class Given_Query_Fields_Over_A_Schema_With_String_Bounds : ResourceSchemaQueryFieldTests
    {
        private ResourceSchema _resourceSchema = null!;

        [SetUp]
        public void Setup()
        {
            _resourceSchema = new ResourceSchema(
                JsonNode.Parse(
                    """
                    {
                      "queryFieldMapping": {
                        "studentUniqueId": [{ "path": "$.studentUniqueId", "type": "string" }],
                        "schoolId": [{ "path": "$.schoolReference.schoolId", "type": "number" }],
                        "localCourseCode": [{ "path": "$.courseOfferingReference.localCourseCode", "type": "string" }],
                        "gradeLevelDescriptor": [{ "path": "$.gradeLevels[*].gradeLevelDescriptor", "type": "string" }],
                        "extensionCode": [{ "path": "$._ext.sample.extensionCode", "type": "string" }],
                        "duration": [{ "path": "$.duration", "type": "string" }],
                        "id": [{ "path": "$.id", "type": "string" }]
                      },
                      "jsonSchemaForInsert": {
                        "type": "object",
                        "properties": {
                          "studentUniqueId": { "type": "string", "maxLength": 32 },
                          "schoolReference": {
                            "type": "object",
                            "properties": { "schoolId": { "type": "integer" } }
                          },
                          "courseOfferingReference": {
                            "type": "object",
                            "properties": { "localCourseCode": { "type": "string", "maxLength": 60 } }
                          },
                          "gradeLevels": {
                            "type": "array",
                            "items": {
                              "type": "object",
                              "properties": { "gradeLevelDescriptor": { "type": "string", "maxLength": 306 } }
                            }
                          },
                          "_ext": {
                            "type": "object",
                            "properties": {
                              "sample": {
                                "type": "object",
                                "properties": { "extensionCode": { "type": "string", "maxLength": 20 } }
                              }
                            }
                          },
                          "duration": { "type": "string" }
                        }
                      }
                    }
                    """
                )!
            );
        }

        [TestCase("studentUniqueId", 32)]
        [TestCase("localCourseCode", 60)]
        [TestCase("gradeLevelDescriptor", 306)]
        [TestCase("extensionCode", 20)]
        public void It_reads_the_declared_max_length_at_the_document_path(
            string queryFieldName,
            int maxLength
        )
        {
            PathOf(_resourceSchema, queryFieldName).MaxLength.Should().Be(maxLength);
        }

        [TestCase("duration")]
        [TestCase("id")]
        [TestCase("schoolId")]
        public void It_leaves_a_path_without_a_declared_string_bound_unbounded(string queryFieldName)
        {
            PathOf(_resourceSchema, queryFieldName).MaxLength.Should().BeNull();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Query_Fields_Without_An_Insert_Schema : ResourceSchemaQueryFieldTests
    {
        [Test]
        public void It_leaves_every_path_unbounded()
        {
            var resourceSchema = new ResourceSchema(
                JsonNode.Parse(
                    """
                    { "queryFieldMapping": { "name": [{ "path": "$.name", "type": "string" }] } }
                    """
                )!
            );

            PathOf(resourceSchema, "name").MaxLength.Should().BeNull();
        }
    }

    /// <summary>
    /// The packaged Data Standard 5.2 schema: the identification-code fields follow the mapped fields, each
    /// with one collection item path, and a name the mapping already has keeps its root path.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_The_Packaged_Data_Standard_52_Schema : ResourceSchemaQueryFieldTests
    {
        private const string PackageRootKey = "DataStandard52ApiSchemaPackageRoot";

        private JsonObject _resourceSchemas = null!;

        [OneTimeSetUp]
        public void Setup()
        {
            _resourceSchemas = PackagedApiSchemaContract.LoadPackagedResourceSchemas(PackageRootKey);
        }

        private ResourceSchema ResourceSchemaOf(string endpointName) => new(_resourceSchemas[endpointName]!);

        private static string[] MappedNames(JsonNode resourceSchemaNode) =>
            [
                .. resourceSchemaNode["queryFieldMapping"]!
                    .AsObject()
                    .Select(static queryField => queryField.Key),
            ];

        [Test]
        public void It_appends_the_identification_code_fields_after_the_mapped_fields()
        {
            // The derived fields follow the item property order of the insert schema.
            string[] expectedNames =
            [
                .. MappedNames(_resourceSchemas["schools"]!),
                "educationOrganizationIdentificationSystemDescriptor",
                "identificationCode",
            ];

            ResourceSchemaOf("schools")
                .QueryFields.Select(static field => field.QueryFieldName)
                .Should()
                .Equal(expectedNames);
        }

        [TestCase("identificationCode", "$.identificationCodes[*].identificationCode", 60)]
        [TestCase(
            "educationOrganizationIdentificationSystemDescriptor",
            "$.identificationCodes[*].educationOrganizationIdentificationSystemDescriptor",
            306
        )]
        public void It_paths_an_identification_code_field_through_the_collection_items(
            string queryFieldName,
            string itemPath,
            int maxLength
        )
        {
            ResourceSchemaOf("schools")
                .QueryFields.Single(field => field.QueryFieldName == queryFieldName)
                .DocumentPathsWithType.Should()
                .Equal(new JsonPathAndType(itemPath, "string", maxLength));
        }

        [TestCase("courseCatalogURL", "$.courseCatalogURL")]
        [TestCase("assigningOrganizationIdentificationCode", "$.assigningOrganizationIdentificationCode")]
        public void It_keeps_the_root_path_of_a_course_transcript_name_the_mapping_already_has(
            string queryFieldName,
            string rootPath
        )
        {
            ResourceSchemaOf("courseTranscripts")
                .QueryFields.Where(field => field.QueryFieldName == queryFieldName)
                .Should()
                .ContainSingle()
                .Which.DocumentPathsWithType.Select(static path => path.JsonPathString)
                .Should()
                .Equal(rootPath);
        }

        [Test]
        public void It_offers_only_the_mapped_fields_on_a_resource_without_a_qualifying_collection()
        {
            ResourceSchemaOf("students")
                .QueryFields.Select(static field => field.QueryFieldName)
                .Should()
                .Equal(MappedNames(_resourceSchemas["students"]!));
        }

        /// <summary>
        /// The effective schema hash is computed from the raw schema. Enumerating every resource's query
        /// fields, identification-code fields included, leaves that schema and its hash exactly as they
        /// were.
        /// </summary>
        [Test]
        public void It_leaves_the_raw_schema_and_its_effective_hash_unchanged()
        {
            var rootNode = PackagedApiSchemaContract.LoadPackagedRootNode(PackageRootKey);
            var hashProvider = new EffectiveSchemaHashProvider(
                NullLogger<EffectiveSchemaHashProvider>.Instance
            );
            var jsonBefore = rootNode.ToJsonString();
            var hashBefore = hashProvider.ComputeHash(new ApiSchemaDocumentNodes(rootNode, []));

            var identificationCodeFieldCount = rootNode["projectSchema"]!["resourceSchemas"]!
                .AsObject()
                .Sum(static resource =>
                    new ResourceSchema(resource.Value!)
                        .QueryFields.Select(static field => field.QueryFieldName)
                        .Except(MappedNames(resource.Value!))
                        .Count()
                );

            identificationCodeFieldCount
                .Should()
                .BePositive("the identification-code fields were enumerated");
            rootNode.ToJsonString().Should().Be(jsonBefore);
            hashProvider.ComputeHash(new ApiSchemaDocumentNodes(rootNode, [])).Should().Be(hashBefore);
        }
    }
}
