// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.OpenApiGenerator.Tests.Integration;

[TestFixture]
public class OpenApiGeneratorTests
{
    [TestFixture]
    public class Given_A_Non_Valid_Paths : OpenApiGeneratorTests
    {
        private ILogger<Services.OpenApiGenerator> _fakeLogger = null!;
        private Services.OpenApiGenerator _generator = null!;

        [SetUp]
        public void SetUp()
        {
            // Create a fake logger
            _fakeLogger = A.Fake<ILogger<Services.OpenApiGenerator>>();
            _generator = new Services.OpenApiGenerator(_fakeLogger);
        }

        [Test]
        public void Should_throw_ArgumentException_when_paths_are_invalid()
        {
            // Act & Assert
            var ex = Assert.Throws<ArgumentException>(() => _generator.Generate("", ""));

            Assert.That(ex?.Message, Is.EqualTo("Core schema path is required."));
        }
    }

    [TestFixture]
    public class Given_Invalid_Values_For_Core_And_Extension : OpenApiGeneratorTests
    {
        private ILogger<Services.OpenApiGenerator> _fakeLogger = null!;
        private Services.OpenApiGenerator _generator = null!;
        private string coreSchemaJson = string.Empty;
        private string extensionSchemaJson = string.Empty;

        [SetUp]
        public void SetUp()
        {
            // Create a fake logger
            _fakeLogger = A.Fake<ILogger<Services.OpenApiGenerator>>();
            _generator = new Services.OpenApiGenerator(_fakeLogger);

            coreSchemaJson =
                @"
                {
                    ""apiSchemaVersion"": ""5.2.0"",
                    ""projectSchema"": {
                        ""description"": ""The Ed-Fi Data Standard v5.2.0"",
                        ""isExtensionProject"": false,
                        ""abstractResources"": {},
                        ""caseInsensitiveEndpointNameMapping"": {},
                        ""resourceNameMapping"": {},
                        ""resourceSchemas"": {},
                        ""openApiBaseDocuments"": {
                            ""descriptors"": {
                                ""openapi"": ""3.0.1"",
                                ""info"": {
                                    ""title"": ""Ed-Fi Descriptors API"",
                                    ""version"": ""5.2.0""
                                },
                                ""components"": {
                                    ""parameters"": {
                                        ""limit"": {
                                            ""in"": ""query"",
                                            ""name"": ""limit"",
                                            ""schema"": { ""type"": ""integer"" }
                                        },
                                        ""numberOfPartitions"": {
                                            ""in"": ""query"",
                                            ""name"": ""number"",
                                            ""schema"": { ""type"": ""integer"" }
                                        },
                                        ""pageSize"": {
                                            ""in"": ""query"",
                                            ""name"": ""pageSize"",
                                            ""schema"": { ""type"": ""integer"" }
                                        },
                                        ""pageToken"": {
                                            ""in"": ""query"",
                                            ""name"": ""pageToken"",
                                            ""schema"": { ""type"": ""string"" }
                                        }
                                    },
                                    ""schemas"": {
                                        ""EdFi_Visa"": {
                                            ""description"": ""An Ed-Fi Descriptor""
                                        }
                                    }
                                },
                                ""paths"": {
                                    ""/ed-fi/absenceEventCategoryDescriptors"": {}
                                },
                                ""tags"": [
                                {
                                    ""description"": ""This entity"",
                                    ""name"": ""academicWeeks""
                                }]
                            },
                            ""resources"": {
                                ""openapi"": ""3.0.1"",
                                ""info"": {
                                    ""title"": ""Ed-Fi Resources API"",
                                    ""version"": ""5.2.0""
                                },
                                ""components"": {
                                    ""parameters"": {
                                        ""limit"": {
                                            ""in"": ""query"",
                                            ""name"": ""limit"",
                                            ""schema"": { ""type"": ""integer"" }
                                        },
                                        ""numberOfPartitions"": {
                                            ""in"": ""query"",
                                            ""name"": ""number"",
                                            ""schema"": { ""type"": ""integer"" }
                                        },
                                        ""pageSize"": {
                                            ""in"": ""query"",
                                            ""name"": ""pageSize"",
                                            ""schema"": { ""type"": ""integer"" }
                                        },
                                        ""pageToken"": {
                                            ""in"": ""query"",
                                            ""name"": ""pageToken"",
                                            ""schema"": { ""type"": ""string"" }
                                        }
                                    },
                                    ""schemas"": {
                                        ""EdFi_StudentResource"": {
                                            ""description"": ""A resource representing a student""
                                        }
                                    }
                                },
                                ""tags"": [
                                {
                                    ""description"": ""This entity"",
                                    ""name"": ""academicWeeks""
                                }],
                                ""paths"": {
                                    ""/ed-fi/academicWeeks"": {
                                        ""get"": {
                                            ""description"": ""This GET operation""
                                        }

                                    }
                                }
                            }
                        }
                    }
                }";

            extensionSchemaJson =
                @"
                {
                    ""apiSchemaVersion"": ""5.2.0"",
                    ""projectSchema"": {
                        ""isExtensionProject"": true,
                        ""projectName"": ""TPDM"",
                        ""projectVersion"": ""5.2.0"",
                        ""abstractResources"": {},
                        ""caseInsensitiveEndpointNameMapping"": {
                            ""accreditationstatusdescriptors"": ""accreditationStatusDescriptors"",
                            ""candidates"": ""candidates""
                        },
                        ""resourceNameMapping"": {
                            ""AccreditationStatusDescriptor"": ""accreditationStatusDescriptors"",
                            ""Candidate"": ""candidates""
                        },
                        ""resourceSchemas"": {
                            ""accreditationStatusDescriptors"": {
                                ""resourceName"": ""AccreditationStatusDescriptor"",
                                ""isDescriptor"": true,
                                ""isResourceExtension"": false,
                                ""openApiFragments"": {
                                    ""descriptors"": {
                                        ""components"": {
                                            ""schemas"": {
                                                ""TPDM_AccreditationStatus"": {
                                                    ""properties"": {
                                                        ""codeValue"": {
                                                            ""type"": ""string""
                                                        }
                                                    }
                                                }
                                            }
                                        },
                                        ""paths"": {
                                            ""/tpdm/accreditationStatusDescriptors"": {
                                                ""get"": {
                                                    ""operationId"": ""getAccreditationStatus"",
                                                    ""responses"": {
                                                        ""200"": {
                                                            ""description"": ""OK"",
                                                            ""content"": {
                                                                ""application/json"": {
                                                                    ""schema"": { ""type"": ""array"" }
                                                                }
                                                            }
                                                        }
                                                    }
                                                }
                                            }
                                        },
                                        ""tags"": [
                                            {
                                                ""description"": ""Accreditation Status"",
                                                ""name"": ""accreditationStatusDescriptors""
                                            }
                                        ]
                                    }
                                }
                            },
                            ""candidates"": {
                                ""resourceName"": ""Candidate"",
                                ""isDescriptor"": false,
                                ""isResourceExtension"": false,
                                ""openApiFragments"": {
                                    ""resources"": {
                                        ""components"": {
                                            ""schemas"": {
                                                ""TPDM_Candidate"": {
                                                    ""properties"": {
                                                        ""codeValue"": {
                                                            ""type"": ""string""
                                                        }
                                                    }
                                                }
                                            }
                                        },
                                        ""paths"": {
                                            ""/tpdm/candidates"": {
                                                ""get"": {
                                                    ""operationId"": ""getAccreditationStatus"",
                                                    ""responses"": {
                                                        ""200"": {
                                                            ""description"": ""OK"",
                                                            ""content"": {
                                                                ""application/json"": {
                                                                    ""schema"": { ""type"": ""array"" }
                                                                }
                                                            }
                                                        }
                                                    }
                                                }
                                            }
                                        },
                                        ""tags"": [
                                            {
                                                ""description"": ""Accreditation Status"",
                                                ""name"": ""candidates""
                                            }
                                        ]
                                    }
                                }
                            }
                        }
                    }
                }";
        }

