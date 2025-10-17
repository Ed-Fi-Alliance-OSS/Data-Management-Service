// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using EdFi.DataManagementService.SchemaGenerator.Abstractions;
using HandlebarsDotNet;

namespace EdFi.DataManagementService.SchemaGenerator.Pgsql
{
    /// <summary>
    /// PostgreSQL DDL generation strategy implementation.
    /// </summary>
    public class PgsqlDdlGeneratorStrategy : IDdlGeneratorStrategy
    {
        /// <summary>
        /// Generates PostgreSQL DDL scripts for the given ApiSchema metadata.
        /// </summary>
        /// <param name="apiSchema">The deserialized ApiSchema metadata object.</param>
        /// <param name="outputDirectory">The directory to write output scripts to.</param>
        /// <param name="includeExtensions">Whether to include extensions in the DDL.</param>
        public void GenerateDdl(ApiSchema apiSchema, string outputDirectory, bool includeExtensions, bool skipUnionViews = false)
        {
            Directory.CreateDirectory(outputDirectory);

            var ddl = GenerateDdlString(apiSchema, includeExtensions, skipUnionViews);

            File.WriteAllText(Path.Combine(outputDirectory, "EdFi-DMS-Database-Schema-PostgreSQL.sql"), ddl);
        }

        /// <summary>
        /// Generates PostgreSQL DDL scripts with advanced options.
        /// </summary>
        /// <param name="apiSchema">The deserialized ApiSchema metadata object.</param>
        /// <param name="outputDirectory">The directory to write output scripts to.</param>
        /// <param name="options">DDL generation options including schema mappings and feature flags.</param>
        public void GenerateDdl(ApiSchema apiSchema, string outputDirectory, DdlGenerationOptions options)
        {
            Directory.CreateDirectory(outputDirectory);

            var ddl = GenerateDdlString(apiSchema, options);

            File.WriteAllText(Path.Combine(outputDirectory, "EdFi-DMS-Database-Schema-PostgreSQL.sql"), ddl);
        }

        /// <summary>
        /// Generates PostgreSQL DDL scripts as a string with advanced options.
        /// </summary>
        /// <param name="apiSchema">The deserialized ApiSchema metadata object.</param>
        /// <param name="options">DDL generation options including schema mappings and feature flags.</param>
        /// <returns>The DDL script.</returns>
        public string GenerateDdlString(ApiSchema apiSchema, DdlGenerationOptions options)
        {
            if (apiSchema.ProjectSchema == null || apiSchema.ProjectSchema.ResourceSchemas == null)
            {
                throw new InvalidDataException("ApiSchema does not contain valid projectSchema.");
            }

            return GenerateDdlStringInternal(apiSchema, options);
        }

        /// <summary>
        /// Generates PostgreSQL DDL scripts as a string (useful for testing - legacy method).
        /// </summary>
        /// <param name="apiSchema">The deserialized ApiSchema metadata object.</param>
        /// <param name="includeExtensions">Whether to include extensions in the DDL.</param>
        /// <param name="skipUnionViews">Whether to skip generating union views.</param>
        /// <returns>The DDL script.</returns>
        public string GenerateDdlString(ApiSchema apiSchema, bool includeExtensions, bool skipUnionViews = false)
        {
            if (apiSchema.ProjectSchema == null || apiSchema.ProjectSchema.ResourceSchemas == null)
            {
                throw new InvalidDataException("ApiSchema does not contain valid projectSchema.");
            }

            var options = new DdlGenerationOptions
            {
                IncludeExtensions = includeExtensions,
                SkipUnionViews = skipUnionViews
            };

            return GenerateDdlStringInternal(apiSchema, options);
        }

