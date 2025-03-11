// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using FakeItEasy;
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
                        ""openApiCoreDescriptors"": {
                            ""components"": {
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
                        ""openApiCoreResources"": {
                            ""components"": {
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
                }";

            extensionSchemaJson =
                @"
                {
                    ""apiSchemaVersion"": ""5.2.0"",
                    ""projectSchema"": {
                        ""isExtensionProject"": true,
                        ""openApiExtensionResourceFragments"": {
                            ""exts"": {},
                            ""newPaths"": {
                                ""/tpdm/accreditationStatusDescriptors"": {
                                    ""get"": {
                                        ""operationId"": ""getAccreditationStatus""
                                    }
                                }
                            },
                            ""newSchemas"": {
                                ""TPDM_AccreditationStatus"": {
                                    ""properties"": {
                                        ""codeValue"": {
                                            ""type"": ""string""
                                        }
                                    }
                                }
                            },
                            ""newTags"": [
                                {
                                    ""description"": ""Accreditation Status"",
                                    ""name"": ""accreditationStatusDescriptors""
                                }
                            ]
                        },
                        ""openApiExtensionDescriptorFragments"": {
                            ""exts"": {},
                            ""newPaths"": {
                                ""/tpdm/candidates"": {
                                    ""get"": {
                                        ""operationId"": ""getAccreditationStatus""
                                    }
                                }
                            },
                            ""newSchemas"": {
                                ""TPDM_Candidate"": {
                                    ""properties"": {
                                        ""codeValue"": {
                                            ""type"": ""string""
                                        }
                                    }
                                }
                            },
                            ""newTags"": [
                                {
                                    ""description"": ""Accreditation Status"",
                                    ""name"": ""candidates""
                                }
                            ]
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
            var ex = Assert.Throws<InvalidOperationException>(
                () => _generator.Generate(coreSchemaPath, extensionSchemaPath)
            );

            // Assert
            Assert.That(
                ex?.Message,
                Is.EqualTo("Node at path '$.projectSchema.openApiCoreResources' not found")
            );

            // Cleanup
            File.Delete(coreSchemaPath);
            File.Delete(extensionSchemaPath);
            File.Delete(outputPath);
        }

        [Test]
        public void Should_check_if_openApiCoreDescriptors_and_openApiCoreResources_exist_in_schema()
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

            // Check if openApiCoreDescriptors and openApiCoreResources keys exist in the core schema
            bool coreDescriptorsExist = coreSchema?["projectSchema"]?["openApiCoreDescriptors"] != null;
            bool coreResourcesExist = coreSchema?["projectSchema"]?["openApiCoreResources"] != null;

            // Assert
            Assert.That(coreDescriptorsExist, "openApiCoreDescriptors key is missing.");
            Assert.That(coreResourcesExist, "openApiCoreResources key is missing.");

            // Cleanup
            File.Delete(coreSchemaPath);
            File.Delete(extensionSchemaPath);
        }

        [Test]
        public void Should_check_if_openApiExtensionDescriptorFragments_and_openApiExtensionResourceFragments_exist_in_schema()
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

            bool extensionDescriptorsExist =
                extensionSchema?["projectSchema"]?["openApiExtensionDescriptorFragments"] != null;
            bool extensionResourcesExist =
                extensionSchema?["projectSchema"]?["openApiExtensionResourceFragments"] != null;

            // Assert that both keys exist in the extension schema
            Assert.That(extensionDescriptorsExist, "openApiExtensionDescriptorFragments key is missing.");
            Assert.That(extensionResourcesExist, "openApiExtensionResourceFragments key is missing.");

            // Cleanup
            File.Delete(coreSchemaPath);
            File.Delete(extensionSchemaPath);
        }
    }
}
