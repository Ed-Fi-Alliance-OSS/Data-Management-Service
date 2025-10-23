// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.SchemaGenerator.Abstractions;
using EdFi.DataManagementService.SchemaGenerator.Mssql;
using EdFi.DataManagementService.SchemaGenerator.Tests.Unit.Shared;
using FluentAssertions;

namespace EdFi.DataManagementService.SchemaGenerator.Tests.Unit.Engines.Mssql
{
    /// <summary>
    /// Unit tests specifically designed to improve line coverage for SQL Server DDL generation.
    /// </summary>
    [TestFixture]
    public class MssqlLineCoverageImprovementTests
    {
        private MssqlDdlGeneratorStrategy _strategy;

        [SetUp]
        public void SetUp()
        {
            _strategy = new MssqlDdlGeneratorStrategy();
        }

        [Test]
        public void GenerateDdlString_WithNullProjectSchema_ThrowsInvalidDataException()
        {
            // Arrange
            var apiSchema = new ApiSchema { ProjectSchema = null! };

            // Act & Assert
            Action act = () => _strategy.GenerateDdlString(apiSchema, includeExtensions: false);
            act.Should()
                .Throw<InvalidDataException>()
                .WithMessage("ApiSchema does not contain valid projectSchema.");
        }

        [Test]
        public void GenerateDdlString_WithNullResourceSchemas_ThrowsInvalidDataException()
        {
            // Arrange
            var apiSchema = new ApiSchema
            {
                ProjectSchema = new ProjectSchema
                {
                    ProjectName = "TestProject",
                    ProjectVersion = "1.0.0",
                    ResourceSchemas = null!,
                },
            };

            // Act & Assert
            Action act = () => _strategy.GenerateDdlString(apiSchema, includeExtensions: false);
            act.Should()
                .Throw<InvalidDataException>()
                .WithMessage("ApiSchema does not contain valid projectSchema.");
        }