        [Test]
        public void Should_throw_InvalidOperationException_when_node_not_found()
        {
            // Arrange
            string coreSchemaPath = "core-schema.json";
            string extensionSchemaPath = "extension-schema.json";
            string outputPath = "output.json";

            File.WriteAllText(coreSchemaPath, "{ \"openapi\": \"3.0.0\" }");
            File.WriteAllText(extensionSchemaPath, "{ \"info\": { \"title\": \"Test API\" } }");

            // Act
            var ex = Assert.Throws<InvalidOperationException>(() =>
                _generator.Generate(coreSchemaPath, extensionSchemaPath)
            );

            // Assert
            Assert.That(
                ex?.Message,
                Is.EqualTo("Node at path '$.projectSchema.openApiBaseDocuments.resources' not found")
            );

            // Cleanup
            File.Delete(coreSchemaPath);
            File.Delete(extensionSchemaPath);
            File.Delete(outputPath);
        }

        [Test]
        public void Should_check_if_openApiBaseDocuments_exists_in_schema()
        {
            // Arrange
            string coreSchemaPath = "core-schema.json";
            string extensionSchemaPath = "extension-schema.json";

            // Write JSON content to files
            File.WriteAllText(coreSchemaPath, coreSchemaJson);
            File.WriteAllText(extensionSchemaPath, extensionSchemaJson);

            // Act
            _generator.Generate(coreSchemaPath, extensionSchemaPath);

            // Read and parse the JSON schema to validate keys
            JsonNode? coreSchema = JsonObject.Parse(File.ReadAllText(coreSchemaPath));

            // Check if openApiBaseDocuments key exists and has descriptors and resources subdocuments
            bool baseDocumentsExist = coreSchema?["projectSchema"]?["openApiBaseDocuments"] != null;
            bool descriptorsExist =
                coreSchema?["projectSchema"]?["openApiBaseDocuments"]?["descriptors"] != null;
            bool resourcesExist = coreSchema?["projectSchema"]?["openApiBaseDocuments"]?["resources"] != null;

            // Assert
            Assert.That(baseDocumentsExist, "openApiBaseDocuments key is missing.");
            Assert.That(descriptorsExist, "openApiBaseDocuments.descriptors key is missing.");
            Assert.That(resourcesExist, "openApiBaseDocuments.resources key is missing.");

            // Cleanup
            File.Delete(coreSchemaPath);
            File.Delete(extensionSchemaPath);
        }

