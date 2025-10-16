// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.SchemaGenerator.Abstractions;
using EdFi.DataManagementService.SchemaGenerator.Mssql;
using EdFi.DataManagementService.SchemaGenerator.Pgsql;
using FluentAssertions;

namespace EdFi.DataManagementService.SchemaGenerator.Tests.Unit
{
    /// <summary>
    /// Comprehensive unit tests to increase branch coverage for column type mapping and various conditional logic.
    /// </summary>
    [TestFixture]
    public class BranchCoverageTests
    {
        [TestFixture]
        public class MssqlColumnTypeMappingTests
        {
            private MssqlDdlGeneratorStrategy _generator;

            [SetUp]
            public void SetUp()
            {
                _generator = new MssqlDdlGeneratorStrategy();
            }

            [TestCase("int64", "BIGINT")]
            [TestCase("bigint", "BIGINT")]
            [TestCase("int32", "INT")]
            [TestCase("integer", "INT")]
            [TestCase("int", "INT")]
            [TestCase("int16", "SMALLINT")]
            [TestCase("short", "SMALLINT")]
            public void MapColumnType_NumericTypes_ReturnsCorrectSqlServerType(string inputType, string expectedType)
            {
                // Arrange
                var column = new ColumnMetadata { ColumnName = "TestColumn", ColumnType = inputType };
                var schema = CreateTestSchema(column);

                // Act
                var result = _generator.GenerateDdlString(schema, false, false);

                // Assert
                result.Should().Contain($"[TestColumn] {expectedType}");
            }

            [TestCase("boolean", "BIT")]
            [TestCase("bool", "BIT")]
            public void MapColumnType_BooleanTypes_ReturnsCorrectSqlServerType(string inputType, string expectedType)
            {
                // Arrange
                var column = new ColumnMetadata { ColumnName = "TestColumn", ColumnType = inputType };
                var schema = CreateTestSchema(column);

                // Act
                var result = _generator.GenerateDdlString(schema, false, false);

                // Assert
                result.Should().Contain($"[TestColumn] {expectedType}");
            }

            [Test]
            public void MapColumnType_StringWithMaxLength_ReturnsNVarcharWithLength()
            {
                // Arrange
                var column = new ColumnMetadata { ColumnName = "TestColumn", ColumnType = "string", MaxLength = "100" };
                var schema = CreateTestSchema(column);

                // Act
                var result = _generator.GenerateDdlString(schema, false, false);

                // Assert
                result.Should().Contain("[TestColumn] NVARCHAR(100)");
            }

            [Test]
            public void MapColumnType_StringWithLargeMaxLength_ReturnsNVarcharMax()
            {
                // Arrange
                var column = new ColumnMetadata { ColumnName = "TestColumn", ColumnType = "string", MaxLength = "5000" };
                var schema = CreateTestSchema(column);

                // Act
                var result = _generator.GenerateDdlString(schema, false, false);

                // Assert
                result.Should().Contain("[TestColumn] NVARCHAR(MAX)");
            }

            [Test]
            public void MapColumnType_StringWithoutMaxLength_ReturnsNVarcharMax()
            {
                // Arrange
                var column = new ColumnMetadata { ColumnName = "TestColumn", ColumnType = "string" };
                var schema = CreateTestSchema(column);

                // Act
                var result = _generator.GenerateDdlString(schema, false, false);

                // Assert
                result.Should().Contain("[TestColumn] NVARCHAR(MAX)");
            }

            [TestCase("date", "DATE")]
            [TestCase("datetime", "DATETIME2(7)")]
            [TestCase("time", "TIME")]
            public void MapColumnType_DateTimeTypes_ReturnsCorrectSqlServerType(string inputType, string expectedType)
            {
                // Arrange
                var column = new ColumnMetadata { ColumnName = "TestColumn", ColumnType = inputType };
                var schema = CreateTestSchema(column);

                // Act
                var result = _generator.GenerateDdlString(schema, false, false);

                // Assert
                result.Should().Contain($"[TestColumn] {expectedType}");
            }

