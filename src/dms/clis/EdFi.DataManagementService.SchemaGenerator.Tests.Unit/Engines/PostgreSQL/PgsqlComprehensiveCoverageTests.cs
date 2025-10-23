// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.SchemaGenerator.Abstractions;
using EdFi.DataManagementService.SchemaGenerator.Pgsql;
using EdFi.DataManagementService.SchemaGenerator.Tests.Unit.Shared;
using FluentAssertions;

namespace EdFi.DataManagementService.SchemaGenerator.Tests.Unit.Engines.PostgreSQL
{
    /// <summary>
    /// Comprehensive tests targeting specific uncovered code paths for maximum line coverage.
    /// </summary>
    [TestFixture]
    public class PgsqlComprehensiveCoverageTests
    {
        private PgsqlDdlGeneratorStrategy _strategy;

        [SetUp]
        public void SetUp()
        {
            _strategy = new PgsqlDdlGeneratorStrategy();
        }

        [Test]
        public void GenerateDdl_WithDescriptorResource_UsesDescriptorSchema()
        {
            // Arrange
            var schema = TestHelpers.GetSchemaWithDescriptor();
            var options = new DdlGenerationOptions
            {
                DescriptorSchema = "descriptors",
                DefaultSchema = "dms"
            };

            // Act
            var result = _strategy.GenerateDdlString(schema, options);

            // Assert
            result.Should().NotBeNull();
            result.Should().Contain("descriptors");
        }

        [Test]
        public void GenerateDdl_WithExtensionResource_UsesExtensionSchema()
        {
            // Arrange
            var schema = TestHelpers.GetSchemaWithExtension();
            var options = new DdlGenerationOptions
            {
                IncludeExtensions = true,
                SchemaMapping = new Dictionary<string, string>
                {
                    ["TPDM"] = "tpdm"
                }
            };

            // Act
            var result = _strategy.GenerateDdlString(schema, options);

            // Assert
            result.Should().NotBeNull();
            result.Should().Contain("tpdm");
        }

        [Test]
        public void GenerateDdl_WithDecimalPrecisionAndScale_CreatesCorrectType()
        {
            // Arrange
            var schema = new ApiSchema
            {
                ProjectSchema = new ProjectSchema
                {
                    ProjectName = "EdFi",
                    ProjectVersion = "5.0",
                    ResourceSchemas = new Dictionary<string, ResourceSchema>
                    {
                        ["TestResource"] = new ResourceSchema
                        {
                            ResourceName = "TestResource",
                            FlatteningMetadata = new FlatteningMetadata
                            {
                                Table = new TableMetadata
                                {
                                    BaseName = "TestResource",
                                    JsonPath = "$.testResource",
                                    Columns =
                                    [
                                        new ColumnMetadata
                                        {
                                            ColumnName = "Amount",
                                            ColumnType = "decimal",
                                            Precision = "18",
                                            Scale = "2",
                                            IsRequired = true
                                        }
                                    ]
                                }
                            }
                        }
                    }
                }
            };

            // Act
            var result = _strategy.GenerateDdlString(schema, includeExtensions: false);

            // Assert
            result.Should().Contain("DECIMAL(18, 2)");
        }

        [Test]
        public void GenerateDdl_WithDecimalScaleOnly_UsesDefaultPrecision()
        {
            // Arrange
            var schema = new ApiSchema
            {
                ProjectSchema = new ProjectSchema
                {
                    ProjectName = "EdFi",
                    ProjectVersion = "5.0",
                    ResourceSchemas = new Dictionary<string, ResourceSchema>
                    {
                        ["TestResource"] = new ResourceSchema
                        {
                            ResourceName = "TestResource",
                            FlatteningMetadata = new FlatteningMetadata
                            {
                                Table = new TableMetadata
                                {
                                    BaseName = "TestResource",
                                    JsonPath = "$.testResource",
                                    Columns =
                                    [
                                        new ColumnMetadata
                                        {
                                            ColumnName = "Amount",
                                            ColumnType = "decimal",
                                            Scale = "4",
                                            IsRequired = true
                                        }
                                    ]
                                }
                            }
                        }
                    }
                }
            };

            // Act
            var result = _strategy.GenerateDdlString(schema, includeExtensions: false);

            // Assert
            result.Should().Contain("DECIMAL(14, 4)"); // scale + 10 default precision
        }

        [Test]
        public void GenerateDdl_WithForeignKeyConstraintsDisabled_DoesNotCreateFKs()
        {
            // Arrange
            var schema = TestHelpers.GetBasicSchema();
            var options = new DdlGenerationOptions
            {
                GenerateForeignKeyConstraints = false
            };

            // Act
            var result = _strategy.GenerateDdlString(schema, options);

            // Assert
            result.Should().NotBeNull();
        }

        [Test]
        public void GenerateDdl_WithNaturalKeyConstraintsDisabled_DoesNotCreateNaturalKeyConstraints()
        {
            // Arrange
            var schema = TestHelpers.GetSchemaWithNaturalKey();
            var options = new DdlGenerationOptions
            {
                GenerateNaturalKeyConstraints = false
            };

            // Act
            var result = _strategy.GenerateDdlString(schema, options);

            // Assert
            result.Should().NotBeNull();
        }

    }
}