        private string GenerateDdlStringInternal(ApiSchema apiSchema, DdlGenerationOptions options)
        {
            if (apiSchema.ProjectSchema == null || apiSchema.ProjectSchema.ResourceSchemas == null)
            {
                throw new InvalidDataException("ApiSchema does not contain valid projectSchema.");
            }

            // Load Handlebars template
            var templatePath = Path.Combine(AppContext.BaseDirectory, "Templates", "pgsql-table-idempotent.hbs");
            if (!File.Exists(templatePath))
            {
                templatePath = Path.Combine(Directory.GetCurrentDirectory(), "Templates", "pgsql-table-idempotent.hbs");
            }

            var templateContent = File.ReadAllText(templatePath);
            var template = Handlebars.Compile(templateContent);

            var sb = new StringBuilder();

            // Generate schema creation statements for all unique schemas
            var usedSchemas = new HashSet<string>();
            foreach (var kvp in apiSchema.ProjectSchema.ResourceSchemas ?? [])
            {
                var resourceSchema = kvp.Value;
                if (resourceSchema.FlatteningMetadata?.Table != null)
                {
                    // Skip extensions if not requested
                    if (!options.IncludeExtensions && resourceSchema.FlatteningMetadata.Table.IsExtensionTable)
                    {
                        continue;
                    }

                    var schemaName = DetermineSchemaName(apiSchema.ProjectSchema, resourceSchema, options);
                    usedSchemas.Add(schemaName);
                }
            }

            // Generate CREATE SCHEMA statements
            foreach (var schema in usedSchemas.OrderBy(s => s))
            {
                sb.AppendLine($"CREATE SCHEMA IF NOT EXISTS {schema};");
            }

            if (usedSchemas.Any())
            {
                sb.AppendLine();
            }

            // Process each resource schema
            foreach (var kvp in apiSchema.ProjectSchema.ResourceSchemas ?? [])
            {
                var resourceName = kvp.Key;
                var resourceSchema = kvp.Value;

                if (resourceSchema.FlatteningMetadata?.Table == null)
                {
                    continue; // Skip resources without flattening metadata
                }

                // Skip extensions if not requested
                if (!options.IncludeExtensions && resourceSchema.FlatteningMetadata.Table.IsExtensionTable)
                {
                    continue;
                }

            {
                var originalSchemaName = GetOriginalSchemaName(apiSchema.ProjectSchema, resourceSchema, options);

                // Recursively generate DDL for root table and all child tables
                GenerateTableDdl(resourceSchema.FlatteningMetadata.Table, template, sb, null, null, originalSchemaName, options, resourceSchema);
            }
            }

            // Generate union views for abstract resources unless skipped
            if (!options.SkipUnionViews && apiSchema.ProjectSchema != null)
            {
                var unionViewTemplatePath = Path.Combine(AppContext.BaseDirectory, "Templates", "pgsql-union-view.hbs");
                if (!File.Exists(unionViewTemplatePath))
                {
                    unionViewTemplatePath = Path.Combine(Directory.GetCurrentDirectory(), "Templates", "pgsql-union-view.hbs");
                }
                var unionViewTemplateContent = File.ReadAllText(unionViewTemplatePath);
                var unionViewTemplate = Handlebars.Compile(unionViewTemplateContent);

                // Approach 1: Look for abstractResources in the project schema
                var projectSchemaNode = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(apiSchema.ProjectSchema)).RootElement;
                if (projectSchemaNode.TryGetProperty("abstractResources", out var abstractResourcesNode))
                {
                    foreach (var abstractResourceProp in abstractResourcesNode.EnumerateObject())
                    {
                        var abstractResource = abstractResourceProp.Value;
                        if (abstractResource.TryGetProperty("flatteningMetadata", out var flatteningMetadata) &&
                            flatteningMetadata.TryGetProperty("subclassTypes", out var subclassTypesNode) &&
                            flatteningMetadata.TryGetProperty("unionViewName", out var unionViewNameNode))
                        {
                            var subclassTypes = new List<string>();
                            foreach (var subclass in subclassTypesNode.EnumerateArray())
                            {
                                subclassTypes.Add(subclass.GetString() ?? "");
                            }

                            var unionViewName = unionViewNameNode.GetString() ?? "";

                            // Build select statements for each subclass
                            var selectStatements = new List<string>();
                            foreach (var subclassType in subclassTypes)
                            {
                                // Find the resource schema for this subclass
                                if (apiSchema.ProjectSchema.ResourceSchemas?.TryGetValue(subclassType.ToLowerInvariant() + "s", out var subclassSchema) == true &&
                                    subclassSchema.FlatteningMetadata?.Table != null)
                                {
                                    var table = subclassSchema.FlatteningMetadata.Table;
                                    // Build SELECT statement for this table - include ALL columns per specification
                                    var columns = new List<string> { "Id" }; // Start with surrogate key
                                    columns.AddRange(table.Columns.Select(c => PgsqlNamingHelper.MakePgsqlIdentifier(c.ColumnName)));

                                    var discriminator = table.DiscriminatorValue ?? subclassType;
                                    var selectCols = string.Join(", ", columns);
                                    var tableRef = BuildTableReference(table.BaseName, apiSchema.ProjectSchema, subclassSchema, options);
                                    selectStatements.Add($"SELECT {selectCols}, '{discriminator}' AS Discriminator, Document_Id, Document_PartitionKey FROM {tableRef}");
                                }
                            }
                            var viewData = new { viewName = unionViewName, selectStatements };
                            sb.AppendLine(unionViewTemplate(viewData));
                        }
                    }
                }

                // Approach 2: Detect polymorphic references from child tables with discriminatorValue
                foreach (var resourceSchema in (apiSchema.ProjectSchema.ResourceSchemas ?? []).Values)
                {
                    if (resourceSchema.FlatteningMetadata?.Table != null)
                    {
                        var table = resourceSchema.FlatteningMetadata.Table;

                        // Check if this table has polymorphic reference indicators
                        bool hasPolymorphicRef = table.Columns.Any(c => c.IsPolymorphicReference);
                        bool hasDiscriminator = table.Columns.Any(c => c.IsDiscriminator);
                        bool hasChildTablesWithDiscriminatorValues = table.ChildTables.Any(ct => !string.IsNullOrEmpty(ct.DiscriminatorValue));

                        if (hasPolymorphicRef && hasDiscriminator && hasChildTablesWithDiscriminatorValues)
                        {
                            // Generate union view for this polymorphic reference
                            var viewName = PgsqlNamingHelper.MakePgsqlIdentifier(table.BaseName);
                            var selectStatements = new List<string>();

                            // Get the natural key columns from the parent table (these should be common across all child tables)
                            var naturalKeyColumns = table.Columns
                                .Where(c => c.IsNaturalKey)
                                .Select(c => PgsqlNamingHelper.MakePgsqlIdentifier(c.ColumnName))
                                .ToList();

                            foreach (var childTable in table.ChildTables.Where(ct => !string.IsNullOrEmpty(ct.DiscriminatorValue)))
                            {
                                // Include ALL columns per specification: Id + all data columns + Document columns
                                var columns = new List<string> { "Id" }; // Start with surrogate key
                                columns.AddRange(childTable.Columns.Select(c => PgsqlNamingHelper.MakePgsqlIdentifier(c.ColumnName)));

                                var selectCols = string.Join(", ", columns);
                                var discriminatorValue = childTable.DiscriminatorValue;
                                var tableRef = BuildTableReference(childTable.BaseName, apiSchema.ProjectSchema, resourceSchema, options);
                                selectStatements.Add($"SELECT {selectCols}, '{discriminatorValue}' AS Discriminator, Document_Id, Document_PartitionKey FROM {tableRef}");
                            }

                            if (selectStatements.Any())
                            {
                                var viewData = new { viewName, selectStatements };
                                sb.AppendLine(unionViewTemplate(viewData));
                            }
                        }
                    }
                }
            }