            [Test]
            public void MapColumnType_DecimalWithPrecisionAndScale_ReturnsDecimalWithValues()
            {
                // Arrange
                var column = new ColumnMetadata { ColumnName = "TestColumn", ColumnType = "decimal", Precision = "10", Scale = "2" };
                var schema = CreateTestSchema(column);

                // Act
                var result = _generator.GenerateDdlString(schema, false, false);

                // Assert
                result.Should().Contain("[TestColumn] DECIMAL(10, 2)");
            }

            [Test]
            public void MapColumnType_DecimalWithoutPrecisionAndScale_ReturnsDecimal()
            {
                // Arrange
                var column = new ColumnMetadata { ColumnName = "TestColumn", ColumnType = "decimal" };
                var schema = CreateTestSchema(column);

                // Act
                var result = _generator.GenerateDdlString(schema, false, false);

                // Assert
                result.Should().Contain("[TestColumn] DECIMAL");
            }

            [TestCase("currency", "MONEY")]
            [TestCase("percent", "DECIMAL(5, 4)")]
            [TestCase("year", "SMALLINT")]
            [TestCase("duration", "NVARCHAR(30)")]
            [TestCase("descriptor", "BIGINT")]
            public void MapColumnType_SpecialEdFiTypes_ReturnsCorrectSqlServerType(string inputType, string expectedType)
            {
                // Arrange
                var column = new ColumnMetadata { ColumnName = "TestColumn", ColumnType = inputType };
                var schema = CreateTestSchema(column);

                // Act
                var result = _generator.GenerateDdlString(schema, false, false);

                // Assert
                result.Should().Contain($"[TestColumn] {expectedType}");
            }

            [TestCase("guid", "UNIQUEIDENTIFIER")]
            [TestCase("uuid", "UNIQUEIDENTIFIER")]
            public void MapColumnType_GuidTypes_ReturnsUniqueIdentifier(string inputType, string expectedType)
            {
                // Arrange
                var column = new ColumnMetadata { ColumnName = "TestColumn", ColumnType = inputType };
                var schema = CreateTestSchema(column);

                // Act
                var result = _generator.GenerateDdlString(schema, false, false);

                // Assert
                result.Should().Contain($"[TestColumn] {expectedType}");
            }

