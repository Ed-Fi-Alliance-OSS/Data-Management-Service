// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.OpenApi;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.OpenApi;

public class OpenApiDocumentTests
{
    internal static JsonNode CoreSchemaRootNode()
    {
        JsonObject schemas = new()
        {
            ["EdFi_AcademicWeek"] = new JsonObject
            {
                ["description"] = "AcademicWeek description",
                ["properties"] = new JsonObject(),
                ["type"] = "string",
            },
            ["EdFi_AccountabilityRating"] = new JsonObject
            {
                ["description"] = "AccountabilityRating description",
                ["properties"] = new JsonObject(),
                ["type"] = "string",
            },
        };

        JsonObject paths = new()
        {
            ["/ed-fi/academicWeeks"] = new JsonObject
            {
                ["get"] = new JsonObject { ["description"] = "academicWeek get description" },
                ["post"] = new JsonObject { ["description"] = "academicWeek post description" },
            },
            ["/ed-fi/academicWeeks/{id}"] = new JsonObject
            {
                ["get"] = new JsonObject { ["description"] = "academicWeek id get description" },
                ["delete"] = new JsonObject { ["description"] = "academicWeek delete description" },
            },
        };

        JsonArray tags = [];
        tags.Add(new JsonObject { ["name"] = "TagName1", ["description"] = "Description1" });
        tags.Add(new JsonObject { ["name"] = "TagName2", ["description"] = "Description2" });

        return new ApiSchemaBuilder()
            .WithStartProject("ed-fi", "5.0.0")
            .WithCoreOpenApiSpecification(schemas, paths, tags)
            .WithEndProject()
            .AsSingleApiSchemaRootNode();
    }

    internal static JsonNode FirstExtensionSchemaRootNode()
    {
        JsonObject exts = new()
        {
            ["EdFi_AcademicWeek"] = new JsonObject
            {
                ["description"] = "ext AcademicWeek description",
                ["type"] = "string",
            },
        };

        JsonObject newPaths = new()
        {
            ["/tpdm/credentials"] = new JsonObject
            {
                ["get"] = new JsonObject { ["description"] = "credential get" },
                ["post"] = new JsonObject { ["description"] = "credential post" },
            },
            ["/tpdm/credentials/{id}"] = new JsonObject
            {
                ["get"] = new JsonObject { ["description"] = "credential id get" },
                ["delete"] = new JsonObject { ["description"] = "credential delete" },
            },
        };

        JsonObject newSchemas = new()
        {
            ["TPDM_Credential"] = new JsonObject
            {
                ["description"] = "TPDM credential description",
                ["type"] = "string",
            },
        };

        JsonArray newTags = [];
        newTags.Add(
            new JsonObject { ["name"] = "ExtensionTagName1", ["description"] = "ExtensionDescription1" }
        );
        newTags.Add(
            new JsonObject { ["name"] = "ExtensionTagName2", ["description"] = "ExtensionDescription2" }
        );

        return new ApiSchemaBuilder()
            .WithStartProject("tpdm", "5.0.0")
            .WithOpenApiExtensionFragments(exts, newPaths, newSchemas, newTags)
            .WithEndProject()
            .AsSingleApiSchemaRootNode();
    }

    internal static JsonNode SecondExtensionSchemaRootNode()
    {
        JsonObject exts = new()
        {
            ["EdFi_AccountabilityRating"] = new JsonObject
            {
                ["description"] = "ext AccountabilityRating description",
                ["type"] = "string",
            },
        };

        JsonObject newPaths = new()
        {
            ["/tpdm/candidates"] = new JsonObject
            {
                ["get"] = new JsonObject { ["description"] = "candidate get" },
                ["post"] = new JsonObject { ["description"] = "candidate post" },
            },
            ["/tpdm/candidates/{id}"] = new JsonObject
            {
                ["get"] = new JsonObject { ["description"] = "candidate id get" },
                ["delete"] = new JsonObject { ["description"] = "candidate delete" },
            },
        };

        JsonObject newSchemas = new()
        {
            ["TPDM_Candidate"] = new JsonObject
            {
                ["description"] = "TPDM candidate description",
                ["type"] = "string",
            },
        };

        JsonArray newTags = [];
        newTags.Add(
            new JsonObject { ["name"] = "ExtensionTagName3", ["description"] = "ExtensionDescription3" }
        );
        newTags.Add(
            new JsonObject { ["name"] = "ExtensionTagName4", ["description"] = "ExtensionDescription4" }
        );

        return new ApiSchemaBuilder()
            .WithStartProject("tpdm", "5.0.0")
            .WithOpenApiExtensionFragments(exts, newPaths, newSchemas, newTags)
            .WithEndProject()
            .AsSingleApiSchemaRootNode();
    }

    [TestFixture]
    public class Given_A_Simple_Core_Schema_Document : OpenApiDocumentTests
    {
        private JsonNode openApiDocumentResult = new JsonObject();