            return sb.ToString();
        }

        private void GenerateTableDdl(
            TableMetadata table,
            HandlebarsTemplate<object, object> template,
            StringBuilder sb,
            string? parentTableName,
            List<string>? parentPkColumns,
            string originalSchemaName,
            DdlGenerationOptions options,
            ResourceSchema? resourceSchema = null)
        {
            var tableName = DetermineTableName(table.BaseName, originalSchemaName, resourceSchema, options);

            // For extension resources and descriptor resources using separate schemas, use the original schema as final schema
            var finalSchemaName = ShouldUseSeparateSchema(originalSchemaName, options, resourceSchema)
                ? originalSchemaName
                : options.ResolveSchemaName(null);

            var isRootTable = parentTableName == null;

            // Track cross-resource references for FK and index generation
            var crossResourceReferences = new List<(string columnName, string referencedResource)>();

            // Generate data columns (excluding parent references and system columns)
            var columns = table.Columns
                .Where(c => !c.IsParentReference) // Parent FKs handled separately
                .Select(c =>
                {
                    // Track cross-resource references
                    if (!string.IsNullOrEmpty(c.FromReferencePath) && c.ColumnName.EndsWith("Id"))
                    {
                        var referencedResource = ResolveResourceNameFromPath(c.FromReferencePath);
                        if (!string.IsNullOrEmpty(referencedResource))
                        {
                            crossResourceReferences.Add((c.ColumnName, referencedResource));
                        }
                    }

                    return new
                    {
                        name = PgsqlNamingHelper.MakePgsqlIdentifier(c.ColumnName),
                        type = MapColumnType(c),
                        isRequired = c.IsRequired
                    };
                })
                .ToList();

            // Natural key columns for unique constraint
            var naturalKeyColumns = table.Columns
                .Where(c => c.IsNaturalKey && !c.IsParentReference)
                .Select(c => PgsqlNamingHelper.MakePgsqlIdentifier(c.ColumnName))
                .ToList();

            // Foreign key constraints
            var fkColumns = new List<object>();

            // 1. FK to parent table (for child tables)
            if (parentTableName != null)
            {
                var parentFkColumn = PgsqlNamingHelper.MakePgsqlIdentifier($"{parentTableName}_Id");
                columns.Insert(0, new
                {
                    name = parentFkColumn,
                    type = "BIGINT",
                    isRequired = true
                });

                fkColumns.Add(new
                {
                    constraintName = $"FK_{table.BaseName}_{parentTableName}",
                    column = parentFkColumn,
                    parentTable = $"{finalSchemaName}.{DetermineTableName(parentTableName, originalSchemaName, resourceSchema, options)}",
                    parentColumn = "Id", // Always reference surrogate key
                    cascade = true
                });
            }

            // 2. FK to cross-resource references
            foreach (var (columnName, referencedResource) in crossResourceReferences)
            {
                fkColumns.Add(new
                {
                    constraintName = $"FK_{table.BaseName}_{referencedResource}",
                    column = PgsqlNamingHelper.MakePgsqlIdentifier(columnName),
                    parentTable = $"{finalSchemaName}.{DetermineTableName(referencedResource, originalSchemaName, null, options)}",
                    parentColumn = "Id",
                    cascade = false // Use RESTRICT for cross-resource FKs to prevent accidental data loss
                });
            }

            // 3. FK to Document table (all tables)
            fkColumns.Add(new
            {
                constraintName = $"FK_{table.BaseName}_Document",
                column = "(Document_Id, Document_PartitionKey)",
                parentTable = $"{finalSchemaName}.Document",
                parentColumn = "(Id, DocumentPartitionKey)",
                cascade = true
            });

            // Unique constraints
            var uniqueConstraints = new List<object>();

            // For root tables: Natural key uniqueness
            if (isRootTable && naturalKeyColumns.Count > 0)
            {
                uniqueConstraints.Add(new
                {
                    constraintName = $"UQ_{table.BaseName}_NaturalKey",
                    columns = naturalKeyColumns
                });
            }

            // For child tables: Parent FK + natural key columns
            if (!isRootTable)
            {
                var identityColumns = new List<string>
                {
                    PgsqlNamingHelper.MakePgsqlIdentifier($"{parentTableName}_Id")
                };
                identityColumns.AddRange(naturalKeyColumns);

                if (identityColumns.Count > 1) // Only if there are identifying columns beyond parent FK
                {
                    uniqueConstraints.Add(new
                    {
                        constraintName = $"UQ_{table.BaseName}_Identity",
                        columns = identityColumns
                    });
                }
            }

            // Index generation for foreign keys (critical for performance)
            var indexes = new List<object>();

            // 1. Index on parent FK (for child tables)
            if (parentTableName != null)
            {
                indexes.Add(new
                {
                    indexName = $"IX_{table.BaseName}_{parentTableName}",
                    tableName,
                    columns = new[] { PgsqlNamingHelper.MakePgsqlIdentifier($"{parentTableName}_Id") }
                });
            }

            // 2. Indexes on cross-resource FKs
            foreach (var (columnName, referencedResource) in crossResourceReferences)
            {
                indexes.Add(new
                {
                    indexName = $"IX_{table.BaseName}_{referencedResource}",
                    tableName,
                    columns = new[] { PgsqlNamingHelper.MakePgsqlIdentifier(columnName) }
                });
            }

            // 3. Index on Document FK (composite index for performance)
            indexes.Add(new
            {
                indexName = $"IX_{table.BaseName}_Document",
                tableName,
                columns = new[] { "Document_Id", "Document_PartitionKey" }
            });

            // Add audit columns if requested
            var allColumns = new List<object>(columns);
            if (options.IncludeAuditColumns)
            {
                allColumns.AddRange(new[]
                {
                    new { name = "CreateDate", type = "TIMESTAMP", isRequired = true },
                    new { name = "LastModifiedDate", type = "TIMESTAMP", isRequired = true },
                    new { name = "ChangeVersion", type = "BIGINT", isRequired = true }
                });
            }

            var data = new
            {
                schemaName = finalSchemaName,
                tableName,
                hasId = true,
                id = "Id",
                hasDocumentColumns = true,
                documentId = "Document_Id",
                documentPartitionKey = "Document_PartitionKey",
                columns = allColumns,
                fkColumns = options.GenerateForeignKeyConstraints ? fkColumns : [],
                uniqueConstraints = options.GenerateNaturalKeyConstraints ? uniqueConstraints : [],
                indexes
            };

            sb.AppendLine(template(data));

            // Recursively process child tables
            foreach (var childTable in table.ChildTables)
            {
                GenerateTableDdl(childTable, template, sb, table.BaseName, null, originalSchemaName, options, resourceSchema);
            }
        }