        [Test]
        public void Should_check_if_resources_have_openApiFragments_in_extension_schema()
        {
            // Arrange
            string coreSchemaPath = "core-schema.json";
            string extensionSchemaPath = "extension-schema.json";

            File.WriteAllText(extensionSchemaPath, extensionSchemaJson);

            File.WriteAllText(coreSchemaPath, coreSchemaJson);

            // Act
            _generator.Generate(coreSchemaPath, extensionSchemaPath);

            // Read and parse the JSON schema to validate keys in extension schema
            JsonNode? extensionSchema = JsonObject.Parse(extensionSchemaJson);

            // Check if resources have openApiFragments at resource level
            bool resourceSchemasExist = extensionSchema?["projectSchema"]?["resourceSchemas"] != null;
            bool descriptorFragmentsExist =
                extensionSchema
                    ?["projectSchema"]
                    ?["resourceSchemas"]
                    ?["accreditationStatusDescriptors"]
                    ?["openApiFragments"]
                    ?["descriptors"] != null;
            bool resourceFragmentsExist =
                extensionSchema
                    ?["projectSchema"]
                    ?["resourceSchemas"]
                    ?["candidates"]
                    ?["openApiFragments"]
                    ?["resources"] != null;

            // Assert that resources have openApiFragments
            Assert.That(resourceSchemasExist, "resourceSchemas key is missing.");
            Assert.That(
                descriptorFragmentsExist,
                "openApiFragments.descriptors key is missing in accreditationStatusDescriptors resource."
            );
            Assert.That(
                resourceFragmentsExist,
                "openApiFragments.resources key is missing in candidates resource."
            );

            // Cleanup
            File.Delete(coreSchemaPath);
            File.Delete(extensionSchemaPath);
        }
    }