        [Test]
        public void GenerateDdl_WithFileOutput_CreatesDirectoryAndWritesFile()
        {
            // Arrange
            var schema = TestHelpers.GetBasicSchema();
            var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

            try
            {
                // Act
                _strategy.GenerateDdl(schema, tempDir, includeExtensions: false);

                // Assert
                Directory.Exists(tempDir).Should().BeTrue();
                var outputFile = Path.Combine(tempDir, "EdFi-DMS-Database-Schema-SQLServer.sql");
                File.Exists(outputFile).Should().BeTrue();

                var content = File.ReadAllText(outputFile);
                content.Should().NotBeEmpty();
                content.Should().Contain("CREATE TABLE");
            }
            finally
            {
                // Cleanup
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
        }

        [Test]
        public void GenerateDdl_WithDdlGenerationOptions_CreatesDirectoryAndWritesFile()
        {
            // Arrange
            var schema = TestHelpers.GetBasicSchema();
            var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            var options = new DdlGenerationOptions
            {
                IncludeExtensions = false,
                GenerateForeignKeyConstraints = true,
            };

            try
            {
                // Act
                _strategy.GenerateDdl(schema, tempDir, options);

                // Assert
                Directory.Exists(tempDir).Should().BeTrue();
                var outputFile = Path.Combine(tempDir, "EdFi-DMS-Database-Schema-SQLServer.sql");
                File.Exists(outputFile).Should().BeTrue();

                var content = File.ReadAllText(outputFile);
                content.Should().NotBeEmpty();
                content.Should().Contain("CREATE TABLE");
            }
            finally
            {
                // Cleanup
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
        }

        [Test]
        public void GenerateDdlString_WithInvalidProjectSchemaInOptionsOverload_ThrowsInvalidDataException()
        {
            // Arrange
            var apiSchema = new ApiSchema { ProjectSchema = null };
            var options = new DdlGenerationOptions();

            // Act & Assert
            Action act = () => _strategy.GenerateDdlString(apiSchema, options);
            act.Should()
                .Throw<InvalidDataException>()
                .WithMessage("ApiSchema does not contain valid projectSchema.");
        }

        [Test]
        public void GenerateDdlString_WithNullResourceSchemasInOptionsOverload_ThrowsInvalidDataException()
        {
            // Arrange
            var apiSchema = new ApiSchema
            {
                ProjectSchema = new ProjectSchema
                {
                    ProjectName = "TestProject",
                    ProjectVersion = "1.0.0",
                    ResourceSchemas = null!,
                },
            };
            var options = new DdlGenerationOptions();

            // Act & Assert
            Action act = () => _strategy.GenerateDdlString(apiSchema, options);
            act.Should()
                .Throw<InvalidDataException>()
                .WithMessage("ApiSchema does not contain valid projectSchema.");
        }

        [Test]
        public void GenerateDdlString_WithValidOptionsOverload_ReturnsValidDdl()
        {
            // Arrange
            var schema = TestHelpers.GetBasicSchema();
            var options = new DdlGenerationOptions
            {
                IncludeExtensions = false,
                GenerateForeignKeyConstraints = true,
                GenerateNaturalKeyConstraints = true,
            };

            // Act
            var result = _strategy.GenerateDdlString(schema, options);

            // Assert
            result.Should().NotBeNull();
            result.Should().NotBeEmpty();
            result.Should().Contain("CREATE TABLE");
            result.Should().Contain("testproject_TestTable");
        }

        [Test]
        public void GenerateDdlString_WithSchemaContainingAbstractResources_HandlesUnionViews()
        {
            // Arrange - Create a schema with abstract resources and subclasses
            var apiSchema = TestHelpers.CreateApiSchemaWithAbstractResource();
            var options = new DdlGenerationOptions { IncludeExtensions = false, SkipUnionViews = false };

            // Act
            var result = _strategy.GenerateDdlString(apiSchema, options);

            // Assert
            result.Should().NotBeNull();
            result.Should().NotBeEmpty();
            result.Should().Contain("CREATE TABLE");
        }

        [Test]
        public void GenerateDdlString_WithComplexPolymorphicSchema_GeneratesCorrectDdl()
        {
            // Arrange - Use the existing polymorphic schema helper
            var schema = TestHelpers.GetSchemaWithPolymorphicReference();

            // Act
            var result = _strategy.GenerateDdlString(schema, includeExtensions: false, skipUnionViews: false);

            // Assert
            result.Should().NotBeNull();
            result.Should().NotBeEmpty();
            result.Should().Contain("CREATE VIEW");
            result.Should().Contain("UNION ALL");
        }

        [Test]
        public void GenerateDdlString_WithSkipUnionViewsEnabled_DoesNotGenerateViews()
        {
            // Arrange
            var schema = TestHelpers.GetSchemaWithPolymorphicReference();

            // Act
            var result = _strategy.GenerateDdlString(schema, includeExtensions: false, skipUnionViews: true);

            // Assert
            result.Should().NotBeNull();
            result.Should().NotBeEmpty();
            result.Should().NotContain("CREATE VIEW");
            result.Should().NotContain("UNION ALL");
        }

        [Test]
        public void GenerateDdlString_WithSchemaHavingAbstractResources_ProcessesAbstractResourcesCorrectly()
        {
            // Arrange - Create schema with abstractResources to hit lines 199-206
            var apiSchema = CreateSchemaWithAbstractResourcesProperty();

            // Create templates in a temp directory to ensure union view processing works
            var originalDir = Directory.GetCurrentDirectory();
            var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempDir);
            Directory.CreateDirectory(Path.Combine(tempDir, "Templates"));

            // Create both required templates
            var tableTemplateFile = Path.Combine(tempDir, "Templates", "mssql-table-idempotent.hbs");
            File.WriteAllText(tableTemplateFile, "CREATE TABLE [{{schemaName}}].[{{tableName}}] (Id int);");

            var unionViewTemplateFile = Path.Combine(tempDir, "Templates", "mssql-union-view.hbs");
            File.WriteAllText(
                unionViewTemplateFile,
                "CREATE VIEW [{{schemaName}}].[{{viewName}}] AS SELECT * FROM [{{schemaName}}].[{{tableName}}];"
            );

            try
            {
                Directory.SetCurrentDirectory(tempDir);

                // Act - This should trigger both template fallback AND abstract resource processing
                var result = _strategy.GenerateDdlString(
                    apiSchema,
                    new DdlGenerationOptions { SkipUnionViews = false }
                );

                // Assert
                result.Should().NotBeNull();
                result.Should().NotBeEmpty();
            }
            finally
            {
                // Cleanup
                Directory.SetCurrentDirectory(originalDir);
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
        }

        [Test]
        public void GenerateDdlString_WithNullProjectSchemaInternal_ThrowsInvalidDataException()
        {
            // Arrange - Create ApiSchema with null ProjectSchema to hit the exact condition on lines 88-90
            var apiSchema = new ApiSchema { ProjectSchema = null };

            // Act & Assert - This should execute line 88 condition check and line 90 exception throw
            Action act = () => _strategy.GenerateDdlString(apiSchema, includeExtensions: false);
            act.Should()
                .Throw<InvalidDataException>()
                .WithMessage("ApiSchema does not contain valid projectSchema.");
        }

        [Test]
        public void GenerateDdlString_WithEmptyResourceSchemas_ThrowsException()
        {
            // Arrange - Another way to trigger the validation error
            var invalidSchema = new ApiSchema
            {
                ProjectSchema = new ProjectSchema
                {
                    ProjectName = "Test",
                    ProjectVersion = "1.0",
                    ResourceSchemas = null!,
                },
            };

            // Act & Assert - This should also hit line 90
            Action act = () => _strategy.GenerateDdlString(invalidSchema, includeExtensions: false);
            act.Should()
                .Throw<InvalidDataException>()
                .WithMessage("ApiSchema does not contain valid projectSchema.");
        }

        [Test]
        public void GenerateDdlString_WithMissingEmbeddedTemplateFile_UsesDirectoryGetCurrentDirectoryFallback()
        {
            // Arrange - Create schema that will need templates
            var schema = TestHelpers.GetBasicSchema();

            // Find the actual embedded template file and rename it temporarily to force fallback
            var appContextDir = AppContext.BaseDirectory;
            var embeddedTemplateFile = Path.Combine(appContextDir, "Templates", "mssql-table-idempotent.hbs");
            var backupTemplateFile = Path.Combine(
                appContextDir,
                "Templates",
                "mssql-table-idempotent.hbs.backup"
            );

            // Create a temp directory with the template for fallback
            var originalDir = Directory.GetCurrentDirectory();
            var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempDir);
            Directory.CreateDirectory(Path.Combine(tempDir, "Templates"));

            var fallbackTemplateFile = Path.Combine(tempDir, "Templates", "mssql-table-idempotent.hbs");
            File.WriteAllText(
                fallbackTemplateFile,
                "-- Template fallback test\nCREATE TABLE [{{schemaName}}].[{{tableName}}] (Id int);"
            );

            try
            {
                // Temporarily move the embedded template to force fallback (if it exists)
                bool templateMoved = false;
                if (File.Exists(embeddedTemplateFile))
                {
                    File.Move(embeddedTemplateFile, backupTemplateFile);
                    templateMoved = true;
                }

                Directory.SetCurrentDirectory(tempDir);

                // Act - This should trigger the template fallback path (lines 96-98)
                var result = _strategy.GenerateDdlString(schema, includeExtensions: false);

                // Assert
                result.Should().NotBeNull();
                result.Should().NotBeEmpty();
                result.Should().Contain("-- Template fallback test");

                // Restore the template if we moved it
                if (templateMoved && File.Exists(backupTemplateFile))
                {
                    File.Move(backupTemplateFile, embeddedTemplateFile);
                }
            }
            finally
            {
                // Cleanup: restore original directory and template
                Directory.SetCurrentDirectory(originalDir);

                // Ensure template is restored even if test fails
                if (File.Exists(backupTemplateFile))
                {
                    File.Move(backupTemplateFile, embeddedTemplateFile);
                }

                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
        }

        private ApiSchema CreateSchemaWithAbstractResources()
        {
            return new ApiSchema
            {
                ProjectSchema = new ProjectSchema
                {
                    ProjectName = "TestProject",
                    ProjectVersion = "1.0.0",
                    ResourceSchemas = new Dictionary<string, ResourceSchema>
                    {
                        ["TestTable"] = new ResourceSchema
                        {
                            ResourceName = "TestTable",
                            FlatteningMetadata = new FlatteningMetadata
                            {
                                Table = new TableMetadata
                                {
                                    BaseName = "TestTable",
                                    JsonPath = "$.testTable",
                                    Columns =
                                    [
                                        new ColumnMetadata
                                        {
                                            ColumnName = "Id",
                                            ColumnType = "int",
                                            IsRequired = true,
                                        },
                                    ],
                                },
                            },
                        },
                    },
                },
            };
        }

        /// <summary>
        /// Creates a schema with abstract resources to trigger abstract resource processing (lines 199-206)
        /// This uses a custom object that includes abstractResources when serialized
        /// </summary>
        private ApiSchema CreateSchemaWithAbstractResourcesProperty()
        {
            // Create an anonymous object that will serialize to JSON with abstractResources
            var projectSchemaWithAbstractResources = new
            {
                projectName = "TestWithAbstract",
                projectVersion = "1.0",
                resourceSchemas = new Dictionary<string, object>
                {
                    ["AbstractTestResource"] = new
                    {
                        resourceName = "AbstractTestResource",
                        flatteningMetadata = new
                        {
                            table = new
                            {
                                baseName = "AbstractTestResource",
                                jsonPath = "$.abstractTestResource",
                                columns = new[]
                                {
                                    new
                                    {
                                        columnName = "Id",
                                        columnType = "int",
                                        isRequired = true,
                                    },
                                },
                            },
                        },
                    },
                },
                abstractResources = new Dictionary<string, object>
                {
                    ["AbstractTestResource"] = new
                    {
                        flatteningMetadata = new
                        {
                            subclassTypes = new[] { "ConcreteType1", "ConcreteType2" },
                            unionViewName = "TestUnionView",
                        },
                    },
                },
            };

            // Serialize and deserialize to get the right structure
            var json = JsonSerializer.Serialize(projectSchemaWithAbstractResources);
            var projectSchema = JsonSerializer.Deserialize<ProjectSchema>(json);

            return new ApiSchema { ProjectSchema = projectSchema };
        }
    }
}
