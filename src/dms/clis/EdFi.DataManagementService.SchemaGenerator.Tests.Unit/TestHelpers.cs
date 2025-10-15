// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.SchemaGenerator.Abstractions;

namespace EdFi.DataManagementService.SchemaGenerator.Tests.Unit
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
                                        new ColumnMetadata { ColumnName = "Id", ColumnType = "bigint", IsNaturalKey = true, IsRequired = true },
                                        new ColumnMetadata { ColumnName = "Name", ColumnType = "string", MaxLength = "100", IsRequired = true },
                                        new ColumnMetadata { ColumnName = "IsActive", ColumnType = "bool", IsRequired = false }
                                    ],
                                    ChildTables = []
                                }
                            }
                        }
                    }
                }
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
                                            IsPolymorphicReference = true
                                        },
                                        new ColumnMetadata
                                        {
                                            ColumnName = "OrganizationType",
                                            ColumnType = "string",
                                            MaxLength = "50",
                                            IsRequired = true,
                                            IsDiscriminator = true
                                        }
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
                                                new ColumnMetadata { ColumnName = "EducationOrganizationId", ColumnType = "int32", IsNaturalKey = true, IsRequired = true },
                                                new ColumnMetadata { ColumnName = "SchoolName", ColumnType = "string", MaxLength = "100", IsRequired = true }
                                            ],
                                            ChildTables = []
                                        },
                                        new TableMetadata
                                        {
                                            BaseName = "LocalEducationAgency",
                                            JsonPath = "$.EducationOrganizationReference.LocalEducationAgency",
                                            DiscriminatorValue = "LocalEducationAgency",
                                            Columns =
                                            [
                                                new ColumnMetadata { ColumnName = "EducationOrganizationId", ColumnType = "int32", IsNaturalKey = true, IsRequired = true },
                                                new ColumnMetadata { ColumnName = "LeaName", ColumnType = "string", MaxLength = "100", IsRequired = true }
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
                    ResourceSchemas = new Dictionary<string, ResourceSchema>()
                }
            };
        }
    }
}
