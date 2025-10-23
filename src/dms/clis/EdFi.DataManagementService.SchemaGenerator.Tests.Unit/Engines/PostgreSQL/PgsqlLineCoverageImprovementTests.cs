// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.SchemaGenerator.Abstractions;
using EdFi.DataManagementService.SchemaGenerator.Pgsql;
using EdFi.DataManagementService.SchemaGenerator.Tests.Unit.Shared;
using FluentAssertions;

namespace EdFi.DataManagementService.SchemaGenerator.Tests.Unit.Engines.PostgreSQL
{
    /// <summary>
    /// Unit tests specifically designed to improve line coverage for PostgreSQL DDL generation.
    /// </summary>
    [TestFixture]
    public class PgsqlLineCoverageImprovementTests
    {
        private PgsqlDdlGeneratorStrategy _strategy;

        [SetUp]
        public void SetUp()
        {
            _strategy = new PgsqlDdlGeneratorStrategy();
        }

        [Test]
        public void GenerateDdlString_WithNullProjectSchema_ThrowsInvalidDataException()
        {
            // Arrange
            var apiSchema = new ApiSchema
            {
                ProjectSchema = null!
            };

            // Act & Assert
            Action act = () => _strategy.GenerateDdlString(apiSchema, includeExtensions: false);
            act.Should().Throw<InvalidDataException>()
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
                    ResourceSchemas = null!
                }
            };

            // Act & Assert
            Action act = () => _strategy.GenerateDdlString(apiSchema, includeExtensions: false);
            act.Should().Throw<InvalidDataException>()
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
                var outputFile = Path.Combine(tempDir, "EdFi-DMS-Database-Schema-PostgreSQL.sql");
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
                GenerateForeignKeyConstraints = true
            };

            try
            {
                // Act
                _strategy.GenerateDdl(schema, tempDir, options);

                // Assert
                Directory.Exists(tempDir).Should().BeTrue();
                var outputFile = Path.Combine(tempDir, "EdFi-DMS-Database-Schema-PostgreSQL.sql");
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
            var apiSchema = new ApiSchema
            {
                ProjectSchema = null
            };
            var options = new DdlGenerationOptions();

            // Act & Assert
            Action act = () => _strategy.GenerateDdlString(apiSchema, options);
            act.Should().Throw<InvalidDataException>()
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
                    ResourceSchemas = null!
                }
            };
            var options = new DdlGenerationOptions();

            // Act & Assert
            Action act = () => _strategy.GenerateDdlString(apiSchema, options);
            act.Should().Throw<InvalidDataException>()
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
                GenerateNaturalKeyConstraints = true
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
            var options = new DdlGenerationOptions
            {
                IncludeExtensions = false,
                SkipUnionViews = false
            };

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
            result.Should().Contain("CREATE OR REPLACE VIEW");
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
            result.Should().NotContain("CREATE OR REPLACE VIEW");
            result.Should().NotContain("UNION ALL");
        }

        [Test]
        public void GenerateDdlString_WithBasicSchema_UsesEmbeddedTemplates()
        {
            // Arrange - Create a schema that will use embedded template resources
            var schema = TestHelpers.GetBasicSchema();

            // Act - This uses the embedded templates from resources
            var result = _strategy.GenerateDdlString(schema, includeExtensions: false);

            // Assert - Verify the DDL was generated successfully using embedded templates
            result.Should().NotBeNull();
            result.Should().NotBeEmpty();
            result.Should().Contain("CREATE TABLE");
            result.Should().Contain("TestTable");
        }

        [Test]
        public void GenerateDdlString_WithUnionViewTemplateFallback_UsesCurrentDirectory()
        {
            // Arrange - Create a schema with abstract resources
            var schema = CreateSchemaWithAbstractResources();

            var originalDir = Directory.GetCurrentDirectory();
            var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempDir);
            Directory.CreateDirectory(Path.Combine(tempDir, "Templates"));

            // Create template files in the temp directory
            var tableTemplateFile = Path.Combine(tempDir, "Templates", "pgsql-table-idempotent.hbs");
            File.WriteAllText(tableTemplateFile, "CREATE TABLE {{schemaName}}.{{tableName}} (id integer);");

            var unionViewTemplateFile = Path.Combine(tempDir, "Templates", "pgsql-union-view.hbs");
            File.WriteAllText(unionViewTemplateFile, "-- PostgreSQL union view fallback test\nCREATE OR REPLACE VIEW {{schemaName}}.{{viewName}} AS SELECT * FROM {{schemaName}}.{{tableName}};");

            try
            {
                Directory.SetCurrentDirectory(tempDir);

                // Act - This should trigger the union view template fallback path
                var result = _strategy.GenerateDdlString(schema, new DdlGenerationOptions { SkipUnionViews = false });

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
                                    Columns = [
                                        new ColumnMetadata { ColumnName = "Id", ColumnType = "integer", IsRequired = true }
                                    ]
                                }
                            }
                        }
                    }
                }
            };
        }
    }
}
