// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Serialization;

namespace EdFi.DataManagementService.SchemaGenerator.Abstractions
{
    /// <summary>
    /// Represents the root ApiSchema metadata structure.
    /// </summary>
    public class ApiSchema
    {
        /// <summary>
        /// ApiSchema version.
        /// </summary>
        [JsonPropertyName("apiSchemaVersion")]
        public string ApiSchemaVersion { get; set; } = string.Empty;

        /// <summary>
        /// Project schema containing resource definitions.
        /// </summary>
        [JsonPropertyName("projectSchema")]
        public ProjectSchema? ProjectSchema { get; set; }
    }

    /// <summary>
    /// Represents the project schema containing resource definitions.
    /// </summary>
    public class ProjectSchema
    {
        /// <summary>
        /// Name of the project.
        /// </summary>
        [JsonPropertyName("projectName")]
        public string ProjectName { get; set; } = string.Empty;

        /// <summary>
        /// Version of the project.
        /// </summary>
        [JsonPropertyName("projectVersion")]
        public string ProjectVersion { get; set; } = string.Empty;

        /// <summary>
        /// Indicates if this is an extension project.
        /// </summary>
        [JsonPropertyName("isExtensionProject")]
        public bool IsExtensionProject { get; set; }

        /// <summary>
        /// Description of the project.
        /// </summary>
        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// Abstract resources (polymorphic types like EducationOrganization).
        /// </summary>
        [JsonPropertyName("abstractResources")]
        public Dictionary<string, System.Text.Json.JsonElement> AbstractResources { get; set; } = [];

        /// <summary>
        /// Resource schemas keyed by resource name.
        /// </summary>
        [JsonPropertyName("resourceSchemas")]
        public Dictionary<string, ResourceSchema> ResourceSchemas { get; set; } = [];
    }

    /// <summary>
    /// Represents a resource schema with flattening metadata.
    /// </summary>
    public class ResourceSchema
    {
        /// <summary>
        /// Name of the resource.
        /// </summary>
        [JsonPropertyName("resourceName")]
        public string ResourceName { get; set; } = string.Empty;

        /// <summary>
        /// Flattening metadata for DDL generation.
        /// </summary>
        [JsonPropertyName("flatteningMetadata")]
        public FlatteningMetadata? FlatteningMetadata { get; set; }
    }

    /// <summary>
    /// Represents flattening metadata containing table structure.
    /// </summary>
    public class FlatteningMetadata
    {
        /// <summary>
        /// Root table metadata.
        /// </summary>
        [JsonPropertyName("table")]
        public TableMetadata? Table { get; set; }
    }

    /// <summary>
    /// Represents a table in the flattened schema (recursive structure).
    /// </summary>
    public class TableMetadata
    {
        /// <summary>
        /// Base name of the table.
        /// </summary>
        [JsonPropertyName("baseName")]
        public string BaseName { get; set; } = string.Empty;

        /// <summary>
        /// JSON path from document root.
        /// </summary>
        [JsonPropertyName("jsonPath")]
        public string JsonPath { get; set; } = string.Empty;

        /// <summary>
        /// Columns for this table.
        /// </summary>
        [JsonPropertyName("columns")]
        public List<ColumnMetadata> Columns { get; set; } = [];

        /// <summary>
        /// Child tables (nested collections).
        /// </summary>
        [JsonPropertyName("childTables")]
        public List<TableMetadata> ChildTables { get; set; } = [];

        /// <summary>
        /// Indicates if this is an extension table.
        /// </summary>
        [JsonPropertyName("isExtensionTable")]
        public bool IsExtensionTable { get; set; }

        /// <summary>
        /// Discriminator value for subclass resources.
        /// </summary>
        [JsonPropertyName("discriminatorValue")]
        public string? DiscriminatorValue { get; set; }
    }

    /// <summary>
    /// Represents a column in a table.
    /// </summary>
    public class ColumnMetadata
    {
        /// <summary>
        /// JSON path (relative to table's jsonPath).
        /// </summary>
        [JsonPropertyName("jsonPath")]
        public string? JsonPath { get; set; }

        /// <summary>
        /// Column name.
        /// </summary>
        [JsonPropertyName("columnName")]
        public string ColumnName { get; set; } = string.Empty;

        /// <summary>
        /// Column type (abstract type from MetaEd).
        /// </summary>
        [JsonPropertyName("columnType")]
        public string ColumnType { get; set; } = string.Empty;

        /// <summary>
        /// Max length for string columns.
        /// </summary>
        [JsonPropertyName("maxLength")]
        public string? MaxLength { get; set; }

        /// <summary>
        /// Precision for decimal columns.
        /// </summary>
        [JsonPropertyName("precision")]
        public string? Precision { get; set; }

        /// <summary>
        /// Scale for decimal columns.
        /// </summary>
        [JsonPropertyName("scale")]
        public string? Scale { get; set; }

        /// <summary>
        /// Indicates if this column is part of the natural key.
        /// </summary>
        [JsonPropertyName("isNaturalKey")]
        public bool IsNaturalKey { get; set; }

        /// <summary>
        /// Indicates if this column is required.
        /// </summary>
        [JsonPropertyName("isRequired")]
        public bool IsRequired { get; set; }

        /// <summary>
        /// Indicates if this is a foreign key to parent table.
        /// </summary>
        [JsonPropertyName("isParentReference")]
        public bool IsParentReference { get; set; }

        /// <summary>
        /// Document paths mapping key for references.
        /// </summary>
        [JsonPropertyName("fromReferencePath")]
        public string? FromReferencePath { get; set; }

        /// <summary>
        /// Indicates if this is a polymorphic reference.
        /// </summary>
        [JsonPropertyName("isPolymorphicReference")]
        public bool IsPolymorphicReference { get; set; }

        /// <summary>
        /// Polymorphic type (e.g., 'EducationOrganization').
        /// </summary>
        [JsonPropertyName("polymorphicType")]
        public string? PolymorphicType { get; set; }

        /// <summary>
        /// Indicates if this is a discriminator column.
        /// </summary>
        [JsonPropertyName("isDiscriminator")]
        public bool IsDiscriminator { get; set; }

        /// <summary>
        /// Indicates if this is a superclass identity column.
        /// </summary>
        [JsonPropertyName("isSuperclassIdentity")]
        public bool IsSuperclassIdentity { get; set; }
    }
}