    /// <summary>
    /// The CLI has no runtime configuration source, so its published cursor-paging contract must be the
    /// shipped appsettings defaults and must otherwise match what runtime OpenAPI assembly serves.
    /// </summary>
    [TestFixture]
    public class Given_A_Core_And_Extension_Schema_With_Eligible_Collections : OpenApiGeneratorTests
    {
        private const int ShippedMaximumPageSize = 500;
        private const int ShippedPartitionCount = 10;

        private const string CoreSchemaFileName = "cursor-paging-core-schema.json";
        private const string ExtensionSchemaFileName = "cursor-paging-extension-schema.json";

        private const string CoreSchemaJson = """
            {
                "apiSchemaVersion": "5.2.0",
                "projectSchema": {
                    "description": "The Ed-Fi Data Standard v5.2.0",
                    "isExtensionProject": false,
                    "projectName": "Ed-Fi",
                    "projectVersion": "5.2.0",
                    "abstractResources": {},
                    "caseInsensitiveEndpointNameMapping": { "academicweeks": "academicWeeks" },
                    "resourceNameMapping": { "AcademicWeek": "academicWeeks" },
                    "resourceSchemas": {
                        "academicWeeks": {
                            "resourceName": "AcademicWeek",
                            "isDescriptor": false,
                            "isResourceExtension": false,
                            "openApiFragments": {
                                "resources": {
                                    "components": {
                                        "schemas": {
                                            "EdFi_AcademicWeek": { "description": "An academic week" }
                                        }
                                    },
                                    "paths": {
                                        "/ed-fi/academicWeeks": {
                                            "get": {
                                                "operationId": "getAcademicWeeks",
                                                "parameters": [
                                                    { "$ref": "#/components/parameters/limit" }
                                                ],
                                                "responses": {
                                                    "200": {
                                                        "description": "OK",
                                                        "content": {
                                                            "application/json": {
                                                                "schema": { "type": "array" }
                                                            }
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    },
                                    "tags": [
                                        { "description": "Academic weeks", "name": "academicWeeks" }
                                    ]
                                }
                            }
                        }
                    },
                    "openApiBaseDocuments": {
                        "descriptors": {
                            "openapi": "3.0.1",
                            "info": { "title": "Ed-Fi Descriptors API", "version": "5.2.0" },
                            "components": {
                                "parameters": {
                                    "limit": {
                                        "in": "query",
                                        "name": "limit",
                                        "schema": { "type": "integer" }
                                    },
                                    "numberOfPartitions": {
                                        "in": "query",
                                        "name": "number",
                                        "schema": { "type": "integer" }
                                    },
                                    "pageSize": {
                                        "in": "query",
                                        "name": "pageSize",
                                        "schema": { "type": "integer" }
                                    },
                                    "pageToken": {
                                        "in": "query",
                                        "name": "pageToken",
                                        "schema": { "type": "string" }
                                    }
                                },
                                "schemas": {}
                            },
                            "paths": {},
                            "tags": []
                        },
                        "resources": {
                            "openapi": "3.0.1",
                            "info": { "title": "Ed-Fi Resources API", "version": "5.2.0" },
                            "components": {
                                "parameters": {
                                    "limit": {
                                        "in": "query",
                                        "name": "limit",
                                        "schema": { "type": "integer" }
                                    },
                                    "numberOfPartitions": {
                                        "in": "query",
                                        "name": "number",
                                        "schema": { "type": "integer" }
                                    },
                                    "pageSize": {
                                        "in": "query",
                                        "name": "pageSize",
                                        "schema": { "type": "integer" }
                                    },
                                    "pageToken": {
                                        "in": "query",
                                        "name": "pageToken",
                                        "schema": { "type": "string" }
                                    }
                                },
                                "schemas": {}
                            },
                            "paths": {},
                            "tags": []
                        }
                    }
                }
            }
            """;

