using EdFi.DataManagementService.SchemaGenerator.Abstractions;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.SchemaGenerator.Tests.Unit.Shared
{
    /// <summary>
    /// Tests for DdlGenerationOptions edge cases to improve coverage.
    /// </summary>
    [TestFixture]
    public class DdlGenerationOptionsCoverageTests
    {
        [Test]
        public void ResolveSchemaName_WithPrefixedTableNamesEnabled_AlwaysReturnsDefaultSchema()
        {
            // Arrange
            var options = new DdlGenerationOptions
            {
                UsePrefixedTableNames = true,
                DefaultSchema = "custom_dms"
            };

            // Act & Assert
            options.ResolveSchemaName("EdFi").Should().Be("custom_dms");
            options.ResolveSchemaName("TPDM").Should().Be("custom_dms");
            options.ResolveSchemaName("SomeUnknown").Should().Be("custom_dms");
            options.ResolveSchemaName(null).Should().Be("custom_dms");
        }

        [Test]
        public void ResolveSchemaName_WithNullOrEmptyProjectName_ReturnsDefaultSchema()
        {
            // Arrange
            var options = new DdlGenerationOptions
            {
                UsePrefixedTableNames = false,
                DefaultSchema = "custom_default"
            };

            // Act & Assert
            options.ResolveSchemaName(null).Should().Be("custom_default");
            options.ResolveSchemaName("").Should().Be("custom_default");
            // Note: whitespace-only strings are not treated as empty by the implementation
        }

        [Test]
        public void ResolveSchemaName_WithCaseInsensitiveMatch_ReturnsCorrectSchema()
        {
            // Arrange
            var options = new DdlGenerationOptions
            {
                UsePrefixedTableNames = false,
                SchemaMapping = new Dictionary<string, string>
                {
                    ["EdFi"] = "edfi_custom",
                    ["TPDM"] = "tpdm_custom"
                }
            };

            // Act & Assert
            options.ResolveSchemaName("edfi").Should().Be("edfi_custom"); // Case insensitive match
            options.ResolveSchemaName("EDFI").Should().Be("edfi_custom");
            options.ResolveSchemaName("tpdm").Should().Be("tpdm_custom");
        }

        [Test]
        public void ResolveSchemaName_WithExtensionProject_ReturnsExtensionsSchema()
        {
            // Arrange
            var options = new DdlGenerationOptions
            {
                UsePrefixedTableNames = false,
                SchemaMapping = new Dictionary<string, string>
                {
                    ["Extensions"] = "custom_extensions"
                }
            };

            // Act & Assert
            options.ResolveSchemaName("SomeExtension").Should().Be("custom_extensions");
            options.ResolveSchemaName("TPDMExtension").Should().Be("custom_extensions");
            options.ResolveSchemaName("SampleExt").Should().Be("custom_extensions");
        }

        [Test]
        public void ResolveSchemaName_WithExtensionProjectButNoExtensionsMapping_ReturnsDefaultSchema()
        {
            // Arrange
            var options = new DdlGenerationOptions
            {
                UsePrefixedTableNames = false,
                DefaultSchema = "fallback"
            };
            // Remove Extensions from default mapping
            options.SchemaMapping.Remove("Extensions");

            // Act & Assert
            options.ResolveSchemaName("SomeExtension").Should().Be("fallback");
            options.ResolveSchemaName("TPDMExtension").Should().Be("fallback");
        }

        [Test]
        public void ResolveTablePrefix_WithPrefixedTableNamesDisabled_ReturnsEmptyString()
        {
            // Arrange
            var options = new DdlGenerationOptions
            {
                UsePrefixedTableNames = false
            };

            // Act & Assert
            options.ResolveTablePrefix("EdFi").Should().Be("");
            options.ResolveTablePrefix("TPDM").Should().Be("");
            options.ResolveTablePrefix(null).Should().Be("");
        }

        [Test]
        public void ResolveTablePrefix_WithNullOrEmptyProjectName_ReturnsEmptyString()
        {
            // Arrange
            var options = new DdlGenerationOptions
            {
                UsePrefixedTableNames = true
            };

            // Act & Assert
            options.ResolveTablePrefix(null).Should().Be("");
            options.ResolveTablePrefix("").Should().Be("");
            // Note: whitespace-only strings are not treated as empty by the implementation
        }

        [Test]
        public void ResolveTablePrefix_WithExactMatch_ReturnsCorrectPrefix()
        {
            // Arrange
            var options = new DdlGenerationOptions
            {
                UsePrefixedTableNames = true,
                SchemaMapping = new Dictionary<string, string>
                {
                    ["EdFi"] = "edfi",
                    ["TPDM"] = "tpdm"
                }
            };

            // Act & Assert
            options.ResolveTablePrefix("EdFi").Should().Be("edfi_");
            options.ResolveTablePrefix("TPDM").Should().Be("tpdm_");
        }

        [Test]
        public void ResolveTablePrefix_WithCaseInsensitiveMatch_ReturnsCorrectPrefix()
        {
            // Arrange
            var options = new DdlGenerationOptions
            {
                UsePrefixedTableNames = true,
                SchemaMapping = new Dictionary<string, string>
                {
                    ["EdFi"] = "edfi",
                    ["TPDM"] = "tpdm"
                }
            };

            // Act & Assert
            options.ResolveTablePrefix("edfi").Should().Be("edfi_"); // Case insensitive
            options.ResolveTablePrefix("EDFI").Should().Be("edfi_");
            options.ResolveTablePrefix("tpdm").Should().Be("tpdm_");
        }

        [Test]
        public void ResolveTablePrefix_WithExtensionProject_ReturnsExtensionsPrefix()
        {
            // Arrange
            var options = new DdlGenerationOptions
            {
                UsePrefixedTableNames = true,
                SchemaMapping = new Dictionary<string, string>
                {
                    ["Extensions"] = "ext"
                }
            };

            // Act & Assert
            options.ResolveTablePrefix("SomeExtension").Should().Be("ext_");
            options.ResolveTablePrefix("TPDMExtension").Should().Be("ext_");
            options.ResolveTablePrefix("SampleExt").Should().Be("ext_");
        }

        [Test]
        public void ResolveTablePrefix_WithExtensionProjectButNoExtensionsMapping_UsesDefaultExtensions()
        {
            // Arrange
            var options = new DdlGenerationOptions
            {
                UsePrefixedTableNames = true
            };
            // Remove Extensions from default mapping
            options.SchemaMapping.Remove("Extensions");

            // Act & Assert
            options.ResolveTablePrefix("SomeExtension").Should().Be("extensions_");
            options.ResolveTablePrefix("TPDMExtension").Should().Be("extensions_");
        }

        [Test]
        public void ResolveTablePrefix_WithUnknownProject_UsesLowercaseProjectName()
        {
            // Arrange
            var options = new DdlGenerationOptions
            {
                UsePrefixedTableNames = true
            };

            // Act & Assert
            options.ResolveTablePrefix("UnknownProject").Should().Be("unknownproject_");
            options.ResolveTablePrefix("CamelCase").Should().Be("camelcase_");
        }

        [Test]
        public void DefaultPropertyValues_ShouldMatchExpectedDefaults()
        {
            // Arrange & Act
            var options = new DdlGenerationOptions();

            // Assert
            options.IncludeExtensions.Should().BeFalse();
            options.SkipUnionViews.Should().BeFalse();
            options.UsePrefixedTableNames.Should().BeTrue();
            options.GenerateNaturalKeyConstraints.Should().BeTrue();
            options.GenerateForeignKeyConstraints.Should().BeTrue();
            options.IncludeAuditColumns.Should().BeTrue();
            options.DefaultSchema.Should().Be("dms");
            options.DescriptorSchema.Should().Be("dms");
        }

        [Test]
        public void DefaultSchemaMapping_ShouldContainExpectedMappings()
        {
            // Arrange & Act
            var options = new DdlGenerationOptions();

            // Assert
            options.SchemaMapping.Should().Contain("EdFi", "edfi");
            options.SchemaMapping.Should().Contain("ed-fi", "edfi");
            options.SchemaMapping.Should().Contain("Sample", "sample");
            options.SchemaMapping.Should().Contain("TPDM", "tpdm");
            options.SchemaMapping.Should().Contain("Extensions", "extensions");
        }

        [Test]
        public void SchemaMapping_ShouldBeModifiable()
        {
            // Arrange
            var options = new DdlGenerationOptions();

            // Act
            options.SchemaMapping["CustomProject"] = "custom_schema";
            options.SchemaMapping["EdFi"] = "modified_edfi";

            // Assert
            options.SchemaMapping.Should().Contain("CustomProject", "custom_schema");
            options.SchemaMapping.Should().Contain("EdFi", "modified_edfi");
        }
    }
}