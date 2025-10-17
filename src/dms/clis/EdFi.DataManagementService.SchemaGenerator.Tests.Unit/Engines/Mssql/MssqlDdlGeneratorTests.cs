// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.SchemaGenerator.Abstractions;
using EdFi.DataManagementService.SchemaGenerator.Mssql;
using EdFi.DataManagementService.SchemaGenerator.Tests.Unit.Shared;
using FluentAssertions;

namespace EdFi.DataManagementService.SchemaGenerator.Tests.Unit.Engines.Mssql
{
    /// <summary>
    /// Unit tests for SQL Server DDL generation.
    /// </summary>
    [TestFixture]
    public class MssqlDdlGeneratorTests
    {
        [Test]
        public void GeneratesIdempotentCreateTable()
        {
            // Arrange
            var schema = TestHelpers.GetBasicSchema();
            var generator = new MssqlDdlGeneratorStrategy();

            // Act
            var sql = generator.GenerateDdlString(schema, includeExtensions: false);

            // Assert
            sql.Should().Contain("IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'testproject_TestTable' AND SCHEMA_NAME(schema_id) = 'dms')");
            sql.Should().Contain("[Id] BIGINT PRIMARY KEY IDENTITY(1,1)");
            sql.Should().Contain("[Document_Id] BIGINT NOT NULL");
            sql.Should().Contain("[Document_PartitionKey] TINYINT NOT NULL");
            sql.Should().Contain("[Name] NVARCHAR(100) NOT NULL");
            sql.Should().Contain("[IsActive] BIT");
            sql.Should().Contain("CONSTRAINT [FK_TestTable_Document]");
            sql.Should().Contain("REFERENCES [dms].[Document]([(Id, DocumentPartitionKey)]) ON DELETE CASCADE");
            sql.Should().Contain("CONSTRAINT [UQ_TestTable_NaturalKey]");
        }

        [Test]
        public void GeneratesPrimaryKeyConstraint()
        {
            // Arrange
            var schema = TestHelpers.GetBasicSchema();
            var generator = new MssqlDdlGeneratorStrategy();

            // Act
            var sql = generator.GenerateDdlString(schema, includeExtensions: false);

            // Assert
            sql.Should().Contain("[Id] BIGINT PRIMARY KEY IDENTITY(1,1)");
            sql.Should().NotContain("CONSTRAINT [PK_TestTable]");
        }

        [Test]
        public void GeneratesNotNullConstraintsForRequiredColumns()
        {
            // Arrange
            var schema = TestHelpers.GetBasicSchema();
            var generator = new MssqlDdlGeneratorStrategy();

            // Act
            var sql = generator.GenerateDdlString(schema, includeExtensions: false);

            // Assert
            sql.Should().Contain("[Id] BIGINT PRIMARY KEY IDENTITY(1,1)");
            sql.Should().Contain("[Document_Id] BIGINT NOT NULL");
            sql.Should().Contain("[Document_PartitionKey] TINYINT NOT NULL");
            sql.Should().Contain("[Name] NVARCHAR(100) NOT NULL");
            sql.Should().Contain("[IsActive] BIT");
            sql.Should().NotContain("[IsActive] BIT NOT NULL");
        }

        [Test]
        public void GeneratesUnionViewForPolymorphicReferenceByDefault()
        {
            // Arrange
            var schema = TestHelpers.GetSchemaWithPolymorphicReference();
            var generator = new MssqlDdlGeneratorStrategy();

            // Act
            var sql = generator.GenerateDdlString(schema, includeExtensions: false);

            // Assert
            sql.Should().Contain("CREATE VIEW [dms].[EducationOrganizationReference] AS");
            // Union views now include ALL columns per dbGeneration.md specification
            sql.Should().Contain("SELECT [Id], [EducationOrganizationId], [SchoolName], ''School'' AS [Discriminator], [Document_Id], [Document_PartitionKey] FROM [dms].[School]");
            sql.Should().Contain("UNION ALL");
            sql.Should().Contain("SELECT [Id], [EducationOrganizationId], [LeaName], ''LocalEducationAgency'' AS [Discriminator], [Document_Id], [Document_PartitionKey] FROM [dms].[LocalEducationAgency]");
        }

        [Test]
        public void SkipsUnionViewWhenFlagIsSet()
        {
            // Arrange
            var schema = TestHelpers.GetSchemaWithPolymorphicReference();
            var generator = new MssqlDdlGeneratorStrategy();

            // Act
            var sql = generator.GenerateDdlString(schema, includeExtensions: false, skipUnionViews: true);

            // Assert
            sql.Should().NotContain("CREATE OR ALTER VIEW");
            sql.Should().NotContain("UNION ALL");
        }

