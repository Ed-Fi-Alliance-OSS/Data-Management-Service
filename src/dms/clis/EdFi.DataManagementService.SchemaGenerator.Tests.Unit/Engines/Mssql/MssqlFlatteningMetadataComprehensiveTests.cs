// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.SchemaGenerator.Abstractions;
using EdFi.DataManagementService.SchemaGenerator.Mssql;
using FluentAssertions;
using Snapshooter.NUnit;

namespace EdFi.DataManagementService.SchemaGenerator.Tests.Unit.Engines.Mssql
{
    /// <summary>
    /// Tests demonstrating generation of real DS sections from MetaEd flatteningMetadata for SQL Server.
    /// These tests use actual flatteningMetadata structures from MetaEd test packages.
    /// </summary>
    [TestFixture]
    public class MssqlFlatteningMetadataComprehensiveTests
    {
        /// <summary>
        /// Validates DDL generation for a simple entity with scalar properties (string, integer, optional reference).
        /// Tests: NVARCHAR with length, INT types, NOT NULL constraints, natural key unique constraint, IDENTITY columns.
        /// </summary>
        [Test]
        public void ValidateSimpleEntityWithScalarProperties_GeneratesCorrectDdl()
        {
            // Arrange - flatteningMetadata from MetaEd test
            var schema = new ApiSchema
            {
                ProjectSchema = new ProjectSchema
                {
                    ProjectName = "EdFi",
                    ProjectVersion = "1.0.0",
                    IsExtensionProject = false,
                    ResourceSchemas = new Dictionary<string, ResourceSchema>
                    {
                        ["DomainEntityName"] = new ResourceSchema
                        {
                            ResourceName = "DomainEntityName",
                            FlatteningMetadata = new FlatteningMetadata
                            {
                                Table = new TableMetadata
                                {
                                    BaseName = "DomainEntityName",
                                    JsonPath = "$",
                                    Columns =
                                    [
                                        new ColumnMetadata
                                        {
                                            ColumnName = "StringIdentity",
                                            ColumnType = "string",
                                            IsNaturalKey = true,
                                            IsRequired = true,
                                            JsonPath = "$.stringIdentity",
                                            MaxLength = "30",
                                        },
                                        new ColumnMetadata
                                        {
                                            ColumnName = "RequiredIntegerProperty",
                                            ColumnType = "integer",
                                            IsRequired = true,
                                            JsonPath = "$.requiredIntegerProperty",
                                        },
                                        new ColumnMetadata
                                        {
                                            ColumnName = "SchoolYear",
                                            ColumnType = "integer",
                                            IsRequired = false,
                                            JsonPath = "$.schoolYearTypeReference.schoolYear",
                                        },
                                    ],
                                    ChildTables = [],
                                },
                            },
                        },
                    },
                },
            };

            var generator = new MssqlDdlGeneratorStrategy();

            // Act
            var sql = generator.GenerateDdlString(schema, includeExtensions: false);

            // Assert - Verify the generated DDL matches the snapshot
            Snapshot.Match(sql);
        }