        /// <summary>
        /// Maps column metadata to PostgreSQL data types according to the specification.
        /// </summary>
        private string MapColumnType(ColumnMetadata column)
        {
            if (string.IsNullOrEmpty(column.ColumnType))
            {
                return "TEXT";
            }

            var baseType = column.ColumnType.ToLower() switch
            {
                // Numeric types
                "int64" or "bigint" => "BIGINT",
                "int32" or "integer" or "int" => "INTEGER",
                "int16" or "short" => "SMALLINT",

                // String types - use VARCHAR with length or TEXT for unlimited
                "string" => MapStringType(column),

                // Boolean type
                "boolean" or "bool" => "BOOLEAN",

                // Date/time types
                "date" => "DATE",
                "datetime" => "TIMESTAMP WITH TIME ZONE", // Per spec preference
                "time" => "TIME",

                // Decimal types with precision/scale support
                "decimal" => MapDecimalType(column),

                // Special Ed-Fi types
                "currency" => "MONEY",
                "percent" => "DECIMAL(5, 4)",
                "year" => "SMALLINT",
                "duration" => "VARCHAR(30)",

                // Descriptor type - FK to unified descriptor table
                "descriptor" => "BIGINT", // FK to dms.Descriptor table

                // GUID type
                "guid" or "uuid" => "UUID",

                // Default fallback
                _ => "TEXT",
            };

            return baseType;
        }