        [SetUp]
        public void Setup()
        {
            JsonNode coreSchemaRootNode = CoreSchemaRootNode();
            OpenApiDocument openApiDocument = new(NullLogger.Instance);
            openApiDocumentResult = openApiDocument.CreateDocument(coreSchemaRootNode, []);
        }

        [Test]
        public void It_should_be_the_simple_result()
        {
            string expectedResult = """
                {
                  "components": {
                    "schemas": {
                      "EdFi_AcademicWeek": {
                        "description": "AcademicWeek description",
                        "properties": {},
                        "type": "string"
                      },
                      "EdFi_AccountabilityRating": {
                        "description": "AccountabilityRating description",
                        "properties": {},
                        "type": "string"
                      }
                    }
                  },
                  "paths": {
                    "/ed-fi/academicWeeks": {
                      "get": {
                        "description": "academicWeek get description"
                      },
                      "post": {
                        "description": "academicWeek post description"
                      }
                    },
                    "/ed-fi/academicWeeks/{id}": {
                      "get": {
                        "description": "academicWeek id get description"
                      },
                      "delete": {
                        "description": "academicWeek delete description"
                      }
                    }
                  },
                  "tags": [
                    {
                      "name": "TagName1",
                      "description": "Description1"
                    },
                    {
                      "name": "TagName2",
                      "description": "Description2"
                    }
                  ]
                }
                """;

            string result = openApiDocumentResult.ToJsonString(new() { WriteIndented = true });

            expectedResult = expectedResult.Replace("\r\n", "\n");
            result = result.Replace("\r\n", "\n");

            result.Should().Be(expectedResult);
        }
    }

    [TestFixture]
    public class Given_A_Core_Schema_And_Multiple_Extension_Schemas : OpenApiDocumentTests
    {
        private JsonNode openApiDocumentResult = new JsonObject();

        [SetUp]
        public void Setup()
        {
            JsonNode coreSchemaRootNode = CoreSchemaRootNode();
            JsonNode[] extensionSchemaRootNodes =
            [
                FirstExtensionSchemaRootNode(),
                SecondExtensionSchemaRootNode(),
            ];
            OpenApiDocument openApiDocument = new(NullLogger.Instance);
            openApiDocumentResult = openApiDocument.CreateDocument(
                coreSchemaRootNode,
                extensionSchemaRootNodes
            );
        }

        [Test]
        public void It_should_merge_in_both_extension_fragments()
        {
            string expectedResult = """
{
  "components": {
    "schemas": {
      "EdFi_AcademicWeek": {
        "description": "AcademicWeek description",
        "properties": {
          "_ext": {
            "description": "ext AcademicWeek description",
            "type": "string"
          }
        },
        "type": "string"
      },
      "EdFi_AccountabilityRating": {
        "description": "AccountabilityRating description",
        "properties": {
          "_ext": {
            "description": "ext AccountabilityRating description",
            "type": "string"
          }
        },
        "type": "string"
      },
      "TPDM_Credential": {
        "description": "TPDM credential description",
        "type": "string"
      },
      "TPDM_Candidate": {
        "description": "TPDM candidate description",
        "type": "string"
      }
    }
  },
  "paths": {
    "/ed-fi/academicWeeks": {
      "get": {
        "description": "academicWeek get description"
      },
      "post": {
        "description": "academicWeek post description"
      }
    },
    "/ed-fi/academicWeeks/{id}": {
      "get": {
        "description": "academicWeek id get description"
      },
      "delete": {
        "description": "academicWeek delete description"
      }
    },
    "/tpdm/credentials": {
      "get": {
        "description": "credential get"
      },
      "post": {
        "description": "credential post"
      }
    },
    "/tpdm/credentials/{id}": {
      "get": {
        "description": "credential id get"
      },
      "delete": {
        "description": "credential delete"
      }
    },
    "/tpdm/candidates": {
      "get": {
        "description": "candidate get"
      },
      "post": {
        "description": "candidate post"
      }
    },
    "/tpdm/candidates/{id}": {
      "get": {
        "description": "candidate id get"
      },
      "delete": {
        "description": "candidate delete"
      }
    }
  },
  "tags": [
    {
      "name": "TagName1",
      "description": "Description1"
    },
    {
      "name": "TagName2",
      "description": "Description2"
    },
    {
      "name": "ExtensionTagName1",
      "description": "ExtensionDescription1"
    },
    {
      "name": "ExtensionTagName2",
      "description": "ExtensionDescription2"
    },
    {
      "name": "ExtensionTagName3",
      "description": "ExtensionDescription3"
    },
    {
      "name": "ExtensionTagName4",
      "description": "ExtensionDescription4"
    }
  ]
}
""";

            string result = openApiDocumentResult.ToJsonString(new() { WriteIndented = true });

            expectedResult = expectedResult.Replace("\r\n", "\n");
            result = result.Replace("\r\n", "\n");

            result.Should().Be(expectedResult);
        }
    }
}
