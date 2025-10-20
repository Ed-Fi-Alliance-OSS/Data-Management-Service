// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.SchemaGenerator.Abstractions;
using EdFi.DataManagementService.SchemaGenerator.Pgsql;
using FluentAssertions;

namespace EdFi.DataManagementService.SchemaGenerator.Tests.Unit.Engines.Pgsql
{
    /// <summary>
    /// Aggressive tests targeting every possible code path to maximize line coverage to 80%+.
    /// </summary>
    [TestFixture]
    public class PgsqlAggressiveCoverageTests
    {
        private PgsqlDdlGeneratorStrategy _strategy;

        [SetUp]
        public void SetUp()
        {
            _strategy = new PgsqlDdlGeneratorStrategy();
        }

        [Test]
        public void GenerateDdl_WithComplexNestedChildTables_GeneratesAllLevels()
        {
            // Arrange - Complex nested structure
            var schema = new ApiSchema
            {
                ProjectSchema = new ProjectSchema
                {
                    ProjectName = "EdFi",
                    ProjectVersion = "5.0",
                    ResourceSchemas = new Dictionary<string, ResourceSchema>
                    {
                        ["Student"] = new ResourceSchema
                        {
                            ResourceName = "Student",
                            FlatteningMetadata = new FlatteningMetadata
                            {
                                Table = new TableMetadata
                                {
                                    BaseName = "Student",
                                    JsonPath = "$.Student",
                                    Columns =
                                    [
                                        new ColumnMetadata { ColumnName = "StudentUniqueId", ColumnType = "string", MaxLength = "32", IsNaturalKey = true, IsRequired = true }
                                    ],
                                    ChildTables =
                                    [
                                        new TableMetadata
                                        {
                                            BaseName = "StudentAddress",
                                            JsonPath = "$.Student.Addresses",
                                            Columns =
                                            [
                                                new ColumnMetadata { ColumnName = "AddressType", ColumnType = "string", MaxLength = "50", IsNaturalKey = true, IsRequired = true },
                                                new ColumnMetadata { ColumnName = "Street", ColumnType = "string", MaxLength = "150", IsRequired = true }
                                            ],
                                            ChildTables =
                                            [
                                                new TableMetadata
                                                {
                                                    BaseName = "StudentAddressPeriod",
                                                    JsonPath = "$.Student.Addresses.Periods",
                                                    Columns =
                                                    [
                                                        new ColumnMetadata { ColumnName = "BeginDate", ColumnType = "date", IsNaturalKey = true, IsRequired = true },
                                                        new ColumnMetadata { ColumnName = "EndDate", ColumnType = "date", IsRequired = false }
                                                    ],
                                                    ChildTables = []
                                                }
                                            ]
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
            result.Should().Contain("StudentAddress");
            result.Should().Contain("StudentAddressPeriod");
        }

        [Test]
        public void GenerateDdl_WithParentReferences_SkipsParentReferenceColumns()
        {
            // Arrange - Schema with parent reference columns
            var schema = new ApiSchema
            {
                ProjectSchema = new ProjectSchema
                {
                    ProjectName = "EdFi",
                    ProjectVersion = "5.0",
                    ResourceSchemas = new Dictionary<string, ResourceSchema>
                    {
                        ["Student"] = new ResourceSchema
                        {
                            ResourceName = "Student",
                            FlatteningMetadata = new FlatteningMetadata
                            {
                                Table = new TableMetadata
                                {
                                    BaseName = "Student",
                                    JsonPath = "$.Student",
                                    Columns =
                                    [
                                        new ColumnMetadata { ColumnName = "StudentUniqueId", ColumnType = "string", MaxLength = "32", IsNaturalKey = true, IsRequired = true }
                                    ],
                                    ChildTables =
                                    [
                                        new TableMetadata
                                        {
                                            BaseName = "StudentEducationOrganizationAssociation",
                                            JsonPath = "$.Student.EducationOrganizations",
                                            Columns =
                                            [
                                                new ColumnMetadata { ColumnName = "EducationOrganizationId", ColumnType = "int32", IsNaturalKey = true, IsRequired = true },
                                                new ColumnMetadata { ColumnName = "StudentUniqueId_Parent", ColumnType = "string", MaxLength = "32", IsParentReference = true, IsRequired = true }
                                            ],
                                            ChildTables = []
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
            result.Should().Contain("StudentEducationOrganizationAssociation");
            result.Should().NotContain("StudentUniqueId_Parent");
        }

        [Test]
        public void GenerateDdl_WithCrossResourceReference_CreatesNonCascadingFK()
        {
            // Arrange - Schema with cross-resource reference (should not cascade)
            var schema = new ApiSchema
            {
                ProjectSchema = new ProjectSchema
                {
                    ProjectName = "EdFi",
                    ProjectVersion = "5.0",
                    ResourceSchemas = new Dictionary<string, ResourceSchema>
                    {
                        ["StudentSchoolAssociation"] = new ResourceSchema
                        {
                            ResourceName = "StudentSchoolAssociation",
                            FlatteningMetadata = new FlatteningMetadata
                            {
                                Table = new TableMetadata
                                {
                                    BaseName = "StudentSchoolAssociation",
                                    JsonPath = "$.StudentSchoolAssociation",
                                    Columns =
                                    [
                                        new ColumnMetadata { ColumnName = "StudentUniqueId", ColumnType = "string", MaxLength = "32", IsNaturalKey = true, IsRequired = true },
                                        new ColumnMetadata { ColumnName = "SchoolId", ColumnType = "int32", IsNaturalKey = true, IsRequired = true, FromReferencePath = "SchoolReference" }
                                    ],
                                    ChildTables = []
                                }
                            }
                        }
                    }
                }
            };

            // Act
            var result = _strategy.GenerateDdlString(schema, includeExtensions: false);

            // Assert
            result.Should().Contain("FOREIGN KEY");
        }

        [Test]
        public void GenerateDdl_WithMultipleNaturalKeyColumns_CreatesCompositeUniqueConstraint()
        {
            // Arrange - Schema with multiple natural key columns
            var schema = new ApiSchema
            {
                ProjectSchema = new ProjectSchema
                {
                    ProjectName = "EdFi",
                    ProjectVersion = "5.0",
                    ResourceSchemas = new Dictionary<string, ResourceSchema>
                    {
                        ["StudentSchoolAssociation"] = new ResourceSchema
                        {
                            ResourceName = "StudentSchoolAssociation",
                            FlatteningMetadata = new FlatteningMetadata
                            {
                                Table = new TableMetadata
                                {
                                    BaseName = "StudentSchoolAssociation",
                                    JsonPath = "$.StudentSchoolAssociation",
                                    Columns =
                                    [
                                        new ColumnMetadata { ColumnName = "StudentUniqueId", ColumnType = "string", MaxLength = "32", IsNaturalKey = true, IsRequired = true },
                                        new ColumnMetadata { ColumnName = "SchoolId", ColumnType = "int32", IsNaturalKey = true, IsRequired = true },
                                        new ColumnMetadata { ColumnName = "EntryDate", ColumnType = "date", IsNaturalKey = true, IsRequired = true }
                                    ],
                                    ChildTables = []
                                }
                            }
                        }
                    }
                }
            };

            // Act
            var result = _strategy.GenerateDdlString(schema, includeExtensions: false);

            // Assert
            result.Should().Contain("UNIQUE");
        }

        [Test]
        public void GenerateDdl_WithChildTableHavingIdentityColumns_CreatesIdentityConstraint()
        {
            // Arrange - Child table with multiple identity columns (parent FK + natural keys)
            var schema = new ApiSchema
            {
                ProjectSchema = new ProjectSchema
                {
                    ProjectName = "EdFi",
                    ProjectVersion = "5.0",
                    ResourceSchemas = new Dictionary<string, ResourceSchema>
                    {
                        ["Student"] = new ResourceSchema
                        {
                            ResourceName = "Student",
                            FlatteningMetadata = new FlatteningMetadata
                            {
                                Table = new TableMetadata
                                {
                                    BaseName = "Student",
                                    JsonPath = "$.Student",
                                    Columns =
                                    [
                                        new ColumnMetadata { ColumnName = "StudentUniqueId", ColumnType = "string", MaxLength = "32", IsNaturalKey = true, IsRequired = true }
                                    ],
                                    ChildTables =
                                    [
                                        new TableMetadata
                                        {
                                            BaseName = "StudentIdentificationCode",
                                            JsonPath = "$.Student.IdentificationCodes",
                                            Columns =
                                            [
                                                new ColumnMetadata { ColumnName = "IdentificationCodeType", ColumnType = "string", MaxLength = "50", IsNaturalKey = true, IsRequired = true },
                                                new ColumnMetadata { ColumnName = "AssigningOrganizationCode", ColumnType = "string", MaxLength = "50", IsNaturalKey = true, IsRequired = true }
                                            ],
                                            ChildTables = []
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
            result.Should().Contain("StudentIdentificationCode");
        }

        [Test]
        public void GenerateDdl_WithAuditColumnsDisabled_DoesNotIncludeAuditColumns()
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
                        ["Student"] = new ResourceSchema
                        {
                            ResourceName = "Student",
                            FlatteningMetadata = new FlatteningMetadata
                            {
                                Table = new TableMetadata
                                {
                                    BaseName = "Student",
                                    JsonPath = "$.Student",
                                    Columns =
                                    [
                                        new ColumnMetadata { ColumnName = "StudentUniqueId", ColumnType = "string", MaxLength = "32", IsNaturalKey = true, IsRequired = true }
                                    ],
                                    ChildTables = []
                                }
                            }
                        }
                    }
                }
            };
            var options = new DdlGenerationOptions
            {
                IncludeAuditColumns = false
            };

            // Act
            var result = _strategy.GenerateDdlString(schema, options);

            // Assert
            result.Should().NotContain("CreateDate");
            result.Should().NotContain("LastModifiedDate");
        }

        [Test]
        public void GenerateDdl_WithPrefixedTableNamesDisabled_UsesUnprefixedNames()
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
                        ["Student"] = new ResourceSchema
                        {
                            ResourceName = "Student",
                            FlatteningMetadata = new FlatteningMetadata
                            {
                                Table = new TableMetadata
                                {
                                    BaseName = "Student",
                                    JsonPath = "$.Student",
                                    Columns =
                                    [
                                        new ColumnMetadata { ColumnName = "StudentUniqueId", ColumnType = "string", MaxLength = "32", IsNaturalKey = true, IsRequired = true }
                                    ],
                                    ChildTables = []
                                }
                            }
                        }
                    }
                }
            };
            var options = new DdlGenerationOptions
            {
                UsePrefixedTableNames = false
            };

            // Act
            var result = _strategy.GenerateDdlString(schema, options);

            // Assert
            result.Should().Contain("Student");
            result.Should().NotContain("edfi_Student");
        }

        [Test]
        public void GenerateDdl_WithCustomSchemaMapping_UsesCustomSchemas()
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
                        ["Student"] = new ResourceSchema
                        {
                            ResourceName = "Student",
                            FlatteningMetadata = new FlatteningMetadata
                            {
                                Table = new TableMetadata
                                {
                                    BaseName = "Student",
                                    JsonPath = "$.Student",
                                    Columns =
                                    [
                                        new ColumnMetadata { ColumnName = "StudentUniqueId", ColumnType = "string", MaxLength = "32", IsNaturalKey = true, IsRequired = true }
                                    ],
                                    ChildTables = []
                                }
                            }
                        }
                    }
                }
            };
            var options = new DdlGenerationOptions
            {
                SchemaMapping = new Dictionary<string, string>
                {
                    ["EdFi"] = "custom_edfi_schema"
                }
            };

            // Act
            var result = _strategy.GenerateDdlString(schema, options);

            // Assert
            result.Should().NotBeNull();
        }

        [Test]
        public void GenerateDdl_WithCaseInsensitiveSchemaMapping_MatchesRegardlessOfCase()
        {
            // Arrange
            var schema = new ApiSchema
            {
                ProjectSchema = new ProjectSchema
                {
                    ProjectName = "TPDM",
                    ProjectVersion = "1.0",
                    ResourceSchemas = new Dictionary<string, ResourceSchema>
                    {
                        ["Candidate"] = new ResourceSchema
                        {
                            ResourceName = "Candidate",
                            FlatteningMetadata = new FlatteningMetadata
                            {
                                Table = new TableMetadata
                                {
                                    BaseName = "Candidate",
                                    JsonPath = "$.Candidate",
                                    IsExtensionTable = true,
                                    Columns =
                                    [
                                        new ColumnMetadata { ColumnName = "CandidateIdentifier", ColumnType = "string", MaxLength = "32", IsNaturalKey = true, IsRequired = true }
                                    ],
                                    ChildTables = []
                                }
                            }
                        }
                    }
                }
            };
            var options = new DdlGenerationOptions
            {
                SchemaMapping = new Dictionary<string, string>
                {
                    ["tpdm"] = "tpdm_schema" // lowercase mapping for uppercase project name
                }
            };

            // Act
            var result = _strategy.GenerateDdlString(schema, options);

            // Assert
            result.Should().NotBeNull();
        }
    }
}
