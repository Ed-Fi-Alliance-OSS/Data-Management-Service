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
using static EdFi.DataManagementService.Core.Tests.Unit.OpenApi.OpenApiDocumentTestBase;

namespace EdFi.DataManagementService.Core.Tests.Unit.OpenApi;

public class OpenApiDocumentTests
{
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
}
