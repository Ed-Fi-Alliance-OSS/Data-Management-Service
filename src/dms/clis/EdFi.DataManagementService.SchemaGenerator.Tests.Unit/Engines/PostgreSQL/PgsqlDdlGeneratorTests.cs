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
    /// Unit tests for PostgreSQL DDL generation.
    /// </summary>
    [TestFixture]
    public class PgsqlDdlGeneratorTests
    {
        [Test]
        public void GeneratesIdempotentCreateTable()
        {
            // Arrange
            var schema = TestHelpers.GetBasicSchema();
            var generator = new PgsqlDdlGeneratorStrategy();

            // Act
            var sql = generator.GenerateDdlString(schema, includeExtensions: false);

            // Assert
            sql.Should().Contain("CREATE TABLE IF NOT EXISTS dms.testproject_TestTable");
            sql.Should().Contain("Id BIGSERIAL PRIMARY KEY");
            sql.Should().Contain("Document_Id BIGINT NOT NULL");
            sql.Should().Contain("Document_PartitionKey SMALLINT NOT NULL");
            sql.Should().Contain("Name VARCHAR(100) NOT NULL");
            sql.Should().Contain("IsActive BOOLEAN");
            sql.Should().Contain("CONSTRAINT FK_TestTable_Document");
            sql.Should().Contain("REFERENCES dms.Document((Id, DocumentPartitionKey)) ON DELETE CASCADE");
            sql.Should().Contain("CONSTRAINT UQ_TestTable_NaturalKey");
        }

        [Test]
        public void GeneratesPrimaryKeyConstraint()
        {
            // Arrange
            var schema = TestHelpers.GetBasicSchema();
            var generator = new PgsqlDdlGeneratorStrategy();

            // Act
            var sql = generator.GenerateDdlString(schema, includeExtensions: false);

            // Assert
            sql.Should().Contain("Id BIGSERIAL PRIMARY KEY");
            sql.Should().NotContain("CONSTRAINT PK_");
        }

        [Test]
        public void GeneratesNotNullConstraintsForRequiredColumns()
        {
            // Arrange
            var schema = TestHelpers.GetBasicSchema();
            var generator = new PgsqlDdlGeneratorStrategy();

            // Act
            var sql = generator.GenerateDdlString(schema, includeExtensions: false);

            // Assert
            sql.Should().Contain("Id BIGSERIAL PRIMARY KEY");
            sql.Should().Contain("Document_Id BIGINT NOT NULL");
            sql.Should().Contain("Document_PartitionKey SMALLINT NOT NULL");
            sql.Should().Contain("Name VARCHAR(100) NOT NULL");
            sql.Should().Contain("IsActive BOOLEAN");
            sql.Should().NotContain("IsActive BOOLEAN NOT NULL");
        }

        [Test]
        public void GeneratesUnionViewForPolymorphicReferenceByDefault()
        {
            // Arrange
            var schema = TestHelpers.GetSchemaWithPolymorphicReference();
            var generator = new PgsqlDdlGeneratorStrategy();

            // Act
            var sql = generator.GenerateDdlString(schema, includeExtensions: false);

            // Assert
            sql.Should().Contain("CREATE OR REPLACE VIEW dms.EducationOrganizationReference AS");
            // Union views now include ALL columns per dbGeneration.md specification
            sql.Should().Contain("SELECT Id, EducationOrganizationId, SchoolName, 'School' AS Discriminator, Document_Id, Document_PartitionKey FROM dms.School");
            sql.Should().Contain("UNION ALL");
            sql.Should().Contain("SELECT Id, EducationOrganizationId, LeaName, 'LocalEducationAgency' AS Discriminator, Document_Id, Document_PartitionKey FROM dms.LocalEducationAgency");
        }

        [Test]
        public void SkipsUnionViewWhenFlagIsSet()
        {
            // Arrange
            var schema = TestHelpers.GetSchemaWithPolymorphicReference();
            var generator = new PgsqlDdlGeneratorStrategy();

            // Act
            var sql = generator.GenerateDdlString(schema, includeExtensions: false, skipUnionViews: true);

            // Assert
            sql.Should().NotContain("CREATE OR REPLACE VIEW");
            sql.Should().NotContain("UNION ALL");
        }

        [Test]
        public void GeneratesChildTables()
        {
            // Arrange
            var schema = TestHelpers.GetSchemaWithPolymorphicReference();
            var generator = new PgsqlDdlGeneratorStrategy();

            // Act
            var sql = generator.GenerateDdlString(schema, includeExtensions: false);

            // Assert
            sql.Should().Contain("CREATE TABLE IF NOT EXISTS dms.testproject_School");
            sql.Should().Contain("CREATE TABLE IF NOT EXISTS dms.testproject_LocalEducationAgency");
            sql.Should().Contain("EducationOrganizationReference_Id BIGINT NOT NULL");
            sql.Should().Contain("CONSTRAINT FK_School_EducationOrganizationReference");
            sql.Should().Contain("REFERENCES dms.testproject_EducationOrganizationReference(Id) ON DELETE CASCADE");
        }

        [Test]
        public void UnionViewIncludesAllColumnsPerSpecification()
        {
            // Arrange
            var schema = TestHelpers.GetSchemaWithPolymorphicReference();
            var generator = new PgsqlDdlGeneratorStrategy();

            // Act
            var sql = generator.GenerateDdlString(schema, includeExtensions: false);

            // Assert
            // Union views should include ALL columns per dbGeneration.md specification
            sql.Should().Contain("SELECT Id, EducationOrganizationId, SchoolName, 'School' AS Discriminator, Document_Id, Document_PartitionKey FROM dms.School");
            sql.Should().Contain("SELECT Id, EducationOrganizationId, LeaName, 'LocalEducationAgency' AS Discriminator, Document_Id, Document_PartitionKey FROM dms.LocalEducationAgency");
        }

        [Test]
        public void GeneratesTableCreationStatement()
        {
            // Arrange
            var schema = TestHelpers.GetBasicSchema();
            var generator = new PgsqlDdlGeneratorStrategy();

            // Act
            var sql = generator.GenerateDdlString(schema, includeExtensions: false);

            // Assert
            sql.Should().Contain("CREATE TABLE IF NOT EXISTS dms.testproject_TestTable");
        }

        [Test]
        public void GeneratesIndexesForForeignKeys()
        {
            // Arrange
            var schema = TestHelpers.GetBasicSchema();
            var generator = new PgsqlDdlGeneratorStrategy();

            // Act
            var sql = generator.GenerateDdlString(schema, includeExtensions: false);

            // Assert
            sql.Should().Contain("CREATE INDEX IF NOT EXISTS IX_TestTable_Document");
            sql.Should().Contain("ON dms.testproject_TestTable(Document_Id, Document_PartitionKey)");
        }

        [Test]
        public void HandlesEmptySchema()
        {
            // Arrange
            var schema = TestHelpers.GetEmptySchema();
            var generator = new PgsqlDdlGeneratorStrategy();

            // Act
            var sql = generator.GenerateDdlString(schema, includeExtensions: false);

            // Assert
            sql.Should().BeEmpty();
        }

        [Test]
        public void GeneratesExtensionTablesDifferently()
        {
            // Arrange
            var schema = GetSchemaWithExtensionTable();
            var generator = new PgsqlDdlGeneratorStrategy();

            // Act
            var sql = generator.GenerateDdlString(schema, includeExtensions: true);

            // Assert
            sql.Should().Contain("extensions.TestExtension");
            sql.Should().Contain("CREATE TABLE IF NOT EXISTS extensions.TestExtension");
        }

        [Test]
        public void SkipsExtensionTablesWhenFlagIsFalse()
        {
            // Arrange
            var schema = GetSchemaWithExtensionTable();
            var generator = new PgsqlDdlGeneratorStrategy();

            // Act
            var sql = generator.GenerateDdlString(schema, includeExtensions: false);

            // Assert
            sql.Should().NotContain("TestExtension");
        }

        [Test]
        public void GeneratesComplexConstraints()
        {
            // Arrange
            var schema = GetSchemaWithComplexConstraints();
            var generator = new PgsqlDdlGeneratorStrategy();

            // Act
            var sql = generator.GenerateDdlString(schema, includeExtensions: false);

            // Assert
            sql.Should().Contain("CONSTRAINT UQ_ComplexTable_NaturalKey");
            sql.Should().Contain("UNIQUE (FirstKey, SecondKey)");
        }

        [Test]
        public void GeneratesCorrectDataTypes()
        {
            // Arrange
            var schema = GetSchemaWithVariousDataTypes();
            var generator = new PgsqlDdlGeneratorStrategy();

            // Act
            var sql = generator.GenerateDdlString(schema, includeExtensions: false);

            // Assert
            sql.Should().Contain("INTEGER NOT NULL");
            sql.Should().Contain("VARCHAR(100) NOT NULL");
            sql.Should().Contain("BOOLEAN");
            sql.Should().Contain("DECIMAL(10, 2)");
        }

        private static ApiSchema GetSchemaWithExtensionTable()
        {
            return new ApiSchema
            {
                ProjectSchema = new ProjectSchema
                {
                    ProjectName = "ExtensionProject",
                    ProjectVersion = "1.0.0",
                    IsExtensionProject = true,
                    Description = "Extension test schema.",
                    ResourceSchemas = new Dictionary<string, ResourceSchema>
                    {
                        ["TestExtension"] = new ResourceSchema
                        {
                            ResourceName = "TestExtension",
                            FlatteningMetadata = new FlatteningMetadata
                            {
                                Table = new TableMetadata
                                {
                                    BaseName = "TestExtension",
                                    JsonPath = "$.TestExtension",
                                    IsExtensionTable = true,
                                    Columns =
                                    [
                                        new ColumnMetadata { ColumnName = "ExtensionId", ColumnType = "bigint", IsNaturalKey = true, IsRequired = true },
                                        new ColumnMetadata { ColumnName = "ExtensionValue", ColumnType = "string", MaxLength = "200", IsRequired = true }
                                    ],
                                    ChildTables = []
                                }
                            }
                        }
                    }
                }
            };
        }

        private static ApiSchema GetSchemaWithComplexConstraints()
        {
            return new ApiSchema
            {
                ProjectSchema = new ProjectSchema
                {
                    ProjectName = "ComplexProject",
                    ProjectVersion = "1.0.0",
                    IsExtensionProject = false,
                    Description = "Complex constraint test schema.",
                    ResourceSchemas = new Dictionary<string, ResourceSchema>
                    {
                        ["ComplexTable"] = new ResourceSchema
                        {
                            ResourceName = "ComplexTable",
                            FlatteningMetadata = new FlatteningMetadata
                            {
                                Table = new TableMetadata
                                {
                                    BaseName = "ComplexTable",
                                    JsonPath = "$.ComplexTable",
                                    Columns =
                                    [
                                        new ColumnMetadata { ColumnName = "FirstKey", ColumnType = "bigint", IsNaturalKey = true, IsRequired = true },
                                        new ColumnMetadata { ColumnName = "SecondKey", ColumnType = "string", MaxLength = "50", IsNaturalKey = true, IsRequired = true },
                                        new ColumnMetadata { ColumnName = "Value", ColumnType = "string", MaxLength = "100", IsRequired = false }
                                    ],
                                    ChildTables = []
                                }
                            }
                        }
                    }
                }
            };
        }

        private static ApiSchema GetSchemaWithVariousDataTypes()
        {
            return new ApiSchema
            {
                ProjectSchema = new ProjectSchema
                {
                    ProjectName = "DataTypesProject",
                    ProjectVersion = "1.0.0",
                    IsExtensionProject = false,
                    Description = "Data types test schema.",
                    ResourceSchemas = new Dictionary<string, ResourceSchema>
                    {
                        ["DataTypesTable"] = new ResourceSchema
                        {
                            ResourceName = "DataTypesTable",
                            FlatteningMetadata = new FlatteningMetadata
                            {
                                Table = new TableMetadata
                                {
                                    BaseName = "DataTypesTable",
                                    JsonPath = "$.DataTypesTable",
                                    Columns =
                                    [
                                        new ColumnMetadata { ColumnName = "IntField", ColumnType = "int32", IsNaturalKey = true, IsRequired = true },
                                        new ColumnMetadata { ColumnName = "StringField", ColumnType = "string", MaxLength = "100", IsRequired = true },
                                        new ColumnMetadata { ColumnName = "BoolField", ColumnType = "bool", IsRequired = false },
                                        new ColumnMetadata { ColumnName = "DecimalField", ColumnType = "decimal", Precision = "10", Scale = "2", IsRequired = false }
                                    ],
                                    ChildTables = []
                                }
                            }
                        }
                    }
                }
            };
        }

        [Test]
        public void UsesPrefixedTableNamesByDefault()
        {
            // Arrange
            var schema = GetEdFiSchema();
            var generator = new PgsqlDdlGeneratorStrategy();
            var options = new DdlGenerationOptions
            {
                UsePrefixedTableNames = true // Default behavior
            };

            // Act
            var sql = generator.GenerateDdlString(schema, options);

            // Assert
            sql.Should().Contain("CREATE TABLE IF NOT EXISTS dms.edfi_School");
            sql.Should().NotContain("CREATE SCHEMA IF NOT EXISTS edfi");
        }

        [Test]
        public void UsesSeparateSchemasWhenPrefixedDisabled()
        {
            // Arrange
            var schema = GetEdFiSchema();
            var generator = new PgsqlDdlGeneratorStrategy();
            var options = new DdlGenerationOptions
            {
                UsePrefixedTableNames = false
            };

            // Act
            var sql = generator.GenerateDdlString(schema, options);

            // Assert
            sql.Should().Contain("CREATE SCHEMA IF NOT EXISTS edfi");
            sql.Should().Contain("CREATE TABLE IF NOT EXISTS edfi.School");
            sql.Should().NotContain("edfi_School");
        }

        private static ApiSchema GetEdFiSchema()
        {
            return new ApiSchema
            {
                ProjectSchema = new ProjectSchema
                {
                    ProjectName = "EdFi",
                    ProjectVersion = "5.0.0",
                    IsExtensionProject = false,
                    Description = "Ed-Fi core schema.",
                    ResourceSchemas = new Dictionary<string, ResourceSchema>
                    {
                        ["School"] = new ResourceSchema
                        {
                            ResourceName = "School",
                            FlatteningMetadata = new FlatteningMetadata
                            {
                                Table = new TableMetadata
                                {
                                    BaseName = "School",
                                    JsonPath = "$.School",
                                    Columns =
                                    [
                                        new ColumnMetadata { ColumnName = "SchoolId", ColumnType = "int32", IsNaturalKey = true, IsRequired = true },
                                        new ColumnMetadata { ColumnName = "SchoolName", ColumnType = "string", MaxLength = "100", IsRequired = true }
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
