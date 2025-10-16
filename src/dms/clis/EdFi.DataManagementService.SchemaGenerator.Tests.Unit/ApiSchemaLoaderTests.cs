// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.SchemaGenerator.Abstractions;
using EdFi.DataManagementService.SchemaGenerator.Cli.Services;
using FluentAssertions;
using System.Text.Json;

namespace EdFi.DataManagementService.SchemaGenerator.Tests.Unit
{
    /// <summary>
    /// Unit tests for ApiSchemaLoader service.
    /// </summary>
    [TestFixture]
    public class ApiSchemaLoaderTests
    {
        private ApiSchemaLoader _loader = null!;
        private string _tempDirectory = null!;

        [SetUp]
        public void SetUp()
        {
            _loader = new ApiSchemaLoader();
            _tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(_tempDirectory);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, true);
            }
        }

        [Test]
        public void Load_WithValidApiSchemaFile_ShouldReturnApiSchema()
        {
            // Arrange
            var apiSchema = new ApiSchema
            {
                ApiSchemaVersion = "1.0.0",
                ProjectSchema = new ProjectSchema
                {
                    ProjectName = "TestProject",
                    ProjectVersion = "1.0.0",
                    IsExtensionProject = false,
                    Description = "Test project",
                    ResourceSchemas = new Dictionary<string, ResourceSchema>
                    {
                        ["TestResource"] = new ResourceSchema
                        {
                            ResourceName = "TestResource",
                            FlatteningMetadata = new FlatteningMetadata
                            {
                                Table = new TableMetadata
                                {
                                    BaseName = "TestTable",
                                    JsonPath = "$.test",
                                    Columns = new List<ColumnMetadata>
                                    {
                                        new ColumnMetadata
                                        {
                                            JsonPath = "$.id",
                                            ColumnName = "Id",
                                            ColumnType = "int",
                                            IsRequired = true,
                                            IsNaturalKey = true
                                        }
                                    },
                                    ChildTables = new List<TableMetadata>()
                                }
                            }
                        }
                    }
                }
            };

            var json = JsonSerializer.Serialize(apiSchema, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            });

            var testFilePath = Path.Combine(_tempDirectory, "test-schema.json");
            File.WriteAllText(testFilePath, json);

            // Act
            var result = _loader.Load(testFilePath);

            // Assert
            result.Should().NotBeNull();
            result.ApiSchemaVersion.Should().Be("1.0.0");
            result.ProjectSchema.Should().NotBeNull();
            result.ProjectSchema!.ProjectName.Should().Be("TestProject");
            result.ProjectSchema.ProjectVersion.Should().Be("1.0.0");
            result.ProjectSchema.IsExtensionProject.Should().BeFalse();
            result.ProjectSchema.Description.Should().Be("Test project");
            result.ProjectSchema.ResourceSchemas.Should().ContainKey("TestResource");
            result.ProjectSchema.ResourceSchemas["TestResource"].ResourceName.Should().Be("TestResource");
        }

        [Test]
        public void Load_WithNonExistentFile_ShouldThrowFileNotFoundException()
        {
            // Arrange
            var nonExistentPath = Path.Combine(_tempDirectory, "non-existent-file.json");

            // Act & Assert
            var exception = Assert.Throws<FileNotFoundException>(() => _loader.Load(nonExistentPath));
            exception!.Message.Should().Contain("ApiSchema file not found");
            exception.Message.Should().Contain(nonExistentPath);
        }

        [Test]
        public void Load_WithInvalidJson_ShouldThrowJsonException()
        {
            // Arrange
            var invalidJson = "{ invalid json content";
            var testFilePath = Path.Combine(_tempDirectory, "invalid-schema.json");
            File.WriteAllText(testFilePath, invalidJson);

            // Act & Assert
            Assert.Throws<JsonException>(() => _loader.Load(testFilePath));
        }

        [Test]
        public void Load_WithEmptyFile_ShouldThrowInvalidDataException()
        {
            // Arrange
            var testFilePath = Path.Combine(_tempDirectory, "empty-schema.json");
            File.WriteAllText(testFilePath, "");

            // Act & Assert
            var exception = Assert.Throws<JsonException>(() => _loader.Load(testFilePath));
            exception.Should().NotBeNull();
        }

        [Test]
        public void Load_WithNullJsonContent_ShouldThrowInvalidDataException()
        {
            // Arrange
            var testFilePath = Path.Combine(_tempDirectory, "null-schema.json");
            File.WriteAllText(testFilePath, "null");

            // Act & Assert
            var exception = Assert.Throws<InvalidDataException>(() => _loader.Load(testFilePath));
            exception!.Message.Should().Be("Failed to deserialize ApiSchema.");
        }

        [Test]
        public void Load_WithMinimalValidSchema_ShouldSucceed()
        {
            // Arrange
            var minimalJson = """
                {
                  "apiSchemaVersion": "1.0.0",
                  "projectSchema": {
                    "projectName": "MinimalProject",
                    "projectVersion": "1.0.0",
                    "isExtensionProject": false,
                    "description": "Minimal test project",
                    "resourceSchemas": {}
                  }
                }
                """;

            var testFilePath = Path.Combine(_tempDirectory, "minimal-schema.json");
            File.WriteAllText(testFilePath, minimalJson);

            // Act
            var result = _loader.Load(testFilePath);

            // Assert
            result.Should().NotBeNull();
            result.ApiSchemaVersion.Should().Be("1.0.0");
            result.ProjectSchema.Should().NotBeNull();
            result.ProjectSchema!.ProjectName.Should().Be("MinimalProject");
            result.ProjectSchema.ResourceSchemas.Should().NotBeNull();
            result.ProjectSchema.ResourceSchemas.Should().BeEmpty();
        }

        [Test]
        public void Load_WithCaseInsensitivePropertyNames_ShouldSucceed()
        {
            // Arrange
            var caseVariedJson = """
                {
                  "APISCHEMAVERSION": "1.0.0",
                  "ProjectSchema": {
                    "PROJECTNAME": "CaseTestProject",
                    "projectversion": "1.0.0",
                    "IsExtensionProject": false,
                    "DESCRIPTION": "Case sensitivity test project",
                    "resourceschemas": {}
                  }
                }
                """;

            var testFilePath = Path.Combine(_tempDirectory, "case-schema.json");
            File.WriteAllText(testFilePath, caseVariedJson);

            // Act
            var result = _loader.Load(testFilePath);

            // Assert
            result.Should().NotBeNull();
            result.ApiSchemaVersion.Should().Be("1.0.0");
            result.ProjectSchema.Should().NotBeNull();
            result.ProjectSchema!.ProjectName.Should().Be("CaseTestProject");
        }
    }
}
