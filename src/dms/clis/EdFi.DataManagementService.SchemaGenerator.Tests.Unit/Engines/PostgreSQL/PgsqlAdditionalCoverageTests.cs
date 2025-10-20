// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.SchemaGenerator.Abstractions;
using EdFi.DataManagementService.SchemaGenerator.Pgsql;
using FluentAssertions;

namespace EdFi.DataManagementService.SchemaGenerator.Tests.Unit.Engines.PostgreSQL
{
    /// <summary>
    /// Additional tests to improve branch coverage for PgsqlDdlGeneratorStrategy.
    /// </summary>
    [TestFixture]
    public class PgsqlAdditionalCoverageTests
    {
        [Test]
        public void PgsqlGenerator_WithNullColumnMaxLength_HandlesGracefully()
        {
            // Arrange
            var schema = new ApiSchema
            {
                ProjectSchema = new ProjectSchema
                {
                    ProjectName = "TestProject",
                    ProjectVersion = "1.0.0",
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
                                    JsonPath = "$.TestTable",
                                    Columns =
                                    [
                                        new ColumnMetadata 
                                        { 
                                            ColumnName = "StringColumnNoMaxLength", 
                                            ColumnType = "string", 
                                            MaxLength = null, // No max length specified
                                            IsRequired = false 
                                        }
                                    ],
                                    ChildTables = []
                                }
                            }
                        }
                    }
                }
            };
            var generator = new PgsqlDdlGeneratorStrategy();

            // Act & Assert - Should not throw exception
            var sql = generator.GenerateDdlString(schema, includeExtensions: false);
            sql.Should().Contain("TEXT"); // Default for string without max length in PostgreSQL
        }

        [Test]
        public void PgsqlGenerator_WithDecimalType_GeneratesCorrectType()
        {
            // Arrange
            var schema = new ApiSchema
            {
                ProjectSchema = new ProjectSchema
                {
                    ProjectName = "TestProject",
                    ProjectVersion = "1.0.0",
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
                                    JsonPath = "$.TestTable",
                                    Columns =
                                    [
                                        new ColumnMetadata 
                                        { 
                                            ColumnName = "DecimalColumn", 
                                            ColumnType = "decimal", 
                                            Precision = "10",
                                            Scale = "2",
                                            IsRequired = true 
                                        }
                                    ],
                                    ChildTables = []
                                }
                            }
                        }
                    }
                }
            };
            var generator = new PgsqlDdlGeneratorStrategy();

            // Act
            var sql = generator.GenerateDdlString(schema, includeExtensions: false);

            // Assert
            sql.Should().Contain("DECIMAL(10, 2) NOT NULL");
        }

        [Test]
        public void PgsqlGenerator_WithDecimalTypeNoPrecision_UsesDefault()
        {
            // Arrange
            var schema = new ApiSchema
            {
                ProjectSchema = new ProjectSchema
                {
                    ProjectName = "TestProject",
                    ProjectVersion = "1.0.0",
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
                                    JsonPath = "$.TestTable",
                                    Columns =
                                    [
                                        new ColumnMetadata 
                                        { 
                                            ColumnName = "DecimalColumn", 
                                            ColumnType = "decimal", 
                                            Precision = null,
                                            Scale = null,
                                            IsRequired = false 
                                        }
                                    ],
                                    ChildTables = []
                                }
                            }
                        }
                    }
                }
            };
            var generator = new PgsqlDdlGeneratorStrategy();

            // Act
            var sql = generator.GenerateDdlString(schema, includeExtensions: false);

            // Assert
            sql.Should().Contain("DECIMAL"); // Default without explicit precision
        }

        [Test]
        public void PgsqlGenerator_WithExtensionResourceAndCustomMapping_UsesCustomSchema()
        {
            // Arrange
            var schema = new ApiSchema
            {
                ProjectSchema = new ProjectSchema
                {
                    ProjectName = "TestExtensionProject",
                    ProjectVersion = "1.0.0",
                    IsExtensionProject = true,
                    ResourceSchemas = new Dictionary<string, ResourceSchema>
                    {
                        ["TestExtensionResource"] = new ResourceSchema
                        {
                            ResourceName = "TestExtensionResource",
                            FlatteningMetadata = new FlatteningMetadata
                            {
                                Table = new TableMetadata
                                {
                                    BaseName = "TestExtension",
                                    JsonPath = "$.TestExtension",
                                    IsExtensionTable = true,
                                    Columns =
                                    [
                                        new ColumnMetadata 
                                        { 
                                            ColumnName = "ExtensionId", 
                                            ColumnType = "int32", 
                                            IsNaturalKey = true,
                                            IsRequired = true 
                                        }
                                    ],
                                    ChildTables = []
                                }
                            }
                        }
                    }
                }
            };
            var options = new DdlGenerationOptions
            {
                IncludeExtensions = true,
                UsePrefixedTableNames = false,
                SchemaMapping = new Dictionary<string, string>
                {
                    ["TestExtensionProject"] = "custom_extension_schema"
                }
            };
            var generator = new PgsqlDdlGeneratorStrategy();

            // Act
            var sql = generator.GenerateDdlString(schema, options);

            // Assert
            sql.Should().Contain("CREATE SCHEMA IF NOT EXISTS extensions");
            sql.Should().Contain("CREATE TABLE IF NOT EXISTS extensions.TestExtension");
        }

        [Test]
        public void PgsqlGenerator_WithDescriptorResourceSeparateSchema_GeneratesCorrectSchema()
        {
            // Arrange
            var schema = new ApiSchema
            {
                ProjectSchema = new ProjectSchema
                {
                    ProjectName = "EdFi",
                    ProjectVersion = "5.0.0",
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
                                            ColumnName = "DescriptorId", 
                                            ColumnType = "int32", 
                                            IsNaturalKey = true,
                                            IsRequired = true 
                                        },
                                        new ColumnMetadata 
                                        { 
                                            ColumnName = "CodeValue", 
                                            ColumnType = "string", 
                                            MaxLength = "50",
                                            IsRequired = true 
                                        }
                                    ],
                                    ChildTables = []
                                }
                            }
                        }
                    }
                }
            };
            var options = new DdlGenerationOptions
            {
                UsePrefixedTableNames = false,
                DescriptorSchema = "descriptors",
                DefaultSchema = "dms"
            };
            var generator = new PgsqlDdlGeneratorStrategy();

            // Act
            var sql = generator.GenerateDdlString(schema, options);

            // Assert
            sql.Should().Contain("CREATE SCHEMA IF NOT EXISTS descriptors");
            sql.Should().Contain("CREATE TABLE IF NOT EXISTS descriptors.TestDescriptor");
        }

        [Test]
        public void PgsqlGenerator_WithReferenceToParentTable_GeneratesCorrectForeignKey()
        {
            // Arrange
            var schema = new ApiSchema
            {
                ProjectSchema = new ProjectSchema
                {
                    ProjectName = "TestProject",
                    ProjectVersion = "1.0.0",
                    ResourceSchemas = new Dictionary<string, ResourceSchema>
                    {
                        ["ParentResource"] = new ResourceSchema
                        {
                            ResourceName = "ParentResource",
                            FlatteningMetadata = new FlatteningMetadata
                            {
                                Table = new TableMetadata
                                {
                                    BaseName = "ParentTable",
                                    JsonPath = "$.ParentTable",
                                    Columns =
                                    [
                                        new ColumnMetadata 
                                        { 
                                            ColumnName = "ParentId", 
                                            ColumnType = "int32", 
                                            IsNaturalKey = true,
                                            IsRequired = true 
                                        }
                                    ],
                                    ChildTables =
                                    [
                                        new TableMetadata
                                        {
                                            BaseName = "ChildTable",
                                            JsonPath = "$.ParentTable.ChildCollection",
                                            Columns =
                                            [
                                                new ColumnMetadata 
                                                { 
                                                    ColumnName = "ChildId", 
                                                    ColumnType = "int32", 
                                                    IsNaturalKey = true,
                                                    IsRequired = true 
                                                },
                                                new ColumnMetadata 
                                                { 
                                                    ColumnName = "ParentTable_Id", 
                                                    ColumnType = "bigint", 
                                                    IsParentReference = true,
                                                    IsRequired = true 
                                                }
                                            ],
                                            ChildTables = []
                                        }
                                    ]
                                }
                            }
                        }
                    }
                }
            };
            var generator = new PgsqlDdlGeneratorStrategy();

            // Act
            var sql = generator.GenerateDdlString(schema, includeExtensions: false);

            // Assert
            sql.Should().Contain("FK_ChildTable_ParentTable");
            sql.Should().Contain("REFERENCES dms.testproject_ParentTable(Id)");
        }

        [Test]
        public void PgsqlGenerator_WithUnsupportedColumnType_UsesText()
        {
            // Arrange
            var schema = new ApiSchema
            {
                ProjectSchema = new ProjectSchema
                {
                    ProjectName = "TestProject",
                    ProjectVersion = "1.0.0",
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
                                    JsonPath = "$.TestTable",
                                    Columns =
                                    [
                                        new ColumnMetadata 
                                        { 
                                            ColumnName = "UnsupportedColumn", 
                                            ColumnType = "unsupported_type", 
                                            IsRequired = false 
                                        }
                                    ],
                                    ChildTables = []
                                }
                            }
                        }
                    }
                }
            };
            var generator = new PgsqlDdlGeneratorStrategy();

            // Act
            var sql = generator.GenerateDdlString(schema, includeExtensions: false);

            // Assert
            sql.Should().Contain("TEXT"); // Default fallback for unsupported types
        }

        [Test]
        public void PgsqlGenerator_WithInt64ColumnType_GeneratesBigInt()
        {
            // Arrange
            var schema = new ApiSchema
            {
                ProjectSchema = new ProjectSchema
                {
                    ProjectName = "TestProject",
                    ProjectVersion = "1.0.0",
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
                                    JsonPath = "$.TestTable",
                                    Columns =
                                    [
                                        new ColumnMetadata 
                                        { 
                                            ColumnName = "BigIntColumn", 
                                            ColumnType = "int64", 
                                            IsRequired = true 
                                        }
                                    ],
                                    ChildTables = []
                                }
                            }
                        }
                    }
                }
            };
            var generator = new PgsqlDdlGeneratorStrategy();

            // Act
            var sql = generator.GenerateDdlString(schema, includeExtensions: false);

            // Assert
            sql.Should().Contain("BIGINT NOT NULL");
        }

        [Test]
        public void PgsqlGenerator_WithDateColumnType_GeneratesDate()
        {
            // Arrange
            var schema = new ApiSchema
            {
                ProjectSchema = new ProjectSchema
                {
                    ProjectName = "TestProject",
                    ProjectVersion = "1.0.0",
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
                                    JsonPath = "$.TestTable",
                                    Columns =
                                    [
                                        new ColumnMetadata 
                                        { 
                                            ColumnName = "DateColumn", 
                                            ColumnType = "date", 
                                            IsRequired = false 
                                        }
                                    ],
                                    ChildTables = []
                                }
                            }
                        }
                    }
                }
            };
            var generator = new PgsqlDdlGeneratorStrategy();

            // Act
            var sql = generator.GenerateDdlString(schema, includeExtensions: false);

            // Assert
            sql.Should().Contain("DateColumn DATE");
        }

        [Test]
        public void PgsqlGenerator_WithTimeColumnType_GeneratesTime()
        {
            // Arrange
            var schema = new ApiSchema
            {
                ProjectSchema = new ProjectSchema
                {
                    ProjectName = "TestProject",
                    ProjectVersion = "1.0.0",
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
                                    JsonPath = "$.TestTable",
                                    Columns =
                                    [
                                        new ColumnMetadata
                                        {
                                            ColumnName = "TimeColumn",
                                            ColumnType = "time",
                                            IsRequired = false
                                        }
                                    ],
                                    ChildTables = []
                                }
                            }
                        }
                    }
                }
            };
            var generator = new PgsqlDdlGeneratorStrategy();

            // Act
            var sql = generator.GenerateDdlString(schema, includeExtensions: false);

            // Assert
            sql.Should().Contain("TimeColumn TIME");
        }

        [Test]
        public void PgsqlGenerator_WithBooleanColumnType_GeneratesBoolean()
        {
            // Arrange
            var schema = new ApiSchema
            {
                ProjectSchema = new ProjectSchema
                {
                    ProjectName = "TestProject",
                    ProjectVersion = "1.0.0",
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
                                    JsonPath = "$.TestTable",
                                    Columns =
                                    [
                                        new ColumnMetadata
                                        {
                                            ColumnName = "BooleanColumn",
                                            ColumnType = "boolean",
                                            IsRequired = false
                                        }
                                    ],
                                    ChildTables = []
                                }
                            }
                        }
                    }
                }
            };
            var generator = new PgsqlDdlGeneratorStrategy();

            // Act
            var sql = generator.GenerateDdlString(schema, includeExtensions: false);

            // Assert
            sql.Should().Contain("BooleanColumn BOOLEAN");
        }
    }
}