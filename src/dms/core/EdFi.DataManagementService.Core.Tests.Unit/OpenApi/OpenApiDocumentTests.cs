// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
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
                ["get"] = new JsonObject
                {
                    ["description"] = "academicWeek get description",
                    ["tags"] = new JsonArray("academicWeeks"),
                },
                ["post"] = new JsonObject
                {
                    ["description"] = "academicWeek post description",
                    ["tags"] = new JsonArray("academicWeeks"),
                },
            },
            ["/ed-fi/academicWeeks/{id}"] = new JsonObject
            {
                ["get"] = new JsonObject
                {
                    ["description"] = "academicWeek id get description",
                    ["tags"] = new JsonArray("academicWeeks"),
                },
                ["delete"] = new JsonObject
                {
                    ["description"] = "academicWeek delete description",
                    ["tags"] = new JsonArray("academicWeeks"),
                },
            },
        };

        JsonObject descriptorsPaths = new()
        {
            ["/ed-fi/accommodationDescriptors"] = new JsonObject
            {
                ["get"] = new JsonObject
                {
                    ["description"] = "accommodationDescriptors get description",
                    ["tags"] = new JsonArray("accommodationDescriptors"),
                },
                ["post"] = new JsonObject
                {
                    ["description"] = "accommodationDescriptors post description",
                    ["tags"] = new JsonArray("accommodationDescriptors"),
                },
            },
            ["/ed-fi/accommodationDescriptors/{id}"] = new JsonObject
            {
                ["get"] = new JsonObject
                {
                    ["description"] = "accommodationDescriptors id get description",
                    ["tags"] = new JsonArray("accommodationDescriptors"),
                },
                ["delete"] = new JsonObject
                {
                    ["description"] = "accommodationDescriptors delete description",
                    ["tags"] = new JsonArray("accommodationDescriptors"),
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
                ["name"] = "academicSubjectDescriptors",
                ["description"] = "AcademicSubjects Descriptors Description",
            }
        );
        descriptorsTags.Add(
            new JsonObject
            {
                ["name"] = "accommodationDescriptors",
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
                        "description": "academicWeek get description",
                        "tags": [
                          "academicWeeks"
                        ]
                      },
                      "post": {
                        "description": "academicWeek post description",
                        "tags": [
                          "academicWeeks"
                        ]
                      }
                    },
                    "/ed-fi/academicWeeks/{id}": {
                      "get": {
                        "description": "academicWeek id get description",
                        "tags": [
                          "academicWeeks"
                        ]
                      },
                      "delete": {
                        "description": "academicWeek delete description",
                        "tags": [
                          "academicWeeks"
                        ]
                      }
                    }
                  },
                  "tags": [
                    {
                      "name": "academicWeeks",
                      "description": "AcademicWeeks Description"
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
                        "description": "accommodationDescriptors get description",
                        "tags": [
                          "accommodationDescriptors"
                        ]
                      },
                      "post": {
                        "description": "accommodationDescriptors post description",
                        "tags": [
                          "accommodationDescriptors"
                        ]
                      }
                    },
                    "/ed-fi/accommodationDescriptors/{id}": {
                      "get": {
                        "description": "accommodationDescriptors id get description",
                        "tags": [
                          "accommodationDescriptors"
                        ]
                      },
                      "delete": {
                        "description": "accommodationDescriptors delete description",
                        "tags": [
                          "accommodationDescriptors"
                        ]
                      }
                    }
                  },
                  "tags": [
                    {
                      "name": "accommodationDescriptors",
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
                      "description": "academicWeek get description",
                      "tags": [
                        "academicWeeks"
                      ]
                    },
                    "post": {
                      "description": "academicWeek post description",
                      "tags": [
                        "academicWeeks"
                      ]
                    }
                  },
                  "/ed-fi/academicWeeks/{id}": {
                    "get": {
                      "description": "academicWeek id get description",
                      "tags": [
                        "academicWeeks"
                      ]
                    },
                    "delete": {
                      "description": "academicWeek delete description",
                      "tags": [
                        "academicWeeks"
                      ]
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
                      "description": "accommodationDescriptors get description",
                      "tags": [
                        "accommodationDescriptors"
                      ]
                    },
                    "post": {
                      "description": "accommodationDescriptors post description",
                      "tags": [
                        "accommodationDescriptors"
                      ]
                    }
                  },
                  "/ed-fi/accommodationDescriptors/{id}": {
                    "get": {
                      "description": "accommodationDescriptors id get description",
                      "tags": [
                        "accommodationDescriptors"
                      ]
                    },
                    "delete": {
                      "description": "accommodationDescriptors delete description",
                      "tags": [
                        "accommodationDescriptors"
                      ]
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
                    "name": "accommodationDescriptors",
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

    [TestFixture]
    public class Given_A_Schema_With_Domain_Filtering : OpenApiDocumentTests
    {
        [Test]
        public void It_should_exclude_SchoolCalendar_and_Enrollment_paths_from_resources()
        {
            // Create minimal paths for this test
            JsonObject pathsWithDomains = new()
            {
                ["/ed-fi/academicWeeks"] = new JsonObject
                {
                    ["get"] = new JsonObject(),
                    ["x-Ed-Fi-domains"] = new JsonArray { "SchoolCalendar" },
                },
                ["/ed-fi/students"] = new JsonObject
                {
                    ["get"] = new JsonObject(),
                    ["x-Ed-Fi-domains"] = new JsonArray { "Enrollment" },
                },
                ["/ed-fi/schools"] = new JsonObject
                {
                    ["get"] = new JsonObject(),
                    ["x-Ed-Fi-domains"] = new JsonArray { "EducationOrganization" },
                },
                ["/ed-fi/calendars"] = new JsonObject
                {
                    ["get"] = new JsonObject(),
                    ["x-Ed-Fi-domains"] = new JsonArray { "SchoolCalendar" },
                },
            };

            var apiSchemaDocumentNodes = new ApiSchemaBuilder()
                .WithStartProject("ed-fi", "5.0.0")
                .WithOpenApiBaseDocuments(
                    resourcesDoc: new JsonObject
                    {
                        ["openapi"] = "3.0.1",
                        ["info"] = new JsonObject
                        {
                            ["title"] = "Ed-Fi Resources API",
                            ["version"] = "5.0.0",
                        },
                        ["components"] = new JsonObject { ["schemas"] = new JsonObject() },
                        ["paths"] = pathsWithDomains,
                        ["tags"] = new JsonArray(),
                    },
                    descriptorsDoc: new JsonObject
                    {
                        ["openapi"] = "3.0.1",
                        ["info"] = new JsonObject
                        {
                            ["title"] = "Ed-Fi Descriptors API",
                            ["version"] = "5.0.0",
                        },
                        ["components"] = new JsonObject { ["schemas"] = new JsonObject() },
                        ["paths"] = new JsonObject(),
                        ["tags"] = new JsonArray(),
                    }
                )
                .WithEndProject()
                .AsApiSchemaNodes();

            string[] excludedDomains = ["SchoolCalendar", "Enrollment"];
            OpenApiDocument openApiDocument = new(NullLogger.Instance, excludedDomains);
            JsonNode resultResources = openApiDocument.CreateDocument(
                apiSchemaDocumentNodes,
                OpenApiDocument.OpenApiDocumentType.Resource
            );

            JsonObject? resultPaths = resultResources["paths"]?.AsObject();
            resultPaths.Should().NotBeNull();

            // Should exclude SchoolCalendar and Enrollment domain paths
            resultPaths!.Should().NotContainKey("/ed-fi/academicWeeks");
            resultPaths.Should().NotContainKey("/ed-fi/students");
            resultPaths.Should().NotContainKey("/ed-fi/calendars");

            // Should keep EducationOrganization domain paths
            resultPaths.Should().ContainKey("/ed-fi/schools");
        }

        [Test]
        public void It_should_exclude_SchoolCalendar_and_Enrollment_paths_from_descriptors()
        {
            // Create minimal descriptor paths for this test
            JsonObject descriptorPathsWithDomains = new()
            {
                ["/ed-fi/calendarTypeDescriptors"] = new JsonObject
                {
                    ["get"] = new JsonObject(),
                    ["x-Ed-Fi-domains"] = new JsonArray { "SchoolCalendar" },
                },
                ["/ed-fi/enrollmentTypeDescriptors"] = new JsonObject
                {
                    ["get"] = new JsonObject(),
                    ["x-Ed-Fi-domains"] = new JsonArray { "Enrollment" },
                },
                ["/ed-fi/schoolTypeDescriptors"] = new JsonObject
                {
                    ["get"] = new JsonObject(),
                    ["x-Ed-Fi-domains"] = new JsonArray { "EducationOrganization" },
                },
            };

            var apiSchemaDocumentNodes = new ApiSchemaBuilder()
                .WithStartProject("ed-fi", "5.0.0")
                .WithOpenApiBaseDocuments(
                    resourcesDoc: new JsonObject
                    {
                        ["openapi"] = "3.0.1",
                        ["info"] = new JsonObject
                        {
                            ["title"] = "Ed-Fi Resources API",
                            ["version"] = "5.0.0",
                        },
                        ["components"] = new JsonObject { ["schemas"] = new JsonObject() },
                        ["paths"] = new JsonObject(),
                        ["tags"] = new JsonArray(),
                    },
                    descriptorsDoc: new JsonObject
                    {
                        ["openapi"] = "3.0.1",
                        ["info"] = new JsonObject
                        {
                            ["title"] = "Ed-Fi Descriptors API",
                            ["version"] = "5.0.0",
                        },
                        ["components"] = new JsonObject { ["schemas"] = new JsonObject() },
                        ["paths"] = descriptorPathsWithDomains,
                        ["tags"] = new JsonArray(),
                    }
                )
                .WithEndProject()
                .AsApiSchemaNodes();

            string[] excludedDomains = ["SchoolCalendar", "Enrollment"];
            OpenApiDocument openApiDocument = new(NullLogger.Instance, excludedDomains);
            JsonNode resultDescriptors = openApiDocument.CreateDocument(
                apiSchemaDocumentNodes,
                OpenApiDocument.OpenApiDocumentType.Descriptor
            );

            JsonObject? resultPaths = resultDescriptors["paths"]?.AsObject();
            resultPaths.Should().NotBeNull();

            // Should exclude SchoolCalendar and Enrollment domain paths
            resultPaths!.Should().NotContainKey("/ed-fi/calendarTypeDescriptors");
            resultPaths.Should().NotContainKey("/ed-fi/enrollmentTypeDescriptors");

            // Should keep EducationOrganization domain paths
            resultPaths.Should().ContainKey("/ed-fi/schoolTypeDescriptors");
        }

        [Test]
        public void It_should_be_case_insensitive_for_domain_matching()
        {
            // Test with different case
            string[] excludedDomains = ["schoolcalendar", "ENROLLMENT"];
            OpenApiDocument openApiDocument = new(NullLogger.Instance, excludedDomains);

            var apiSchemaDocumentNodes = new ApiSchemaBuilder()
                .WithStartProject("ed-fi", "5.0.0")
                .WithOpenApiBaseDocuments(
                    resourcesDoc: new JsonObject
                    {
                        ["openapi"] = "3.0.1",
                        ["info"] = new JsonObject
                        {
                            ["title"] = "Ed-Fi Resources API",
                            ["version"] = "5.0.0",
                        },
                        ["components"] = new JsonObject { ["schemas"] = new JsonObject() },
                        ["paths"] = new JsonObject
                        {
                            ["/ed-fi/students"] = new JsonObject
                            {
                                ["get"] = new JsonObject(),
                                ["x-Ed-Fi-domains"] = new JsonArray { "Enrollment" },
                            },
                        },
                        ["tags"] = new JsonArray(),
                    },
                    descriptorsDoc: new JsonObject
                    {
                        ["openapi"] = "3.0.1",
                        ["info"] = new JsonObject
                        {
                            ["title"] = "Ed-Fi Descriptors API",
                            ["version"] = "5.0.0",
                        },
                        ["components"] = new JsonObject { ["schemas"] = new JsonObject() },
                        ["paths"] = new JsonObject(),
                        ["tags"] = new JsonArray(),
                    }
                )
                .WithEndProject()
                .AsApiSchemaNodes();

            JsonNode result = openApiDocument.CreateDocument(
                apiSchemaDocumentNodes,
                OpenApiDocument.OpenApiDocumentType.Resource
            );
            JsonObject? resultPaths = result["paths"]?.AsObject();

            resultPaths.Should().NotBeNull();
            resultPaths!.Should().NotContainKey("/ed-fi/students");
        }

        [Test]
        public void It_should_not_filter_when_no_domains_excluded()
        {
            // Test with no excluded domains
            OpenApiDocument openApiDocument = new(NullLogger.Instance, null);

            var apiSchemaDocumentNodes = new ApiSchemaBuilder()
                .WithStartProject("ed-fi", "5.0.0")
                .WithOpenApiBaseDocuments(
                    resourcesDoc: new JsonObject
                    {
                        ["openapi"] = "3.0.1",
                        ["info"] = new JsonObject
                        {
                            ["title"] = "Ed-Fi Resources API",
                            ["version"] = "5.0.0",
                        },
                        ["components"] = new JsonObject { ["schemas"] = new JsonObject() },
                        ["paths"] = new JsonObject
                        {
                            ["/ed-fi/academicWeeks"] = new JsonObject
                            {
                                ["get"] = new JsonObject(),
                                ["x-Ed-Fi-domains"] = new JsonArray { "SchoolCalendar" },
                            },
                            ["/ed-fi/students"] = new JsonObject
                            {
                                ["get"] = new JsonObject(),
                                ["x-Ed-Fi-domains"] = new JsonArray { "Enrollment" },
                            },
                        },
                        ["tags"] = new JsonArray(),
                    },
                    descriptorsDoc: new JsonObject
                    {
                        ["openapi"] = "3.0.1",
                        ["info"] = new JsonObject
                        {
                            ["title"] = "Ed-Fi Descriptors API",
                            ["version"] = "5.0.0",
                        },
                        ["components"] = new JsonObject { ["schemas"] = new JsonObject() },
                        ["paths"] = new JsonObject(),
                        ["tags"] = new JsonArray(),
                    }
                )
                .WithEndProject()
                .AsApiSchemaNodes();

            JsonNode result = openApiDocument.CreateDocument(
                apiSchemaDocumentNodes,
                OpenApiDocument.OpenApiDocumentType.Resource
            );
            JsonObject? resultPaths = result["paths"]?.AsObject();

            resultPaths.Should().NotBeNull();
            resultPaths!.Should().ContainKey("/ed-fi/academicWeeks");
            resultPaths.Should().ContainKey("/ed-fi/students");
        }

        [Test]
        public void It_should_remove_unused_tags_after_domain_filtering()
        {
            // Create paths with tags that will reference some but not all global tags
            JsonObject pathsWithTags = new()
            {
                ["/ed-fi/schools"] = new JsonObject
                {
                    ["get"] = new JsonObject { ["tags"] = new JsonArray { "schools" } },
                    ["x-Ed-Fi-domains"] = new JsonArray { "EducationOrganization" },
                },
                ["/ed-fi/academicWeeks"] = new JsonObject
                {
                    ["get"] = new JsonObject { ["tags"] = new JsonArray { "academicWeeks" } },
                    ["x-Ed-Fi-domains"] = new JsonArray { "SchoolCalendar" },
                },
            };

            // Create global tags array with both used and unused tags
            JsonArray globalTags = new()
            {
                new JsonObject { ["name"] = "schools", ["description"] = "School resources" },
                new JsonObject { ["name"] = "academicWeeks", ["description"] = "Academic week resources" },
                new JsonObject { ["name"] = "unusedTag", ["description"] = "This tag should be removed" },
            };

            var apiSchemaDocumentNodes = new ApiSchemaBuilder()
                .WithStartProject("ed-fi", "5.0.0")
                .WithOpenApiBaseDocuments(
                    resourcesDoc: new JsonObject
                    {
                        ["openapi"] = "3.0.1",
                        ["info"] = new JsonObject
                        {
                            ["title"] = "Ed-Fi Resources API",
                            ["version"] = "5.0.0",
                        },
                        ["components"] = new JsonObject { ["schemas"] = new JsonObject() },
                        ["paths"] = pathsWithTags,
                        ["tags"] = globalTags,
                    },
                    descriptorsDoc: new JsonObject
                    {
                        ["openapi"] = "3.0.1",
                        ["info"] = new JsonObject
                        {
                            ["title"] = "Ed-Fi Descriptors API",
                            ["version"] = "5.0.0",
                        },
                        ["components"] = new JsonObject { ["schemas"] = new JsonObject() },
                        ["paths"] = new JsonObject(),
                        ["tags"] = new JsonArray(),
                    }
                )
                .WithEndProject()
                .AsApiSchemaNodes();

            // Exclude SchoolCalendar domain which will remove /ed-fi/academicWeeks path
            string[] excludedDomains = ["SchoolCalendar"];
            OpenApiDocument openApiDocument = new(NullLogger.Instance, excludedDomains);

            JsonNode result = openApiDocument.CreateDocument(
                apiSchemaDocumentNodes,
                OpenApiDocument.OpenApiDocumentType.Resource
            );

            // Verify paths are filtered correctly
            JsonObject? resultPaths = result["paths"]?.AsObject();
            resultPaths.Should().NotBeNull();
            resultPaths!.Should().ContainKey("/ed-fi/schools");
            resultPaths.Should().NotContainKey("/ed-fi/academicWeeks");

            // Verify tags are filtered correctly
            JsonArray? resultTags = result["tags"]?.AsArray();
            resultTags.Should().NotBeNull();

            // Should keep "schools" tag (used by remaining path)
            var schoolsTag = resultTags!.FirstOrDefault(t => t?["name"]?.GetValue<string>() == "schools");
            schoolsTag.Should().NotBeNull();

            // Should remove "academicWeeks" tag (path was removed by domain filtering)
            var academicWeeksTag = resultTags!.FirstOrDefault(t =>
                t?["name"]?.GetValue<string>() == "academicWeeks"
            );
            academicWeeksTag.Should().BeNull();

            // Should remove "unusedTag" (never used by any path)
            var unusedTag = resultTags!.FirstOrDefault(t => t?["name"]?.GetValue<string>() == "unusedTag");
            unusedTag.Should().BeNull();
        }

        [Test]
        public void It_should_handle_malformed_domain_data_gracefully()
        {
            // Test with excluded domains that might have malformed data in the API schema
            string[] excludedDomains = ["TestDomain"];
            OpenApiDocument openApiDocument = new(NullLogger.Instance, excludedDomains);

            var apiSchemaDocumentNodes = new ApiSchemaBuilder()
                .WithStartProject("ed-fi", "5.0.0")
                .WithOpenApiBaseDocuments(
                    resourcesDoc: new JsonObject
                    {
                        ["openapi"] = "3.0.1",
                        ["info"] = new JsonObject
                        {
                            ["title"] = "Ed-Fi Resources API",
                            ["version"] = "5.0.0",
                        },
                        ["components"] = new JsonObject { ["schemas"] = new JsonObject() },
                        ["paths"] = new JsonObject
                        {
                            ["/ed-fi/testResource1"] = new JsonObject
                            {
                                ["get"] = new JsonObject(),
                                ["x-Ed-Fi-domains"] = new JsonArray { "TestDomain" }, // Valid string
                            },
                            ["/ed-fi/testResource2"] = new JsonObject
                            {
                                ["get"] = new JsonObject(),
                                ["x-Ed-Fi-domains"] = new JsonArray { 123 }, // Invalid: number instead of string
                            },
                            ["/ed-fi/testResource3"] = new JsonObject
                            {
                                ["get"] = new JsonObject(),
                                ["x-Ed-Fi-domains"] = new JsonArray { "" }, // Invalid: empty string
                            },
                            ["/ed-fi/testResource4"] = new JsonObject
                            {
                                ["get"] = new JsonObject(),
                                ["x-Ed-Fi-domains"] = new JsonArray { "OtherDomain" }, // Valid but not excluded
                            },
                        },
                        ["tags"] = new JsonArray(),
                    },
                    descriptorsDoc: new JsonObject
                    {
                        ["openapi"] = "3.0.1",
                        ["info"] = new JsonObject
                        {
                            ["title"] = "Ed-Fi Descriptors API",
                            ["version"] = "5.0.0",
                        },
                        ["components"] = new JsonObject { ["schemas"] = new JsonObject() },
                        ["paths"] = new JsonObject(),
                        ["tags"] = new JsonArray(),
                    }
                )
                .WithEndProject()
                .AsApiSchemaNodes();

            // This should not throw an exception despite malformed domain data
            JsonNode result = openApiDocument.CreateDocument(
                apiSchemaDocumentNodes,
                OpenApiDocument.OpenApiDocumentType.Resource
            );
            JsonObject? resultPaths = result["paths"]?.AsObject();

            resultPaths.Should().NotBeNull();

            // Should exclude testResource1 (valid "TestDomain" string that matches excluded domain)
            resultPaths!.Should().NotContainKey("/ed-fi/testResource1");

            // Should keep testResource2 (malformed data - number instead of string)
            resultPaths.Should().ContainKey("/ed-fi/testResource2");

            // Should keep testResource3 (empty string, not matching excluded domain)
            resultPaths.Should().ContainKey("/ed-fi/testResource3");

            // Should keep testResource4 (valid string but not in excluded domains)
            resultPaths.Should().ContainKey("/ed-fi/testResource4");
        }

        [Test]
        public void It_should_not_exclude_path_when_only_some_domains_are_excluded()
        {
            // Test that a path with multiple domains is NOT excluded when only some domains are in the excluded list
            string[] excludedDomains = ["SchoolCalendar", "Enrollment"];
            OpenApiDocument openApiDocument = new(NullLogger.Instance, excludedDomains);

            var apiSchemaDocumentNodes = new ApiSchemaBuilder()
                .WithStartProject("ed-fi", "5.0.0")
                .WithOpenApiBaseDocuments(
                    resourcesDoc: new JsonObject
                    {
                        ["openapi"] = "3.0.1",
                        ["info"] = new JsonObject
                        {
                            ["title"] = "Ed-Fi Resources API",
                            ["version"] = "5.0.0",
                        },
                        ["components"] = new JsonObject { ["schemas"] = new JsonObject() },
                        ["paths"] = new JsonObject
                        {
                            ["/ed-fi/mixedDomainResource"] = new JsonObject
                            {
                                ["get"] = new JsonObject { ["description"] = "Resource with mixed domains" },
                                // Has both excluded and non-excluded domains
                                ["x-Ed-Fi-domains"] = new JsonArray
                                {
                                    "SchoolCalendar",
                                    "EducationOrganization",
                                },
                            },
                            ["/ed-fi/allExcludedDomainResource"] = new JsonObject
                            {
                                ["get"] = new JsonObject
                                {
                                    ["description"] = "Resource with all excluded domains",
                                },
                                // Has only excluded domains
                                ["x-Ed-Fi-domains"] = new JsonArray { "SchoolCalendar", "Enrollment" },
                            },
                        },
                        ["tags"] = new JsonArray(),
                    },
                    descriptorsDoc: new JsonObject
                    {
                        ["openapi"] = "3.0.1",
                        ["info"] = new JsonObject
                        {
                            ["title"] = "Ed-Fi Descriptors API",
                            ["version"] = "5.0.0",
                        },
                        ["components"] = new JsonObject { ["schemas"] = new JsonObject() },
                        ["paths"] = new JsonObject(),
                        ["tags"] = new JsonArray(),
                    }
                )
                .WithEndProject()
                .AsApiSchemaNodes();

            JsonNode result = openApiDocument.CreateDocument(
                apiSchemaDocumentNodes,
                OpenApiDocument.OpenApiDocumentType.Resource
            );
            JsonObject? resultPaths = result["paths"]?.AsObject();

            resultPaths.Should().NotBeNull();

            // Should keep the mixed domain resource (has "EducationOrganization" which is not excluded)
            resultPaths!.Should().ContainKey("/ed-fi/mixedDomainResource");

            // Should exclude the all-excluded domain resource (all domains are excluded)
            resultPaths.Should().NotContainKey("/ed-fi/allExcludedDomainResource");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Refactored_Extension_Processing : OpenApiDocumentTests
    {
        private JsonNode openApiResourcesResult = new JsonObject();

        [SetUp]
        public void Setup()
        {
            // Use existing test data to verify our refactoring maintains backward compatibility
            JsonNode coreSchemaRootNode = CoreSchemaRootNode();
            JsonNode[] extensionSchemaRootNodes = [FirstExtensionSchemaRootNode()];

            OpenApiDocument openApiDocument = new(NullLogger.Instance);
            openApiResourcesResult = openApiDocument.CreateDocument(
                new(coreSchemaRootNode, extensionSchemaRootNodes),
                OpenApiDocument.OpenApiDocumentType.Resource
            );
        }

        [Test]
        public void It_should_maintain_backward_compatibility_with_simple_extensions()
        {
            // This test verifies that our refactored code still handles simple extension schemas
            // (the ones without a "properties" field) correctly using ProcessDirectExtensionSchema
            JsonPath jsonPath = JsonPath.Parse("$.components.schemas");
            PathResult pathResult = jsonPath.Evaluate(openApiResourcesResult);

            pathResult.Matches.Should().HaveCount(1);
            JsonObject? schemas = pathResult.Matches[0].Value?.AsObject();

            // Verify that the simple extension schemas are created properly
            schemas.Should().NotBeNull();
            schemas?.Should().ContainKey("EdFi_AcademicWeekExtension");
            schemas?.Should().ContainKey("tpdm_EdFi_AcademicWeekExtension");

            // Verify the structure matches expected format
            var extensionSchema = schemas?["EdFi_AcademicWeekExtension"]?.AsObject();
            extensionSchema.Should().NotBeNull();
            extensionSchema?["type"]?.GetValue<string>().Should().Be("object");
            extensionSchema?["properties"]?.AsObject().Should().ContainKey("tpdm");

            var projectSchema = schemas?["tpdm_EdFi_AcademicWeekExtension"]?.AsObject();
            projectSchema.Should().NotBeNull();
            projectSchema?["description"]?.GetValue<string>().Should().Be("ext AcademicWeek description");
            projectSchema?["type"]?.GetValue<string>().Should().Be("string");
        }

        [Test]
        public void It_should_create_extension_references_in_core_schemas()
        {
            // Verify that _ext references are added to core schemas that have extensions
            JsonPath jsonPath = JsonPath.Parse("$.components.schemas.EdFi_AcademicWeek");
            PathResult pathResult = jsonPath.Evaluate(openApiResourcesResult);

            pathResult.Matches.Should().HaveCount(1);
            JsonObject? academicWeekSchema = pathResult.Matches[0].Value?.AsObject();

            academicWeekSchema.Should().NotBeNull();
            academicWeekSchema?["properties"]?.AsObject().Should().ContainKey("_ext");

            var extRef = academicWeekSchema?["properties"]?.AsObject()?["_ext"]?.AsObject();
            extRef.Should().NotBeNull();
            extRef
                ?["$ref"]?.GetValue<string>()
                .Should()
                .Be("#/components/schemas/EdFi_AcademicWeekExtension");
        }

        [Test]
        public void It_should_not_modify_schemas_without_extensions()
        {
            // Verify that schemas without extensions remain unchanged
            JsonPath jsonPath = JsonPath.Parse("$.components.schemas.EdFi_AccountabilityRating");
            PathResult pathResult = jsonPath.Evaluate(openApiResourcesResult);

            pathResult.Matches.Should().HaveCount(1);
            JsonObject? schema = pathResult.Matches[0].Value?.AsObject();

            schema.Should().NotBeNull();
            schema?["properties"]?.AsObject().Should().NotContainKey("_ext");
            schema?["description"]?.GetValue<string>().Should().Be("AccountabilityRating description");
        }

        [Test]
        public void It_should_use_refactored_methods_for_complex_scenarios()
        {
            // Verify that the refactored helper methods are working correctly
            // This is tested implicitly by the successful execution of the other tests
            // and the fact that all original tests still pass

            // Verify that extensions are handled correctly
            JsonPath jsonPath = JsonPath.Parse("$.components.schemas");
            PathResult pathResult = jsonPath.Evaluate(openApiResourcesResult);

            JsonObject? schemas = pathResult.Matches[0].Value?.AsObject();
            schemas.Should().NotBeNull();

            // Should have AcademicWeek extensions from the test data (only extension defined in FirstExtensionSchemaRootNode)
            schemas?.Should().ContainKey("EdFi_AcademicWeekExtension");
            schemas?.Should().ContainKey("tpdm_EdFi_AcademicWeekExtension");

            // Should also have the TPDM schema defined in the extension
            schemas?.Should().ContainKey("TPDM_Credential");

            // Verify that schemas without extensions are not modified
            schemas?.Should().NotContainKey("EdFi_SchoolExtension");
            schemas?.Should().NotContainKey("tpdm_EdFi_SchoolExtension");
        }
    }
}
