using EdFi.DataManagementService.SchemaGenerator.Abstractions;
using EdFi.DataManagementService.SchemaGenerator.Pgsql;
using EdFi.DataManagementService.SchemaGenerator.Tests.Unit.Shared;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.SchemaGenerator.Tests.Unit.Engines.PostgreSQL
{
    /// <summary>
    /// Tests for PgsqlDdlGeneratorStrategy error paths and edge cases to improve coverage.
    /// </summary>
    [TestFixture]
    public class PgsqlErrorPathCoverageTests
    {
        private PgsqlDdlGeneratorStrategy _strategy = null!;

        [SetUp]
        public void Setup()
        {
            _strategy = new PgsqlDdlGeneratorStrategy();
        }

        [Test]
        public void GenerateDdlString_WithNullProjectSchema_ThrowsInvalidDataException()
        {
            // Arrange
            var apiSchema = new ApiSchema { ProjectSchema = null };
            var options = new DdlGenerationOptions();

            // Act & Assert
            var exception = Assert.Throws<InvalidDataException>(() =>
                _strategy.GenerateDdlString(apiSchema, options));

            exception.Message.Should().Be("ApiSchema does not contain valid projectSchema.");
        }

        [Test]
        public void GenerateDdlString_WithNullResourceSchemas_ThrowsInvalidDataException()
        {
            // Arrange
            var apiSchema = new ApiSchema
            {
                ProjectSchema = new ProjectSchema
                {
                    ProjectName = "EdFi",
                    ResourceSchemas = null!
                }
            };
            var options = new DdlGenerationOptions();

            // Act & Assert
            var exception = Assert.Throws<InvalidDataException>(() =>
                _strategy.GenerateDdlString(apiSchema, options));

            exception.Message.Should().Be("ApiSchema does not contain valid projectSchema.");
        }

        [Test]
        public void GenerateDdlString_LegacyMethod_WithNullProjectSchema_ThrowsInvalidDataException()
        {
            // Arrange
            var apiSchema = new ApiSchema { ProjectSchema = null };

            // Act & Assert
            var exception = Assert.Throws<InvalidDataException>(() =>
                _strategy.GenerateDdlString(apiSchema, true, false));

            exception.Message.Should().Be("ApiSchema does not contain valid projectSchema.");
        }

        [Test]
        public void GenerateDdlString_LegacyMethod_WithNullResourceSchemas_ThrowsInvalidDataException()
        {
            // Arrange
            var apiSchema = new ApiSchema
            {
                ProjectSchema = new ProjectSchema
                {
                    ProjectName = "EdFi",
                    ResourceSchemas = null!
                }
            };

            // Act & Assert
            var exception = Assert.Throws<InvalidDataException>(() =>
                _strategy.GenerateDdlString(apiSchema, true, false));

            exception.Message.Should().Be("ApiSchema does not contain valid projectSchema.");
        }

        [Test]
        public void GenerateDdl_WithValidInput_CreatesOutputDirectory()
        {
            // Arrange
            var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            var apiSchema = TestHelpers.CreateBasicApiSchema();

            try
            {
                // Act
                _strategy.GenerateDdl(apiSchema, tempDir, true, false);

                // Assert
                Directory.Exists(tempDir).Should().BeTrue();
                File.Exists(Path.Combine(tempDir, "EdFi-DMS-Database-Schema-PostgreSQL.sql")).Should().BeTrue();
            }
            finally
            {
                // Cleanup
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }

        [Test]
        public void GenerateDdl_WithOptionsOverload_CreatesOutputDirectory()
        {
            // Arrange
            var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            var apiSchema = TestHelpers.CreateBasicApiSchema();
            var options = new DdlGenerationOptions { IncludeExtensions = true };

            try
            {
                // Act
                _strategy.GenerateDdl(apiSchema, tempDir, options);

                // Assert
                Directory.Exists(tempDir).Should().BeTrue();
                File.Exists(Path.Combine(tempDir, "EdFi-DMS-Database-Schema-PostgreSQL.sql")).Should().BeTrue();
            }
            finally
            {
                // Cleanup
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }

        [Test]
        public void GenerateDdlString_WithEmptyResourceSchemas_GeneratesMinimalDdl()
        {
            // Arrange
            var apiSchema = new ApiSchema
            {
                ProjectSchema = new ProjectSchema
                {
                    ProjectName = "EdFi",
                    ResourceSchemas = new Dictionary<string, ResourceSchema>()
                }
            };
            var options = new DdlGenerationOptions();

            // Act
            var result = _strategy.GenerateDdlString(apiSchema, options);

            // Assert
            result.Should().NotBeNull();
            result.Should().NotContain("CREATE TABLE"); // No tables for empty schema
        }

        [Test]
        public void GenerateDdlString_WithResourceWithoutFlatteningMetadata_SkipsResource()
        {
            // Arrange
            var apiSchema = new ApiSchema
            {
                ProjectSchema = new ProjectSchema
                {
                    ProjectName = "EdFi",
                    ResourceSchemas = new Dictionary<string, ResourceSchema>
                    {
                        ["students"] = new ResourceSchema
                        {
                            ResourceName = "Students",
                            FlatteningMetadata = null // No flattening metadata
                        }
                    }
                }
            };
            var options = new DdlGenerationOptions();

            // Act
            var result = _strategy.GenerateDdlString(apiSchema, options);

            // Assert
            result.Should().NotBeNull();
            result.Should().NotContain("CREATE TABLE"); // No tables when no flattening metadata
        }

        [Test]
        public void GenerateDdlString_WithExtensionResourceWhenExtensionsDisabled_SkipsExtension()
        {
            // Arrange
            var apiSchema = TestHelpers.CreateApiSchemaWithExtensionTable();
            var options = new DdlGenerationOptions { IncludeExtensions = false };

            // Act
            var result = _strategy.GenerateDdlString(apiSchema, options);

            // Assert
            result.Should().NotBeNull();
            result.Should().NotContain("TPDMStudentExtension"); // Extension table should be skipped
        }

        [Test]
        public void GenerateDdlString_WithUnknownColumnType_UsesTextFallback()
        {
            // Arrange
            var apiSchema = TestHelpers.CreateApiSchemaWithCustomColumnTypes("unknowntype");
            var options = new DdlGenerationOptions();

            // Act
            var result = _strategy.GenerateDdlString(apiSchema, options);

            // Assert
            result.Should().NotBeNull();
            result.Should().Contain("TEXT"); // Unknown types fallback to TEXT in PostgreSQL
        }

        [Test]
        public void GenerateDdlString_WithCurrencyColumnType_GeneratesMoneyType()
        {
            // Arrange
            var apiSchema = TestHelpers.CreateApiSchemaWithCustomColumnTypes("currency");
            var options = new DdlGenerationOptions();

            // Act
            var result = _strategy.GenerateDdlString(apiSchema, options);

            // Assert
            result.Should().NotBeNull();
            result.Should().Contain("MONEY"); // Currency should map to MONEY
        }

        [Test]
        public void GenerateDdlString_WithPercentColumnType_GeneratesDecimalType()
        {
            // Arrange
            var apiSchema = TestHelpers.CreateApiSchemaWithCustomColumnTypes("percent");
            var options = new DdlGenerationOptions();

            // Act
            var result = _strategy.GenerateDdlString(apiSchema, options);

            // Assert
            result.Should().NotBeNull();
            result.Should().Contain("DECIMAL(5, 4)"); // Percent should map to DECIMAL(5,4)
        }

        [Test]
        public void GenerateDdlString_WithYearColumnType_GeneratesSmallIntType()
        {
            // Arrange
            var apiSchema = TestHelpers.CreateApiSchemaWithCustomColumnTypes("year");
            var options = new DdlGenerationOptions();

            // Act
            var result = _strategy.GenerateDdlString(apiSchema, options);

            // Assert
            result.Should().NotBeNull();
            result.Should().Contain("SMALLINT"); // Year should map to SMALLINT
        }

        [Test]
        public void GenerateDdlString_WithDurationColumnType_GeneratesVarchar30Type()
        {
            // Arrange
            var apiSchema = TestHelpers.CreateApiSchemaWithCustomColumnTypes("duration");
            var options = new DdlGenerationOptions();

            // Act
            var result = _strategy.GenerateDdlString(apiSchema, options);

            // Assert
            result.Should().NotBeNull();
            result.Should().Contain("VARCHAR(30)"); // Duration should map to VARCHAR(30)
        }

        [Test]
        public void GenerateDdlString_WithGuidColumnType_GeneratesUuidType()
        {
            // Arrange
            var apiSchema = TestHelpers.CreateApiSchemaWithCustomColumnTypes("guid");
            var options = new DdlGenerationOptions();

            // Act
            var result = _strategy.GenerateDdlString(apiSchema, options);

            // Assert
            result.Should().NotBeNull();
            result.Should().Contain("UUID"); // GUID should map to UUID in PostgreSQL
        }

        [Test]
        public void GenerateDdlString_WithUuidColumnType_GeneratesUuidType()
        {
            // Arrange
            var apiSchema = TestHelpers.CreateApiSchemaWithCustomColumnTypes("uuid");
            var options = new DdlGenerationOptions();

            // Act
            var result = _strategy.GenerateDdlString(apiSchema, options);

            // Assert
            result.Should().NotBeNull();
            result.Should().Contain("UUID"); // UUID should map to UUID
        }

        [Test]
        public void GenerateDdlString_WithDescriptorColumnType_GeneratesBigIntType()
        {
            // Arrange
            var apiSchema = TestHelpers.CreateApiSchemaWithCustomColumnTypes("descriptor");
            var options = new DdlGenerationOptions();

            // Act
            var result = _strategy.GenerateDdlString(apiSchema, options);

            // Assert
            result.Should().NotBeNull();
            result.Should().Contain("BIGINT"); // Descriptor should map to BIGINT FK
        }

        [Test]
        public void GenerateDdlString_WithInt16ColumnType_GeneratesSmallIntType()
        {
            // Arrange
            var apiSchema = TestHelpers.CreateApiSchemaWithCustomColumnTypes("int16");
            var options = new DdlGenerationOptions();

            // Act
            var result = _strategy.GenerateDdlString(apiSchema, options);

            // Assert
            result.Should().NotBeNull();
            result.Should().Contain("SMALLINT"); // Int16 should map to SMALLINT
        }

        [Test]
        public void GenerateDdlString_WithShortColumnType_GeneratesSmallIntType()
        {
            // Arrange
            var apiSchema = TestHelpers.CreateApiSchemaWithCustomColumnTypes("short");
            var options = new DdlGenerationOptions();

            // Act
            var result = _strategy.GenerateDdlString(apiSchema, options);

            // Assert
            result.Should().NotBeNull();
            result.Should().Contain("SMALLINT"); // Short should map to SMALLINT
        }
    }
}
