// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using EdFi.DataManagementService.SchemaGenerator.Abstractions;
using HandlebarsDotNet;

namespace EdFi.DataManagementService.SchemaGenerator.Mssql
{
    /// <summary>
    /// SQL Server DDL generation strategy implementation.
    /// </summary>
    public class MssqlDdlGeneratorStrategy : IDdlGeneratorStrategy
    {
        /// <summary>
        /// Generates SQL Server DDL scripts for the given ApiSchema metadata.
        /// </summary>
        /// <param name="apiSchema">The deserialized ApiSchema metadata object.</param>
        /// <param name="outputDirectory">The directory to write output scripts to.</param>
        /// <param name="includeExtensions">Whether to include extensions in the DDL.</param>
        public void GenerateDdl(ApiSchema apiSchema, string outputDirectory, bool includeExtensions, bool skipUnionViews = false)
        {
            Directory.CreateDirectory(outputDirectory);

            var ddl = GenerateDdlString(apiSchema, includeExtensions, skipUnionViews);

            File.WriteAllText(Path.Combine(outputDirectory, "schema-mssql.sql"), ddl);
        }

        /// <summary>
        /// Generates SQL Server DDL scripts with advanced options.
        /// </summary>
        /// <param name="apiSchema">The deserialized ApiSchema metadata object.</param>
        /// <param name="outputDirectory">The directory to write output scripts to.</param>
        /// <param name="options">DDL generation options including schema mappings and feature flags.</param>
        public void GenerateDdl(ApiSchema apiSchema, string outputDirectory, DdlGenerationOptions options)
        {
            Directory.CreateDirectory(outputDirectory);

            var ddl = GenerateDdlString(apiSchema, options);

            File.WriteAllText(Path.Combine(outputDirectory, "schema-mssql.sql"), ddl);
        }

        /// <summary>
        /// Generates SQL Server DDL scripts as a string with advanced options.
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
        /// Generates SQL Server DDL scripts as a string (useful for testing - legacy method).
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
            var templatePath = Path.Combine(AppContext.BaseDirectory, "Templates", "mssql-table-idempotent.hbs");
            if (!File.Exists(templatePath))
            {
                templatePath = Path.Combine(Directory.GetCurrentDirectory(), "Templates", "mssql-table-idempotent.hbs");
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
                sb.AppendLine($"IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = '{schema}')");
                sb.AppendLine("BEGIN");
                sb.AppendLine($"    CREATE SCHEMA [{schema}];");
                sb.AppendLine($"    PRINT 'Schema {schema} created.';");
                sb.AppendLine("END");
                sb.AppendLine("ELSE");
                sb.AppendLine($"    PRINT 'Schema {schema} already exists, skipped.';");
                sb.AppendLine("GO");
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

                var schemaName = DetermineSchemaName(apiSchema.ProjectSchema, resourceSchema, options);

                // Recursively generate DDL for root table and all child tables
                GenerateTableDdl(resourceSchema.FlatteningMetadata.Table, template, sb, null, null, schemaName, options);
            }

            // Generate union views for abstract resources unless skipped
            if (!options.SkipUnionViews && apiSchema.ProjectSchema != null)
            {
                var unionViewTemplatePath = Path.Combine(AppContext.BaseDirectory, "Templates", "mssql-union-view.hbs");
                if (!File.Exists(unionViewTemplatePath))
                {
                    unionViewTemplatePath = Path.Combine(Directory.GetCurrentDirectory(), "Templates", "mssql-union-view.hbs");
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
                                    // Build SELECT statement for this table
                                    var columns = table.Columns.Select(c => $"[{c.ColumnName}]").ToList();
                                    var selectCols = string.Join(", ", columns);
                                    var discriminator = table.DiscriminatorValue ?? subclassType;
                                    selectStatements.Add($"SELECT {selectCols}, ''{discriminator}'' as Discriminator FROM [dms].[{table.BaseName}]");
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
                            var viewName = table.BaseName;
                            var selectStatements = new List<string>();

                            // Get the natural key columns from the parent table (these should be common across all child tables)
                            var naturalKeyColumns = table.Columns
                                .Where(c => c.IsNaturalKey)
                                .Select(c => c.ColumnName)
                                .ToList();

                            foreach (var childTable in table.ChildTables.Where(ct => !string.IsNullOrEmpty(ct.DiscriminatorValue)))
                            {
                                // Select only the natural key columns (common across all child tables)
                                var selectCols = string.Join(", ", naturalKeyColumns.Select(c => $"[{c}]"));
                                var discriminatorValue = childTable.DiscriminatorValue;
                                var tableName = childTable.BaseName;
                                selectStatements.Add($"SELECT {selectCols}, ''{discriminatorValue}'' AS [Discriminator] FROM [dms].[{tableName}]");
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
            string schemaName,
            DdlGenerationOptions options)
        {
            var tableName = table.BaseName;
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
                        name = c.ColumnName,
                        type = MapColumnType(c),
                        isRequired = c.IsRequired
                    };
                })
                .ToList();