        /// <summary>
        /// Validates DDL generation for an entity with collection properties that create child tables.
        /// Tests: Parent-child table relationships, foreign key constraints with ON DELETE CASCADE, child table naming.
        /// </summary>
        [Test]
        public void ValidateEntityWithCollectionProperties_GeneratesChildTables()
        {
            // Arrange - flatteningMetadata from MetaEd test
            var schema = new ApiSchema
            {
                ProjectSchema = new ProjectSchema
                {
                    ProjectName = "EdFi",
                    ProjectVersion = "1.0.0",
                    IsExtensionProject = false,
                    ResourceSchemas = new Dictionary<string, ResourceSchema>
                    {
                        ["DomainEntityName"] = new ResourceSchema
                        {
                            ResourceName = "DomainEntityName",
                            FlatteningMetadata = new FlatteningMetadata
                            {
                                Table = new TableMetadata
                                {
                                    BaseName = "DomainEntityName",
                                    JsonPath = "$",
                                    Columns =
                                    [
                                        new ColumnMetadata
                                        {
                                            ColumnName = "StringIdentity",
                                            ColumnType = "string",
                                            IsNaturalKey = true,
                                            IsRequired = true,
                                            JsonPath = "$.stringIdentity",
                                            MaxLength = "30",
                                        },
                                    ],
                                    ChildTables =
                                    [
                                        new TableMetadata
                                        {
                                            BaseName = "DomainEntityNameRequiredIntegerProperty",
                                            JsonPath = "$.requiredIntegerProperties[*]",
                                            Columns =
                                            [
                                                new ColumnMetadata
                                                {
                                                    ColumnName = "RequiredIntegerProperty",
                                                    ColumnType = "integer",
                                                    IsRequired = false,
                                                    JsonPath =
                                                        "$.requiredIntegerProperties[*].requiredIntegerProperty",
                                                },
                                                new ColumnMetadata
                                                {
                                                    ColumnName = "DomainEntityName_Id",
                                                    ColumnType = "bigint",
                                                    IsParentReference = true,
                                                    IsRequired = true,
                                                },
                                            ],
                                            ChildTables = [],
                                        },
                                        new TableMetadata
                                        {
                                            BaseName = "DomainEntityNameSchoolYear",
                                            JsonPath = "$.schoolYearTypeReference[*]",
                                            Columns =
                                            [
                                                new ColumnMetadata
                                                {
                                                    ColumnName = "SchoolYear",
                                                    ColumnType = "integer",
                                                    IsRequired = false,
                                                    JsonPath = "$.schoolYearTypeReference[*].schoolYear",
                                                },
                                                new ColumnMetadata
                                                {
                                                    ColumnName = "DomainEntityName_Id",
                                                    ColumnType = "bigint",
                                                    IsParentReference = true,
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

            var generator = new MssqlDdlGeneratorStrategy();

            // Act
            var sql = generator.GenerateDdlString(schema, includeExtensions: false);

            // Assert - Verify the generated DDL matches the snapshot
            Snapshot.Match(sql);
        }

        /// <summary>
        /// Validates DDL generation for entities with reference chains (entity → reference → reference with identity).
        /// Tests: Multi-level foreign key relationships, identity columns as foreign keys, complex reference resolution.
        /// </summary>
        [Test]
        public void ValidateNestedReferenceChains_GeneratesMultilevelForeignKeys()
        {
            // Arrange - flatteningMetadata from MetaEd test
            var schema = new ApiSchema
            {
                ProjectSchema = new ProjectSchema
                {
                    ProjectName = "EdFi",
                    ProjectVersion = "1.0.0",
                    IsExtensionProject = false,
                    ResourceSchemas = new Dictionary<string, ResourceSchema>
                    {
                        ["DomainEntityName"] = new ResourceSchema
                        {
                            ResourceName = "DomainEntityName",
                            FlatteningMetadata = new FlatteningMetadata
                            {
                                Table = new TableMetadata
                                {
                                    BaseName = "DomainEntityName",
                                    JsonPath = "$",
                                    Columns =
                                    [
                                        new ColumnMetadata
                                        {
                                            ColumnName = "SectionIdentifier",
                                            ColumnType = "string",
                                            IsNaturalKey = true,
                                            IsRequired = true,
                                            JsonPath = "$.sectionIdentifier",
                                            MaxLength = "30",
                                        },
                                        new ColumnMetadata
                                        {
                                            ColumnName = "CourseOffering_Id",
                                            ColumnType = "bigint",
                                            FromReferencePath = "CourseOffering",
                                            IsNaturalKey = true,
                                            IsRequired = true,
                                        },
                                    ],
                                    ChildTables =
                                    [
                                        new TableMetadata
                                        {
                                            BaseName = "DomainEntityNameClassPeriod",
                                            JsonPath = "$.classPeriods[*]",
                                            Columns =
                                            [
                                                new ColumnMetadata
                                                {
                                                    ColumnName = "ClassPeriod_Id",
                                                    ColumnType = "bigint",
                                                    FromReferencePath = "ClassPeriod",
                                                    IsRequired = true,
                                                },
                                                new ColumnMetadata
                                                {
                                                    ColumnName = "DomainEntityName_Id",
                                                    ColumnType = "bigint",
                                                    IsParentReference = true,
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

            var generator = new MssqlDdlGeneratorStrategy();

            // Act
            var sql = generator.GenerateDdlString(schema, includeExtensions: false);

            // Assert - Verify the generated DDL matches the snapshot
            Snapshot.Match(sql);
        }

        /// <summary>
        /// Validates DDL generation for entities with implicit reference merging (shared reference resolution).
        /// Tests: Composite natural keys with references, implicit merge of School references from different paths.
        /// </summary>
        [Test]
        public void ValidateImplicitReferenceMerging_GeneratesCompositeNaturalKeys()
        {
            // Arrange - flatteningMetadata from MetaEd test
            var schema = new ApiSchema
            {
                ProjectSchema = new ProjectSchema
                {
                    ProjectName = "EdFi",
                    ProjectVersion = "1.0.0",
                    IsExtensionProject = false,
                    ResourceSchemas = new Dictionary<string, ResourceSchema>
                    {
                        ["DomainEntityName"] = new ResourceSchema
                        {
                            ResourceName = "DomainEntityName",
                            FlatteningMetadata = new FlatteningMetadata
                            {
                                Table = new TableMetadata
                                {
                                    BaseName = "DomainEntityName",
                                    JsonPath = "$",
                                    Columns =
                                    [
                                        new ColumnMetadata
                                        {
                                            ColumnName = "SectionIdentifier",
                                            ColumnType = "string",
                                            IsNaturalKey = true,
                                            IsRequired = true,
                                            JsonPath = "$.sectionIdentifier",
                                            MaxLength = "30",
                                        },
                                        new ColumnMetadata
                                        {
                                            ColumnName = "CourseOffering_Id",
                                            ColumnType = "bigint",
                                            FromReferencePath = "CourseOffering",
                                            IsNaturalKey = true,
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

            var generator = new MssqlDdlGeneratorStrategy();

            // Act
            var sql = generator.GenerateDdlString(schema, includeExtensions: false);

            // Assert - Verify the generated DDL matches the snapshot
            Snapshot.Match(sql);
        }

        /// <summary>
        /// Validates DDL generation for entities with choice structures and inline common elements.
        /// Tests: Nested choice handling, inline common references, complex child table with choice-based foreign keys.
        /// </summary>
        [Test]
        public void ValidateNestedChoiceStructures_GeneratesChoiceBasedForeignKeys()
        {
            // Arrange - flatteningMetadata from MetaEd test
            var schema = new ApiSchema
            {
                ProjectSchema = new ProjectSchema
                {
                    ProjectName = "EdFi",
                    ProjectVersion = "1.0.0",
                    IsExtensionProject = false,
                    ResourceSchemas = new Dictionary<string, ResourceSchema>
                    {
                        ["EducationContent"] = new ResourceSchema
                        {
                            ResourceName = "EducationContent",
                            FlatteningMetadata = new FlatteningMetadata
                            {
                                Table = new TableMetadata
                                {
                                    BaseName = "EducationContent",
                                    JsonPath = "$",
                                    Columns =
                                    [
                                        new ColumnMetadata
                                        {
                                            ColumnName = "ContentIdentifier",
                                            ColumnType = "string",
                                            IsNaturalKey = true,
                                            IsRequired = true,
                                            JsonPath = "$.contentIdentifier",
                                            MaxLength = "30",
                                        },
                                        new ColumnMetadata
                                        {
                                            ColumnName = "LearningResourceMetadataURI",
                                            ColumnType = "string",
                                            IsRequired = false,
                                            JsonPath = "$.learningResourceMetadataURI",
                                            MaxLength = "30",
                                        },
                                        new ColumnMetadata
                                        {
                                            ColumnName = "Description",
                                            ColumnType = "string",
                                            IsRequired = false,
                                            JsonPath = "$.description",
                                            MaxLength = "30",
                                        },
                                        new ColumnMetadata
                                        {
                                            ColumnName = "ShortDescription",
                                            ColumnType = "string",
                                            IsRequired = false,
                                            JsonPath = "$.shortDescription",
                                            MaxLength = "30",
                                        },
                                        new ColumnMetadata
                                        {
                                            ColumnName = "ContentClassDescriptorId",
                                            ColumnType = "descriptor",
                                            IsRequired = false,
                                            JsonPath = "$.contentClassDescriptor",
                                        },
                                    ],
                                    ChildTables =
                                    [
                                        new TableMetadata
                                        {
                                            BaseName = "EducationContentDerivativeSourceEducationContent",
                                            JsonPath = "$.derivativeSourceEducationContents[*]",
                                            Columns =
                                            [
                                                new ColumnMetadata
                                                {
                                                    ColumnName = "DerivativeSource_EducationContent_Id",
                                                    ColumnType = "bigint",
                                                    FromReferencePath =
                                                        "LearningResourceChoice.LearningResource.DerivativeSourceEducationContentSource.EducationContent",
                                                    IsRequired = false,
                                                },
                                                new ColumnMetadata
                                                {
                                                    ColumnName = "EducationContent_Id",
                                                    ColumnType = "bigint",
                                                    IsParentReference = true,
                                                    IsRequired = true,
                                                },
                                            ],
                                            ChildTables = [],
                                        },
                                        new TableMetadata
                                        {
                                            BaseName = "EducationContentRequiredURI",
                                            JsonPath = "$.requiredURIs[*]",
                                            Columns =
                                            [
                                                new ColumnMetadata
                                                {
                                                    ColumnName = "RequiredURI",
                                                    ColumnType = "string",
                                                    IsRequired = false,
                                                    JsonPath = "$.requiredURIs[*].requiredURI",
                                                    MaxLength = "30",
                                                },
                                                new ColumnMetadata
                                                {
                                                    ColumnName = "EducationContent_Id",
                                                    ColumnType = "bigint",
                                                    IsParentReference = true,
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

            var generator = new MssqlDdlGeneratorStrategy();

            // Act
            var sql = generator.GenerateDdlString(schema, includeExtensions: false);

            // Assert - Verify the generated DDL matches the snapshot
            Snapshot.Match(sql);
        }

        /// <summary>
        /// Validates DDL generation for scalar collections with naming conflicts (property name includes parent entity prefix).
        /// Tests: Table naming when collection property already contains parent entity name, deduplication logic.
        /// </summary>
        [Test]
        public void ValidateScalarCollectionNamingConflicts_AppliesDeduplicationLogic()
        {
            // Arrange - flatteningMetadata from MetaEd test
            var schema = new ApiSchema
            {
                ProjectSchema = new ProjectSchema
                {
                    ProjectName = "EdFi",
                    ProjectVersion = "1.0.0",
                    IsExtensionProject = false,
                    ResourceSchemas = new Dictionary<string, ResourceSchema>
                    {
                        ["EducationContent"] = new ResourceSchema
                        {
                            ResourceName = "EducationContent",
                            FlatteningMetadata = new FlatteningMetadata
                            {
                                Table = new TableMetadata
                                {
                                    BaseName = "EducationContent",
                                    JsonPath = "$",
                                    Columns =
                                    [
                                        new ColumnMetadata
                                        {
                                            ColumnName = "ContentIdentifier",
                                            ColumnType = "string",
                                            IsNaturalKey = true,
                                            IsRequired = true,
                                            JsonPath = "$.contentIdentifier",
                                            MaxLength = "30",
                                        },
                                    ],
                                    ChildTables =
                                    [
                                        new TableMetadata
                                        {
                                            BaseName = "EducationContentEducationContentSuffixName",
                                            JsonPath = "$.suffixNames[*]",
                                            Columns =
                                            [
                                                new ColumnMetadata
                                                {
                                                    ColumnName = "EducationContentSuffixName",
                                                    ColumnType = "string",
                                                    IsRequired = false,
                                                    JsonPath = "$.suffixNames[*].educationContentSuffixName",
                                                    MaxLength = "30",
                                                },
                                                new ColumnMetadata
                                                {
                                                    ColumnName = "EducationContent_Id",
                                                    ColumnType = "bigint",
                                                    IsParentReference = true,
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

            var generator = new MssqlDdlGeneratorStrategy();

            // Act
            var sql = generator.GenerateDdlString(schema, includeExtensions: false);

            // Assert - Verify the generated DDL matches the snapshot
            Snapshot.Match(sql);
        }

        /// <summary>
        /// Validates DDL generation for entities with acronym-based property names (special casing preservation).
        /// Tests: Handling of acronyms like IEP in column names, DATETIME2 type mapping, composite keys with mixed types.
        /// </summary>
        [Test]
        public void ValidateAcronymPropertyNames_PreservesSpecialCasing()
        {
            // Arrange - flatteningMetadata from MetaEd test
            var schema = new ApiSchema
            {
                ProjectSchema = new ProjectSchema
                {
                    ProjectName = "EdFi",
                    ProjectVersion = "1.0.0",
                    IsExtensionProject = false,
                    ResourceSchemas = new Dictionary<string, ResourceSchema>
                    {
                        ["StudentSpecialEducationProgramAssociation"] = new ResourceSchema
                        {
                            ResourceName = "StudentSpecialEducationProgramAssociation",
                            FlatteningMetadata = new FlatteningMetadata
                            {
                                Table = new TableMetadata
                                {
                                    BaseName = "StudentSpecialEducationProgramAssociation",
                                    JsonPath = "$",
                                    Columns =
                                    [
                                        new ColumnMetadata
                                        {
                                            ColumnName = "ContentIdentifier",
                                            ColumnType = "string",
                                            IsNaturalKey = true,
                                            IsRequired = true,
                                            JsonPath = "$.contentIdentifier",
                                            MaxLength = "30",
                                        },
                                        new ColumnMetadata
                                        {
                                            ColumnName = "IEPBeginDate",
                                            ColumnType = "datetime",
                                            IsNaturalKey = true,
                                            IsRequired = true,
                                            JsonPath = "$.iepBeginDate",
                                        },
                                    ],
                                    ChildTables = [],
                                },
                            },
                        },
                    },
                },
            };

            var generator = new MssqlDdlGeneratorStrategy();

            // Act
            var sql = generator.GenerateDdlString(schema, includeExtensions: false);

            // Assert - Verify the generated DDL matches the snapshot
            Snapshot.Match(sql);
        }

        /// <summary>
        /// Validates DDL generation for entities with inline common collections (reusable common element in collection).
        /// Tests: Common/inline element handling in child tables, descriptor foreign keys in collections, composite unique constraints.
        /// </summary>
        [Test]
        public void ValidateInlineCommonCollections_GeneratesDescriptorForeignKeys()
        {
            // Arrange - flatteningMetadata from MetaEd test
            var schema = new ApiSchema
            {
                ProjectSchema = new ProjectSchema
                {
                    ProjectName = "EdFi",
                    ProjectVersion = "1.0.0",
                    IsExtensionProject = false,
                    ResourceSchemas = new Dictionary<string, ResourceSchema>
                    {
                        ["Assessment"] = new ResourceSchema
                        {
                            ResourceName = "Assessment",
                            FlatteningMetadata = new FlatteningMetadata
                            {
                                Table = new TableMetadata
                                {
                                    BaseName = "Assessment",
                                    JsonPath = "$",
                                    Columns =
                                    [
                                        new ColumnMetadata
                                        {
                                            ColumnName = "AssessmentIdentifier",
                                            ColumnType = "integer",
                                            IsNaturalKey = true,
                                            IsRequired = true,
                                            JsonPath = "$.assessmentIdentifier",
                                        },
                                    ],
                                    ChildTables =
                                    [
                                        new TableMetadata
                                        {
                                            BaseName = "AssessmentAssessmentIdentificationCode",
                                            JsonPath = "$.identificationCodes[*]",
                                            Columns =
                                            [
                                                new ColumnMetadata
                                                {
                                                    ColumnName = "IdentificationCode",
                                                    ColumnType = "string",
                                                    IsRequired = true,
                                                    JsonPath = "$.identificationCodes[*].identificationCode",
                                                    MaxLength = "30",
                                                },
                                                new ColumnMetadata
                                                {
                                                    ColumnName = "AssessmentIdentificationSystemDescriptorId",
                                                    ColumnType = "descriptor",
                                                    IsNaturalKey = true,
                                                    IsRequired = true,
                                                    JsonPath =
                                                        "$.identificationCodes[*].assessmentIdentificationSystemDescriptor",
                                                },
                                                new ColumnMetadata
                                                {
                                                    ColumnName = "Assessment_Id",
                                                    ColumnType = "bigint",
                                                    IsParentReference = true,
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

            var generator = new MssqlDdlGeneratorStrategy();

            // Act
            var sql = generator.GenerateDdlString(schema, includeExtensions: false);

            // Assert - Verify the generated DDL matches the snapshot
            Snapshot.Match(sql);
        }

        /// <summary>
        /// Validates DDL generation for subclass entities inheriting from a superclass (entity inheritance/polymorphism).
        /// Tests: Discriminator values, superclass identity columns, child tables attached to subclass entities.
        /// </summary>
        [Test]
        public void ValidateSubclassInheritance_GeneratesDiscriminatorAndSuperclassIdentity()
        {
            // Arrange - flatteningMetadata from MetaEd test
            var schema = new ApiSchema
            {
                ProjectSchema = new ProjectSchema
                {
                    ProjectName = "EdFi",
                    ProjectVersion = "1.0.0",
                    IsExtensionProject = false,
                    ResourceSchemas = new Dictionary<string, ResourceSchema>
                    {
                        ["CommunityOrganization"] = new ResourceSchema
                        {
                            ResourceName = "CommunityOrganization",
                            FlatteningMetadata = new FlatteningMetadata
                            {
                                Table = new TableMetadata
                                {
                                    BaseName = "CommunityOrganization",
                                    JsonPath = "$",
                                    DiscriminatorValue = "CommunityOrganization",
                                    Columns =
                                    [
                                        new ColumnMetadata
                                        {
                                            ColumnName = "CommunityOrganizationId",
                                            ColumnType = "integer",
                                            IsNaturalKey = true,
                                            IsRequired = true,
                                            IsSuperclassIdentity = true,
                                            JsonPath = "$.communityOrganizationId",
                                        },
                                    ],
                                    ChildTables =
                                    [
                                        new TableMetadata
                                        {
                                            BaseName =
                                                "CommunityOrganizationEducationOrganizationIdentificationCode",
                                            JsonPath = "$.identificationCodes[*]",
                                            Columns =
                                            [
                                                new ColumnMetadata
                                                {
                                                    ColumnName = "IdentificationCode",
                                                    ColumnType = "string",
                                                    IsRequired = true,
                                                    JsonPath = "$.identificationCodes[*].identificationCode",
                                                    MaxLength = "30",
                                                },
                                                new ColumnMetadata
                                                {
                                                    ColumnName =
                                                        "EducationOrganizationIdentificationSystemDescriptorId",
                                                    ColumnType = "descriptor",
                                                    IsNaturalKey = true,
                                                    IsRequired = true,
                                                    JsonPath =
                                                        "$.identificationCodes[*].educationOrganizationIdentificationSystemDescriptor",
                                                },
                                                new ColumnMetadata
                                                {
                                                    ColumnName = "CommunityOrganization_Id",
                                                    ColumnType = "bigint",
                                                    IsParentReference = true,
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

            var generator = new MssqlDdlGeneratorStrategy();

            // Act
            var sql = generator.GenerateDdlString(schema, includeExtensions: false);

            // Assert - Verify the generated DDL matches the snapshot
            Snapshot.Match(sql);
        }

        /// <summary>
        /// Validates DDL generation for associations with nested common collections (collection within a collection).
        /// Tests: Multi-level child table hierarchies, grandchild table foreign keys, nested array path handling.
        /// </summary>
        [Test]
        public void ValidateNestedCommonCollections_GeneratesMultilevelChildTables()
        {
            // Arrange - flatteningMetadata from MetaEd test
            var schema = new ApiSchema
            {
                ProjectSchema = new ProjectSchema
                {
                    ProjectName = "EdFi",
                    ProjectVersion = "1.0.0",
                    IsExtensionProject = false,
                    ResourceSchemas = new Dictionary<string, ResourceSchema>
                    {
                        ["StudentEducationOrganizationAssociation"] = new ResourceSchema
                        {
                            ResourceName = "StudentEducationOrganizationAssociation",
                            FlatteningMetadata = new FlatteningMetadata
                            {
                                Table = new TableMetadata
                                {
                                    BaseName = "StudentEducationOrganizationAssociation",
                                    JsonPath = "$",
                                    Columns =
                                    [
                                        new ColumnMetadata
                                        {
                                            ColumnName = "StudentId",
                                            ColumnType = "integer",
                                            IsNaturalKey = true,
                                            IsRequired = true,
                                            JsonPath = "$.studentId",
                                        },
                                    ],
                                    ChildTables =
                                    [
                                        new TableMetadata
                                        {
                                            BaseName = "StudentEducationOrganizationAssociationAddress",
                                            JsonPath = "$.addresses[*]",
                                            Columns =
                                            [
                                                new ColumnMetadata
                                                {
                                                    ColumnName = "StreetNumberName",
                                                    ColumnType = "string",
                                                    IsRequired = true,
                                                    JsonPath = "$.addresses[*].streetNumberName",
                                                    MaxLength = "30",
                                                },
                                                new ColumnMetadata
                                                {
                                                    ColumnName = "StudentEducationOrganizationAssociation_Id",
                                                    ColumnType = "bigint",
                                                    IsParentReference = true,
                                                    IsRequired = true,
                                                },
                                            ],
                                            ChildTables =
                                            [
                                                new TableMetadata
                                                {
                                                    BaseName =
                                                        "StudentEducationOrganizationAssociationAddressPeriod",
                                                    JsonPath = "$.addresses[*].periods[*]",
                                                    Columns =
                                                    [
                                                        new ColumnMetadata
                                                        {
                                                            ColumnName = "BeginDate",
                                                            ColumnType = "integer",
                                                            IsNaturalKey = true,
                                                            IsRequired = true,
                                                            JsonPath = "$.addresses[*].periods[*].beginDate",
                                                        },
                                                        new ColumnMetadata
                                                        {
                                                            ColumnName = "EndDate",
                                                            ColumnType = "integer",
                                                            IsRequired = false,
                                                            JsonPath = "$.addresses[*].periods[*].endDate",
                                                        },
                                                        new ColumnMetadata
                                                        {
                                                            ColumnName =
                                                                "StudentEducationOrganizationAssociationAddress_Id",
                                                            ColumnType = "bigint",
                                                            IsParentReference = true,
                                                            IsRequired = true,
                                                        },
                                                    ],
                                                    ChildTables = [],
                                                },
                                            ],
                                        },
                                    ],
                                },
                            },
                        },
                    },
                },
            };

            var generator = new MssqlDdlGeneratorStrategy();

            // Act
            var sql = generator.GenerateDdlString(schema, includeExtensions: false);

            // Assert - Verify the generated DDL matches the snapshot
            Snapshot.Match(sql);
        }

        /// <summary>
        /// Validates DDL generation for entities with role-named descriptor properties.
        /// Tests: Descriptor columns with role name prefix (e.g., AssessedGradeLevelDescriptorId), descriptor type mapping.
        /// </summary>
        [Test]
        public void ValidateDescriptorWithRoleName_GeneratesRoleBasedDescriptorColumn()
        {
            // Arrange - flatteningMetadata from MetaEd test (lines 3704-3745)
            var schema = new ApiSchema
            {
                ProjectSchema = new ProjectSchema
                {
                    ProjectName = "EdFi",
                    ProjectVersion = "1.0.0",
                    IsExtensionProject = false,
                    ResourceSchemas = new Dictionary<string, ResourceSchema>
                    {
                        ["Assessment"] = new ResourceSchema
                        {
                            ResourceName = "Assessment",
                            FlatteningMetadata = new FlatteningMetadata
                            {
                                Table = new TableMetadata
                                {
                                    BaseName = "Assessment",
                                    JsonPath = "$",
                                    Columns =
                                    [
                                        new ColumnMetadata
                                        {
                                            ColumnName = "AssessmentIdentifier",
                                            ColumnType = "integer",
                                            IsNaturalKey = true,
                                            IsRequired = true,
                                            JsonPath = "$.assessmentIdentifier",
                                        },
                                        new ColumnMetadata
                                        {
                                            ColumnName = "AssessedGradeLevelDescriptorId",
                                            ColumnType = "descriptor",
                                            IsRequired = false,
                                            JsonPath = "$.assessedGradeLevelDescriptor",
                                        },
                                    ],
                                    ChildTables = [],
                                },
                            },
                        },
                    },
                },
            };

            var generator = new MssqlDdlGeneratorStrategy();

            // Act
            var sql = generator.GenerateDdlString(schema, includeExtensions: false);

            // Assert - Verify the generated DDL matches the snapshot
            Snapshot.Match(sql);
        }

        /// <summary>
        /// Validates union view generation for abstract resources with multiple concrete subclass implementations.
        /// Tests: CREATE VIEW with UNION ALL, discriminator column injection, schema-prefixed view names.
        /// </summary>
        [Test]
        public void ValidateAbstractResourceWithSubclasses_GeneratesConcreteTablesForUnionView()
        {
            // Arrange - Create schema using JSON to include abstractResources
            var projectSchemaJson =
                @"{
                ""projectName"": ""EdFi"",
                ""projectVersion"": ""1.0.0"",
                ""isExtensionProject"": false,
                ""abstractResources"": {
                    ""educationOrganizations"": {
                        ""abstractResourceName"": ""EducationOrganization"",
                        ""flatteningMetadata"": {
                            ""subclassTypes"": [""schools"", ""localeducationagencys"", ""stateeducationagencys""],
                            ""unionViewName"": ""EducationOrganization""
                        }
                    }
                },
                ""resourceSchemas"": {
                    ""schools"": {
                        ""resourceName"": ""School"",
                        ""flatteningMetadata"": {
                            ""table"": {
                                ""baseName"": ""School"",
                                ""jsonPath"": ""$"",
                                ""discriminatorValue"": ""School"",
                                ""columns"": [
                                    {
                                        ""columnName"": ""EducationOrganizationId"",
                                        ""columnType"": ""integer"",
                                        ""isNaturalKey"": true,
                                        ""isRequired"": true,
                                        ""isSuperclassIdentity"": true,
                                        ""jsonPath"": ""$.educationOrganizationId""
                                    },
                                    {
                                        ""columnName"": ""NameOfInstitution"",
                                        ""columnType"": ""string"",
                                        ""isRequired"": true,
                                        ""jsonPath"": ""$.nameOfInstitution"",
                                        ""maxLength"": ""255""
                                    },
                                    {
                                        ""columnName"": ""SchoolTypeDescriptor"",
                                        ""columnType"": ""descriptor"",
                                        ""isRequired"": false,
                                        ""jsonPath"": ""$.schoolTypeDescriptor""
                                    }
                                ],
                                ""childTables"": []
                            }
                        }
                    },
                    ""localeducationagencys"": {
                        ""resourceName"": ""LocalEducationAgency"",
                        ""flatteningMetadata"": {
                            ""table"": {
                                ""baseName"": ""LocalEducationAgency"",
                                ""jsonPath"": ""$"",
                                ""discriminatorValue"": ""LocalEducationAgency"",
                                ""columns"": [
                                    {
                                        ""columnName"": ""EducationOrganizationId"",
                                        ""columnType"": ""integer"",
                                        ""isNaturalKey"": true,
                                        ""isRequired"": true,
                                        ""isSuperclassIdentity"": true,
                                        ""jsonPath"": ""$.educationOrganizationId""
                                    },
                                    {
                                        ""columnName"": ""NameOfInstitution"",
                                        ""columnType"": ""string"",
                                        ""isRequired"": true,
                                        ""jsonPath"": ""$.nameOfInstitution"",
                                        ""maxLength"": ""255""
                                    },
                                    {
                                        ""columnName"": ""LocalEducationAgencyCategoryDescriptor"",
                                        ""columnType"": ""descriptor"",
                                        ""isRequired"": true,
                                        ""jsonPath"": ""$.localEducationAgencyCategoryDescriptor""
                                    }
                                ],
                                ""childTables"": []
                            }
                        }
                    },
                    ""stateeducationagencys"": {
                        ""resourceName"": ""StateEducationAgency"",
                        ""flatteningMetadata"": {
                            ""table"": {
                                ""baseName"": ""StateEducationAgency"",
                                ""jsonPath"": ""$"",
                                ""discriminatorValue"": ""StateEducationAgency"",
                                ""columns"": [
                                    {
                                        ""columnName"": ""EducationOrganizationId"",
                                        ""columnType"": ""integer"",
                                        ""isNaturalKey"": true,
                                        ""isRequired"": true,
                                        ""isSuperclassIdentity"": true,
                                        ""jsonPath"": ""$.educationOrganizationId""
                                    },
                                    {
                                        ""columnName"": ""NameOfInstitution"",
                                        ""columnType"": ""string"",
                                        ""isRequired"": true,
                                        ""jsonPath"": ""$.nameOfInstitution"",
                                        ""maxLength"": ""255""
                                    },
                                    {
                                        ""columnName"": ""StateEducationAgencyCategoryDescriptor"",
                                        ""columnType"": ""descriptor"",
                                        ""isRequired"": false,
                                        ""jsonPath"": ""$.stateEducationAgencyCategoryDescriptor""
                                    }
                                ],
                                ""childTables"": []
                            }
                        }
                    }
                }
            }";

            var projectSchema = System.Text.Json.JsonSerializer.Deserialize<ProjectSchema>(
                projectSchemaJson,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            );

            var schema = new ApiSchema { ProjectSchema = projectSchema! };
            var generator = new MssqlDdlGeneratorStrategy();

            // Act - Generate DDL without skipping union views
            var sql = generator.GenerateDdlString(schema, includeExtensions: false, skipUnionViews: false);

            // Assert - Verify the generated DDL matches the snapshot
            Snapshot.Match(sql);
        }

        /// <summary>
        /// Validates polymorphic reference handling in entity tables.
        /// Tests: Polymorphic reference columns, correct foreign key generation for abstract resource references.
        /// </summary>
        [Test]
        public void ValidatePolymorphicReference_GeneratesCorrectForeignKey()
        {
            // Arrange - StudentSchoolAssociation referencing abstract EducationOrganization
            var schema = new ApiSchema
            {
                ProjectSchema = new ProjectSchema
                {
                    ProjectName = "EdFi",
                    ProjectVersion = "1.0.0",
                    IsExtensionProject = false,
                    ResourceSchemas = new Dictionary<string, ResourceSchema>
                    {
                        ["studentschoolassociations"] = new ResourceSchema
                        {
                            ResourceName = "StudentSchoolAssociation",
                            FlatteningMetadata = new FlatteningMetadata
                            {
                                Table = new TableMetadata
                                {
                                    BaseName = "StudentSchoolAssociation",
                                    JsonPath = "$",
                                    Columns =
                                    [
                                        new ColumnMetadata
                                        {
                                            ColumnName = "StudentUniqueId",
                                            ColumnType = "string",
                                            IsNaturalKey = true,
                                            IsRequired = true,
                                            JsonPath = "$.studentReference.studentUniqueId",
                                            MaxLength = "32",
                                        },
                                        new ColumnMetadata
                                        {
                                            ColumnName = "SchoolId",
                                            ColumnType = "integer",
                                            IsNaturalKey = true,
                                            IsRequired = true,
                                            IsPolymorphicReference = true,
                                            IsSuperclassIdentity = true,
                                            PolymorphicType = "EducationOrganization",
                                            JsonPath = "$.schoolReference.schoolId",
                                            FromReferencePath = "SchoolReference",
                                        },
                                        new ColumnMetadata
                                        {
                                            ColumnName = "EntryDate",
                                            ColumnType = "date",
                                            IsNaturalKey = true,
                                            IsRequired = true,
                                            JsonPath = "$.entryDate",
                                        },
                                        new ColumnMetadata
                                        {
                                            ColumnName = "EntryGradeLevelDescriptor",
                                            ColumnType = "descriptor",
                                            IsRequired = true,
                                            JsonPath = "$.entryGradeLevelDescriptor",
                                        },
                                        new ColumnMetadata
                                        {
                                            ColumnName = "ExitWithdrawDate",
                                            ColumnType = "date",
                                            IsRequired = false,
                                            JsonPath = "$.exitWithdrawDate",
                                        },
                                    ],
                                    ChildTables = [],
                                },
                            },
                        },
                    },
                },
            };

            var generator = new MssqlDdlGeneratorStrategy();

            // Act
            var sql = generator.GenerateDdlString(schema, includeExtensions: false);

            // Assert - Verify the table is created with polymorphic reference column
            sql.Should().NotBeEmpty();
            Snapshot.Match(sql);
        }

        /// <summary>
        /// Validates subclass entity with superclass identity inheritance.
        /// Tests: Superclass identity columns marked with IsSuperclassIdentity, subclass-specific properties, discriminator values.
        /// </summary>
        [Test]
        public void ValidateSubclassWithSuperclassIdentity_InheritsIdentityColumns()
        {
            // Arrange - GeneralStudentProgramAssociation (subclass) with StudentProgramAssociation (superclass)
            var schema = new ApiSchema
            {
                ProjectSchema = new ProjectSchema
                {
                    ProjectName = "EdFi",
                    ProjectVersion = "1.0.0",
                    IsExtensionProject = false,
                    ResourceSchemas = new Dictionary<string, ResourceSchema>
                    {
                        ["generalstudentprogramassociations"] = new ResourceSchema
                        {
                            ResourceName = "GeneralStudentProgramAssociation",
                            FlatteningMetadata = new FlatteningMetadata
                            {
                                Table = new TableMetadata
                                {
                                    BaseName = "GeneralStudentProgramAssociation",
                                    JsonPath = "$",
                                    DiscriminatorValue = "GeneralStudentProgramAssociation",
                                    Columns =
                                    [
                                        // Superclass identity fields (marked with isSuperclassIdentity)
                                        new ColumnMetadata
                                        {
                                            ColumnName = "StudentUniqueId",
                                            ColumnType = "string",
                                            IsNaturalKey = true,
                                            IsRequired = true,
                                            IsSuperclassIdentity = true,
                                            JsonPath = "$.studentReference.studentUniqueId",
                                            MaxLength = "32",
                                        },
                                        new ColumnMetadata
                                        {
                                            ColumnName = "ProgramEducationOrganizationId",
                                            ColumnType = "integer",
                                            IsNaturalKey = true,
                                            IsRequired = true,
                                            IsSuperclassIdentity = true,
                                            JsonPath = "$.programReference.educationOrganizationId",
                                        },
                                        new ColumnMetadata
                                        {
                                            ColumnName = "ProgramName",
                                            ColumnType = "string",
                                            IsNaturalKey = true,
                                            IsRequired = true,
                                            IsSuperclassIdentity = true,
                                            JsonPath = "$.programReference.programName",
                                            MaxLength = "60",
                                        },
                                        new ColumnMetadata
                                        {
                                            ColumnName = "ProgramTypeDescriptor",
                                            ColumnType = "descriptor",
                                            IsNaturalKey = true,
                                            IsRequired = true,
                                            IsSuperclassIdentity = true,
                                            JsonPath = "$.programReference.programTypeDescriptor",
                                        },
                                        new ColumnMetadata
                                        {
                                            ColumnName = "BeginDate",
                                            ColumnType = "date",
                                            IsNaturalKey = true,
                                            IsRequired = true,
                                            IsSuperclassIdentity = true,
                                            JsonPath = "$.beginDate",
                                        },
                                        // Subclass-specific fields
                                        new ColumnMetadata
                                        {
                                            ColumnName = "ServedOutsideOfRegularSession",
                                            ColumnType = "boolean",
                                            IsRequired = false,
                                            JsonPath = "$.servedOutsideOfRegularSession",
                                        },
                                    ],
                                    ChildTables = [],
                                },
                            },
                        },
                    },
                },
            };

            var generator = new MssqlDdlGeneratorStrategy();

            // Act
            var sql = generator.GenerateDdlString(schema, includeExtensions: false);

            // Assert
            sql.Should().NotBeEmpty();
            Snapshot.Match(sql);
        }

        /// <summary>
        /// Validates that discriminator values are properly stored in table metadata.
        /// Tests: DiscriminatorValue property handling in table definitions (prerequisite for union view generation).
        /// Note: This tests discriminator storage capability; actual union view generation is tested in other tests.
        /// </summary>
        [Test]
        public void ValidateDiscriminatorValues_StoresInTableMetadata()
        {
            // Arrange - Multiple entities with discriminator values (not necessarily in abstract hierarchy)
            var schema = new ApiSchema
            {
                ProjectSchema = new ProjectSchema
                {
                    ProjectName = "EdFi",
                    ProjectVersion = "1.0.0",
                    IsExtensionProject = false,
                    ResourceSchemas = new Dictionary<string, ResourceSchema>
                    {
                        ["assessments"] = new ResourceSchema
                        {
                            ResourceName = "Assessment",
                            FlatteningMetadata = new FlatteningMetadata
                            {
                                Table = new TableMetadata
                                {
                                    BaseName = "Assessment",
                                    JsonPath = "$",
                                    DiscriminatorValue = "Assessment",
                                    Columns =
                                    [
                                        new ColumnMetadata
                                        {
                                            ColumnName = "AssessmentIdentifier",
                                            ColumnType = "string",
                                            IsNaturalKey = true,
                                            IsRequired = true,
                                            JsonPath = "$.assessmentIdentifier",
                                            MaxLength = "60",
                                        },
                                        new ColumnMetadata
                                        {
                                            ColumnName = "Namespace",
                                            ColumnType = "string",
                                            IsNaturalKey = true,
                                            IsRequired = true,
                                            JsonPath = "$.namespace",
                                            MaxLength = "255",
                                        },
                                        new ColumnMetadata
                                        {
                                            ColumnName = "AssessmentTitle",
                                            ColumnType = "string",
                                            IsRequired = true,
                                            JsonPath = "$.assessmentTitle",
                                            MaxLength = "100",
                                        },
                                        new ColumnMetadata
                                        {
                                            ColumnName = "AssessmentCategoryDescriptor",
                                            ColumnType = "descriptor",
                                            IsRequired = true,
                                            JsonPath = "$.assessmentCategoryDescriptor",
                                        },
                                    ],
                                    ChildTables = [],
                                },
                            },
                        },
                        ["objectiveassessments"] = new ResourceSchema
                        {
                            ResourceName = "ObjectiveAssessment",
                            FlatteningMetadata = new FlatteningMetadata
                            {
                                Table = new TableMetadata
                                {
                                    BaseName = "ObjectiveAssessment",
                                    JsonPath = "$",
                                    DiscriminatorValue = "ObjectiveAssessment",
                                    Columns =
                                    [
                                        new ColumnMetadata
                                        {
                                            ColumnName = "AssessmentIdentifier",
                                            ColumnType = "string",
                                            IsNaturalKey = true,
                                            IsRequired = true,
                                            JsonPath = "$.assessmentReference.assessmentIdentifier",
                                            MaxLength = "60",
                                        },
                                        new ColumnMetadata
                                        {
                                            ColumnName = "IdentificationCode",
                                            ColumnType = "string",
                                            IsNaturalKey = true,
                                            IsRequired = true,
                                            JsonPath = "$.identificationCode",
                                            MaxLength = "60",
                                        },
                                        new ColumnMetadata
                                        {
                                            ColumnName = "Namespace",
                                            ColumnType = "string",
                                            IsNaturalKey = true,
                                            IsRequired = true,
                                            JsonPath = "$.assessmentReference.namespace",
                                            MaxLength = "255",
                                        },
                                        new ColumnMetadata
                                        {
                                            ColumnName = "Description",
                                            ColumnType = "string",
                                            IsRequired = false,
                                            JsonPath = "$.description",
                                            MaxLength = "1024",
                                        },
                                    ],
                                    ChildTables = [],
                                },
                            },
                        },
                    },
                },
            };

            var generator = new MssqlDdlGeneratorStrategy();

            // Act
            var sql = generator.GenerateDdlString(schema, includeExtensions: false);

            // Assert - Verify both tables are created with their discriminator values stored
            Snapshot.Match(sql);
        }

        /// <summary>
        /// Validates that union view names include schema prefix when UsePrefixedTableNames is enabled.
        /// Tests: Schema prefix applied to view name, consistency with prefixed table naming.
        /// </summary>
        [Test]
        public void ValidateUnionViewNaming_IncludesSchemaPrefix()
        {
            // Arrange - Create schema with abstractResources containing union view metadata
            var projectSchemaJson =
                @"{
                ""projectName"": ""EdFi"",
                ""projectVersion"": ""1.0.0"",
                ""isExtensionProject"": false,
                ""abstractResources"": {
                    ""educationOrganizations"": {
                        ""abstractResourceName"": ""EducationOrganization"",
                        ""flatteningMetadata"": {
                            ""subclassTypes"": [""schools"", ""localEducationAgencies""],
                            ""unionViewName"": ""EducationOrganization""
                        }
                    }
                },
                ""resourceSchemas"": {
                    ""schools"": {
                        ""resourceName"": ""School"",
                        ""flatteningMetadata"": {
                            ""table"": {
                                ""baseName"": ""School"",
                                ""jsonPath"": ""$"",
                                ""discriminatorValue"": ""School"",
                                ""columns"": [
                                    {
                                        ""columnName"": ""SchoolId"",
                                        ""columnType"": ""integer"",
                                        ""isNaturalKey"": true,
                                        ""isRequired"": true,
                                        ""jsonPath"": ""$.schoolId""
                                    },
                                    {
                                        ""columnName"": ""NameOfInstitution"",
                                        ""columnType"": ""string"",
                                        ""isRequired"": true,
                                        ""jsonPath"": ""$.nameOfInstitution"",
                                        ""maxLength"": ""255""
                                    }
                                ],
                                ""childTables"": []
                            }
                        }
                    },
                    ""localEducationAgencies"": {
                        ""resourceName"": ""LocalEducationAgency"",
                        ""flatteningMetadata"": {
                            ""table"": {
                                ""baseName"": ""LocalEducationAgency"",
                                ""jsonPath"": ""$"",
                                ""discriminatorValue"": ""LocalEducationAgency"",
                                ""columns"": [
                                    {
                                        ""columnName"": ""LocalEducationAgencyId"",
                                        ""columnType"": ""integer"",
                                        ""isNaturalKey"": true,
                                        ""isRequired"": true,
                                        ""jsonPath"": ""$.localEducationAgencyId""
                                    },
                                    {
                                        ""columnName"": ""NameOfInstitution"",
                                        ""columnType"": ""string"",
                                        ""isRequired"": true,
                                        ""jsonPath"": ""$.nameOfInstitution"",
                                        ""maxLength"": ""255""
                                    }
                                ],
                                ""childTables"": []
                            }
                        }
                    }
                }
            }";

            var projectSchema = System.Text.Json.JsonSerializer.Deserialize<ProjectSchema>(
                projectSchemaJson,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            );

            var schema = new ApiSchema { ProjectSchema = projectSchema! };
            var generator = new MssqlDdlGeneratorStrategy();

            // Act - Generate DDL with union views enabled and prefixed table names (default)
            var sql = generator.GenerateDdlString(schema, includeExtensions: false, skipUnionViews: false);

            // Assert - Verify the generated DDL matches the snapshot
            Snapshot.Match(sql);
        }

        /// <summary>
        /// Test: Union view for GeneralStudentProgramAssociation with multiple subclasses
        /// This tests a complex union view with many subclass types, verifying the schema prefix
        /// is applied consistently to both the view name and all referenced tables.
        /// </summary>
        [Test]
        public void WhenGeneratingComplexUnionViewWithManySubclasses_ShouldApplyPrefixToViewAndTables()
        {
            // Arrange - Create schema with GeneralStudentProgramAssociation abstract resource
            var projectSchemaJson =
                @"{
                ""projectName"": ""EdFi"",
                ""projectVersion"": ""1.0.0"",
                ""isExtensionProject"": false,
                ""abstractResources"": {
                    ""generalStudentProgramAssociations"": {
                        ""abstractResourceName"": ""GeneralStudentProgramAssociation"",
                        ""flatteningMetadata"": {
                            ""subclassTypes"": [""studentCTEProgramAssociations"", ""studentHomelessProgramAssociations""],
                            ""unionViewName"": ""GeneralStudentProgramAssociation""
                        }
                    }
                },
                ""resourceSchemas"": {
                    ""studentCTEProgramAssociations"": {
                        ""resourceName"": ""StudentCTEProgramAssociation"",
                        ""flatteningMetadata"": {
                            ""table"": {
                                ""baseName"": ""StudentCTEProgramAssociation"",
                                ""jsonPath"": ""$"",
                                ""discriminatorValue"": ""StudentCTEProgramAssociation"",
                                ""columns"": [
                                    {
                                        ""columnName"": ""StudentUniqueId"",
                                        ""columnType"": ""string"",
                                        ""isNaturalKey"": true,
                                        ""isRequired"": true,
                                        ""jsonPath"": ""$.studentReference.studentUniqueId"",
                                        ""maxLength"": ""32""
                                    },
                                    {
                                        ""columnName"": ""BeginDate"",
                                        ""columnType"": ""date"",
                                        ""isNaturalKey"": true,
                                        ""isRequired"": true,
                                        ""jsonPath"": ""$.beginDate""
                                    },
                                    {
                                        ""columnName"": ""NonTraditionalGenderStatus"",
                                        ""columnType"": ""boolean"",
                                        ""isRequired"": false,
                                        ""jsonPath"": ""$.nonTraditionalGenderStatus""
                                    }
                                ],
                                ""childTables"": []
                            }
                        }
                    },
                    ""studentHomelessProgramAssociations"": {
                        ""resourceName"": ""StudentHomelessProgramAssociation"",
                        ""flatteningMetadata"": {
                            ""table"": {
                                ""baseName"": ""StudentHomelessProgramAssociation"",
                                ""jsonPath"": ""$"",
                                ""discriminatorValue"": ""StudentHomelessProgramAssociation"",
                                ""columns"": [
                                    {
                                        ""columnName"": ""StudentUniqueId"",
                                        ""columnType"": ""string"",
                                        ""isNaturalKey"": true,
                                        ""isRequired"": true,
                                        ""jsonPath"": ""$.studentReference.studentUniqueId"",
                                        ""maxLength"": ""32""
                                    },
                                    {
                                        ""columnName"": ""BeginDate"",
                                        ""columnType"": ""date"",
                                        ""isNaturalKey"": true,
                                        ""isRequired"": true,
                                        ""jsonPath"": ""$.beginDate""
                                    },
                                    {
                                        ""columnName"": ""AwaitingFosterCare"",
                                        ""columnType"": ""boolean"",
                                        ""isRequired"": false,
                                        ""jsonPath"": ""$.awaitingFosterCare""
                                    }
                                ],
                                ""childTables"": []
                            }
                        }
                    }
                }
            }";

            var projectSchema = System.Text.Json.JsonSerializer.Deserialize<ProjectSchema>(
                projectSchemaJson,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            );

            var schema = new ApiSchema { ProjectSchema = projectSchema! };
            var generator = new MssqlDdlGeneratorStrategy();

            // Act - Generate DDL with union views
            var sql = generator.GenerateDdlString(schema, includeExtensions: false, skipUnionViews: false);

            // Assert - Verify the generated DDL matches the snapshot
            Snapshot.Match(sql);
            // Verify the view name doesn't have double schema prefix (e.g., not "dms.dms.edfi_")
            sql.Should().NotContain("dms.dms.edfi_");
        }

        /// <summary>
        /// Validates that union view generation can be disabled via the SkipUnionViews option.
        /// Tests: No CREATE VIEW statements generated, skipUnionViews parameter functionality.
        /// </summary>
        [Test]
        public void ValidateSkipUnionViewsOption_SuppressesViewGeneration()
        {
            // Arrange
            var projectSchemaJson =
                @"{
                ""projectName"": ""EdFi"",
                ""projectVersion"": ""1.0.0"",
                ""isExtensionProject"": false,
                ""abstractResources"": {
                    ""educationOrganizations"": {
                        ""abstractResourceName"": ""EducationOrganization"",
                        ""flatteningMetadata"": {
                            ""subclassTypes"": [""schools""],
                            ""unionViewName"": ""EducationOrganization""
                        }
                    }
                },
                ""resourceSchemas"": {
                    ""schools"": {
                        ""resourceName"": ""School"",
                        ""flatteningMetadata"": {
                            ""table"": {
                                ""baseName"": ""School"",
                                ""jsonPath"": ""$"",
                                ""discriminatorValue"": ""School"",
                                ""columns"": [
                                    {
                                        ""columnName"": ""SchoolId"",
                                        ""columnType"": ""integer"",
                                        ""isNaturalKey"": true,
                                        ""isRequired"": true,
                                        ""jsonPath"": ""$.schoolId""
                                    }
                                ],
                                ""childTables"": []
                            }
                        }
                    }
                }
            }";

            var projectSchema = System.Text.Json.JsonSerializer.Deserialize<ProjectSchema>(
                projectSchemaJson,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            );

            var schema = new ApiSchema { ProjectSchema = projectSchema! };
            var generator = new MssqlDdlGeneratorStrategy();

            // Act - Generate DDL with union views disabled
            var sql = generator.GenerateDdlString(schema, includeExtensions: false, skipUnionViews: true);

            // Assert - Verify no union view is created
            Snapshot.Match(sql);
        }

        /// <summary>
        /// Validates that ALL tables (root and child) have required system columns.
        /// Tests: Id, Document_Id, Document_PartitionKey, audit columns for all tables.
        /// </summary>
        [Test]
        public void ValidateAllTablesHaveRequiredColumns()
        {
            // Arrange - Schema with root table and child table
            var schema = new ApiSchema
            {
                ProjectSchema = new ProjectSchema
                {
                    ProjectName = "EdFi",
                    ProjectVersion = "1.0.0",
                    IsExtensionProject = false,
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
                                    JsonPath = "$",
                                    Columns =
                                    [
                                        new ColumnMetadata
                                        {
                                            ColumnName = "StudentUniqueId",
                                            ColumnType = "string",
                                            IsNaturalKey = true,
                                            IsRequired = true,
                                            JsonPath = "$.studentUniqueId",
                                            MaxLength = "32",
                                        },
                                    ],
                                    ChildTables =
                                    [
                                        new TableMetadata
                                        {
                                            BaseName = "StudentAddress",
                                            JsonPath = "$.addresses[*]",
                                            Columns =
                                            [
                                                new ColumnMetadata
                                                {
                                                    ColumnName = "AddressTypeDescriptorId",
                                                    ColumnType = "integer",
                                                    IsRequired = true,
                                                    IsNaturalKey = true,
                                                    JsonPath = "$.addresses[*].addressTypeDescriptor",
                                                },
                                                new ColumnMetadata
                                                {
                                                    ColumnName = "Student_Id",
                                                    ColumnType = "integer",
                                                    IsParentReference = true,
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

            var generator = new MssqlDdlGeneratorStrategy();
            var options = new DdlGenerationOptions { IncludeAuditColumns = true };

            // Act
            var sql = generator.GenerateDdlString(schema, options);

            // Assert - Root table (Student) has all required columns
            Snapshot.Match(sql);
        }
    }
}
