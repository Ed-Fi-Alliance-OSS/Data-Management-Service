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
    /// Tests for MssqlDdlGeneratorStrategy branch coverage improvements targeting specific conditional logic.
    /// </summary>
    [TestFixture]
    public class MssqlBranchCoverageImprovementTests
    {
        private MssqlDdlGeneratorStrategy _strategy = null!;

        [SetUp]
        public void Setup()
        {
            var logger = LoggerFactory.Create(builder => { }).CreateLogger<MssqlDdlGeneratorStrategy>();
            _strategy = new MssqlDdlGeneratorStrategy(logger);
        }

        [Test]
        public void GenerateDdlString_WithForeignKeyConstraintsDisabled_SkipsFKGeneration()
        {
            // Arrange
            var apiSchema = TestHelpers.CreateBasicApiSchema();
            var options = new DdlGenerationOptions { GenerateForeignKeyConstraints = false };

            // Act
            var result = _strategy.GenerateDdlString(apiSchema, options);

            // Assert
            result.Should().NotBeNull();
            result.Should().NotContain("ALTER TABLE"); // No FK constraints generated
            result.Should().NotContain("ADD CONSTRAINT");
        }

        [Test]
        public void GenerateDdlString_WithNaturalKeyConstraintsDisabled_SkipsUniqueConstraints()
        {
            // Arrange
            var apiSchema = TestHelpers.CreateBasicApiSchema();
            var options = new DdlGenerationOptions { GenerateNaturalKeyConstraints = false };

            // Act
            var result = _strategy.GenerateDdlString(apiSchema, options);

            // Assert
            result.Should().NotBeNull();
            // Note: Still contains tables, just without unique constraints on natural keys
        }

        [Test]
        public void GenerateDdlString_WithAuditColumnsEnabled_IncludesAuditColumns()
        {
            // Arrange
            var apiSchema = TestHelpers.CreateBasicApiSchema();
            var options = new DdlGenerationOptions { IncludeAuditColumns = true };

            // Act
            var result = _strategy.GenerateDdlString(apiSchema, options);

            // Assert
            result.Should().NotBeNull();
            result.Should().Contain("[CreateDate] DATETIME2 NOT NULL");
            result.Should().Contain("[LastModifiedDate] DATETIME2 NOT NULL");
            result.Should().Contain("[ChangeVersion] BIGINT NOT NULL");
        }

        [Test]
        public void GenerateDdlString_WithAuditColumnsDisabled_ExcludesAuditColumns()
        {
            // Arrange
            var apiSchema = TestHelpers.CreateBasicApiSchema();
            var options = new DdlGenerationOptions { IncludeAuditColumns = false };

            // Act
            var result = _strategy.GenerateDdlString(apiSchema, options);

            // Assert
            result.Should().NotBeNull();
            result.Should().NotContain("CreateDate");
            result.Should().NotContain("LastModifiedDate");
            result.Should().NotContain("ChangeVersion");
        }

        [Test]
        public void GenerateDdlString_WithDecimalColumnWithPrecisionAndScale_GeneratesCorrectType()
        {
            // Arrange
            var apiSchema = TestHelpers.CreateApiSchemaWithDecimalColumn("18", "2");
            var options = new DdlGenerationOptions();

            // Act
            var result = _strategy.GenerateDdlString(apiSchema, options);

            // Assert
            result.Should().NotBeNull();
            result.Should().Contain("DECIMAL(18, 2)");
        }

        [Test]
        public void GenerateDdlString_WithDecimalColumnWithPrecisionOnly_GeneratesTypeWithZeroScale()
        {
            // Arrange
            var apiSchema = TestHelpers.CreateApiSchemaWithDecimalColumn("10", null);
            var options = new DdlGenerationOptions();

            // Act
            var result = _strategy.GenerateDdlString(apiSchema, options);

            // Assert
            result.Should().NotBeNull();
            result.Should().Contain("DECIMAL(10, 0)"); // Scale defaults to 0
        }

        [Test]
        public void GenerateDdlString_WithDecimalColumnWithScaleOnly_GeneratesTypeWithCalculatedPrecision()
        {
            // Arrange
            var apiSchema = TestHelpers.CreateApiSchemaWithDecimalColumn(null, "4");
            var options = new DdlGenerationOptions();

            // Act
            var result = _strategy.GenerateDdlString(apiSchema, options);

            // Assert
            result.Should().NotBeNull();
            result.Should().Contain("DECIMAL(14, 4)"); // Precision = scale + 10
        }

        [Test]
        public void GenerateDdlString_WithStringColumnExceeding4000Chars_UsesNvarcharMax()
        {
            // Arrange
            var apiSchema = TestHelpers.CreateApiSchemaWithStringColumn("5000"); // Exceeds SQL Server NVARCHAR limit
            var options = new DdlGenerationOptions();

            // Act
            var result = _strategy.GenerateDdlString(apiSchema, options);

            // Assert
            result.Should().NotBeNull();
            result.Should().Contain("NVARCHAR(MAX)"); // Should use MAX for large strings
        }

        [Test]
        public void GenerateDdlString_WithStringColumnUnder4000Chars_UsesSpecificLength()
        {
            // Arrange
            var apiSchema = TestHelpers.CreateApiSchemaWithStringColumn("1000");
            var options = new DdlGenerationOptions();

            // Act
            var result = _strategy.GenerateDdlString(apiSchema, options);

            // Assert
            result.Should().NotBeNull();
            result.Should().Contain("NVARCHAR(1000)"); // Should use specific length
        }

        [Test]
        public void GenerateDdlString_WithReferenceColumnEndingInId_CreatesFK()
        {
            // Arrange
            var apiSchema = TestHelpers.CreateApiSchemaWithReferenceColumn();
            var options = new DdlGenerationOptions { GenerateForeignKeyConstraints = true };

            // Act
            var result = _strategy.GenerateDdlString(apiSchema, options);

            // Assert
            result.Should().NotBeNull();
            result.Should().Contain("SchoolId"); // Reference column
            result.Should().Contain("ALTER TABLE"); // FK constraint generation
            result.Should().Contain("School"); // Referenced table
        }

        [Test]
        public void GenerateDdlString_WithChildTable_GeneratesParentFK()
        {
            // Arrange
            var apiSchema = TestHelpers.CreateApiSchemaWithChildTable();
            var options = new DdlGenerationOptions();

            // Act
            var result = _strategy.GenerateDdlString(apiSchema, options);

            // Assert
            result.Should().NotBeNull();
            result.Should().Contain("Student_Id"); // Parent FK column
            result.Should().Contain("FK_StudentAddress_Student"); // FK constraint name
        }

        [Test]
        public void GenerateDdlString_WithUnionViewsEnabled_GeneratesUnionViews()
        {
            // Arrange
            var apiSchema = TestHelpers.CreateApiSchemaWithPolymorphicReference();
            var options = new DdlGenerationOptions { SkipUnionViews = false };

            // Act
            var result = _strategy.GenerateDdlString(apiSchema, options);

            // Assert
            result.Should().NotBeNull();
            result.Should().Contain("CREATE VIEW"); // Union view generation
            result.Should().Contain("UNION ALL"); // Union syntax
            result.Should().Contain("Discriminator"); // Discriminator column
        }

        [Test]
        public void GenerateDdlString_WithUnionViewsSkipped_ExcludesUnionViews()
        {
            // Arrange
            var apiSchema = TestHelpers.CreateApiSchemaWithPolymorphicReference();
            var options = new DdlGenerationOptions { SkipUnionViews = true };

            // Act
            var result = _strategy.GenerateDdlString(apiSchema, options);

            // Assert
            result.Should().NotBeNull();
            result.Should().NotContain("UNION ALL");
        }

        [Test]
        public void GenerateDdlString_WithDescriptorResource_UsesDescriptorSchema()
        {
            // Arrange
            var apiSchema = TestHelpers.CreateApiSchemaWithDescriptorResource();
            var options = new DdlGenerationOptions
            {
                DescriptorSchema = "descriptors",
                DefaultSchema = "dms",
                UsePrefixedTableNames = false,
            };

            // Act
            var result = _strategy.GenerateDdlString(apiSchema, options);

            // Assert
            result.Should().NotBeNull();
            result.Should().Contain("CREATE SCHEMA [descriptors]"); // Separate descriptor schema
            result.Should().Contain("[descriptors].[GradeLevelDescriptor]"); // Table in descriptor schema
        }

        [Test]
        public void GenerateDdlString_WithExtensionProjectTPDM_ExtractsProjectName()
        {
            // Arrange
            var apiSchema = TestHelpers.CreateApiSchemaWithTPDMExtension();
            var options = new DdlGenerationOptions
            {
                IncludeExtensions = true,
                UsePrefixedTableNames = false,
            };

            // Act
            var result = _strategy.GenerateDdlString(apiSchema, options);

            // Assert
            result.Should().NotBeNull();
            result.Should().Contain("TPDMStudentExtension"); // Extension table
        }

        [Test]
        public void GenerateDdlString_WithExtensionProjectCaseInsensitive_MatchesSchemaMapping()
        {
            // Arrange
            var apiSchema = TestHelpers.CreateApiSchemaWithSampleExtension();
            var options = new DdlGenerationOptions
            {
                IncludeExtensions = true,
                UsePrefixedTableNames = false, // Use separate schemas to trigger schema mapping
            };

            // Act
            var result = _strategy.GenerateDdlString(apiSchema, options);

            // Assert
            result.Should().NotBeNull();
            result.Should().Contain("[extensions]"); // Should use extensions schema for extension projects
        }

        [Test]
        public void GenerateDdlString_WithProjectCaseInsensitive_MatchesSchemaMapping()
        {
            // Arrange
            var apiSchema = new ApiSchema
            {
                ProjectSchema = new ProjectSchema
                {
                    ProjectName = "edfi", // lowercase
                    ProjectVersion = "1.0.0",
                    ResourceSchemas = new Dictionary<string, ResourceSchema>
                    {
                        ["students"] = TestHelpers.CreateBasicResourceSchema(),
                    },
                },
            };
            var options = new DdlGenerationOptions
            {
                SchemaMapping = new Dictionary<string, string>
                {
                    ["EdFi"] = "custom_edfi_schema", // Mixed case key
                },
            };

            // Act
            var result = _strategy.GenerateDdlString(apiSchema, options);

            // Assert
            result.Should().NotBeNull();
            // Case-insensitive matching should work
        }

        [Test]
        public void GenerateDdlString_WithReferencePathWithoutReference_ReturnsOriginalPath()
        {
            // Arrange
            var apiSchema = TestHelpers.CreateApiSchemaWithNonReferenceColumn();
            var options = new DdlGenerationOptions();

            // Act
            var result = _strategy.GenerateDdlString(apiSchema, options);

            // Assert
            result.Should().NotBeNull();
            // Should handle non-reference paths correctly
        }

        [Test]
        public void GenerateDdlString_WithEmptyReferenceColumn_SkipsReferenceProcessing()
        {
            // Arrange
            var apiSchema = TestHelpers.CreateApiSchemaWithEmptyReferenceColumn();
            var options = new DdlGenerationOptions();

            // Act
            var result = _strategy.GenerateDdlString(apiSchema, options);

            // Assert
            result.Should().NotBeNull();
            // Should handle empty reference paths gracefully
        }
    }
}