            // Natural key columns for unique constraint
            var naturalKeyColumns = table.Columns
                .Where(c => c.IsNaturalKey && !c.IsParentReference)
                .Select(c => c.ColumnName)
                .ToList();

            // Foreign key constraints
            var fkColumns = new List<object>();

            // 1. FK to parent table (for child tables)
            if (parentTableName != null)
            {
                var parentFkColumn = $"{parentTableName}_Id";
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
                    parentTable = $"[{schemaName}].[{parentTableName}]",
                    parentColumn = "[Id]", // Always reference surrogate key
                    cascade = true
                });
            }

            // 2. FK to cross-resource references
            foreach (var (columnName, referencedResource) in crossResourceReferences)
            {
                fkColumns.Add(new
                {
                    constraintName = $"FK_{table.BaseName}_{referencedResource}",
                    column = columnName,
                    parentTable = $"[{schemaName}].[{referencedResource}]",
                    parentColumn = "[Id]",
                    cascade = false // Use RESTRICT for cross-resource FKs to prevent accidental data loss
                });
            }

            // 3. FK to Document table (all tables)
            fkColumns.Add(new
            {
                constraintName = $"FK_{table.BaseName}_Document",
                column = "Document_Id, Document_PartitionKey",
                parentTable = $"[{schemaName}].[Document]",
                parentColumn = "[(Id, DocumentPartitionKey)]",
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
                    $"{parentTableName}_Id"
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
                    columns = new[] { $"{parentTableName}_Id" }
                });
            }

            // 2. Indexes on cross-resource FKs
            foreach (var (columnName, referencedResource) in crossResourceReferences)
            {
                indexes.Add(new
                {
                    indexName = $"IX_{table.BaseName}_{referencedResource}",
                    tableName,
                    columns = new[] { columnName }
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
                    new { name = "CreateDate", type = "DATETIME2(7)", isRequired = true },
                    new { name = "LastModifiedDate", type = "DATETIME2(7)", isRequired = true },
                    new { name = "ChangeVersion", type = "BIGINT", isRequired = true }
                });
            }

            var data = new
            {
                schemaName,
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
                GenerateTableDdl(childTable, template, sb, table.BaseName, null, schemaName, options);
            }
        }

        /// <summary>
        /// Maps column metadata to SQL Server data types according to the specification.
        /// </summary>
        private string MapColumnType(ColumnMetadata column)
        {
            if (string.IsNullOrEmpty(column.ColumnType))
            {
                return "NVARCHAR(MAX)";
            }

            var baseType = column.ColumnType.ToLower() switch
            {
                // Numeric types
                "int64" or "bigint" => "BIGINT",
                "int32" or "integer" or "int" => "INT",
                "int16" or "short" => "SMALLINT",

                // String types - use NVARCHAR with length or NVARCHAR(MAX) for unlimited
                "string" => column.MaxLength != null
                    ? (int.Parse(column.MaxLength) > 4000 ? "NVARCHAR(MAX)" : $"NVARCHAR({column.MaxLength})")
                    : "NVARCHAR(MAX)",

                // Boolean type
                "boolean" or "bool" => "BIT",

                // Date/time types
                "date" => "DATE",
                "datetime" => "DATETIME2(7)", // High precision as per spec
                "time" => "TIME",

                // Decimal types with precision/scale support
                "decimal" => column.Precision != null && column.Scale != null
                    ? $"DECIMAL({column.Precision}, {column.Scale})"
                    : "DECIMAL",

                // Special Ed-Fi types
                "currency" => "MONEY",
                "percent" => "DECIMAL(5, 4)",
                "year" => "SMALLINT",
                "duration" => "NVARCHAR(30)",

                // Descriptor type - FK to unified descriptor table
                "descriptor" => "BIGINT", // FK to dms.Descriptor table

                // GUID type
                "guid" or "uuid" => "UNIQUEIDENTIFIER",

                // Default fallback
                _ => "NVARCHAR(MAX)",
            };

            return baseType;
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
        /// Determines the appropriate database schema name for a resource.
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
                return options.DescriptorSchema;
            }

            // Handle extension resources - they use extension schema mapping
            if (resourceSchema.FlatteningMetadata?.Table?.IsExtensionTable == true)
            {
                // Try to extract the extension project name from resource
                var extensionProject = ExtractExtensionProjectName(resourceSchema.ResourceName);
                if (!string.IsNullOrEmpty(extensionProject))
                {
                    return options.ResolveSchemaName(extensionProject);
                }
                return options.ResolveSchemaName("Extensions");
            }

            // Use project name to determine schema
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
    }
}
