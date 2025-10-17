// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.SchemaGenerator.Abstractions;
using EdFi.DataManagementService.SchemaGenerator.Pgsql;
using FluentAssertions;
using System.Text.RegularExpressions;

namespace EdFi.DataManagementService.SchemaGenerator.Tests.Unit.Engines.PostgreSQL
{
    /// <summary>
    /// PostgreSQL-specific unit tests for error handling and edge cases.
    /// </summary>
    [TestFixture]
    public class PgsqlErrorHandlingTests
    {
        [Test]
        public void PgsqlGenerator_WithNullSchema_ThrowsNullReferenceException()
        {
            // Arrange
            var generator = new PgsqlDdlGeneratorStrategy();

            // Act & Assert
            Action act = () => generator.GenerateDdlString(null!, includeExtensions: false);
            act.Should().Throw<NullReferenceException>();
        }

        [Test]
        public void PgsqlGenerator_WithNullProjectSchema_HandlesGracefully()
        {
            // Arrange
            var schema = new ApiSchema { ProjectSchema = null! };
            var generator = new PgsqlDdlGeneratorStrategy();

            // Act & Assert
            Action act = () => generator.GenerateDdlString(schema, includeExtensions: false);
            act.Should().Throw<InvalidDataException>()
                .WithMessage("ApiSchema does not contain valid projectSchema.");
        }

        [Test]
        public void PgsqlGenerator_WithTableHavingNoColumns_GeneratesEmptyTable()
        {
            // Arrange
            var schema = GetSchemaWithEmptyTable();
            var generator = new PgsqlDdlGeneratorStrategy();

            // Act
            var sql = generator.GenerateDdlString(schema, includeExtensions: false);

            // Assert
            sql.Should().Contain("CREATE TABLE IF NOT EXISTS dms.emptytableproject_EmptyTable");
            sql.Should().Contain("Id BIGSERIAL PRIMARY KEY");
            sql.Should().Contain("Document_Id BIGINT NOT NULL");
            sql.Should().Contain("Document_PartitionKey SMALLINT NOT NULL");
        }

        [Test]
        public void PgsqlGenerator_WithSpecialCharactersInTableName_HandlesCorrectly()
        {
            // Arrange
            var schema = GetSchemaWithSpecialCharacters();
            var generator = new PgsqlDdlGeneratorStrategy();

            // Act
            var sql = generator.GenerateDdlString(schema, includeExtensions: false);

            // Assert
            sql.Should().Contain("CREATE TABLE IF NOT EXISTS dms.specialcharsproject_TableWithDashes");
        }

        [Test]
        public void PgsqlGenerator_WithVeryLongColumnNames_TruncatesAppropriately()
        {
            // Arrange
            var schema = GetSchemaWithLongColumnNames();
            var generator = new PgsqlDdlGeneratorStrategy();

            // Act
            var sql = generator.GenerateDdlString(schema, includeExtensions: false);

            // Assert
            sql.Should().NotBeEmpty();

            // PostgreSQL truncates identifiers longer than 63 characters
            // The column name is 70 characters, so it should be truncated to 63
            var longColumnName = "VeryLongColumnNameThatExceedsTypicalLimitsAndShouldBeHandledGracefully";
            longColumnName.Length.Should().Be(70); // Verify our test data is correct

            // PostgreSQL should truncate this to 63 characters with a hash suffix for uniqueness
            var truncatedBaseName = longColumnName.Substring(0, 54); // Leave room for hash suffix
            sql.Should().Contain(truncatedBaseName); // Should contain the truncated base name
            sql.Should().NotContain(longColumnName); // Should not contain the full name

            // PostgreSQL adds a hash suffix to make truncated names unique
            // The total length should be exactly 63 characters
            sql.Should().MatchRegex(@$"{Regex.Escape(truncatedBaseName)}_[a-f0-9]{{8}}");
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