        private const string ExtensionSchemaJson = """
            {
                "apiSchemaVersion": "5.2.0",
                "projectSchema": {
                    "isExtensionProject": true,
                    "projectName": "TPDM",
                    "projectVersion": "5.2.0",
                    "abstractResources": {},
                    "caseInsensitiveEndpointNameMapping": { "candidates": "candidates" },
                    "resourceNameMapping": { "Candidate": "candidates" },
                    "resourceSchemas": {
                        "candidates": {
                            "resourceName": "Candidate",
                            "isDescriptor": false,
                            "isResourceExtension": false,
                            "openApiFragments": {
                                "resources": {
                                    "components": {
                                        "schemas": {
                                            "TPDM_Candidate": { "description": "A candidate" }
                                        }
                                    },
                                    "paths": {
                                        "/tpdm/candidates": {
                                            "get": {
                                                "operationId": "get_TPDMCandidates",
                                                "responses": {
                                                    "200": {
                                                        "description": "OK",
                                                        "content": {
                                                            "application/json": {
                                                                "schema": { "type": "array" }
                                                            }
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    },
                                    "tags": [{ "description": "Candidates", "name": "candidates" }]
                                }
                            }
                        }
                    }
                }
            }
            """;

        private JsonNode generatedDocument = null!;

        [SetUp]
        public void SetUp()
        {
            Services.OpenApiGenerator generator = new(A.Fake<ILogger<Services.OpenApiGenerator>>());

            File.WriteAllText(CoreSchemaFileName, CoreSchemaJson);
            File.WriteAllText(ExtensionSchemaFileName, ExtensionSchemaJson);

            try
            {
                generatedDocument =
                    JsonNode.Parse(generator.Generate(CoreSchemaFileName, ExtensionSchemaFileName))
                    ?? throw new InvalidOperationException("Generator produced no document.");
            }
            finally
            {
                File.Delete(CoreSchemaFileName);
                File.Delete(ExtensionSchemaFileName);
            }
        }

        private JsonNode ParameterSchema(string componentName)
        {
            return generatedDocument["components"]!["parameters"]![componentName]!["schema"]!;
        }

        [Test]
        public void It_should_publish_the_shipped_maximum_page_size()
        {
            JsonNode limitSchema = ParameterSchema("limit");
            limitSchema["default"]!.GetValue<int>().Should().Be(ShippedMaximumPageSize);
            limitSchema["maximum"]!.GetValue<int>().Should().Be(ShippedMaximumPageSize);

            JsonNode pageSizeSchema = ParameterSchema("pageSize");
            pageSizeSchema["default"]!.GetValue<int>().Should().Be(ShippedMaximumPageSize);
            pageSizeSchema["maximum"]!.GetValue<int>().Should().Be(ShippedMaximumPageSize);
        }

        [Test]
        public void It_should_publish_the_shipped_partition_count()
        {
            ParameterSchema("numberOfPartitions")["default"]!
                .GetValue<int>()
                .Should()
                .Be(ShippedPartitionCount);
        }

        [Test]
        public void It_should_augment_the_core_collection_with_a_partitions_operation()
        {
            generatedDocument["paths"]!.AsObject().Should().ContainKey("/ed-fi/academicWeeks/partitions");
            generatedDocument["paths"]!["/ed-fi/academicWeeks/partitions"]!["get"]!["operationId"]!
                .GetValue<string>()
                .Should()
                .Be("getAcademicWeeksPartitions");
        }

        [Test]
        public void It_should_augment_the_extension_collection_with_a_prefixed_partitions_operation()
        {
            generatedDocument["paths"]!.AsObject().Should().ContainKey("/tpdm/candidates/partitions");
            generatedDocument["paths"]!["/tpdm/candidates/partitions"]!["get"]!["operationId"]!
                .GetValue<string>()
                .Should()
                .Be("get_TPDMCandidatesPartitions");
        }

        [Test]
        public void It_should_add_the_cursor_parameters_to_both_collection_operations()
        {
            foreach (string collectionPath in new[] { "/ed-fi/academicWeeks", "/tpdm/candidates" })
            {
                List<string> references = generatedDocument["paths"]![collectionPath]!["get"]!["parameters"]!
                    .AsArray()
                    .Where(parameter => parameter!["$ref"] is not null)
                    .Select(parameter => parameter!["$ref"]!.GetValue<string>())
                    .ToList();

                references
                    .Should()
                    .Contain("#/components/parameters/pageToken")
                    .And.Contain("#/components/parameters/pageSize");
            }
        }
    }
}
