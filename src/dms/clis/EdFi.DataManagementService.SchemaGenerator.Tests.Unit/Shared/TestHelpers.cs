// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.SchemaGenerator.Abstractions;

namespace EdFi.DataManagementService.SchemaGenerator.Tests.Unit.Shared
{
    /// <summary>
    /// Shared helper methods for DDL generator tests.
    /// </summary>
    public static class TestHelpers
    {
        /// <summary>
        /// Creates a basic schema with a single table for testing.
        /// </summary>
        public static ApiSchema GetBasicSchema()
        {
            return new ApiSchema
            {
                ProjectSchema = new ProjectSchema
                {
                    ProjectName = "TestProject",
                    ProjectVersion = "1.0.0",
                    IsExtensionProject = false,
                    Description = "Test schema for DDL generation.",
                    ResourceSchemas = new Dictionary<string, ResourceSchema>
                    {
                        ["TestTable"] = new ResourceSchema
                        {
                            ResourceName = "TestTable",
                            FlatteningMetadata = new FlatteningMetadata
                            {
                                Table = new TableMetadata
                                {
                                    BaseName = "TestTable",
                                    JsonPath = "$.TestTable",
                                    Columns =
                                    [
                                        new ColumnMetadata
                                        {
                                            ColumnName = "Id",
                                            ColumnType = "bigint",
                                            IsNaturalKey = true,
                                            IsRequired = true,
                                        },
                                        new ColumnMetadata
                                        {
                                            ColumnName = "Name",
                                            ColumnType = "string",
                                            MaxLength = "100",
                                            IsRequired = true,
                                        },
                                        new ColumnMetadata
                                        {
                                            ColumnName = "IsActive",
                                            ColumnType = "bool",
                                            IsRequired = false,
                                        },
                                    ],
                                    ChildTables = [],
                                },
                            },
                        },
                    },
                },
            };
        }

