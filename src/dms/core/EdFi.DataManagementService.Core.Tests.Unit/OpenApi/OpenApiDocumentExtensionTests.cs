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
using static EdFi.DataManagementService.Core.Tests.Unit.OpenApi.OpenApiDocumentTestBase;

namespace EdFi.DataManagementService.Core.Tests.Unit.OpenApi;

public class OpenApiDocumentExtensionTests
{
    [TestFixture]
    [Parallelizable]
    public class Given_A_Core_Schema_And_Multiple_Extension_Schemas : OpenApiDocumentExtensionTests
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
    [Parallelizable]
    public class Given_An_Extension_With_Common_Extension_Overrides : OpenApiDocumentExtensionTests
    {
        private JsonNode openApiResourcesResult = new JsonObject();

        private static JsonNode SampleExtensionWithCommonOverrides()
        {
            JsonObject exts = new()
            {
                ["EdFi_Contact"] = new JsonObject
                {
                    ["description"] = "Sample Contact extension",
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["isSportsFan"] = new JsonObject { ["type"] = "boolean" },
                    },
                },
            };

            JsonArray commonOverrides =
            [
                new JsonObject
                {
                    ["insertionLocations"] = new JsonArray("$.properties.addresses.items"),
                    ["schemaFragment"] = new JsonObject
                    {
                        ["additionalProperties"] = false,
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["sample"] = new JsonObject
                            {
                                ["type"] = "object",
                                ["properties"] = new JsonObject
                                {
                                    ["complex"] = new JsonObject { ["maxLength"] = 255, ["type"] = "string" },
                                    ["onBusRoute"] = new JsonObject { ["type"] = "boolean" },
                                },
                            },
                        },
                    },
                },
            ];

            var builder = new ApiSchemaBuilder().WithStartProject("sample", "1.0.0");

            builder
                .WithStartResource("Contact", isResourceExtension: true)
                .WithResourceExtensionFragments("resources", exts)
                .WithCommonExtensionOverrides(commonOverrides)
                .WithEndResource();

