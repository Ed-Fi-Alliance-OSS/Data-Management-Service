// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.OpenApi;
using FluentAssertions;
using Json.Path;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.OpenApi;

public class OpenApiDocumentTests
{
    internal static JsonNode CoreSchemaRootNode()
    {
        JsonObject descriptorSchemas = new()
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
            ["EdFi_School"] = new JsonObject
            {
                ["description"] = "AccountabilityRating description",
                ["properties"] = new JsonObject(),
                ["type"] = "string",
            },
            ["EdFi_SurveyResponse"] = new JsonObject
            {
                ["description"] = "AccountabilityRating description",
                ["properties"] = new JsonObject(),
                ["type"] = "string",
            },
        };

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
            ["EdFi_School"] = new JsonObject
            {
                ["description"] = "AccountabilityRating description",
                ["properties"] = new JsonObject(),
                ["type"] = "string",
            },
            ["EdFi_SurveyResponse"] = new JsonObject
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

        JsonObject descriptorsPaths = new()
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

        JsonArray descriptorsTags = [];
        tags.Add(new JsonObject { ["name"] = "TagName1", ["description"] = "Description1" });
        tags.Add(new JsonObject { ["name"] = "TagName2", ["description"] = "Description2" });

        return new ApiSchemaBuilder()
            .WithStartProject("ed-fi", "5.0.0")
            .WithCoreOpenApiCoreDescriptors(descriptorSchemas, descriptorsPaths, descriptorsTags)
            .WithCoreOpenApiCoreResources(schemas, paths, tags)
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

        JsonObject descriptorExts = new()
        {
            ["EdFi_AccountabilityRating"] = new JsonObject
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
        };

