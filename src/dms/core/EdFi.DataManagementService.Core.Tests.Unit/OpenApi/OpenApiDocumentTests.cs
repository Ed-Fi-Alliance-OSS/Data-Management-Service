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
    private static JsonObject ChangeQueriesDocument(
        string title,
        bool includeAvailableChangeVersionsPath = true
    )
    {
        JsonObject paths = [];
        if (includeAvailableChangeVersionsPath)
        {
            paths["/availableChangeVersions"] = new JsonObject
            {
                ["get"] = new JsonObject
                {
                    ["description"] = "availableChangeVersions get description",
                    ["tags"] = new JsonArray("changeQueries"),
                },
            };
        }

        return new JsonObject
        {
            ["openapi"] = "3.0.1",
            ["info"] = new JsonObject { ["title"] = title, ["version"] = "5.0.0" },
            ["paths"] = paths,
            ["components"] = new JsonObject { ["schemas"] = new JsonObject() },
            ["tags"] = new JsonArray
            {
                new JsonObject { ["name"] = "changeQueries", ["description"] = "Change Queries" },
            },
        };
    }

    private static JsonObject BaseOpenApiDocument(
        string title,
        JsonObject paths,
        JsonObject schemas,
        JsonArray tags
    )
    {
        return new JsonObject
        {
            ["openapi"] = "3.0.1",
            ["info"] = new JsonObject { ["title"] = title, ["version"] = "5.0.0" },
            ["paths"] = paths,
            ["components"] = new JsonObject { ["schemas"] = schemas },
            ["tags"] = tags,
        };
    }

    private static JsonObject ComponentSchemas(params string[] schemaNames)
    {
        JsonObject schemas = [];

        foreach (string schemaName in schemaNames)
        {
            schemas[schemaName] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
        }

        return schemas;
    }

    private static JsonArray Tags(params string[] tagNames)
    {
        return new JsonArray(
            tagNames
                .Select(tagName => new JsonObject { ["name"] = tagName, ["description"] = $"{tagName} tag" })
                .ToArray()
        );
    }

    private static JsonObject GetPath(
        string tagName,
        string responseSchemaName,
        string[] parameterNames,
        string[]? domains = null
    )
    {
        JsonObject path = new()
        {
            ["get"] = new JsonObject
            {
                ["tags"] = new JsonArray(tagName),
                ["parameters"] = new JsonArray(parameterNames.Select(Parameter).ToArray()),
                ["responses"] = new JsonObject
                {
                    ["200"] = new JsonObject
                    {
                        ["description"] = "OK",
                        ["content"] = new JsonObject
                        {
                            ["application/json"] = new JsonObject
                            {
                                ["schema"] = new JsonObject
                                {
                                    ["type"] = "array",
                                    ["items"] = new JsonObject
                                    {
                                        ["$ref"] = $"#/components/schemas/{responseSchemaName}",
                                    },
                                },
                            },
                        },
                    },
                },
            },
        };

        if (domains is not null)
        {
            path["x-Ed-Fi-domains"] = new JsonArray(
                domains.Select(domain => JsonValue.Create(domain)).ToArray()
            );
        }

        return path;
    }

    private static JsonObject Parameter(string parameterName)
    {
        return new JsonObject
        {
            ["name"] = parameterName,
            ["in"] = "query",
            ["schema"] = new JsonObject { ["type"] = "integer", ["format"] = "int64" },
        };
    }

    private static string[] ParameterNames(JsonNode openApiSpecification, string path)
    {
        return openApiSpecification["paths"]![path]!["get"]!["parameters"]!
            .AsArray()
            .Select(parameter => parameter!["name"]!.GetValue<string>())
            .ToArray();
    }

    private static string ResponseSchemaRef(JsonNode openApiSpecification, string path)
    {
        return openApiSpecification["paths"]![path]!["get"]!["responses"]!["200"]!["content"]![
            "application/json"
        ]!["schema"]!["items"]!["$ref"]!.GetValue<string>();
    }

    private static string[] TagNames(JsonNode openApiSpecification)
    {
        return openApiSpecification["tags"]!
            .AsArray()
            .Select(tag => tag!["name"]!.GetValue<string>())
            .ToArray();
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
                    "title": "Ed-Fi API",
                    "version": "8.0.0"
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
                    "title": "Ed-Fi API",
                    "version": "8.0.0"
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
    public class Given_A_Core_Schema_With_A_Change_Queries_Base_Document : OpenApiDocumentTests
    {
        private JsonNode? changeQueriesResult;
        private ApiSchemaDocumentNodes apiSchemaDocumentNodes = new(new JsonObject(), []);

        [SetUp]
        public void Setup()
        {
            apiSchemaDocumentNodes = new ApiSchemaBuilder()
                .WithStartProject("ed-fi", "5.0.0")
                .WithOpenApiBaseDocuments(changeQueriesDoc: ChangeQueriesDocument("Ed-Fi Change Queries API"))
                .WithEndProject()
                .AsApiSchemaNodes();

            OpenApiDocument openApiDocument = new(NullLogger.Instance);
            changeQueriesResult = openApiDocument.CreateChangeQueriesDocument(apiSchemaDocumentNodes);
        }

        [Test]
        public void It_should_return_the_core_change_queries_document()
        {
            changeQueriesResult.Should().NotBeNull();
            changeQueriesResult!["info"]!["title"]!
                .GetValue<string>()
                .Should()
                .Be("Ed-Fi Change Queries API");
            changeQueriesResult["paths"]!.AsObject().Should().ContainKey("/availableChangeVersions");
        }

        [Test]
        public void It_should_return_a_deep_clone()
        {
            changeQueriesResult.Should().NotBeNull();
            changeQueriesResult!["info"]!["title"] = "Mutated Change Queries API";

            JsonNode rawChangeQueriesDocument = apiSchemaDocumentNodes.CoreApiSchemaRootNode[
                "projectSchema"
            ]!["openApiBaseDocuments"]!["changeQueries"]!;

            rawChangeQueriesDocument["info"]!["title"]!
                .GetValue<string>()
                .Should()
                .Be("Ed-Fi Change Queries API");

            OpenApiDocument openApiDocument = new(NullLogger.Instance);
            JsonNode? secondResult = openApiDocument.CreateChangeQueriesDocument(apiSchemaDocumentNodes);

            secondResult.Should().NotBeNull();
            secondResult!["info"]!["title"]!.GetValue<string>().Should().Be("Ed-Fi Change Queries API");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Core_Schema_With_A_Pathless_Change_Queries_Base_Document : OpenApiDocumentTests
    {
        private JsonNode? changeQueriesResult;

        [SetUp]
        public void Setup()
        {
            var apiSchemaDocumentNodes = new ApiSchemaBuilder()
                .WithStartProject("ed-fi", "5.0.0")
                .WithOpenApiBaseDocuments(
                    changeQueriesDoc: ChangeQueriesDocument(
                        "Ed-Fi Pathless Change Queries API",
                        includeAvailableChangeVersionsPath: false
                    )
                )
                .WithEndProject()
                .AsApiSchemaNodes();

            OpenApiDocument openApiDocument = new(NullLogger.Instance);
            changeQueriesResult = openApiDocument.CreateChangeQueriesDocument(apiSchemaDocumentNodes);
        }

        [Test]
        public void It_should_treat_the_document_as_present()
        {
            changeQueriesResult.Should().NotBeNull();
            changeQueriesResult!["paths"]!.AsObject().Should().BeEmpty();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Core_Schema_Without_A_Change_Queries_Base_Document : OpenApiDocumentTests
    {
        private JsonNode? changeQueriesResult;

        [SetUp]
        public void Setup()
        {
            var apiSchemaDocumentNodes = new ApiSchemaBuilder()
                .WithStartProject("ed-fi", "5.0.0")
                .WithOpenApiBaseDocuments()
                .WithEndProject()
                .AsApiSchemaNodes();

            OpenApiDocument openApiDocument = new(NullLogger.Instance);
            changeQueriesResult = openApiDocument.CreateChangeQueriesDocument(apiSchemaDocumentNodes);
        }

        [Test]
        public void It_should_return_null()
        {
            changeQueriesResult.Should().BeNull();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_An_Extension_Schema_With_A_Change_Queries_Base_Document : OpenApiDocumentTests
    {
        private JsonNode? changeQueriesResult;

        [SetUp]
        public void Setup()
        {
            var apiSchemaDocumentNodes = new ApiSchemaBuilder()
                .WithStartProject("ed-fi", "5.0.0")
                .WithOpenApiBaseDocuments()
                .WithEndProject()
                .WithStartProject("Sample", "1.0.0")
                .WithOpenApiBaseDocuments(
                    changeQueriesDoc: ChangeQueriesDocument("Sample Change Queries API")
                )
                .WithEndProject()
                .AsApiSchemaNodes();

            OpenApiDocument openApiDocument = new(NullLogger.Instance);
            changeQueriesResult = openApiDocument.CreateChangeQueriesDocument(apiSchemaDocumentNodes);
        }

        [Test]
        public void It_should_ignore_the_extension_document()
        {
            changeQueriesResult.Should().BeNull();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Core_And_Extension_Change_Queries_Base_Documents : OpenApiDocumentTests
    {
        private JsonNode? changeQueriesResult;

        [SetUp]
        public void Setup()
        {
            var apiSchemaDocumentNodes = new ApiSchemaBuilder()
                .WithStartProject("ed-fi", "5.0.0")
                .WithOpenApiBaseDocuments(changeQueriesDoc: ChangeQueriesDocument("Ed-Fi Change Queries API"))
                .WithEndProject()
                .WithStartProject("Sample", "1.0.0")
                .WithOpenApiBaseDocuments(
                    changeQueriesDoc: ChangeQueriesDocument("Sample Change Queries API")
                )
                .WithEndProject()
                .AsApiSchemaNodes();

            OpenApiDocument openApiDocument = new(NullLogger.Instance);
            changeQueriesResult = openApiDocument.CreateChangeQueriesDocument(apiSchemaDocumentNodes);
        }

        [Test]
        public void It_should_return_only_the_core_document()
        {
            changeQueriesResult.Should().NotBeNull();
            changeQueriesResult!["info"]!["title"]!
                .GetValue<string>()
                .Should()
                .Be("Ed-Fi Change Queries API");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Core_Resource_And_Descriptor_OpenApi_With_Change_Query_Paths : OpenApiDocumentTests
    {
        private JsonNode openApiResourcesResult = new JsonObject();
        private JsonNode openApiDescriptorsResult = new JsonObject();

        [SetUp]
        public void Setup()
        {
            JsonObject resourcePaths = new()
            {
                ["/ed-fi/students"] = GetPath(
                    "students",
                    "EdFi_Student",
                    ["minChangeVersion", "maxChangeVersion"]
                ),
                ["/ed-fi/students/deletes"] = GetPath(
                    "students",
                    "EdFi_DeletedResource",
                    ["minChangeVersion", "maxChangeVersion", "offset", "limit", "totalCount"]
                ),
                ["/ed-fi/students/keyChanges"] = GetPath(
                    "students",
                    "EdFi_StudentKeyChange",
                    ["minChangeVersion", "maxChangeVersion", "offset", "limit", "totalCount"]
                ),
                ["/ed-fi/schoolYearTypes/deletes"] = GetPath(
                    "schoolYearTypes",
                    "EdFi_DeletedResource",
                    ["minChangeVersion", "maxChangeVersion", "offset", "limit", "totalCount"]
                ),
                ["/ed-fi/schoolYearTypes/keyChanges"] = GetPath(
                    "schoolYearTypes",
                    "EdFi_SchoolYearTypeKeyChange",
                    ["minChangeVersion", "maxChangeVersion", "offset", "limit", "totalCount"]
                ),
            };

            JsonObject descriptorPaths = new()
            {
                ["/ed-fi/accommodationDescriptors"] = GetPath(
                    "accommodationDescriptors",
                    "EdFi_AccommodationDescriptor",
                    ["minChangeVersion", "maxChangeVersion"]
                ),
                ["/ed-fi/accommodationDescriptors/deletes"] = GetPath(
                    "accommodationDescriptors",
                    "EdFi_DeletedResource",
                    ["minChangeVersion", "maxChangeVersion", "offset", "limit", "totalCount"]
                ),
                ["/ed-fi/accommodationDescriptors/keyChanges"] = GetPath(
                    "accommodationDescriptors",
                    "EdFi_DescriptorKeyChange",
                    ["minChangeVersion", "maxChangeVersion", "offset", "limit", "totalCount"]
                ),
            };

            var apiSchemaDocumentNodes = new ApiSchemaBuilder()
                .WithStartProject("ed-fi", "5.0.0")
                .WithOpenApiBaseDocuments(
                    resourcesDoc: BaseOpenApiDocument(
                        "Ed-Fi Resources API",
                        resourcePaths,
                        ComponentSchemas(
                            "EdFi_Student",
                            "EdFi_DeletedResource",
                            "EdFi_StudentKeyChange",
                            "EdFi_SchoolYearTypeKeyChange"
                        ),
                        Tags("students", "schoolYearTypes")
                    ),
                    descriptorsDoc: BaseOpenApiDocument(
                        "Ed-Fi Descriptors API",
                        descriptorPaths,
                        ComponentSchemas(
                            "EdFi_AccommodationDescriptor",
                            "EdFi_DeletedResource",
                            "EdFi_DescriptorKeyChange"
                        ),
                        Tags("accommodationDescriptors")
                    )
                )
                .WithEndProject()
                .AsApiSchemaNodes();

            OpenApiDocument openApiDocument = new(NullLogger.Instance);
            openApiResourcesResult = openApiDocument.CreateDocument(
                apiSchemaDocumentNodes,
                OpenApiDocument.OpenApiDocumentType.Resource
            );
            openApiDescriptorsResult = openApiDocument.CreateDocument(
                apiSchemaDocumentNodes,
                OpenApiDocument.OpenApiDocumentType.Descriptor
            );
        }

        [Test]
        public void It_should_preserve_live_change_version_filters()
        {
            ParameterNames(openApiResourcesResult, "/ed-fi/students")
                .Should()
                .Contain(["minChangeVersion", "maxChangeVersion"]);

            ParameterNames(openApiDescriptorsResult, "/ed-fi/accommodationDescriptors")
                .Should()
                .Contain(["minChangeVersion", "maxChangeVersion"]);
        }

        [Test]
        public void It_should_preserve_resource_and_school_year_type_change_query_paths()
        {
            JsonObject resourcePaths = openApiResourcesResult["paths"]!.AsObject();
            resourcePaths.Should().ContainKey("/ed-fi/students/deletes");
            resourcePaths.Should().ContainKey("/ed-fi/students/keyChanges");
            resourcePaths.Should().ContainKey("/ed-fi/schoolYearTypes/deletes");
            resourcePaths.Should().ContainKey("/ed-fi/schoolYearTypes/keyChanges");
            resourcePaths.Should().NotContainKey("/availableChangeVersions");

            ParameterNames(openApiResourcesResult, "/ed-fi/students/deletes")
                .Should()
                .Contain(["minChangeVersion", "maxChangeVersion", "offset", "limit", "totalCount"]);

            ResponseSchemaRef(openApiResourcesResult, "/ed-fi/students/keyChanges")
                .Should()
                .Be("#/components/schemas/EdFi_StudentKeyChange");

            openApiResourcesResult["components"]!["schemas"]!
                .AsObject()
                .Should()
                .ContainKeys("EdFi_StudentKeyChange", "EdFi_SchoolYearTypeKeyChange");

            TagNames(openApiResourcesResult).Should().Contain(["students", "schoolYearTypes"]);
        }

        [Test]
        public void It_should_preserve_descriptor_change_query_paths()
        {
            JsonObject descriptorPaths = openApiDescriptorsResult["paths"]!.AsObject();
            descriptorPaths.Should().ContainKey("/ed-fi/accommodationDescriptors/deletes");
            descriptorPaths.Should().ContainKey("/ed-fi/accommodationDescriptors/keyChanges");

            ParameterNames(openApiDescriptorsResult, "/ed-fi/accommodationDescriptors/keyChanges")
                .Should()
                .Contain(["minChangeVersion", "maxChangeVersion", "offset", "limit", "totalCount"]);

            ResponseSchemaRef(openApiDescriptorsResult, "/ed-fi/accommodationDescriptors/keyChanges")
                .Should()
                .Be("#/components/schemas/EdFi_DescriptorKeyChange");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Extension_Resource_And_Descriptor_OpenApi_With_Change_Query_Paths
        : OpenApiDocumentTests
    {
        private JsonNode openApiResourcesResult = new JsonObject();
        private JsonNode openApiDescriptorsResult = new JsonObject();

        [SetUp]
        public void Setup()
        {
            JsonObject extensionResourcePaths = new()
            {
                ["/sample/staffAbsenceEvents"] = GetPath(
                    "staffAbsenceEvents",
                    "Sample_StaffAbsenceEvent",
                    ["minChangeVersion", "maxChangeVersion"]
                ),
                ["/sample/staffAbsenceEvents/deletes"] = GetPath(
                    "staffAbsenceEvents",
                    "EdFi_DeletedResource",
                    ["minChangeVersion", "maxChangeVersion", "offset", "limit", "totalCount"]
                ),
                ["/sample/staffAbsenceEvents/keyChanges"] = GetPath(
                    "staffAbsenceEvents",
                    "Sample_StaffAbsenceEventKeyChange",
                    ["minChangeVersion", "maxChangeVersion", "offset", "limit", "totalCount"]
                ),
            };

            JsonObject extensionDescriptorPaths = new()
            {
                ["/sample/absenceReasonDescriptors"] = GetPath(
                    "absenceReasonDescriptors",
                    "Sample_AbsenceReasonDescriptor",
                    ["minChangeVersion", "maxChangeVersion"]
                ),
                ["/sample/absenceReasonDescriptors/deletes"] = GetPath(
                    "absenceReasonDescriptors",
                    "EdFi_DeletedResource",
                    ["minChangeVersion", "maxChangeVersion", "offset", "limit", "totalCount"]
                ),
                ["/sample/absenceReasonDescriptors/keyChanges"] = GetPath(
                    "absenceReasonDescriptors",
                    "Sample_AbsenceReasonDescriptorKeyChange",
                    ["minChangeVersion", "maxChangeVersion", "offset", "limit", "totalCount"]
                ),
            };

            var apiSchemaDocumentNodes = new ApiSchemaBuilder()
                .WithStartProject("ed-fi", "5.0.0")
                .WithOpenApiBaseDocuments(
                    resourcesDoc: BaseOpenApiDocument(
                        "Ed-Fi Resources API",
                        [],
                        ComponentSchemas("EdFi_DeletedResource"),
                        []
                    ),
                    descriptorsDoc: BaseOpenApiDocument(
                        "Ed-Fi Descriptors API",
                        [],
                        ComponentSchemas("EdFi_DeletedResource"),
                        []
                    )
                )
                .WithEndProject()
                .WithStartProject("Sample", "1.0.0")
                .WithStartResource("StaffAbsenceEvent")
                .WithNewExtensionResourceFragments(
                    "resources",
                    ComponentSchemas("Sample_StaffAbsenceEvent", "Sample_StaffAbsenceEventKeyChange"),
                    extensionResourcePaths,
                    Tags("staffAbsenceEvents")
                )
                .WithEndResource()
                .WithStartResource("AbsenceReasonDescriptor", isDescriptor: true)
                .WithNewExtensionResourceFragments(
                    "descriptors",
                    ComponentSchemas(
                        "Sample_AbsenceReasonDescriptor",
                        "Sample_AbsenceReasonDescriptorKeyChange"
                    ),
                    extensionDescriptorPaths,
                    Tags("absenceReasonDescriptors")
                )
                .WithEndResource()
                .WithEndProject()
                .AsApiSchemaNodes();

            OpenApiDocument openApiDocument = new(NullLogger.Instance);
            openApiResourcesResult = openApiDocument.CreateDocument(
                apiSchemaDocumentNodes,
                OpenApiDocument.OpenApiDocumentType.Resource
            );
            openApiDescriptorsResult = openApiDocument.CreateDocument(
                apiSchemaDocumentNodes,
                OpenApiDocument.OpenApiDocumentType.Descriptor
            );
        }

        [Test]
        public void It_should_preserve_extension_resource_change_query_paths()
        {
            JsonObject resourcePaths = openApiResourcesResult["paths"]!.AsObject();
            resourcePaths.Should().ContainKey("/sample/staffAbsenceEvents");
            resourcePaths.Should().ContainKey("/sample/staffAbsenceEvents/deletes");
            resourcePaths.Should().ContainKey("/sample/staffAbsenceEvents/keyChanges");

            ParameterNames(openApiResourcesResult, "/sample/staffAbsenceEvents/deletes")
                .Should()
                .Contain(["minChangeVersion", "maxChangeVersion", "offset", "limit", "totalCount"]);

            ResponseSchemaRef(openApiResourcesResult, "/sample/staffAbsenceEvents/keyChanges")
                .Should()
                .Be("#/components/schemas/Sample_StaffAbsenceEventKeyChange");
        }

        [Test]
        public void It_should_preserve_extension_descriptor_change_query_paths()
        {
            JsonObject descriptorPaths = openApiDescriptorsResult["paths"]!.AsObject();
            descriptorPaths.Should().ContainKey("/sample/absenceReasonDescriptors");
            descriptorPaths.Should().ContainKey("/sample/absenceReasonDescriptors/deletes");
            descriptorPaths.Should().ContainKey("/sample/absenceReasonDescriptors/keyChanges");

            ParameterNames(openApiDescriptorsResult, "/sample/absenceReasonDescriptors/keyChanges")
                .Should()
                .Contain(["minChangeVersion", "maxChangeVersion", "offset", "limit", "totalCount"]);

            ResponseSchemaRef(openApiDescriptorsResult, "/sample/absenceReasonDescriptors/keyChanges")
                .Should()
                .Be("#/components/schemas/Sample_AbsenceReasonDescriptorKeyChange");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Change_Query_Paths_With_Domain_Filtering : OpenApiDocumentTests
    {
        private JsonNode openApiResourcesResult = new JsonObject();

        [SetUp]
        public void Setup()
        {
            JsonObject resourcePaths = new()
            {
                ["/ed-fi/academicWeeks/deletes"] = GetPath(
                    "academicWeeks",
                    "EdFi_DeletedResource",
                    ["minChangeVersion", "maxChangeVersion", "offset", "limit", "totalCount"],
                    ["SchoolCalendar"]
                ),
                ["/ed-fi/calendars/keyChanges"] = GetPath(
                    "calendars",
                    "EdFi_CalendarKeyChange",
                    ["minChangeVersion", "maxChangeVersion", "offset", "limit", "totalCount"],
                    ["SchoolCalendar"]
                ),
                ["/ed-fi/students/keyChanges"] = GetPath(
                    "students",
                    "EdFi_StudentKeyChange",
                    ["minChangeVersion", "maxChangeVersion", "offset", "limit", "totalCount"],
                    ["SchoolCalendar", "Enrollment"]
                ),
            };

            var apiSchemaDocumentNodes = new ApiSchemaBuilder()
                .WithStartProject("ed-fi", "5.0.0")
                .WithOpenApiBaseDocuments(
                    resourcesDoc: BaseOpenApiDocument(
                        "Ed-Fi Resources API",
                        resourcePaths,
                        ComponentSchemas(
                            "EdFi_DeletedResource",
                            "EdFi_CalendarKeyChange",
                            "EdFi_StudentKeyChange"
                        ),
                        Tags("academicWeeks", "calendars", "students")
                    )
                )
                .WithEndProject()
                .AsApiSchemaNodes();

            OpenApiDocument openApiDocument = new(NullLogger.Instance, ["SchoolCalendar"]);
            openApiResourcesResult = openApiDocument.CreateDocument(
                apiSchemaDocumentNodes,
                OpenApiDocument.OpenApiDocumentType.Resource
            );
        }

        [Test]
        public void It_should_apply_path_domain_filtering_to_deletes_and_key_changes()
        {
            JsonObject resourcePaths = openApiResourcesResult["paths"]!.AsObject();
            resourcePaths.Should().NotContainKey("/ed-fi/academicWeeks/deletes");
            resourcePaths.Should().NotContainKey("/ed-fi/calendars/keyChanges");
            resourcePaths.Should().ContainKey("/ed-fi/students/keyChanges");
        }

        [Test]
        public void It_should_keep_multi_domain_change_query_paths_until_all_valid_domains_are_excluded()
        {
            JsonObject resourcePaths = openApiResourcesResult["paths"]!.AsObject();
            resourcePaths.Should().ContainKey("/ed-fi/students/keyChanges");

            ResponseSchemaRef(openApiResourcesResult, "/ed-fi/students/keyChanges")
                .Should()
                .Be("#/components/schemas/EdFi_StudentKeyChange");

            openApiResourcesResult["components"]!["schemas"]!
                .AsObject()
                .Should()
                .ContainKey("EdFi_StudentKeyChange");
        }

        [Test]
        public void It_should_remove_only_tags_unused_after_change_query_path_filtering()
        {
            string[] resultTags = TagNames(openApiResourcesResult);

            resultTags.Should().NotContain("academicWeeks");
            resultTags.Should().NotContain("calendars");
            resultTags.Should().Contain("students");
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
