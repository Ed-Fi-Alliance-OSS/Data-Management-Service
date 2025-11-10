// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.SchemaGenerator.Abstractions;
using EdFi.DataManagementService.SchemaGenerator.Mssql;
using EdFi.DataManagementService.SchemaGenerator.Tests.Unit.Shared;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.SchemaGenerator.Tests.Unit.Engines.Mssql
{
    /// <summary>
    /// Tests for MssqlDdlGeneratorStrategy error paths and edge cases to improve coverage.
    /// </summary>
    [TestFixture]
    public class MssqlErrorPathCoverageTests
    {
        private MssqlDdlGeneratorStrategy _strategy = null!;

        [SetUp]
        public void Setup()
        {
            var logger = LoggerFactory.Create(builder => { }).CreateLogger<MssqlDdlGeneratorStrategy>();
            _strategy = new MssqlDdlGeneratorStrategy(logger);
        }

        [Test]
        public void GenerateDdlString_WithNullProjectSchema_ThrowsInvalidDataException()
        {
            // Arrange
            var apiSchema = new ApiSchema { ProjectSchema = null };
            var options = new DdlGenerationOptions();

            // Act & Assert
            var exception = Assert.Throws<InvalidDataException>(() =>
                _strategy.GenerateDdlString(apiSchema, options)
            );

            exception.Message.Should().Be("ApiSchema does not contain valid projectSchema.");
        }

        [Test]
        public void GenerateDdlString_WithNullResourceSchemas_ThrowsInvalidDataException()
        {
            // Arrange
            var apiSchema = new ApiSchema
            {
                ProjectSchema = new ProjectSchema { ProjectName = "EdFi", ResourceSchemas = null! },
            };
            var options = new DdlGenerationOptions();

            // Act & Assert
            var exception = Assert.Throws<InvalidDataException>(() =>
                _strategy.GenerateDdlString(apiSchema, options)
            );

            exception.Message.Should().Be("ApiSchema does not contain valid projectSchema.");
        }

        [Test]
        public void GenerateDdlString_LegacyMethod_WithNullProjectSchema_ThrowsInvalidDataException()
        {
            // Arrange
            var apiSchema = new ApiSchema { ProjectSchema = null };

            // Act & Assert
            var exception = Assert.Throws<InvalidDataException>(() =>
                _strategy.GenerateDdlString(apiSchema, true, false)
            );

            exception.Message.Should().Be("ApiSchema does not contain valid projectSchema.");
        }

        [Test]
        public void GenerateDdlString_LegacyMethod_WithNullResourceSchemas_ThrowsInvalidDataException()
        {
            // Arrange
            var apiSchema = new ApiSchema
            {
                ProjectSchema = new ProjectSchema { ProjectName = "EdFi", ResourceSchemas = null! },
            };

            // Act & Assert
            var exception = Assert.Throws<InvalidDataException>(() =>
                _strategy.GenerateDdlString(apiSchema, true, false)
            );

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
                File.Exists(Path.Combine(tempDir, "EdFi-DMS-Database-Schema-SQLServer.sql"))
                    .Should()
                    .BeTrue();
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
                File.Exists(Path.Combine(tempDir, "EdFi-DMS-Database-Schema-SQLServer.sql"))
                    .Should()
                    .BeTrue();
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
                    ResourceSchemas = new Dictionary<string, ResourceSchema>(),
                },
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
                            FlatteningMetadata = null, // No flattening metadata
                        },
                    },
                },
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
        public void GenerateDdlString_WithUnknownColumnType_UsesNvarcharMaxFallback()
        {
            // Arrange
            var apiSchema = TestHelpers.CreateApiSchemaWithCustomColumnTypes("unknowntype");
            var options = new DdlGenerationOptions();

            // Act
            var result = _strategy.GenerateDdlString(apiSchema, options);

            // Assert
            result.Should().NotBeNull();
            result.Should().Contain("NVARCHAR(MAX)"); // Unknown types fallback to NVARCHAR(MAX)
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
        public void GenerateDdlString_WithDurationColumnType_GeneratesNvarchar30Type()
        {
            // Arrange
            var apiSchema = TestHelpers.CreateApiSchemaWithCustomColumnTypes("duration");
            var options = new DdlGenerationOptions();

            // Act
            var result = _strategy.GenerateDdlString(apiSchema, options);

            // Assert
            result.Should().NotBeNull();
            result.Should().Contain("NVARCHAR(30)"); // Duration should map to NVARCHAR(30)
        }

        [Test]
        public void GenerateDdlString_WithGuidColumnType_GeneratesUniqueIdentifierType()
        {
            // Arrange
            var apiSchema = TestHelpers.CreateApiSchemaWithCustomColumnTypes("guid");
            var options = new DdlGenerationOptions();

            // Act
            var result = _strategy.GenerateDdlString(apiSchema, options);

            // Assert
            result.Should().NotBeNull();
            result.Should().Contain("UNIQUEIDENTIFIER"); // GUID should map to UNIQUEIDENTIFIER
        }

        [Test]
        public void GenerateDdlString_WithUuidColumnType_GeneratesUniqueIdentifierType()
        {
            // Arrange
            var apiSchema = TestHelpers.CreateApiSchemaWithCustomColumnTypes("uuid");
            var options = new DdlGenerationOptions();

            // Act
            var result = _strategy.GenerateDdlString(apiSchema, options);

            // Assert
            result.Should().NotBeNull();
            result.Should().Contain("UNIQUEIDENTIFIER"); // UUID should map to UNIQUEIDENTIFIER
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
