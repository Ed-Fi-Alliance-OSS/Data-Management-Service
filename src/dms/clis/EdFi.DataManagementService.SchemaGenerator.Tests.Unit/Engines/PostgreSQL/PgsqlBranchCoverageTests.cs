// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.SchemaGenerator.Abstractions;
using EdFi.DataManagementService.SchemaGenerator.Pgsql;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.SchemaGenerator.Tests.Unit.Engines.PostgreSQL
{
    /// <summary>
    /// PostgreSQL-specific branch coverage tests for column type mapping and DDL generation logic.
    /// </summary>
    [TestFixture]
    public class PgsqlBranchCoverageTests
    {
        [TestFixture]
        public class PgsqlColumnTypeMappingTests
        {
            private PgsqlDdlGeneratorStrategy _strategy;

            [SetUp]
            public void SetUp()
            {
                var logger = LoggerFactory.Create(builder => { }).CreateLogger<PgsqlDdlGeneratorStrategy>();
                _strategy = new PgsqlDdlGeneratorStrategy(logger);
            }

            [TestCase("int64", "BIGINT")]
            [TestCase("bigint", "BIGINT")]
            [TestCase("int32", "INTEGER")]
            [TestCase("integer", "INTEGER")]
            [TestCase("int", "INTEGER")]
            [TestCase("int16", "SMALLINT")]
            [TestCase("short", "SMALLINT")]
            public void MapColumnType_NumericTypes_ReturnsCorrectPostgresType(
                string inputType,
                string expectedType
            )
            {
                // Arrange
                var column = new ColumnMetadata { ColumnName = "TestColumn", ColumnType = inputType };
                var schema = CreateTestSchema(column);

                // Act
                var result = _strategy.GenerateDdlString(schema, false, false);

                // Assert
                result.Should().Contain($"TestColumn {expectedType}");
            }

            [TestCase("boolean", "BOOLEAN")]
            [TestCase("bool", "BOOLEAN")]
            public void MapColumnType_BooleanTypes_ReturnsCorrectPostgresType(
                string inputType,
                string expectedType
            )
            {
                // Arrange
                var column = new ColumnMetadata { ColumnName = "TestColumn", ColumnType = inputType };
                var schema = CreateTestSchema(column);

                // Act
                var result = _strategy.GenerateDdlString(schema, false, false);

                // Assert
                result.Should().Contain($"TestColumn {expectedType}");
            }

            [TestCase("decimal", "DECIMAL")]
            public void MapColumnType_DecimalTypes_ReturnsCorrectPostgresType(
                string inputType,
                string expectedType
            )
            {
                // Arrange
                var column = new ColumnMetadata { ColumnName = "TestColumn", ColumnType = inputType };
                var schema = CreateTestSchema(column);

                // Act
                var result = _strategy.GenerateDdlString(schema, false, false);

                // Assert
                result.Should().Contain($"TestColumn {expectedType}");
            }

            [TestCase("string", "50", "VARCHAR(50)")]
            [TestCase("string", "100", "VARCHAR(100)")]
            [TestCase("string", null, "TEXT")]
            public void MapColumnType_StringTypesWithLength_ReturnsCorrectPostgresType(
                string inputType,
                string? maxLength,
                string expectedType
            )
            {
                // Arrange
                var column = new ColumnMetadata
                {
                    ColumnName = "TestColumn",
                    ColumnType = inputType,
                    MaxLength = maxLength,
                };
                var schema = CreateTestSchema(column);

                // Act
                var result = _strategy.GenerateDdlString(schema, false, false);

                // Assert
                result.Should().Contain($"TestColumn {expectedType}");
            }

            [TestCase("date", "DATE")]
            [TestCase("time", "TIME")]
            [TestCase("datetime", "TIMESTAMP WITH TIME ZONE")]
            public void MapColumnType_DateTimeTypes_ReturnsCorrectPostgresType(
                string inputType,
                string expectedType
            )
            {
                // Arrange
                var column = new ColumnMetadata { ColumnName = "TestColumn", ColumnType = inputType };
                var schema = CreateTestSchema(column);

                // Act
                var result = _strategy.GenerateDdlString(schema, false, false);

                // Assert
                result.Should().Contain($"TestColumn {expectedType}");
            }

            [TestCase("guid", "UUID")]
            [TestCase("uuid", "UUID")]
            public void MapColumnType_GuidTypes_ReturnsCorrectPostgresType(
                string inputType,
                string expectedType
            )
            {
                // Arrange
                var column = new ColumnMetadata { ColumnName = "TestColumn", ColumnType = inputType };
                var schema = CreateTestSchema(column);

                // Act
                var result = _strategy.GenerateDdlString(schema, false, false);

                // Assert
                result.Should().Contain($"TestColumn {expectedType}");
            }

            [Test]
            public void MapColumnType_UnknownType_ReturnsText()
            {
                // Arrange
                var column = new ColumnMetadata { ColumnName = "TestColumn", ColumnType = "unknown_type" };
                var schema = CreateTestSchema(column);

                // Act
                var result = _strategy.GenerateDdlString(schema, false, false);

                // Assert
                result.Should().Contain("TestColumn TEXT");
            }

            [Test]
            public void MapColumnType_NullType_ReturnsText()
            {
                // Arrange
                var column = new ColumnMetadata { ColumnName = "TestColumn", ColumnType = null! };
                var schema = CreateTestSchema(column);

                // Act
                var result = _strategy.GenerateDdlString(schema, false, false);

                // Assert
                result.Should().Contain("TestColumn TEXT");
            }

            [Test]
            public void MapColumnType_EmptyType_ReturnsText()
            {
                // Arrange
                var column = new ColumnMetadata { ColumnName = "TestColumn", ColumnType = "" };
                var schema = CreateTestSchema(column);

                // Act
                var result = _strategy.GenerateDdlString(schema, false, false);

                // Assert
                result.Should().Contain("TestColumn TEXT");
            }

            [Test]
            public void MapColumnType_StringWithoutMaxLength_ReturnsText()
            {
                // Arrange
                var column = new ColumnMetadata
                {
                    ColumnName = "TestColumn",
                    ColumnType = "string",
                    MaxLength = null,
                };
                var schema = CreateTestSchema(column);

                // Act
                var result = _strategy.GenerateDdlString(schema, false, false);

                // Assert
                result.Should().Contain("TestColumn TEXT");
            }

            [Test]
            public void MapColumnType_VarcharWithInvalidMaxLength_ReturnsText()
            {
                // Arrange
                var column = new ColumnMetadata
                {
                    ColumnName = "TestColumn",
                    ColumnType = "varchar",
                    MaxLength = "invalid",
                };
                var schema = CreateTestSchema(column);

                // Act
                var result = _strategy.GenerateDdlString(schema, false, false);

                // Assert
                result.Should().Contain("TestColumn TEXT");
            }

            private static ApiSchema CreateTestSchema(ColumnMetadata column)
            {
                return new ApiSchema
                {
                    ProjectSchema = new ProjectSchema
                    {
                        ProjectName = "TestProject",
                        ProjectVersion = "1.0.0",
                        Description = "Test schema for type mapping.",
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
                                        JsonPath = "$.TestTable",
                                        Columns = [column],
                                        ChildTables = [],
                                    },
                                },
                            },
                        },
                    },
                };
            }
        }

        [TestFixture]
        public class PgsqlDescriptorResourceTests
        {
            private PgsqlDdlGeneratorStrategy _strategy;

            [SetUp]
            public void SetUp()
            {
                var logger = LoggerFactory.Create(builder => { }).CreateLogger<PgsqlDdlGeneratorStrategy>();
                _strategy = new PgsqlDdlGeneratorStrategy(logger);
            }

            [Test]
            public void PgsqlGenerator_DescriptorResource_UsesDescriptorSchema()
            {
                // Arrange

                var schema = CreateDescriptorSchema();
                var options = new DdlGenerationOptions { DescriptorSchema = "descriptors" };

                // Act
                var result = _strategy.GenerateDdlString(schema, options);

                // Assert
                result.Should().Contain("descriptors.TestDescriptor");
            }

            [Test]
            public void PgsqlGenerator_TypeResource_UsesDescriptorSchema()
            {
                // Arrange

                var schema = CreateTypeSchema();
                var options = new DdlGenerationOptions { DescriptorSchema = "descriptors" };

                // Act
                var result = _strategy.GenerateDdlString(schema, options);

                // Assert
                result.Should().Contain("descriptors.TestType");
            }

            private static ApiSchema CreateDescriptorSchema()
            {
                return new ApiSchema
                {
                    ProjectSchema = new ProjectSchema
                    {
                        ProjectName = "TestProject",
                        ProjectVersion = "1.0.0",
                        Description = "Test descriptor schema",
                        ResourceSchemas = new Dictionary<string, ResourceSchema>
                        {
                            ["TestDescriptor"] = new ResourceSchema
                            {
                                ResourceName = "TestDescriptor",
                                FlatteningMetadata = new FlatteningMetadata
                                {
                                    Table = new TableMetadata
                                    {
                                        BaseName = "TestDescriptor",
                                        JsonPath = "$.TestDescriptor",
                                        Columns =
                                        [
                                            new ColumnMetadata
                                            {
                                                ColumnName = "Id",
                                                ColumnType = "bigint",
                                                IsNaturalKey = true,
                                                IsRequired = true,
                                            },
                                        ],
                                        ChildTables = [],
                                    },
                                },
                            },
                        },
                    },
                };
            }

            private static ApiSchema CreateTypeSchema()
            {
                return new ApiSchema
                {
                    ProjectSchema = new ProjectSchema
                    {
                        ProjectName = "TestProject",
                        ProjectVersion = "1.0.0",
                        Description = "Test type schema",
                        ResourceSchemas = new Dictionary<string, ResourceSchema>
                        {
                            ["TestType"] = new ResourceSchema
                            {
                                ResourceName = "TestType",
                                FlatteningMetadata = new FlatteningMetadata
                                {
                                    Table = new TableMetadata
                                    {
                                        BaseName = "TestType",
                                        JsonPath = "$.TestType",
                                        Columns =
                                        [
                                            new ColumnMetadata
                                            {
                                                ColumnName = "Id",
                                                ColumnType = "bigint",
                                                IsNaturalKey = true,
                                                IsRequired = true,
                                            },
                                        ],
                                        ChildTables = [],
                                    },
                                },
                            },
                        },
                    },
                };
            }
        }

        [TestFixture]
        public class PgsqlExtensionResourceTests
        {
            private PgsqlDdlGeneratorStrategy _strategy;

            [SetUp]
            public void SetUp()
            {
                var logger = LoggerFactory.Create(builder => { }).CreateLogger<PgsqlDdlGeneratorStrategy>();
                _strategy = new PgsqlDdlGeneratorStrategy(logger);
            }

            [Test]
            public void PgsqlGenerator_ExtensionResource_WithExtractableProjectName_UsesCorrectSchema()
            {
                // Arrange

                var schema = CreateExtensionSchema("TPDMStudentExtension");
                var options = new DdlGenerationOptions();
                options.SchemaMapping["TPDM"] = "tpdm_ext";
                options.IncludeExtensions = true;

                // Act
                var result = _strategy.GenerateDdlString(schema, options);

                // Assert
                result.Should().Contain("tpdm_ext.TPDMStudentExtension");
            }

            [Test]
            public void PgsqlGenerator_ExtensionResource_WithoutExtractableProjectName_UsesExtensionsSchema()
            {
                // Arrange

                var schema = CreateExtensionSchema("SimpleExtension");
                var options = new DdlGenerationOptions();
                options.SchemaMapping["Extensions"] = "ext";
                options.IncludeExtensions = true;

                // Act
                var result = _strategy.GenerateDdlString(schema, options);

                // Assert
                result.Should().Contain("ext.SimpleExtension");
            }

            private static ApiSchema CreateExtensionSchema(string resourceName)
            {
                return new ApiSchema
                {
                    ProjectSchema = new ProjectSchema
                    {
                        ProjectName = "TestProject",
                        ProjectVersion = "1.0.0",
                        Description = "Test extension schema",
                        ResourceSchemas = new Dictionary<string, ResourceSchema>
                        {
                            [resourceName] = new ResourceSchema
                            {
                                ResourceName = resourceName,
                                FlatteningMetadata = new FlatteningMetadata
                                {
                                    Table = new TableMetadata
                                    {
                                        BaseName = resourceName,
                                        JsonPath = $"$.{resourceName}",
                                        IsExtensionTable = true,
                                        Columns =
                                        [
                                            new ColumnMetadata
                                            {
                                                ColumnName = "Id",
                                                ColumnType = "bigint",
                                                IsNaturalKey = true,
                                                IsRequired = true,
                                            },
                                        ],
                                        ChildTables = [],
                                    },
                                },
                            },
                        },
                    },
                };
            }
        }

        [TestFixture]
        public class PgsqlReferenceResolutionTests
        {
            private PgsqlDdlGeneratorStrategy _strategy;

            [SetUp]
            public void SetUp()
            {
                var logger = LoggerFactory.Create(builder => { }).CreateLogger<PgsqlDdlGeneratorStrategy>();
                _strategy = new PgsqlDdlGeneratorStrategy(logger);
            }

            [Test]
            public void PgsqlGenerator_ResolveResourceNameFromPath_WithReferencePattern_ReturnsResourceName()
            {
                // Arrange

                var schema = CreateSchemaWithReference();

                // Act
                var result = _strategy.GenerateDdlString(schema, false, false);

                // Assert - Should NOT generate FK constraint or index for entity reference
                // Per design decision: Entity references (FromReferencePath) should NOT have FK constraints or indexes
                // Only Document FK and indexes should exist
                result.Should().NotContain("IX_TestTable_Student");
                result.Should().NotContain("REFERENCES dms.testproject_Student");
                result.Should().Contain("REFERENCES dms.Document"); // Document FK should still exist
                result.Should().Contain("IX_TestTable_Document"); // Document index should exist
            }

            [Test]
            public void PgsqlGenerator_ResolveResourceNameFromPath_EmptyPath_ReturnsEmpty()
            {
                // Arrange

                var schema = CreateSchemaWithEmptyReference();

                // Act
                var result = _strategy.GenerateDdlString(schema, false, false);

                // Assert - Should handle empty reference gracefully
                result.Should().NotBeNull();
            }

            private static ApiSchema CreateSchemaWithReference()
            {
                return new ApiSchema
                {
                    ProjectSchema = new ProjectSchema
                    {
                        ProjectName = "TestProject",
                        ProjectVersion = "1.0.0",
                        Description = "Test reference schema",
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
                                        JsonPath = "$.TestTable",
                                        Columns =
                                        [
                                            new ColumnMetadata
                                            {
                                                ColumnName = "StudentId",
                                                ColumnType = "bigint",
                                                IsNaturalKey = true,
                                                IsRequired = true,
                                                FromReferencePath = "StudentReference",
                                            },
                                        ],
                                        ChildTables = [],
                                    },
                                },
                            },
                        },
                    },
                };
            }

            private static ApiSchema CreateSchemaWithEmptyReference()
            {
                return new ApiSchema
                {
                    ProjectSchema = new ProjectSchema
                    {
                        ProjectName = "TestProject",
                        ProjectVersion = "1.0.0",
                        Description = "Test empty reference schema",
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
                                        JsonPath = "$.TestTable",
                                        Columns =
                                        [
                                            new ColumnMetadata
                                            {
                                                ColumnName = "EmptyRefId",
                                                ColumnType = "bigint",
                                                IsNaturalKey = true,
                                                IsRequired = true,
                                                FromReferencePath = "",
                                            },
                                        ],
                                        ChildTables = [],
                                    },
                                },
                            },
                        },
                    },
                };
            }
        }
    }
}
