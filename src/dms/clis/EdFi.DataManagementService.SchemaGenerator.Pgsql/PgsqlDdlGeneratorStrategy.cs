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
        public void GenerateDdl(ApiSchema apiSchema, string outputDirectory, bool includeExtensions)
        {
            Directory.CreateDirectory(outputDirectory);

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

            File.WriteAllText(Path.Combine(outputDirectory, "schema-pgsql.sql"), sb.ToString());
            File.WriteAllText(Path.Combine(outputDirectory, "schema-pgsql-summary.txt"), summary.ToString());
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
                    name = PgsqlNamingHelper.MakePgsqlIdentifier(c.ColumnName),
                    type = MapColumnType(c),
                    isRequired = c.IsRequired,
                    isNaturalKey = c.IsNaturalKey,
                    isParentReference = c.IsParentReference,
                    fromReferencePath = c.FromReferencePath
                })
                .ToList();

            // Primary key columns
            var pkColumns = table.Columns.Where(c => c.IsNaturalKey).Select(c => PgsqlNamingHelper.MakePgsqlIdentifier(c.ColumnName)).ToList();

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
                            column = PgsqlNamingHelper.MakePgsqlIdentifier(col.ColumnName),
                            parentTable = PgsqlNamingHelper.MakePgsqlIdentifier(parentTableName),
                            parentColumn = PgsqlNamingHelper.MakePgsqlIdentifier(parentPk)
                        });
                    }
                }
            }

            var data = new
            {
                tableName = PgsqlNamingHelper.MakePgsqlIdentifier(table.BaseName),
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
                "integer" or "int" => "INTEGER",
                "short" => "SMALLINT",
                "string" => column.MaxLength != null ? $"VARCHAR({column.MaxLength})" : "TEXT",
                "boolean" or "bool" => "BOOLEAN",
                "date" => "DATE",
                "datetime" => "TIMESTAMP",
                "time" => "TIME",
                "decimal" => column.Precision != null && column.Scale != null
                    ? $"DECIMAL({column.Precision}, {column.Scale})"
                    : "DECIMAL",
                "currency" => "MONEY",
                "percent" => "DECIMAL(5, 4)",
                "year" => "SMALLINT",
                "duration" => "VARCHAR(30)",
                "descriptor" => "BIGINT", // FK to descriptor table
                _ => "TEXT",
            };

            return baseType;
        }
    }
}