        /// <summary>
        /// Maps string column metadata to PostgreSQL VARCHAR or TEXT types with proper length constraints.
        /// </summary>
        private static string MapStringType(ColumnMetadata column)
        {
            if (!string.IsNullOrEmpty(column.MaxLength))
            {
                return $"VARCHAR({column.MaxLength})";
            }

            return "TEXT"; // Fallback for unlimited length
        }

        /// <summary>
        /// Maps decimal column metadata to PostgreSQL DECIMAL types with precision and scale from MetaEd metadata.
        /// </summary>
        private static string MapDecimalType(ColumnMetadata column)
        {
            // Use precision and scale from MetaEd metadata if available
            if (!string.IsNullOrEmpty(column.Precision))
            {
                var precision = column.Precision;
                var scale = !string.IsNullOrEmpty(column.Scale) ? column.Scale : "0";
                return $"DECIMAL({precision}, {scale})";
            }

            // If only scale is provided (edge case), use a reasonable default precision
            if (!string.IsNullOrEmpty(column.Scale))
            {
                var scale = column.Scale;
                var precision = int.Parse(scale) + 10; // Default: scale + 10 for precision
                return $"DECIMAL({precision}, {scale})";
            }

            return "DECIMAL"; // Fallback to generic decimal without constraints
        }

