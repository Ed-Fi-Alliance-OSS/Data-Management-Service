// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.SchemaGenerator.Abstractions;
using EdFi.DataManagementService.SchemaGenerator.Mssql;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.SchemaGenerator.Tests.Unit.Engines.Mssql
{
    /// <summary>
    /// MSSQL-specific branch coverage tests for column type mapping and DDL generation logic.
    /// </summary>
    [TestFixture]
    public class MssqlBranchCoverageTests
    {
        [TestFixture]
        public class MssqlColumnTypeMappingTests
        {
            private MssqlDdlGeneratorStrategy _strategy;

            [SetUp]
            public void SetUp()
            {
                var logger = LoggerFactory.Create(builder => { }).CreateLogger<MssqlDdlGeneratorStrategy>();
                _strategy = new MssqlDdlGeneratorStrategy(logger);
            }

            [TestCase("int64", "BIGINT")]
            [TestCase("bigint", "BIGINT")]
            [TestCase("int32", "INT")]
            [TestCase("integer", "INT")]
            [TestCase("int", "INT")]
            [TestCase("int16", "SMALLINT")]
            [TestCase("short", "SMALLINT")]
            public void MapColumnType_NumericTypes_ReturnsCorrectSqlServerType(
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
                result.Should().Contain($"[TestColumn] {expectedType}");
            }

            [TestCase("boolean", "BIT")]
            [TestCase("bool", "BIT")]
            public void MapColumnType_BooleanTypes_ReturnsCorrectSqlServerType(
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
                result.Should().Contain($"[TestColumn] {expectedType}");
            }

            [TestCase("decimal", "DECIMAL")]
            [TestCase("currency", "MONEY")]
            public void MapColumnType_DecimalTypes_ReturnsCorrectSqlServerType(
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
                result.Should().Contain($"[TestColumn] {expectedType}");
            }

            [TestCase("string", "50", "NVARCHAR(50)")]
            [TestCase("string", "200", "NVARCHAR(200)")]
            [TestCase("string", "5000", "NVARCHAR(MAX)")]
            [TestCase("string", null, "NVARCHAR(MAX)")]
            public void MapColumnType_StringTypesWithLength_ReturnsCorrectSqlServerType(
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
                result.Should().Contain($"[TestColumn] {expectedType}");
            }

            [TestCase("date", "DATE")]
            [TestCase("time", "TIME")]
            [TestCase("datetime", "DATETIME2(7)")]
            public void MapColumnType_DateTimeTypes_ReturnsCorrectSqlServerType(
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
                result.Should().Contain($"[TestColumn] {expectedType}");
            }

            [TestCase("guid", "UNIQUEIDENTIFIER")]
            [TestCase("uuid", "UNIQUEIDENTIFIER")]
            public void MapColumnType_GuidTypes_ReturnsCorrectSqlServerType(
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
                result.Should().Contain($"[TestColumn] {expectedType}");
            }

            [Test]
            public void MapColumnType_UnknownType_ReturnsNVarcharMax()
            {
                // Arrange
                var column = new ColumnMetadata { ColumnName = "TestColumn", ColumnType = "unknown_type" };
                var schema = CreateTestSchema(column);

                // Act
                var result = _strategy.GenerateDdlString(schema, false, false);

                // Assert
                result.Should().Contain("[TestColumn] NVARCHAR(MAX)");
            }

            [Test]
            public void MapColumnType_NullType_ReturnsNVarcharMax()
            {
                // Arrange
                var column = new ColumnMetadata { ColumnName = "TestColumn", ColumnType = null! };
                var schema = CreateTestSchema(column);

                // Act
                var result = _strategy.GenerateDdlString(schema, false, false);

                // Assert
                result.Should().Contain("[TestColumn] NVARCHAR(MAX)");
            }

            [Test]
            public void MapColumnType_EmptyType_ReturnsNVarcharMax()
            {
                // Arrange
                var column = new ColumnMetadata { ColumnName = "TestColumn", ColumnType = "" };
                var schema = CreateTestSchema(column);

                // Act
                var result = _strategy.GenerateDdlString(schema, false, false);

                // Assert
                result.Should().Contain("[TestColumn] NVARCHAR(MAX)");
            }

            [Test]
            public void MapColumnType_StringWithoutMaxLength_ReturnsNVarcharMax()
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
                result.Should().Contain("[TestColumn] NVARCHAR(MAX)");
            }

            [Test]
            public void MapColumnType_VarcharWithInvalidMaxLength_ReturnsVarcharMax()
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
                result.Should().Contain("[TestColumn] NVARCHAR(MAX)");
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
        public class MssqlDescriptorResourceTests
        {
            private MssqlDdlGeneratorStrategy _strategy;

            [SetUp]
            public void SetUp()
            {
                var logger = LoggerFactory.Create(builder => { }).CreateLogger<MssqlDdlGeneratorStrategy>();
                _strategy = new MssqlDdlGeneratorStrategy(logger);
            }

            [Test]
            public void Mssql_generator_DescriptorResource_UsesDescriptorSchema()
            {
                // Arrange

                var schema = CreateDescriptorSchema();
                var options = new DdlGenerationOptions { DescriptorSchema = "descriptors" };

                // Act
                var result = _strategy.GenerateDdlString(schema, options);

                // Assert
                result.Should().Contain("[descriptors].[TestDescriptor]");
            }

            [Test]
            public void Mssql_generator_TypeResource_UsesDescriptorSchema()
            {
                // Arrange
                var schema = CreateTypeSchema();
                var options = new DdlGenerationOptions { DescriptorSchema = "descriptors" };

                // Act
                var result = _strategy.GenerateDdlString(schema, options);

                // Assert
                result.Should().Contain("[descriptors].[TestType]");
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
        public class MssqlExtensionResourceTests
        {
            private MssqlDdlGeneratorStrategy _strategy;

            [SetUp]
            public void SetUp()
            {
                var logger = LoggerFactory.Create(builder => { }).CreateLogger<MssqlDdlGeneratorStrategy>();
                _strategy = new MssqlDdlGeneratorStrategy(logger);
            }

            [Test]
            public void Mssql_generator_ExtensionResource_WithExtractableProjectName_UsesCorrectSchema()
            {
                // Arrange

                var schema = CreateExtensionSchema("TPDMStudentExtension");
                var options = new DdlGenerationOptions();
                options.SchemaMapping["TPDM"] = "tpdm_ext";
                options.IncludeExtensions = true;

                // Act
                var result = _strategy.GenerateDdlString(schema, options);

                // Assert
                result.Should().Contain("[tpdm_ext].[TPDMStudentExtension]");
            }

            [Test]
            public void Mssql_generator_ExtensionResource_WithoutExtractableProjectName_UsesExtensionsSchema()
            {
                // Arrange

                var schema = CreateExtensionSchema("SimpleExtension");
                var options = new DdlGenerationOptions();
                options.SchemaMapping["Extensions"] = "ext";
                options.IncludeExtensions = true;

                // Act
                var result = _strategy.GenerateDdlString(schema, options);

                // Assert
                result.Should().Contain("[ext].[SimpleExtension]");
            }

            [Test]
            public void ExtensionProjectNameExtraction_ValidExtensionName_ReturnsProjectName()
            {
                // Arrange

                var schema = CreateExtensionSchema("TPDMStudentExtension");
                var options = new DdlGenerationOptions();
                options.SchemaMapping["TPDM"] = "tpdm";
                options.IncludeExtensions = true;

                // Act
                var result = _strategy.GenerateDdlString(schema, options);

                // Assert - Should use TPDM schema
                result.Should().Contain("[tpdm].[TPDMStudentExtension]");
            }

            [Test]
            public void ExtensionProjectNameExtraction_InvalidExtensionName_UsesExtensionsDefault()
            {
                // Arrange - Extension name that doesn't match pattern

                var schema = CreateExtensionSchema("InvalidExtension");
                var options = new DdlGenerationOptions();
                options.SchemaMapping["Extensions"] = "extensions";
                options.IncludeExtensions = true;

                // Act
                var result = _strategy.GenerateDdlString(schema, options);

                // Assert - Should use Extensions schema
                result.Should().Contain("[extensions].[InvalidExtension]");
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
        public class MssqlReferenceResolutionTests
        {
            private MssqlDdlGeneratorStrategy _strategy;

            [SetUp]
            public void SetUp()
            {
                var logger = LoggerFactory.Create(builder => { }).CreateLogger<MssqlDdlGeneratorStrategy>();
                _strategy = new MssqlDdlGeneratorStrategy(logger);
            }

            [Test]
            public void Mssql_generator_ResolveResourceNameFromPath_WithReferencePattern_ReturnsResourceName()
            {
                // Arrange

                var schema = CreateSchemaWithReference();

                // Act
                var result = _strategy.GenerateDdlString(schema, false, false);

                // Assert - Should generate index for entity reference but NOT FK constraint
                // Per design decision: Entity references (FromReferencePath) should NOT have FK constraints
                result.Should().Contain("IX_TestTable_Student");
                result.Should().NotContain("REFERENCES [dms].[testproject_Student]");
                result.Should().Contain("REFERENCES [dms].[Document]"); // Document FK should still exist
            }

            [Test]
            public void Mssql_generator_ResolveResourceNameFromPath_EmptyPath_ReturnsEmpty()
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