        JsonObject descriptorNewPaths = new()
        {
            ["/tpdm/credentialsSurvey"] = new JsonObject
            {
                ["get"] = new JsonObject { ["description"] = "credential get" },
                ["post"] = new JsonObject { ["description"] = "credential post" },
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

        JsonObject descriptorNewSchemas = new()
        {
            ["TPDM_CredentialSurvey"] = new JsonObject
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

        JsonArray descriptorNewTags = [];
        newTags.Add(
            new JsonObject { ["name"] = "ExtensionTagName1", ["description"] = "ExtensionDescription1" }
        );
        newTags.Add(
            new JsonObject { ["name"] = "ExtensionTagName2", ["description"] = "ExtensionDescription2" }
        );

        return new ApiSchemaBuilder()
            .WithStartProject("tpdm", "5.0.0")
            .WithOpenApiExtensionResourceFragments(exts, newPaths, newSchemas, newTags)
            .WithOpenApiExtensionDescriptorFragments(
                descriptorExts,
                descriptorNewPaths,
                descriptorNewSchemas,
                descriptorNewTags
            )
            .WithEndProject()
            .AsSingleApiSchemaRootNode();
    }

    internal static JsonNode SecondExtensionSchemaRootNode()
    {
        JsonObject exts = new()
        {
            ["EdFi_School"] = new JsonObject
            {
                ["description"] = "ext AccountabilityRating description",
                ["type"] = "string",
            },
        };

        JsonObject descriptorExts = new()
        {
            ["EdFi_SurveyResponse"] = new JsonObject
            {
                ["description"] = "ext AccountabilityRating description",
                ["type"] = "string",
            },
        };

        JsonObject newPaths = new()
        {
            ["/tpdm/candidates/{id}"] = new JsonObject
            {
                ["get"] = new JsonObject { ["description"] = "candidate id get" },
                ["delete"] = new JsonObject { ["description"] = "candidate delete" },
            },
        };

        JsonObject descriptorNewPaths = new()
        {
            ["/tpdm/candidatesSurvey/{id}"] = new JsonObject
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

        JsonObject descriptorNewSchemas = new()
        {
            ["TPDM_CandidateSurvey"] = new JsonObject
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

        JsonArray descriptorNewTags = [];
        newTags.Add(
            new JsonObject { ["name"] = "ExtensionTagName3", ["description"] = "ExtensionDescription3" }
        );
        newTags.Add(
            new JsonObject { ["name"] = "ExtensionTagName4", ["description"] = "ExtensionDescription4" }
        );

        return new ApiSchemaBuilder()
            .WithStartProject("tpdm", "5.0.0")
            .WithOpenApiExtensionResourceFragments(exts, newPaths, newSchemas, newTags)
            .WithOpenApiExtensionDescriptorFragments(
                descriptorExts,
                descriptorNewPaths,
                descriptorNewSchemas,
                descriptorNewTags
            )
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
            openApiDocumentResult = openApiDocument.CreateDocument(new(coreSchemaRootNode, []));
        }

        [Test]
        public void It_should_be_the_simple_result()
        {
            string expectedResult = """
                [
                  {
                    "apiSchemaVersion": "1.0.0",
                    "projectSchema": {
                      "abstractResources": {},
                      "caseInsensitiveEndpointNameMapping": {},
                      "description": "ed-fi description",
                      "isExtensionProject": false,
                      "projectName": "ed-fi",
                      "projectVersion": "5.0.0",
                      "projectEndpointName": "ed-fi",
                      "resourceNameMapping": {},
                      "resourceSchemas": {},
                      "openApiCoreDescriptors": {
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
                            },
                            "EdFi_School": {
                              "description": "AccountabilityRating description",
                              "properties": {},
                              "type": "string"
                            },
                            "EdFi_SurveyResponse": {
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
                        "tags": []
                      },
                      "openApiCoreResources": {
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
                            },
                            "EdFi_School": {
                              "description": "AccountabilityRating description",
                              "properties": {},
                              "type": "string"
                            },
                            "EdFi_SurveyResponse": {
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
                          },
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
                    }
                  }
                ]
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
                new(coreSchemaRootNode, extensionSchemaRootNodes)
            );
        }

        [Test]
        public void It_should_merge_in_openApiCoreResources_exts()
        {
            string expectedResult = """
                {
                  "_ext": {
                    "description": "ext AcademicWeek description",
                    "type": "string"
                  }
                }
                """;
            AssertResults(
                "$.projectSchema.openApiCoreResources.components.schemas.EdFi_AcademicWeek.properties",
                openApiDocumentResult,
                expectedResult
            );
        }

        [Test]
        public void It_should_merge_in_openApiCoreResources_paths()
        {
            string expectedResult = """
                {
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
                  "/tpdm/credentialsSurvey": {
                    "get": {
                      "description": "credential get"
                    },
                    "post": {
                      "description": "credential post"
                    }
                  },
                  "/tpdm/candidates/{id}": {
                    "get": {
                      "description": "candidate id get"
                    },
                    "delete": {
                      "description": "candidate delete"
                    }
                  },
                  "/tpdm/candidatesSurvey/{id}": {
                    "get": {
                      "description": "candidate id get"
                    },
                    "delete": {
                      "description": "candidate delete"
                    }
                  }
                }
                """;
            AssertResults(
                "$.projectSchema.openApiCoreResources.paths",
                openApiDocumentResult,
                expectedResult
            );
        }

        [Test]
        public void It_should_merge_in_openApiCoreResources_schemas()
        {
            string expectedResult = """
                {
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
                        "description": "ext AcademicWeek description",
                        "type": "string"
                      }
                    },
                    "type": "string"
                  },
                  "EdFi_School": {
                    "description": "AccountabilityRating description",
                    "properties": {
                      "_ext": {
                        "description": "ext AccountabilityRating description",
                        "type": "string"
                      }
                    },
                    "type": "string"
                  },
                  "EdFi_SurveyResponse": {
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
                  "TPDM_CredentialSurvey": {
                    "description": "TPDM credential description",
                    "type": "string"
                  },
                  "TPDM_Candidate": {
                    "description": "TPDM candidate description",
                    "type": "string"
                  },
                  "TPDM_CandidateSurvey": {
                    "description": "TPDM candidate description",
                    "type": "string"
                  }
                }
                """;

            AssertResults(
                "$.projectSchema.openApiCoreResources.components.schemas",
                openApiDocumentResult,
                expectedResult
            );
        }

        [Test]
        public void It_should_merge_in_openApiCoreResources_tags()
        {
            string expectedResult = """
                [
                  {
                    "name": "TagName1",
                    "description": "Description1"
                  },
                  {
                    "name": "TagName2",
                    "description": "Description2"
                  },
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
                """;

            AssertResults("$.projectSchema.openApiCoreResources.tags", openApiDocumentResult, expectedResult);
        }

        [Test]
        public void It_should_merge_in_both_extension_fragments_exts()
        {
            string expectedResult = """
                {
                  "EdFi_School": {
                    "description": "ext AccountabilityRating description",
                    "type": "string"
                  }
                }
                """;

            AssertResults(
                "$.projectSchema.openApiExtensionResourceFragments.exts",
                openApiDocumentResult,
                expectedResult
            );
        }

        [Test]
        public void It_should_merge_in_both_extension_fragments_newPaths()
        {
            string expectedResult = """
                {
                  "/tpdm/candidates/{id}": {
                    "get": {
                      "description": "candidate id get"
                    },
                    "delete": {
                      "description": "candidate delete"
                    }
                  }
                }
                """;

            AssertResults(
                "$.projectSchema.openApiExtensionResourceFragments.newPaths",
                openApiDocumentResult,
                expectedResult
            );
        }

        [Test]
        public void It_should_merge_in_both_extension_fragments_newSchemas()
        {
            string expectedResult = """
                {
                  "TPDM_Candidate": {
                    "description": "TPDM candidate description",
                    "type": "string"
                  }
                }
                """;
            AssertResults(
                "$.projectSchema.openApiExtensionResourceFragments.newSchemas",
                openApiDocumentResult,
                expectedResult
            );
        }

        [Test]
        public void It_should_merge_in_both_extension_fragments_newTags()
        {
            string expectedResult = """
                [
                  {
                    "name": "ExtensionTagName3",
                    "description": "ExtensionDescription3"
                  },
                  {
                    "name": "ExtensionTagName4",
                    "description": "ExtensionDescription4"
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
                """;
            AssertResults(
                "$.projectSchema.openApiExtensionResourceFragments.newTags",
                openApiDocumentResult,
                expectedResult
            );
        }

        public static void AssertResults(
            string jsonPathSource,
            JsonNode openApiDocumentResult,
            string expectedResult
        )
        {
            JsonPath jsonPath = JsonPath.Parse(jsonPathSource);
            JsonNode? openApiExtensionResourceFragments = null;
            string resultString = string.Empty;
            foreach (JsonNode? openApiDocumentResultNode in openApiDocumentResult.AsArray())
            {
                PathResult result = jsonPath.Evaluate(openApiDocumentResultNode);

                if (result.Matches.Count == 1)
                {
                    openApiExtensionResourceFragments = result.Matches[0].Value;
                    resultString = JsonSerializer.Serialize(
                        openApiExtensionResourceFragments,
                        new JsonSerializerOptions { WriteIndented = true }
                    );
                }
            }

            expectedResult = expectedResult.Replace("\r\n", "\n");
            resultString = resultString.Replace("\r\n", "\n");

            resultString.Should().Be(expectedResult);
        }
    }
}