        /// <summary>
        /// Resolves the target resource name from a reference path.
        /// For example: "StudentReference" -> "Student", "SchoolReference" -> "School"
        /// </summary>
        private string ResolveResourceNameFromPath(string referencePath)
        {
            if (string.IsNullOrEmpty(referencePath))
            {
                return string.Empty;
            }

            // Remove "Reference" suffix if present
            if (referencePath.EndsWith("Reference", StringComparison.OrdinalIgnoreCase))
            {
                return referencePath.Substring(0, referencePath.Length - "Reference".Length);
            }

            // Otherwise, assume the path itself is the resource name
            return referencePath;
        }

        /// <summary>
        /// Determines the original schema name for a resource without "dms" resolution.
        /// This is used for table name prefixing when UsePrefixedTableNames is true.
        /// </summary>
        /// <param name="projectSchema">The project schema containing the resource.</param>
        /// <param name="resourceSchema">The resource schema being processed.</param>
        /// <param name="options">DDL generation options containing schema mappings.</param>
        /// <returns>The original database schema name (before dms resolution).</returns>
        private string GetOriginalSchemaName(ProjectSchema projectSchema, ResourceSchema resourceSchema, DdlGenerationOptions options)
        {
            // Handle descriptor resources
            if (IsDescriptorResource(resourceSchema))
            {
                // If descriptor schema is different from default schema, don't use prefixed table names
                if (options.DescriptorSchema != options.DefaultSchema)
                {
                    // Return a special marker that will be handled in DetermineTableName
                    return options.DescriptorSchema;
                }
                return "descriptors";
            }

            // Handle extension resources - they use extension schema mapping
            if (resourceSchema.FlatteningMetadata?.Table?.IsExtensionTable == true)
            {
                // Try to extract the extension project name from resource
                var extensionProject = ExtractExtensionProjectName(resourceSchema.ResourceName);
                if (!string.IsNullOrEmpty(extensionProject))
                {
                    // Get the raw schema mapping without dms resolution
                    if (options.SchemaMapping.TryGetValue(extensionProject, out var schema))
                    {
                        return schema;
                    }
                    // Try case-insensitive match
                    var match = options.SchemaMapping.FirstOrDefault(kvp =>
                        string.Equals(kvp.Key, extensionProject, StringComparison.OrdinalIgnoreCase));
                    if (!match.Equals(default(KeyValuePair<string, string>)))
                    {
                        return match.Value;
                    }
                    return extensionProject.ToLowerInvariant();
                }
                // Return mapped Extensions schema or default "extensions"
                return options.SchemaMapping.ContainsKey("Extensions") ? options.SchemaMapping["Extensions"] : "extensions";
            }

            // Use project name to determine original schema
            if (options.SchemaMapping.TryGetValue(projectSchema.ProjectName, out var projectSchema1))
            {
                return projectSchema1;
            }
            // Try case-insensitive match
            var projectMatch = options.SchemaMapping.FirstOrDefault(kvp =>
                string.Equals(kvp.Key, projectSchema.ProjectName, StringComparison.OrdinalIgnoreCase));
            if (!projectMatch.Equals(default(KeyValuePair<string, string>)))
            {
                return projectMatch.Value;
            }
            return projectSchema.ProjectName.ToLowerInvariant();
        }