        /// <summary>
        /// Creates a schema with polymorphic references for testing union view generation.
        /// </summary>
        public static ApiSchema GetSchemaWithPolymorphicReference()
        {
            return new ApiSchema
            {
                ProjectSchema = new ProjectSchema
                {
                    ProjectName = "TestProject",
                    ProjectVersion = "1.0.0",
                    IsExtensionProject = false,
                    Description = "Test schema with polymorphic reference.",
                    ResourceSchemas = new Dictionary<string, ResourceSchema>
                    {
                        ["EducationOrganizationReference"] = new ResourceSchema
                        {
                            ResourceName = "EducationOrganizationReference",
                            FlatteningMetadata = new FlatteningMetadata
                            {
                                Table = new TableMetadata
                                {
                                    BaseName = "EducationOrganizationReference",
                                    JsonPath = "$.EducationOrganizationReference",
                                    Columns =
                                    [
                                        new ColumnMetadata
                                        {
                                            ColumnName = "EducationOrganizationId",
                                            ColumnType = "int32",
                                            IsNaturalKey = true,
                                            IsRequired = true,
                                            IsPolymorphicReference = true,
                                        },
                                        new ColumnMetadata
                                        {
                                            ColumnName = "OrganizationType",
                                            ColumnType = "string",
                                            MaxLength = "50",
                                            IsRequired = true,
                                            IsDiscriminator = true,
                                        },
                                    ],
                                    ChildTables =
                                    [
                                        new TableMetadata
                                        {
                                            BaseName = "School",
                                            JsonPath = "$.EducationOrganizationReference.School",
                                            DiscriminatorValue = "School",
                                            Columns =
                                            [
                                                new ColumnMetadata
                                                {
                                                    ColumnName = "EducationOrganizationId",
                                                    ColumnType = "int32",
                                                    IsNaturalKey = true,
                                                    IsRequired = true,
                                                },
                                                new ColumnMetadata
                                                {
                                                    ColumnName = "SchoolName",
                                                    ColumnType = "string",
                                                    MaxLength = "100",
                                                    IsRequired = true,
                                                },
                                            ],
                                            ChildTables = [],
                                        },
                                        new TableMetadata
                                        {
                                            BaseName = "LocalEducationAgency",
                                            JsonPath =
                                                "$.EducationOrganizationReference.LocalEducationAgency",
                                            DiscriminatorValue = "LocalEducationAgency",
                                            Columns =
                                            [
                                                new ColumnMetadata
                                                {
                                                    ColumnName = "EducationOrganizationId",
                                                    ColumnType = "int32",
                                                    IsNaturalKey = true,
                                                    IsRequired = true,
                                                },
                                                new ColumnMetadata
                                                {
                                                    ColumnName = "LeaName",
                                                    ColumnType = "string",
                                                    MaxLength = "100",
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
        }

        /// <summary>
        /// Creates an empty schema with no resource schemas for testing edge cases.
        /// </summary>
        public static ApiSchema GetEmptySchema()
        {
            return new ApiSchema
            {
                ProjectSchema = new ProjectSchema
                {
                    ProjectName = "EmptyProject",
                    ProjectVersion = "1.0.0",
                    IsExtensionProject = false,
                    Description = "Empty test schema.",
                    ResourceSchemas = [],
                },
            };
        }

        /// <summary>
        /// Creates a basic API schema for testing.
        /// </summary>
        public static ApiSchema CreateBasicApiSchema()
        {
            return new ApiSchema
            {
                ProjectSchema = new ProjectSchema
                {
                    ProjectName = "EdFi",
                    ProjectVersion = "1.0.0",
                    IsExtensionProject = false,
                    Description = "Basic test schema.",
                    ResourceSchemas = new Dictionary<string, ResourceSchema>
                    {
                        ["students"] = new ResourceSchema
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
                                        new ColumnMetadata
                                        {
                                            ColumnName = "StudentUniqueId",
                                            ColumnType = "string",
                                            MaxLength = "32",
                                            IsNaturalKey = true,
                                            IsRequired = true,
                                        },
                                        new ColumnMetadata
                                        {
                                            ColumnName = "FirstName",
                                            ColumnType = "string",
                                            MaxLength = "75",
                                            IsRequired = true,
                                        },
                                        new ColumnMetadata
                                        {
                                            ColumnName = "LastSurname",
                                            ColumnType = "string",
                                            MaxLength = "75",
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
        }

        /// <summary>
        /// Creates an API schema with extension table for testing.
        /// </summary>
        public static ApiSchema CreateApiSchemaWithExtensionTable()
        {
            return new ApiSchema
            {
                ProjectSchema = new ProjectSchema
                {
                    ProjectName = "EdFi",
                    ProjectVersion = "1.0.0",
                    IsExtensionProject = false,
                    Description = "Schema with extension table.",
                    ResourceSchemas = new Dictionary<string, ResourceSchema>
                    {
                        ["students"] = new ResourceSchema
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
                                        new ColumnMetadata
                                        {
                                            ColumnName = "StudentUniqueId",
                                            ColumnType = "string",
                                            MaxLength = "32",
                                            IsNaturalKey = true,
                                            IsRequired = true,
                                        },
                                    ],
                                    ChildTables = [],
                                },
                            },
                        },
                        ["tpdmstudentextensions"] = new ResourceSchema
                        {
                            ResourceName = "TPDMStudentExtension",
                            FlatteningMetadata = new FlatteningMetadata
                            {
                                Table = new TableMetadata
                                {
                                    BaseName = "TPDMStudentExtension",
                                    JsonPath = "$.TPDMStudentExtension",
                                    IsExtensionTable = true,
                                    Columns =
                                    [
                                        new ColumnMetadata
                                        {
                                            ColumnName = "StudentUniqueId",
                                            ColumnType = "string",
                                            MaxLength = "32",
                                            IsNaturalKey = true,
                                            IsRequired = true,
                                        },
                                        new ColumnMetadata
                                        {
                                            ColumnName = "ExtensionField",
                                            ColumnType = "string",
                                            MaxLength = "100",
                                            IsRequired = false,
                                        },
                                    ],
                                    ChildTables = [],
                                },
                            },
                        },
                    },
                },
            };
        }

        /// <summary>
        /// Creates an API schema with custom column types for testing type mapping.
        /// </summary>
        public static ApiSchema CreateApiSchemaWithCustomColumnTypes(string columnType)
        {
            return new ApiSchema
            {
                ProjectSchema = new ProjectSchema
                {
                    ProjectName = "EdFi",
                    ProjectVersion = "1.0.0",
                    IsExtensionProject = false,
                    Description = "Schema with custom column type.",
                    ResourceSchemas = new Dictionary<string, ResourceSchema>
                    {
                        ["testtable"] = new ResourceSchema
                        {
                            ResourceName = "TestTable",
                            FlatteningMetadata = new FlatteningMetadata
                            {
                                Table = new TableMetadata
                                {
                                    BaseName = "TestTable",
                                    JsonPath = "$.TestTable",
                                    Columns =
                                    [
                                        new ColumnMetadata
                                        {
                                            ColumnName = "Id",
                                            ColumnType = "bigint",
                                            IsNaturalKey = true,
                                            IsRequired = true,
                                        },
                                        new ColumnMetadata
                                        {
                                            ColumnName = "TestColumn",
                                            ColumnType = columnType,
                                            IsRequired = false,
                                        },
                                    ],
                                    ChildTables = [],
                                },
                            },
                        },
                    },
                },
            };
        }

        /// <summary>
        /// Creates an API schema with decimal column for testing precision/scale.
        /// </summary>
        public static ApiSchema CreateApiSchemaWithDecimalColumn(string? precision, string? scale)
        {
            return new ApiSchema
            {
                ProjectSchema = new ProjectSchema
                {
                    ProjectName = "EdFi",
                    ProjectVersion = "1.0.0",
                    ResourceSchemas = new Dictionary<string, ResourceSchema>
                    {
                        ["testtable"] = new ResourceSchema
                        {
                            ResourceName = "TestTable",
                            FlatteningMetadata = new FlatteningMetadata
                            {
                                Table = new TableMetadata
                                {
                                    BaseName = "TestTable",
                                    JsonPath = "$.TestTable",
                                    Columns =
                                    [
                                        new ColumnMetadata
                                        {
                                            ColumnName = "Id",
                                            ColumnType = "bigint",
                                            IsNaturalKey = true,
                                            IsRequired = true,
                                        },
                                        new ColumnMetadata
                                        {
                                            ColumnName = "DecimalColumn",
                                            ColumnType = "decimal",
                                            Precision = precision,
                                            Scale = scale,
                                            IsRequired = false,
                                        },
                                    ],
                                    ChildTables = [],
                                },
                            },
                        },
                    },
                },
            };
        }

        /// <summary>
        /// Creates an API schema with string column for testing length constraints.
        /// </summary>
        public static ApiSchema CreateApiSchemaWithStringColumn(string maxLength)
        {
            return new ApiSchema
            {
                ProjectSchema = new ProjectSchema
                {
                    ProjectName = "EdFi",
                    ProjectVersion = "1.0.0",
                    ResourceSchemas = new Dictionary<string, ResourceSchema>
                    {
                        ["testtable"] = new ResourceSchema
                        {
                            ResourceName = "TestTable",
                            FlatteningMetadata = new FlatteningMetadata
                            {
                                Table = new TableMetadata
                                {
                                    BaseName = "TestTable",
                                    JsonPath = "$.TestTable",
                                    Columns =
                                    [
                                        new ColumnMetadata
                                        {
                                            ColumnName = "Id",
                                            ColumnType = "bigint",
                                            IsNaturalKey = true,
                                            IsRequired = true,
                                        },
                                        new ColumnMetadata
                                        {
                                            ColumnName = "StringColumn",
                                            ColumnType = "string",
                                            MaxLength = maxLength,
                                            IsRequired = false,
                                        },
                                    ],
                                    ChildTables = [],
                                },
                            },
                        },
                    },
                },
            };
        }

        /// <summary>
        /// Creates an API schema with reference column for testing FK generation.
        /// </summary>
        public static ApiSchema CreateApiSchemaWithReferenceColumn()
        {
            return new ApiSchema
            {
                ProjectSchema = new ProjectSchema
                {
                    ProjectName = "EdFi",
                    ProjectVersion = "1.0.0",
                    ResourceSchemas = new Dictionary<string, ResourceSchema>
                    {
                        ["students"] = new ResourceSchema
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
                                        new ColumnMetadata
                                        {
                                            ColumnName = "StudentUniqueId",
                                            ColumnType = "string",
                                            MaxLength = "32",
                                            IsNaturalKey = true,
                                            IsRequired = true,
                                        },
                                        new ColumnMetadata
                                        {
                                            ColumnName = "SchoolId",
                                            ColumnType = "int32",
                                            FromReferencePath = "SchoolReference",
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
        }

        /// <summary>
        /// Creates an API schema with child table for testing parent FK generation.
        /// </summary>
        public static ApiSchema CreateApiSchemaWithChildTable()
        {
            return new ApiSchema
            {
                ProjectSchema = new ProjectSchema
                {
                    ProjectName = "EdFi",
                    ProjectVersion = "1.0.0",
                    ResourceSchemas = new Dictionary<string, ResourceSchema>
                    {
                        ["students"] = new ResourceSchema
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
                                        new ColumnMetadata
                                        {
                                            ColumnName = "StudentUniqueId",
                                            ColumnType = "string",
                                            MaxLength = "32",
                                            IsNaturalKey = true,
                                            IsRequired = true,
                                        },
                                    ],
                                    ChildTables =
                                    [
                                        new TableMetadata
                                        {
                                            BaseName = "StudentAddress",
                                            JsonPath = "$.Student.StudentAddress",
                                            Columns =
                                            [
                                                new ColumnMetadata
                                                {
                                                    ColumnName = "AddressTypeDescriptorId",
                                                    ColumnType = "int32",
                                                    IsNaturalKey = true,
                                                    IsRequired = true,
                                                },
                                                new ColumnMetadata
                                                {
                                                    ColumnName = "StreetNumberName",
                                                    ColumnType = "string",
                                                    MaxLength = "150",
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
        }

        /// <summary>
        /// Creates an API schema with descriptor resource for testing descriptor schema.
        /// </summary>
        public static ApiSchema CreateApiSchemaWithDescriptorResource()
        {
            return new ApiSchema
            {
                ProjectSchema = new ProjectSchema
                {
                    ProjectName = "EdFi",
                    ProjectVersion = "1.0.0",
                    ResourceSchemas = new Dictionary<string, ResourceSchema>
                    {
                        ["gradeleveldescriptors"] = new ResourceSchema
                        {
                            ResourceName = "GradeLevelDescriptor",
                            FlatteningMetadata = new FlatteningMetadata
                            {
                                Table = new TableMetadata
                                {
                                    BaseName = "GradeLevelDescriptor",
                                    JsonPath = "$.GradeLevelDescriptor",
                                    Columns =
                                    [
                                        new ColumnMetadata
                                        {
                                            ColumnName = "GradeLevelDescriptorId",
                                            ColumnType = "int32",
                                            IsNaturalKey = true,
                                            IsRequired = true,
                                        },
                                        new ColumnMetadata
                                        {
                                            ColumnName = "GradeLevel",
                                            ColumnType = "string",
                                            MaxLength = "50",
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
        }

        /// <summary>
        /// Creates an API schema with TPDM extension for testing extension project name extraction.
        /// </summary>
        public static ApiSchema CreateApiSchemaWithTPDMExtension()
        {
            return new ApiSchema
            {
                ProjectSchema = new ProjectSchema
                {
                    ProjectName = "TPDM",
                    ProjectVersion = "1.0.0",
                    ResourceSchemas = new Dictionary<string, ResourceSchema>
                    {
                        ["tpdmstudentextensions"] = new ResourceSchema
                        {
                            ResourceName = "TPDMStudentExtension",
                            FlatteningMetadata = new FlatteningMetadata
                            {
                                Table = new TableMetadata
                                {
                                    BaseName = "TPDMStudentExtension",
                                    JsonPath = "$.TPDMStudentExtension",
                                    IsExtensionTable = true,
                                    Columns =
                                    [
                                        new ColumnMetadata
                                        {
                                            ColumnName = "StudentUniqueId",
                                            ColumnType = "string",
                                            MaxLength = "32",
                                            IsNaturalKey = true,
                                            IsRequired = true,
                                        },
                                        new ColumnMetadata
                                        {
                                            ColumnName = "ExtensionField",
                                            ColumnType = "string",
                                            MaxLength = "100",
                                            IsRequired = false,
                                        },
                                    ],
                                    ChildTables = [],
                                },
                            },
                        },
                    },
                },
            };
        }

        /// <summary>
        /// Creates an API schema with Sample extension for testing case-insensitive schema mapping.
        /// </summary>
        public static ApiSchema CreateApiSchemaWithSampleExtension()
        {
            return new ApiSchema
            {
                ProjectSchema = new ProjectSchema
                {
                    ProjectName = "Sample",
                    ProjectVersion = "1.0.0",
                    ResourceSchemas = new Dictionary<string, ResourceSchema>
                    {
                        ["samplestudentextensions"] = new ResourceSchema
                        {
                            ResourceName = "SampleStudentExtension",
                            FlatteningMetadata = new FlatteningMetadata
                            {
                                Table = new TableMetadata
                                {
                                    BaseName = "SampleStudentExtension",
                                    JsonPath = "$.SampleStudentExtension",
                                    IsExtensionTable = true,
                                    Columns =
                                    [
                                        new ColumnMetadata
                                        {
                                            ColumnName = "StudentUniqueId",
                                            ColumnType = "string",
                                            MaxLength = "32",
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
        }

        /// <summary>
        /// Creates a basic resource schema for testing.
        /// </summary>
        public static ResourceSchema CreateBasicResourceSchema()
        {
            return new ResourceSchema
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
                            new ColumnMetadata
                            {
                                ColumnName = "StudentUniqueId",
                                ColumnType = "string",
                                MaxLength = "32",
                                IsNaturalKey = true,
                                IsRequired = true,
                            },
                        ],
                        ChildTables = [],
                    },
                },
            };
        }

        /// <summary>
        /// Creates an API schema with non-reference column for testing reference path handling.
        /// </summary>
        public static ApiSchema CreateApiSchemaWithNonReferenceColumn()
        {
            return new ApiSchema
            {
                ProjectSchema = new ProjectSchema
                {
                    ProjectName = "EdFi",
                    ProjectVersion = "1.0.0",
                    ResourceSchemas = new Dictionary<string, ResourceSchema>
                    {
                        ["students"] = new ResourceSchema
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
                                        new ColumnMetadata
                                        {
                                            ColumnName = "StudentUniqueId",
                                            ColumnType = "string",
                                            MaxLength = "32",
                                            IsNaturalKey = true,
                                            IsRequired = true,
                                        },
                                        new ColumnMetadata
                                        {
                                            ColumnName = "SchoolId",
                                            ColumnType = "int32",
                                            FromReferencePath = "School",
                                            IsRequired = true,
                                        }, // No "Reference" suffix
                                    ],
                                    ChildTables = [],
                                },
                            },
                        },
                    },
                },
            };
        }

        /// <summary>
        /// Creates an API schema with empty reference column for testing edge cases.
        /// </summary>
        public static ApiSchema CreateApiSchemaWithEmptyReferenceColumn()
        {
            return new ApiSchema
            {
                ProjectSchema = new ProjectSchema
                {
                    ProjectName = "EdFi",
                    ProjectVersion = "1.0.0",
                    ResourceSchemas = new Dictionary<string, ResourceSchema>
                    {
                        ["students"] = new ResourceSchema
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
                                        new ColumnMetadata
                                        {
                                            ColumnName = "StudentUniqueId",
                                            ColumnType = "string",
                                            MaxLength = "32",
                                            IsNaturalKey = true,
                                            IsRequired = true,
                                        },
                                        new ColumnMetadata
                                        {
                                            ColumnName = "EmptyRefId",
                                            ColumnType = "int32",
                                            FromReferencePath = "",
                                            IsRequired = true,
                                        }, // Empty reference path
                                    ],
                                    ChildTables = [],
                                },
                            },
                        },
                    },
                },
            };
        }

        /// <summary>
        /// Creates an API schema with polymorphic reference for testing union view generation.
        /// </summary>
        public static ApiSchema CreateApiSchemaWithPolymorphicReference()
        {
            return new ApiSchema
            {
                ProjectSchema = new ProjectSchema
                {
                    ProjectName = "EdFi",
                    ProjectVersion = "1.0.0",
                    ResourceSchemas = new Dictionary<string, ResourceSchema>
                    {
                        ["educationorganizations"] = new ResourceSchema
                        {
                            ResourceName = "EducationOrganization",
                            FlatteningMetadata = new FlatteningMetadata
                            {
                                Table = new TableMetadata
                                {
                                    BaseName = "EducationOrganization",
                                    JsonPath = "$.EducationOrganization",
                                    Columns =
                                    [
                                        new ColumnMetadata
                                        {
                                            ColumnName = "EducationOrganizationId",
                                            ColumnType = "int32",
                                            IsNaturalKey = true,
                                            IsRequired = true,
                                            IsPolymorphicReference = true,
                                        },
                                        new ColumnMetadata
                                        {
                                            ColumnName = "Discriminator",
                                            ColumnType = "string",
                                            MaxLength = "50",
                                            IsRequired = true,
                                            IsDiscriminator = true,
                                        },
                                    ],
                                    ChildTables =
                                    [
                                        new TableMetadata
                                        {
                                            BaseName = "School",
                                            JsonPath = "$.EducationOrganization.School",
                                            DiscriminatorValue = "School",
                                            Columns =
                                            [
                                                new ColumnMetadata
                                                {
                                                    ColumnName = "EducationOrganizationId",
                                                    ColumnType = "int32",
                                                    IsNaturalKey = true,
                                                    IsRequired = true,
                                                },
                                                new ColumnMetadata
                                                {
                                                    ColumnName = "SchoolName",
                                                    ColumnType = "string",
                                                    MaxLength = "100",
                                                    IsRequired = true,
                                                },
                                            ],
                                            ChildTables = [],
                                        },
                                        new TableMetadata
                                        {
                                            BaseName = "LocalEducationAgency",
                                            JsonPath = "$.EducationOrganization.LocalEducationAgency",
                                            DiscriminatorValue = "LocalEducationAgency",
                                            Columns =
                                            [
                                                new ColumnMetadata
                                                {
                                                    ColumnName = "EducationOrganizationId",
                                                    ColumnType = "int32",
                                                    IsNaturalKey = true,
                                                    IsRequired = true,
                                                },
                                                new ColumnMetadata
                                                {
                                                    ColumnName = "LeaName",
                                                    ColumnType = "string",
                                                    MaxLength = "100",
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
        }

        /// <summary>
        /// Creates an API schema with abstract resources and subclasses for testing union view generation.
        /// </summary>
        public static ApiSchema CreateApiSchemaWithAbstractResource()
        {
            return new ApiSchema
            {
                ProjectSchema = new ProjectSchema
                {
                    ProjectName = "EdFi",
                    ProjectVersion = "1.0.0",
                    IsExtensionProject = false,
                    Description = "Test schema with abstract resources.",
                    ResourceSchemas = new Dictionary<string, ResourceSchema>
                    {
                        ["EducationOrganization"] = new ResourceSchema
                        {
                            ResourceName = "EducationOrganization",
                            FlatteningMetadata = new FlatteningMetadata
                            {
                                Table = new TableMetadata
                                {
                                    BaseName = "EducationOrganization",
                                    JsonPath = "$.EducationOrganization",
                                    Columns =
                                    [
                                        new ColumnMetadata
                                        {
                                            ColumnName = "EducationOrganizationId",
                                            ColumnType = "int32",
                                            IsNaturalKey = true,
                                            IsRequired = true,
                                        },
                                        new ColumnMetadata
                                        {
                                            ColumnName = "Discriminator",
                                            ColumnType = "string",
                                            MaxLength = "50",
                                            IsRequired = true,
                                        },
                                    ],
                                    ChildTables =
                                    [
                                        new TableMetadata
                                        {
                                            BaseName = "School",
                                            JsonPath = "$.EducationOrganization.School",
                                            DiscriminatorValue = "School",
                                            Columns =
                                            [
                                                new ColumnMetadata
                                                {
                                                    ColumnName = "EducationOrganizationId",
                                                    ColumnType = "int32",
                                                    IsNaturalKey = true,
                                                    IsRequired = true,
                                                },
                                                new ColumnMetadata
                                                {
                                                    ColumnName = "SchoolName",
                                                    ColumnType = "string",
                                                    MaxLength = "100",
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
        }

        /// <summary>
        /// Gets a schema with a descriptor resource.
        /// </summary>
        public static ApiSchema GetSchemaWithDescriptor()
        {
            return CreateApiSchemaWithDescriptorResource();
        }

        /// <summary>
        /// Gets a schema with an extension resource.
        /// </summary>
        public static ApiSchema GetSchemaWithExtension()
        {
            return CreateApiSchemaWithTPDMExtension();
        }

        /// <summary>
        /// Gets a schema with child tables.
        /// </summary>
        public static ApiSchema GetSchemaWithChildTables()
        {
            return CreateApiSchemaWithChildTable();
        }

        /// <summary>
        /// Gets a schema with cross-resource references.
        /// </summary>
        public static ApiSchema GetSchemaWithCrossResourceReferences()
        {
            return CreateApiSchemaWithReferenceColumn();
        }

        /// <summary>
        /// Gets a schema with natural key columns.
        /// </summary>
        public static ApiSchema GetSchemaWithNaturalKey()
        {
            return GetBasicSchema();
        }

        /// <summary>
        /// Gets a schema with multiple identity columns.
        /// </summary>
        public static ApiSchema GetSchemaWithMultipleIdentityColumns()
        {
            return new ApiSchema
            {
                ProjectSchema = new ProjectSchema
                {
                    ProjectName = "TestProject",
                    ProjectVersion = "1.0.0",
                    ResourceSchemas = new Dictionary<string, ResourceSchema>
                    {
                        ["TestTable"] = new ResourceSchema
                        {
                            ResourceName = "TestTable",
                            FlatteningMetadata = new FlatteningMetadata
                            {
                                Table = new TableMetadata
                                {
                                    BaseName = "TestTable",
                                    JsonPath = "$.TestTable",
                                    Columns =
                                    [
                                        new ColumnMetadata
                                        {
                                            ColumnName = "Id1",
                                            ColumnType = "bigint",
                                            IsNaturalKey = true,
                                            IsRequired = true,
                                        },
                                        new ColumnMetadata
                                        {
                                            ColumnName = "Id2",
                                            ColumnType = "bigint",
                                            IsNaturalKey = true,
                                            IsRequired = true,
                                        },
                                        new ColumnMetadata
                                        {
                                            ColumnName = "Id3",
                                            ColumnType = "bigint",
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
        }
    }
}
