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
        tags.Add(
            new JsonObject { ["name"] = "academicWeeks", ["description"] = "AcademicWeeks Description" }
        );
        tags.Add(
            new JsonObject
            {
                ["name"] = "accountabilityRating",
                ["description"] = "AccountabilityRatings Description",
            }
        );

        JsonArray descriptorsTags = [];
        descriptorsTags.Add(
            new JsonObject
            {
                ["name"] = "academicSubjects",
                ["description"] = "AcademicSubjects Descriptors Description",
            }
        );
        descriptorsTags.Add(
            new JsonObject
            {
                ["name"] = "accommodations",
                ["description"] = "Accommodations Descriptors Description",
            }
        );

        var builder = new ApiSchemaBuilder()
            .WithStartProject("ed-fi", "5.0.0")
            .WithOpenApiBaseDocuments(
                resourcesDoc: new JsonObject
                {
                    ["openapi"] = "3.0.1",
                    ["info"] = new JsonObject { ["title"] = "Ed-Fi Resources API", ["version"] = "5.0.0" },
                    ["components"] = new JsonObject { ["schemas"] = schemas },
                    ["paths"] = paths,
                    ["tags"] = tags,
                },
                descriptorsDoc: new JsonObject
                {
                    ["openapi"] = "3.0.1",
                    ["info"] = new JsonObject { ["title"] = "Ed-Fi Descriptors API", ["version"] = "5.0.0" },
                    ["components"] = new JsonObject { ["schemas"] = descriptorSchemas },
                    ["paths"] = descriptorsPaths,
                    ["tags"] = descriptorsTags,
                }
            );

        // Add resources for each schema
        builder.WithSimpleResource("AcademicWeek", false, schemas["EdFi_AcademicWeek"]);
        builder.WithSimpleResource("AccountabilityRating", false, schemas["EdFi_AccountabilityRating"]);

        // Add descriptors
        builder.WithSimpleDescriptor(
            "AbsenceEventCategoryDescriptor",
            descriptorSchemas["EdFi_AbsenceEventCategoryDescriptor"]
        );
        builder.WithSimpleDescriptor(
            "AcademicHonorCategoryDescriptor",
            descriptorSchemas["EdFi_AcademicHonorCategoryDescriptor"]
        );
        builder.WithSimpleDescriptor(
            "AcademicSubjectDescriptor",
            descriptorSchemas["EdFi_AcademicSubjectDescriptor"]
        );
        builder.WithSimpleDescriptor(
            "AccommodationDescriptor",
            descriptorSchemas["EdFi_AccommodationDescriptor"]
        );

        return builder.WithEndProject().AsSingleApiSchemaRootNode();
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
        };

        JsonObject descriptorNewPaths = new()
        {
            ["/tpdm/credentialDescriptor"] = new JsonObject
            {
                ["get"] = new JsonObject { ["description"] = "credential descriptor get" },
                ["post"] = new JsonObject { ["description"] = "credential descriptor post" },
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
            ["TPDM_CredentialDescriptor"] = new JsonObject
            {
                ["description"] = "TPDM credential descriptor description",
                ["type"] = "string",
            },
        };

        JsonArray newTags = [];
        newTags.Add(
            new JsonObject
            {
                ["name"] = "ExtensionTagName1",
                ["description"] = "First Extension Description1",
            }
        );
        newTags.Add(
            new JsonObject
            {
                ["name"] = "ExtensionTagName2",
                ["description"] = "First Extension Description2",
            }
        );

        JsonArray descriptorNewTags = [];
        descriptorNewTags.Add(
            new JsonObject
            {
                ["name"] = "ExtensionTagName1",
                ["description"] = "First Extension Descriptor Description1",
            }
        );
        descriptorNewTags.Add(
            new JsonObject
            {
                ["name"] = "ExtensionTagName2",
                ["description"] = "First Extension Descriptor Description2",
            }
        );

        var builder = new ApiSchemaBuilder().WithStartProject("tpdm", "5.0.0");

        // Add resource extension (exts)
        builder
            .WithStartResource("AcademicWeekExtension", isResourceExtension: true)
            .WithResourceExtensionFragments("resources", exts)
            .WithEndResource();

        // Add new extension resource
        builder
            .WithStartResource("Credential", isDescriptor: false)
            .WithNewExtensionResourceFragments("resources", newSchemas, newPaths, newTags)
            .WithEndResource();

        // Add new extension descriptor
        builder
            .WithStartResource("CredentialDescriptor", isDescriptor: true)
            .WithNewExtensionResourceFragments(
                "descriptors",
                descriptorNewSchemas,
                descriptorNewPaths,
                descriptorNewTags
            )
            .WithEndResource();

        return builder.WithEndProject().AsSingleApiSchemaRootNode();
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
            new JsonObject
            {
                ["name"] = "ExtensionTagName3",
                ["description"] = "Second Extension Description3",
            }
        );
        newTags.Add(
            new JsonObject
            {
                ["name"] = "ExtensionTagName4",
                ["description"] = "Second Extension Description4",
            }
        );

        JsonArray descriptorNewTags = [];
        descriptorNewTags.Add(
            new JsonObject
            {
                ["name"] = "ExtensionTagName3",
                ["description"] = "Second Extension Descriptor Description3",
            }
        );
        descriptorNewTags.Add(
            new JsonObject
            {
                ["name"] = "ExtensionTagName4",
                ["description"] = "Second Extension Descriptor Description4",
            }
        );

        var builder = new ApiSchemaBuilder().WithStartProject("tpdm", "5.0.0");

        // Add resource extension (exts)
        builder
            .WithStartResource("SchoolExtension", isResourceExtension: true)
            .WithResourceExtensionFragments("resources", exts)
            .WithEndResource();

        // Add new extension resource
        builder
            .WithStartResource("Candidate", isDescriptor: false)
            .WithNewExtensionResourceFragments("resources", newSchemas, newPaths, newTags)
            .WithEndResource();

        // Add new extension descriptor
        builder
            .WithStartResource("CandidateDescriptor", isDescriptor: true)
            .WithNewExtensionResourceFragments(
                "descriptors",
                descriptorNewSchemas,
                descriptorNewPaths,
                descriptorNewTags
            )
            .WithEndResource();

        return builder.WithEndProject().AsSingleApiSchemaRootNode();
    }

    [TestFixture]
    [Parallelizable]
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
                OpenApiDocument.OpenApiDocumentType.Resource
            );
            openApiDescriptorsResult = openApiDocument.CreateDocument(
                new(coreSchemaRootNode, []),
                OpenApiDocument.OpenApiDocumentType.Descriptor
            );
        }

        [Test]
        public void It_should_be_the_simple_resources_result()
        {
            string expectedResult = """
                {
                  "openapi": "3.0.1",
                  "info": {
                    "title": "Ed-Fi Resources API",
                    "version": "5.0.0"
                  },
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
                  "openapi": "3.0.1",
                  "info": {
                    "title": "Ed-Fi Descriptors API",
                    "version": "5.0.0"
                  },
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
    [Parallelizable]
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
                OpenApiDocument.OpenApiDocumentType.Resource
            );
            openApiDescriptorsResult = openApiDocument.CreateDocument(
                new(coreSchemaRootNode, extensionSchemaRootNodes),
                OpenApiDocument.OpenApiDocumentType.Descriptor
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
            AssertResults("$.paths", openApiResourcesResult, expectedResult);
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
                        "$ref": "#/components/schemas/EdFi_AcademicWeekExtension"
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
                        "$ref": "#/components/schemas/EdFi_SchoolExtension"
                      }
                    },
                    "type": "string"
                  },
                  "EdFi_SurveyResponse": {
                    "description": "SurveyResponse description",
                    "properties": {},
                    "type": "string"
                  },
                  "EdFi_AcademicWeekExtension": {
                    "type": "object",
                    "properties": {
                      "tpdm": {
                        "$ref": "#/components/schemas/tpdm_EdFi_AcademicWeekExtension"
                      }
                    }
                  },
                  "tpdm_EdFi_AcademicWeekExtension": {
                    "description": "ext AcademicWeek description",
                    "type": "string"
                  },
                  "TPDM_Credential": {
                    "description": "TPDM credential description",
                    "type": "string"
                  },
                  "EdFi_SchoolExtension": {
                    "type": "object",
                    "properties": {
                      "tpdm": {
                        "$ref": "#/components/schemas/tpdm_EdFi_SchoolExtension"
                      }
                    }
                  },
                  "tpdm_EdFi_SchoolExtension": {
                    "description": "ext School description",
                    "type": "string"
                  },
                  "TPDM_Candidate": {
                    "description": "TPDM candidate description",
                    "type": "string"
                  }
                }
                """;

            AssertResults("$.components.schemas", openApiResourcesResult, expectedResult);
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
                    "name": "ExtensionTagName3",
                    "description": "Second Extension Description3"
                  },
                  {
                    "name": "ExtensionTagName4",
                    "description": "Second Extension Description4"
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
                  "/tpdm/credentialDescriptor": {
                    "get": {
                      "description": "credential descriptor get"
                    },
                    "post": {
                      "description": "credential descriptor post"
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
                  "TPDM_CredentialDescriptor": {
                    "description": "TPDM credential descriptor description",
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
                    "description": "Second Extension Descriptor Description3"
                  },
                  {
                    "name": "ExtensionTagName4",
                    "description": "Second Extension Descriptor Description4"
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