        /// <summary>
        /// Determines the database schema name for a resource.
        /// </summary>
        /// <param name="projectSchema">The project schema containing the resource.</param>
        /// <param name="resourceSchema">The resource schema being processed.</param>
        /// <param name="options">DDL generation options containing schema mappings.</param>
        /// <returns>The database schema name to use.</returns>
        private string DetermineSchemaName(ProjectSchema projectSchema, ResourceSchema resourceSchema, DdlGenerationOptions options)
        {
            // Handle descriptor resources - they go to descriptor schema
            if (IsDescriptorResource(resourceSchema))
            {
                // If descriptor schema is different from default schema, use separate schema
                if (options.DescriptorSchema != options.DefaultSchema)
                {
                    return options.DescriptorSchema;
                }
                // Otherwise use prefixed table naming with default schema
                return options.ResolveSchemaName(options.DescriptorSchema);
            }

            // Handle extension resources - they always use separate schemas (no prefixed table names)
            if (resourceSchema.FlatteningMetadata?.Table?.IsExtensionTable == true)
            {
                // Try to extract the extension project name from resource
                var extensionProject = ExtractExtensionProjectName(resourceSchema.ResourceName);
                if (!string.IsNullOrEmpty(extensionProject))
                {
                    // Get the raw schema mapping without dms resolution
                    if (options.SchemaMapping.TryGetValue(extensionProject, out var schema))
                    {
                        return schema;
                    }
                    // Try case-insensitive match
                    var match = options.SchemaMapping.FirstOrDefault(kvp =>
                        string.Equals(kvp.Key, extensionProject, StringComparison.OrdinalIgnoreCase));
                    if (!match.Equals(default(KeyValuePair<string, string>)))
                    {
                        return match.Value;
                    }
                    return extensionProject.ToLowerInvariant();
                }
                // Fall back to extensions schema
                return options.SchemaMapping.ContainsKey("Extensions") ? options.SchemaMapping["Extensions"] : "extensions";
            }

            // Use project name to determine schema - apply prefixed table logic based on options
            if (!options.UsePrefixedTableNames)
            {
                // When not using prefixed table names, use the original schema mapping
                if (options.SchemaMapping.TryGetValue(projectSchema.ProjectName, out var mappedSchema))
                {
                    return mappedSchema;
                }
                // Try case-insensitive match
                var projectMatch = options.SchemaMapping.FirstOrDefault(kvp =>
                    string.Equals(kvp.Key, projectSchema.ProjectName, StringComparison.OrdinalIgnoreCase));
                if (!projectMatch.Equals(default(KeyValuePair<string, string>)))
                {
                    return projectMatch.Value;
                }
                return projectSchema.ProjectName.ToLowerInvariant();
            }

            // Use dms schema for prefixed table names
            return options.ResolveSchemaName(projectSchema.ProjectName);
        }

