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
            var summary = new StringBuilder();

            // Process each resource schema
            foreach (var kvp in apiSchema.ProjectSchema.ResourceSchemas)
            {
                var resourceName = kvp.Key;
                var resourceSchema = kvp.Value;

                if (resourceSchema.FlatteningMetadata?.Table == null)
                {
                    continue; // Skip resources without flattening metadata
                }

                // Skip extensions if not requested
                if (!includeExtensions && resourceSchema.FlatteningMetadata.Table.IsExtensionTable)
                {
                    continue;
                }

                // Recursively generate DDL for root table and all child tables
                GenerateTableDdl(resourceSchema.FlatteningMetadata.Table, template, sb, summary, null, null);
            }

            // Generate union views for abstract resources unless skipped
            if (!skipUnionViews && apiSchema.ProjectSchema != null)
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
                                if (apiSchema.ProjectSchema.ResourceSchemas.TryGetValue(subclassType.ToLowerInvariant() + "s", out var subclassSchema) &&
                                    subclassSchema.FlatteningMetadata?.Table != null)
                                {
                                    var table = subclassSchema.FlatteningMetadata.Table;
                                    // Build SELECT statement for this table
                                    var columns = table.Columns.Select(c => $"[{c.ColumnName}]").ToList();
                                    var selectCols = string.Join(", ", columns);
                                    var discriminator = table.DiscriminatorValue ?? subclassType;
                                    selectStatements.Add($"SELECT {selectCols}, '{discriminator}' as Discriminator FROM [{table.BaseName}]");
                                }
                            }
                            var viewData = new { viewName = unionViewName, selectStatements };
                            sb.AppendLine(unionViewTemplate(viewData));
                        }
                    }
                }

                // Approach 2: Detect polymorphic references from child tables with discriminatorValue
                foreach (var resourceSchema in apiSchema.ProjectSchema.ResourceSchemas.Values)
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
                                selectStatements.Add($"SELECT {selectCols}, '{discriminatorValue}' AS [Discriminator] FROM [{tableName}]");
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

            File.WriteAllText(Path.Combine(outputDirectory, "schema-mssql.sql"), sb.ToString());
            File.WriteAllText(Path.Combine(outputDirectory, "schema-mssql-summary.txt"), summary.ToString());
        }

        private void GenerateTableDdl(
            TableMetadata table,
            HandlebarsTemplate<object, object> template,
            StringBuilder sb,
            StringBuilder summary,
            string? parentTableName,
            List<string>? parentPkColumns)
        {
            var columns = table.Columns
                .Select(c => new
                {
                    name = c.ColumnName,
                    type = MapColumnType(c),
                    isRequired = c.IsRequired,
                    isNaturalKey = c.IsNaturalKey,
                    isParentReference = c.IsParentReference,
                    fromReferencePath = c.FromReferencePath
                })
                .ToList();

            // Primary key columns
            var pkColumns = table.Columns
                .Where(c => c.IsNaturalKey)
                .Select(c => c.ColumnName)
                .ToList();

            // Foreign key columns (parent reference only)
            var fkColumns = new List<object>();
            if (parentTableName != null && parentPkColumns != null && parentPkColumns.Count > 0)
            {
                foreach (var col in table.Columns.Where(c => c.IsParentReference))
                {
                    foreach (var parentPk in parentPkColumns)
                    {
                        fkColumns.Add(new
                        {
                            column = col.ColumnName,
                            parentTable = parentTableName,
                            parentColumn = parentPk
                        });
                    }
                }
            }

            var data = new
            {
                tableName = table.BaseName,
                columns,
                pkColumns,
                fkColumns
            };
            sb.AppendLine(template(data));
            summary.AppendLine($"{table.BaseName}: will be created if not exists");

            // Recursively process child tables, passing this table's PK columns
            foreach (var childTable in table.ChildTables)
            {
                GenerateTableDdl(childTable, template, sb, summary, table.BaseName, pkColumns);
            }
        }

        private string MapColumnType(ColumnMetadata column)
        {
            var baseType = column.ColumnType.ToLower() switch
            {
                "bigint" => "BIGINT",
                "integer" or "int" => "INT",
                "short" => "SMALLINT",
                "string" => column.MaxLength != null
                    ? (int.Parse(column.MaxLength) > 4000 ? "NVARCHAR(MAX)" : $"NVARCHAR({column.MaxLength})")
                    : "NVARCHAR(MAX)",
                "boolean" or "bool" => "BIT",
                "date" => "DATE",
                "datetime" => "DATETIME2(7)",
                "time" => "TIME",
                "decimal" => column.Precision != null && column.Scale != null
                    ? $"DECIMAL({column.Precision}, {column.Scale})"
                    : "DECIMAL",
                "currency" => "MONEY",
                "percent" => "DECIMAL(5, 4)",
                "year" => "SMALLINT",
                "duration" => "NVARCHAR(30)",
                "descriptor" => "BIGINT", // FK to descriptor table
                _ => "NVARCHAR(MAX)",
            };

            return baseType;
        }
    }
}
