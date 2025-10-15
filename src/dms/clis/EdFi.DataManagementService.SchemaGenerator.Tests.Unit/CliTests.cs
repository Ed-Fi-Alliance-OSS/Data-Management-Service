// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.SchemaGenerator.Abstractions;
using FluentAssertions;

namespace EdFi.DataManagementService.SchemaGenerator.Tests.Unit
{
    /// <summary>
    /// Unit tests for API schema abstractions.
    /// </summary>
    [TestFixture]
    public class ApiSchemaTests
    {
        [Test]
        public void ApiSchema_WithValidProjectSchema_ShouldReturnCorrectProperties()
        {
            // Arrange
            var projectSchema = new ProjectSchema
            {
                ProjectName = "TestProject",
                ProjectVersion = "1.0.0",
                IsExtensionProject = false,
                Description = "Test project description",
                ResourceSchemas = new Dictionary<string, ResourceSchema>()
            };

            var apiSchema = new ApiSchema
            {
                ApiSchemaVersion = "v1.0",
                ProjectSchema = projectSchema
            };

            // Act & Assert
            apiSchema.ApiSchemaVersion.Should().Be("v1.0");
            apiSchema.ProjectSchema.Should().NotBeNull();
            apiSchema.ProjectSchema.ProjectName.Should().Be("TestProject");
            apiSchema.ProjectSchema.ProjectVersion.Should().Be("1.0.0");
            apiSchema.ProjectSchema.IsExtensionProject.Should().BeFalse();
            apiSchema.ProjectSchema.Description.Should().Be("Test project description");
            apiSchema.ProjectSchema.ResourceSchemas.Should().NotBeNull();
        }

        [Test]
        public void ColumnMetadata_WithAllProperties_ShouldReturnCorrectValues()
        {
            // Arrange
            var columnMetadata = new ColumnMetadata
            {
                JsonPath = "$.TestColumn",
                ColumnName = "TestColumn",
                ColumnType = "string",
                MaxLength = "100",
                Precision = "10",
                Scale = "2",
                IsNaturalKey = true,
                IsRequired = true,
                IsParentReference = false,
                FromReferencePath = "$.Reference",
                IsPolymorphicReference = false,
                PolymorphicType = "TestType",
                IsDiscriminator = false,
                IsSuperclassIdentity = false
            };

            // Act & Assert
            columnMetadata.JsonPath.Should().Be("$.TestColumn");
            columnMetadata.ColumnName.Should().Be("TestColumn");
            columnMetadata.ColumnType.Should().Be("string");
            columnMetadata.MaxLength.Should().Be("100");
            columnMetadata.Precision.Should().Be("10");
            columnMetadata.Scale.Should().Be("2");
            columnMetadata.IsNaturalKey.Should().BeTrue();
            columnMetadata.IsRequired.Should().BeTrue();
            columnMetadata.IsParentReference.Should().BeFalse();
            columnMetadata.FromReferencePath.Should().Be("$.Reference");
            columnMetadata.IsPolymorphicReference.Should().BeFalse();
            columnMetadata.PolymorphicType.Should().Be("TestType");
            columnMetadata.IsDiscriminator.Should().BeFalse();
            columnMetadata.IsSuperclassIdentity.Should().BeFalse();
        }

        [Test]
        public void TableMetadata_WithChildTables_ShouldMaintainHierarchy()
        {
            // Arrange
            var childTable = new TableMetadata
            {
                BaseName = "ChildTable",
                JsonPath = "$.Parent.Child",
                Columns = [],
                ChildTables = [],
                IsExtensionTable = false,
                DiscriminatorValue = "Child"
            };

            var parentTable = new TableMetadata
            {
                BaseName = "ParentTable",
                JsonPath = "$.Parent",
                Columns = [],
                ChildTables = [childTable],
                IsExtensionTable = false,
                DiscriminatorValue = null
            };

            // Act & Assert
            parentTable.BaseName.Should().Be("ParentTable");
            parentTable.JsonPath.Should().Be("$.Parent");
            parentTable.ChildTables.Should().HaveCount(1);
            parentTable.ChildTables[0].BaseName.Should().Be("ChildTable");
            parentTable.ChildTables[0].DiscriminatorValue.Should().Be("Child");
            parentTable.IsExtensionTable.Should().BeFalse();
        }

        [Test]
        public void ResourceSchema_WithFlatteningMetadata_ShouldProvideTableAccess()
        {
            // Arrange
            var tableMetadata = new TableMetadata
            {
                BaseName = "TestTable",
                JsonPath = "$.Test",
                Columns = [],
                ChildTables = []
            };

            var flatteningMetadata = new FlatteningMetadata
            {
                Table = tableMetadata
            };

            var resourceSchema = new ResourceSchema
            {
                ResourceName = "TestResource",
                FlatteningMetadata = flatteningMetadata
            };

            // Act & Assert
            resourceSchema.ResourceName.Should().Be("TestResource");
            resourceSchema.FlatteningMetadata.Should().NotBeNull();
            resourceSchema.FlatteningMetadata.Table.Should().NotBeNull();
            resourceSchema.FlatteningMetadata.Table.BaseName.Should().Be("TestTable");
        }
    }
}