        /// <summary>
        /// Determines if a resource schema represents a descriptor resource.
        /// </summary>
        private bool IsDescriptorResource(ResourceSchema resourceSchema)
        {
            // Check if resource name ends with "Descriptor" or has descriptor-like patterns
            return resourceSchema.ResourceName.EndsWith("Descriptor", StringComparison.OrdinalIgnoreCase) ||
                   resourceSchema.ResourceName.EndsWith("Type", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Determines if a schema should use separate schemas instead of prefixed table names.
        /// </summary>
        /// <param name="originalSchemaName">The original schema name.</param>
        /// <param name="options">DDL generation options.</param>
        /// <param name="resourceSchema">The resource schema (optional, for extension detection).</param>
        /// <returns>True if should use separate schema; otherwise, false.</returns>
        private bool ShouldUseSeparateSchema(string originalSchemaName, DdlGenerationOptions options, ResourceSchema? resourceSchema = null)
        {
            // Extension resources always use separate schemas (if we have resource schema info)
            if (resourceSchema?.FlatteningMetadata?.Table?.IsExtensionTable == true)
            {
                return true;
            }

            // Descriptor resources using separate schemas
            if (originalSchemaName == options.DescriptorSchema && originalSchemaName != options.DefaultSchema)
            {
                return true;
            }

            // When not using prefixed table names, use separate schemas for all non-default schemas
            if (!options.UsePrefixedTableNames && originalSchemaName != options.DefaultSchema)
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Extracts the extension project name from a resource name.
        /// For example: "TPDMStudentExtension" -> "TPDM"
        /// </summary>
        private string ExtractExtensionProjectName(string resourceName)
        {
            if (resourceName.EndsWith("Extension", StringComparison.OrdinalIgnoreCase))
            {
                var baseName = resourceName.Substring(0, resourceName.Length - "Extension".Length);

                // Look for common patterns like "TPDMStudent" -> "TPDM"
                // Match 2-5 uppercase letters at start followed by a capital letter (indicating next word)
                var match = System.Text.RegularExpressions.Regex.Match(baseName, @"^([A-Z]{2,5})(?=[A-Z][a-z])");
                if (match.Success)
                {
                    return match.Groups[1].Value;
                }

                // Fallback: just match 2-4 uppercase letters at the start
                match = System.Text.RegularExpressions.Regex.Match(baseName, @"^([A-Z]{2,4})");
                if (match.Success)
                {
                    return match.Groups[1].Value;
                }
            }

            return string.Empty;
        }

        /// <summary>
        /// Determines the final table name including any necessary prefixes.
        /// </summary>
        /// <param name="baseName">The base table name from metadata.</param>
        /// <param name="originalSchemaName">The original schema name before prefix resolution.</param>
        /// <param name="resourceSchema">The resource schema context (for extension detection).</param>
        /// <param name="options">DDL generation options.</param>
        /// <returns>The final table name to use in DDL.</returns>
        private string DetermineTableName(string baseName, string originalSchemaName, ResourceSchema? resourceSchema, DdlGenerationOptions options)
        {
            // Special case: if originalSchemaName equals DescriptorSchema and it's different from DefaultSchema,
            // don't use prefixed table names (descriptor resources use separate schemas)
            if (originalSchemaName == options.DescriptorSchema && originalSchemaName != options.DefaultSchema)
            {
                return PgsqlNamingHelper.MakePgsqlIdentifier(baseName);
            }

            // Extension resources that use separate schemas should use original table name
            if (resourceSchema?.FlatteningMetadata?.Table?.IsExtensionTable == true)
            {
                return PgsqlNamingHelper.MakePgsqlIdentifier(baseName);
            }

            if (options.UsePrefixedTableNames && originalSchemaName != options.DefaultSchema)
            {
                // Use original schema name as prefix: edfi_School, tpdm_Student, etc.
                return PgsqlNamingHelper.MakePgsqlIdentifier($"{originalSchemaName}_{baseName}");
            }

            return PgsqlNamingHelper.MakePgsqlIdentifier(baseName);
        }

        /// <summary>
        /// Builds a full table reference including schema and proper table name.
        /// </summary>
        /// <param name="baseName">The base table name from metadata.</param>
        /// <param name="projectSchema">The project schema context.</param>
        /// <param name="resourceSchema">The resource schema context.</param>
        /// <param name="options">DDL generation options.</param>
        /// <returns>The full table reference (schema.tablename).</returns>
        private string BuildTableReference(string baseName, ProjectSchema projectSchema, ResourceSchema? resourceSchema, DdlGenerationOptions options)
        {
            string schemaName;
            if (resourceSchema != null)
            {
                schemaName = DetermineSchemaName(projectSchema, resourceSchema, options);
            }
            else
            {
                schemaName = options.ResolveSchemaName(projectSchema.ProjectName);
            }

            var tableName = DetermineTableName(baseName, schemaName, resourceSchema, options);
            var finalSchemaName = options.ResolveSchemaName(null); // Always use the resolved schema (dms when prefixed)

            return $"{finalSchemaName}.{tableName}";
        }
    }
}
