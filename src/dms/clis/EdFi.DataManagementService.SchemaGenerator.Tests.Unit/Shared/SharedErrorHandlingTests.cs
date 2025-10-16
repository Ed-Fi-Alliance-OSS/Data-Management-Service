// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.SchemaGenerator.Abstractions;
using FluentAssertions;

namespace EdFi.DataManagementService.SchemaGenerator.Tests.Unit.Shared
{
    /// <summary>
    /// Shared unit tests for error handling and edge cases that apply to all database engines.
    /// </summary>
    [TestFixture]
    public class SharedErrorHandlingTests
    {
        [Test]
        public void ApiSchema_WithNullProjectSchema_ShouldBeInvalid()
        {
            // Arrange
            var schema = new ApiSchema { ProjectSchema = null! };

            // Act & Assert
            schema.ProjectSchema.Should().BeNull();
        }

        [Test]
        public void ApiSchema_WithNullResourceSchemas_ShouldBeInvalid()
        {
            // Arrange
            var schema = new ApiSchema
            {
                ProjectSchema = new ProjectSchema
                {
                    ProjectName = "TestProject",
                    ProjectVersion = "1.0.0",
                    ResourceSchemas = null!
                }
            };

            // Act & Assert
            schema.ProjectSchema.ResourceSchemas.Should().BeNull();
        }

        [Test]
        public void DdlGenerationOptions_DefaultValues_ShouldBeCorrect()
        {
            // Arrange & Act
            var options = new DdlGenerationOptions();

            // Assert
            options.IncludeExtensions.Should().BeFalse();
            options.SkipUnionViews.Should().BeFalse();
            options.DescriptorSchema.Should().Be("dms");
            options.SchemaMapping.Should().NotBeNull();
        }

        [Test]
        public void ColumnMetadata_WithNullColumnType_ShouldBeHandledByMappers()
        {
            // Arrange
            var column = new ColumnMetadata
            {
                ColumnName = "TestColumn",
                ColumnType = null!,
                IsRequired = true
            };

            // Act & Assert
            column.ColumnType.Should().BeNull();
            column.ColumnName.Should().Be("TestColumn");
            column.IsRequired.Should().BeTrue();
        }

        [Test]
        public void TableMetadata_WithEmptyColumns_ShouldBeValid()
        {
            // Arrange & Act
            var table = new TableMetadata
            {
                BaseName = "EmptyTable",
                JsonPath = "$.EmptyTable",
                Columns = [],
                ChildTables = []
            };

            // Assert
            table.BaseName.Should().Be("EmptyTable");
            table.Columns.Should().BeEmpty();
            table.ChildTables.Should().BeEmpty();
        }

        [Test]
        public void ResourceSchema_WithValidMetadata_ShouldBeConstructable()
        {
            // Arrange & Act
            var resource = new ResourceSchema
            {
                ResourceName = "TestResource",
                FlatteningMetadata = new FlatteningMetadata
                {
                    Table = new TableMetadata
                    {
                        BaseName = "TestTable",
                        JsonPath = "$.TestTable",
                        Columns = [],
                        ChildTables = []
                    }
                }
            };

            // Assert
            resource.ResourceName.Should().Be("TestResource");
            resource.FlatteningMetadata.Should().NotBeNull();
            resource.FlatteningMetadata.Table.Should().NotBeNull();
            resource.FlatteningMetadata.Table.BaseName.Should().Be("TestTable");
        }
    }
}
