// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.SchemaGenerator.Abstractions;
using FluentAssertions;

namespace EdFi.DataManagementService.SchemaGenerator.Tests.Unit.Shared
{
    /// <summary>
    /// Unit tests for DdlGenerationOptions to increase branch coverage.
    /// </summary>
    [TestFixture]
    public class DdlGenerationOptionsTests
    {
        [Test]
        public void ResolveSchemaName_WithMappingExists_ReturnsMapping()
        {
            // Arrange
            var options = new DdlGenerationOptions { UsePrefixedTableNames = false };
            options.SchemaMapping["TestProject"] = "test_schema";

            // Act
            var result = options.ResolveSchemaName("TestProject");

            // Assert
            result.Should().Be("test_schema");
        }

        [Test]
        public void ResolveSchemaName_WithoutMapping_ReturnsDefaultSchema()
        {
            // Arrange
            var options = new DdlGenerationOptions();

            // Act
            var result = options.ResolveSchemaName("UnknownProject");

            // Assert
            result.Should().Be("dms"); // Default schema
        }

        [Test]
        public void ResolveSchemaName_WithEmptyProjectName_ReturnsDefaultSchema()
        {
            // Arrange
            var options = new DdlGenerationOptions();

            // Act
            var result = options.ResolveSchemaName("");

            // Assert
            result.Should().Be("dms"); // Default schema
        }

        [Test]
        public void ResolveSchemaName_WithNullProjectName_ReturnsDefaultSchema()
        {
            // Arrange
            var options = new DdlGenerationOptions();

            // Act
            var result = options.ResolveSchemaName(null);

            // Assert
            result.Should().Be("dms"); // Default schema
        }

        [Test]
        public void ResolveSchemaName_WithMixedCaseMapping_ReturnsExactMapping()
        {
            // Arrange
            var options = new DdlGenerationOptions { UsePrefixedTableNames = false };
            options.SchemaMapping["TestProject"] = "Custom_Schema_Name";

            // Act
            var result = options.ResolveSchemaName("TestProject");

            // Assert
            result.Should().Be("Custom_Schema_Name");
        }

        [Test]
        public void ResolveSchemaName_CaseInsensitiveMapping_ReturnsMappingWhenCaseMatches()
        {
            // Arrange
            var options = new DdlGenerationOptions { UsePrefixedTableNames = false };
            options.SchemaMapping["testproject"] = "lowercase_schema";

            // Act
            var result = options.ResolveSchemaName("TestProject");

            // Assert
            result.Should().Be("lowercase_schema");
        }

        [Test]
        public void DefaultValues_ShouldBeCorrectlyInitialized()
        {
            // Arrange & Act
            var options = new DdlGenerationOptions();

            // Assert
            options.DescriptorSchema.Should().Be("dms");
            options.SchemaMapping.Should().NotBeNull();
            options.SchemaMapping.Should().HaveCount(4); // EdFi, Sample, TPDM, Extensions
            options.DefaultSchema.Should().Be("dms");
        }

        [Test]
        public void SchemaMapping_CanAddMultipleMappings()
        {
            // Arrange
            var options = new DdlGenerationOptions { UsePrefixedTableNames = false };

            // Act
            options.SchemaMapping["Project1"] = "schema1";
            options.SchemaMapping["Project2"] = "schema2";
            options.SchemaMapping["Project3"] = "schema3";

            // Assert
            options.SchemaMapping.Should().Contain(kvp => kvp.Key == "Project1" && kvp.Value == "schema1");
            options.ResolveSchemaName("Project1").Should().Be("schema1");
            options.ResolveSchemaName("Project2").Should().Be("schema2");
            options.ResolveSchemaName("Project3").Should().Be("schema3");
        }

        [Test]
        public void SchemaMapping_CanOverrideExistingMapping()
        {
            // Arrange
            var options = new DdlGenerationOptions { UsePrefixedTableNames = false };
            options.SchemaMapping["TestProject"] = "original_schema";

            // Act
            options.SchemaMapping["TestProject"] = "updated_schema";

            // Assert
            options.ResolveSchemaName("TestProject").Should().Be("updated_schema");
        }

        [Test]
        public void DescriptorSchema_CanBeModified()
        {
            // Arrange
            var options = new DdlGenerationOptions
            {
                // Act
                DescriptorSchema = "custom_descriptors"
            };

            // Assert
            options.DescriptorSchema.Should().Be("custom_descriptors");
        }

        [Test]
        public void ResolveSchemaName_ExtensionProject_ReturnsExtensionsSchema()
        {
            // Arrange
            var options = new DdlGenerationOptions { UsePrefixedTableNames = false };

            // Act
            var result = options.ResolveSchemaName("MyExtension");

            // Assert
            result.Should().Be("extensions");
        }

        [Test]
        public void ResolveSchemaName_ExtProjectEnding_ReturnsExtensionsSchema()
        {
            // Arrange
            var options = new DdlGenerationOptions { UsePrefixedTableNames = false };

            // Act
            var result = options.ResolveSchemaName("SomeExt");

            // Assert
            result.Should().Be("extensions");
        }

        [Test]
        public void ResolveSchemaName_DefaultSchemaOverride_UsesCustomDefault()
        {
            // Arrange
            var options = new DdlGenerationOptions
            {
                DefaultSchema = "custom_default"
            };

            // Act
            var result = options.ResolveSchemaName("UnknownProject");

            // Assert
            result.Should().Be("custom_default");
        }

        [Test]
        public void ResolveSchemaName_EdFiProject_ReturnsEdFiSchema()
        {
            // Arrange
            var options = new DdlGenerationOptions { UsePrefixedTableNames = false };

            // Act
            var result = options.ResolveSchemaName("EdFi");

            // Assert
            result.Should().Be("edfi");
        }

        [Test]
        public void ResolveSchemaName_TPDMProject_ReturnsTPDMSchema()
        {
            // Arrange
            var options = new DdlGenerationOptions { UsePrefixedTableNames = false };

            // Act
            var result = options.ResolveSchemaName("TPDM");

            // Assert
            result.Should().Be("tpdm");
        }

        [Test]
        public void ResolveSchemaName_SampleProject_ReturnsSampleSchema()
        {
            // Arrange
            var options = new DdlGenerationOptions { UsePrefixedTableNames = false };

            // Act
            var result = options.ResolveSchemaName("Sample");

            // Assert
            result.Should().Be("sample");
        }
    }
}
