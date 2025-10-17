// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.SchemaGenerator.Abstractions
{
    /// <summary>
    /// Configuration for DDL generation including schema mappings and options.
    /// </summary>
    public class DdlGenerationOptions
    {
        /// <summary>
        /// Whether to include extensions in the DDL generation.
        /// </summary>
        public bool IncludeExtensions { get; set; } = false;

        /// <summary>
        /// Whether to skip generating union views.
        /// </summary>
        public bool SkipUnionViews { get; set; } = false;

        /// <summary>
        /// Whether to use prefixed table names instead of separate schemas.
        /// When true, generates tables as dms.{schema}_{table} instead of {schema}.{table}.
        /// </summary>
        public bool UsePrefixedTableNames { get; set; } = true;

        /// <summary>
        /// Whether to generate natural key unique constraints.
        /// </summary>
        public bool GenerateNaturalKeyConstraints { get; set; } = true;

        /// <summary>
        /// Whether to generate foreign key constraints.
        /// </summary>
        public bool GenerateForeignKeyConstraints { get; set; } = true;

        /// <summary>
        /// Whether to add standard DMS audit columns.
        /// </summary>
        public bool IncludeAuditColumns { get; set; } = true;

        /// <summary>
        /// Schema mappings for projects. Key is project name, value is database schema name.
        /// If not specified, defaults to 'dms' for all projects.
        /// </summary>
        public Dictionary<string, string> SchemaMapping { get; set; } = new()
        {
            ["EdFi"] = "edfi",
            ["Sample"] = "sample",
            ["TPDM"] = "tpdm",
            ["Extensions"] = "extensions"
        };

        /// <summary>
        /// Default schema name to use when project is not found in SchemaMapping.
        /// </summary>
        public string DefaultSchema { get; set; } = "dms";

        /// <summary>
        /// Schema name for descriptor tables.
        /// </summary>
        public string DescriptorSchema { get; set; } = "dms";

        /// <summary>
        /// Resolves the database schema name for a given project name.
        /// </summary>
        /// <param name="projectName">The project name from ApiSchema.</param>
        /// <returns>The database schema name to use.</returns>
        public string ResolveSchemaName(string? projectName)
        {
            if (UsePrefixedTableNames)
            {
                // When using prefixed table names, always use the default schema (dms)
                return DefaultSchema;
            }

            if (string.IsNullOrEmpty(projectName))
            {
                return DefaultSchema;
            }

            // Try exact match first
            if (SchemaMapping.TryGetValue(projectName, out var schema))
            {
                return schema;
            }

            // Try case-insensitive match
            var key = SchemaMapping.Keys.FirstOrDefault(k =>
                string.Equals(k, projectName, StringComparison.OrdinalIgnoreCase));

            if (key != null)
            {
                return SchemaMapping[key];
            }

            // Check if it's an extension project (contains "Extension" or ends with "Ext")
            if (projectName.Contains("Extension", StringComparison.OrdinalIgnoreCase) ||
                projectName.EndsWith("Ext", StringComparison.OrdinalIgnoreCase))
            {
                return SchemaMapping.GetValueOrDefault("Extensions", DefaultSchema);
            }

            return DefaultSchema;
        }

        /// <summary>
        /// Resolves the table name prefix for a given project name.
        /// Used when UsePrefixedTableNames is true.
        /// </summary>
        /// <param name="projectName">The project name from ApiSchema.</param>
        /// <returns>The prefix to use for table names.</returns>
        public string ResolveTablePrefix(string? projectName)
        {
            if (!UsePrefixedTableNames)
            {
                return string.Empty;
            }

            if (string.IsNullOrEmpty(projectName))
            {
                return string.Empty;
            }

            // Try exact match first
            if (SchemaMapping.TryGetValue(projectName, out var schema))
            {
                return schema + "_";
            }

            // Try case-insensitive match
            var key = SchemaMapping.Keys.FirstOrDefault(k =>
                string.Equals(k, projectName, StringComparison.OrdinalIgnoreCase));

            if (key != null)
            {
                return SchemaMapping[key] + "_";
            }

            // Check if it's an extension project (contains "Extension" or ends with "Ext")
            if (projectName.Contains("Extension", StringComparison.OrdinalIgnoreCase) ||
                projectName.EndsWith("Ext", StringComparison.OrdinalIgnoreCase))
            {
                var extensionSchema = SchemaMapping.GetValueOrDefault("Extensions", "extensions");
                return extensionSchema + "_";
            }

            // Use lowercase project name as prefix
            return projectName.ToLowerInvariant() + "_";
        }
    }
}