        [Test]
        public void GeneratesChildTables()
        {
            // Arrange
            var schema = TestHelpers.GetSchemaWithPolymorphicReference();
            var generator = new MssqlDdlGeneratorStrategy();

            // Act
            var sql = generator.GenerateDdlString(schema, includeExtensions: false);

            // Assert
            sql.Should().Contain("[dms].[testproject_School]");
            sql.Should().Contain("[dms].[testproject_LocalEducationAgency]");
            sql.Should().Contain("[EducationOrganizationReference_Id] BIGINT NOT NULL");
            sql.Should().Contain("CONSTRAINT [FK_School_EducationOrganizationReference]");
            sql.Should().Contain("REFERENCES [dms].[testproject_EducationOrganizationReference]([Id]) ON DELETE CASCADE");
        }

        [Test]
        public void UnionViewIncludesAllColumnsPerSpecification()
        {
            // Arrange
            var schema = TestHelpers.GetSchemaWithPolymorphicReference();
            var generator = new MssqlDdlGeneratorStrategy();

            // Act
            var sql = generator.GenerateDdlString(schema, includeExtensions: false);

            // Assert
            // Union views should include ALL columns per dbGeneration.md specification
            sql.Should().Contain("SELECT [Id], [EducationOrganizationId], [SchoolName], ''School'' AS [Discriminator], [Document_Id], [Document_PartitionKey] FROM [dms].[School]");
            sql.Should().Contain("SELECT [Id], [EducationOrganizationId], [LeaName], ''LocalEducationAgency'' AS [Discriminator], [Document_Id], [Document_PartitionKey] FROM [dms].[LocalEducationAgency]");
        }

        [Test]
        public void UsesBracketedIdentifiersForTableAndColumnNames()
        {
            // Arrange
            var schema = TestHelpers.GetBasicSchema();
            var generator = new MssqlDdlGeneratorStrategy();

            // Act
            var sql = generator.GenerateDdlString(schema, includeExtensions: false);

            // Assert
            sql.Should().Contain("[testproject_TestTable]");
            sql.Should().Contain("[Id]");
            sql.Should().Contain("[Name]");
            sql.Should().Contain("[IsActive]");
        }

        [Test]
        public void GeneratesTableCreationStatement()
        {
            // Arrange
            var schema = TestHelpers.GetBasicSchema();
            var generator = new MssqlDdlGeneratorStrategy();

            // Act
            var sql = generator.GenerateDdlString(schema, includeExtensions: false);

            // Assert
            sql.Should().Contain("CREATE TABLE [dms].[testproject_TestTable]");
            sql.Should().Contain("IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'testproject_TestTable'");
        }

        [Test]
        public void GeneratesIndexesForForeignKeys()
        {
            // Arrange
            var schema = TestHelpers.GetBasicSchema();
            var generator = new MssqlDdlGeneratorStrategy();

            // Act
            var sql = generator.GenerateDdlString(schema, includeExtensions: false);

            // Assert
            sql.Should().Contain("CREATE NONCLUSTERED INDEX [IX_TestTable_Document]");
            sql.Should().Contain("ON [dms].[testproject_TestTable]([Document_Id], [Document_PartitionKey])");
        }

        [Test]
        public void HandlesEmptySchema()
        {
            // Arrange
            var schema = TestHelpers.GetEmptySchema();
            var generator = new MssqlDdlGeneratorStrategy();

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
            var generator = new MssqlDdlGeneratorStrategy();

            // Act
            var sql = generator.GenerateDdlString(schema, includeExtensions: true);

            // Assert
            sql.Should().Contain("[extensions].[TestExtension]");
            sql.Should().Contain("CREATE TABLE [extensions].[TestExtension]");
        }

        [Test]
        public void SkipsExtensionTablesWhenFlagIsFalse()
        {
            // Arrange
            var schema = GetSchemaWithExtensionTable();
            var generator = new MssqlDdlGeneratorStrategy();

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
            var generator = new MssqlDdlGeneratorStrategy();

            // Act
            var sql = generator.GenerateDdlString(schema, includeExtensions: false);

            // Assert
            sql.Should().Contain("CONSTRAINT [UQ_ComplexTable_NaturalKey]");
            sql.Should().Contain("UNIQUE ([FirstKey], [SecondKey])");
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

        [Test]
        public void UsesPrefixedTableNamesByDefault()
        {
            // Arrange
            var schema = GetEdFiSchema();
            var generator = new MssqlDdlGeneratorStrategy();
            var options = new DdlGenerationOptions
            {
                UsePrefixedTableNames = true // Default behavior
            };

            // Act
            var sql = generator.GenerateDdlString(schema, options);

            // Assert
            sql.Should().Contain("CREATE TABLE [dms].[edfi_School]");
            sql.Should().NotContain("CREATE SCHEMA [edfi]");
        }

        [Test]
        public void UsesSeparateSchemasWhenPrefixedDisabled()
        {
            // Arrange
            var schema = GetEdFiSchema();
            var generator = new MssqlDdlGeneratorStrategy();
            var options = new DdlGenerationOptions
            {
                UsePrefixedTableNames = false
            };

            // Act
            var sql = generator.GenerateDdlString(schema, options);

            // Assert
            sql.Should().Contain("CREATE SCHEMA [edfi]");
            sql.Should().Contain("CREATE TABLE [edfi].[School]");
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
