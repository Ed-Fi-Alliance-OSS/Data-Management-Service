// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.ApiSchema.Model;
using FluentAssertions;
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
}