            [TestCase("unknown")]
            [TestCase("custom")]
            [TestCase("")]
            public void MapColumnType_UnknownTypes_ReturnsNVarcharMax(string inputType)
            {
                // Arrange
                var column = new ColumnMetadata { ColumnName = "TestColumn", ColumnType = inputType };
                var schema = CreateTestSchema(column);

                // Act
                var result = _generator.GenerateDdlString(schema, false, false);

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
                        Description = "Test schema for column type mapping",
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
                                        JsonPath = "$.TestResource",
                                        Columns = [column],
                                        ChildTables = []
                                    }
                                }
                            }
                        }
                    }
                };
            }
        }

        [TestFixture]
        public class PgsqlColumnTypeMappingTests
        {
            private PgsqlDdlGeneratorStrategy _generator;

            [SetUp]
            public void SetUp()
            {
                _generator = new PgsqlDdlGeneratorStrategy();
            }

            [TestCase("int64", "BIGINT")]
            [TestCase("bigint", "BIGINT")]
            [TestCase("int32", "INTEGER")]
            [TestCase("integer", "INTEGER")]
            [TestCase("int", "INTEGER")]
            [TestCase("int16", "SMALLINT")]
            [TestCase("short", "SMALLINT")]
            public void MapColumnType_NumericTypes_ReturnsCorrectPostgreSqlType(string inputType, string expectedType)
            {
                // Arrange
                var column = new ColumnMetadata { ColumnName = "TestColumn", ColumnType = inputType };
                var schema = CreateTestSchema(column);

                // Act
                var result = _generator.GenerateDdlString(schema, false, false);

                // Assert
                result.Should().Contain($"TestColumn {expectedType}");
            }

            [TestCase("boolean", "BOOLEAN")]
            [TestCase("bool", "BOOLEAN")]
            public void MapColumnType_BooleanTypes_ReturnsCorrectPostgreSqlType(string inputType, string expectedType)
            {
                // Arrange
                var column = new ColumnMetadata { ColumnName = "TestColumn", ColumnType = inputType };
                var schema = CreateTestSchema(column);

                // Act
                var result = _generator.GenerateDdlString(schema, false, false);

                // Assert
                result.Should().Contain($"TestColumn {expectedType}");
            }

            [Test]
            public void MapColumnType_StringWithMaxLength_ReturnsVarcharWithLength()
            {
                // Arrange
                var column = new ColumnMetadata { ColumnName = "TestColumn", ColumnType = "string", MaxLength = "100" };
                var schema = CreateTestSchema(column);

                // Act
                var result = _generator.GenerateDdlString(schema, false, false);

                // Assert
                result.Should().Contain("TestColumn VARCHAR(100)");
            }

            [Test]
            public void MapColumnType_StringWithoutMaxLength_ReturnsText()
            {
                // Arrange
                var column = new ColumnMetadata { ColumnName = "TestColumn", ColumnType = "string" };
                var schema = CreateTestSchema(column);

                // Act
                var result = _generator.GenerateDdlString(schema, false, false);

                // Assert
                result.Should().Contain("TestColumn TEXT");
            }

            [TestCase("date", "DATE")]
            [TestCase("datetime", "TIMESTAMP WITH TIME ZONE")]
            [TestCase("time", "TIME")]
            public void MapColumnType_DateTimeTypes_ReturnsCorrectPostgreSqlType(string inputType, string expectedType)
            {
                // Arrange
                var column = new ColumnMetadata { ColumnName = "TestColumn", ColumnType = inputType };
                var schema = CreateTestSchema(column);

                // Act
                var result = _generator.GenerateDdlString(schema, false, false);

                // Assert
                result.Should().Contain($"TestColumn {expectedType}");
            }

            [Test]
            public void MapColumnType_DecimalWithPrecisionAndScale_ReturnsDecimalWithValues()
            {
                // Arrange
                var column = new ColumnMetadata { ColumnName = "TestColumn", ColumnType = "decimal", Precision = "10", Scale = "2" };
                var schema = CreateTestSchema(column);

                // Act
                var result = _generator.GenerateDdlString(schema, false, false);

                // Assert
                result.Should().Contain("TestColumn DECIMAL(10, 2)");
            }

            [Test]
            public void MapColumnType_DecimalWithoutPrecisionAndScale_ReturnsDecimal()
            {
                // Arrange
                var column = new ColumnMetadata { ColumnName = "TestColumn", ColumnType = "decimal" };
                var schema = CreateTestSchema(column);

                // Act
                var result = _generator.GenerateDdlString(schema, false, false);

                // Assert
                result.Should().Contain("TestColumn DECIMAL");
            }

            [TestCase("currency", "MONEY")]
            [TestCase("percent", "DECIMAL(5, 4)")]
            [TestCase("year", "SMALLINT")]
            [TestCase("duration", "VARCHAR(30)")]
            [TestCase("descriptor", "BIGINT")]
            public void MapColumnType_SpecialEdFiTypes_ReturnsCorrectPostgreSqlType(string inputType, string expectedType)
            {
                // Arrange
                var column = new ColumnMetadata { ColumnName = "TestColumn", ColumnType = inputType };
                var schema = CreateTestSchema(column);

                // Act
                var result = _generator.GenerateDdlString(schema, false, false);

                // Assert
                result.Should().Contain($"TestColumn {expectedType}");
            }

            [TestCase("guid", "UUID")]
            [TestCase("uuid", "UUID")]
            public void MapColumnType_GuidTypes_ReturnsUuid(string inputType, string expectedType)
            {
                // Arrange
                var column = new ColumnMetadata { ColumnName = "TestColumn", ColumnType = inputType };
                var schema = CreateTestSchema(column);

                // Act
                var result = _generator.GenerateDdlString(schema, false, false);

                // Assert
                result.Should().Contain($"TestColumn {expectedType}");
            }

            [TestCase("unknown")]
            [TestCase("custom")]
            [TestCase("")]
            public void MapColumnType_UnknownTypes_ReturnsText(string inputType)
            {
                // Arrange
                var column = new ColumnMetadata { ColumnName = "TestColumn", ColumnType = inputType };
                var schema = CreateTestSchema(column);

                // Act
                var result = _generator.GenerateDdlString(schema, false, false);

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
                        Description = "Test schema for column type mapping",
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
                                        JsonPath = "$.TestResource",
                                        Columns = [column],
                                        ChildTables = []
                                    }
                                }
                            }
                        }
                    }
                };
            }
        }

        [TestFixture]
        public class DescriptorResourceTests
        {
            [Test]
            public void MssqlGenerator_DescriptorResource_UsesDescriptorSchema()
            {
                // Arrange
                var generator = new MssqlDdlGeneratorStrategy();
                var schema = CreateDescriptorSchema();
                var options = new DdlGenerationOptions { DescriptorSchema = "descriptors" };

                // Act
                var result = generator.GenerateDdlString(schema, options);

                // Assert
                result.Should().Contain("[descriptors].[TestDescriptor]");
            }

            [Test]
            public void PgsqlGenerator_DescriptorResource_UsesDescriptorSchema()
            {
                // Arrange
                var generator = new PgsqlDdlGeneratorStrategy();
                var schema = CreateDescriptorSchema();
                var options = new DdlGenerationOptions { DescriptorSchema = "descriptors" };

                // Act
                var result = generator.GenerateDdlString(schema, options);

                // Assert
                result.Should().Contain("descriptors.TestDescriptor");
            }

            [Test]
            public void MssqlGenerator_TypeResource_UsesDescriptorSchema()
            {
                // Arrange
                var generator = new MssqlDdlGeneratorStrategy();
                var schema = CreateTypeSchema();
                var options = new DdlGenerationOptions { DescriptorSchema = "descriptors" };

                // Act
                var result = generator.GenerateDdlString(schema, options);

                // Assert
                result.Should().Contain("[descriptors].[TestType]");
            }

            [Test]
            public void PgsqlGenerator_TypeResource_UsesDescriptorSchema()
            {
                // Arrange
                var generator = new PgsqlDdlGeneratorStrategy();
                var schema = CreateTypeSchema();
                var options = new DdlGenerationOptions { DescriptorSchema = "descriptors" };

                // Act
                var result = generator.GenerateDdlString(schema, options);

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
                                        Columns = [
                                            new ColumnMetadata { ColumnName = "Id", ColumnType = "bigint", IsNaturalKey = true, IsRequired = true }
                                        ],
                                        ChildTables = []
                                    }
                                }
                            }
                        }
                    }
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
                                        Columns = [
                                            new ColumnMetadata { ColumnName = "Id", ColumnType = "bigint", IsNaturalKey = true, IsRequired = true }
                                        ],
                                        ChildTables = []
                                    }
                                }
                            }
                        }
                    }
                };
            }
        }

        [TestFixture]
        public class ExtensionResourceTests
        {
            [Test]
            public void MssqlGenerator_ExtensionResource_WithExtractableProjectName_UsesCorrectSchema()
            {
                // Arrange
                var generator = new MssqlDdlGeneratorStrategy();
                var schema = CreateExtensionSchema("TPDMStudentExtension");
                var options = new DdlGenerationOptions();
                options.SchemaMapping["TPDM"] = "tpdm_ext";
                options.IncludeExtensions = true;

                // Act
                var result = generator.GenerateDdlString(schema, options);

                // Assert
                result.Should().Contain("[tpdm_ext].[TPDMStudentExtension]");
            }

            [Test]
            public void PgsqlGenerator_ExtensionResource_WithExtractableProjectName_UsesCorrectSchema()
            {
                // Arrange
                var generator = new PgsqlDdlGeneratorStrategy();
                var schema = CreateExtensionSchema("TPDMStudentExtension");
                var options = new DdlGenerationOptions();
                options.SchemaMapping["TPDM"] = "tpdm_ext";
                options.IncludeExtensions = true;

                // Act
                var result = generator.GenerateDdlString(schema, options);

                // Assert
                result.Should().Contain("tpdm_ext.TPDMStudentExtension");
            }

            [Test]
            public void MssqlGenerator_ExtensionResource_WithoutExtractableProjectName_UsesExtensionsSchema()
            {
                // Arrange
                var generator = new MssqlDdlGeneratorStrategy();
                var schema = CreateExtensionSchema("SimpleExtension");
                var options = new DdlGenerationOptions();
                options.SchemaMapping["Extensions"] = "ext";
                options.IncludeExtensions = true;

                // Act
                var result = generator.GenerateDdlString(schema, options);

                // Assert
                result.Should().Contain("[ext].[SimpleExtension]");
            }

            [Test]
            public void PgsqlGenerator_ExtensionResource_WithoutExtractableProjectName_UsesExtensionsSchema()
            {
                // Arrange
                var generator = new PgsqlDdlGeneratorStrategy();
                var schema = CreateExtensionSchema("SimpleExtension");
                var options = new DdlGenerationOptions();
                options.SchemaMapping["Extensions"] = "ext";
                options.IncludeExtensions = true;

                // Act
                var result = generator.GenerateDdlString(schema, options);

                // Assert
                result.Should().Contain("ext.SimpleExtension");
            }

            [Test]
            public void ExtensionProjectNameExtraction_ValidExtensionName_ReturnsProjectName()
            {
                // Arrange
                var generator = new MssqlDdlGeneratorStrategy();
                var schema = CreateExtensionSchema("TPDMStudentExtension");
                var options = new DdlGenerationOptions();
                options.SchemaMapping["TPDM"] = "tpdm";
                options.IncludeExtensions = true;

                // Act
                var result = generator.GenerateDdlString(schema, options);

                // Assert - Should use TPDM schema
                result.Should().Contain("[tpdm].[TPDMStudentExtension]");
            }

            [Test]
            public void ExtensionProjectNameExtraction_InvalidExtensionName_UsesExtensionsDefault()
            {
                // Arrange - Extension name that doesn't match pattern
                var generator = new MssqlDdlGeneratorStrategy();
                var schema = CreateExtensionSchema("InvalidExtension");
                var options = new DdlGenerationOptions();
                options.SchemaMapping["Extensions"] = "extensions";
                options.IncludeExtensions = true;

                // Act
                var result = generator.GenerateDdlString(schema, options);

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
                                        Columns = [
                                            new ColumnMetadata { ColumnName = "Id", ColumnType = "bigint", IsNaturalKey = true, IsRequired = true }
                                        ],
                                        ChildTables = []
                                    }
                                }
                            }
                        }
                    }
                };
            }
        }

        [TestFixture]
        public class ReferenceResolutionTests
        {
            [Test]
            public void MssqlGenerator_ResolveResourceNameFromPath_WithReferencePattern_ReturnsResourceName()
            {
                // Arrange
                var generator = new MssqlDdlGeneratorStrategy();
                var schema = CreateSchemaWithReference();

                // Act
                var result = generator.GenerateDdlString(schema, false, false);

                // Assert - Should contain foreign key reference to Student table
                result.Should().Contain("REFERENCES [dms].[Student]");
            }

            [Test]
            public void PgsqlGenerator_ResolveResourceNameFromPath_WithReferencePattern_ReturnsResourceName()
            {
                // Arrange
                var generator = new PgsqlDdlGeneratorStrategy();
                var schema = CreateSchemaWithReference();

                // Act
                var result = generator.GenerateDdlString(schema, false, false);

                // Assert - Should contain foreign key reference to Student table
                result.Should().Contain("REFERENCES dms.Student");
            }

            [Test]
            public void MssqlGenerator_ResolveResourceNameFromPath_EmptyPath_ReturnsEmpty()
            {
                // Arrange
                var generator = new MssqlDdlGeneratorStrategy();
                var schema = CreateSchemaWithEmptyReference();

                // Act
                var result = generator.GenerateDdlString(schema, false, false);

                // Assert - Should handle empty reference gracefully
                result.Should().NotBeNull();
            }

            [Test]
            public void PgsqlGenerator_ResolveResourceNameFromPath_EmptyPath_ReturnsEmpty()
            {
                // Arrange
                var generator = new PgsqlDdlGeneratorStrategy();
                var schema = CreateSchemaWithEmptyReference();

                // Act
                var result = generator.GenerateDdlString(schema, false, false);

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
                                        Columns = [
                                            new ColumnMetadata
                                            {
                                                ColumnName = "StudentId",
                                                ColumnType = "bigint",
                                                IsNaturalKey = true,
                                                IsRequired = true,
                                                FromReferencePath = "StudentReference"
                                            }
                                        ],
                                        ChildTables = []
                                    }
                                }
                            }
                        }
                    }
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
                                        Columns = [
                                            new ColumnMetadata
                                            {
                                                ColumnName = "TestId",
                                                ColumnType = "bigint",
                                                IsNaturalKey = true,
                                                IsRequired = true,
                                                FromReferencePath = ""
                                            }
                                        ],
                                        ChildTables = []
                                    }
                                }
                            }
                        }
                    }
                };
            }
        }

        [TestFixture]
        public class DdlGenerationOptionsTests
        {
            [Test]
            public void MssqlGenerator_ResolveSchemaName_WithCustomMapping_UsesMapping()
            {
                // Arrange
                var generator = new MssqlDdlGeneratorStrategy();
                var schema = CreateBasicSchema();
                var options = new DdlGenerationOptions();
                options.SchemaMapping["TestProject"] = "custom_schema";

                // Act
                var result = generator.GenerateDdlString(schema, options);

                // Assert
                result.Should().Contain("[custom_schema].[TestTable]");
            }

            [Test]
            public void PgsqlGenerator_ResolveSchemaName_WithCustomMapping_UsesMapping()
            {
                // Arrange
                var generator = new PgsqlDdlGeneratorStrategy();
                var schema = CreateBasicSchema();
                var options = new DdlGenerationOptions();
                options.SchemaMapping["TestProject"] = "custom_schema";

                // Act
                var result = generator.GenerateDdlString(schema, options);

                // Assert
                result.Should().Contain("custom_schema.TestTable");
            }

            [Test]
            public void MssqlGenerator_ResolveSchemaName_WithoutMapping_UsesDefaultTransformation()
            {
                // Arrange
                var generator = new MssqlDdlGeneratorStrategy();
                var schema = CreateBasicSchema();
                var options = new DdlGenerationOptions();

                // Act
                var result = generator.GenerateDdlString(schema, options);

                // Assert
                result.Should().Contain("[dms].[TestTable]"); // Default transformation
            }

            [Test]
            public void PgsqlGenerator_ResolveSchemaName_WithoutMapping_UsesDefaultTransformation()
            {
                // Arrange
                var generator = new PgsqlDdlGeneratorStrategy();
                var schema = CreateBasicSchema();
                var options = new DdlGenerationOptions();

                // Act
                var result = generator.GenerateDdlString(schema, options);

                // Assert
                result.Should().Contain("dms.TestTable"); // Default transformation
            }

            private static ApiSchema CreateBasicSchema()
            {
                return new ApiSchema
                {
                    ProjectSchema = new ProjectSchema
                    {
                        ProjectName = "TestProject",
                        ProjectVersion = "1.0.0",
                        Description = "Test basic schema",
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
                                        Columns = [
                                            new ColumnMetadata { ColumnName = "Id", ColumnType = "bigint", IsNaturalKey = true, IsRequired = true }
                                        ],
                                        ChildTables = []
                                    }
                                }
                            }
                        }
                    }
                };
            }
        }

        [TestFixture]
        public class GenerateDdlMethodTests
        {
            [Test]
            public void MssqlGenerator_GenerateDdl_WithOutputDirectory_CreatesFile()
            {
                // Arrange
                var generator = new MssqlDdlGeneratorStrategy();
                var schema = CreateBasicSchema();
                var tempDir = Path.GetTempPath();
                var outputDir = Path.Combine(tempDir, "mssql_test_" + Guid.NewGuid().ToString());

                try
                {
                    // Act
                    generator.GenerateDdl(schema, outputDir, false, false);

                    // Assert
                    var filePath = Path.Combine(outputDir, "schema-mssql.sql");
                    File.Exists(filePath).Should().BeTrue();
                    var content = File.ReadAllText(filePath);
                    content.Should().Contain("CREATE TABLE");
                }
                finally
                {
                    // Cleanup
                    if (Directory.Exists(outputDir))
                    {
                        Directory.Delete(outputDir, true);
                    }
                }
            }

            [Test]
            public void PgsqlGenerator_GenerateDdl_WithOutputDirectory_CreatesFile()
            {
                // Arrange
                var generator = new PgsqlDdlGeneratorStrategy();
                var schema = CreateBasicSchema();
                var tempDir = Path.GetTempPath();
                var outputDir = Path.Combine(tempDir, "pgsql_test_" + Guid.NewGuid().ToString());

                try
                {
                    // Act
                    generator.GenerateDdl(schema, outputDir, false, false);

                    // Assert
                    var filePath = Path.Combine(outputDir, "schema-pgsql.sql");
                    File.Exists(filePath).Should().BeTrue();
                    var content = File.ReadAllText(filePath);
                    content.Should().Contain("CREATE TABLE");
                }
                finally
                {
                    // Cleanup
                    if (Directory.Exists(outputDir))
                    {
                        Directory.Delete(outputDir, true);
                    }
                }
            }

            [Test]
            public void MssqlGenerator_GenerateDdl_WithOptions_CreatesFile()
            {
                // Arrange
                var generator = new MssqlDdlGeneratorStrategy();
                var schema = CreateBasicSchema();
                var tempDir = Path.GetTempPath();
                var outputDir = Path.Combine(tempDir, "mssql_options_test_" + Guid.NewGuid().ToString());
                var options = new DdlGenerationOptions();

                try
                {
                    // Act
                    generator.GenerateDdl(schema, outputDir, options);

                    // Assert
                    var filePath = Path.Combine(outputDir, "schema-mssql.sql");
                    File.Exists(filePath).Should().BeTrue();
                    var content = File.ReadAllText(filePath);
                    content.Should().Contain("CREATE TABLE");
                }
                finally
                {
                    // Cleanup
                    if (Directory.Exists(outputDir))
                    {
                        Directory.Delete(outputDir, true);
                    }
                }
            }

            [Test]
            public void PgsqlGenerator_GenerateDdl_WithOptions_CreatesFile()
            {
                // Arrange
                var generator = new PgsqlDdlGeneratorStrategy();
                var schema = CreateBasicSchema();
                var tempDir = Path.GetTempPath();
                var outputDir = Path.Combine(tempDir, "pgsql_options_test_" + Guid.NewGuid().ToString());
                var options = new DdlGenerationOptions();

                try
                {
                    // Act
                    generator.GenerateDdl(schema, outputDir, options);

                    // Assert
                    var filePath = Path.Combine(outputDir, "schema-pgsql.sql");
                    File.Exists(filePath).Should().BeTrue();
                    var content = File.ReadAllText(filePath);
                    content.Should().Contain("CREATE TABLE");
                }
                finally
                {
                    // Cleanup
                    if (Directory.Exists(outputDir))
                    {
                        Directory.Delete(outputDir, true);
                    }
                }
            }

            private static ApiSchema CreateBasicSchema()
            {
                return new ApiSchema
                {
                    ProjectSchema = new ProjectSchema
                    {
                        ProjectName = "TestProject",
                        ProjectVersion = "1.0.0",
                        Description = "Test basic schema",
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
                                        Columns = [
                                            new ColumnMetadata { ColumnName = "Id", ColumnType = "bigint", IsNaturalKey = true, IsRequired = true }
                                        ],
                                        ChildTables = []
                                    }
                                }
                            }
                        }
                    }
                };
            }
        }
    }
}