            return builder.WithEndProject().AsSingleApiSchemaRootNode();
        }

        [SetUp]
        public void Setup()
        {
            JsonNode coreSchemaRootNode = CoreSchemaWithContactAndAddress();
            JsonNode[] extensionSchemaRootNodes = [SampleExtensionWithCommonOverrides()];

            OpenApiDocument openApiDocument = new(NullLogger.Instance);
            openApiResourcesResult = openApiDocument.CreateDocument(
                new(coreSchemaRootNode, extensionSchemaRootNodes),
                OpenApiDocument.OpenApiDocumentType.Resource
            );
        }

        [Test]
        public void It_should_add_ext_to_common_type_component_schema()
        {
            JsonPath jsonPath = JsonPath.Parse("$.components.schemas.EdFi_Contact_Address.properties._ext");
            PathResult pathResult = jsonPath.Evaluate(openApiResourcesResult);

            pathResult.Matches.Should().HaveCount(1);
            var extRef = pathResult.Matches[0].Value?.AsObject();
            extRef.Should().NotBeNull();
            extRef
                ?["$ref"]?.GetValue<string>()
                .Should()
                .Be("#/components/schemas/EdFi_Contact_AddressExtension");
        }

        [Test]
        public void It_should_create_extension_schema_for_common_type()
        {
            JsonPath jsonPath = JsonPath.Parse("$.components.schemas.EdFi_Contact_AddressExtension");
            PathResult pathResult = jsonPath.Evaluate(openApiResourcesResult);

            pathResult.Matches.Should().HaveCount(1);
            var extensionSchema = pathResult.Matches[0].Value?.AsObject();
            extensionSchema.Should().NotBeNull();
            extensionSchema?["type"]?.GetValue<string>().Should().Be("object");
            extensionSchema?["properties"]?.AsObject().Should().ContainKey("sample");

            var sampleRef = extensionSchema?["properties"]?["sample"]?.AsObject();
            sampleRef
                ?["$ref"]?.GetValue<string>()
                .Should()
                .Be("#/components/schemas/sample_EdFi_Contact_AddressExtension");
        }

        [Test]
        public void It_should_create_project_extension_schema_for_common_type()
        {
            JsonPath jsonPath = JsonPath.Parse("$.components.schemas.sample_EdFi_Contact_AddressExtension");
            PathResult pathResult = jsonPath.Evaluate(openApiResourcesResult);

            pathResult.Matches.Should().HaveCount(1);
            var projectSchema = pathResult.Matches[0].Value?.AsObject();
            projectSchema.Should().NotBeNull();
            projectSchema?["type"]?.GetValue<string>().Should().Be("object");
            projectSchema?["properties"]?.AsObject().Should().ContainKey("complex");
            projectSchema?["properties"]?.AsObject().Should().ContainKey("onBusRoute");
        }

        [Test]
        public void It_should_also_add_ext_to_top_level_resource()
        {
            JsonPath jsonPath = JsonPath.Parse("$.components.schemas.EdFi_Contact.properties._ext");
            PathResult pathResult = jsonPath.Evaluate(openApiResourcesResult);

            pathResult.Matches.Should().HaveCount(1);
            var extRef = pathResult.Matches[0].Value?.AsObject();
            extRef.Should().NotBeNull();
            extRef?["$ref"]?.GetValue<string>().Should().Be("#/components/schemas/EdFi_ContactExtension");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Multiple_Extensions_Targeting_Same_Common_Type : OpenApiDocumentExtensionTests
    {
        private JsonNode openApiResourcesResult = new JsonObject();

        private static JsonNode FirstExtensionWithCommonOverrides()
        {
            JsonObject exts = new()
            {
                ["EdFi_Contact"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["isSportsFan"] = new JsonObject { ["type"] = "boolean" },
                    },
                },
            };

            JsonArray commonOverrides =
            [
                new JsonObject
                {
                    ["insertionLocations"] = new JsonArray("$.properties.addresses.items"),
                    ["schemaFragment"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["sample"] = new JsonObject
                            {
                                ["type"] = "object",
                                ["properties"] = new JsonObject
                                {
                                    ["complex"] = new JsonObject { ["type"] = "string" },
                                },
                            },
                        },
                    },
                },
            ];

            var builder = new ApiSchemaBuilder().WithStartProject("sample", "1.0.0");
            builder
                .WithStartResource("Contact", isResourceExtension: true)
                .WithResourceExtensionFragments("resources", exts)
                .WithCommonExtensionOverrides(commonOverrides)
                .WithEndResource();

            return builder.WithEndProject().AsSingleApiSchemaRootNode();
        }

        private static JsonNode SecondExtensionWithCommonOverrides()
        {
            JsonObject exts = new()
            {
                ["EdFi_Contact"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["favoriteColor"] = new JsonObject { ["type"] = "string" },
                    },
                },
            };

            JsonArray commonOverrides =
            [
                new JsonObject
                {
                    ["insertionLocations"] = new JsonArray("$.properties.addresses.items"),
                    ["schemaFragment"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["tpdm"] = new JsonObject
                            {
                                ["type"] = "object",
                                ["properties"] = new JsonObject
                                {
                                    ["rating"] = new JsonObject { ["type"] = "integer" },
                                },
                            },
                        },
                    },
                },
            ];

            var builder = new ApiSchemaBuilder().WithStartProject("tpdm", "5.0.0");
            builder
                .WithStartResource("Contact", isResourceExtension: true)
                .WithResourceExtensionFragments("resources", exts)
                .WithCommonExtensionOverrides(commonOverrides)
                .WithEndResource();

            return builder.WithEndProject().AsSingleApiSchemaRootNode();
        }

        [SetUp]
        public void Setup()
        {
            JsonNode coreSchemaRootNode = CoreSchemaWithContactAndAddress();
            JsonNode[] extensionSchemaRootNodes =
            [
                FirstExtensionWithCommonOverrides(),
                SecondExtensionWithCommonOverrides(),
            ];

            OpenApiDocument openApiDocument = new(NullLogger.Instance);
            openApiResourcesResult = openApiDocument.CreateDocument(
                new(coreSchemaRootNode, extensionSchemaRootNodes),
                OpenApiDocument.OpenApiDocumentType.Resource
            );
        }

        [Test]
        public void It_should_have_both_projects_in_extension_schema()
        {
            JsonPath jsonPath = JsonPath.Parse(
                "$.components.schemas.EdFi_Contact_AddressExtension.properties"
            );
            PathResult pathResult = jsonPath.Evaluate(openApiResourcesResult);

            pathResult.Matches.Should().HaveCount(1);
            var properties = pathResult.Matches[0].Value?.AsObject();
            properties.Should().NotBeNull();
            properties.Should().ContainKey("sample");
            properties.Should().ContainKey("tpdm");
        }

        [Test]
        public void It_should_create_project_schemas_for_both_extensions()
        {
            JsonPath jsonPath = JsonPath.Parse("$.components.schemas");
            PathResult pathResult = jsonPath.Evaluate(openApiResourcesResult);

            var schemas = pathResult.Matches[0].Value?.AsObject();
            schemas.Should().ContainKey("sample_EdFi_Contact_AddressExtension");
            schemas.Should().ContainKey("tpdm_EdFi_Contact_AddressExtension");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_An_Extension_With_Nested_Collection_Common_Override : OpenApiDocumentExtensionTests
    {
        private JsonNode openApiResourcesResult = new JsonObject();

        private static JsonNode CoreSchemaWithNestedCollections()
        {
            JsonObject telephoneSchema = new()
            {
                ["description"] = "Student Address Telephone",
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["telephoneNumber"] = new JsonObject { ["type"] = "string" },
                },
            };

            JsonObject addressSchema = new()
            {
                ["description"] = "Student Address",
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["streetNumberName"] = new JsonObject { ["type"] = "string" },
                    ["telephones"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject
                        {
                            ["$ref"] = "#/components/schemas/EdFi_Student_Address_Telephone",
                        },
                    },
                },
            };

            JsonObject studentSchema = new()
            {
                ["description"] = "Student description",
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["studentUniqueId"] = new JsonObject { ["type"] = "string" },
                    ["addresses"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject { ["$ref"] = "#/components/schemas/EdFi_Student_Address" },
                    },
                },
            };

            JsonObject schemas = new()
            {
                ["EdFi_Student"] = studentSchema,
                ["EdFi_Student_Address"] = addressSchema,
                ["EdFi_Student_Address_Telephone"] = telephoneSchema,
            };

            var builder = new ApiSchemaBuilder()
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
                        ["components"] = new JsonObject { ["schemas"] = schemas },
                        ["paths"] = new JsonObject
                        {
                            ["/ed-fi/students"] = new JsonObject
                            {
                                ["get"] = new JsonObject
                                {
                                    ["description"] = "students get",
                                    ["tags"] = new JsonArray("students"),
                                },
                            },
                        },
                        ["tags"] = new JsonArray(
                            new JsonObject { ["name"] = "students", ["description"] = "Students" }
                        ),
                    }
                )
                .WithSimpleResource("Student", false)
                .WithEndProject();

            return builder.AsSingleApiSchemaRootNode();
        }

        private static JsonNode ExtensionWithNestedOverride()
        {
            JsonObject exts = new()
            {
                ["EdFi_Student"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject { ["petName"] = new JsonObject { ["type"] = "string" } },
                },
            };

            JsonArray commonOverrides =
            [
                new JsonObject
                {
                    ["insertionLocations"] = new JsonArray(
                        "$.properties.addresses.items.properties.telephones.items"
                    ),
                    ["schemaFragment"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["sample"] = new JsonObject
                            {
                                ["type"] = "object",
                                ["properties"] = new JsonObject
                                {
                                    ["isPrimary"] = new JsonObject { ["type"] = "boolean" },
                                },
                            },
                        },
                    },
                },
            ];

            var builder = new ApiSchemaBuilder().WithStartProject("sample", "1.0.0");
            builder
                .WithStartResource("Student", isResourceExtension: true)
                .WithResourceExtensionFragments("resources", exts)
                .WithCommonExtensionOverrides(commonOverrides)
                .WithEndResource();

            return builder.WithEndProject().AsSingleApiSchemaRootNode();
        }

        [SetUp]
        public void Setup()
        {
            JsonNode coreSchemaRootNode = CoreSchemaWithNestedCollections();
            JsonNode[] extensionSchemaRootNodes = [ExtensionWithNestedOverride()];

            OpenApiDocument openApiDocument = new(NullLogger.Instance);
            openApiResourcesResult = openApiDocument.CreateDocument(
                new(coreSchemaRootNode, extensionSchemaRootNodes),
                OpenApiDocument.OpenApiDocumentType.Resource
            );
        }

        [Test]
        public void It_should_add_ext_to_deeply_nested_common_type()
        {
            JsonPath jsonPath = JsonPath.Parse(
                "$.components.schemas.EdFi_Student_Address_Telephone.properties._ext"
            );
            PathResult pathResult = jsonPath.Evaluate(openApiResourcesResult);

            pathResult.Matches.Should().HaveCount(1);
            var extRef = pathResult.Matches[0].Value?.AsObject();
            extRef.Should().NotBeNull();
            extRef
                ?["$ref"]?.GetValue<string>()
                .Should()
                .Be("#/components/schemas/EdFi_Student_Address_TelephoneExtension");
        }

        [Test]
        public void It_should_create_project_schema_for_nested_common_type()
        {
            JsonPath jsonPath = JsonPath.Parse(
                "$.components.schemas.sample_EdFi_Student_Address_TelephoneExtension"
            );
            PathResult pathResult = jsonPath.Evaluate(openApiResourcesResult);

            pathResult.Matches.Should().HaveCount(1);
            var projectSchema = pathResult.Matches[0].Value?.AsObject();
            projectSchema.Should().NotBeNull();
            projectSchema?["properties"]?.AsObject().Should().ContainKey("isPrimary");
        }

        [Test]
        public void It_should_not_add_ext_to_intermediate_common_type()
        {
            JsonPath jsonPath = JsonPath.Parse("$.components.schemas.EdFi_Student_Address.properties._ext");
            PathResult pathResult = jsonPath.Evaluate(openApiResourcesResult);

            pathResult.Matches.Should().HaveCount(0);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_An_Extension_With_Common_Overrides_And_Exts : OpenApiDocumentExtensionTests
    {
        private JsonNode openApiResourcesResult = new JsonObject();

        private static JsonNode ExtensionWithCommonOverridesAndExts()
        {
            JsonObject exts = new()
            {
                ["EdFi_Contact"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject { ["pet"] = new JsonObject { ["type"] = "string" } },
                },
            };

            JsonArray commonOverrides =
            [
                new JsonObject
                {
                    ["insertionLocations"] = new JsonArray("$.properties.addresses.items"),
                    ["schemaFragment"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["sample"] = new JsonObject
                            {
                                ["type"] = "object",
                                ["properties"] = new JsonObject
                                {
                                    ["complex"] = new JsonObject { ["type"] = "string" },
                                },
                            },
                        },
                    },
                },
            ];

            var extensionBuilder = new ApiSchemaBuilder().WithStartProject("sample", "1.0.0");
            extensionBuilder
                .WithStartResource("Contact", isResourceExtension: true)
                .WithResourceExtensionFragments("resources", exts)
                .WithCommonExtensionOverrides(commonOverrides)
                .WithEndResource();

            return extensionBuilder.WithEndProject().AsSingleApiSchemaRootNode();
        }

        [SetUp]
        public void Setup()
        {
            JsonNode coreSchemaRootNode = CoreSchemaWithContactAndAddress();
            JsonNode[] extensionSchemaRootNodes = [ExtensionWithCommonOverridesAndExts()];

            OpenApiDocument openApiDocument = new(NullLogger.Instance);
            openApiResourcesResult = openApiDocument.CreateDocument(
                new(coreSchemaRootNode, extensionSchemaRootNodes),
                OpenApiDocument.OpenApiDocumentType.Resource
            );
        }

        [Test]
        public void It_should_succeed_and_return_a_document()
        {
            openApiResourcesResult.Should().NotBeNull();
        }

        [Test]
        public void It_should_add_ext_to_common_type_using_derived_core_schema_name()
        {
            JsonPath jsonPath = JsonPath.Parse("$.components.schemas.EdFi_Contact_Address.properties._ext");
            PathResult pathResult = jsonPath.Evaluate(openApiResourcesResult);

            pathResult.Matches.Should().HaveCount(1);
            var extRef = pathResult.Matches[0].Value?.AsObject();
            extRef.Should().NotBeNull();
            extRef
                ?["$ref"]?.GetValue<string>()
                .Should()
                .Be("#/components/schemas/EdFi_Contact_AddressExtension");
        }

        [Test]
        public void It_should_create_project_extension_schema_for_common_type()
        {
            JsonPath jsonPath = JsonPath.Parse("$.components.schemas.sample_EdFi_Contact_AddressExtension");
            PathResult pathResult = jsonPath.Evaluate(openApiResourcesResult);

            pathResult.Matches.Should().HaveCount(1);
            var projectSchema = pathResult.Matches[0].Value?.AsObject();
            projectSchema.Should().NotBeNull();
            projectSchema?["properties"]?.AsObject().Should().ContainKey("complex");
        }

        [Test]
        public void It_should_also_add_ext_to_top_level_resource()
        {
            JsonPath jsonPath = JsonPath.Parse("$.components.schemas.EdFi_Contact.properties._ext");
            PathResult pathResult = jsonPath.Evaluate(openApiResourcesResult);

            pathResult.Matches.Should().HaveCount(1);
            var extRef = pathResult.Matches[0].Value?.AsObject();
            extRef.Should().NotBeNull();
            extRef?["$ref"]?.GetValue<string>().Should().Be("#/components/schemas/EdFi_ContactExtension");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_An_Extension_With_Common_Overrides_But_No_Exts : OpenApiDocumentExtensionTests
    {
        private JsonNode openApiResourcesResult = new JsonObject();

        private static JsonNode ExtensionWithCommonOverridesButNoExts()
        {
            JsonArray commonOverrides =
            [
                new JsonObject
                {
                    ["insertionLocations"] = new JsonArray("$.properties.addresses.items"),
                    ["schemaFragment"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["sample"] = new JsonObject
                            {
                                ["type"] = "object",
                                ["properties"] = new JsonObject
                                {
                                    ["complex"] = new JsonObject { ["type"] = "string" },
                                },
                            },
                        },
                    },
                },
            ];

            var extensionBuilder = new ApiSchemaBuilder().WithStartProject("sample", "1.0.0");
            extensionBuilder
                .WithStartResource("Contact", isResourceExtension: true)
                .WithNewExtensionResourceFragments("resources")
                .WithCommonExtensionOverrides(commonOverrides)
                .WithEndResource();

            return extensionBuilder.WithEndProject().AsSingleApiSchemaRootNode();
        }

        [SetUp]
        public void Setup()
        {
            JsonNode coreSchemaRootNode = CoreSchemaWithContactAndAddress();
            JsonNode[] extensionSchemaRootNodes = [ExtensionWithCommonOverridesButNoExts()];

            OpenApiDocument openApiDocument = new(NullLogger.Instance);
            openApiResourcesResult = openApiDocument.CreateDocument(
                new(coreSchemaRootNode, extensionSchemaRootNodes),
                OpenApiDocument.OpenApiDocumentType.Resource
            );
        }

        [Test]
        public void It_should_succeed_and_return_a_document()
        {
            openApiResourcesResult.Should().NotBeNull();
        }

        [Test]
        public void It_should_derive_core_schema_from_component_schemas()
        {
            JsonPath jsonPath = JsonPath.Parse("$.components.schemas.EdFi_Contact_Address.properties._ext");
            PathResult pathResult = jsonPath.Evaluate(openApiResourcesResult);

            pathResult.Matches.Should().HaveCount(1);
            var extRef = pathResult.Matches[0].Value?.AsObject();
            extRef.Should().NotBeNull();
            extRef
                ?["$ref"]?.GetValue<string>()
                .Should()
                .Be("#/components/schemas/EdFi_Contact_AddressExtension");
        }

        [Test]
        public void It_should_create_project_extension_schema_for_common_type()
        {
            JsonPath jsonPath = JsonPath.Parse("$.components.schemas.sample_EdFi_Contact_AddressExtension");
            PathResult pathResult = jsonPath.Evaluate(openApiResourcesResult);

            pathResult.Matches.Should().HaveCount(1);
            var projectSchema = pathResult.Matches[0].Value?.AsObject();
            projectSchema.Should().NotBeNull();
            projectSchema?["properties"]?.AsObject().Should().ContainKey("complex");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_An_Extension_With_Unresolvable_Insertion_Location : OpenApiDocumentExtensionTests
    {
        private static JsonNode ExtensionWithUnresolvableOverride()
        {
            JsonObject exts = new()
            {
                ["EdFi_Contact"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["isSportsFan"] = new JsonObject { ["type"] = "boolean" },
                    },
                },
            };

            JsonArray commonOverrides =
            [
                new JsonObject
                {
                    ["insertionLocations"] = new JsonArray("$.properties.nonExistentCollection.items"),
                    ["schemaFragment"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["sample"] = new JsonObject
                            {
                                ["type"] = "object",
                                ["properties"] = new JsonObject
                                {
                                    ["complex"] = new JsonObject { ["type"] = "string" },
                                },
                            },
                        },
                    },
                },
            ];

            var builder = new ApiSchemaBuilder().WithStartProject("sample", "1.0.0");
            builder
                .WithStartResource("Contact", isResourceExtension: true)
                .WithResourceExtensionFragments("resources", exts)
                .WithCommonExtensionOverrides(commonOverrides)
                .WithEndResource();

            return builder.WithEndProject().AsSingleApiSchemaRootNode();
        }

        [Test]
        public void It_should_throw_when_insertion_location_cannot_be_resolved()
        {
            JsonNode coreSchemaRootNode = CoreSchemaWithContactOnly();
            JsonNode[] extensionSchemaRootNodes = [ExtensionWithUnresolvableOverride()];

            OpenApiDocument doc = new(NullLogger.Instance);
            var action = () =>
                doc.CreateDocument(
                    new(coreSchemaRootNode, extensionSchemaRootNodes),
                    OpenApiDocument.OpenApiDocumentType.Resource
                );

            action
                .Should()
                .Throw<InvalidOperationException>()
                .WithMessage("*Contact*")
                .WithMessage("*nonExistentCollection*EdFi_Contact*");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_An_Extension_With_Override_Missing_InsertionLocations : OpenApiDocumentExtensionTests
    {
        private static JsonNode ExtensionWithMissingInsertionLocations()
        {
            JsonObject exts = new()
            {
                ["EdFi_Contact"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["isSportsFan"] = new JsonObject { ["type"] = "boolean" },
                    },
                },
            };

            JsonArray commonOverrides =
            [
                new JsonObject
                {
                    ["schemaFragment"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["sample"] = new JsonObject { ["type"] = "string" },
                        },
                    },
                },
            ];

            var builder = new ApiSchemaBuilder().WithStartProject("sample", "1.0.0");
            builder
                .WithStartResource("Contact", isResourceExtension: true)
                .WithResourceExtensionFragments("resources", exts)
                .WithCommonExtensionOverrides(commonOverrides)
                .WithEndResource();

            return builder.WithEndProject().AsSingleApiSchemaRootNode();
        }

        [Test]
        public void It_should_throw_with_actionable_message()
        {
            JsonNode coreSchemaRootNode = CoreSchemaWithContactOnly();
            JsonNode[] extensionSchemaRootNodes = [ExtensionWithMissingInsertionLocations()];

            OpenApiDocument doc = new(NullLogger.Instance);
            var action = () =>
                doc.CreateDocument(
                    new(coreSchemaRootNode, extensionSchemaRootNodes),
                    OpenApiDocument.OpenApiDocumentType.Resource
                );

            action
                .Should()
                .Throw<InvalidOperationException>()
                .WithMessage("*Contact*")
                .WithMessage("*insertionLocations*");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_An_Extension_With_Override_Missing_SchemaFragment : OpenApiDocumentExtensionTests
    {
        private static JsonNode ExtensionWithMissingSchemaFragment()
        {
            JsonObject exts = new()
            {
                ["EdFi_Contact"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["isSportsFan"] = new JsonObject { ["type"] = "boolean" },
                    },
                },
            };

            JsonArray commonOverrides =
            [
                new JsonObject { ["insertionLocations"] = new JsonArray("$.properties.contactUniqueId") },
            ];

            var builder = new ApiSchemaBuilder().WithStartProject("sample", "1.0.0");
            builder
                .WithStartResource("Contact", isResourceExtension: true)
                .WithResourceExtensionFragments("resources", exts)
                .WithCommonExtensionOverrides(commonOverrides)
                .WithEndResource();

            return builder.WithEndProject().AsSingleApiSchemaRootNode();
        }

        [Test]
        public void It_should_throw_with_actionable_message()
        {
            JsonNode coreSchemaRootNode = CoreSchemaWithContactOnly();
            JsonNode[] extensionSchemaRootNodes = [ExtensionWithMissingSchemaFragment()];

            OpenApiDocument doc = new(NullLogger.Instance);
            var action = () =>
                doc.CreateDocument(
                    new(coreSchemaRootNode, extensionSchemaRootNodes),
                    OpenApiDocument.OpenApiDocumentType.Resource
                );

            action
                .Should()
                .Throw<InvalidOperationException>()
                .WithMessage("*Contact*")
                .WithMessage("*schemaFragment*");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_An_Extension_With_Override_Missing_Both_InsertionLocations_And_SchemaFragment
        : OpenApiDocumentExtensionTests
    {
        private static JsonNode ExtensionWithMissingBothFields()
        {
            JsonObject exts = new()
            {
                ["EdFi_Contact"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["isSportsFan"] = new JsonObject { ["type"] = "boolean" },
                    },
                },
            };

            JsonArray commonOverrides = [new JsonObject { ["foo"] = "bar" }];

            var builder = new ApiSchemaBuilder().WithStartProject("sample", "1.0.0");
            builder
                .WithStartResource("Contact", isResourceExtension: true)
                .WithResourceExtensionFragments("resources", exts)
                .WithCommonExtensionOverrides(commonOverrides)
                .WithEndResource();

            return builder.WithEndProject().AsSingleApiSchemaRootNode();
        }

        [Test]
        public void It_should_throw_with_actionable_message()
        {
            JsonNode coreSchemaRootNode = CoreSchemaWithContactOnly();
            JsonNode[] extensionSchemaRootNodes = [ExtensionWithMissingBothFields()];

            OpenApiDocument doc = new(NullLogger.Instance);
            var action = () =>
                doc.CreateDocument(
                    new(coreSchemaRootNode, extensionSchemaRootNodes),
                    OpenApiDocument.OpenApiDocumentType.Resource
                );

            action
                .Should()
                .Throw<InvalidOperationException>()
                .WithMessage("*Contact*")
                .WithMessage("*insertionLocations*")
                .WithMessage("*schemaFragment*");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_An_Extension_With_Override_Missing_Properties : OpenApiDocumentExtensionTests
    {
        private static JsonNode ExtensionWithSchemaFragmentMissingProperties()
        {
            JsonObject exts = new()
            {
                ["EdFi_Contact"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["isSportsFan"] = new JsonObject { ["type"] = "boolean" },
                    },
                },
            };

            JsonArray commonOverrides =
            [
                new JsonObject
                {
                    ["insertionLocations"] = new JsonArray("$.properties.contactUniqueId"),
                    ["schemaFragment"] = new JsonObject { ["type"] = "object" },
                },
            ];

            var builder = new ApiSchemaBuilder().WithStartProject("sample", "1.0.0");
            builder
                .WithStartResource("Contact", isResourceExtension: true)
                .WithResourceExtensionFragments("resources", exts)
                .WithCommonExtensionOverrides(commonOverrides)
                .WithEndResource();

            return builder.WithEndProject().AsSingleApiSchemaRootNode();
        }

        [Test]
        public void It_should_throw_with_actionable_message()
        {
            JsonNode coreSchemaRootNode = CoreSchemaWithContactOnly();
            JsonNode[] extensionSchemaRootNodes = [ExtensionWithSchemaFragmentMissingProperties()];

            OpenApiDocument doc = new(NullLogger.Instance);
            var action = () =>
                doc.CreateDocument(
                    new(coreSchemaRootNode, extensionSchemaRootNodes),
                    OpenApiDocument.OpenApiDocumentType.Resource
                );

            action
                .Should()
                .Throw<InvalidOperationException>()
                .WithMessage("*Contact*")
                .WithMessage("*insertionLocations*")
                .WithMessage("*properties*");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_An_Extension_With_Override_Empty_JsonPath : OpenApiDocumentExtensionTests
    {
        private static JsonNode ExtensionWithEmptyJsonPath()
        {
            JsonObject exts = new()
            {
                ["EdFi_Contact"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["isSportsFan"] = new JsonObject { ["type"] = "boolean" },
                    },
                },
            };

            JsonArray commonOverrides =
            [
                new JsonObject
                {
                    ["insertionLocations"] = new JsonArray(""),
                    ["schemaFragment"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["sample"] = new JsonObject { ["type"] = "string" },
                        },
                    },
                },
            ];

            var builder = new ApiSchemaBuilder().WithStartProject("sample", "1.0.0");
            builder
                .WithStartResource("Contact", isResourceExtension: true)
                .WithResourceExtensionFragments("resources", exts)
                .WithCommonExtensionOverrides(commonOverrides)
                .WithEndResource();

            return builder.WithEndProject().AsSingleApiSchemaRootNode();
        }

        [Test]
        public void It_should_throw_with_actionable_message()
        {
            JsonNode coreSchemaRootNode = CoreSchemaWithContactOnly();
            JsonNode[] extensionSchemaRootNodes = [ExtensionWithEmptyJsonPath()];

            OpenApiDocument doc = new(NullLogger.Instance);
            var action = () =>
                doc.CreateDocument(
                    new(coreSchemaRootNode, extensionSchemaRootNodes),
                    OpenApiDocument.OpenApiDocumentType.Resource
                );

            action
                .Should()
                .Throw<InvalidOperationException>()
                .WithMessage("*Contact*")
                .WithMessage("*null or empty*");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_An_Extension_With_Target_Schema_Without_Properties : OpenApiDocumentExtensionTests
    {
        private static JsonNode CoreSchemaWithContactAndAddressWithoutProperties()
        {
            JsonObject addressSchema = new()
            {
                ["description"] = "Address without properties",
                ["type"] = "object",
            };

            JsonObject contactSchema = new()
            {
                ["description"] = "Contact description",
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["contactUniqueId"] = new JsonObject { ["type"] = "string" },
                    ["addresses"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject { ["$ref"] = "#/components/schemas/EdFi_Contact_Address" },
                    },
                },
            };

            JsonObject schemas = new()
            {
                ["EdFi_Contact"] = contactSchema,
                ["EdFi_Contact_Address"] = addressSchema,
            };

            var builder = new ApiSchemaBuilder()
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
                        ["components"] = new JsonObject { ["schemas"] = schemas },
                        ["paths"] = new JsonObject
                        {
                            ["/ed-fi/contacts"] = new JsonObject
                            {
                                ["get"] = new JsonObject
                                {
                                    ["description"] = "contacts get",
                                    ["tags"] = new JsonArray("contacts"),
                                },
                            },
                        },
                        ["tags"] = new JsonArray(
                            new JsonObject { ["name"] = "contacts", ["description"] = "Contacts" }
                        ),
                    }
                )
                .WithSimpleResource("Contact", false)
                .WithEndProject();

            return builder.AsSingleApiSchemaRootNode();
        }

        private static JsonNode ExtensionTargetingSchemaWithoutProperties()
        {
            JsonObject exts = new()
            {
                ["EdFi_Contact"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["isSportsFan"] = new JsonObject { ["type"] = "boolean" },
                    },
                },
            };

            JsonArray commonOverrides =
            [
                new JsonObject
                {
                    ["insertionLocations"] = new JsonArray("$.properties.addresses.items"),
                    ["schemaFragment"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["sample"] = new JsonObject { ["type"] = "string" },
                        },
                    },
                },
            ];

            var builder = new ApiSchemaBuilder().WithStartProject("sample", "1.0.0");
            builder
                .WithStartResource("Contact", isResourceExtension: true)
                .WithResourceExtensionFragments("resources", exts)
                .WithCommonExtensionOverrides(commonOverrides)
                .WithEndResource();

            return builder.WithEndProject().AsSingleApiSchemaRootNode();
        }

        [Test]
        public void It_should_throw_with_actionable_message()
        {
            JsonNode coreSchemaRootNode = CoreSchemaWithContactAndAddressWithoutProperties();
            JsonNode[] extensionSchemaRootNodes = [ExtensionTargetingSchemaWithoutProperties()];

            OpenApiDocument doc = new(NullLogger.Instance);
            var action = () =>
                doc.CreateDocument(
                    new(coreSchemaRootNode, extensionSchemaRootNodes),
                    OpenApiDocument.OpenApiDocumentType.Resource
                );

            action
                .Should()
                .Throw<InvalidOperationException>()
                .WithMessage("*Contact*")
                .WithMessage("*no 'properties' object*");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_An_Extension_With_Null_Override_Entries : OpenApiDocumentExtensionTests
    {
        private static JsonNode ExtensionWithNullOverrideEntries()
        {
            JsonObject validOverrideEntry = new()
            {
                ["insertionLocations"] = new JsonArray("$.properties.addresses.items"),
                ["schemaFragment"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["sample"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["nullFilterTest"] = new JsonObject { ["type"] = "string" },
                            },
                        },
                    },
                },
            };

            JsonArray commonOverrides = new JsonArray(null, validOverrideEntry, null);

            JsonObject exts = new()
            {
                ["EdFi_Contact"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject { ["pet"] = new JsonObject { ["type"] = "string" } },
                },
            };

            var builder = new ApiSchemaBuilder().WithStartProject("sample", "1.0.0");
            builder
                .WithStartResource("Contact", isResourceExtension: true)
                .WithResourceExtensionFragments("resources", exts)
                .WithCommonExtensionOverrides(commonOverrides)
                .WithEndResource();

            return builder.WithEndProject().AsSingleApiSchemaRootNode();
        }

        [Test]
        public void It_should_throw_InvalidOperationException_for_null_override_entry()
        {
            JsonNode coreSchemaRootNode = CoreSchemaWithContactAndAddress();
            JsonNode[] extensionSchemaRootNodes = [ExtensionWithNullOverrideEntries()];

            OpenApiDocument openApiDocument = new(NullLogger.Instance);

            Action act = () =>
                openApiDocument.CreateDocument(
                    new(coreSchemaRootNode, extensionSchemaRootNodes),
                    OpenApiDocument.OpenApiDocumentType.Resource
                );

            act.Should()
                .Throw<InvalidOperationException>()
                .WithMessage("*Null override entry*commonExtensionOverrides*");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_An_Extension_With_Insertion_Location_Invalid_Final_Segment
        : OpenApiDocumentExtensionTests
    {
        private static JsonNode ExtensionWithInvalidFinalSegmentOverride()
        {
            JsonObject exts = new()
            {
                ["EdFi_Contact"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["isSportsFan"] = new JsonObject { ["type"] = "boolean" },
                    },
                },
            };

            JsonArray commonOverrides =
            [
                new JsonObject
                {
                    ["insertionLocations"] = new JsonArray("$.properties.addresses.nonExistent"),
                    ["schemaFragment"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["sample"] = new JsonObject { ["type"] = "string" },
                        },
                    },
                },
            ];

            var builder = new ApiSchemaBuilder().WithStartProject("sample", "1.0.0");
            builder
                .WithStartResource("Contact", isResourceExtension: true)
                .WithResourceExtensionFragments("resources", exts)
                .WithCommonExtensionOverrides(commonOverrides)
                .WithEndResource();

            return builder.WithEndProject().AsSingleApiSchemaRootNode();
        }

        [Test]
        public void It_should_throw_when_final_segment_of_insertion_location_is_invalid()
        {
            JsonNode coreSchemaRootNode = CoreSchemaWithContactAndAddress();
            JsonNode[] extensionSchemaRootNodes = [ExtensionWithInvalidFinalSegmentOverride()];

            OpenApiDocument doc = new(NullLogger.Instance);
            var action = () =>
                doc.CreateDocument(
                    new(coreSchemaRootNode, extensionSchemaRootNodes),
                    OpenApiDocument.OpenApiDocumentType.Resource
                );

            action
                .Should()
                .Throw<InvalidOperationException>()
                .WithMessage("*Contact*")
                .WithMessage("*nonExistent*");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_An_Extension_With_Multiple_Insertion_Locations : OpenApiDocumentExtensionTests
    {
        private JsonNode openApiResourcesResult = new JsonObject();

        private static JsonNode CoreSchemaWithContactAddressAndName()
        {
            JsonObject contactNameSchema = new()
            {
                ["description"] = "Contact Name",
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["firstName"] = new JsonObject { ["type"] = "string" },
                    ["lastSurname"] = new JsonObject { ["type"] = "string" },
                },
            };

            JsonObject contactAddressSchema = new()
            {
                ["description"] = "Contact Address",
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["streetNumberName"] = new JsonObject { ["type"] = "string" },
                    ["city"] = new JsonObject { ["type"] = "string" },
                },
            };

            JsonObject contactSchema = new()
            {
                ["description"] = "Contact description",
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["contactUniqueId"] = new JsonObject { ["type"] = "string" },
                    ["addresses"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject { ["$ref"] = "#/components/schemas/EdFi_Contact_Address" },
                    },
                    ["name"] = new JsonObject { ["$ref"] = "#/components/schemas/EdFi_Contact_Name" },
                },
            };

            JsonObject schemas = new()
            {
                ["EdFi_Contact"] = contactSchema,
                ["EdFi_Contact_Address"] = contactAddressSchema,
                ["EdFi_Contact_Name"] = contactNameSchema,
            };

            var builder = new ApiSchemaBuilder()
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
                        ["components"] = new JsonObject { ["schemas"] = schemas },
                        ["paths"] = new JsonObject
                        {
                            ["/ed-fi/contacts"] = new JsonObject
                            {
                                ["get"] = new JsonObject
                                {
                                    ["description"] = "contacts get",
                                    ["tags"] = new JsonArray("contacts"),
                                },
                            },
                        },
                        ["tags"] = new JsonArray(
                            new JsonObject { ["name"] = "contacts", ["description"] = "Contacts" }
                        ),
                    }
                )
                .WithSimpleResource("Contact", false)
                .WithEndProject();

            return builder.AsSingleApiSchemaRootNode();
        }

        private static JsonNode ExtensionWithMultipleInsertionLocations()
        {
            JsonArray commonOverrides =
            [
                new JsonObject
                {
                    ["insertionLocations"] = new JsonArray(
                        "$.properties.addresses.items",
                        "$.properties.name"
                    ),
                    ["schemaFragment"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["sample"] = new JsonObject
                            {
                                ["type"] = "object",
                                ["properties"] = new JsonObject
                                {
                                    ["extraField"] = new JsonObject { ["type"] = "string" },
                                },
                            },
                        },
                    },
                },
            ];

            var builder = new ApiSchemaBuilder().WithStartProject("sample", "1.0.0");

            builder
                .WithStartResource("Contact", isResourceExtension: true)
                .WithNewExtensionResourceFragments("resources")
                .WithCommonExtensionOverrides(commonOverrides)
                .WithEndResource();

            return builder.WithEndProject().AsSingleApiSchemaRootNode();
        }

        [SetUp]
        public void Setup()
        {
            JsonNode coreSchemaRootNode = CoreSchemaWithContactAddressAndName();
            JsonNode[] extensionSchemaRootNodes = [ExtensionWithMultipleInsertionLocations()];

            OpenApiDocument openApiDocument = new(NullLogger.Instance);
            openApiResourcesResult = openApiDocument.CreateDocument(
                new(coreSchemaRootNode, extensionSchemaRootNodes),
                OpenApiDocument.OpenApiDocumentType.Resource
            );
        }

        [Test]
        public void It_should_add_ext_to_addresses_items_schema()
        {
            JsonPath jsonPath = JsonPath.Parse("$.components.schemas.EdFi_Contact_Address.properties._ext");
            PathResult pathResult = jsonPath.Evaluate(openApiResourcesResult);

            pathResult.Matches.Should().HaveCount(1);
            var extRef = pathResult.Matches[0].Value?.AsObject();
            extRef.Should().NotBeNull();
            extRef
                ?["$ref"]?.GetValue<string>()
                .Should()
                .Be("#/components/schemas/EdFi_Contact_AddressExtension");
        }

        [Test]
        public void It_should_add_ext_to_name_schema()
        {
            JsonPath jsonPath = JsonPath.Parse("$.components.schemas.EdFi_Contact_Name.properties._ext");
            PathResult pathResult = jsonPath.Evaluate(openApiResourcesResult);

            pathResult.Matches.Should().HaveCount(1);
            var extRef = pathResult.Matches[0].Value?.AsObject();
            extRef.Should().NotBeNull();
            extRef
                ?["$ref"]?.GetValue<string>()
                .Should()
                .Be("#/components/schemas/EdFi_Contact_NameExtension");
        }

        [Test]
        public void It_should_create_extension_schema_for_addresses_items()
        {
            JsonPath jsonPath = JsonPath.Parse("$.components.schemas.EdFi_Contact_AddressExtension");
            PathResult pathResult = jsonPath.Evaluate(openApiResourcesResult);

            pathResult.Matches.Should().HaveCount(1);
            var extensionSchema = pathResult.Matches[0].Value?.AsObject();
            extensionSchema.Should().NotBeNull();
            extensionSchema?["type"]?.GetValue<string>().Should().Be("object");
            extensionSchema?["properties"]?.AsObject().Should().ContainKey("sample");
        }

        [Test]
        public void It_should_create_extension_schema_for_name()
        {
            JsonPath jsonPath = JsonPath.Parse("$.components.schemas.EdFi_Contact_NameExtension");
            PathResult pathResult = jsonPath.Evaluate(openApiResourcesResult);

            pathResult.Matches.Should().HaveCount(1);
            var extensionSchema = pathResult.Matches[0].Value?.AsObject();
            extensionSchema.Should().NotBeNull();
            extensionSchema?["type"]?.GetValue<string>().Should().Be("object");
            extensionSchema?["properties"]?.AsObject().Should().ContainKey("sample");
        }

        [Test]
        public void It_should_create_project_extension_schema_for_name_with_extra_field()
        {
            JsonPath jsonPath = JsonPath.Parse(
                "$.components.schemas.sample_EdFi_Contact_NameExtension.properties.extraField"
            );
            PathResult pathResult = jsonPath.Evaluate(openApiResourcesResult);

            pathResult.Matches.Should().HaveCount(1);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_CommonExtensionOverrides_Exist_But_OpenApiFragments_Lacks_DocType_Key
        : OpenApiDocumentExtensionTests
    {
        private JsonNode _openApiDescriptorsResult = new JsonObject();
        private JsonNode _openApiResourcesResult = new JsonObject();

        private static JsonNode ExtensionWithResourceFragmentsOnly()
        {
            JsonArray commonOverrides =
            [
                new JsonObject
                {
                    ["insertionLocations"] = new JsonArray("$.properties.addresses.items"),
                    ["schemaFragment"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["sample"] = new JsonObject
                            {
                                ["type"] = "object",
                                ["properties"] = new JsonObject
                                {
                                    ["complex"] = new JsonObject { ["type"] = "string" },
                                },
                            },
                        },
                    },
                },
            ];

            var extensionBuilder = new ApiSchemaBuilder().WithStartProject("sample", "1.0.0");
            extensionBuilder
                .WithStartResource("Contact", isResourceExtension: true)
                .WithNewExtensionResourceFragments("resources")
                .WithCommonExtensionOverrides(commonOverrides)
                .WithEndResource();

            return extensionBuilder.WithEndProject().AsSingleApiSchemaRootNode();
        }

        [SetUp]
        public void Setup()
        {
            JsonNode coreSchemaRootNode = CoreSchemaWithContactAndAddress();
            JsonNode[] extensionSchemaRootNodes = [ExtensionWithResourceFragmentsOnly()];

            OpenApiDocument openApiDocument = new(NullLogger.Instance);

            _openApiDescriptorsResult = openApiDocument.CreateDocument(
                new(coreSchemaRootNode, extensionSchemaRootNodes),
                OpenApiDocument.OpenApiDocumentType.Descriptor
            );

            _openApiResourcesResult = openApiDocument.CreateDocument(
                new(coreSchemaRootNode, extensionSchemaRootNodes),
                OpenApiDocument.OpenApiDocumentType.Resource
            );
        }

        [Test]
        public void It_should_not_throw_when_generating_descriptor_doc_type()
        {
            _openApiDescriptorsResult.Should().NotBeNull();
        }

        [Test]
        public void It_should_add_ext_to_common_type_for_resource_doc_type()
        {
            JsonPath jsonPath = JsonPath.Parse("$.components.schemas.EdFi_Contact_Address.properties._ext");
            PathResult pathResult = jsonPath.Evaluate(_openApiResourcesResult);

            pathResult.Matches.Should().HaveCount(1);
            var extRef = pathResult.Matches[0].Value?.AsObject();
            extRef.Should().NotBeNull();
            extRef
                ?["$ref"]?.GetValue<string>()
                .Should()
                .Be("#/components/schemas/EdFi_Contact_AddressExtension");
        }

        [Test]
        public void It_should_create_extension_schema_for_resource_doc_type()
        {
            JsonPath jsonPath = JsonPath.Parse("$.components.schemas.EdFi_Contact_AddressExtension");
            PathResult pathResult = jsonPath.Evaluate(_openApiResourcesResult);

            pathResult.Matches.Should().HaveCount(1);
            pathResult.Matches[0].Value.Should().NotBeNull();
        }

        [Test]
        public void It_should_create_project_extension_schema_for_resource_doc_type()
        {
            JsonPath jsonPath = JsonPath.Parse("$.components.schemas.sample_EdFi_Contact_AddressExtension");
            PathResult pathResult = jsonPath.Evaluate(_openApiResourcesResult);

            pathResult.Matches.Should().HaveCount(1);
            var projectSchema = pathResult.Matches[0].Value?.AsObject();
            projectSchema.Should().NotBeNull();
            projectSchema?["properties"]?.AsObject().Should().ContainKey("complex");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Override_Array_Contains_Non_JsonObject_Entry : OpenApiDocumentExtensionTests
    {
        private ApiSchemaDocumentNodes _apiSchemaDocumentNodes = null!;
        private OpenApiDocument _openApiDocument = null!;

        [SetUp]
        public void Setup()
        {
            var coreSchemaRootNode = CoreSchemaWithContactAndAddress();

            var extensionBuilder = new ApiSchemaBuilder().WithStartProject("sample", "1.0.0");

            // Build a commonExtensionOverrides array that contains a non-JsonObject entry (a plain string)
            var overridesWithInvalidEntry = new JsonArray { JsonValue.Create("this-is-not-a-json-object")! };

            extensionBuilder
                .WithStartResource("Contact", isResourceExtension: true)
                .WithResourceExtensionFragments(
                    "resources",
                    new JsonObject
                    {
                        ["EdFi_Contact"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["extra"] = new JsonObject { ["type"] = "string" },
                            },
                        },
                    }
                )
                .WithCommonExtensionOverrides(overridesWithInvalidEntry)
                .WithEndResource();

            var extensionSchemaRootNode = extensionBuilder.WithEndProject().AsSingleApiSchemaRootNode();

            _apiSchemaDocumentNodes = new ApiSchemaDocumentNodes(
                coreSchemaRootNode,
                [extensionSchemaRootNode]
            );
            _openApiDocument = new OpenApiDocument(NullLogger.Instance);
        }

        [Test]
        public void It_should_throw_InvalidOperationException()
        {
            Action act = () =>
                _openApiDocument.CreateDocument(
                    _apiSchemaDocumentNodes,
                    OpenApiDocument.OpenApiDocumentType.Resource
                );

            act.Should()
                .Throw<InvalidOperationException>()
                .WithMessage("*Expected a JsonObject override entry*Contact*");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Override_Entry_Has_Empty_InsertionLocations_Array : OpenApiDocumentExtensionTests
    {
        private ApiSchemaDocumentNodes _apiSchemaDocumentNodes = null!;
        private OpenApiDocument _openApiDocument = null!;

        [SetUp]
        public void Setup()
        {
            var coreSchemaRootNode = CoreSchemaWithContactAndAddress();

            var extensionBuilder = new ApiSchemaBuilder().WithStartProject("sample", "1.0.0");

            extensionBuilder
                .WithStartResource("Contact", isResourceExtension: true)
                .WithResourceExtensionFragments(
                    "resources",
                    new JsonObject
                    {
                        ["EdFi_Contact"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["extra"] = new JsonObject { ["type"] = "string" },
                            },
                        },
                    }
                )
                .WithCommonExtensionOverrides([
                    new JsonObject
                    {
                        // Empty array — should throw
                        ["insertionLocations"] = new JsonArray(),
                        ["schemaFragment"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["sample"] = new JsonObject { ["type"] = "object" },
                            },
                        },
                    },
                ])
                .WithEndResource();

            var extensionSchemaRootNode = extensionBuilder.WithEndProject().AsSingleApiSchemaRootNode();

            _apiSchemaDocumentNodes = new ApiSchemaDocumentNodes(
                coreSchemaRootNode,
                [extensionSchemaRootNode]
            );
            _openApiDocument = new OpenApiDocument(NullLogger.Instance);
        }

        [Test]
        public void It_should_throw_InvalidOperationException()
        {
            Action act = () =>
                _openApiDocument.CreateDocument(
                    _apiSchemaDocumentNodes,
                    OpenApiDocument.OpenApiDocumentType.Resource
                );

            act.Should().Throw<InvalidOperationException>().WithMessage("*empty 'insertionLocations' array*");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Shared_Component_Multiple_Insertion_Locations : OpenApiDocumentExtensionTests
    {
        private JsonNode openApiResourcesResult = new JsonObject();

        private static JsonNode CoreSchemaWithContactAndSharedAddress()
        {
            JsonObject addressSchema = new()
            {
                ["description"] = "Address",
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["streetNumberName"] = new JsonObject { ["type"] = "string" },
                    ["city"] = new JsonObject { ["type"] = "string" },
                },
            };

            JsonObject contactSchema = new()
            {
                ["description"] = "Contact description",
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["contactUniqueId"] = new JsonObject { ["type"] = "string" },
                    ["mailingAddress"] = new JsonObject { ["$ref"] = "#/components/schemas/EdFi_Address" },
                    ["physicalAddress"] = new JsonObject { ["$ref"] = "#/components/schemas/EdFi_Address" },
                },
            };

            JsonObject schemas = new() { ["EdFi_Contact"] = contactSchema, ["EdFi_Address"] = addressSchema };

            var builder = new ApiSchemaBuilder()
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
                        ["components"] = new JsonObject { ["schemas"] = schemas },
                        ["paths"] = new JsonObject
                        {
                            ["/ed-fi/contacts"] = new JsonObject
                            {
                                ["get"] = new JsonObject
                                {
                                    ["description"] = "contacts get",
                                    ["tags"] = new JsonArray("contacts"),
                                },
                            },
                        },
                        ["tags"] = new JsonArray(
                            new JsonObject { ["name"] = "contacts", ["description"] = "Contacts" }
                        ),
                    }
                )
                .WithSimpleResource("Contact", false)
                .WithEndProject();

            return builder.AsSingleApiSchemaRootNode();
        }

        private static JsonNode ExtensionWithSharedComponentOverrides()
        {
            JsonArray commonOverrides =
            [
                new JsonObject
                {
                    ["insertionLocations"] = new JsonArray(
                        "$.properties.mailingAddress",
                        "$.properties.physicalAddress"
                    ),
                    ["schemaFragment"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["sample"] = new JsonObject
                            {
                                ["type"] = "object",
                                ["properties"] = new JsonObject
                                {
                                    ["someExtensionProperty"] = new JsonObject { ["type"] = "string" },
                                },
                            },
                        },
                    },
                },
            ];

            var builder = new ApiSchemaBuilder().WithStartProject("sample", "1.0.0");
            builder
                .WithStartResource("Contact", isResourceExtension: true)
                .WithNewExtensionResourceFragments("resources")
                .WithCommonExtensionOverrides(commonOverrides)
                .WithEndResource();

            return builder.WithEndProject().AsSingleApiSchemaRootNode();
        }

        [SetUp]
        public void Setup()
        {
            JsonNode coreSchemaRootNode = CoreSchemaWithContactAndSharedAddress();
            JsonNode[] extensionSchemaRootNodes = [ExtensionWithSharedComponentOverrides()];

            OpenApiDocument openApiDocument = new(NullLogger.Instance);
            openApiResourcesResult = openApiDocument.CreateDocument(
                new(coreSchemaRootNode, extensionSchemaRootNodes),
                OpenApiDocument.OpenApiDocumentType.Resource
            );
        }

        [Test]
        public void It_should_not_throw()
        {
            openApiResourcesResult.Should().NotBeNull();
        }

        [Test]
        public void It_should_create_extension_schema_once()
        {
            JsonPath jsonPath = JsonPath.Parse("$.components.schemas.EdFi_AddressExtension");
            PathResult pathResult = jsonPath.Evaluate(openApiResourcesResult);

            pathResult.Matches.Should().HaveCount(1);
            var extensionSchema = pathResult.Matches[0].Value?.AsObject();
            extensionSchema.Should().NotBeNull();
            extensionSchema?["type"]?.GetValue<string>().Should().Be("object");
            extensionSchema?["properties"]?.AsObject().Should().ContainKey("sample");
        }

        [Test]
        public void It_should_create_project_extension_schema_once()
        {
            JsonPath jsonPath = JsonPath.Parse("$.components.schemas.sample_EdFi_AddressExtension");
            PathResult pathResult = jsonPath.Evaluate(openApiResourcesResult);

            pathResult.Matches.Should().HaveCount(1);
            var projectSchema = pathResult.Matches[0].Value?.AsObject();
            projectSchema.Should().NotBeNull();
            projectSchema?["properties"]?.AsObject().Should().ContainKey("someExtensionProperty");
        }

        [Test]
        public void It_should_add_ext_to_shared_address_schema()
        {
            JsonPath jsonPath = JsonPath.Parse("$.components.schemas.EdFi_Address.properties._ext");
            PathResult pathResult = jsonPath.Evaluate(openApiResourcesResult);

            pathResult.Matches.Should().HaveCount(1);
            var extRef = pathResult.Matches[0].Value?.AsObject();
            extRef.Should().NotBeNull();
            extRef?["$ref"]?.GetValue<string>().Should().Be("#/components/schemas/EdFi_AddressExtension");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_An_Extension_With_Non_Object_Fragment_Property : OpenApiDocumentExtensionTests
    {
        private ApiSchemaDocumentNodes _apiSchemaDocumentNodes = null!;
        private OpenApiDocument _openApiDocument = null!;

        [SetUp]
        public void Setup()
        {
            var coreSchemaRootNode = CoreSchemaWithContactAndAddress();

            var extensionBuilder = new ApiSchemaBuilder().WithStartProject("sample", "1.0.0");

            extensionBuilder
                .WithStartResource("Contact", isResourceExtension: true)
                .WithResourceExtensionFragments(
                    "resources",
                    new JsonObject
                    {
                        ["EdFi_Contact"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["extra"] = new JsonObject { ["type"] = "string" },
                            },
                        },
                    }
                )
                .WithCommonExtensionOverrides([
                    new JsonObject
                    {
                        ["insertionLocations"] = new JsonArray("$.properties.addresses.items"),
                        ["schemaFragment"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                // Non-object value — should trigger the guard
                                ["sample"] = JsonValue.Create("not-an-object"),
                            },
                        },
                    },
                ])
                .WithEndResource();

            var extensionSchemaRootNode = extensionBuilder.WithEndProject().AsSingleApiSchemaRootNode();

            _apiSchemaDocumentNodes = new ApiSchemaDocumentNodes(
                coreSchemaRootNode,
                [extensionSchemaRootNode]
            );
            _openApiDocument = new OpenApiDocument(NullLogger.Instance);
        }

        [Test]
        public void It_should_throw_InvalidOperationException()
        {
            Action act = () =>
                _openApiDocument.CreateDocument(
                    _apiSchemaDocumentNodes,
                    OpenApiDocument.OpenApiDocumentType.Resource
                );

            act.Should()
                .Throw<InvalidOperationException>()
                .WithMessage("*fragment property*")
                .WithMessage("*not a valid JSON object*");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Extension_With_Only_CommonExtensionOverrides_No_Fragments
        : OpenApiDocumentExtensionTests
    {
        private JsonNode? _resourceSpec;
        private JsonNode? _descriptorSpec;

        private static JsonNode ExtensionWithOnlyCommonOverrides()
        {
            JsonArray commonOverrides =
            [
                new JsonObject
                {
                    ["insertionLocations"] = new JsonArray("$.properties.addresses.items"),
                    ["schemaFragment"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["sample"] = new JsonObject
                            {
                                ["type"] = "object",
                                ["properties"] = new JsonObject
                                {
                                    ["complex"] = new JsonObject { ["maxLength"] = 255, ["type"] = "string" },
                                    ["onBusRoute"] = new JsonObject { ["type"] = "boolean" },
                                },
                            },
                        },
                    },
                },
            ];

            var builder = new ApiSchemaBuilder().WithStartProject("sample", "1.0.0");

            builder
                .WithStartResource("Contact", isResourceExtension: true)
                .WithCommonExtensionOverrides(commonOverrides)
                .WithEndResource();

            return builder.WithEndProject().AsSingleApiSchemaRootNode();
        }

        [SetUp]
        public void Setup()
        {
            JsonNode coreSchemaRootNode = CoreSchemaWithContactAndAddress();
            JsonNode[] extensionSchemaRootNodes = [ExtensionWithOnlyCommonOverrides()];

            OpenApiDocument openApiDocument = new(NullLogger.Instance);

            _resourceSpec = openApiDocument.CreateDocument(
                new(coreSchemaRootNode, extensionSchemaRootNodes),
                OpenApiDocument.OpenApiDocumentType.Resource
            );

            _descriptorSpec = openApiDocument.CreateDocument(
                new(coreSchemaRootNode, extensionSchemaRootNodes),
                OpenApiDocument.OpenApiDocumentType.Descriptor
            );
        }

        [Test]
        public void It_should_add_ext_to_target_component_in_resource_spec()
        {
            JsonPath jsonPath = JsonPath.Parse("$.components.schemas.EdFi_Contact_Address.properties._ext");
            PathResult pathResult = jsonPath.Evaluate(_resourceSpec!);

            pathResult.Matches.Should().HaveCount(1);
            var extRef = pathResult.Matches[0].Value?.AsObject();
            extRef.Should().NotBeNull();
            extRef
                ?["$ref"]?.GetValue<string>()
                .Should()
                .Be("#/components/schemas/EdFi_Contact_AddressExtension");
        }

        [Test]
        public void It_should_create_extension_schema_in_resource_spec()
        {
            JsonPath jsonPath = JsonPath.Parse("$.components.schemas.EdFi_Contact_AddressExtension");
            PathResult pathResult = jsonPath.Evaluate(_resourceSpec!);

            pathResult.Matches.Should().HaveCount(1);
            var extensionSchema = pathResult.Matches[0].Value?.AsObject();
            extensionSchema.Should().NotBeNull();
            extensionSchema?["type"]?.GetValue<string>().Should().Be("object");
            extensionSchema?["properties"]?.AsObject().Should().ContainKey("sample");

            var sampleRef = extensionSchema?["properties"]?["sample"]?.AsObject();
            sampleRef
                ?["$ref"]?.GetValue<string>()
                .Should()
                .Be("#/components/schemas/sample_EdFi_Contact_AddressExtension");
        }

        [Test]
        public void It_should_create_project_extension_schema_in_resource_spec()
        {
            JsonPath jsonPath = JsonPath.Parse("$.components.schemas.sample_EdFi_Contact_AddressExtension");
            PathResult pathResult = jsonPath.Evaluate(_resourceSpec!);

            pathResult.Matches.Should().HaveCount(1);
            var projectSchema = pathResult.Matches[0].Value?.AsObject();
            projectSchema.Should().NotBeNull();
            projectSchema?["type"]?.GetValue<string>().Should().Be("object");
            projectSchema?["properties"]?.AsObject().Should().ContainKey("complex");
            projectSchema?["properties"]?.AsObject().Should().ContainKey("onBusRoute");
        }

        [Test]
        public void It_should_not_add_extension_schemas_to_descriptor_spec()
        {
            _descriptorSpec.Should().NotBeNull();

            JsonPath extensionSchemaPath = JsonPath.Parse(
                "$.components.schemas.EdFi_Contact_AddressExtension"
            );
            PathResult extensionResult = extensionSchemaPath.Evaluate(_descriptorSpec!);
            extensionResult.Matches.Should().HaveCount(0);

            JsonPath projectSchemaPath = JsonPath.Parse(
                "$.components.schemas.sample_EdFi_Contact_AddressExtension"
            );
            PathResult projectResult = projectSchemaPath.Evaluate(_descriptorSpec!);
            projectResult.Matches.Should().HaveCount(0);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Non_Resource_Extension_With_CommonExtensionOverrides
        : OpenApiDocumentExtensionTests
    {
        private JsonNode? _resourceSpec;

        private static JsonNode NonResourceExtensionWithCommonOverrides()
        {
            JsonArray commonOverrides =
            [
                new JsonObject
                {
                    ["insertionLocations"] = new JsonArray("$.properties.addresses.items"),
                    ["schemaFragment"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["sample"] = new JsonObject
                            {
                                ["type"] = "object",
                                ["properties"] = new JsonObject
                                {
                                    ["onBusRoute"] = new JsonObject { ["type"] = "boolean" },
                                },
                            },
                        },
                    },
                },
            ];

            var builder = new ApiSchemaBuilder().WithStartProject("sample", "1.0.0");

            builder
                .WithStartResource("Contact", isResourceExtension: false)
                .WithCommonExtensionOverrides(commonOverrides)
                .WithEndResource();

            return builder.WithEndProject().AsSingleApiSchemaRootNode();
        }

        [SetUp]
        public void Setup()
        {
            JsonNode coreSchemaRootNode = CoreSchemaWithContactAndAddress();
            JsonNode[] extensionSchemaRootNodes = [NonResourceExtensionWithCommonOverrides()];

            OpenApiDocument openApiDocument = new(NullLogger.Instance);

            _resourceSpec = openApiDocument.CreateDocument(
                new(coreSchemaRootNode, extensionSchemaRootNodes),
                OpenApiDocument.OpenApiDocumentType.Resource
            );
        }

        [Test]
        public void It_should_not_add_ext_to_target_component()
        {
            JsonPath jsonPath = JsonPath.Parse("$.components.schemas.EdFi_Contact_Address.properties._ext");
            PathResult pathResult = jsonPath.Evaluate(_resourceSpec!);

            pathResult.Matches.Should().HaveCount(0);
        }

        [Test]
        public void It_should_not_create_extension_schemas()
        {
            JsonPath extensionSchemaPath = JsonPath.Parse(
                "$.components.schemas.EdFi_Contact_AddressExtension"
            );
            PathResult extensionResult = extensionSchemaPath.Evaluate(_resourceSpec!);
            extensionResult.Matches.Should().HaveCount(0);

            JsonPath projectSchemaPath = JsonPath.Parse(
                "$.components.schemas.sample_EdFi_Contact_AddressExtension"
            );
            PathResult projectResult = projectSchemaPath.Evaluate(_resourceSpec!);
            projectResult.Matches.Should().HaveCount(0);
        }
    }
}
