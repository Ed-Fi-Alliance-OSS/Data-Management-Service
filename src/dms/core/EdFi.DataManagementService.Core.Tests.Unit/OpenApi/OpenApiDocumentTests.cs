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
            ["EdFi_AbsenceEventCategoryDescriptor"] = new JsonObject
            {
                ["description"] = "An Ed-Fi Descriptor",
                ["properties"] = new JsonObject(),
                ["type"] = "string",
            },
            ["EdFi_AcademicHonorCategoryDescriptor"] = new JsonObject
            {
                ["description"] = "An Ed-Fi Descriptor",
                ["properties"] = new JsonObject(),
                ["type"] = "string",
            },
            ["EdFi_AcademicSubjectDescriptor"] = new JsonObject
            {
                ["description"] = "An Ed-Fi Descriptor",
                ["properties"] = new JsonObject(),
                ["type"] = "string",
            },
            ["EdFi_AccommodationDescriptor"] = new JsonObject
            {
                ["description"] = "An Ed-Fi Descriptor",
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
                ["description"] = "School description",
                ["properties"] = new JsonObject(),
                ["type"] = "string",
            },
            ["EdFi_SurveyResponse"] = new JsonObject
            {
                ["description"] = "SurveyResponse description",
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
            ["/ed-fi/accommodationDescriptors"] = new JsonObject
            {
                ["get"] = new JsonObject { ["description"] = "accommodationDescriptors get description" },
                ["post"] = new JsonObject { ["description"] = "accommodationDescriptors post description" },
            },
            ["/ed-fi/accommodationDescriptors/{id}"] = new JsonObject
            {
                ["get"] = new JsonObject { ["description"] = "accommodationDescriptors id get description" },
                ["delete"] = new JsonObject
                {
                    ["description"] = "accommodationDescriptors delete description",
                },
            },
        };

        JsonArray tags = [];
        tags.Add(new JsonObject { ["name"] = "academicWeeks", ["description"] = "AcademicWeeks Description" });
        tags.Add(new JsonObject { ["name"] = "accountabilityRating", ["description"] = "AccountabilityRatings Description" });

        JsonArray descriptorsTags = [];
        descriptorsTags.Add(new JsonObject { ["name"] = "academicSubjects", ["description"] = "AcademicSubjects Descriptors Description" });
        descriptorsTags.Add(new JsonObject { ["name"] = "accommodations", ["description"] = "Accommodations Descriptors Description" });

        return new ApiSchemaBuilder()
            .WithStartProject("ed-fi", "5.0.0")
            .WithOpenApiCoreDescriptors(descriptorSchemas, descriptorsPaths, descriptorsTags)
            .WithOpenApiCoreResources(schemas, paths, tags)
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

        JsonObject descriptorExts = new() { };

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
            ["/tpdm/credentialDecriptor"] = new JsonObject
            {
                ["get"] = new JsonObject { ["description"] = "credential decriptor get" },
                ["post"] = new JsonObject { ["description"] = "credential decriptor post" },
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
            ["TPDM_CredentialDecriptor"] = new JsonObject
            {
                ["description"] = "TPDM credential decriptor description",
                ["type"] = "string",
            },
        };

        JsonArray newTags = [];
        newTags.Add(
            new JsonObject { ["name"] = "ExtensionTagName1", ["description"] = "First Extension Description1" }
        );
        newTags.Add(
            new JsonObject { ["name"] = "ExtensionTagName2", ["description"] = "First Extension Description2" }
        );

        JsonArray descriptorNewTags = [];
        newTags.Add(
            new JsonObject { ["name"] = "ExtensionTagName1", ["description"] = "First Extension Descriptor Description1" }
        );
        newTags.Add(
            new JsonObject { ["name"] = "ExtensionTagName2", ["description"] = "First Extension Descriptor Description2" }
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
                ["description"] = "ext School description",
                ["type"] = "string",
            },
        };

        JsonObject descriptorExts = new() { };

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
            ["/tpdm/candidateDescriptor/{id}"] = new JsonObject
            {
                ["get"] = new JsonObject { ["description"] = "candidate descriptor id get" },
                ["delete"] = new JsonObject { ["description"] = "candidate descriptor delete" },
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
            ["TPDM_CandidateDescriptor"] = new JsonObject
            {
                ["description"] = "TPDM candidate descriptor description",
                ["type"] = "string",
            },
        };

        JsonArray newTags = [];
        newTags.Add(
            new JsonObject { ["name"] = "ExtensionTagName3", ["description"] = "Second Extension Description3" }
        );
        newTags.Add(
            new JsonObject { ["name"] = "ExtensionTagName4", ["description"] = "Second Extension Description4" }
        );

        JsonArray descriptorNewTags = [];
        newTags.Add(
            new JsonObject { ["name"] = "ExtensionTagName3", ["description"] = "Second Extension Descriptor Description3" }
        );
        newTags.Add(
            new JsonObject { ["name"] = "ExtensionTagName4", ["description"] = "Second Extension Descriptor Description4" }
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
        private JsonNode openApiResourcesResult = new JsonObject();
        private JsonNode openApiDescriptorsResult = new JsonObject();

        [SetUp]
        public void Setup()
        {
            JsonNode coreSchemaRootNode = CoreSchemaRootNode();
            OpenApiDocument openApiDocument = new(NullLogger.Instance);
            openApiResourcesResult = openApiDocument.CreateDocument(
                new(coreSchemaRootNode, []),
                OpenApiDocument.DocumentSection.Resource
            );
            openApiDescriptorsResult = openApiDocument.CreateDocument(
                new(coreSchemaRootNode, []),
                OpenApiDocument.DocumentSection.Descriptor
            );
        }

        [Test]
        public void It_should_be_the_simple_resources_result()
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
                  },
                  "EdFi_School": {
                    "description": "School description",
                    "properties": {},
                    "type": "string"
                  },
                  "EdFi_SurveyResponse": {
                    "description": "SurveyResponse description",
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
                  "name": "academicWeeks",
                  "description": "AcademicWeeks Description"
                },
                {
                  "name": "accountabilityRating",
                  "description": "AccountabilityRatings Description"
                }
              ]
            }
            """;

            string result = openApiResourcesResult.ToJsonString(new() { WriteIndented = true });

            expectedResult = expectedResult.Replace("\r\n", "\n");
            result = result.Replace("\r\n", "\n");

            result.Should().Be(expectedResult);
        }

        [Test]
        public void It_should_be_the_simple_descriptors_result()
        {
            string expectedResult = """
                {
                  "components": {
                    "schemas": {
                      "EdFi_AbsenceEventCategoryDescriptor": {
                        "description": "An Ed-Fi Descriptor",
                        "properties": {},
                        "type": "string"
                      },
                      "EdFi_AcademicHonorCategoryDescriptor": {
                        "description": "An Ed-Fi Descriptor",
                        "properties": {},
                        "type": "string"
                      },
                      "EdFi_AcademicSubjectDescriptor": {
                        "description": "An Ed-Fi Descriptor",
                        "properties": {},
                        "type": "string"
                      },
                      "EdFi_AccommodationDescriptor": {
                        "description": "An Ed-Fi Descriptor",
                        "properties": {},
                        "type": "string"
                      }
                    }
                  },
                  "paths": {
                    "/ed-fi/accommodationDescriptors": {
                      "get": {
                        "description": "accommodationDescriptors get description"
                      },
                      "post": {
                        "description": "accommodationDescriptors post description"
                      }
                    },
                    "/ed-fi/accommodationDescriptors/{id}": {
                      "get": {
                        "description": "accommodationDescriptors id get description"
                      },
                      "delete": {
                        "description": "accommodationDescriptors delete description"
                      }
                    }
                  },
                  "tags": [
                    {
                      "name": "academicSubjects",
                      "description": "AcademicSubjects Descriptors Description"
                    },
                    {
                      "name": "accommodations",
                      "description": "Accommodations Descriptors Description"
                    }
                  ]
                }
                """;

            string result = openApiDescriptorsResult.ToJsonString(new() { WriteIndented = true });

            expectedResult = expectedResult.Replace("\r\n", "\n");
            result = result.Replace("\r\n", "\n");

            result.Should().Be(expectedResult);
        }
    }

    [TestFixture]
    public class Given_A_Core_Schema_And_Multiple_Extension_Schemas : OpenApiDocumentTests
    {
        private JsonNode openApiResourcesResult = new JsonObject();
        private JsonNode openApiDescriptorsResult = new JsonObject();

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
            openApiResourcesResult = openApiDocument.CreateDocument(
                new(coreSchemaRootNode, extensionSchemaRootNodes),
                 OpenApiDocument.DocumentSection.Resource
            );
            openApiDescriptorsResult = openApiDocument.CreateDocument(
              new(coreSchemaRootNode, extensionSchemaRootNodes),
              OpenApiDocument.DocumentSection.Descriptor
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
                "$.paths",
                openApiResourcesResult,
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
                    "properties": {},
                    "type": "string"
                  },
                  "EdFi_School": {
                    "description": "School description",
                    "properties": {
                      "_ext": {
                        "description": "ext School description",
                        "type": "string"
                      }
                    },
                    "type": "string"
                  },
                  "EdFi_SurveyResponse": {
                    "description": "SurveyResponse description",
                    "properties": {},
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
                """;

            AssertResults(
                "$.components.schemas",
                openApiResourcesResult,
                expectedResult
            );
        }

        [Test]
        public void It_should_merge_in_openApiCoreResources_tags()
        {
            string expectedResult = """
             [
               {
                 "name": "academicWeeks",
                 "description": "AcademicWeeks Description"
               },
               {
                 "name": "accountabilityRating",
                 "description": "AccountabilityRatings Description"
               },
               {
                 "name": "ExtensionTagName1",
                 "description": "First Extension Description1"
               },
               {
                 "name": "ExtensionTagName2",
                 "description": "First Extension Description2"
               },
               {
                 "name": "ExtensionTagName1",
                 "description": "First Extension Descriptor Description1"
               },
               {
                 "name": "ExtensionTagName2",
                 "description": "First Extension Descriptor Description2"
               },
               {
                 "name": "ExtensionTagName3",
                 "description": "Second Extension Description3"
               },
               {
                 "name": "ExtensionTagName4",
                 "description": "Second Extension Description4"
               },
               {
                 "name": "ExtensionTagName3",
                 "description": "Second Extension Descriptor Description3"
               },
               {
                 "name": "ExtensionTagName4",
                 "description": "Second Extension Descriptor Description4"
               }
             ]
             """;

            AssertResults("$.tags", openApiResourcesResult, expectedResult);
        }


        [Test]
        public void It_should_merge_in_openApiCoreDescriptor_paths()
        {
            string expectedResult = """
            {
              "/ed-fi/accommodationDescriptors": {
                "get": {
                  "description": "accommodationDescriptors get description"
                },
                "post": {
                  "description": "accommodationDescriptors post description"
                }
              },
              "/ed-fi/accommodationDescriptors/{id}": {
                "get": {
                  "description": "accommodationDescriptors id get description"
                },
                "delete": {
                  "description": "accommodationDescriptors delete description"
                }
              },
              "/tpdm/credentialDecriptor": {
                "get": {
                  "description": "credential decriptor get"
                },
                "post": {
                  "description": "credential decriptor post"
                }
              },
              "/tpdm/candidateDescriptor/{id}": {
                "get": {
                  "description": "candidate descriptor id get"
                },
                "delete": {
                  "description": "candidate descriptor delete"
                }
              }
            }
            """;

            AssertResults("$.paths", openApiDescriptorsResult, expectedResult);
        }

        [Test]
        public void It_should_merge_in_openApiCoreDescriptor_schemas()
        {
            string expectedResult = """
            {
              "EdFi_AbsenceEventCategoryDescriptor": {
                "description": "An Ed-Fi Descriptor",
                "properties": {},
                "type": "string"
              },
              "EdFi_AcademicHonorCategoryDescriptor": {
                "description": "An Ed-Fi Descriptor",
                "properties": {},
                "type": "string"
              },
              "EdFi_AcademicSubjectDescriptor": {
                "description": "An Ed-Fi Descriptor",
                "properties": {},
                "type": "string"
              },
              "EdFi_AccommodationDescriptor": {
                "description": "An Ed-Fi Descriptor",
                "properties": {},
                "type": "string"
              },
              "TPDM_CredentialDecriptor": {
                "description": "TPDM credential decriptor description",
                "type": "string"
              },
              "TPDM_CandidateDescriptor": {
                "description": "TPDM candidate descriptor description",
                "type": "string"
              }
            }
            """;

            AssertResults("$.components.schemas", openApiDescriptorsResult, expectedResult);
        }

        [Test]
        public void It_should_merge_in_openApiCoreDescriptor_tags()
        {
            string expectedResult = """
            [
              {
                "name": "academicSubjects",
                "description": "AcademicSubjects Descriptors Description"
              },
              {
                "name": "accommodations",
                "description": "Accommodations Descriptors Description"
              }
            ]
            """;

            AssertResults("$.tags", openApiDescriptorsResult, expectedResult);
        }


        public static void AssertResults(
            string jsonPathSource,
            JsonNode openApiResourcesResult,
            string expectedResult
        )
        {
            JsonPath jsonPath = JsonPath.Parse(jsonPathSource);
            string result = string.Empty;

            PathResult pathResult = jsonPath.Evaluate(openApiResourcesResult);

            if (pathResult.Matches.Count == 1)
            {
                JsonNode? openApiExtensionResourceFragments = pathResult.Matches[0].Value;
                result = JsonSerializer.Serialize(
                    openApiExtensionResourceFragments,
                    new JsonSerializerOptions { WriteIndented = true }
                );
            }

            expectedResult = expectedResult.Replace("\r\n", "\n");
            result = result.Replace("\r\n", "\n");

            result.Should().Be(expectedResult);
        }
    }
}
