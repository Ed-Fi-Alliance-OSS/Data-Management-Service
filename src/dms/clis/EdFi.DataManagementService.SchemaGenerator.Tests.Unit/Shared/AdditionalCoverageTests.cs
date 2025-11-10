// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.SchemaGenerator.Abstractions;
using EdFi.DataManagementService.SchemaGenerator.Mssql;
using EdFi.DataManagementService.SchemaGenerator.Pgsql;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.SchemaGenerator.Tests.Unit.Shared
{
    /// <summary>
    /// Additional tests to increase branch coverage for edge cases and conditional paths.
    /// </summary>
    [TestFixture]
    public class AdditionalCoverageTests
    {
        [Test]
        public void PgsqlGenerator_WithEmptyResourceName_HandlesGracefully()
        {
            // Arrange
            var logger = LoggerFactory.Create(builder => { }).CreateLogger<PgsqlDdlGeneratorStrategy>();
            var generator = new PgsqlDdlGeneratorStrategy(logger);
            var apiSchema = new ApiSchema
            {
                ApiSchemaVersion = "1.0.0",
                ProjectSchema = new ProjectSchema
                {
                    ProjectName = "TestProject",
                    ProjectVersion = "1.0.0",
                    ResourceSchemas = new Dictionary<string, ResourceSchema>
                    {
                        [""] = new ResourceSchema
                        {
                            ResourceName = "",
                            FlatteningMetadata = new FlatteningMetadata
                            {
                                Table = new TableMetadata
                                {
                                    BaseName = "EmptyResource",
                                    JsonPath = "$.test",
                                    Columns =
                                    [
                                        new ColumnMetadata
                                        {
                                            ColumnName = "Id",
                                            ColumnType = "int",
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

            // Act
            var result = generator.GenerateDdlString(apiSchema, false);

            // Assert
            result.Should().NotBeNullOrEmpty();
        }

        [Test]
        public void MssqlGenerator_WithEmptyResourceName_HandlesGracefully()
        {
            // Arrange
            var logger = LoggerFactory.Create(builder => { }).CreateLogger<MssqlDdlGeneratorStrategy>();
            var generator = new MssqlDdlGeneratorStrategy(logger);
            var apiSchema = new ApiSchema
            {
                ApiSchemaVersion = "1.0.0",
                ProjectSchema = new ProjectSchema
                {
                    ProjectName = "TestProject",
                    ProjectVersion = "1.0.0",
                    ResourceSchemas = new Dictionary<string, ResourceSchema>
                    {
                        [""] = new ResourceSchema
                        {
                            ResourceName = "",
                            FlatteningMetadata = new FlatteningMetadata
                            {
                                Table = new TableMetadata
                                {
                                    BaseName = "EmptyResource",
                                    JsonPath = "$.test",
                                    Columns =
                                    [
                                        new ColumnMetadata
                                        {
                                            ColumnName = "Id",
                                            ColumnType = "int",
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

            // Act
            var result = generator.GenerateDdlString(apiSchema, false);

            // Assert
            result.Should().NotBeNullOrEmpty();
        }

        [Test]
        public void DdlGenerationOptions_WithNullSchemaMapping_InitializesCorrectly()
        {
            // Arrange & Act
            var options = new DdlGenerationOptions { SchemaMapping = null! };

            // Assert
            options.DefaultSchema.Should().Be("dms");
        }

        [Test]
        public void PgsqlGenerator_WithMultipleChildTables_GeneratesAllTables()
        {
            // Arrange
            var logger = LoggerFactory.Create(builder => { }).CreateLogger<PgsqlDdlGeneratorStrategy>();
            var generator = new PgsqlDdlGeneratorStrategy(logger);
            var apiSchema = new ApiSchema
            {
                ApiSchemaVersion = "1.0.0",
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
                                    BaseName = "Parent",
                                    JsonPath = "$.parent",
                                    Columns =
                                    [
                                        new ColumnMetadata
                                        {
                                            ColumnName = "ParentId",
                                            ColumnType = "int",
                                            IsRequired = true,
                                            IsNaturalKey = true,
                                        },
                                    ],
                                    ChildTables =
                                    [
                                        new TableMetadata
                                        {
                                            BaseName = "Child1",
                                            JsonPath = "$.children1",
                                            Columns =
                                            [
                                                new ColumnMetadata
                                                {
                                                    ColumnName = "Child1Id",
                                                    ColumnType = "int",
                                                    IsRequired = true,
                                                },
                                            ],
                                            ChildTables = [],
                                        },
                                        new TableMetadata
                                        {
                                            BaseName = "Child2",
                                            JsonPath = "$.children2",
                                            Columns =
                                            [
                                                new ColumnMetadata
                                                {
                                                    ColumnName = "Child2Id",
                                                    ColumnType = "int",
                                                    IsRequired = true,
                                                },
                                            ],
                                            ChildTables = [],
                                        },
                                    ],
                                },
                            },
                        },
                    },
                },
            };

            // Act
            var result = generator.GenerateDdlString(apiSchema, false);

            // Assert
            result.Should().Contain("Child1");
            result.Should().Contain("Child2");
        }

        [Test]
        public void MssqlGenerator_WithMultipleChildTables_GeneratesAllTables()
        {
            // Arrange
            var logger = LoggerFactory.Create(builder => { }).CreateLogger<MssqlDdlGeneratorStrategy>();
            var generator = new MssqlDdlGeneratorStrategy(logger);
            var apiSchema = new ApiSchema
            {
                ApiSchemaVersion = "1.0.0",
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
                                    BaseName = "Parent",
                                    JsonPath = "$.parent",
                                    Columns =
                                    [
                                        new ColumnMetadata
                                        {
                                            ColumnName = "ParentId",
                                            ColumnType = "int",
                                            IsRequired = true,
                                            IsNaturalKey = true,
                                        },
                                    ],
                                    ChildTables =
                                    [
                                        new TableMetadata
                                        {
                                            BaseName = "Child1",
                                            JsonPath = "$.children1",
                                            Columns =
                                            [
                                                new ColumnMetadata
                                                {
                                                    ColumnName = "Child1Id",
                                                    ColumnType = "int",
                                                    IsRequired = true,
                                                },
                                            ],
                                            ChildTables = [],
                                        },
                                        new TableMetadata
                                        {
                                            BaseName = "Child2",
                                            JsonPath = "$.children2",
                                            Columns =
                                            [
                                                new ColumnMetadata
                                                {
                                                    ColumnName = "Child2Id",
                                                    ColumnType = "int",
                                                    IsRequired = true,
                                                },
                                            ],
                                            ChildTables = [],
                                        },
                                    ],
                                },
                            },
                        },
                    },
                },
            };

            // Act
            var result = generator.GenerateDdlString(apiSchema, false);

            // Assert
            result.Should().Contain("Child1");
            result.Should().Contain("Child2");
        }

        [Test]
        public void PgsqlGenerator_WithColumnWithoutMaxLength_UsesText()
        {
            // Arrange
            var logger = LoggerFactory.Create(builder => { }).CreateLogger<PgsqlDdlGeneratorStrategy>();
            var generator = new PgsqlDdlGeneratorStrategy(logger);
            var apiSchema = new ApiSchema
            {
                ApiSchemaVersion = "1.0.0",
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
                                    BaseName = "Test",
                                    JsonPath = "$.test",
                                    Columns =
                                    [
                                        new ColumnMetadata
                                        {
                                            ColumnName = "Description",
                                            ColumnType = "string",
                                            MaxLength = null,
                                            IsRequired = false,
                                        },
                                    ],
                                    ChildTables = [],
                                },
                            },
                        },
                    },
                },
            };

            // Act
            var result = generator.GenerateDdlString(apiSchema, false);

            // Assert
            result.Should().Contain("TEXT");
        }

        [Test]
        public void MssqlGenerator_WithColumnWithoutMaxLength_UsesNVarcharMax()
        {
            // Arrange
            var logger = LoggerFactory.Create(builder => { }).CreateLogger<MssqlDdlGeneratorStrategy>();
            var generator = new MssqlDdlGeneratorStrategy(logger);
            var apiSchema = new ApiSchema
            {
                ApiSchemaVersion = "1.0.0",
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
                                    BaseName = "Test",
                                    JsonPath = "$.test",
                                    Columns =
                                    [
                                        new ColumnMetadata
                                        {
                                            ColumnName = "Description",
                                            ColumnType = "string",
                                            MaxLength = null,
                                            IsRequired = false,
                                        },
                                    ],
                                    ChildTables = [],
                                },
                            },
                        },
                    },
                },
            };

            // Act
            var result = generator.GenerateDdlString(apiSchema, false);

            // Assert
            result.Should().Contain("NVARCHAR(MAX)");
        }

        [Test]
        public void DdlGenerationOptions_ResolveTablePrefix_WithComplexProjectName_HandlesCorrectly()
        {
            // Arrange
            var options = new DdlGenerationOptions { UsePrefixedTableNames = true };
            options.SchemaMapping["MyCustomProject123"] = "custom";

            // Act
            var result = options.ResolveTablePrefix("MyCustomProject123");

            // Assert
            result.Should().Be("custom_");
        }

        [Test]
        public void DdlGenerationOptions_ResolveSchemaName_WithComplexProjectName_HandlesCorrectly()
        {
            // Arrange
            var options = new DdlGenerationOptions { UsePrefixedTableNames = false };
            options.SchemaMapping["MyCustomProject123"] = "custom";

            // Act
            var result = options.ResolveSchemaName("MyCustomProject123");

            // Assert
            result.Should().Be("custom");
        }
    }
}
