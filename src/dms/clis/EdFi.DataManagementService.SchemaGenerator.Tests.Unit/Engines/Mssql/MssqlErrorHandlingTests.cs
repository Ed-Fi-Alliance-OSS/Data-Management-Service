// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.SchemaGenerator.Abstractions;
using EdFi.DataManagementService.SchemaGenerator.Mssql;
using FluentAssertions;

namespace EdFi.DataManagementService.SchemaGenerator.Tests.Unit.Engines.Mssql
{
    /// <summary>
    /// MSSQL-specific unit tests for error handling and edge cases.
    /// </summary>
    [TestFixture]
    public class MssqlErrorHandlingTests
    {
        [Test]
        public void MssqlGenerator_WithNullSchema_ThrowsNullReferenceException()
        {
            // Arrange
            var generator = new MssqlDdlGeneratorStrategy();

            // Act & Assert
            Action act = () => generator.GenerateDdlString(null!, includeExtensions: false);
            act.Should().Throw<NullReferenceException>();
        }

        [Test]
        public void MssqlGenerator_WithNullProjectSchema_HandlesGracefully()
        {
            // Arrange
            var schema = new ApiSchema { ProjectSchema = null! };
            var generator = new MssqlDdlGeneratorStrategy();

            // Act & Assert
            Action act = () => generator.GenerateDdlString(schema, includeExtensions: false);
            act.Should().Throw<InvalidDataException>()
                .WithMessage("ApiSchema does not contain valid projectSchema.");
        }

        [Test]
        public void MssqlGenerator_WithTableHavingNoColumns_GeneratesEmptyTable()
        {
            // Arrange
            var schema = GetSchemaWithEmptyTable();
            var generator = new MssqlDdlGeneratorStrategy();

            // Act
            var sql = generator.GenerateDdlString(schema, includeExtensions: false);

            // Assert
            sql.Should().Contain("CREATE TABLE [dms].[EmptyTable]");
            sql.Should().Contain("[Id] BIGINT PRIMARY KEY IDENTITY(1,1)");
            sql.Should().Contain("[Document_Id] BIGINT NOT NULL");
            sql.Should().Contain("[Document_PartitionKey] TINYINT NOT NULL");
        }

        [Test]
        public void MssqlGenerator_WithSpecialCharactersInTableName_HandlesCorrectly()
        {
            // Arrange
            var schema = GetSchemaWithSpecialCharacters();
            var generator = new MssqlDdlGeneratorStrategy();

            // Act
            var sql = generator.GenerateDdlString(schema, includeExtensions: false);

            // Assert
            sql.Should().Contain("CREATE TABLE [dms].[Table-With-Dashes]");
        }

        [Test]
        public void MssqlGenerator_WithVeryLongColumnNames_HandlesCorrectly()
        {
            // Arrange
            var schema = GetSchemaWithLongColumnNames();
            var generator = new MssqlDdlGeneratorStrategy();

            // Act
            var sql = generator.GenerateDdlString(schema, includeExtensions: false);

            // Assert
            sql.Should().NotBeEmpty();
            sql.Should().Contain("[VeryLongColumnNameThatExceedsTypicalLimitsAndShouldBeHandledGracefully]");
        }

        private static ApiSchema GetSchemaWithEmptyTable()
        {
            return new ApiSchema
            {
                ProjectSchema = new ProjectSchema
                {
                    ProjectName = "EmptyTableProject",
                    ProjectVersion = "1.0.0",
                    IsExtensionProject = false,
                    Description = "Schema with empty table.",
                    ResourceSchemas = new Dictionary<string, ResourceSchema>
                    {
                        ["EmptyTable"] = new ResourceSchema
                        {
                            ResourceName = "EmptyTable",
                            FlatteningMetadata = new FlatteningMetadata
                            {
                                Table = new TableMetadata
                                {
                                    BaseName = "EmptyTable",
                                    JsonPath = "$.EmptyTable",
                                    Columns = [], // Empty columns list
                                    ChildTables = []
                                }
                            }
                        }
                    }
                }
            };
        }

        private static ApiSchema GetSchemaWithSpecialCharacters()
        {
            return new ApiSchema
            {
                ProjectSchema = new ProjectSchema
                {
                    ProjectName = "SpecialCharsProject",
                    ProjectVersion = "1.0.0",
                    IsExtensionProject = false,
                    Description = "Schema with special characters.",
                    ResourceSchemas = new Dictionary<string, ResourceSchema>
                    {
                        ["Table-With-Dashes"] = new ResourceSchema
                        {
                            ResourceName = "Table-With-Dashes",
                            FlatteningMetadata = new FlatteningMetadata
                            {
                                Table = new TableMetadata
                                {
                                    BaseName = "Table-With-Dashes",
                                    JsonPath = "$.Table-With-Dashes",
                                    Columns =
                                    [
                                        new ColumnMetadata { ColumnName = "Column-With-Dashes", ColumnType = "string", MaxLength = "50", IsNaturalKey = true, IsRequired = true }
                                    ],
                                    ChildTables = []
                                }
                            }
                        }
                    }
                }
            };
        }

        private static ApiSchema GetSchemaWithLongColumnNames()
        {
            return new ApiSchema
            {
                ProjectSchema = new ProjectSchema
                {
                    ProjectName = "LongNamesProject",
                    ProjectVersion = "1.0.0",
                    IsExtensionProject = false,
                    Description = "Schema with long column names.",
                    ResourceSchemas = new Dictionary<string, ResourceSchema>
                    {
                        ["LongNamesTable"] = new ResourceSchema
                        {
                            ResourceName = "LongNamesTable",
                            FlatteningMetadata = new FlatteningMetadata
                            {
                                Table = new TableMetadata
                                {
                                    BaseName = "LongNamesTable",
                                    JsonPath = "$.LongNamesTable",
                                    Columns =
                                    [
                                        new ColumnMetadata
                                        {
                                            ColumnName = "VeryLongColumnNameThatExceedsTypicalLimitsAndShouldBeHandledGracefully",
                                            ColumnType = "string",
                                            MaxLength = "100",
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
        }
    }
}